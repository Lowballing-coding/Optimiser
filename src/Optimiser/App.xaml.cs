using System.Windows;
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

    static Mutex single = null!;
    static EventWaitHandle quit = null!;
    static Forms.NotifyIcon? tray;

    // Show what went wrong instead of vanishing. The app still closes afterwards, so nothing runs on in a bad state.
    public App() => DispatcherUnhandledException += (_, e) =>
        MessageBox.Show(e.Exception.Message, "Optimiser hit a problem", MessageBoxButton.OK, MessageBoxImage.Error);

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

        try { GameWatcher.RestorePowerMode(); } catch { } // left on Best performance if Optimiser was closed mid-game
        try
        {
            Watcher = new GameWatcher(Backup);
            Watcher.Changed += () => Dispatcher.InvokeAsync(OnGamesChanged);
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
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Quit());
        tray.MouseClick += (_, me) => { if (me.Button == Forms.MouseButtons.Left) ShowWindow(); };

        MainWindow = new MainWindow();
        MainWindow.Closed += (_, _) => Quit();
        if (!e.Args.Contains("--tray")) MainWindow.Show();

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
        GameList = await Task.Run(() =>
        {
            var games = Games.Find();
            if (Watcher != null)
            {
                Watcher.Games = games;
                Watcher.CheckRunning();
            }
            return games;
        });
        GamesScanned = true;
        OnGamesChanged();
    }

    static void OnGamesChanged()
    {
        var running = Watcher?.Running ?? [];
        var text = running.Count == 0 ? "Optimiser" : $"Optimiser: gaming profile on for {string.Join(", ", running)}";
        if (tray != null) tray.Text = text.Length > 127 ? text[..124] + "..." : text; // Windows' limit
        GamesChanged?.Invoke();
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
