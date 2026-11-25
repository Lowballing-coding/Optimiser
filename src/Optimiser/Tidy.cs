using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Optimiser;

// One part of the health score: points out of a maximum, and what it found.
public record HealthItem(string Name, int Points, int Max, string Detail);

// The Dashboard's health score: temperatures, the Nvidia driver's age, startup apps, free disk space and Windows updates.
// Each part only counts when this PC has it, so a desktop without an Nvidia GPU isn't marked down for its driver.
public static class Health
{
    public static int Score(List<HealthItem> items) => items.Sum(i => i.Max) is var max and > 0 ? 100 * items.Sum(i => i.Points) / max : 100;

    // The facts are passed in, so the scoring can be checked without this PC's numbers.
    public static List<HealthItem> Items(double? hottest, DateTime? driver, int startupApps, double freeGb, double totalGb, int? updates, bool restart, DateTime now)
    {
        var items = new List<HealthItem>();
        if (hottest is { } t)
            items.Add(new("Temperature", t < 70 ? 25 : t < 80 ? 15 : t < 90 ? 5 : 0, 25,
                t < 80 ? $"Hottest sensor at {t:0} °C." : $"Hottest sensor at {t:0} °C. The Heat page shows what's warming it."));
        if (driver is { } d)
        {
            var days = (int)(now - d).TotalDays;
            items.Add(new("Nvidia driver", days <= 90 ? 20 : days <= 180 ? 10 : 0, 20,
                days <= 90 ? $"From {d:MMMM yyyy}, recent enough." : $"From {d:MMMM yyyy}. The NVIDIA App updates it in a couple of clicks."));
        }
        items.Add(new("Startup apps", startupApps <= 5 ? 20 : startupApps <= 10 ? 10 : 0, 20,
            $"{startupApps} start with Windows." + (startupApps > 5 ? " Stop the ones you don't need at the bottom of the Optimisations page." : "")));
        if (totalGb > 0)
        {
            var free = freeGb / totalGb;
            items.Add(new("Disk space", free >= 0.2 ? 20 : free >= 0.1 ? 10 : 0, 20,
                $"{freeGb:0} GB free of {totalGb:0} GB." + (free < 0.2 ? " Games stutter when the drive is nearly full; Disk cleanup below frees some." : "")));
        }
        if (updates != null || restart)
            items.Add(new("Windows updates", restart ? 5 : updates == 0 ? 15 : 5, 15,
                restart ? "Updates are waiting for a restart." : updates == 0 ? "Up to date." : $"{updates} waiting to install in Settings > Windows Update."));
        return items;
    }

    // This PC's numbers. Slow (Windows Update's list takes a second or two), so off the UI thread.
    public static List<HealthItem> Check()
    {
        var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        var startups = StartupApps.Find().Count(a => !StartupApps.IsStopped(a));
        bool restart;
        using (var k = Reg.Open(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) restart = k != null;
        return Items(Heat.Hottest(), App.Hardware?.NvidiaDriverDate, startups, drive.AvailableFreeSpace / 1e9, drive.TotalSize / 1e9,
            PendingUpdates(), restart, DateTime.Now);
    }


    // From Windows Update's last check, without going online. Null if Windows Update won't say.
    static int? PendingUpdates()
    {
        try
        {
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
            var searcher = session.CreateUpdateSearcher();
            searcher.Online = false;
            return (int)searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'").Updates.Count;
        }
        catch { return null; }
    }
}

// A place Disk cleanup looks, and how old a file there has to be.
public record CleanupSpot(string Name, string Why, string[] Folders, int Days);

// What a scan found in one place: the files that would go, and their size.
public record CleanupFind(CleanupSpot Spot, List<string> Files, long Bytes);

// Temp files, old shader caches and leftover driver installers. Scan only lists; nothing is deleted until you've seen
// the list and pressed Delete. As this runs as admin, every file is opened first and only deleted if the file it really is
// sits inside one of the allowed folders, so a shortcut or junction can't point the delete at anything else.
public static class Cleanup
{
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Drive => Path.GetPathRoot(Environment.SystemDirectory)!;

