using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Optimiser;

// A shot's setup on half a pitch, seen from behind Peter's (blue) car: where he starts, the ball and its path, and
// Necto. Field units as RLBot's: blue defends -y, so the half with the action is drawn, its goal at the top when he
// attacks. The ball's path is lifted by its height over a faint ground shadow, so lobs and aerials read as arcs.
public sealed class ShotPicture : FrameworkElement
{
    const double HalfLength = 5120, Width2 = 4096;
    static readonly Brush Blue = Frozen(new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)));
    ShotInfo? shot;
    BitmapImage? image;

    public ShotPicture() => Height = 150; // about half a pitch's shape at a card's width

    public ShotInfo? Shot
    {
        get => shot;
        set { (shot, image) = (value, Picture(value?.Image)); InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var line = new Pen(Paint("Line"), 1);
        var frame = new Rect(0, 0, w, h);
        dc.PushClip(new RectangleGeometry(frame, 6, 6));
        dc.DrawRectangle(Paint("Bg"), null, frame);
        if (image != null)
        {
            dc.DrawImage(image, frame);
            dc.Pop();
            dc.DrawRoundedRectangle(null, line, frame, 6, 6);
            return;
        }

        var s = shot;
        var points = s == null ? [] : s.Path.Concat(s.Ball == null ? [] : [s.Ball]).ToList();
        var attacking = points.Count == 0 || points.Average(p => p[1]) >= 0;
        var top = attacking ? HalfLength : 0;
        Point At(double x, double y, double z = 0) => new((Width2 - x) / (2 * Width2) * w, (top - y) / HalfLength * h - z / HalfLength * h * 0.55);

        // The half: centre circle at the halfway line, the box and the goal (orange for theirs, muted for his own).
        var halfway = attacking ? h : 0;
        dc.DrawLine(line, new Point(0, halfway), new Point(w, halfway));
        dc.DrawEllipse(null, line, new Point(w / 2, halfway), w * 0.11, w * 0.11);
        var goalEdge = attacking ? 0 : h;
        var box = At(1450, attacking ? HalfLength - 1100 : -HalfLength + 1100);
        var boxOther = At(-1450, attacking ? HalfLength : -HalfLength);
        dc.DrawRectangle(null, line, new Rect(box, boxOther));
        dc.DrawLine(new Pen(attacking ? Paint("Accent") : Paint("Muted"), 4), At(893, attacking ? HalfLength : -HalfLength), At(-893, attacking ? HalfLength : -HalfLength));

        if (s != null)
        {
            // The ball's ground shadow, then its path rising and fading in with time, and where it ends.
            if (s.Path.Count > 1)
            {
                var shadow = new Pen(Paint("Line"), 1.5) { DashStyle = new DashStyle([2, 3], 0) };
                for (var i = 1; i < s.Path.Count; i++)
                    dc.DrawLine(shadow, At(s.Path[i - 1][0], s.Path[i - 1][1]), At(s.Path[i][0], s.Path[i][1]));
                for (var i = 1; i < s.Path.Count; i++)
                {
                    var pen = new Pen(Paint("Text"), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.PushOpacity(0.35 + 0.65 * i / (s.Path.Count - 1));
                    dc.DrawLine(pen, At(s.Path[i - 1][0], s.Path[i - 1][1], s.Path[i - 1][2]), At(s.Path[i][0], s.Path[i][1], s.Path[i][2]));
                    dc.Pop();
                }
                var end = s.Path[^1];
                dc.DrawEllipse(null, new Pen(Paint("Text"), 1.2), At(end[0], end[1], end[2]), 4, 4);
            }
            foreach (var o in s.Opponents) Car(dc, At(o[0], o[1]), o.Length > 2 ? o[2] : -Math.PI / 2, Paint("Accent"));
            if (s.Car != null) Car(dc, At(s.Car[0], s.Car[1]), s.Car.Length > 2 ? s.Car[2] : Math.PI / 2, Blue);
            if (s.Ball != null)
            {
                dc.DrawEllipse(Frozen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0))), null, At(s.Ball[0], s.Ball[1]), 5, 2.5);
                dc.DrawEllipse(Paint("Text"), new Pen(Paint("Bg"), 1.5), At(s.Ball[0], s.Ball[1], s.Ball[2]), 5.5, 5.5);
            }
        }
        dc.Pop();
        dc.DrawRoundedRectangle(null, line, frame, 6, 6);
    }

    // A small car pointing along yaw (radians, 0 = +x): on screen x and y are both flipped, so the angle turns by 180°.
    static void Car(DrawingContext dc, Point at, double yaw, Brush colour)
    {
        var angle = Math.Atan2(-Math.Sin(yaw), -Math.Cos(yaw)) * 180 / Math.PI;
        dc.PushTransform(new RotateTransform(angle, at.X, at.Y));
        dc.DrawRoundedRectangle(colour, null, new Rect(at.X - 8, at.Y - 4.5, 16, 9), 2.5, 2.5);
        dc.DrawRectangle(Paint("Bg"), null, new Rect(at.X + 3, at.Y - 3, 2.5, 6)); // the windscreen, at the front
        dc.Pop();
    }

    // Read into memory, so the file isn't held open while the trainer redraws it. Null if it's missing or broken.
    static BitmapImage? Picture(string? file)
    {
        if (file == null || !File.Exists(file)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // the trainer redraws them each start
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException) { return null; }
    }

    static Brush Paint(string name) => (Brush)Application.Current.FindResource(name);

    static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}
