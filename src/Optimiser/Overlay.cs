using System.Management;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;

namespace Optimiser;

// Stacked bars in the top-right corner of the game, in the style of Rocket League's own performance stats: each bar
// has a reading and a smaller second line under it, values coloured green, yellow or red, and orange where heat is
// slowing the CPU or GPU down. It's Optimiser's own window,
// kept on top and click-through: nothing is loaded into the game and its process isn't opened, so anti-cheat has nothing
// to refuse. A game in exclusive fullscreen can draw over it; borderless fullscreen always shows it.
public sealed class Overlay : Window
{
    public const string Setting = "FpsOverlay";
    public enum Tone { Plain, Good, Fair, Bad, Hot }
    public record Bar(string Label, string Value, Tone Tone, string SubLabel = "", string SubValue = "", Tone SubTone = Tone.Plain);
    // The highest GPU temperature since the game started, for its bar's second line.
    public record Peaks(double? GpuMaxTemp);

    static Overlay? shown;
    static Stats? stats;
    static (DateTime Game, double TempMax) peaks = (default, double.MinValue);
    static bool noCpuTemp; // Windows had no thermal zone to read, so stop asking

    static readonly Brush Label = Frozen(0xE8, 0xE8, 0xE8), Good = Frozen(0x8B, 0xE0, 0x4E), Fair = Frozen(0xE8, 0xD8, 0x4A),
        Bad = Frozen(0xFF, 0x5A, 0x4F), Hot = Frozen(0xFF, 0x9A, 0x2E);
    static readonly Brush Shade = Freeze(new LinearGradientBrush(
        [new(Color.FromArgb(0x00, 0, 0, 0), 0), new(Color.FromArgb(0x8C, 0, 0, 0), 0.35), new(Color.FromArgb(0x99, 0, 0, 0), 1)], 0));

    readonly StackPanel bars = new();