    public static List<CleanupSpot> Spots() =>
    [
        new("Temporary files", "Left behind by installers and apps. Only files untouched for a week, so nothing in use goes.",
            [Path.Combine(Local, "Temp"), Path.Combine(Path.GetDirectoryName(Environment.SystemDirectory)!, "Temp")], 7),
        new("Old shader caches", "Graphics drivers rebuild these as you play. Only ones a month old, so your current games keep theirs.",
            [Path.Combine(Local, "NVIDIA", "DXCache"), Path.Combine(Local, "NVIDIA", "GLCache"), Path.Combine(Local, "D3DSCache"), Path.Combine(Local, "AMD", "DxCache")], 30),
        new("Leftover driver installers", "Unpacked Nvidia and AMD installers from past driver updates. The installed driver doesn't need them.",
            [Path.Combine(Drive, "NVIDIA"), Path.Combine(Drive, "AMD"),
             Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NVIDIA Corporation", "Downloader")], 14),
    ];

    public static List<CleanupFind> Scan() => [.. Spots().Select(spot =>
    {
        var files = spot.Folders.Where(Allowed).SelectMany(f => Files(f, DateTime.Now.AddDays(-spot.Days))).ToList();
        return new CleanupFind(spot, [.. files.Select(f => f.Path)], files.Sum(f => f.Bytes));
    })];

    // Every file under the folder older than the cut-off, never following a junction or symbolic link.
    public static IEnumerable<(string Path, long Bytes)> Files(string folder, DateTime before)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        if (!Directory.Exists(folder) || new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)) yield break;
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", options))
            if (f.LastWriteTime < before && f.CreationTime < before) yield return (f.FullName, f.Length);
    }

    // Deletes what a scan found. Files in use are skipped. Returns how much went.
    public static long Delete(IEnumerable<CleanupFind> finds)
    {
        long freed = 0;
        foreach (var find in finds)
        {
            var roots = find.Spot.Folders.Where(Allowed).Select(FinalPath).OfType<string>().ToList();
            foreach (var file in find.Files)
                if (DeleteInside(file, roots) is { } bytes) freed += bytes;
            foreach (var root in find.Spot.Folders.Where(Allowed)) RemoveEmpty(root);
        }
        return freed;
    }

