using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Optimiser;

// What a template shows for one optimisation.
public record Row(Optimisation Item, bool On, bool CanSwitch, string? Note)
{
    public string Name => Item.Name;
    public string Description => Item.Description;
    public string? ButtonText => Item.ButtonText;
    public bool HasButton => Item.Button != null;
    public bool IsSwitch => Item.TurnOn != null;
    public bool IsCheck => Item.TurnOn == null && Item.CountsToScore;
    public bool HasNote => Note != null;
    public string Status => On ? "Done" : "Not yet";
    public string SwitchTip => !CanSwitch ? "Already set this way in Windows" : On ? "Turn off and put back the original" : "Turn on";
}

// One tweak's saved originals on the Backups page.
public record BackupGroup(string Tweak, string Detail);

public partial class MainWindow : Window
{
    bool busy;

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
            App.Hardware = await Task.Run(Hardware.Detect);
            ShowHardware(App.Hardware);
            Refresh();
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
        CpuName.Text = hw.Cpu.Replace("(R)", "").Replace("(TM)", "");
        CpuCores.Text = hw.IsHybridCpu
            ? $"{hw.PCores} performance + {hw.ECores} efficiency cores, {hw.Threads} threads"
            : $"{hw.PCores} cores, {hw.Threads} threads";
        GpuNames.Text = string.Join("\n", hw.Gpus.OrderByDescending(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)));
        Ram.Text = $"{hw.RamGb:0} GB memory";
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
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(dot);
        row.Children.Add(text);
        Features.Children.Add(row);
    }

    // Re-reads the real state of every optimisation and redraws everything that depends on it.
    void Refresh()
    {
        if (App.Hardware == null) return;
        var rows = Tweaks.All(App.Backup).Where(o => o.AppliesTo(App.Hardware)).Select(ToRow).ToList();

        var scored = rows.Where(r => r.Item.CountsToScore).ToList();
        var score = scored.Count == 0 ? 100 : 100 * scored.Count(r => r.On) / scored.Count;
        var left = scored.Where(r => !r.On).ToList();
        Headline.Text = $"{score}% optimised";
        Subline.Text = left.Count == 0
            ? "Everything Optimiser recommends for this PC is on."
            : $"{left.Count} recommendation{(left.Count == 1 ? "" : "s")} left that can make games run faster.";
        DrawScore(score / 100.0);

        var notices = new List<string>();
        if (App.RestartNeeded.Count > 0) notices.Add($"Restart your PC to finish changing {string.Join(" and ", App.RestartNeeded)}.");
        if (App.Hardware.IsLaptop && SystemParameters.PowerLineStatus == PowerLineStatus.Offline)
            notices.Add("You're on battery, so the GPU is held back. Plug in for full performance.");
        Notice.Text = string.Join("\n", notices);
        Notice.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Recommended.ItemsSource = left;
        AllDone.Visibility = left.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyAllButton.Visibility = left.Any(r => r.IsSwitch) ? Visibility.Visible : Visibility.Collapsed;

        Groups.Children.Clear();
        foreach (var group in rows.GroupBy(r => r.Item.Group))
        {
            Groups.Children.Add(new TextBlock { Text = group.Key, Style = (Style)FindResource("GroupTitle") });
            Groups.Children.Add(new ItemsControl { ItemsSource = group.ToList(), ItemTemplate = (DataTemplate)FindResource("RowCard") });
        }
        ShowChanges();
    }

    static Row ToRow(Optimisation o)
    {
        var on = o.IsOn();
        var alreadyInWindows = o.UsesBackup && on && !App.Backup.Owns(o.Name);
        var note = alreadyInWindows ? "Already on in Windows"
                 : o.NeedsRestart && App.RestartNeeded.Contains(o.Name) ? "Restart to finish"
                 : o.NeedsRestart ? "Needs a restart"
                 : null;
        return new Row(o, on, o.TurnOn != null && !alreadyInWindows, note);
    }

    // Draws the orange part of the gauge: 270 degrees around, starting bottom-left, for a full score.
    void DrawScore(double fraction)
    {
        const double cx = 74, cy = 74, r = 67, start = 135;
        if (fraction <= 0) { ScoreArc.Data = null; return; }
        Point At(double degrees) => new(cx + r * Math.Cos(degrees * Math.PI / 180), cy + r * Math.Sin(degrees * Math.PI / 180));
        var sweep = 270 * Math.Min(fraction, 1);
        var figure = new PathFigure { StartPoint = At(start), IsClosed = false };
        figure.Segments.Add(new ArcSegment(At(start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
        ScoreArc.Data = new PathGeometry([figure]);
    }

    // Runs a change off the UI thread (restore points and services can take a while), then redraws.
    async Task<bool> Change(string doing, Action action, string done)
    {
        if (busy) return false;
        busy = true;
        StatusText.Text = doing;
        var firstChange = App.Backup.Snapshot().Count == 0;
        var ok = true;
        try
        {
            await Task.Run(action);
            StatusText.Text = done;
            if (firstChange && App.Backup.RestorePointError is { } error)
                StatusText.Text += $" No restore point was made ({error}), but the original settings are saved on the Backups page.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"That didn't work: {ex.Message}";
            ok = false;
        }
        busy = false;
        Refresh();
        return ok;
    }

    async void OnRowSwitch(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var o = ((Row)box.DataContext).Item;
        var ok = box.IsChecked == true
            ? await Change($"Turning on {o.Name}…", o.TurnOn!, $"Turned on {o.Name}.")
            : await Change($"Turning off {o.Name}…", o.TurnOff!, $"Turned off {o.Name} and put back the original setting.");
        if (ok && o.NeedsRestart) App.RestartNeeded.Add(o.Name);
        Refresh(); // also snaps the switch back if the change failed or was ignored while busy
    }

    async void OnRowButton(object sender, RoutedEventArgs e)
    {
        var row = (Row)((FrameworkElement)sender).DataContext;
        await Change($"Opening {row.ButtonText?.Replace("Open ", "")}…", row.Item.Button!, "");
    }

    async void OnApplyAll(object sender, RoutedEventArgs e)
    {
        var todo = Tweaks.All(App.Backup)
            .Where(o => o.AppliesTo(App.Hardware) && o.CountsToScore && o.TurnOn != null && !o.IsOn()).ToList();
        var failed = new List<string>();
        var applied = new List<Optimisation>();
        await Change($"Applying {todo.Count} optimisations…", () =>
        {
            foreach (var o in todo)
            {
                try { o.TurnOn!(); applied.Add(o); }
                catch (Exception ex) { failed.Add($"{o.Name} ({ex.Message})"); }
            }
        }, $"Applied {todo.Count} optimisations.");
        foreach (var o in applied.Where(o => o.NeedsRestart)) App.RestartNeeded.Add(o.Name);
        if (failed.Count > 0) StatusText.Text = $"Some couldn't be applied: {string.Join(", ", failed)}.";
        Refresh();
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
        var changes = App.Backup.Snapshot();
        ChangeList.ItemsSource = changes
            .GroupBy(c => c.Tweak)
            .Select(g => new BackupGroup(g.Key, g.Count() == 1
                ? $@"{g.First().Key}\{g.First().Name}, changed {g.First().At:d MMM HH:mm}"
                : $"{g.Count()} settings, changed {g.First().At:d MMM HH:mm}"))
            .Reverse()
            .ToList();
        NoChanges.Visibility = changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UndoAllButton.IsEnabled = changes.Count > 0;
    }

    // Undo goes through each tweak's own "off", so side effects like restarting a service happen too.
    static Action TurnOffFor(string tweak) =>
        Tweaks.All(App.Backup).FirstOrDefault(o => o.Name == tweak)?.TurnOff ?? (() => App.Backup.Undo(tweak));

    async void OnUndoOne(object sender, RoutedEventArgs e)
    {
        var tweak = ((BackupGroup)((FrameworkElement)sender).DataContext).Tweak;
        if (await Change($"Undoing {tweak}…", TurnOffFor(tweak), $"Put back the original settings for {tweak}.")
            && Tweaks.All(App.Backup).Any(o => o.Name == tweak && o.NeedsRestart))
            App.RestartNeeded.Add(tweak);
        Refresh();
    }

    async void OnUndoAll(object sender, RoutedEventArgs e)
    {
        var tweaks = App.Backup.Snapshot().Select(c => c.Tweak).Distinct().Reverse().ToList();
        await Change("Undoing everything…", () => { foreach (var t in tweaks) TurnOffFor(t)(); },
            $"Put back the original settings for {tweaks.Count} optimisation{(tweaks.Count == 1 ? "" : "s")}. "
            + "Some only take effect after a restart.");
    }

    async void OnRestorePoint(object sender, RoutedEventArgs e)
    {
        string? error = null;
        await Change("Creating a restore point. This can take a minute…",
            () => error = RestorePoint.Create("Optimiser manual restore point"),
            "Restore point created. If one was already made today, Windows keeps that one instead.");
        if (error != null) StatusText.Text = $"Couldn't create a restore point. {error}";
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
        Application.Current.Shutdown();
    }

    void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnMaximise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void OnClose(object sender, RoutedEventArgs e) => Close();
}
