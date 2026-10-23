using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Optimiser;

// Mouse wheel and touchpad scrolling glide to the new place instead of jumping, in every scrolling part of the app.
// Each notch adds to where the page is heading, so a quick spin of the wheel keeps going smoothly.
public static class SmoothScroll
{
    static readonly Dictionary<ScrollViewer, double> heading = [];
    static TimeSpan lastFrame;

    public static bool Busy => heading.Count > 0;

    public static void Enable() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));

    // Like a browser: 100 px a notch at Windows' usual 3 lines, or a page when Windows is set to scroll by pages.
    public static double Step(ScrollViewer viewer) =>
        SystemParameters.WheelScrollLines is var lines and > 0 and < 100 ? lines * 100 / 3.0 : viewer.ViewportHeight;

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        // The wheel goes to the innermost scrolling part under the mouse that can still move that way, and once that
        // reaches its end, the one around it. Preview events reach the outermost first, so the others let it pass.
        if (e.Handled || sender != Target(e.OriginalSource as DependencyObject, e.Delta)) return;
        var viewer = (ScrollViewer)sender;
        e.Handled = true;
        heading[viewer] = Math.Clamp(Position(viewer) - e.Delta / 120.0 * Step(viewer), 0, viewer.ScrollableHeight);
        if (heading.Count == 1) CompositionTarget.Rendering += OnFrame;
    }

    static ScrollViewer? Target(DependencyObject? d, int delta)
    {
        for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ScrollViewer viewer && viewer.ScrollableHeight > 0
                && (delta > 0 ? Position(viewer) > 0 : Position(viewer) < viewer.ScrollableHeight))
                return viewer;
        return null;
    }

    static double Position(ScrollViewer viewer) => heading.TryGetValue(viewer, out var to) ? to : viewer.VerticalOffset;

    static void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        var seconds = lastFrame == default ? 1 / 60.0 : (now - lastFrame).TotalSeconds;
        if (seconds <= 0) return; // fired twice for one frame
        lastFrame = now;
        // Covers most of the way in about a tenth of a second, at any refresh rate. Straight there when Windows'
        // animation effects are off.
        var share = SystemParameters.ClientAreaAnimation ? 1 - Math.Exp(-seconds / 0.06) : 1;
        foreach (var (viewer, target) in heading.ToList())
        {
            var to = Math.Min(target, viewer.ScrollableHeight); // the page may have got shorter
            var next = viewer.VerticalOffset + (to - viewer.VerticalOffset) * share;
            if (Math.Abs(to - next) < 0.5 || !viewer.IsVisible)
            {
                next = to;
                heading.Remove(viewer);
            }
            viewer.ScrollToVerticalOffset(next);
        }
        if (heading.Count > 0) return;
        CompositionTarget.Rendering -= OnFrame;
        lastFrame = default;
    }
}
