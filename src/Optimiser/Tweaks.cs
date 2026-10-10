using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace Optimiser;

// One line on the Optimisations page: either a switch Optimiser can flip, or a check it can only
// detect (TurnOn == null) with a button that helps you do it yourself.
public class Optimisation
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Group { get; init; }
    public Func<Hardware, bool> AppliesTo { get; init; } = _ => true;
    public required Func<bool> IsOn { get; init; }
    public Action? TurnOn { get; init; }
    public Action? TurnOff { get; init; }
    public bool UsesBackup { get; init; }      // registry tweak: "off" puts back what was there before
    public bool NeedsRestart { get; init; }
    public bool CountsToScore { get; init; } = true;
    public string? ButtonText { get; init; }
    public Action? Button { get; init; }
}

public static class Tweaks
{
    public const string BiggestWins = "Biggest wins on this laptop";
    public const string WhileGaming = "While a game is running";
    public const string WindowsSettings = "Windows settings";
    public const string GpuTweakName = "Run games on the dedicated GPU";

    public static List<Optimisation> All(Backup backup) =>
    [
        new()
        {
            Name = "Discrete Graphics Mode",
            Group = BiggestWins,
            Description = "Connects the laptop screen straight to the RTX GPU instead of passing every frame through the "
                        + "Intel graphics, which is often 5 to 15% more FPS on the built-in screen. Switch it under GPU Switch "
                        + "in MSI Center; it needs a restart. Battery life is shorter in this mode.",
            AppliesTo = hw => hw.IsMsi && hw.IsLaptop && hw.HasNvidia,
            IsOn = () => !App.Hardware.HasIntegratedGpu, // in this mode Windows no longer sees the Intel GPU
            ButtonText = "Open MSI Center",
            Button = OpenMsiCenter,
        },
        new()
        {
            Name = "Extreme Performance in MSI Center",
            Group = BiggestWins,
            Description = "MSI's Extreme Performance scenario raises the CPU and GPU power limits and fan speeds. Optimiser "
                        + "can't read this MSI setting, so it isn't counted in your score.",
            AppliesTo = hw => hw.IsMsi,
            IsOn = () => false,
            CountsToScore = false,
            ButtonText = "Open MSI Center",
            Button = OpenMsiCenter,
        },
        Background(WhileGaming),
        SettingSwitch("Best performance power mode", WhileGaming,
            "Switches Windows' power mode to Best performance while a game runs, then puts your usual mode back when it "
            + "closes. Higher clocks while you play, normal battery life the rest of the time.",
            GameWatcher.PowerModeSetting),
        SettingSwitch("Never power-throttle games", WhileGaming,
            "Windows can treat a game as background work, for example while you alt-tab, and slow it down. On Intel 12th "
            + "gen and newer it also moves it onto the slower efficiency cores. This tells Windows never to do that to games.",
            GameWatcher.NoThrottleSetting),
        SettingSwitch("High priority for games", WhileGaming,
            "Gives running games High CPU priority, so browsers, launchers and updaters wait their turn instead of taking "
            + "time from the game.",
            GameWatcher.PrioritySetting),
        new()
        {
            Name = GpuTweakName,
            Group = WhileGaming,
            Description = "Sets every game to High performance in Windows' graphics settings, so none of them can end up on the "
                        + "built-in graphics by mistake. Games you install later get it the first time you play them.",
            AppliesTo = hw => hw.HasDedicatedGpu && hw.HasIntegratedGpu,
            IsOn = () => Settings.Get(GameWatcher.GpuSetting, false),
            TurnOn = () =>
            {
                Settings.Set(GameWatcher.GpuSetting, true);
                foreach (var exe in Games.Find().SelectMany(Games.Exes))
                    GameWatcher.PreferDedicatedGpu(backup, exe);
            },
            TurnOff = () =>
            {
                Settings.Set(GameWatcher.GpuSetting, false);
                backup.Undo(GpuTweakName);
            },
        },
        RegistryTweak(backup, "Game Mode", WindowsSettings,
            "Windows' own gaming mode. It stops Windows Update installing drivers or showing restart prompts mid-game and "
            + "gives the game more of the CPU.",
            [(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1),
             (@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AllowAutoGameMode", 1)]),
        RegistryTweak(backup, "Turn off background game recording", WindowsSettings,
            "Stops the Xbox Game Bar from constantly recording the last few minutes of gameplay in the background, which "
            + "costs GPU time and disk writes. You can still take screenshots and clips by hand.",
            [(@"HKEY_CURRENT_USER\System\GameConfigStore", "GameDVR_Enabled", 0),
             (@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0)]),
        RegistryTweak(backup, "Hardware-accelerated GPU scheduling", WindowsSettings,
            "Lets the graphics card manage its own memory and work queue instead of Windows doing it, which lowers "
            + "latency and can smooth out frame times. Needs a restart.",
            [(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2)],
            appliesTo: hw => hw.HasDedicatedGpu, needsRestart: true),
        RegistryTweak(backup, "Turn off Windows telemetry", WindowsSettings,
            "Stops the Connected User Experiences and Telemetry service, which collects and uploads usage data in the "
            + "background. A small saving in CPU and disk use.",
            [(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\DiagTrack", "Start", 4)],
            after: on => Run("sc.exe", on ? "stop DiagTrack" : "start DiagTrack")),
    ];

    // Shown on both the Optimisations and Settings pages.
    static Optimisation Background(string group) => new()
    {
        Name = "Run in the background and start with Windows",
        Group = group,
        Description = "The gaming profile only works while Optimiser is running. This starts it in the system tray when you "
                    + "sign in, without a UAC prompt, and closing the window leaves it running there.",
        IsOn = AutoStart.IsOn,
        TurnOn = () => AutoStart.TurnOn(Environment.ProcessPath!),
        TurnOff = AutoStart.TurnOff,
    };

    public const string GamingProfile = "Gaming profile", UpdatesGroup = "Updates";

    // The Settings page: how Optimiser itself behaves. Not part of the score.
    public static List<Optimisation> AppSettings() =>
    [
        new()
        {
            Name = "Switch to the gaming profile automatically",
            Group = GamingProfile,
            Description = "Turn this off to pause the gaming profile without closing Optimiser. Your games and the switches on "
                        + "the Optimisations page stay as they are for when you turn it back on.",
            IsOn = () => App.Watcher?.Enabled ?? Settings.Get(GameWatcher.EnabledSetting, true),
            TurnOn = () => SetGamingProfile(true),
            TurnOff = () => SetGamingProfile(false),
        },
        Background(GamingProfile),
        SettingSwitch("Tell me when the gaming profile switches on and off", GamingProfile,
            "Shows a notification when a game starts and the gaming profile turns on, and again when it turns off.",
            App.NotifySetting),
        SettingSwitch("Warn me when a game starts on battery", GamingProfile,
            "On battery the RTX GPU is held back to a fraction of its power, so this reminds you to plug in.",
            App.BatteryWarningSetting),
        SettingSwitch("Check for updates automatically", UpdatesGroup,
            "Looks for a newer Optimiser on GitHub when it starts and twice a day, and lets you know. Nothing installs "
            + "until you choose to.",
            Updates.AutoCheckSetting),
    ];

    static void SetGamingProfile(bool on)
    {
        Settings.Set(GameWatcher.EnabledSetting, on);
        if (App.Watcher != null) App.Watcher.Enabled = on;
    }

    // A switch made of registry values. On = every value already matches; off = undo from the backup.
    public static Optimisation RegistryTweak(Backup backup, string name, string group, string description,
        (string Key, string Name, object Value)[] values,
        Func<Hardware, bool>? appliesTo = null, bool needsRestart = false, Action<bool>? after = null) => new()
    {
        Name = name,
        Group = group,
        Description = description,
        AppliesTo = appliesTo ?? (_ => true),
        NeedsRestart = needsRestart,
        UsesBackup = true,
        IsOn = () => values.All(v => Equals(Reg.Read(v.Key, v.Name), v.Value)),
        TurnOn = () =>
        {
            foreach (var v in values)
                backup.Set(name, v.Key, v.Name, v.Value, v.Value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
            after?.Invoke(true);
        },
        TurnOff = () =>
        {
            backup.Undo(name);
            after?.Invoke(false);
        },
    };

    // Optimiser's own behaviour switches (stored in Settings), on unless turned off.
    public static Optimisation SettingSwitch(string name, string group, string description, string setting,
        Func<Hardware, bool>? appliesTo = null) => new()
    {
        Name = name,
        Group = group,
        Description = description,
        AppliesTo = appliesTo ?? (_ => true),
        IsOn = () => Settings.Get(setting, true),
        TurnOn = () => Settings.Set(setting, true),
        TurnOff = () => Settings.Set(setting, false),
    };

    public static void Run(string exe, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, arguments) { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(15000);
    }

    // MSI Center is a Store app, so it's launched by the app ID Windows lists for it. If it isn't installed,
    // the Store opens on it instead.
    public static void OpenMsiCenter()
    {
        const string script = "$a = Get-StartApps | Where-Object Name -like 'MSI Center*' | Select-Object -First 1; "
                            + "if ($a) { Start-Process ('shell:AppsFolder\\' + $a.AppID) } else { exit 1 }";
        using var p = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}")
            { CreateNoWindow = true, UseShellExecute = false })!;
        if (!p.WaitForExit(15000)) return; // still starting; it'll open MSI Center when it gets there
        if (p.ExitCode != 0)
            Process.Start(new ProcessStartInfo("ms-windows-store://search/?query=MSI%20Center") { UseShellExecute = true });
    }
}
