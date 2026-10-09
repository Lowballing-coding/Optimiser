using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Optimiser;

public partial class MainWindow : Window
{
    readonly Backup backup = new();

    public MainWindow()
    {
        InitializeComponent();
        ((RadioButton)Nav.Children[0]).IsChecked = true;
        StateChanged += (_, _) =>
        {
            var maximised = WindowState == WindowState.Maximized;
            // A borderless window hangs off the screen edge when maximised; pad it back in.
            Root.Margin = maximised ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
            MaxButton.Content = maximised ? "" : "";
            MaxButton.ToolTip = maximised ? "Restore" : "Maximise";
        };
        Loaded += OnLoaded;
        ShowChanges();
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowHardware(await Task.Run(Hardware.Detect));
        }
        catch (Exception ex)
        {
            Headline.Text = "Couldn't scan this PC";
            Subline.Text = $"Windows hardware info (WMI) failed: {ex.Message}";
        }

        // `Optimiser.exe --snapshot <folder>` saves a picture of every page and exits. CI uses it to check the UI.
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--snapshot");
        if (i >= 0 && i + 1 < args.Length) await Snapshot(args[i + 1]);
    }

    void ShowHardware(Hardware hw)
    {
        Headline.Text = "Not optimised yet";
        Subline.Text = "Optimisations arrive in the next update. This is what Optimiser found on your PC.";

        CpuName.Text = hw.Cpu.Replace("(R)", "").Replace("(TM)", "");
        CpuCores.Text = hw.IsHybridCpu
            ? $"{hw.PCores} performance + {hw.ECores} efficiency cores, {hw.Threads} threads"
            : $"{hw.PCores} cores, {hw.Threads} threads";
        GpuNames.Text = string.Join("\n", hw.Gpus.OrderByDescending(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)));
        Ram.Text = $"{hw.RamGb:0} GB";
        var maker = hw.IsMsi ? "MSI" : hw.Maker;
        Machine.Text = $"{maker} {hw.Model} ({(hw.IsLaptop ? "laptop" : "desktop")})";

        Features.Children.Clear();
        AddFeature("Nvidia graphics tweaks", hw.HasNvidia);
        AddFeature("Performance-core scheduling for games", hw.IsHybridCpu);
        AddFeature("Laptop power and battery checks", hw.IsLaptop);
        AddFeature("MSI Center checks", hw.IsMsi);
    }

    void AddFeature(string name, bool found)
    {
        var dot = new Ellipse
        {
            Width = 8, Height = 8, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)FindResource(found ? "Good" : "Line"),
        };
        var text = new TextBlock(new Run(name)) { TextWrapping = TextWrapping.Wrap };
        if (!found)
        {
            text.Foreground = (Brush)FindResource("Muted");
            text.Inlines.Add(new Run(" (not on this PC)"));
        }
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        row.Children.Add(dot);
        row.Children.Add(text);
        Features.Children.Add(row);
    }

    void OnNav(object sender, RoutedEventArgs e)
    {
        var page = (string)((RadioButton)sender).Tag;
        PageTitle.Text = page;
        foreach (FrameworkElement p in Pages.Children)
            p.Visibility = p.Name == page ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowChanges()
    {
        ChangeList.ItemsSource = backup.Changes.AsEnumerable().Reverse().ToList();
        NoChanges.Visibility = backup.Changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UndoAllButton.IsEnabled = backup.Changes.Count > 0;
    }

    void OnUndoAll(object sender, RoutedEventArgs e)
    {
        try
        {
            var count = backup.Changes.Count;
            backup.UndoAll();
            BackupStatus.Text = $"Put back {count} setting{(count == 1 ? "" : "s")}. Some only take effect after a restart.";
        }
        catch (Exception ex)
        {
            BackupStatus.Text = $"Couldn't undo everything: {ex.Message}. The settings still listed below are still changed.";
        }
        ShowChanges();
    }

    async void OnRestorePoint(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        BackupStatus.Text = "Creating a restore point. This can take a minute.";
        var error = await Task.Run(() => RestorePoint.Create("Optimiser manual restore point"));
        BackupStatus.Text = error == null
            ? "Restore point created. If one was already made today, Windows keeps that one instead."
            : $"Couldn't create a restore point. {error}";
        button.IsEnabled = true;
    }

    async Task Snapshot(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (RadioButton nav in Nav.Children)
        {
            nav.IsChecked = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(System.IO.Path.Combine(folder, $"{nav.Tag}.png"));
            png.Save(file);
        }
        Close();
    }

    void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnMaximise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void OnClose(object sender, RoutedEventArgs e) => Close();
}