    // Opens the file itself (not what a link points to), checks where it really is, then deletes through that same handle.
    public static long? DeleteInside(string path, List<string> roots)
    {
        using var h = CreateFile(path, DeleteAccess | ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var info) || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) return null;
        if (Final(h) is not { } real || !roots.Any(r => real.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))) return null;
        var delete = 1;
        return SetFileInformationByHandle(h, FileDispositionInfo, ref delete, sizeof(int)) ? (long)info.SizeHigh << 32 | info.SizeLow : null;
    }

    // Empty folders left after the files went, deepest first. The folder itself stays.
    static void RemoveEmpty(string root)
    {
        if (new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories("*", options).Select(d => d.FullName).OrderByDescending(d => d.Length).ToList())
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
    }

    // A folder is only cleaned if it really is in your profile, Windows' own Temp folder, or a driver folder at the top of
    // the system drive or in ProgramData. Your profile's location comes from HKLM, which only admins can change; the TEMP
    // and AppData settings live where any program you run could point them at C:\Windows.
    public static bool Allowed(string folder)
    {
        if (!Directory.Exists(folder) || FinalPath(folder) is not { } real) return false;
        var windows = Path.GetDirectoryName(Environment.SystemDirectory)!;
        string[] fixedOk = [Path.Combine(windows, "Temp"), Path.Combine(Drive, "NVIDIA"), Path.Combine(Drive, "AMD"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NVIDIA Corporation", "Downloader")];
        if (fixedOk.Any(f => real.Equals(f, StringComparison.OrdinalIgnoreCase))) return true;
        var profile = Reg.Read($@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{WindowsIdentity.GetCurrent().User}", "ProfileImagePath") as string;
        return profile != null && Environment.ExpandEnvironmentVariables(profile) is var p
            && real.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            && !real.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase);
    }

    static string? FinalPath(string folder)
    {
        using var h = CreateFile(folder, ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        return h.IsInvalid ? null : Final(h);
    }

    // The path Windows resolved the handle to, without the \\?\ prefix.
    static string? Final(SafeFileHandle h)
    {
        var buffer = new char[1024];
        var n = GetFinalPathNameByHandle(h, buffer, (uint)buffer.Length, 0);
        if (n == 0 || n >= buffer.Length) return null;
        var path = new string(buffer, 0, (int)n);
        return path.StartsWith(@"\\?\UNC\") ? null : path.StartsWith(@"\\?\") ? path[4..] : path;
    }

    public static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    const uint DeleteAccess = 0x10000, ReadAttributes = 0x80, ShareAll = 7, OpenExisting = 3, OpenReparsePoint = 0x200000, BackupSemantics = 0x2000000;
    const int FileDispositionInfo = 4;

    [StructLayout(LayoutKind.Sequential)]
    struct FileInfo
    {
        public uint Attributes;
        public uint CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh; // FILETIMEs, 4-byte aligned
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] static extern bool GetFileInformationByHandle(SafeFileHandle h, out FileInfo info);
    [DllImport("kernel32.dll")] static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref int info, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetFinalPathNameByHandle(SafeFileHandle h, char[] path, uint size, uint flags);
}

// The weekly report: the week's temperatures, FPS and what changed, from what Optimiser already keeps.
public static class Report
{
    public const string ShownSetting = "WeeklyReportShown";

    public static List<string> Lines(DateTime now, List<HeatDay> days, List<Spike> spikes, List<TrendPoint> trend, List<Change> changes, List<BenchRun> benches)
    {
        var since = now.AddDays(-7);
        var lines = new List<string>();
        var week = days.Where(d => d.Day > since).ToList();
        if (week.Count > 0)
        {
            var hot = week.MaxBy(d => d.Max)!;
            var count = spikes.Count(s => s.At > since);
            lines.Add($"Hottest: {hot.Max:0} °C on {hot.Day:dddd}." + (count == 0 ? " No heat spikes." : $" {count} heat spike{(count == 1 ? "" : "s")} logged on the Heat page."));
        }
        foreach (var game in trend.Where(t => t.Start > since).GroupBy(t => t.Game))
        {
            var average = game.Average(t => t.Average);
            var before = trend.Where(t => t.Game == game.Key && t.Start <= since && t.Start > since.AddDays(-7)).ToList();
            var vs = before.Count > 0 ? $", {average / before.Average(t => t.Average) - 1:+0%;-0%;no change} on the week before" : "";
            lines.Add($"{game.Key}: {average:0} FPS average over {game.Count()} session{(game.Count() == 1 ? "" : "s")}{vs}.");
        }
        var drivers = trend.Where(t => t.Start > since).Select(t => t.Driver).Distinct().Where(d => d != "").ToList();
        if (drivers.Count > 1) lines.Add($"New Nvidia driver: {drivers[0]}.");
        var changed = changes.Where(c => c.At > since).Select(c => c.Tweak).Distinct().ToList();
        if (changed.Count > 0) lines.Add($"Changed: {string.Join(", ", changed)}.");
        if (benches.FirstOrDefault(b => b.At > since) is { } bench) lines.Add($"Latest benchmark: {bench.Score:N0}.");
        if (lines.Count == 0) lines.Add("Nothing to report yet. Play a game or two and check back.");
        return lines;
    }

    public static List<string> ThisWeek() => Lines(DateTime.Now, Heat.Days(), Heat.Spikes(), Fps.Trend(), App.Backup.Snapshot(), Benchmark.History());

    // Once a week, a notification with the first line or two.
    public static string? Due()
    {
        if (DateTime.TryParse(Settings.GetText(ShownSetting), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var shown)
            && DateTime.Now - shown < TimeSpan.FromDays(7)) return null;
        var report = shown == default ? null : "Your weekly report: " + string.Join(" ", ThisWeek().Take(2)); // the first week starts now
        Settings.SetText(ShownSetting, DateTime.Now.ToString("o")); // after, so a report that failed is tried again
        return report;
    }
}
