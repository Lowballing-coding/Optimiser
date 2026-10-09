using System.Windows;

namespace Optimiser;

public partial class App : Application
{
    public static Backup Backup { get; } = new();
    public static Hardware Hardware { get; set; } = null!; // set once the scan finishes, before any tweak is shown
    public static HashSet<string> RestartNeeded { get; } = []; // tweaks changed this session that need a restart

    // Show what went wrong instead of vanishing. The app still closes afterwards, so nothing runs on in a bad state.
    public App() => DispatcherUnhandledException += (_, e) =>
        MessageBox.Show(e.Exception.Message, "Optimiser hit a problem", MessageBoxButton.OK, MessageBoxImage.Error);
}
