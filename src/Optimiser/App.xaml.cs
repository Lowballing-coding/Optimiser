using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Optimiser;

public partial class App : Application
{
    public static Backup Backup { get; } = new();
    public static Hardware Hardware { get; set; } = null!; // set once the scan finishes, before any tweak is shown
    public static HashSet<string> RestartNeeded { get; } = []; // tweaks changed this session that need a restart

    public static GameWatcher? Watcher { get; private set; }
    public static string? WatcherError { get; private set; }
    public static List<Game> GameList { get; private set; } = [];
    public static bool GamesScanned { get; private set; }
    public static event Action? GamesChanged; // the list was rescanned or a game started or stopped; UI thread
    public static bool Exiting { get; private set; } // closing the window should really exit, not hide to the tray

    public const string NotifySetting = "NotifyGamingProfile", BatteryWarningSetting = "WarnOnBattery";
    public static Release? Update { get; private set; }    // a newer build, if the last check found one
    public static DateTime? UpdateChecked { get; private set; }
    public static string? UpdateError { get; private set; }
    public static event Action? UpdateChanged;              // UI thread

    public static ProfileMode CurrentMode => Watcher?.Mode ?? GameWatcher.SavedMode;

    static List<string> wasRunning = [];
    static Version? toldAbout;

    static Mutex single = null!;
    static EventWaitHandle quit = null!;
    static Forms.NotifyIcon? tray;

    // Show what went wrong instead of vanishing, then close properly so the power mode goes back and the tray
    // icon goes away. Closing means nothing runs on in a bad state.
    public App() => DispatcherUnhandledException += (_, e) =>
    {
        MessageBox.Show(e.Exception.Message, "Optimiser hit a problem", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Quit();
    };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One Optimiser at a time. A new one asks the running one to close and takes over, so opening a newer
        // download replaces the older version running in the tray.
        single = new Mutex(false, @"Local\Optimiser");
        quit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\OptimiserQuit");
        if (!Own(0))
        {
            quit.Set();
            if (!Own(15000))
            {
                MessageBox.Show("Another copy of Optimiser is running and didn't close. Exit it from the tray and try again.", "Optimiser");
                Quit();
                return;
            }
        }
        quit.Reset();
        ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => Dispatcher.InvokeAsync(Quit), null, Timeout.Infinite, true);
        Updates.CleanUp();

        try { GameWatcher.RestorePowerMode(); } catch { } // left on Best performance if Optimiser was closed mid-game
        try
        {
            Watcher = new GameWatcher(Backup);
            Watcher.Changed += () => Dispatcher.InvokeAsync(() => OnGamesChanged());
            Watcher.Warning += message => Dispatcher.InvokeAsync(() => Notify(message, Forms.ToolTipIcon.Warning));
        }
        catch (Exception ex) { WatcherError = ex.Message; }
        _ = ScanGames();

