using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Optimiser;

// Automatic: on while a game is running. On: on until you switch it back. Off: never.
public enum ProfileMode { Automatic, On, Off }

// Waits for Windows' process start/stop events (no polling) and switches the gaming profile on while any game
// from the Games list with its own switch on is running, then puts the power mode back when the last one closes.
// It sees every game on the list in every mode, so the FPS recorder can follow games with the profile off too.
public sealed class GameWatcher : IDisposable
{
    public const string PowerModeSetting = "GamingPowerMode", PrioritySetting = "GamingPriority",
        NoThrottleSetting = "GamingNoThrottle", GpuSetting = "GamingGpuPreference", SavedPowerMode = "PowerModeBeforeGaming",
        ModeSetting = "GamingProfileMode", OldEnabledSetting = "GamingProfile";
    public const string GpuPreferences = @"HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences";
    static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    readonly Backup backup;
    readonly ManagementEventWatcher starts = new(new EventQuery("SELECT ProcessID FROM Win32_ProcessStartTrace"));
    readonly ManagementEventWatcher stops = new(new EventQuery("SELECT ProcessID FROM Win32_ProcessStopTrace"));
    readonly Dictionary<uint, Game> running = [];  // games getting the gaming profile; empty in Off mode
    readonly Dictionary<uint, Game> playing = [];  // every game on the list that's running, under the running lock
    readonly object switching = new();
    List<Game> games = [];
    volatile ProfileMode mode;
    bool profileOn; // under the running lock

    public event Action? Changed;            // a game started or stopped
    public event Action<string>? Warning;     // something went wrong, worth a tray notification

    public GameWatcher(Backup backup)
    {
        this.backup = backup;
        starts.EventArrived += (_, e) =>
        {
            var pid = (uint)e.NewEvent["ProcessID"];
            try { if (ExePath(pid) is { } exe) Training.OnGameStart(pid, exe); }
            catch { } // only a convenience: the game still opens without the trainer
            OnStart(pid);
        };
        stops.EventArrived += (_, e) =>
        {
            var pid = (uint)e.NewEvent["ProcessID"];
            try { Training.OnGameStop(pid); }
            catch { } // only a convenience, like the start
            OnStop(pid);
        };
        mode = SavedMode;
        starts.Start();
        stops.Start();
        lock (running) Sync();
    }

    // The mode chosen last time. Versions before modes had an on/off switch, which maps to Automatic or Off.
    public static ProfileMode SavedMode =>
        Enum.TryParse<ProfileMode>(Settings.GetText(ModeSetting), out var saved) ? saved
        : Settings.Get(OldEnabledSetting, true) ? ProfileMode.Automatic : ProfileMode.Off;

    // Games are still watched in On mode, so one that starts gets high priority and the other per-game changes.
    public ProfileMode Mode
    {
        get => mode;
        set
        {
            lock (switching) // the window and the tray menu can both change it
            {
                if (value == mode) return;
                var wasOff = mode == ProfileMode.Off;
                lock (running)
                {
                    mode = value;
                    if (value == ProfileMode.Off) running.Clear();
                    Sync();
                }
                if (wasOff) CheckRunning(); // games already running get the profile now
            }
            Changed?.Invoke();
        }
    }

    public bool ProfileOn
    {
        get { lock (running) return profileOn; }
    }

    // Every game found. One taken off the list, or whose own gaming profile switch was turned off, stops getting the
    // profile straight away; one whose switch was turned on gets it at the next CheckRunning.
    public List<Game> Games
    {
        get { lock (running) return games; }
        set
        {
            lock (running)
            {
                games = value;
                foreach (var pid in playing.Where(p => !value.Contains(p.Value)).Select(p => p.Key).ToList()) playing.Remove(pid);
                foreach (var pid in running.Where(r => !playing.ContainsKey(r.Key) || !Optimiser.Games.ProfileOn(r.Value)).Select(r => r.Key).ToList())
                    running.Remove(pid);
                Sync();
            }
        }
    }

    // The games getting the gaming profile.
    public List<string> Running
    {
        get { lock (running) return [.. running.Values.Select(g => g.Name).Distinct()]; }
    }

    // Every game on the list that's running, by process id, profile or not: what the FPS recorder follows.
    public Dictionary<uint, Game> Playing
    {
        get { lock (running) return new(playing); }
    }

