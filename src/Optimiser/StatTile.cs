using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Optimiser;

// One reading on the Stats page: a label, a big number, a thin meter and, for the busy ones, the last minute as a line.
public sealed class StatTile : Border
{
    const int Seconds = 60;
    readonly TextBlock value = new() { FontSize = 28, FontWeight = FontWeights.SemiBold };
    readonly TextBlock unit = new() { FontSize = 13, Margin = new Thickness(8, 0, 0, 5), VerticalAlignment = VerticalAlignment.Bottom };
    readonly TextBlock warning = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    readonly ColumnDefinition filled = new(), empty = new();
    readonly Polyline? trend;
    readonly Queue<double> history = new();

    public StatTile(string label, bool showTrend)
    {
        Style = (Style)Application.Current.FindResource("Panel");
        Margin = new Thickness(6, 0, 6, 12);
        unit.Foreground = (Brush)Application.Current.FindResource("Muted");
        warning.Foreground = (Brush)Application.Current.FindResource("Warn");

        var meter = new Grid { Height = 4, Margin = new Thickness(0, 12, 0, 0), ColumnDefinitions = { filled, empty } };
        meter.Children.Add(new Border { Background = (Brush)Application.Current.FindResource("Line"), CornerRadius = new CornerRadius(2) });
        Grid.SetColumnSpan(meter.Children[0], 2);
        meter.Children.Add(new Border { Background = (Brush)Application.Current.FindResource("Accent"), CornerRadius = new CornerRadius(2) });

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, Foreground = unit.Foreground, FontSize = 13 });
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { value, unit } });
        panel.Children.Add(meter);
        if (showTrend)
        {
            trend = new Polyline { Stroke = (Brush)Application.Current.FindResource("Accent"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            var canvas = new Canvas { Height = 36, Margin = new Thickness(0, 14, 0, 0), ClipToBounds = true, Children = { trend } };
            canvas.SizeChanged += (_, _) => DrawTrend();
            panel.Children.Add(canvas);
        }
        panel.Children.Add(warning);
        Child = panel;
        Show("–", "", null);
    }

    // fraction fills the meter (0..1); trendValue (0..100) is added to the line once per call.
    public void Show(string number, string unitText, double? fraction, string? warn = null, double? trendValue = null)
    {
        value.Text = number;
        unit.Text = unitText;
        var f = Math.Clamp(fraction ?? 0, 0, 1);
        filled.Width = new GridLength(f, GridUnitType.Star);
        empty.Width = new GridLength(1 - f, GridUnitType.Star);
        warning.Text = warn ?? "";
        warning.Visibility = warn == null ? Visibility.Collapsed : Visibility.Visible;
        if (trend != null && trendValue is { } t)
        {
            history.Enqueue(Math.Clamp(t, 0, 100));
            if (history.Count > Seconds) history.Dequeue();
            DrawTrend();
        }
    }

    void DrawTrend()
    {
        if (trend?.Parent is not Canvas canvas || canvas.ActualWidth == 0) return;
        var step = canvas.ActualWidth / (Seconds - 1);
        var start = Seconds - history.Count; // newest sample sits at the right edge
        trend.Points = new PointCollection(history.Select((v, i) =>
            new Point((start + i) * step, 1 + (canvas.Height - 2) * (1 - v / 100))));
    }
}
