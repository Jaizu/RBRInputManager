using System.Windows.Threading;
using Vortice.DirectInput;

namespace RBRInputManager.Core;

[Flags]
public enum InputKind { Axis = 1, Button = 2, Any = Axis | Button }

/// <summary>A captured input: the device and the DirectInput object (dwType, as RBR stores it).</summary>
public sealed record CapturedInput(Guid Device, string DeviceName, uint ObjectType, InputKind Kind, string Label)
{
    public bool IsKeyboard => Device == DirectInputNames.SysKeyboard;
    public int Instance => DirectInputNames.Instance(ObjectType);
}

/// <summary>
/// Detects a button / key press or an axis movement on any connected device.
/// Devices are polled on a background thread (~125 Hz) so a busy UI never delays detection; results are
/// raised on the UI thread. Axes are compared to a baseline taken at start, so pedals resting at an end
/// stop or a wheel slightly off-centre don't trigger. Devices are opened non-exclusively, so RBR's own
/// state is untouched.
/// </summary>
public sealed class InputCapture : IDisposable
{
    private const int AxisRange = 65535; // DirectInput default range

    // Pedals and handbrakes travel their whole range; a wheel's range is its full rotation (900° or more),
    // so force feedback axes need a much smaller share of it to register.
    private const int PedalThreshold = AxisRange / 5;    // 20 % of travel
    private const int WheelThreshold = AxisRange / 16;   // ~6 %, about 55° on a 900° wheel
    private const uint FfActuator = 0x01000000;

    private sealed class Joy
    {
        public required IDirectInputDevice8 Device { get; init; }
        public required Guid Guid { get; init; }
        public required string Name { get; init; }
        /// <summary>Object behind each JoystickState axis field (X Y Z Rx Ry Rz Slider0 Slider1), or null.</summary>
        public required (uint Type, string Name)?[] Axes { get; init; }
        /// <summary>Object behind each JoystickState.Buttons index.</summary>
        public required List<(uint Type, string Name)> Buttons { get; init; }
        public int[] BaseAxes = Array.Empty<int>();
        public bool[] BaseButtons = Array.Empty<bool>();
        // Monitor mode: an axis fires once when it leaves its rest position and re-arms when it returns.
        public bool[] Armed = Array.Empty<bool>();
        // Last state read by the polling thread, for the live input display.
        public volatile int[]? LastAxes;
        public volatile bool[]? LastButtons;
    }

    // JoystickState axis fields in order. Object offsets in the data format Vortice builds are not the
    // DIJOYSTATE2 ones (e.g. a SIMAGIC "Throttle" slider sits where Y would be), so objects are matched to
    // fields by their axis GUID; sliders and buttons by order.
    private static readonly Guid[] AxisGuids =
        { ObjectGuid.XAxis, ObjectGuid.YAxis, ObjectGuid.ZAxis, ObjectGuid.RxAxis, ObjectGuid.RyAxis, ObjectGuid.RzAxis };
    private const int AxisFields = 8;

    private readonly IDirectInput8 _di = DInput.DirectInput8Create();
    private readonly List<Joy> _joys = new();
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private IDirectInputDevice8? _keyboard;
    private HashSet<Key> _baseKeys = new();
    private volatile HashSet<Key> _lastKeys = new();
    private InputKind _accept;
    private bool _monitor;
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>Raised on the UI thread.</summary>
    public event Action<CapturedInput>? Captured;

    /// <summary>Monitor mode only: keep polling but don't report anything.</summary>
    public bool Paused { get; set; }

