using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Optimiser;

// Waits for Windows' process start/stop events (no polling) and switches the gaming profile on while any game
// from the Games list is running, then puts the power mode back when the last one closes.
public sealed class GameWatcher : IDisposable
{
    public const string PowerModeSetting = "GamingPowerMode", PrioritySetting = "GamingPriority",
        NoThrottleSetting = "GamingNoThrottle", GpuSetting = "GamingGpuPreference", SavedPowerMode = "PowerModeBeforeGaming";
    public const string GpuPreferences = @"HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences";
    static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    readonly Backup backup;
    readonly ManagementEventWatcher starts = new(new WqlEventQuery("SELECT ProcessID FROM Win32_ProcessStartTrace"));
    readonly ManagementEventWatcher stops = new(new WqlEventQuery("SELECT ProcessID FROM Win32_ProcessStopTrace"));
    readonly Dictionary<uint, Game> running = [];
    List<Game> games = [];

    public event Action? Changed;            // a game started or stopped
    public event Action<string>? Warning;     // something worth a tray notification

    public GameWatcher(Backup backup)
    {
        this.backup = backup;
        starts.EventArrived += (_, e) => OnStart((uint)e.NewEvent["ProcessID"]);
        stops.EventArrived += (_, e) => OnStop((uint)e.NewEvent["ProcessID"]);
        starts.Start();
        stops.Start();
    }

    public List<Game> Games
    {
        get { lock (running) return games; }
        set { lock (running) games = value; }
    }

    public List<string> Running
    {
        get { lock (running) return [.. running.Values.Select(g => g.Name).Distinct()]; }
    }

    void OnStart(uint pid)
    {
        if (ExePath(pid) is not { } exe) return;
        Game? game;
        bool first;
        lock (running)
        {
            game = Optimiser.Games.ForExe(exe, games);
            if (game == null || running.ContainsKey(pid)) return;
            first = running.Count == 0;
            running[pid] = game;
        }
        try
        {
            if (first) StartProfile();
            Tune(pid);
            if (Settings.Get(GpuSetting, false)) PreferDedicatedGpu(backup, exe); // ready for next launch
        }
        catch (Exception e)
        {
            Warning?.Invoke($"Couldn't fully switch to the gaming profile for {game.Name}: {e.Message}");
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
        bool last;
        lock (running)
        {
            if (!running.Remove(pid)) return;
            last = running.Count == 0;
        }
        if (last) RestorePowerMode();
        Changed?.Invoke();
    }

    void StartProfile()
    {
        if (Settings.Get(PowerModeSetting, true) && PowerGetEffectiveOverlayScheme(out var current) == 0)
        {
            // Saved, so a crash mid-game still gets it put back on next start.
            if (Settings.GetText(SavedPowerMode) == null) Settings.SetText(SavedPowerMode, current.ToString());
            PowerSetActiveOverlayScheme(BestPerformance);
        }
        if (Stats.OnBattery())
            Warning?.Invoke("You're on battery, so the RTX GPU is held back. Plug in for full performance.");
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
        var updated = Regex.Replace(current, @"GpuPreference=\d+;", "") + "GpuPreference=2;";
        backup.Set(Tweaks.GpuTweakName, GpuPreferences, exe, updated, RegistryValueKind.String);
    }

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
        RestorePowerMode();
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
