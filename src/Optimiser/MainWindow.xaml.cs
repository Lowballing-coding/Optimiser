using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

public record GameRow(Game Game, bool Running, bool ProfileOn)
{
    public string ProfileTip => ProfileOn ? "The gaming profile switches on while this game runs. Turn off to leave this game out."
                                          : "Turn on to switch on the gaming profile while this game runs.";
}

public partial class MainWindow : Window
{
    public const double SidebarOpen = 200; // sidebar width while hovered; 72 when closed
    const string PlacementSetting = "WindowPlacement";
    bool busy, toldAboutTray;
    readonly List<RadioButton> modeButtons = [];
    List<Row> rows = [];        // every optimisation on the Optimisations page, as last drawn
    List<SetRow> setRows = [];
    Stats? stats;
    readonly DispatcherTimer statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly StatTile gpuTemp = new("GPU temperature", showTrend: true), gpuLoad = new("GPU load", showTrend: true),
        gpuClock = new("GPU clock", false), gpuPower = new("GPU power", false), vram = new("Graphics memory", false),
        cpuLoad = new("CPU load", showTrend: true), ram = new("Memory", false), power = new("Power", false);

    public MainWindow()
    {
        InitializeComponent();
        ((RadioButton)Nav.Children[0]).IsChecked = true;
        StateChanged += (_, _) => ShowState();
        RestorePlacement();
        SourceInitialized += (_, _) =>
        {
            // A pin made from the window starts this copy. Updates replace it in place, so the pin keeps working.
            try { Taskbar.SetIdentity(new WindowInteropHelper(this).Handle, Environment.ProcessPath!); }
            catch { } // only changes how the taskbar groups the window
        };
        Loaded += OnLoaded;
        Closed += (_, _) => stats?.Dispose();
        Closing += (_, e) =>
        {
            SavePlacement();
            // With background mode on, closing keeps Optimiser running in the tray to watch for games.
            if (App.Exiting || !AutoStart.IsOn()) return;
            e.Cancel = true;
            Hide();
            if (!toldAboutTray) App.Notify("Optimiser is still running here and switches to the gaming profile when a game starts. Right-click to exit.");
            toldAboutTray = true;
        };
        DashMode.Content = ModePicker();
        SettingsMode.Content = ModePicker();
        App.GamesChanged += ShowGames;
        App.UpdateChanged += () => ShowUpdate(App.Update);
        ShowUpdate(null);
        statsTimer.Tick += (_, _) => ShowStats();
        statsTimer.Start();
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
        ShowChanges();
        var settings = Tweaks.AppSettings().Select(ToRow).ToList();
        UpdateSettings.ItemsSource = settings.Where(r => r.Item.Group == Tweaks.UpdatesGroup).ToList();
        ProfileSettings.ItemsSource = settings.Where(r => r.Item.Group != Tweaks.UpdatesGroup).ToList();
        if (App.Hardware == null) return;
        rows = Tweaks.All(App.Backup).Where(o => o.AppliesTo(App.Hardware)).Select(ToRow).ToList();
        SetList.ItemsSource = setRows = [.. Sets.Load().Select(s => new SetRow(s.Name, [.. SetCandidates.Where(r => s.Items.Contains(r.Name))]))];

        var scored = rows.Where(r => r.Item.CountsToScore).ToList();
        var score = scored.Count == 0 ? 100 : 100 * scored.Count(r => r.On) / scored.Count;
        var left = scored.Where(r => !r.On).ToList();
        Headline.Text = $"{score}% optimised";
        Subline.Text = left.Count == 0
            ? "Everything Optimiser recommends for this PC is on."
            : $"{left.Count} recommendation{(left.Count == 1 ? "" : "s")} left that can make games run faster.";
        DrawScore(score / 100.0);

        var notices = new List<string>();
        List<string> restart;
        lock (App.RestartNeeded) restart = [.. App.RestartNeeded];
        if (restart.Count > 0) notices.Add($"Restart your PC to finish changing {string.Join(" and ", restart)}.");
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
        ShowGames(); // the background switch changes what the Games page says
    }

