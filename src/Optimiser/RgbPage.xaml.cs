using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Optimiser;

// One RGB app on the page: whether it has Optimiser's colour, and what to tell you about it.
public record RgbRow(RgbApp App, string Badge, Brush BadgeBrush, string Detail)
{
    public bool CanOpen => App.Open != null;
    public string OpenText => App.Open?.StartsWith("ms-settings:") == true ? "Open settings" : "Open";
}

public partial class RgbPage : UserControl
{
    static readonly (string Name, Color Colour)[] Presets =
    [
        ("orange", Color.FromRgb(0xFF, 0x8A, 0x1F)),
        ("red", Color.FromRgb(0xFF, 0x1E, 0x1E)),
        ("pink", Color.FromRgb(0xFF, 0x2D, 0x95)),
        ("purple", Color.FromRgb(0x8A, 0x2B, 0xE2)),
        ("blue", Color.FromRgb(0x1E, 0x5B, 0xFF)),
        ("cyan", Color.FromRgb(0x00, 0xC8, 0xFF)),
        ("green", Color.FromRgb(0x00, 0xE0, 0x5A)),
        ("white", Colors.White),
        ("off", Colors.Black),
    ];
    readonly Button custom;
    List<RgbApp>? apps; // null until the page is first opened
    bool looking, sample;

    public RgbPage()
    {
        InitializeComponent();
        foreach (var (name, c) in Presets)
            Swatches.Children.Add(Swatch(name == "off" ? "Lights off" : char.ToUpper(name[0]) + name[1..], c, name == "off" ? "" : ""));
        Swatches.Children.Add(custom = Swatch("Pick any colour", null, ""));
        Rgb.Changed += () => Dispatcher.InvokeAsync(() => { if (!sample) Show(Rgb.Colour, Rgb.Last); });
        IsVisibleChanged += (_, _) => { if (IsVisible) Look(); };
        Show(Rgb.Colour, Rgb.Last);
    }

    Button Swatch(string name, Color? c, string glyph)
    {
        var button = new Button
        {
            Style = (Style)FindResource("Swatch"), Tag = c, ToolTip = name, Content = glyph,
            Background = c is { } x ? new SolidColorBrush(x) : Paint("Surface"),
            Foreground = c == Colors.Black ? Paint("Muted") : Paint("Text"),
        };
        AutomationProperties.SetName(button, name);
        button.Click += OnSwatch;
        return button;
    }

    // Looks for RGB apps the first time the page opens, rather than running PowerShell every time Optimiser starts.
    async void Look()
    {
        if (apps != null || looking) return;
        looking = true;
        var found = await Task.Run(() =>
        {
            try { return Rgb.Find(); }
            catch { return Rgb.Match([]); } // no Start menu list; still show the apps Optimiser can reach
        });
        looking = false;
        if (sample) return;
        apps = found;
        Show(Rgb.Colour, Rgb.Last);
    }

    void Show(Color? want, RgbRound last)
    {
        var preset = Presets.FirstOrDefault(p => p.Colour == want).Name;
        foreach (Button b in Swatches.Children)
            b.BorderBrush = (b.Tag is Color c && c == want) || (b == custom && want != null && preset == null) ? Paint("Text") : Brushes.Transparent;
        custom.Background = want is { } mine && preset == null ? new SolidColorBrush(mine) : Paint("Surface");
        custom.Content = preset == null && want != null ? "" : "";
        HandBack.IsEnabled = want != null;
        SummaryDot.Fill = want is { } dot ? new SolidColorBrush(dot) : Brushes.Transparent;

        var name = preset ?? (want is { } w ? Rgb.Hex(w) : "");
        var synced = (apps ?? []).Where(a => a.Lighting != null && last.Results.TryGetValue(a.Lighting, out var why) && why == null)
            .Select(a => a.Name).ToList();
        Summary.Text = want == null ? "Each app is showing its own lighting."
                     : last.Colour != want || apps == null ? (preset == "off" ? "Turning the lights off…" : $"Setting {name}…")
                     : synced.Count == 0 ? "None of your RGB apps took the colour. Each one below says why."
                     : preset == "off" ? $"Lights off on {Join(synced)}."
                     : $"Showing {name} on {Join(synced)}.";

        if (apps == null) return;
        Note.Text = "No RGB apps found. Optimiser looks for SteelSeries GG, Razer Synapse, Logitech G HUB, Wootility, "
                  + "Corsair iCUE, Armoury Crate, OpenRGB, SignalRGB and others in the Start menu.";
        Note.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppList.ItemsSource = apps.Select(a => Row(a, want, last)).ToList();
    }

    RgbRow Row(RgbApp app, Color? want, RgbRound last)
    {
        if (app.Lighting == null)
            return new(app, app.Open?.StartsWith("ms-settings:") == true ? "Windows setting" : "Open only", Paint("Muted"), app.Hint);
        if (want == null || last.Colour != want) return new(app, "Can sync", Paint("Muted"), app.Hint);
        return last.Results.TryGetValue(app.Lighting, out var why) && why == null
            ? new(app, "Synced", Paint("Good"), app.Hint)
            : new(app, "Not synced", Paint("Warn"), $"{why ?? "Optimiser hasn't reached it yet."} {app.Hint}");
    }

    static string Join(List<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names[..^1])} and {names[^1]}";

    Brush Paint(string name) => (Brush)FindResource(name);

    void OnSwatch(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is Color c)
        {
            Pick(c);
            return;
        }
        var now = Rgb.Colour ?? Presets[0].Colour;
        using var dialog = new Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(now.R, now.G, now.B) };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) Pick(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
    }

    void Pick(Color? c)
    {
        try { Rgb.Set(c); }
        catch (Exception ex)
        {
            Summary.Text = $"That didn't work: {ex.Message}";
            return;
        }
        Show(c, Rgb.Last);
    }

    void OnHandBack(object sender, RoutedEventArgs e) => Pick(null);

    void OnOpen(object sender, RoutedEventArgs e)
    {
        var app = ((RgbRow)((FrameworkElement)sender).DataContext).App;
        try { Tweaks.OpenAsYou(app.Open!); }
        catch (Exception ex) { Summary.Text = $"Couldn't open {app.Name}: {ex.Message}"; }
    }

    // For the CI snapshot: the page with apps found and a colour on, without touching any real lighting.
    public void ShowSample()
    {
        sample = true;
        apps = Rgb.Match([("SteelSeries GG", "a"), ("Razer Synapse", "b"), ("Logitech G HUB", "c"), ("Wootility", "d"),
                          ("Redragon K530RGB Pro", "e")]);
        var orange = Presets[0].Colour;
        Show(orange, new(orange, new Dictionary<Lighting, string?>
        {
            [Rgb.SteelSeriesGG] = null, [Rgb.RazerChroma] = null, [Rgb.LogitechGHub] = "It isn't running, or didn't answer.",
        }));
    }
}