    Overlay()
    {
        (WindowStyle, AllowsTransparency, Background, ResizeMode) = (WindowStyle.None, true, Brushes.Transparent, ResizeMode.NoResize);
        (Topmost, ShowInTaskbar, ShowActivated, Focusable, SizeToContent) = (true, false, false, false, SizeToContent.WidthAndHeight);
        (Left, Top) = (-10000, -10000); // placed in pixels once it has a size
        FontFamily = (FontFamily)Application.Current.Resources["Ui"];
        Content = bars;
        DpiChanged += (_, _) => Place(GetForegroundWindow()); // moving onto a screen with other scaling resizes it, so place it again
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GwlExStyle, GetWindowLong(h, GwlExStyle) | ClickThrough | ToolWindow | NoActivate);
        };
    }

    // Once a second from App, after Fps.Tick: shown while the switch is on and a game from the list is the window in front.
    public static void Tick()
    {
        try
        {
            if (!Settings.Get(Setting, false) || Fps.Now() is not { InFront: true } game)
            {
                CloseAll();
                return;
            }
            stats ??= new Stats();
            shown ??= new Overlay();
            var r = stats.Read();
            if (peaks.Game != game.Session.Start) peaks = (game.Session.Start, double.MinValue);
            if (r.GpuTemp is { } t) peaks.TempMax = Math.Max(peaks.TempMax, t);
            shown.Fill(Bars(game.Session, game.Current, r, CpuTemp(), new(peaks.TempMax == double.MinValue ? null : peaks.TempMax)));
            if (!shown.IsVisible) shown.Show();
            shown.Place(GetForegroundWindow());
        }
        catch { CloseAll(); } // never worth a crash mid-game
    }

    static void CloseAll()
    {
        shown?.Close();
        shown = null;
        stats?.Dispose();
        stats = null;
    }

    // Windows' own reading of the hottest ACPI thermal zone, which on laptops sits by the CPU. A true CPU core reading
    // needs a third-party kernel driver, which isn't worth the risk. Null when there's no zone or it reads nonsense.
    static double? CpuTemp()
    {
        if (noCpuTemp) return null;
        try
        {
            using var search = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            var hottest = search.Get().Cast<ManagementObject>().Select(z => Convert.ToDouble(z["CurrentTemperature"]) / 10 - 273.15)
                .Where(c => c is > 15 and < 115).DefaultIfEmpty(double.NaN).Max();
            if (!double.IsNaN(hottest)) return hottest;
        }
        catch (ManagementException) { }
        noCpuTemp = true;
        return null;
    }

    // FPS: green at 100 and up, yellow from 60, red below.
    static Tone FpsTone(double? fps) => fps switch { null => Tone.Plain, >= 100 => Tone.Good, >= 60 => Tone.Fair, _ => Tone.Bad };
    // Temperatures: green below 80 °C, yellow to 87 °C, orange from 87 °C, where the GPU starts slowing itself down.
    static Tone TempTone(double t) => t >= 87 ? Tone.Hot : t >= 80 ? Tone.Fair : Tone.Good;

    // What the bars say: FPS and average, the 1% and 0.1% lows, CPU usage and temperature, the GPU's usage and clock,
    // its temperature and highest, and an orange bar for each thing heat or power is holding back. Whatever heat is
    // slowing down turns orange.
    public static List<Bar> Bars(FpsSession s, double? current, Reading r, double? cpuTemp, Peaks peaks)
    {
        var bars = new List<Bar>
        {
            new("FPS", FrameStats.Show(current), FpsTone(current), "Avg", FrameStats.Show(s.Average), FpsTone(s.Average)),
            new("1% low", FrameStats.Show(s.Low1), FpsTone(s.Low1), "0.1%", FrameStats.Show(s.Low01), FpsTone(s.Low01)),
        };
        var held = new List<string>();
        var cpuSlow = r.CpuLoad >= 50 && r.CpuPercentOfBase < 100; // below its base speed while busy
        bars.Add(new("CPU", $"{r.CpuLoad:0}%", cpuSlow ? Tone.Hot : Tone.Plain,
            cpuTemp is { } ct ? "Temp" : "", cpuTemp is { } c ? $"{c:0} °C" : "", cpuTemp is { } tt ? TempTone(tt) : Tone.Plain));
        if (cpuSlow) held.Add("CPU held back by heat or power");
        var gpuHeld = r.GpuLimits is { } g ? Stats.Limit(g) switch
        {
            "Heat" => "GPU slowed by heat",
            "Power brake" => "GPU held back by the charger",
            "Slowdown" => "GPU slowed down",
            _ => null,
        } : null;
        if (r.GpuLoad is { } load)
            bars.Add(new("GPU", $"{load:0}%", gpuHeld != null ? Tone.Hot : load >= 90 ? Tone.Good : Tone.Fair,
                r.GpuClock is { } clock ? "Clock" : "", r.GpuClock is { } ck ? $"{ck:0} MHz" : "", gpuHeld != null ? Tone.Hot : Tone.Plain));
        if (r.GpuTemp is { } temp)
            bars.Add(new("GPU temp", $"{temp:0} °C", TempTone(temp),
                "Max", peaks.GpuMaxTemp is { } max ? $"{max:0} °C" : "–", peaks.GpuMaxTemp is { } m ? TempTone(m) : Tone.Plain));
        if (gpuHeld != null) held.Insert(0, gpuHeld);
        bars.AddRange(held.Select(h => new Bar("", h, Tone.Hot)));
        return bars;
    }

    void Fill(List<Bar> list)
    {
        bars.Children.Clear();
        foreach (var bar in list)
        {
            var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Right };
            grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new() { Width = new(bar.Label == "" ? 0 : 74) }); // values line up from bar to bar
            Add(grid, bar.Label, Label, 0, 0, 13);
            Add(grid, bar.Value, Paint(bar.Tone), 0, bar.Label == "" ? 0 : 1, 13);
            if (bar.SubLabel != "" || bar.SubValue != "")
            {
                grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                Add(grid, bar.SubLabel, Label, 1, 0, 11.5);
                Add(grid, bar.SubValue, Paint(bar.SubTone), 1, 1, 11.5);
            }
            bars.Children.Add(new Border
            {
                Background = Shade, Width = 230, Margin = new(0, 0, 0, 8), Padding = new(0, 3, 14, 4), Child = grid,
            });
        }
    }

    static void Add(Grid grid, string text, Brush brush, int row, int column, double size)
    {
        var block = new TextBlock
        {
            Text = text, Foreground = brush, FontSize = size, Margin = new(column == 0 ? 0 : 6, 0, 0, 0),
            HorizontalAlignment = column == 0 ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular); // numbers don't jitter as they change
        Grid.SetRow(block, row);
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    static Brush Paint(Tone tone) => tone switch { Tone.Good => Good, Tone.Fair => Fair, Tone.Bad => Bad, Tone.Hot => Hot, _ => Label };
    static Brush Frozen(byte r, byte g, byte b) => Freeze(new SolidColorBrush(Color.FromRgb(r, g, b)));
    static Brush Freeze(Brush brush) { brush.Freeze(); return brush; }

    // Flush with the top-right edge of the screen the game is on, in pixels, and back on top in case the game pushed it down.
    void Place(IntPtr game)
    {
        UpdateLayout();
        var screen = System.Windows.Forms.Screen.FromHandle(game).Bounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), top = (int)(14 * dpi.DpiScaleY);
        SetWindowPos(new WindowInteropHelper(this).Handle, TopmostWindow, screen.Right - width, screen.Top + top, 0, 0,
            NoSize | NoActivateWindow);
    }

    // For the CI snapshot: the bars with made-up numbers, as they show over a game that's running hot.
    public static Overlay Sample()
    {
        var overlay = new Overlay();
        var session = new FpsSession("Call of Duty", DateTime.Now, 600, 104.4, 131, 71.2, 52.0, true, [], 1);
        var reading = new Reading(88, 97, 1350, 1800, 115, 125, 6.1, 8, 72, 18.3, 32, true, false, 100,
            Stats.SwThermalSlowdown, 92, 2300, 4);
        overlay.Fill(Bars(session, 112, reading, 91, new(89)));
        overlay.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x5C, 0x7A)); // stands in for the game behind it
        return overlay;
    }

    const int GwlExStyle = -20, ClickThrough = 0x20, ToolWindow = 0x80, NoActivate = 0x8000000;
    const uint NoSize = 0x1, NoActivateWindow = 0x10;
    static readonly IntPtr TopmostWindow = new(-1);

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    static int GetWindowLong(IntPtr h, int index) => (int)GetWindowLongPtr(h, index);
    static void SetWindowLong(IntPtr h, int index, int value) => SetWindowLongPtr(h, index, value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
