using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace RBRInputManager;

public enum TooltipSide { Right, Bottom, Top, Left }

/// <summary>
/// Places tooltips beside, below or above their target, centred on it, with the arrow pointing at it.
/// The first side (the element's <c>Prefer</c>, then right, below, above, left) where the bubble fits inside the
/// window wins; if none does, WPF takes the first that fits on screen. The template flips the arrow to match.
/// </summary>
public static class TooltipPlacement
{
    // Distance from the popup's edge to the arrow tip: shadow margin + arrow box inset (ToolTip template, App.xaml).
    private const double TipInset = 7;
    private const double Gap = 3;

    // The element whose tooltip is opening; the placement callback gets only sizes, not the element.
    private static FrameworkElement? _owner;

    public static readonly CustomPopupPlacementCallback Callback = (popup, target, _) =>
    {
        double dpi = _owner is null ? 1 : VisualTreeHelper.GetDpi(_owner).DpiScaleX;
        double inset = (TipInset - Gap) * dpi;
        var room = Room(_owner);

        // Below or above: centred on the target, but slid back inside the window (OnOpened moves the arrow to match).
        double x = (target.Width - popup.Width) / 2;
        if (room is { } rx)
        {
            double shadow = 6 * dpi;
            x = Math.Min(x, target.Width + rx.Right - popup.Width + shadow);
            x = Math.Max(x, -rx.Left - shadow);
        }
        CustomPopupPlacement At(TooltipSide side) => side switch
        {
            TooltipSide.Right => new(new Point(target.Width - inset, (target.Height - popup.Height) / 2), PopupPrimaryAxis.Horizontal),
            TooltipSide.Left => new(new Point(-popup.Width + inset, (target.Height - popup.Height) / 2), PopupPrimaryAxis.Horizontal),
            TooltipSide.Bottom => new(new Point(x, target.Height - inset), PopupPrimaryAxis.Vertical),
            _ => new(new Point(x, -popup.Height + inset), PopupPrimaryAxis.Vertical),
        };

        var sides = Order(_owner);
        if (room is { } r)
        {
            // Space the bubble needs past the target (the shadow on its far side may spill), in device pixels like the rest.
            double w = popup.Width - inset - 6 * dpi, h = popup.Height - inset - 6 * dpi;
            var fits = sides.Where(s => s switch
            {
                TooltipSide.Right => r.Right >= w,
                TooltipSide.Left => r.Left >= w,
                TooltipSide.Bottom => r.Bottom >= h,
                _ => r.Top >= h,
            }).ToList();
            sides = fits.Concat(sides.Except(fits)).ToList();
        }
        return sides.Select(At).ToArray();
    };

    /// <summary>Side to try first for this element's tooltip (default: right).</summary>
    public static readonly DependencyProperty PreferProperty = DependencyProperty.RegisterAttached(
        "Prefer", typeof(TooltipSide), typeof(TooltipPlacement), new PropertyMetadata(TooltipSide.Right));

    public static TooltipSide GetPrefer(DependencyObject o) => (TooltipSide)o.GetValue(PreferProperty);
    public static void SetPrefer(DependencyObject o, TooltipSide value) => o.SetValue(PreferProperty, value);

    /// <summary>Where the tooltip ended up relative to its target, so the template points the arrow back.</summary>
    public static readonly DependencyProperty SideProperty = DependencyProperty.RegisterAttached(
        "Side", typeof(TooltipSide), typeof(TooltipPlacement), new PropertyMetadata(TooltipSide.Right));