        var icon = GetResourceStream(new Uri("pack://application:,,,/Assets/optimiser.ico")).Stream;
        tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(icon, Forms.SystemInformation.SmallIconSize),
            Text = "Optimiser",
            ContextMenuStrip = new Forms.ContextMenuStrip(),
            Visible = true,
        };
        tray.ContextMenuStrip.Items.Add("Open Optimiser", null, (_, _) => ShowWindow());
        var profile = new Forms.ToolStripMenuItem("Gaming profile");
        foreach (var mode in Enum.GetValues<ProfileMode>())
            profile.DropDownItems.Add(new Forms.ToolStripMenuItem(mode.ToString(), null, (_, _) => SetModeFromTray(mode)) { Tag = mode });
        tray.ContextMenuStrip.Items.Add(profile);
        tray.ContextMenuStrip.Opening += (_, _) =>
        {
            foreach (Forms.ToolStripMenuItem item in profile.DropDownItems) item.Checked = (ProfileMode)item.Tag! == CurrentMode;
        };
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Quit());
        tray.MouseClick += (_, me) => { if (me.Button == Forms.MouseButtons.Left) ShowWindow(); };
        tray.BalloonTipClicked += (_, _) => ShowWindow();

        MainWindow = new MainWindow();
        MainWindow.Closed += (_, _) => Quit();
        if (!e.Args.Contains("--tray")) MainWindow.Show();

        if (!e.Args.Contains("--snapshot"))
        {
            Rgb.Start(); // puts back the colour picked on the RGB page
            var twiceADay = new DispatcherTimer { Interval = TimeSpan.FromHours(12) };
            twiceADay.Tick += (_, _) => AutoCheck();
            twiceADay.Start();
            AutoCheck();
        }

        // Keeps the installed copy up to date when a newer download is opened.
        if (AutoStart.IsOn()) Task.Run(() => { try { AutoStart.TurnOn(Environment.ProcessPath!); } catch { } });
    }

    static bool Own(int milliseconds)
    {
        try { return single.WaitOne(milliseconds); }
        catch (AbandonedMutexException) { return true; } // the old one closed without letting go; it's ours now
    }

    public static async Task ScanGames()
    {
        try
        {
            GameList = await Task.Run(() =>
            {
                var games = Games.Find();
                if (Watcher != null)
                {
                    Watcher.Games = [.. games.Where(Games.ProfileOn)];
                    Watcher.CheckRunning();
                }
                return games;
            });
        }
        catch (Exception e) { Notify($"Couldn't look for games: {e.Message}", Forms.ToolTipIcon.Warning); }
        GamesScanned = true;
        OnGamesChanged(rescan: true);
    }

    // rescan: a game can drop out of the running list because its switch was turned off, not because it closed.
    static void OnGamesChanged(bool rescan = false)
    {
        var running = Watcher?.Running ?? [];
        // One notification per change, so the battery warning isn't replaced by the "on" one a moment later. In On
        // mode the profile is already on, so only the battery warning is worth showing.
        var automatic = CurrentMode == ProfileMode.Automatic;
        if (running.Count > 0 && wasRunning.Count == 0)
        {
            var on = automatic ? $"Gaming profile on for {string.Join(", ", running)}. " : "";
            if (Settings.Get(BatteryWarningSetting, true) && Stats.OnBattery())
                Notify($"{on}You're on battery, so the RTX GPU is held back. Plug in for full performance.", Forms.ToolTipIcon.Warning);
            else if (automatic && Settings.Get(NotifySetting, true)) Notify(on.TrimEnd());
        }
        else if (running.Count == 0 && wasRunning.Count > 0 && automatic && Settings.Get(NotifySetting, true))
            Notify(rescan ? "Gaming profile off." : $"Gaming profile off now that {string.Join(", ", wasRunning)} closed.");
        wasRunning = running;

        var text = running.Count > 0 ? $"Optimiser: gaming profile on for {string.Join(", ", running)}"
                 : Watcher?.ProfileOn == true ? "Optimiser: gaming profile on" : "Optimiser";
        if (tray != null) tray.Text = text.Length > 127 ? text[..124] + "..." : text; // Windows' limit
        GamesChanged?.Invoke();
    }

    // Watcher first, so a WMI failure doesn't save a mode that isn't working. Runs off the UI thread: turning
    // watching back on checks every running program.
    public static void SetProfileMode(ProfileMode mode)
    {
        if (Watcher == null && mode != ProfileMode.Off) throw new InvalidOperationException($"Windows wouldn't report programs starting: {WatcherError}");
        if (Watcher != null) Watcher.Mode = mode;
        Settings.SetText(GameWatcher.ModeSetting, mode.ToString());
    }

    static void SetModeFromTray(ProfileMode mode) => Task.Run(() =>
    {
        try { SetProfileMode(mode); }
        catch (Exception e) { Current.Dispatcher.InvokeAsync(() => Notify($"Couldn't change the gaming profile: {e.Message}", Forms.ToolTipIcon.Warning)); }
        Current.Dispatcher.InvokeAsync(() => OnGamesChanged()); // also when there's no watcher to say so
    });

    public static async Task CheckForUpdates()
    {
        try
        {
            Update = await Updates.Check();
            UpdateChecked = DateTime.Now;
            UpdateError = null;
        }
        catch (Exception e) { UpdateError = e.Message; }
        UpdateChanged?.Invoke();
    }

    static async void AutoCheck()
    {
        if (!Settings.Get(Updates.AutoCheckSetting, true)) return;
        await CheckForUpdates();
        if (Update is not { } update || update.Version == toldAbout) return;
        toldAbout = update.Version;
        Notify($"Optimiser {update.Version} is ready to install. Open Settings to update.");
    }

    public static void Notify(string message, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info) =>
        tray?.ShowBalloonTip(8000, "Optimiser", message, icon);

    static void ShowWindow()
    {
        var window = Current.MainWindow;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public static void Quit()
    {
        Exiting = true;
        Current.Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Exiting = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        try { Watcher?.Dispose(); } catch { } // puts the power mode back
        base.OnExit(e);
    }
}
