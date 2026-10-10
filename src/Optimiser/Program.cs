using System.Diagnostics;

namespace Optimiser;

public static class Program
{
    const string ExtractTo = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";

    [STAThread]
    public static int Main(string[] args)
    {
        // The installed copy starts as admin at sign-in with nobody watching. .NET unpacks WPF's DLLs into the user's
        // temp folder by default, where any program could swap them, so it restarts itself unpacking into Program
        // Files instead. Nothing has loaded from the temp folder yet at this point.
        if (AutoStart.IsInstalled(Environment.ProcessPath!) && Environment.GetEnvironmentVariable(ExtractTo) != AutoStart.RuntimeFolder)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment[ExtractTo] = AutoStart.RuntimeFolder;
            Process.Start(start)?.Dispose();
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
