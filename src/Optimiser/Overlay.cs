using System.Globalization;
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

    static readonly Brush Label = Frozen(0xE8, 0xE8, 0xE8), Good = Frozen(0x8B, 0xE0, 0x4E), Fair = Frozen(0xE8, 0xD8, 0x4A),
        Bad = Frozen(0xFF, 0x5A, 0x4F), Hot = Frozen(0xFF, 0x9A, 0x2E);
    // Okabe-Ito colours for the colour-blind palette: blue, yellow and pink stay apart for every common kind.
    static readonly Brush SafeGood = Frozen(0x56, 0xB4, 0xE9), SafeFair = Frozen(0xF0, 0xE4, 0x42), SafeBad = Frozen(0xCC, 0x79, 0xA7);
    static readonly LinearGradientBrush Shade = Fade(0), ShadeLeft = Fade(180); // dark at the screen edge, fading inwards

    // How it looks, set on the Stats page. Numbers are saved as text; anything unset is the look it shipped with.
    public const string SizeSetting = "OverlaySize", ShadeSetting = "OverlayShade", OpacitySetting = "OverlayOpacity", EdgeSetting = "OverlayEdge",
        CornerSetting = "OverlayCorner", ColoursSetting = "OverlayColours", DetailsSetting = "OverlayDetails", HiddenSetting = "OverlayHidden";
    public static readonly string[] Corners = ["Top right", "Top left", "Bottom right", "Bottom left"], Palettes = ["Standard", "Colour-blind", "White"],
        StatNames = ["FPS", "1% low", "CPU", "GPU", "GPU temp", "Ping", "Warnings"];
    public static readonly string[] LookSettings = [SizeSetting, ShadeSetting, OpacitySetting, EdgeSetting, CornerSetting, ColoursSetting, DetailsSetting, HiddenSetting];
    public const double DefaultSize = 80, DefaultEdge = 14; // 80%: Peter found the first size too big (2026-11-27)

    public static double Number(string setting, double fallback) =>
        double.TryParse(Settings.GetText(setting), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    Overlay()
    {
        (WindowStyle, AllowsTransparency, Background, ResizeMode) = (WindowStyle.None, true, Brushes.Transparent, ResizeMode.NoResize);
        (Topmost, ShowInTaskbar, ShowActivated, Focusable, SizeToContent) = (true, false, false, false, SizeToContent.WidthAndHeight);
        (Left, Top) = (-10000, -10000); // placed in pixels once it has a size
        FontFamily = (FontFamily)Application.Current.Resources["Ui"];
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
            Network.ForOverlay = true;
            shown.Fill(Layout(Bars(game.Session, game.Current, r, CpuTemp(), new(peaks.TempMax == double.MinValue ? null : peaks.TempMax), Network.Now()),
                Settings.GetText(LayoutSetting), Settings.GetList(HiddenSetting)));
            if (!shown.IsVisible) shown.Show();
            shown.Place(GetForegroundWindow());
        }
        catch { CloseAll(); } // never worth a crash mid-game
    }

    static void CloseAll()
    {
        if (shown != null) Network.ForOverlay = false;
        shown?.Close();
        shown = null;
        stats?.Dispose();
        stats = null;
    }

    // Windows' own reading of the hottest ACPI thermal zone, which on laptops sits by the CPU. A true CPU core reading
    // needs a third-party kernel driver, which isn't worth the risk.
    static double? CpuTemp() => Heat.Zones() is { Count: > 0 } zones ? zones.Max(z => z.Celsius) : null;

    // FPS: green at 100 and up, yellow from 60, red below.
    static Tone FpsTone(double? fps) => fps switch { null => Tone.Plain, >= 100 => Tone.Good, >= 60 => Tone.Fair, _ => Tone.Bad };
    // Temperatures: green below 80 °C, yellow to 87 °C, orange from 87 °C, where the GPU starts slowing itself down.
    static Tone TempTone(double t) => t >= 87 ? Tone.Hot : t >= 80 ? Tone.Fair : Tone.Good;

    // What the bars say: FPS and average, the 1% and 0.1% lows, CPU usage and temperature, the GPU's usage and clock,
    // its temperature and highest, and an orange bar for each thing heat or power is holding back. Whatever heat is
    // slowing down turns orange.
    public static List<Bar> Bars(FpsSession s, double? current, Reading r, double? cpuTemp, Peaks peaks, NetStats? net = null)
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
        if (net is { Ping: { } ping })
            bars.Add(new("Ping", $"{ping:0} ms", ping < 50 ? Tone.Good : ping < 100 ? Tone.Fair : Tone.Bad,
                "Jitter", $"{net.Jitter ?? 0:0} ms, {net.Loss:0}% lost", net.Loss > 0 || net.Jitter > 20 ? Tone.Bad : Tone.Plain));
        else if (net is { Loss: 100 }) bars.Add(new("Ping", "No reply", Tone.Bad));
        if (gpuHeld != null) held.Insert(0, gpuHeld);
        bars.AddRange(held.Select(h => new Bar("", h, Tone.Hot)));
        return bars;
    }

    // Layout presets: Full shows every bar, Compact the ones that matter most mid-match, FPS only just the first.
    public const string LayoutSetting = "OverlayLayout";
    public static readonly string[] Layouts = ["Full", "Compact", "FPS only"];

    // The preset, minus any stat unticked on the Stats page ("Warnings" is the orange held-back bars).
    public static List<Bar> Layout(List<Bar> bars, string? layout, string[]? hidden = null) =>
        [.. (layout switch
        {
            "Compact" => bars.Where(b => b.Label is "FPS" or "1% low" or "GPU temp" or "Ping" or ""),
            "FPS only" => bars.Take(1),
            _ => bars,
        }).Where(b => !(hidden ?? []).Contains(b.Label == "" ? "Warnings" : b.Label))];

    void Fill(List<Bar> list) => Content = Build(list);

    // The bars as the saved look has them: size, see-through background, overall opacity, side, colours and second lines.
    // Also drawn as the preview on the Stats page.
    public static StackPanel Build(List<Bar> list)
    {
        var left = Settings.GetText(CornerSetting)?.EndsWith("left") == true;
        var palette = Settings.GetText(ColoursSetting);
        var details = Settings.Get(DetailsSetting, true);
        var shade = (left ? ShadeLeft : Shade).Clone();
        shade.Opacity = Math.Clamp(Number(ShadeSetting, 100), 0, 100) / 100;
        shade.Freeze();
        var scale = Math.Clamp(Number(SizeSetting, DefaultSize), 50, 150) / 100;
        var panel = new StackPanel { LayoutTransform = new ScaleTransform(scale, scale), Opacity = Math.Clamp(Number(OpacitySetting, 100), 20, 100) / 100 };
        foreach (var bar in list)
        {
            var grid = new Grid { HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right };
            grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new() { Width = new(bar.Label == "" ? 0 : 74) }); // values line up from bar to bar
            Add(grid, bar.Label, Label, 0, 0, 13);
            Add(grid, bar.Value, Paint(bar.Tone, palette), 0, bar.Label == "" ? 0 : 1, 13);
            if (details && (bar.SubLabel != "" || bar.SubValue != ""))
            {
                grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                Add(grid, bar.SubLabel, Label, 1, 0, 11.5);
                Add(grid, bar.SubValue, Paint(bar.SubTone, palette), 1, 1, 11.5);
            }
            panel.Children.Add(new Border
            {
                Background = shade, Width = 230, Margin = new(0, 0, 0, 8), Padding = left ? new(14, 3, 0, 4) : new(0, 3, 14, 4), Child = grid,
            });
        }
        return panel;
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

    // Orange (heat) stays orange in every palette, so a slowdown always stands out.
    static Brush Paint(Tone tone, string? palette) => (tone, palette) switch
    {
        (Tone.Hot, _) => Hot,
        (_, "White") => Label,
        (Tone.Good, "Colour-blind") => SafeGood,
        (Tone.Fair, "Colour-blind") => SafeFair,
        (Tone.Bad, "Colour-blind") => SafeBad,
        (Tone.Good, _) => Good,
        (Tone.Fair, _) => Fair,
        (Tone.Bad, _) => Bad,
        _ => Label,
    };
    static Brush Frozen(byte r, byte g, byte b) => Freeze(new SolidColorBrush(Color.FromRgb(r, g, b)));
    static Brush Freeze(Brush brush) { brush.Freeze(); return brush; }
    static LinearGradientBrush Fade(double angle)
    {
        var brush = new LinearGradientBrush(
            [new(Color.FromArgb(0x00, 0, 0, 0), 0), new(Color.FromArgb(0x8C, 0, 0, 0), 0.35), new(Color.FromArgb(0x99, 0, 0, 0), 1)], angle);
        brush.Freeze();
        return brush;
    }

    // Flush with the picked side of the screen the game is on, the saved gap from its top or bottom, in pixels, and back on
    // top in case the game pushed it down.
    void Place(IntPtr game)
    {
        UpdateLayout();
        var screen = System.Windows.Forms.Screen.FromHandle(game).Bounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        var corner = Settings.GetText(CornerSetting) ?? Corners[0];
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY),
            gap = (int)(Math.Clamp(Number(EdgeSetting, DefaultEdge), 0, 200) * dpi.DpiScaleY);
        SetWindowPos(new WindowInteropHelper(this).Handle, TopmostWindow, corner.EndsWith("left") ? screen.Left : screen.Right - width,
            corner.StartsWith("Bottom") ? screen.Bottom - height - gap : screen.Top + gap, 0, 0, NoSize | NoActivateWindow);
    }

    // Made-up numbers, as they show over a game that's running hot: the CI snapshot and the Stats page preview.
    public static List<Bar> SampleBars() => Bars(new FpsSession("Call of Duty", DateTime.Now, 600, 104.4, 131, 71.2, 52.0, true, [], 1), 112,
        new Reading(88, 97, 1350, 1800, 115, 125, 6.1, 8, 72, 18.3, 32, true, false, 100, Stats.SwThermalSlowdown, 92, 2300, 4),
        91, new(89), new(24, 3, 0));

    public static Overlay Sample()
    {
        var overlay = new Overlay();
        overlay.Fill(SampleBars());
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

// The key that shows or hides the overlay from inside a game: a Windows hotkey on Optimiser's main window, which works
// whichever window is in front. Saved as "modifiers,virtual key"; Ctrl+Shift+O until you pick another.
public static class OverlayKey
{
    public const string Setting = "OverlayHotkey";
    const int Id = 1;
    public const uint Alt = 1, Ctrl = 2, Shift = 4, NoRepeat = 0x4000;
    public const int Message = 0x0312; // WM_HOTKEY

    public static (uint Mods, uint Key) Current =>
        Settings.GetText(Setting)?.Split(',') is [var m, var k] && uint.TryParse(m, out var mods) && uint.TryParse(k, out var key) ? (mods, key) : (Ctrl | Shift, 0x4F);

    // False when another program already has that combination.
    public static bool Register(IntPtr hwnd, (uint Mods, uint Key) combo)
    {
        UnregisterHotKey(hwnd, Id);
        return RegisterHotKey(hwnd, Id, combo.Mods | NoRepeat, combo.Key);
    }

    public static string Name((uint Mods, uint Key) combo) =>
        string.Join("+", new[] { (Ctrl, "Ctrl"), (Alt, "Alt"), (Shift, "Shift") }.Where(m => (combo.Mods & m.Item1) != 0).Select(m => m.Item2)
            .Append(System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)combo.Key) switch
            {
                var k and >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((char)('0' + (k - System.Windows.Input.Key.D0))).ToString(),
                var k => k.ToString(),
            }));

    // Flips the overlay switch; it shows or hides at the next second.
    public static void Toggle() => Settings.Set(Overlay.Setting, !Settings.Get(Overlay.Setting, false));

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
