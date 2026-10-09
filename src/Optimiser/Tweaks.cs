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
