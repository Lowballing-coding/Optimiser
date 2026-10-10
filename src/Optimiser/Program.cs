using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Optimiser;

public static class Program
{
    const string ExtractTo = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";

    [STAThread]
    public static int Main(string[] args)
    {
        // The installed copy starts as admin at sign-in with nobody watching. .NET unpacks WPF's five native DLLs into
        // the user's temp folder by default, where any program could swap them, so it restarts itself unpacking into
        // Program Files instead. The runtime itself is built into the exe, and WPF hasn't loaded yet at this point.
        if (AutoStart.IsInstalled(Environment.ProcessPath!) && Environment.GetEnvironmentVariable(ExtractTo) != AutoStart.RuntimeFolder)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment[ExtractTo] = AutoStart.RuntimeFolder;
            Process.Start(start)?.Dispose();
            return 0;
        }

        return Run();
    }

    // Kept out of Main so compiling Main doesn't touch WPF before the check above.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
