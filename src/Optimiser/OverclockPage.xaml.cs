using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Optimiser;

public partial class OverclockPage : UserControl
{
    bool sample;

    public OverclockPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible && !sample) Show(); };
    }

    // Reads the GPU fresh each time the page opens, since a game or another app may have changed it.
    void Show()
    {
        ShowCpu(App.Hardware?.Cpu);
        Keep.IsChecked = Settings.Get(Overclock.KeepSetting, false);
        if (Overclock.Running) return; // the test's progress is on screen
        using var gpu = NvGpu.Open(out var why);
        Testing.Visibility = StopButton.Visibility = Visibility.Collapsed;
        if (gpu == null)
        {
            (State.Text, StateDot.Fill) = (why!, Paint("Muted"));
            Controls.Visibility = Visibility.Collapsed;
            return;
        }
        GpuName.Text = gpu.Name;
        var o = gpu.Offsets;
        (State.Text, StateDot.Fill) = o == (0, 0) ? ("On Nvidia's own clocks.", Paint("Muted"))
                                    : ($"Overclocked: +{o.Core} MHz core, +{o.Memory} MHz memory.", Paint("Good"));
        if (Overclock.Crashed is { } c && Result.Visibility != Visibility.Visible)
            ShowResult($"The last test didn't finish at +{c.Core} MHz core and +{c.Memory} MHz memory, most likely because the PC froze. "
                     + "Your GPU started on stock clocks, and the next test stays below that setting.");
        Controls.Visibility = Visibility.Visible;
        TuneButton.IsEnabled = true;
        StockButton.IsEnabled = o != (0, 0) || Overclock.Saved != null;
    }

    void ShowCpu(string? cpu)
    {
        if (cpu == null) return;
        CpuName.Text = cpu;
        // Intel's K and HX chips and AMD's desktop Ryzens can be overclocked; other laptop chips are locked by their maker.
        var unlocked = Regex.IsMatch(cpu, @"\d{4,5}K[FS]?\b|\d{4,5}HX\b", RegexOptions.IgnoreCase)
                    || (cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) && App.Hardware?.IsLaptop == false);
        CpuNote.Text = unlocked
            ? "This processor can be overclocked, but only in the BIOS, where a wrong setting can stop Windows starting. "
              + "That's not something an app can test its way out of safely, so Optimiser leaves it alone."
            : "Its maker locks this processor's speed, so it can't be overclocked, and on recent laptops undervolting is switched off "
              + "in the BIOS to guard against a security flaw. It already boosts as high as it safely can while it's cool and plugged "
              + "in, and Optimiser's power tweaks and gaming profile help it stay there.";
    }

    async void OnTune(object sender, RoutedEventArgs e)
    {
        TuneButton.IsEnabled = StockButton.IsEnabled = false;
        StopButton.Visibility = Testing.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;
        Result.Visibility = Visibility.Collapsed;
        (State.Text, StateDot.Fill) = ("Testing. Your games and other GPU apps will be slow until it finishes.", Paint("Warn"));
        (Stage.Text, Live.Text, Bar.Value) = ("Starting the test…", "", 0);
        string result;
        try
        {
            result = await Overclock.Tune(p => Dispatcher.InvokeAsync(() => ShowProgress(p)));
        }
        catch (Exception ex) { result = $"The test couldn't run: {ex.Message}"; }
        Show();
        ShowResult(result);
    }

    void ShowProgress(TuneProgress p)
    {
        if (!Overclock.Running) return; // a late report after the test ended
        Stage.Text = p.Stage;
        Live.Text = string.Join("   ", new[] { p.Temp is { } t ? $"{t} °C" : null, p.Clock is { } c ? $"{c:N0} MHz" : null }.OfType<string>());
        Bar.Value = p.Fraction * 100;
    }

    void ShowResult(string text) => (Result.Text, Result.Visibility) = (text, Visibility.Visible);

    void OnStop(object sender, RoutedEventArgs e)
    {
        Overclock.Stop();
        StopButton.IsEnabled = false;
        Stage.Text = "Stopping…";
    }

    void OnStock(object sender, RoutedEventArgs e)
    {
        try
        {
            Overclock.Reset();
            ShowResult("Your GPU is back on Nvidia's own clocks.");
        }
        catch (Exception ex) { ShowResult($"That didn't work: {ex.Message}"); }
        Show();
    }

    void OnKeep(object sender, RoutedEventArgs e) => Settings.Set(Overclock.KeepSetting, Keep.IsChecked == true);

    Brush Paint(string name) => (Brush)FindResource(name);

    // For the CI snapshot, which has no Nvidia GPU: the page partway through a test.
    public void ShowSample()
    {
        sample = true;
        GpuName.Text = "NVIDIA GeForce RTX 3070 Ti Laptop GPU";
        ShowCpu("12th Gen Intel(R) Core(TM) i7-12700H");
        (State.Text, StateDot.Fill) = ("Testing. Your games and other GPU apps will be slow until it finishes.", Paint("Warn"));
        Testing.Visibility = StopButton.Visibility = Visibility.Visible;
        (Stage.Text, Live.Text, Bar.Value) = ("Testing the memory at +300 MHz", "74 °C   1,785 MHz", 55);
        TuneButton.IsEnabled = StockButton.IsEnabled = false;
    }
}