    /// <summary>Opens every attached controller and the keyboard. <paramref name="hwnd"/> is the app window.</summary>
    public InputCapture(IntPtr hwnd)
    {
        foreach (var inst in _di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
        {
            try
            {
                var dev = _di.CreateDevice(inst.InstanceGuid);
                dev.SetCooperativeLevel(hwnd, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                dev.SetDataFormat<RawJoystickState>();
                var axes = new (uint, string)?[AxisFields];
                var axisObjects = dev.GetObjects(DeviceObjectTypeFlags.Axis).OrderBy(o => o.Offset).ToList();
                foreach (var o in axisObjects)
                {
                    int field = Array.IndexOf(AxisGuids, o.ObjectType);
                    if (field >= 0 && axes[field] is null) axes[field] = (RawType(o), o.Name.Trim());
                }
                int slider = 6;
                foreach (var o in axisObjects.Where(o => o.ObjectType == ObjectGuid.Slider))
                    if (slider < AxisFields) axes[slider++] = (RawType(o), o.Name.Trim());
                var buttons = dev.GetObjects(DeviceObjectTypeFlags.Button).OrderBy(o => o.ObjectId.InstanceNumber)
                    .Select(o => (RawType(o), o.Name.Trim())).ToList();
                dev.Acquire();
                _joys.Add(new Joy { Device = dev, Guid = inst.InstanceGuid, Name = inst.InstanceName.Trim(), Axes = axes, Buttons = buttons });
            }
            catch { /* a device that can't be opened just isn't offered */ }
        }

        try
        {
            _keyboard = _di.CreateDevice(PredefinedDevice.SysKeyboard);
            _keyboard.SetCooperativeLevel(hwnd, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
            _keyboard.SetDataFormat<RawKeyboardState>();
            _keyboard.Acquire();
        }
        catch { _keyboard = null; }
    }

    /// <summary>DIDFT dwType of an object: type/attribute flags plus the instance number in bits 8–23.</summary>
    public static uint RawType(DeviceObjectInstance o) =>
        ((uint)o.ObjectId.Flags & 0xFF0000FF) | ((uint)o.ObjectId.InstanceNumber & 0xFFFF) << 8;

    private static int Threshold(uint type) => (type & FfActuator) != 0 ? WheelThreshold : PedalThreshold;

    /// <summary>Capture mode: fires once for the first new input, then stops.</summary>
    public void Start(InputKind accept)
    {
        _monitor = false;
        Begin(accept);
    }

    /// <summary>
    /// Monitor mode: keeps running and fires for every new press (button/key going down) or axis leaving its
    /// rest position. <see cref="Paused"/> stops reporting without closing the devices.
    /// </summary>
    public void StartMonitor()
    {
        _monitor = true;
        Begin(InputKind.Any);
    }

    private void Begin(InputKind accept)
    {
        Stop();
        _accept = accept;
        foreach (var j in _joys)
        {
            var s = Read(j);
            j.BaseAxes = s?.Axes ?? new int[AxisFields];
            j.BaseButtons = s?.Buttons ?? Array.Empty<bool>();
            j.Armed = Enumerable.Repeat(true, AxisFields).ToArray();
        }
        _baseKeys = ReadKeys();

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "InputCapture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join(200);
        _thread = null;
    }

    private void Loop()
    {
        while (_running)
        {
            var hit = _monitor ? PollMonitor() : PollCapture();
            if (hit is not null)
            {
                if (!_monitor) _running = false; // capture mode stops at the first input
                if (!(_monitor && Paused)) _ui.BeginInvoke(() => Captured?.Invoke(hit));
                if (!_running) break;
            }
            Thread.Sleep(8);
        }
    }

    private CapturedInput? PollCapture()
    {
        foreach (var j in _joys)
        {
            var s = Read(j);
            if (s is null) continue;

            if (_accept.HasFlag(InputKind.Button))
                for (int b = 0; b < Math.Min(s.Value.Buttons.Length, j.Buttons.Count); b++)
                    if (s.Value.Buttons[b] && !(b < j.BaseButtons.Length && j.BaseButtons[b]))
                        return new CapturedInput(j.Guid, j.Name, j.Buttons[b].Type, InputKind.Button,
                            $"Button {DirectInputNames.Instance(j.Buttons[b].Type)}");

            if (_accept.HasFlag(InputKind.Axis))
                for (int a = 0; a < AxisFields; a++)
                    if (j.Axes[a] is { } obj && Math.Abs(s.Value.Axes[a] - j.BaseAxes[a]) > Threshold(obj.Type))
                        return new CapturedInput(j.Guid, j.Name, obj.Type, InputKind.Axis, obj.Name);
        }

        if (_accept.HasFlag(InputKind.Button) && _keyboard is not null)
            foreach (var k in ReadKeys())
                if (!_baseKeys.Contains(k) && k != Key.Escape) // Esc cancels the capture in the UI
                    return KeyInput(k);
        return null;
    }

    private CapturedInput? PollMonitor()
    {
        CapturedInput? hit = null;
        foreach (var j in _joys)
        {
            var s = Read(j);
            if (s is null) continue;

            for (int b = 0; b < Math.Min(s.Value.Buttons.Length, j.Buttons.Count); b++)
                if (s.Value.Buttons[b] && !(b < j.BaseButtons.Length && j.BaseButtons[b]))
                    hit ??= new CapturedInput(j.Guid, j.Name, j.Buttons[b].Type, InputKind.Button,
                        $"Button {DirectInputNames.Instance(j.Buttons[b].Type)}");
            j.BaseButtons = s.Value.Buttons; // edges only: holding a button fires once

            for (int a = 0; a < AxisFields; a++)
            {
                if (j.Axes[a] is not { } obj) continue;
                int delta = Math.Abs(s.Value.Axes[a] - j.BaseAxes[a]), threshold = Threshold(obj.Type);
                if (j.Armed[a] && delta > threshold)
                {
                    j.Armed[a] = false;
                    hit ??= new CapturedInput(j.Guid, j.Name, obj.Type, InputKind.Axis, obj.Name);
                }
                else if (!j.Armed[a] && delta < threshold / 3)
                    j.Armed[a] = true;
            }
        }

        var keys = ReadKeys();
        foreach (var k in keys)
            if (!_baseKeys.Contains(k))
                hit ??= KeyInput(k);
        _baseKeys = keys;
        return hit;
    }

    // ---------------------------------------------------------------- live values (monitor thread's last poll)

    /// <summary>Last polled position of an axis as 0..1, or null when the device or axis isn't open.</summary>
    public double? AxisValue(Guid device, uint type)
    {
        if (_joys.Find(j => j.Guid == device) is not { LastAxes: { } axes } joy) return null;
        for (int a = 0; a < AxisFields; a++)
            if (joy.Axes[a] is { } obj && SameObject(obj.Type, type))
                return Math.Clamp(axes[a] / (double)AxisRange, 0, 1);
        return null;
    }

    /// <summary>Whether a button or key was down at the last poll.</summary>
    public bool IsDown(Guid device, uint type)
    {
        if (device == DirectInputNames.SysKeyboard)
            return _lastKeys.Contains((Key)DirectInputNames.Instance(type));
        if (_joys.Find(j => j.Guid == device) is not { LastButtons: { } buttons } joy) return false;
        int b = joy.Buttons.FindIndex(o => SameObject(o.Type, type));
        return b >= 0 && b < buttons.Length && buttons[b];
    }

    // Same kind (axis/button) and instance; attribute flags such as FFACTUATOR may differ from what RBR stored.
    private static bool SameObject(uint a, uint b) =>
        DirectInputNames.Instance(a) == DirectInputNames.Instance(b) && ((a & 0x03) != 0) == ((b & 0x03) != 0) &&
        ((a & 0x0C) != 0) == ((b & 0x0C) != 0);

    private static CapturedInput KeyInput(Key k)
    {
        int dik = (int)k;
        return new CapturedInput(DirectInputNames.SysKeyboard, "Keyboard", 0x04u | (uint)dik << 8, InputKind.Button,
            $"Key {DirectInputNames.KeyName(dik)}");
    }

    private static (int[] Axes, bool[] Buttons)? Read(Joy j)
    {
        try
        {
            j.Device.Poll();
            var st = j.Device.GetCurrentJoystickState();
            var axes = new[] { st.X, st.Y, st.Z, st.RotationX, st.RotationY, st.RotationZ, st.Sliders[0], st.Sliders[1] };
            var buttons = st.Buttons.ToArray();
            j.LastAxes = axes;
            j.LastButtons = buttons;
            return (axes, buttons);
        }
        catch
        {
            try { j.Device.Acquire(); } catch { /* unplugged */ }
            return null;
        }
    }

    private HashSet<Key> ReadKeys()
    {
        if (_keyboard is null) return new();
        try
        {
            _keyboard.Poll();
            return _lastKeys = _keyboard.GetCurrentKeyboardState().PressedKeys.ToHashSet();
        }
        catch
        {
            try { _keyboard.Acquire(); } catch { }
            return new();
        }
    }

    public void Dispose()
    {
        Stop();
        foreach (var j in _joys)
        {
            try { j.Device.Unacquire(); } catch { }
            j.Device.Dispose();
        }
        if (_keyboard is not null)
        {
            try { _keyboard.Unacquire(); } catch { }
            _keyboard.Dispose();
        }
        _di.Dispose();
    }
}