    public static TooltipSide GetSide(DependencyObject o) => (TooltipSide)o.GetValue(SideProperty);
    public static void SetSide(DependencyObject o, TooltipSide value) => o.SetValue(SideProperty, value);

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent,
            new ToolTipEventHandler((s, _) => _owner = s as FrameworkElement), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnOpened));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.ClosedEvent,
            new RoutedEventHandler((s, _) => { if (s == _open) _open = null; }));
        // Like native tooltips: scrolling hides the open one, since the content under it moves away. The next mouse
        // move over an element opens that element's tooltip as usual.
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler((_, _) => { if (_open is { } tip) tip.IsOpen = false; }), handledEventsToo: true);
    }

    // The tooltip on screen, if any (WPF shows one at a time).
    private static ToolTip? _open;

    private static List<TooltipSide> Order(FrameworkElement? owner)
    {
        var all = new List<TooltipSide> { TooltipSide.Right, TooltipSide.Bottom, TooltipSide.Top, TooltipSide.Left };
        if (owner is null) return all;
        var prefer = GetPrefer(owner);
        all.Remove(prefer);
        all.Insert(0, prefer);
        return all;
    }

    /// <summary>Free space around the placement target inside its window, in device pixels.</summary>
    private static Thickness? Room(FrameworkElement? owner)
    {
        if (owner is null || (ToolTipService.GetPlacementTarget(owner) as FrameworkElement ?? owner) is not { } target
            || Window.GetWindow(target)?.Content is not FrameworkElement root
            || PresentationSource.FromVisual(target) is null || !root.IsAncestorOf(target)) return null;
        var dpi = VisualTreeHelper.GetDpi(target);
        var rect = target.TransformToAncestor(root).TransformBounds(new Rect(target.RenderSize));
        return new Thickness(rect.Left * dpi.DpiScaleX, rect.Top * dpi.DpiScaleY,
            (root.ActualWidth - rect.Right) * dpi.DpiScaleX, (root.ActualHeight - rect.Bottom) * dpi.DpiScaleY);
    }

    private static void OnOpened(object sender, RoutedEventArgs e)
    {
        _open = sender as ToolTip;
        if (sender is not ToolTip tip || tip.PlacementTarget is not UIElement target
            || PresentationSource.FromVisual(tip) is null || PresentationSource.FromVisual(target) is null) return;
        var t = new Rect(target.PointToScreen(new Point(0, 0)), target.PointToScreen(new Point(target.RenderSize.Width, target.RenderSize.Height)));
        // Trimmed to where the arrow tips sit, which is Gap away from the target on the side it was placed.
        var p = new Rect(tip.PointToScreen(new Point(TipInset, TipInset)),
            tip.PointToScreen(new Point(tip.ActualWidth - TipInset, tip.ActualHeight - TipInset)));
        var side = p.Left >= t.Right - 1 ? TooltipSide.Right
            : p.Right <= t.Left + 1 ? TooltipSide.Left
            : p.Top >= t.Bottom - 1 ? TooltipSide.Bottom
            : TooltipSide.Top;
        SetSide(tip, side);

        // When the bubble had to slide to stay inside the window or the screen, move the arrow off centre so it still
        // points at the target's centre. Screen pixels → the tooltip's units; keep it clear of the rounded corners.
        var dpi = VisualTreeHelper.GetDpi(tip);
        bool vertical = side is TooltipSide.Bottom or TooltipSide.Top;
        double shift = vertical
            ? (t.X + t.Width / 2 - (p.X + p.Width / 2)) / dpi.DpiScaleX
            : (t.Y + t.Height / 2 - (p.Y + p.Height / 2)) / dpi.DpiScaleY;
        double max = Math.Max(0, (vertical ? p.Width / dpi.DpiScaleX : p.Height / dpi.DpiScaleY) / 2 - 15);
        shift = Math.Clamp(shift, -max, max);
        foreach (var name in new[] { "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown" })
            if (tip.Template?.FindName(name, tip) is FrameworkElement arrow)
            {
                // The arrows are centred: a margin of 2·shift on one side moves the centre by shift.
                double a = Math.Max(0, 2 * shift), b = Math.Max(0, -2 * shift);
                arrow.Margin = name is "ArrowUp" or "ArrowDown" ? new Thickness(a, 0, b, 0) : new Thickness(0, a, 0, b);
            }
    }
}