    static Row ToRow(Optimisation o)
    {
        var on = o.IsOn();
        var alreadyInWindows = o.UsesBackup && on && !App.Backup.Owns(o.Name);
        bool changedThisSession;
        lock (App.RestartNeeded) changedThisSession = App.RestartNeeded.Contains(o.Name);
        var note = alreadyInWindows ? "Already set in Windows"
                 : o.NeedsRestart && changedThisSession ? "Restart to finish"
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
        if (busy)
        {
            Refresh(); // puts back a switch the user flicked while another change was running
            return false;
        }
        busy = true;
        StatusText.Text = doing;
        var firstChange = App.Backup.Snapshot().Count == 0;
        var ok = true;
        try
        {
            await Task.Run(action);
            StatusText.Text = done;
            if (firstChange && App.Backup.Snapshot().Count > 0 && App.Backup.RestorePointError is { } error)
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

    // Runs on a worker thread. The one place a tweak is switched, so no path forgets the restart notice.
    static void Flip(Optimisation o, bool on)
    {
        (on ? o.TurnOn! : o.TurnOff!)();
        if (o.NeedsRestart) lock (App.RestartNeeded) App.RestartNeeded.Add(o.Name);
    }

    async void OnRowSwitch(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var o = ((Row)box.DataContext).Item;
        if (box.IsChecked == true) await Change($"Turning on {o.Name}…", () => Flip(o, true), $"Turned on {o.Name}.");
        else await Change($"Turning off {o.Name}…", () => Flip(o, false),
            $"Turned off {o.Name}{(o.UsesBackup ? " and put back the original setting" : "")}.");
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
        await FlipAll(todo, true, $"Applying {todo.Count} optimisations…", $"Applied {todo.Count} optimisations.");
    }

    // One failing doesn't stop the rest.
    async Task FlipAll(List<Optimisation> todo, bool on, string doing, string done)
    {
        var failed = new List<string>();
        await Change(doing, () =>
        {
            foreach (var o in todo)
            {
                try { Flip(o, on); }
                catch (Exception ex) { failed.Add($"{o.Name} ({ex.Message})"); }
            }
        }, done);
        if (failed.Count > 0) StatusText.Text += $" Some couldn't be turned {(on ? "on" : "off")}: {string.Join(", ", failed)}.";
    }

    // What a set can hold: any switch except running in the background, which the gaming profile itself needs.
    IEnumerable<Row> SetCandidates => rows.Where(r => r.IsSwitch && r.Name != Tweaks.BackgroundName);

    async void OnSetSwitch(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var set = (SetRow)box.DataContext;
        var on = box.IsChecked == true;
        var todo = Sets.ToFlip(set, setRows, on);
        if (todo.Count == 0)
        {
            StatusText.Text = on ? $"None of {set.Name}'s optimisations are on this PC."
                : $"{set.Name} stays on: another set that's on has everything in it, or Windows already had it that way.";
            Refresh(); // puts the switch back
            return;
        }
        var kept = on ? 0 : set.Rows.Count(r => r.On && !todo.Contains(r));
        await FlipAll([.. todo.Select(r => r.Item)], on, $"Turning {(on ? "on" : "off")} {set.Name}…",
            $"Turned {(on ? "on" : "off")} {set.Name}." + (kept == 0 ? ""
                : $" {kept} stayed on because another set that's on has {(kept == 1 ? "it" : "them")}, or Windows already had {(kept == 1 ? "it" : "them")} on."));
    }

    async void OnSaveSet(object sender, RoutedEventArgs e)
    {
        var name = SetName.Text.Trim();
        var items = SetCandidates.Where(r => r.On).Select(r => r.Name).ToList();
        if (name.Length == 0)
        {
            StatusText.Text = "Type a name for the set first, like Gaming.";
            SetName.Focus();
        }
        else if (items.Count == 0) StatusText.Text = "Nothing is switched on yet. Turn on what you want in the set, then save it.";
        else if (await Change($"Saving {name}…", () => Sets.Save(name, items, rows.Select(r => r.Name)), $"Saved {name} with {items.Count} optimisation{(items.Count == 1 ? "" : "s")}."))
            SetName.Clear();
    }

    void OnSetNameKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnSaveSet(sender, e);
    }

    async void OnDeleteSet(object sender, RoutedEventArgs e)
    {
        var set = (SetRow)((FrameworkElement)sender).DataContext;
        await Change($"Deleting {set.Name}…", () => Sets.Delete(set.Name), $"Deleted {set.Name}. Its optimisations stay as they are.");
    }

    RadioButton[] NavButtons => [.. Nav.Children.OfType<RadioButton>(), SettingsNav];

    void OnNav(object sender, RoutedEventArgs e)
    {
        var page = (string)((RadioButton)sender).Tag;
        PageTitle.Text = page;
        foreach (FrameworkElement p in Pages.Children)
        {
            p.Visibility = p.Name == page ? Visibility.Visible : Visibility.Collapsed;
            if (p.Name == page && SystemParameters.ClientAreaAnimation)
                p.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }
        ShowStats();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var pages = NavButtons;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key - Key.D1 is var n && n >= 0 && n < pages.Length)
        {
            pages[n].IsChecked = true;
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    // Called every second, but only reads anything while the Stats page is on screen: polling the GPU stops a
    // laptop's RTX GPU from going to sleep.
    void ShowStats()
    {
        if (Stats.Visibility != Visibility.Visible || !IsVisible || WindowState == WindowState.Minimized) return;
        if (stats == null)
        {
            stats = new Stats();
            var gpuTiles = new[] { gpuTemp, gpuLoad, gpuClock, gpuPower, vram };
            var first = stats.Read();
            foreach (var tile in (first.GpuTemp == null ? [] : gpuTiles).Append(cpuLoad).Append(ram).Append(power))
                Tiles.Children.Add(tile);
            StatsIntro.Text = first.GpuTemp == null
                ? "Live readings, updated every second. No Nvidia graphics driver was found, so there are no GPU readings."
                : "Live readings, updated every second. Reading the RTX GPU keeps it awake, so Optimiser only reads it while this page is open.";
        }

        var r = stats.Read();
        if (r.GpuTemp is { } t)
            gpuTemp.Show($"{t:0}", "°C", t / 100, t >= 87 ? "Hot. The GPU slows itself down at around 87 °C." : null, t);
        if (r.GpuLoad is { } l) gpuLoad.Show($"{l:0}", "%", l / 100, trendValue: l);
        if (r.GpuClock is { } c) gpuClock.Show($"{c:0}", r.GpuMaxClock is { } mc ? $"of {mc:0} MHz" : "MHz", c / r.GpuMaxClock);
        if (r.GpuWatts is { } w) gpuPower.Show($"{w:0}", r.GpuWattLimit is { } wl ? $"of {wl:0} W" : "W", w / r.GpuWattLimit);
        if (r.VramUsedGb is { } vu) vram.Show($"{vu:0.0}", $"of {r.VramTotalGb:0} GB", vu / r.VramTotalGb);
        cpuLoad.Show($"{r.CpuLoad:0}", "%", r.CpuLoad / 100, trendValue: r.CpuLoad);
        ram.Show($"{r.RamUsedGb:0.0}", $"of {r.RamTotalGb:0} GB", r.RamUsedGb / r.RamTotalGb);
        if (!r.HasBattery) power.Show("Mains", "", null);
        else power.Show(r.OnBattery ? "On battery" : "Plugged in", r.BatteryPercent is { } b ? $"{b}% charged" : "",
            r.BatteryPercent / 100.0, r.OnBattery ? "The GPU is held back on battery. Plug in for full performance." : null);
    }

    void ShowGames()
    {
        var running = App.Watcher?.Running ?? [];
        var mode = App.CurrentMode;
        foreach (var b in modeButtons) b.IsChecked = (ProfileMode)b.Tag == mode;
        GameList.ItemsSource = App.GameList.Select(g => new GameRow(g, running.Contains(g.Name), Optimiser.Games.ProfileOn(g))).ToList();
        GameCount.Text = App.GameList.Count == 1 ? "1 game" : $"{App.GameList.Count} games";
        GameCount.Visibility = NoGames.Visibility = Visibility.Collapsed;
        if (App.GamesScanned) (App.GameList.Count > 0 ? GameCount : NoGames).Visibility = Visibility.Visible;

        var watching = App.GameList.Count switch { 0 => "No games found yet.", 1 => "Watching 1 game.", var n => $"Watching {n} games." };
        var (status, hint, dot) =
            App.WatcherError is { } error ? ("Can't watch for games", $"Windows wouldn't report programs starting: {error}", Paint("Warn"))
            : mode == ProfileMode.Off ? ("Gaming profile off", "It won't switch on for games. Choose Automatic to have it switch on while a game runs.", Paint("Muted"))
            : mode == ProfileMode.On ? ("Gaming profile on", "You switched it on, so it stays on until you choose Automatic or Off.", Paint("Good"))
            : running.Count > 0 ? ("Gaming profile on", $"{string.Join(", ", running)} {(running.Count == 1 ? "is" : "are")} running.", Paint("Good"))
            : !App.GamesScanned ? ("Looking for games", "Checking your Steam and Epic libraries.", Paint("Muted"))
            : AutoStart.IsOn() ? ("Waiting for a game", $"{watching} Optimiser runs in the tray, so this works with the window closed too.", Paint("Muted"))
            : ("Waiting for a game", $"{watching} This only works while Optimiser is open. Turn on \"Run in the background and start "
                                   + "with Windows\" on the Optimisations page so it keeps working after you close the window.", Paint("Warn"));
        GameStatus.Text = DashGameStatus.Text = status;
        GameHint.Text = DashGameHint.Text = hint;
        GameDot.Fill = DashGameDot.Fill = dot;
    }

    void OnShowGames(object sender, RoutedEventArgs e) => ((RadioButton)Nav.Children[2]).IsChecked = true;

    // One Automatic / On / Off picker; the dashboard and Settings each get one, kept in step by ShowGames.
    FrameworkElement ModePicker()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var group = $"Mode{modeButtons.Count}";
        foreach (var (mode, tip) in new[] { (ProfileMode.Automatic, "On while a game is running"),
                                             (ProfileMode.On, "On now, until you switch it back"),
                                             (ProfileMode.Off, "Never switches on") })
        {
            var button = new RadioButton { Content = mode.ToString(), Tag = mode, GroupName = group, ToolTip = tip, Style = (Style)FindResource("Segment") };
            System.Windows.Automation.AutomationProperties.SetName(button, $"Gaming profile: {mode}");
            button.Click += OnModeClick;
            modeButtons.Add(button);
            row.Children.Add(button);
        }
        return new Border { Style = (Style)FindResource("SegmentBox"), Child = row };
    }

    async void OnModeClick(object sender, RoutedEventArgs e)
    {
        var mode = (ProfileMode)((RadioButton)sender).Tag;
        if (mode == App.CurrentMode) return;
        await Change("Switching the gaming profile…", () => App.SetProfileMode(mode), mode switch
        {
            ProfileMode.On => "Gaming profile on. It stays on until you choose Automatic or Off.",
            ProfileMode.Off => "Gaming profile off. It won't switch on for games until you choose Automatic or On.",
            _ => "The gaming profile now switches on by itself while a game is running.",
        });
    }

    async void OnGameProfile(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var game = ((GameRow)box.DataContext).Game;
        var on = box.IsChecked == true;
        await Change(on ? $"Adding {game.Name} to the gaming profile…" : $"Leaving {game.Name} out of the gaming profile…",
            () => Optimiser.Games.SetProfile(game, on),
            on ? $"The gaming profile switches on while {game.Name} runs." : $"The gaming profile no longer switches on for {game.Name}.");
        await App.ScanGames(); // hands the watcher the new list
    }

    Brush Paint(string name) => (Brush)FindResource(name);

    async void OnScanGames(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Looking for games…";
        await App.ScanGames();
        StatusText.Text = $"Found {GameCount.Text}.";
    }

    async void OnAddGame(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder a game is installed in" };
        if (dialog.ShowDialog(this) != true) return;
        if (Optimiser.Games.Add(dialog.FolderName) is { } refused)
        {
            StatusText.Text = refused;
            return;
        }
        await App.ScanGames();
        StatusText.Text = $"Added {System.IO.Path.GetFileName(dialog.FolderName)}.";
    }

    async void OnRemoveGame(object sender, RoutedEventArgs e)
    {
        var game = ((GameRow)((FrameworkElement)sender).DataContext).Game;
        Optimiser.Games.Remove(game.Folder); // "Games" alone is the page
        await App.ScanGames();
        StatusText.Text = $"Removed {game.Name}.";
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
    // A tweak no longer in the catalogue (say, from an older version) is put back straight from the backup.
    static void Undo(string tweak)
    {
        var o = Tweaks.All(App.Backup).FirstOrDefault(o => o.Name == tweak && o.TurnOff != null);
        if (o != null) Flip(o, false);
        else App.Backup.Undo(tweak);
    }

    async void OnUndoOne(object sender, RoutedEventArgs e)
    {
        var tweak = ((BackupGroup)((FrameworkElement)sender).DataContext).Tweak;
        await Change($"Undoing {tweak}…", () => Undo(tweak), $"Put back the original settings for {tweak}.");
    }

    async void OnUndoAll(object sender, RoutedEventArgs e)
    {
        var tweaks = App.Backup.Snapshot().Select(c => c.Tweak).Distinct().Reverse().ToList();
        var failed = new List<string>();
        await Change("Undoing everything…", () =>
        {
            foreach (var t in tweaks)
            {
                try { Undo(t); }
                catch (Exception ex) { failed.Add($"{t} ({ex.Message})"); }
            }
        }, $"Put back the original settings for {tweaks.Count - failed.Count} optimisation{(tweaks.Count == 1 ? "" : "s")}. "
           + "Some only take effect after a restart.");
        if (failed.Count > 0) StatusText.Text += $" These are still changed: {string.Join(", ", failed)}.";
    }

    async void OnRestorePoint(object sender, RoutedEventArgs e)
    {
        string? error = null;
        await Change("Creating a restore point. This can take a minute…",
            () => error = RestorePoint.Create("Optimiser manual restore point"),
            "Restore point created. If one was already made today, Windows keeps that one instead.");
        if (error != null) StatusText.Text = $"Couldn't create a restore point. {error}";
    }

    void ShowUpdate(Release? update)
    {
        VersionText.Text = $"Optimiser {Updates.Current}";
        UpdateText.Text = update != null ? $"Optimiser {update.Version} is ready to install. It takes a few seconds and Optimiser reopens by itself."
                        : App.UpdateError is { } error ? $"Couldn't check for updates: {error}"
                        : App.UpdateChecked is { } at ? $"You have the latest version. Checked at {at:HH:mm}."
                        : "Not checked yet.";
        InstallButton.Content = $"Install {update?.Version}";
        InstallButton.Visibility = UpdateDot.Visibility = update != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateNotes.Text = update?.Notes;
        WhatsNew.Visibility = string.IsNullOrEmpty(update?.Notes) ? Visibility.Collapsed : Visibility.Visible;
    }

    async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        UpdateText.Text = "Checking…";
        await App.CheckForUpdates();
        CheckButton.IsEnabled = true;
    }

    async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (App.Update is not { } update || busy) return;
        busy = true; // no tweak changes mid-install
        CheckButton.IsEnabled = InstallButton.IsEnabled = false;
        var progress = new Progress<int>(p => ShowProgress(p)); // shown on the update card, not the top bar
        UpdateText.Text = $"Downloading Optimiser {update.Version}…";
        ShowProgress(0);
        try
        {
            await Task.Run(() => Updates.Install(update, progress));
            UpdateText.Text = $"Installed Optimiser {update.Version}. It opens in a moment and closes this one.";
            CheckButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            UpdateText.Text = $"Couldn't install the update: {ex.Message}";
            ShowProgress(null);
            CheckButton.IsEnabled = InstallButton.IsEnabled = true;
        }
        busy = false;
    }

