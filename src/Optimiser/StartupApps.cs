using System.IO;
using Microsoft.Win32;

namespace Optimiser;

// One app that starts when you sign in. Approved is where Windows (and Task Manager's Startup apps) keeps its
// enabled/disabled flag, so stopping it is the same reversible switch Task Manager uses.
public record StartupApp(string Name, string Command, string ApprovedKey, string ApprovedName);

public static class StartupApps
{
    const string Approved = @"\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupApp> Find()
    {
        var apps = new List<StartupApp>();
        void FromRun(string hive, string runKey, string approved)
        {
            using var k = Reg.Open(hive + runKey);
            foreach (var name in k?.GetValueNames() ?? [])
                if (k!.GetValue(name) is string command && !IsWindows(command))
                    apps.Add(new(name, command, hive + Approved + approved, name));
        }
        void FromFolder(Environment.SpecialFolder folder, string hive)
        {
            var dir = Environment.GetFolderPath(folder);
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                apps.Add(new(Path.GetFileNameWithoutExtension(file), file, hive + Approved + "StartupFolder", Path.GetFileName(file)));
        }
        FromRun("HKEY_CURRENT_USER", @"\Software\Microsoft\Windows\CurrentVersion\Run", "Run");
        FromRun("HKEY_LOCAL_MACHINE", @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run");
        FromRun("HKEY_LOCAL_MACHINE", @"\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32");
        FromFolder(Environment.SpecialFolder.Startup, "HKEY_CURRENT_USER");
        FromFolder(Environment.SpecialFolder.CommonStartup, "HKEY_LOCAL_MACHINE");
        return [.. apps.DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)]; // the name is the undo record's key
    }

    // Windows' own parts (like the Windows Security icon) aren't offered.
    static bool IsWindows(string command) =>
        Environment.ExpandEnvironmentVariables(command).TrimStart('"')
            .StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);

    // The first byte's low bit means disabled; no value at all means enabled.
    public static bool IsStopped(StartupApp app) => Reg.Read(app.ApprovedKey, app.ApprovedName) is byte[] { Length: > 0 } b && (b[0] & 1) == 1;

    // What Task Manager writes: 3, then when it was disabled.
    public static byte[] StoppedFlag() => [3, 0, 0, 0, .. BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc())];

    public static Optimisation Tweak(Backup backup, StartupApp app)
    {
        var name = $"Don't start {app.Name} with Windows";
        return new()
        {
            Name = name,
            Group = Tweaks.StartupGroup,
            Description = $"Starts every time you sign in and keeps running in the background: {app.Command}",
            IsOn = () => IsStopped(app),
            TurnOn = () => backup.Set(name, app.ApprovedKey, app.ApprovedName, StoppedFlag(), RegistryValueKind.Binary),
            TurnOff = () => backup.Undo(name),
            UsesBackup = true,
            CountsToScore = false, // which apps you need is up to you
        };
    }
}
