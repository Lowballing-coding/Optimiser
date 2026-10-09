using System.Windows;

namespace Optimiser;

public partial class App : Application
{
    // Show what went wrong instead of vanishing. The app still closes afterwards, so nothing runs on in a bad state.
    public App() => DispatcherUnhandledException += (_, e) =>
        MessageBox.Show(e.Exception.Message, "Optimiser hit a problem", MessageBoxButton.OK, MessageBoxImage.Error);
}