    // Runs on a WMI thread. The profile switches under the same lock as the count, so a game starting just as
    // another closes can't leave the power mode stuck on Best performance.
    void OnStart(uint pid)
    {
        if (ExePath(pid) is not { } exe) return;
        Game? game = null;
        try
        {
            bool profile;
            lock (running)
            {
                game = Optimiser.Games.ForExe(exe, games);
                if (game == null) return;
                var isNew = playing.TryAdd(pid, game);
                profile = mode != ProfileMode.Off && Optimiser.Games.ProfileOn(game) && running.TryAdd(pid, game);
                if (!isNew && !profile) return; // already known
                if (profile) Sync();
            }
            if (profile)
            {
                Tune(pid);
                if (Settings.Get(GpuSetting, false)) PreferDedicatedGpu(backup, exe); // ready for next launch
            }
        }
        catch (Exception e)
        {
            Warning?.Invoke($"Couldn't fully switch to the gaming profile for {game?.Name}: {e.Message}");
        }
        Changed?.Invoke();
    }

    // Picks up games that were already running when Optimiser started or rescanned.
    public void CheckRunning()
    {
        foreach (var p in Process.GetProcesses())
            using (p) OnStart((uint)p.Id);
    }

    void OnStop(uint pid)
    {
        try
        {
            lock (running)
            {
                if (!playing.Remove(pid)) return;
                if (running.Remove(pid)) Sync();
            }
        }
        catch (Exception e)
        {
            Warning?.Invoke($"Couldn't put your power mode back: {e.Message}");
        }
        Changed?.Invoke();
    }

    // Switches the power mode to match the profile, under the running lock: on in On mode, or in Automatic while a
    // game runs. The same lock as the count, so a game starting as another closes can't leave it stuck on.
    void Sync()
    {
        var on = mode == ProfileMode.On || (mode == ProfileMode.Automatic && running.Count > 0);
        if (on == profileOn) return;
        profileOn = on;
        if (on) StartProfile();
        else RestorePowerMode();
    }

    void StartProfile()
    {
        if (Settings.Get(PowerModeSetting, true) && PowerGetEffectiveOverlayScheme(out var current) == 0)
        {
            // Saved, so a crash mid-game still gets it put back on next start.
            if (Settings.GetText(SavedPowerMode) == null) Settings.SetText(SavedPowerMode, current.ToString());
            PowerSetActiveOverlayScheme(BestPerformance);
        }
    }

    // Puts back the power mode from before the last game started, if one was saved.
    public static void RestorePowerMode()
    {
        if (Settings.GetText(SavedPowerMode) is not { } saved) return;
        if (Guid.TryParse(saved, out var mode)) PowerSetActiveOverlayScheme(mode);
        Settings.SetText(SavedPowerMode, null);
    }

    // High priority and no power throttling (which on Intel hybrid CPUs moves work to the efficiency cores).
    // Anti-cheat protected games refuse the handle; that's fine, they just don't get these two.
    static void Tune(uint pid)
    {
        var process = OpenProcess(ProcessSetInformation | ProcessQueryLimited, false, pid);
        if (process == IntPtr.Zero) return;
        try
        {
            if (Settings.Get(PrioritySetting, true)) SetPriorityClass(process, HighPriorityClass);
            if (Settings.Get(NoThrottleSetting, true))
            {
                var state = new PowerThrottlingState { Version = 1, ControlMask = 1 /* execution speed */, StateMask = 0 /* off */ };
                SetProcessInformation(process, 4 /* ProcessPowerThrottling */, ref state, Marshal.SizeOf<PowerThrottlingState>());
            }
        }
        finally { CloseHandle(process); }
    }

    // Windows' per-app graphics setting ("High performance" in Settings > Display > Graphics), kept alongside any
    // other options Windows stored in the same value.
    public static void PreferDedicatedGpu(Backup backup, string exe)
    {
        var current = Reg.Read(GpuPreferences, exe) as string ?? "";
        if (current.Contains("GpuPreference=2;")) return;
        backup.Set(Tweaks.GpuTweakName, GpuPreferences, exe, Tweaks.WithFlag(current, "GpuPreference", 2), RegistryValueKind.String);
    }

    // Puts Windows' own graphics setting back for programs that are no longer on the games list, such as one you
    // removed. One whose .exe is missing, for example on a drive that isn't plugged in, keeps it for when it's back.
    // ponytail: a scan that misses a Steam library undoes its games too; each gets it back the next time it's played.
    public static void ForgetDedicatedGpu(Backup backup, IEnumerable<Game> games) =>
        backup.Undo(Tweaks.GpuTweakName, c => File.Exists(c.Name) && Optimiser.Games.ForExe(c.Name, games) == null);

    static string? ExePath(uint pid)
    {
        var process = OpenProcess(ProcessQueryLimited, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    public void Dispose()
    {
        starts.Stop();
        stops.Stop();
        starts.Dispose();
        stops.Dispose();
        lock (running) RestorePowerMode();
    }

    const uint ProcessSetInformation = 0x0200, ProcessQueryLimited = 0x1000, HighPriorityClass = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool SetPriorityClass(IntPtr process, uint priorityClass);
    [DllImport("kernel32.dll")]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    // The Settings > System > Power > Power mode slider. Undocumented exports, but stable since Windows 10 1709.
    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid mode);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid mode);
}
