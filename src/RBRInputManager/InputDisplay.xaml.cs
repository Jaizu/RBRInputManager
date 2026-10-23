using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RBRInputManager;

/// <summary>Input levels for <see cref="InputDisplay"/>: steering -1..1, pedals 0..1; null when nothing is bound.</summary>
public readonly record struct InputLevels(double? Steering, double? Clutch, double? Brake, double? Throttle, double? Handbrake);

/// <summary>Live steering and pedal display styled after RBR's in-game input HUD.</summary>
public partial class InputDisplay : UserControl
{
    // Wheel rotation shown at full steering lock (half of a 900° wheel).
    private const double LockDegrees = 450;
    private const double BarHeight = 24;
    private const double Unbound = 0.35;

    private readonly (Rectangle Fill, TextBlock Text, FrameworkElement[] Parts)[] _pedals;

    public InputDisplay()
    {
        InitializeComponent();
        _pedals = new[]
        {
            AddPedal(0, "Clutch", Color.FromRgb(0x3E, 0x8E, 0xD8), stem: true),
            AddPedal(1, "Brake", Color.FromRgb(0xD8, 0x3A, 0x2A), stem: true),
            AddPedal(2, "Throttle", Color.FromRgb(0x5C, 0xA8, 0x1C), stem: true),
            AddPedal(3, "Handbrake", Color.FromRgb(0xE8, 0xA0, 0x1E), stem: false),
        };
        Show(default);
    }

    private (Rectangle, TextBlock, FrameworkElement[]) AddPedal(int column, string name, Color color, bool stem)
    {
        var fill = new Rectangle { Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Bottom, Height = 0 };
        var bar = new Border
        {
            Width = 22, Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)), BorderThickness = new Thickness(1),
            Child = fill, ToolTip = name,
        };
        var text = new TextBlock
        {
            FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
        };
        var parts = new List<FrameworkElement> { bar, text };
        if (stem) parts.Add(new Rectangle { Width = 5, Fill = Brushes.White });
        for (int row = 0; row < parts.Count; row++)
        {
            Grid.SetRow(parts[row], row);
            Grid.SetColumn(parts[row], column);
            Pedals.Children.Add(parts[row]);
        }
        return (fill, text, parts.ToArray());
    }

    public void Show(InputLevels levels)
    {
        double steer = Math.Clamp(levels.Steering ?? 0, -1, 1);
        WheelRotate.Angle = steer * LockDegrees;
        double half = SteerTrack.Width / 2, extent = Math.Abs(steer) * half;
        SteerFill.Width = extent;
        Canvas.SetLeft(SteerFill, steer < 0 ? half - extent : half);
        WheelBox.Opacity = levels.Steering is null ? Unbound : 1;

        double?[] values = { levels.Clutch, levels.Brake, levels.Throttle, levels.Handbrake };
        for (int i = 0; i < _pedals.Length; i++)
        {
            var (fill, text, parts) = _pedals[i];
            double v = Math.Clamp(values[i] ?? 0, 0, 1);
            fill.Height = v * BarHeight;
            text.Text = $"{Math.Round(v * 100)}%";
            foreach (var p in parts) p.Opacity = values[i] is null ? Unbound : 1;
        }
    }
}
