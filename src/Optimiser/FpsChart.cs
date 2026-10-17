using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Optimiser;

// FPS over one session: an orange line, labelled dashed lines at the average and the 1% low, grid lines every 30 or
// 60 FPS, and on hover a crosshair with the time and FPS at that point.
public sealed class FpsChart : Canvas
{
    const double Left = 34, Bottom = 22; // room for the FPS and time labels
    readonly Polyline line = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    readonly Line average = new() { StrokeThickness = 1, StrokeDashArray = [4, 4] }, low = new() { StrokeThickness = 1, StrokeDashArray = [2, 3] },
        cross = new() { StrokeThickness = 1 };
    readonly Ellipse dot = new() { Width = 9, Height = 9 };
    readonly TextBlock tipText = new() { FontSize = 12.5 };
    readonly Border tip = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 5, 9, 5) };
    readonly List<UIElement> grid = [];
    FpsSession? session;
    double top; // the FPS at the top edge
    double? hovering; // where the pointer is, 0..1, so a redraw keeps the crosshair under it

    public FpsChart()
    {
        Height = 190;
        Background = Brushes.Transparent; // so the whole area takes the mouse
        line.Stroke = dot.Fill = Paint("Accent");
        average.Stroke = cross.Stroke = Paint("Muted");
        low.Stroke = Paint("Warn");
        tip.Background = Paint("Bg");
        tip.BorderBrush = Paint("Line");
        tip.Child = tipText;
        foreach (var e in new UIElement[] { average, low, line, cross, dot, tip }) Children.Add(e);
        Hide();
        SizeChanged += (_, _) => Draw();
        MouseMove += (_, e) => Hover((e.GetPosition(this).X - Left) / (ActualWidth - Left));
        MouseLeave += (_, _) => Hide();
    }

    public void Show(FpsSession? s)
    {
        session = s;
        Draw();
    }

    void Draw()
    {
        foreach (var e in grid) Children.Remove(e);
        grid.Clear();
        average.Visibility = low.Visibility = Visibility.Collapsed;
        if (session is not { Chart.Length: > 1 } s || ActualWidth <= Left) { line.Points = []; return; }

        var max = Math.Max(s.Chart.Max(), 60);
        var gap = max <= 120 ? 30 : max <= 240 ? 60 : 120;
        top = Math.Ceiling(max * 1.1 / gap) * gap;
        for (var fps = 0.0; fps <= top; fps += gap)
        {
            Add(new Line { X1 = Left, X2 = ActualWidth, Y1 = Y(fps), Y2 = Y(fps), Stroke = Paint("Line"), StrokeThickness = 1 });
            Add(Label($"{fps:0}", 0, Y(fps) - 8));
        }
        Add(Label("0:00", Left, Height - Bottom + 4));
        var end = Label(Time(s.Seconds), 0, Height - Bottom + 4);
        end.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SetLeft(end, ActualWidth - end.DesiredSize.Width);
        Add(end);

        line.Points = new PointCollection(s.Chart.Select((v, i) => new Point(X(i), Y(v))));
        // The average's label sits above its line and the low's below, so they can't overlap however close they are.
        Mark(average, "Average", s.Average, above: true);
        Mark(low, "1% low", s.Low1, above: false);
        if (hovering is { } h) Hover(h);
    }

    // at runs 0..1 across the chart. Public so the CI snapshot can show it.
    public void Hover(double at)
    {
        if (session is not { Chart.Length: > 1 } s) return;
        hovering = at;
        var i = (int)Math.Round(Math.Clamp(at, 0, 1) * (s.Chart.Length - 1));
        var (x, y) = (X(i), Y(s.Chart[i]));
        (cross.X1, cross.X2, cross.Y1, cross.Y2) = (x, x, 0, Height - Bottom);
        SetLeft(dot, x - dot.Width / 2);
        SetTop(dot, y - dot.Height / 2);
        tipText.Text = $"{Time(i * s.Step)}, {s.Chart[i]} FPS";
        tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SetLeft(tip, x + 12 + tip.DesiredSize.Width < ActualWidth ? x + 12 : x - 12 - tip.DesiredSize.Width); // stays inside
        SetTop(tip, Math.Clamp(y - tip.DesiredSize.Height - 8, 0, Height - Bottom - tip.DesiredSize.Height));
        cross.Visibility = dot.Visibility = tip.Visibility = Visibility.Visible;
    }

    void Hide()
    {
        hovering = null;
        cross.Visibility = dot.Visibility = tip.Visibility = Visibility.Collapsed;
    }

    void Mark(Line mark, string name, double? fps, bool above)
    {
        if (fps is not { } f) return;
        (mark.X1, mark.X2, mark.Y1, mark.Y2) = (Left, ActualWidth, Y(f), Y(f));
        mark.Visibility = Visibility.Visible;
        var label = Label($"{name} {FrameStats.Show(f)}", 0, 0);
        label.Foreground = mark.Stroke;
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SetLeft(label, ActualWidth - label.DesiredSize.Width);
        SetTop(label, above ? Y(f) - label.DesiredSize.Height - 1 : Y(f) + 1);
        grid.Add(label);
        Children.Add(label); // over the line, so it stays readable
    }

    double X(int i) => Left + (ActualWidth - Left) * i / (session!.Chart.Length - 1);
    double Y(double fps) => (Height - Bottom) * (1 - Math.Min(fps, top) / top);

    void Add(UIElement e)
    {
        grid.Add(e);
        Children.Insert(0, e); // under the line
    }

    TextBlock Label(string text, double x, double y)
    {
        var label = new TextBlock { Text = text, FontSize = 11.5, Foreground = Paint("Muted") };
        SetLeft(label, x);
        SetTop(label, y);
        return label;
    }

    public static string Time(int seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    static Brush Paint(string name) => (Brush)Application.Current.FindResource(name);
}
