using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Optimiser;

public static class Program
{
    const string ExtractTo = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";
    public const string ShowEvent = @"Local\OptimiserShow"; // see App.OnStartup

    [STAThread]
    public static int Main(string[] args)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return StartAsAdmin(args);

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

    // Opened without admin rights, as from a taskbar pin. When the installed copy is at least this new, no UAC prompt
    // is needed: the Optimiser already running shows its window, or the background-mode sign-in task starts one as
    // admin. A newer download, or no background mode, asks Windows as before.
    static int StartAsAdmin(string[] args)
    {
        var exe = Environment.ProcessPath!;
        if (!args.Contains("--tray") && AutoStart.InstalledIsAtLeast(exe))
        {
            if (ShowRunning()) return 0;
            try
            {
                if (AutoStart.Run())
                    for (var waited = 0; waited < 20000; waited += 250)
                    {
                        Thread.Sleep(250);
                        if (ShowRunning()) return 0;
                    }
            }
            catch (Exception) { } // the task couldn't be run; ask instead
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        try { Process.Start(start)?.Dispose(); }
        catch (Win32Exception) { } // you said no
        return 0;
    }

    // Asks the Optimiser that's running to show its window, if one is running.
    static bool ShowRunning()
    {
        if (!EventWaitHandle.TryOpenExisting(ShowEvent, out var show)) return false;
        using (show)
        {
            AllowSetForegroundWindow(-1); // so its window comes to the front, as if you'd opened it directly
            show.Set();
        }
        return true;
    }

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    // Kept out of Main so compiling Main doesn't touch WPF before the check above.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
