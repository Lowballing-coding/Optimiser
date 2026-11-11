using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;

namespace Optimiser;

// A small panel in the top-right corner of the game with its FPS and what's holding it back. It's Optimiser's own window,
// kept on top and click-through: nothing is loaded into the game and its process isn't opened, so anti-cheat has nothing
// to refuse. A game in exclusive fullscreen can draw over it; borderless fullscreen always shows it.
public sealed class Overlay : Window
{
    public const string Setting = "FpsOverlay";
    static Overlay? shown;
    static Stats? stats;

    readonly Grid grid = new();

    Overlay()
    {
        (WindowStyle, AllowsTransparency, Background, ResizeMode) = (WindowStyle.None, true, Brushes.Transparent, ResizeMode.NoResize);
        (Topmost, ShowInTaskbar, ShowActivated, Focusable, SizeToContent) = (true, false, false, false, SizeToContent.WidthAndHeight);
        (Left, Top) = (-10000, -10000); // placed in pixels once it has a size
        FontFamily = (FontFamily)Application.Current.Resources["Ui"];
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x11, 0x13, 0x17)),
            CornerRadius = new(8),
            Padding = new(12, 8, 14, 10),
            Child = grid,
        };
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
            shown.Fill(Lines(game.Session, game.Current, stats.Read()));
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

    // What the panel says: the FPS numbers, the CPU and GPU, and a warning line when heat or power is holding them back.
    public static List<(string Label, string Value, bool Warn)> Lines(FpsSession s, double? current, Reading r)
    {
        var lines = new List<(string, string, bool)>
        {
            ("FPS", FrameStats.Show(current), false),
            ("Average", FrameStats.Show(s.Average), false),
            ("1% low", FrameStats.Show(s.Low1), false),
        };
        var cpuHeld = false;
        if (r.CpuPercentOfBase is { } pct && r.CpuBaseMhz is { } baseMhz)
        {
            lines.Add(("CPU", $"{pct / 100 * baseMhz / 1000:0.0} GHz", false));
            cpuHeld = r.CpuLoad >= 50 && pct < 100;
        }
        if (r.GpuLoad is { } load) lines.Add(("GPU", $"{load:0}%", false));
        if (r.GpuTemp is { } temp) lines.Add(("GPU temp", $"{temp:0} °C", temp >= 87));
        var gpuHeld = r.GpuLimits is { } g ? Stats.Limit(g) switch
        {
            "Heat" => "GPU slowed by heat",
            "Power brake" => "GPU held back by the charger",
            "Slowdown" => "GPU slowed down",
            _ => null,
        } : null;
        if (gpuHeld != null) lines.Add(("", gpuHeld, true));
        if (cpuHeld) lines.Add(("", "CPU held back by heat or power", true));
        return lines;
    }

    void Fill(List<(string Label, string Value, bool Warn)> lines)
    {
        var res = Application.Current.Resources;
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        for (var i = 0; i < lines.Count; i++)
        {
            var (label, value, warn) = lines[i];
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var first = i == 0;
            var name = new TextBlock
            {
                Text = label, FontSize = 12, Foreground = (Brush)res["Muted"],
                Margin = new(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            var number = new TextBlock
            {
                Text = value, FontSize = first ? 22 : 13, FontWeight = first ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = (Brush)res[warn ? "Warn" : first ? "Accent" : "Text"],
                TextAlignment = TextAlignment.Right, HorizontalAlignment = label == "" ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            };
            Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular); // numbers don't jitter as they change
            Grid.SetRow(name, i);
            Grid.SetRow(number, i);
            if (label == "") Grid.SetColumnSpan(number, 2); // a warning runs across the panel
            else
            {
                Grid.SetColumn(number, 1);
                grid.Children.Add(name);
            }
            grid.Children.Add(number);
        }
    }

    // Top-right of the screen the game is on, in pixels, and back on top in case the game pushed it down.
    void Place(IntPtr game)
    {
        UpdateLayout();
        var screen = System.Windows.Forms.Screen.FromHandle(game).Bounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), margin = (int)(16 * dpi.DpiScaleX);
        SetWindowPos(new WindowInteropHelper(this).Handle, TopmostWindow, screen.Right - width - margin, screen.Top + margin, 0, 0,
            NoSize | NoActivateWindow);
    }

    // For the CI snapshot: the panel with made-up numbers, as it shows over a game that's running hot.
    public static Overlay Sample()
    {
        var overlay = new Overlay();
        var session = new FpsSession("Call of Duty", DateTime.Now, 600, 104.4, 131, 71.2, 52.0, true, [], 1);
        var reading = new Reading(88, 97, 1350, 1800, 115, 125, 6.1, 8, 72, 18.3, 32, true, false, 100,
            Stats.SwThermalSlowdown, 92, 2300, 4);
        overlay.Fill(Lines(session, 112, reading));
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