    // The bar under the update card while an update downloads; null hides it.
    void ShowProgress(int? percent)
    {
        UpdateProgress.Visibility = percent == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateBar.Value = percent ?? 0;
        UpdatePercent.Text = $"{percent}%";
    }

    void OnOpenGitHub(object sender, RoutedEventArgs e) => Tweaks.OpenAsYou("https://github.com/Lowballing-coding/Optimiser/commits/main");

    void ShowState()
    {
        var maximised = WindowState == WindowState.Maximized;
        // A borderless window hangs off the screen edge when maximised; pad it back in.
        Root.Margin = maximised ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxButton.Content = maximised ? "" : "";
        MaxButton.ToolTip = maximised ? "Restore" : "Maximise";
    }

    // Opens where it was last closed, unless that's off every screen now.
    void RestorePlacement()
    {
        if (Optimiser.Settings.GetText(PlacementSetting)?.Split(',') is not { Length: 5 } parts) return;
        var n = parts[..4].Select(p => double.TryParse(p, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
        if (n.Any(v => !double.IsFinite(v)) || n[2] <= 0 || n[3] <= 0) return;
        var bounds = new Rect(n[0], n[1], n[2], n[3]);
        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                               SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screens.Contains(bounds.TopLeft + new Vector(60, 20))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        if (parts[4] == "max") WindowState = WindowState.Maximized;
        ShowState();
    }

    void SavePlacement()
    {
        var b = RestoreBounds;
        if (b.IsEmpty) return;
        try
        {
            Optimiser.Settings.SetText(PlacementSetting, string.Create(CultureInfo.InvariantCulture,
                $"{b.Left:0},{b.Top:0},{b.Width:0},{b.Height:0},{(WindowState == WindowState.Maximized ? "max" : "normal")}"));
        }
        catch { } // only a nicety
    }

    async Task Snapshot(string folder)
    {
        Directory.CreateDirectory(folder);
        async Task Save(string name)
        {
            await Task.Delay(250); // let the page fade in
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(System.IO.Path.Combine(folder, $"{name}.png"));
            png.Save(file);
        }
        if (Taskbar.GetId(new WindowInteropHelper(this).Handle) != Taskbar.AppId) Environment.Exit(3); // CI fails the run
        foreach (var nav in NavButtons)
        {
            nav.IsChecked = true;
            await Save((string)nav.Tag);
        }
        ShowUpdate(new Release(new Version(1, 0, 99), "", 0, null, "Settings page with update checks\nRemembers where you left the window"));
        await Save("Settings with an update");
        UpdateText.Text = "Downloading Optimiser 1.0.99…";
        ShowProgress(42);
        await Save("Settings installing an update");
        ShowProgress(null);
        ((RadioButton)Nav.Children[2]).IsChecked = true;
        GameList.ItemsSource = new[]
        {
            new GameRow(new Game("Cyberpunk 2077", @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077", "Steam"), true, true),
            new GameRow(new Game("Fortnite", @"C:\Program Files\Epic Games\Fortnite", "Epic Games"), false, false),
        };
        GameCount.Text = "2 games";
        GameCount.Visibility = Visibility.Visible;
        NoGames.Visibility = Visibility.Collapsed;
        (GameStatus.Text, GameHint.Text, GameDot.Fill) = ("Gaming profile on", "Cyberpunk 2077 is running.", Paint("Good"));
        await Save("Games with a list");
        ((RadioButton)Nav.Children[1]).IsChecked = true;
        var sample = SetCandidates.ToList();
        SetList.ItemsSource = new[] { new SetRow("Gaming", sample), new SetRow("Everyday", [.. sample.Take(2)]) };
        await Save("Optimisations with sets");
        RGB.ShowSample();
        RgbNav.IsChecked = true;
        await Save("RGB with apps found");
        Sidebar.Width = SidebarOpen; // as it looks while hovered
        await Save("Sidebar");
        App.Quit();
    }

    void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnMaximise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void OnClose(object sender, RoutedEventArgs e) => Close();
}
