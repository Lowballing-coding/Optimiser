using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Optimiser;

public record SpikeRow(Spike Spike)
{
    public string Heading => $"{Spike.At:d MMM HH:mm}, {Spike.Celsius:0} °C";
    public string Detail => $"{Spike.Zone}, CPU {Spike.CpuPercent:0}% busy. " + (Spike.Top.Length == 0 ? "Nothing was using the CPU." : string.Join(", ", Spike.Top));
}

public partial class HeatPage : UserControl
{
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly Queue<(double Temp, int Rpm)> recent = new(); // the last 10 readings, for the fan lag warning
    bool sample;

    public HeatPage()
    {
        InitializeComponent();
        timer.Tick += (_, _) => Read();
        IsVisibleChanged += (_, _) =>
        {
            if (sample) return;
            if (!IsVisible) { timer.Stop(); recent.Clear(); return; }
            QuietSwitch.IsChecked = Settings.Get(QuietProfile.Setting, false);
            WatchSwitch.IsChecked = Settings.Get(Heat.WatchSetting, true);
            ShowQuiet();
            ShowSpikes(Heat.Spikes());
            Read();
            timer.Start(); // the fans are only read while this page is open
        };
    }

    async void Read()
    {
        var (zones, fans) = await Task.Run(() => (Heat.Zones(), Heat.FanRpm()));
        if (!IsVisible) return;
        Show(zones, fans);
        if (zones.Count == 0 || fans == null) return;
        recent.Enqueue((zones.Max(z => z.Celsius), fans.Max()));
        if (recent.Count > 10) recent.Dequeue();
        Lag.Visibility = Heat.FansLag([.. recent]) ? Visibility.Visible : Visibility.Collapsed;
    }

    void Show(List<Zone> zones, int[]? fans)
    {
        Readings.Children.Clear();
        foreach (var z in zones.OrderByDescending(z => z.Celsius)) Readings.Children.Add(Reading(z.Name, $"{z.Celsius:0} °C", z.Celsius >= Heat.SpikeAt));
        for (var i = 0; i < (fans?.Length ?? 0); i++) Readings.Children.Add(Reading($"Fan {i + 1}", fans![i] == 0 ? "Stopped" : $"{fans[i]:N0} RPM", false));
        NoReadings.Visibility = zones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoReadings.Text = "Windows doesn't report any temperature sensors on this PC.";
        FanNote.Text = fans != null
            ? "Fans read from MSI's firmware, the same readings MSI Center shows. Stopped is normal when the laptop is cool."
            : "This PC doesn't share its fan speeds with Windows. On MSI laptops MSI Center shows them.";
    }

    StackPanel Reading(string label, string value, bool hot) => new()
    {
        Margin = new(0, 0, 16, 14),
        Children =
        {
            new TextBlock { Text = label, Style = (Style)FindResource("MutedText"), FontSize = 13 },
            new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new(0, 4, 0, 0),
                            Foreground = (System.Windows.Media.Brush)FindResource(hot ? "Warn" : "Text") },
        },
    };

    void ShowQuiet() => QuietState.Text = !Settings.Get(QuietProfile.Setting, false) ? ""
        : QuietProfile.Capped ? "On now: boost off and Best power efficiency." : "Lifted while a game runs.";

    void ShowSpikes(List<Spike> spikes)
    {
        SpikeList.ItemsSource = spikes.Select(s => new SpikeRow(s)).ToList();
        NoSpikes.Visibility = spikes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = spikes.Count > 0;
    }

    async void OnQuiet(object sender, RoutedEventArgs e)
    {
        var on = QuietSwitch.IsChecked == true;
        Settings.Set(QuietProfile.Setting, on);
        await Task.Run(() =>
        {
            if (!on) QuietProfile.Lift();
            else if (App.Watcher is { } w) w.ApplyRates(); // caps now unless a game is running
        });
        ShowQuiet();
    }

    void OnWatch(object sender, RoutedEventArgs e) => Settings.Set(Heat.WatchSetting, WatchSwitch.IsChecked == true);

    void OnClear(object sender, RoutedEventArgs e)
    {
        Heat.ClearSpikes();
        ShowSpikes([]);
    }

    // For the CI snapshot, which has no thermal zones or fans: a hot moment with a spike logged.
    public void ShowSample()
    {
        sample = true;
        timer.Stop();
        Show([new("TZ00", 95), new("TZ01", 61), new("TZ02", 48)], [2650, 0]);
        Lag.Visibility = Visibility.Visible;
        (QuietSwitch.IsChecked, WatchSwitch.IsChecked) = (true, true);
        QuietState.Text = "On now: boost off and Best power efficiency.";
        ShowSpikes([new(new DateTime(2026, 11, 24, 23, 41, 0), "TZ00", 96, 4, ["MSI.CentralServer 1.8%", "AWCC.UCSubAgent 1.1%", "explorer 0.4%"])]);
    }
}
