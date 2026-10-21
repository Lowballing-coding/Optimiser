// Self-checks for the parts that touch the system. Run on Windows as admin: dotnet run --project tests/Optimiser.Tests
// Uses a throwaway key under HKEY_CURRENT_USER and a throwaway folder, so it changes nothing real.
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Optimiser;
using Optimiser.Tests;

const string sub = @"Software\OptimiserSelfTest";
const string key = @"HKEY_CURRENT_USER\" + sub;
const string state = key + @"\State";

Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
using (var k = Registry.CurrentUser.CreateSubKey(sub))
{
    k.SetValue("Existing", 5, RegistryValueKind.DWord);
    k.SetValue("Path", @"%TEMP%\x", RegistryValueKind.ExpandString);
    k.SetValue("Lines", new[] { "a", "b" }, RegistryValueKind.MultiString);
}

var backup = new Backup(state, makeRestorePoint: false);
backup.Set("Tweak A", key, "Existing", 1, RegistryValueKind.DWord);
backup.Set("Tweak A", key, "Existing", 2, RegistryValueKind.DWord); // must not replace the saved original
backup.Set("Tweak A", key, "New", "hello", RegistryValueKind.String);
backup.Set("Tweak B", key, "Path", @"C:\y", RegistryValueKind.String); // different type than before
backup.Set("Tweak B", key, "Lines", new[] { "c" }, RegistryValueKind.MultiString);
Check(Read("Existing") is 2, "tweak value written");
Check(Throws(() => backup.Set("Tweak C", key.ToUpperInvariant(), "existing", 3, RegistryValueKind.DWord)),
      "a second tweak can't take over a setting another tweak owns");

backup = new Backup(state, makeRestorePoint: false); // as if the app restarted
Check(backup.Changes.Count == 4, "backup survives a restart");

backup.Undo("Tweak A");
Check(Read("Existing") is 5, "undo restores the original, not the first tweaked value");
Check(Read("New") is null, "undo deletes a value that didn't exist before");
Check(backup.Changes.Count == 2, "undone changes leave the list");

backup.UndoAll();
using (var k = Registry.CurrentUser.OpenSubKey(sub)!)
{
    Check(k.GetValueKind("Path") == RegistryValueKind.ExpandString, "undo restores the original value type");
    Check((string)k.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames)! == @"%TEMP%\x",
          "undo restores unexpanded text");
    Check(((string[])k.GetValue("Lines")!).SequenceEqual(["a", "b"]), "undo restores multi-line values");
}
Check(new Backup(state, makeRestorePoint: false).Changes.Count == 0, "empty after undo all");

// A registry tweak: on means every value matches, off puts back the original.
backup = new Backup(state, makeRestorePoint: false);
using (var k = Registry.CurrentUser.CreateSubKey(sub)) k.SetValue("Mode", 0, RegistryValueKind.DWord);
var tweak = Tweaks.RegistryTweak(backup, "Test tweak", "Group", "Description", [(key, "Mode", 1), (key, "Text", "on")]);
Check(!tweak.IsOn(), "tweak starts off");
tweak.TurnOn!();
Check(tweak.IsOn() && backup.Owns("Test tweak"), "tweak turns on and is recorded");
tweak.TurnOff!();
Check(!tweak.IsOn() && Read("Mode") is 0 && Read("Text") is null && !backup.Owns("Test tweak"), "tweak turns off cleanly");

// Optimiser's own settings.
Settings.Key = key + @"\Settings";
Check(Settings.Get("Missing", true) && !Settings.Get("Missing", false), "settings fall back to the default");
Settings.Set("Flag", false);
Settings.SetList("Folders", [@"D:\Games\A", @"E:\B"]);
Check(!Settings.Get("Flag", true) && Settings.GetList("Folders").Length == 2, "settings round-trip");

// GPU type flags.
Hardware Fake(params string[] gpus) => new("cpu", 6, 8, 20, gpus, 32, "Micro-Star International Co., Ltd.", "GE66", true);
Check(Fake("NVIDIA GeForce RTX 3070 Ti Laptop GPU", "Intel(R) Iris(R) Xe Graphics") is { HasIntegratedGpu: true, HasDedicatedGpu: true, IsMsi: true },
      "hybrid graphics detected");
Check(Fake("NVIDIA GeForce RTX 3070 Ti Laptop GPU") is { HasIntegratedGpu: false }, "discrete graphics mode detected");
Check(Fake("AMD Radeon(TM) Graphics", "AMD Radeon RX 6800M") is { HasIntegratedGpu: true, HasDedicatedGpu: true, HasNvidia: false },
      "AMD integrated and dedicated told apart");
Check(Fake("Intel(R) Arc(TM) A770 Graphics") is { HasDedicatedGpu: true, HasIntegratedGpu: false }, "Intel Arc card is dedicated");

// Live readings work without an Nvidia driver too (CI has none).
using (var stats = new Stats())
{
    Thread.Sleep(200);
    var reading = stats.Read();
    Console.WriteLine(reading);
    Check(reading.RamTotalGb > 0 && reading.RamUsedGb > 0 && reading.CpuLoad is >= 0 and <= 100, "live readings");
    Check(stats.HasCpuSpeed && reading.CpuPercentOfBase > 0 && reading.CpuBaseMhz > 0, "CPU speed from Windows' own counter");
}
Check(Stats.Limit(0x40) == "Heat" && Stats.Limit(0x80 | 0x4) == "Power brake" && Stats.Limit(0x4 | 0x1) == "Power limit"
      && Stats.Limit(0x1) == "Idle" && Stats.Limit(0) == "Nothing", "what's holding the GPU back, in words, worst first");
Check(Stats.PowerModeName(4) == "Best performance" && Stats.PowerModeName(5) == "Game Mode" && Stats.PowerModeName(9) == "",
      "Windows' power modes by the names Settings uses");

var hw = Hardware.Detect();
Console.WriteLine(hw);
Check(hw.Cpu != "Unknown" && hw.PCores > 0 && hw.RamGb > 0 && hw.Gpus.Length > 0, "hardware detected");

// Game libraries. Not the temp folder: on CI its path uses a short 8.3 name, which running programs never report.
var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptimiserSelfTest");
if (Directory.Exists(temp)) Directory.Delete(temp, true);
var steam = Path.Combine(temp, "Steam");
var library2 = Path.Combine(temp, "Library 2");
Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common", "Hades"));
Directory.CreateDirectory(Path.Combine(library2, "steamapps", "common", "Cyberpunk 2077"));
File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
    "libraryfolders"
    {
        "0" { "path"		"{{steam.Replace(@"\", @"\\")}}" }
        "1" { "path"		"{{library2.Replace(@"\", @"\\")}}" }
    }
    """);
Manifest(steam, 1145360, "Hades", "Hades");
Manifest(steam, 228980, "Steamworks Common Redistributables", "Steamworks Shared");
Manifest(steam, 431960, "Wallpaper Engine", "wallpaper_engine");
Manifest(library2, 1091500, "Cyberpunk 2077", "Cyberpunk 2077");
var steamGames = Games.Steam(steam.Replace('\\', '/')).ToList(); // Steam stores its own path with forward slashes
Check(steamGames.Select(g => g.Name).Order().SequenceEqual(["Cyberpunk 2077", "Hades"]) && steamGames.All(g => Directory.Exists(g.Folder)),
      "Steam games found in every library, Steam's own tools and Wallpaper Engine skipped");
Games.Remove(steamGames[0]);
Check(Games.Visible(steamGames).SequenceEqual([steamGames[1]]), "a game Steam found can be taken off the list");
Check(Games.Add(steamGames[0].Folder) == null && Games.Visible(steamGames).Count() == 2, "and comes back when its folder is added");
Games.Remove(Games.Added().Single());

var epic = Path.Combine(temp, "Epic");
Directory.CreateDirectory(epic);
File.WriteAllText(Path.Combine(epic, "a.item"), """{ "DisplayName": "Fortnite", "InstallLocation": "C:\\Games\\Fortnite", "AppCategories": ["public", "games"] }""");
File.WriteAllText(Path.Combine(epic, "b.item"), """{ "DisplayName": "Unreal Engine", "InstallLocation": "C:\\UE", "AppCategories": ["engines"] }""");
File.WriteAllText(Path.Combine(epic, "c.item"), """{ "DisplayName": "Half writ""");
File.WriteAllText(Path.Combine(epic, "d.item"), """{ "DisplayName": "Odd", "InstallLocation": "", "AppCategories": "games" }""");
Check(Games.Epic(epic).Select(g => g.Name).SequenceEqual(["Fortnite"]), "Epic games found, apps and broken or odd manifests skipped");

List<Game> library = [new("Hades", @"C:\Games\Hades", "Steam")];
Check(Games.ForExe(@"C:\Games\Hades\x64\Hades.exe", library)?.Name == "Hades", "a game's program is matched to the game");
Check(Games.ForExe(@"C:\Games\Hades II\Hades2.exe", library) == null, "a folder with a longer name isn't");
Check(Games.ForExe(@"C:\Games\Hades\UnityCrashHandler64.exe", library) == null
      && Games.ForExe(@"C:\Games\Hades\EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", library) == null,
      "crash reporters and anti-cheat aren't games");

Check(Games.Add(@"C:\") != null && Games.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) != null
      && Games.Add(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!) != null
      && Games.Add(Environment.SystemDirectory) != null,
      "drives and system folders can't be added as games");
var gameDir = Path.Combine(temp, "Fake Game");
Directory.CreateDirectory(gameDir);
Check(Games.Add(gameDir + @"\") == null && Games.Added().Single() is { Name: "Fake Game" }, "a game folder can be added");
Games.Remove(Games.Added().Single());
Check(!Games.Added().Any(), "and removed");

// The watcher spots a game starting and stopping. A copy of cmd.exe stands in for the game.
var fakeGame = Path.Combine(gameDir, "FakeGame.exe");
File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fakeGame);
backup = new Backup(state, makeRestorePoint: false);
using (var watcher = new GameWatcher(backup) { Games = [new Game("Fake Game", gameDir, Games.AddedByYou)] })
{
    var changed = new AutoResetEvent(false);
    watcher.Changed += () => changed.Set();
    using var game = Process.Start(new ProcessStartInfo(fakeGame, "/c ping -n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
    Check(changed.WaitOne(15000) && watcher.Running.SequenceEqual(["Fake Game"]), "a game starting switches on the gaming profile");
    Check(game.PriorityClass == ProcessPriorityClass.High, "the game gets high priority");
    game.Kill(entireProcessTree: true);
    game.WaitForExit();
    Check(changed.WaitOne(15000) && watcher.Running.Count == 0 && !watcher.ProfileOn && Settings.GetText(GameWatcher.SavedPowerMode) == null,
          "the profile switches off and the power mode goes back when the game closes");

    using var again = Process.Start(new ProcessStartInfo(fakeGame, "/c ping -n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
    Check(changed.WaitOne(15000) && watcher.ProfileOn, "the profile switches on for the game again");
    watcher.Games = [];
    Check(watcher.Running.Count == 0 && !watcher.ProfileOn, "taking a game off the list switches its profile off straight away");
    again.Kill(entireProcessTree: true);
    again.WaitForExit();

    var fake = new Game("Fake Game", gameDir, Games.AddedByYou);
    Games.SetProfile(fake, false);
    watcher.Games = [fake];
    using (var unprofiled = Process.Start(new ProcessStartInfo(fakeGame, "/c ping -n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!)
    {
        Check(changed.WaitOne(15000) && watcher.Playing.ContainsValue(fake) && watcher.Running.Count == 0 && !watcher.ProfileOn,
              "a game with its own switch off is still seen, for its FPS, but gets no profile");
        unprofiled.Kill(entireProcessTree: true);
        unprofiled.WaitForExit();
    }
    Check(changed.WaitOne(15000) && watcher.Playing.Count == 0, "and stops being seen when it closes");
    Games.SetProfile(fake, true);

    watcher.Mode = ProfileMode.On;
    Check(watcher.ProfileOn, "On switches the profile on with no game running");
    watcher.Mode = ProfileMode.Off;
    Check(!watcher.ProfileOn && Settings.GetText(GameWatcher.SavedPowerMode) == null, "Off switches it off and puts the power mode back");
    watcher.Games = [new Game("Fake Game", gameDir, Games.AddedByYou)];
    using var ignored = Process.Start(new ProcessStartInfo(fakeGame, "/c ping -n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
    Check(SpinWait.SpinUntil(() => watcher.Playing.Values.Any(g => g.Name == "Fake Game"), 15000), "Off still sees games, so their FPS is recorded");
    Check(watcher.Running.Count == 0 && !watcher.ProfileOn, "but ignores them");
    watcher.Mode = ProfileMode.Automatic;
    Check(watcher.Running.SequenceEqual(["Fake Game"]) && watcher.ProfileOn, "back to Automatic picks up a game that's already running");
    ignored.Kill(entireProcessTree: true);
    ignored.WaitForExit();
}

// Modes saved by older versions, and each game's own switch.
Settings.SetText(GameWatcher.ModeSetting, null);
Settings.Set(GameWatcher.OldEnabledSetting, false);
Check(GameWatcher.SavedMode == ProfileMode.Off, "an older version's paused profile becomes Off");
Settings.SetText(GameWatcher.ModeSetting, "On");
Check(GameWatcher.SavedMode == ProfileMode.On, "the chosen mode is remembered");
var hades = new Game("Hades", @"C:\Games\Hades", "Steam");
Games.SetProfile(hades, false);
Check(!Games.ProfileOn(hades) && Games.ProfileOn(hades with { Folder = @"C:\Games\Other" }), "a game's profile can be switched off on its own");
Games.SetProfile(hades, true);
Check(Games.ProfileOn(hades), "and back on");

// Optimisation sets: saved by name, updated in place, and switching one off leaves what another set that's on needs.
Sets.Save("Gaming", ["A", "B", "GPU"], []);
Sets.Save("Everyday", ["A"], []);
Sets.Save(" gaming ", ["A", "C"], ["A", "B", "C"]);
Check(Sets.Load() is [{ Name: " gaming ", Items: ["A", "C", "GPU"] }, { Name: "Everyday" }],
    "saving under the same name updates that set in place, keeping what isn't on this PC right now");
Sets.Delete("GAMING");
Check(Sets.Load() is [{ Name: "Everyday" }], "deletes a set by name");
Sets.Delete("Everyday");
Row SetItem(string name, bool on, bool canSwitch = true) =>
    new(new Optimisation { Name = name, Description = "", Group = "", IsOn = () => on, TurnOn = () => { } }, on, canSwitch, null);
var (a, b, c, d) = (SetItem("A", true), SetItem("B", true), SetItem("C", false), SetItem("D", true, canSwitch: false));
var gaming = new SetRow("Gaming", [a, b, c, d]);
var everyday = new SetRow("Everyday", [a]);
Check(!gaming.On && everyday.On, "a set is on when everything in it is");
Check(Sets.ToFlip(gaming, [gaming, everyday], on: true) is [{ Name: "C" }], "switching a set on turns on only what's off");
Check(Sets.ToFlip(gaming, [gaming, everyday], on: false) is [{ Name: "B" }],
    "switching it off leaves what another set that's on has, and what Windows already had");

// The per-game GPU setting keeps Windows' other options in the same value, and undo puts it back.
const string gpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
using (var k = Registry.CurrentUser.CreateSubKey(gpuKey)) k.SetValue(fakeGame, "SwapEffectUpgradeEnable=1;GpuPreference=1;");
GameWatcher.PreferDedicatedGpu(backup, fakeGame);
Check(Registry.GetValue(GameWatcher.GpuPreferences, fakeGame, null) is "SwapEffectUpgradeEnable=1;GpuPreference=2;", "game set to the dedicated GPU");
GameWatcher.ForgetDedicatedGpu(backup, [new Game("Fake Game", gameDir, Games.AddedByYou)]);
Check(Registry.GetValue(GameWatcher.GpuPreferences, fakeGame, null) is "SwapEffectUpgradeEnable=1;GpuPreference=2;", "and keeps it while it's on the games list");
GameWatcher.ForgetDedicatedGpu(backup, []);
Check(Registry.GetValue(GameWatcher.GpuPreferences, fakeGame, null) is "SwapEffectUpgradeEnable=1;GpuPreference=1;", "a program taken off the list gets its GPU setting back");
GameWatcher.PreferDedicatedGpu(backup, fakeGame);
backup.Undo(Tweaks.GpuTweakName);
Check(Registry.GetValue(GameWatcher.GpuPreferences, fakeGame, null) is "SwapEffectUpgradeEnable=1;GpuPreference=1;", "undo puts the GPU setting back");
using (var k = Registry.CurrentUser.OpenSubKey(gpuKey, writable: true)!) k.DeleteValue(fakeGame);

// Start with Windows: a copy of the app somewhere only admins can change, started by a logon task.
AutoStart.TaskName = "OptimiserSelfTest";
AutoStart.InstallFolder = Path.Combine(temp, "Installed");
var unversioned = Path.Combine(temp, "Unversioned.exe");
File.WriteAllText(unversioned, "an older build");
AutoStart.TurnOn(unversioned);
Check(AutoStart.IsOn() && File.Exists(AutoStart.InstalledExe), "start with Windows installs a copy and adds the task");
AutoStart.TurnOn(fakeGame);
Check(new FileInfo(AutoStart.InstalledExe).Length == new FileInfo(fakeGame).Length, "a newer copy replaces the installed one");
AutoStart.TurnOn(unversioned);
Check(new FileInfo(AutoStart.InstalledExe).Length == new FileInfo(fakeGame).Length, "an older copy doesn't roll it back");
Check(AutoStart.InstalledIsAtLeast(fakeGame) && AutoStart.InstalledIsAtLeast(unversioned), "the installed copy can stand in for one no newer");
AutoStart.TurnOff();
Check(!AutoStart.IsOn() && !Directory.Exists(AutoStart.InstallFolder), "turning it off removes both");

// GPU stress test, on Windows' software renderer since CI has no GPU: it passes on a healthy device, and a
// deliberately wrong value is caught, so a real error during an overclock can't slip through.
using (var stress = new GpuStress(software: true))
{
    var run = stress.Run(TimeSpan.FromSeconds(2));
    Console.WriteLine($"GPU test: {run.Verdict}, {run.Score:0.0} passes a second over {stress.MegaBytes} MB");
    Check(run is { Verdict: Verdict.Passed, Score: > 0 }, "the GPU test passes when nothing is wrong");
    stress.Inject = true;
    Check(stress.Run(TimeSpan.FromSeconds(10)).Verdict == Verdict.Errors, "the GPU test catches a wrong answer");
}
Check(Overclock.Parse("120,400") == (120, 400) && Overclock.Parse("bad") == null && Overclock.Parse(null) == null, "saved offsets read back");
Check(NvGpu.Open(out var noGpu) == null && noGpu != null, "no Nvidia GPU explains itself");

// Screens: reading refresh rates works, whatever the runner's screen offers.
Check(Display.Screens().All(s => s.Hz > 1 && s.Hz <= s.MaxHz), "reads each screen's refresh rate");

// Startup apps: stopping one writes Task Manager's "disabled" flag, and undo takes it away again.
const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
using (var k = Registry.CurrentUser.CreateSubKey(runKey))
{
    k.SetValue("OptimiserSelfTest", "\"C:\\SelfTest\\app.exe\" --tray");
    k.SetValue("OptimiserSelfTestWindows", @"%windir%\system32\notepad.exe", RegistryValueKind.ExpandString);
}
var apps = StartupApps.Find();
Check(apps.Any(a => a.Name == "OptimiserSelfTest") && apps.All(a => a.Name != "OptimiserSelfTestWindows"),
    "lists startup apps but not Windows' own");
var app = apps.First(a => a.Name == "OptimiserSelfTest");
var stop = StartupApps.Tweak(backup, app);
stop.TurnOn!();
Check(stop.IsOn() && Registry.GetValue(app.ApprovedKey, app.ApprovedName, null) is byte[] { Length: 12 } flag && flag[0] == 3,
    "stopping an app marks it disabled the way Task Manager does");
stop.TurnOff!();
Check(!stop.IsOn() && Registry.GetValue(app.ApprovedKey, app.ApprovedName, null) == null, "undo lets it start again");
using (var k = Registry.CurrentUser.CreateSubKey(runKey))
{
    k.DeleteValue("OptimiserSelfTest");
    k.DeleteValue("OptimiserSelfTestWindows");
}

// Updates: reads the version from the release title and only offers this repo's own download.
const string release = """
    {"name": "Optimiser 1.0.12", "body": "Merge pull request #7 from Lowballing-coding/settings-updates\n\nSettings page\r\n",
     "assets": [{"name": "Optimiser.exe", "size": 70000000, "digest": "sha256:abc123",
                 "browser_download_url": "https://github.com/Lowballing-coding/Optimiser/releases/download/latest/Optimiser.exe"}]}
    """;
var update = Updates.Parse(release, new Version(1, 0, 9));
Check(update is { Size: 70000000, Sha256: "abc123", Notes: "Settings page" } && update.Version == new Version(1, 0, 12), "finds a newer release");
Check(Updates.Parse(release, new Version(1, 0, 12)) == null, "no update when already on that version");
Check(Updates.Parse(release.Replace("Lowballing-coding/Optimiser/releases", "someone/else/releases"), new Version(1, 0, 9)) == null,
    "ignores a download from anywhere else");

// RGB: Optimiser only talks to lighting apps on this PC and only loads G HUB's DLL from Program Files.
Check(SteelSeries.Address("""{"address": "127.0.0.1:51248", "encryptedAddress": "127.0.0.1:51249"}""") == "http://127.0.0.1:51248/"
      && SteelSeries.Address("""{"address": "example.com:80"}""") == null && SteelSeries.Address("half wr") == null,
      "uses GameSense's address only if it's on this PC");
Check(Razer.Session("""{"sessionid": 3486, "uri": "http://localhost:55235/chromasdk"}""") == "http://localhost:55235/chromasdk"
      && Razer.Session("""{"sessionid": 1, "uri": "http://example.com/chromasdk"}""") == null,
      "uses Razer Chroma's session address only if it's on this PC");
Check(Razer.Bgr(System.Windows.Media.Color.FromRgb(0xFF, 0x8A, 0x1F)) == 0x1F8AFF, "Razer colours are blue, green, red");
Check(Rgb.RainbowAt(0) == System.Windows.Media.Color.FromRgb(255, 0, 0) && Rgb.RainbowAt(3333) == System.Windows.Media.Color.FromRgb(0, 255, 0)
      && Rgb.RainbowAt(5000) == System.Windows.Media.Color.FromRgb(0, 255, 255) && Rgb.RainbowAt(10000) == Rgb.RainbowAt(0)
      && Rgb.RainbowAt(1234) != Rgb.Rainbow,
      "the rainbow goes round the colour wheel every 10 seconds");
Check(Razer.Result("""{"result": 4309}""") == 4309 && Razer.Result("""{"result": 2147500037}""") == 2147500037
      && Razer.Result("""{"sessionid": 1, "uri": "http://localhost:55235/chromasdk"}""") == 0 && Razer.Result("") == 0,
      "reads Razer Chroma's answer code");
var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
Check(Logitech.InProgramFiles(Path.Combine(programFiles, @"LGHUB\sdks\sdk_legacy_led_x64.dll"))
      && !Logitech.InProgramFiles(@"C:\Users\Public\sdk_legacy_led_x64.dll")
      && !Logitech.InProgramFiles(Path.Combine(programFiles, @"..\Users\Public\x.dll"))
      && !Logitech.InProgramFiles(programFiles + @"Evil\x.dll"),
      "only loads the Logitech DLL from Program Files");
var startMenu = StartMenu.Apps();
Console.WriteLine($"{startMenu.Count} Start menu apps, e.g. {string.Join(", ", startMenu.Take(3))}");
Check(startMenu.All(a => a.Name != "" && a.AppId != ""), "reads the Start menu without PowerShell");
var rgbApps = Rgb.Match([("SteelSeries GG", "a"), ("Razer Synapse Uninstall", "b"), ("Razer Synapse", "c"), ("Notepad", "d"),
                         ("Wootility", "e\""), ("Wootility", "e")], [Rgb.LogitechGHub]);
Check(rgbApps.Select(a => a.Name).SequenceEqual(["SteelSeries GG", "Razer Synapse", "Logitech G HUB", "Wootility"])
      && rgbApps[1] is { Open: @"shell:AppsFolder\c", Lighting: Razer } && rgbApps[2] is { Open: null, Lighting: Logitech }
      && rgbApps[3] is { Open: @"shell:AppsFolder\e", Lighting: null },
      "finds RGB apps in the Start menu, and ones Optimiser can reach that aren't in it");
var redragon = Rgb.Match([("K530RGB Pro Firmware", @"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Redragon K530RGB Pro\Old\Update.exe"),
                          ("OemDrv", @"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Redragon K530RGB Pro\devtray.exe")], []);
Check(redragon is [{ Name: "OemDrv", Lighting: null, Open: @"shell:AppsFolder\{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Redragon K530RGB Pro\devtray.exe" }],
      "finds an RGB app by its program's folder too, but never its updater");

// Frame counting: present events in, frame times per game out, PresentMon's way.
{
    const long F = 10_000_000; // QPC ticks per second
    const long Frame = F / 100; // 10 ms
    const FrameCounter.Kind Dxgi = FrameCounter.Kind.Dxgi, D3d9 = FrameCounter.Kind.D3d9, K171 = FrameCounter.Kind.Kernel171, K168 = FrameCounter.Kind.Kernel168;

    var fc = new FrameCounter(F);
    for (var i = 0; i < 10; i++) { fc.RuntimeStart(1, 100, Dxgi, 0xA, 0, i * Frame); fc.RuntimeStop(1, 0); fc.RuntimeStart(1, 100, Dxgi, 0xA, 1, i * Frame + 5); fc.RuntimeStop(1, 0); }
    var ms = fc.Drain(100);
    Check(ms.Count == 9 && ms.All(m => Math.Abs(m - 10) < 1e-9), "DXGI_PRESENT_TEST calls aren't frames");

    fc = new FrameCounter(F);
    for (var i = 0; i < 10; i++)
    {
        fc.RuntimeStart(1, 100, Dxgi, 0xA, 8, i * Frame); fc.RuntimeStop(1, unchecked((int)0x887A000A)); // DXGI_ERROR_WAS_STILL_DRAWING
        fc.RuntimeStart(1, 100, Dxgi, 0xA, 0, i * Frame + 3); fc.RuntimeStop(1, 0x087A0001);              // DXGI_STATUS_OCCLUDED
    }
    Check(fc.Drain(100).Count == 9, "a failed Present isn't a frame; an occluded one is");

    fc = new FrameCounter(F);
    for (var i = 0; i < 60; i++) { fc.RuntimeStart(1, 100, Dxgi, 0xA, 0, i * Frame); fc.Kernel(100, K171, 2, i * Frame + 2); fc.Kernel(100, K171, 2, i * Frame + 4); fc.RuntimeStop(1, 0); }
    Check(fc.Drain(100).Count == 59, "DirectX's own frames win over the kernel's, even when the kernel logs more");

    fc = new FrameCounter(F);
    for (var i = 0; i < 30; i++) { fc.Kernel(100, K171, 3, i * Frame); fc.Kernel(100, K168, 0, i * Frame + 1); }
    ms = fc.Drain(100);
    Check(ms.Count == 29 && Math.Abs(1000 / ms.Average() - 100) < 1e-6, "Vulkan and OpenGL: the kernel's two kinds of present aren't added together");

    fc = new FrameCounter(F);
    for (var i = 0; i < 30; i++) { fc.Kernel(100, K171, 0x1111, i * Frame); fc.Kernel(100, K171, 0x2222, i * Frame + 1); }
    Check(Math.Abs(1000 / fc.Drain(100).Average() - 100) < 1e-6, "a hybrid laptop's two graphics chips don't double the count");

    fc = new FrameCounter(F);
    for (var i = 0; i < 20; i++) { fc.RuntimeStart(1, 100, Dxgi, 0xA, 0, i * Frame); fc.RuntimeStop(1, 0); }
    for (var i = 0; i < 5; i++) { fc.RuntimeStart(2, 100, D3d9, 0xB, 0, i * 4 * Frame); fc.RuntimeStop(2, 0); }
    for (var i = 0; i < 50; i++) { fc.RuntimeStart(3, 200, Dxgi, 0xC, 0, i * Frame); fc.RuntimeStop(3, 0); }
    Check(fc.Drain(100).Count == 19 && fc.Drain(200).Count == 49 && fc.Drain(100).Count == 0, "the busiest swap chain of each process, and each frame once");

    fc = new FrameCounter(F);
    foreach (var t in new long[] { 0, 2 * Frame, Frame, 3 * Frame, 3 * Frame + 5 * F }) { fc.RuntimeStart(1, 100, Dxgi, 0xA, 0, t); fc.RuntimeStop(1, 0); }
    ms = fc.Drain(100);
    Check(ms.Count == 2 && Math.Abs(ms[0] - 20) < 1e-9 && Math.Abs(ms[1] - 10) < 1e-9, "events out of order and gaps over 4 s give no frame time");

    // The same, from the raw events as Windows logs them.
    var parsed = new FrameCounter(F) { Pids = [100] };
    void Log(Guid provider, ushort id, uint pid, long qpc, byte[] data, ushort flags = 0x40) =>
        parsed.Handle(new EtwEvent { Provider = provider, Id = id, Flags = flags, ProcessId = (int)pid, ThreadId = 1, Qpc = qpc, Data = data });
    byte[] ok = BitConverter.GetBytes(0), present = [.. BitConverter.GetBytes(0xA0UL), 0, 0, 0, 0], test = [.. BitConverter.GetBytes(0xA0UL), 1, 0, 0, 0];
    for (var i = 0; i < 10; i++)
    {
        Log(FrameCounter.Dxgi, 42, 100, i * Frame, present); Log(FrameCounter.Dxgi, 43, 100, i * Frame, ok);
        Log(FrameCounter.Dxgi, 55, 100, i * Frame + 5, test); Log(FrameCounter.Dxgi, 56, 100, i * Frame + 5, ok);
        Log(FrameCounter.Dxgi, 42, 200, i * Frame, present); Log(FrameCounter.Dxgi, 43, 200, i * Frame, ok); // not a game
        Log(FrameCounter.Dxgi, 42, 100, i * Frame + 6, [1, 2]);                                             // cut short
    }
    Check(parsed.Drain(100).Count == 9 && parsed.Drain(200).Count == 0, "DXGI events count for a game, but not its TEST presents, short events or other programs");
    byte[] History(uint model) => [.. BitConverter.GetBytes(0x10UL), .. BitConverter.GetBytes(0UL), .. BitConverter.GetBytes(model)];
    for (var i = 0; i < 10; i++)
    {
        Log(FrameCounter.DxgKrnl, 171, 100, i * Frame, History(2));
        Log(FrameCounter.DxgKrnl, 171, 100, i * Frame + 1, History(7)); // composition: the desktop's present, not the game's
        Log(FrameCounter.DxgKrnl, 171, 100, i * Frame + 2, History(7));
    }
    Check(parsed.Drain(100) is { Count: 9 } kernel && kernel.All(m => Math.Abs(m - 10) < 1e-9), "the kernel's presents count for Vulkan and OpenGL, without the desktop's");
    for (var i = 0; i < 5; i++) { Log(FrameCounter.D3d9, 1, 100, i * 2 * Frame, BitConverter.GetBytes(0xB0u), 0x20); Log(FrameCounter.D3d9, 2, 100, i * 2 * Frame, ok, 0x20); }
    Check(parsed.Drain(100) is { Count: 4 } d3d9 && d3d9.All(m => Math.Abs(m - 20) < 1e-9), "a 32-bit DirectX 9 game's frames");
    for (var i = 0; i < 5; i++) Log(FrameCounter.DxgKrnl, 168, 100, i * Frame, []);
    Check(parsed.Drain(100).Count == 4, "and a fullscreen Vulkan game's flips");
}

// FPS numbers: average, high, 1% and 0.1% lows and the chart, from frame times.
{
    static FrameStats Feed(params (int n, double ms)[] runs)
    {
        var s = new FrameStats();
        foreach (var (n, ms) in runs) for (var i = 0; i < n; i++) s.Add(ms);
        return s;
    }
    static bool Near(double? a, double b, double tol = 1e-9) => a is { } x && Math.Abs(x - b) <= tol;

    var e = new FrameStats().Summary();
    Check(e is { Average: null, High: null, OnePercentLow: null, PointOnePercentLow: null, Last: null, Frames: 0, Seconds: 0 } && e.Points.Length == 0,
          "nothing to show before any frames");
    Check(FrameStats.Show(null) == "–" && FrameStats.Show(96.5) == "97" && FrameStats.Show(95.49) == "95", "numbers show as whole FPS or a dash");

    var s = Feed((1000, 10)).Summary();
    Check(Near(s.Average, 100) && s.High == 100 && Near(s.OnePercentLow, 100) && Near(s.PointOnePercentLow, 100) && s.Seconds == 10, "steady 100 FPS reads 100 everywhere");
    Check(s.Points.Length == 10 && s.Points.All(p => p == 100) && s.Last == 100 && s.SecondsPerPoint == 1, "one chart point per second");

    s = Feed((990, 10), (10, 50)).Summary();
    Check(Near(s.Average, 1000.0 * 1000 / 10400) && FrameStats.Show(s.Average) == "96", "the average is frames over time: 96.15");
    Check(Near(s.OnePercentLow, 20) && Near(s.PointOnePercentLow, 20), "the 1% and 0.1% lows are the slow 50 ms frames: 20 FPS");
    Check(s.High == 100 && s.Last == 92 && s.Points.Length == 10, "the best second is 100; the one with the slow frames had 92");

    var few = Feed((99, 20));
    s = few.Summary();
    Check(Near(s.Average, 50) && s.High == 50 && s.OnePercentLow == null && s.PointOnePercentLow == null, "under 100 frames: no 1% low yet");
    few.Add(20);
    s = few.Summary();
    Check(Near(s.OnePercentLow, 50) && s.PointOnePercentLow == null && s.Frames == 100, "100 frames: a 1% low, still no 0.1% low");
    s = Feed((500, 1)).Summary();
    Check(s.Average == null && s.High == null && s.Frames == 500, "nothing shows until the first full second");

    s = Feed((197, 10), (3, 40)).Summary();
    Check(Near(s.OnePercentLow, 25), "200 frames: the 1% low is the slowest 2 of the three 40 ms frames");
    s = Feed((297, 10), (2, 25), (1, 100)).Summary();
    Check(Near(s.OnePercentLow, 20), "the lows are frames over time (100, 25, 25 ms = 20 FPS), not the mean of their FPS");
    s = Feed((1998, 5), (1, 20), (1, 100)).Summary();
    Check(Near(s.Average, 2000 * 1000.0 / 10110) && Near(s.OnePercentLow, 20 * 1000.0 / 210) && Near(s.PointOnePercentLow, 2 * 1000.0 / 120),
          "1% low = 20 frames in 210 ms; 0.1% low = 2 frames in 120 ms");

    var p = new FrameStats();
    for (var i = 0; i < 100; i++) p.Add(10);
    p.Add(5000); p.Add(0); p.Add(-3); p.Add(double.NaN); p.Add(double.PositiveInfinity);
    for (var i = 0; i < 100; i++) p.Add(10);
    s = p.Summary();
    Check(s.Frames == 200 && Near(s.Average, 100) && Near(s.OnePercentLow, 100) && s.Points.Length == 2, "a 5 s pause and bad frame times are left out");
    p.Add(FrameStats.PauseMs);
    Check(p.Summary() is { Frames: 201 } f && Near(f.OnePercentLow, 2 * 1000.0 / 1010), "a 1 s freeze is a frame, not a pause");

    s = Feed((60_000, 10), (30_000, 20)).Summary();
    Check(s.Points.Length == 300 && s.SecondsPerPoint == 4 && s.Points[0] == 100 && s.Points[^1] == 50 && s.Seconds == 1200, "a long session's chart points merge to stay under 600");
    Check(Near(s.Average, 75) && s.High == 100 && Near(s.OnePercentLow, 50) && Near(s.PointOnePercentLow, 50), "and the numbers stay exact");

    var rng = new Random(1);
    var times = Enumerable.Range(0, 200_000).Select(_ => 4 + rng.NextDouble() * 4 + (rng.Next(200) == 0 ? rng.NextDouble() * 80 : 0)).ToArray();
    var r = new FrameStats();
    foreach (var t in times) r.Add(t);
    s = r.Summary();
    var slow = times.OrderDescending().ToArray();
    double Exact(int k) => 1000.0 * k / slow.Take(k).Sum();
    Check(Near(s.Average, times.Length * 1000 / times.Sum(), 1e-6) && Near(s.OnePercentLow, Exact(2000), Exact(2000) * 0.001)
          && Near(s.PointOnePercentLow, Exact(200), Exact(200) * 0.001), "random frames: the lows are within 0.1% of sorting every frame");

    s = Feed((2_200_000, 2.5)).Summary();
    Check(Near(s.Average, 400, 1e-6) && s.Seconds == 5500, "a 90 minute session at 400 FPS still averages 400");

    var shared = new FrameStats();
    var writer = Task.Run(() => { for (var i = 0; i < 300_000; i++) shared.Add(3.3); });
    while (!writer.IsCompleted) _ = shared.Summary();
    writer.Wait();
    Check(shared.Summary().Frames == 300_000, "adding and reading at the same time loses nothing");
}

// The recorder: a made-up game's frames through Windows' counter. Process ids 7 and 11 can't be real ones (Windows'
// are multiples of 4), so no real program's frames get in.
long fpsAt = 0;
void Frames(uint pid, int n, double ms)
{
    for (var i = 0; i < n; i++)
    {
        Fps.Counter.RuntimeStart(1, pid, FrameCounter.Kind.Dxgi, 0xA, 0, fpsAt += (long)Math.Round(ms * Stopwatch.Frequency / 1000));
        Fps.Counter.RuntimeStop(1, 0);
    }
}
var fpsGame = new Game("Fps Game", @"C:\Games\Fps", "Steam");
Check(Fps.Update(new() { [7] = fpsGame, [11] = fpsGame }, profileOn: true).Count == 0 && Fps.Error == null,
      $"frames are counted while a game runs ({Fps.Error})");
Frames(11, 20, 16.7); // a launcher in the game's folder: a few frames
Frames(7, 1, 10);
for (var second = 0; second < 40; second++)
{
    Frames(7, 95, 10);
    Frames(7, 1, 50); // one hitch a second
    Fps.Tick(second is < 20 or >= 30 ? 7u : 0); // alt-tabbed for 10 seconds
}
fpsAt += 3 * Stopwatch.Frequency; // a loading screen
Frames(7, 1, 10);
Fps.Tick(7);
Frames(7, 200, 10);
Fps.Counter.RuntimeStart(1, 7, FrameCounter.Kind.Dxgi, 0xA, 0, fpsAt - 5); // out of order: no frame time
Fps.Counter.RuntimeStop(1, 0);
Fps.Tick(7);
Check(Fps.Now() is { Session.Game: "Fps Game", Current: > 99.9 and < 100.1, InFront: true }, "the game being played shows live");
Check(Fps.Update(new() { [7] = fpsGame }, true).Count == 0, "the launcher closing doesn't end the session");
var played = Fps.Update([], false);
Check(played is [{ Game: "Fps Game", Profile: true, Seconds: 28, High: 100, Low1: 20, Low01: 20, Step: 1, Chart.Length: 28 }]
      && Math.Abs(played[0].Average!.Value - 2696 / 28.0) < 1e-9,
      "one session per game, from the process that drew most, without the loading screen, the alt-tabbed seconds or the 2 s after each switch to the game");
Check(Etw.Stop(Fps.SessionName) == 4201, "frame counting stops when no game runs");
Fps.Update(new() { [15] = fpsGame }, false);
for (var i = 0; i < 3; i++)
{
    Frames(15, 300, 10);
    Fps.Tick(15);
}
Check(Fps.Update([], false).Count == 0 && Fps.Now() == null, "a session under 10 seconds isn't kept");

for (var i = 0; i < 25; i++) Fps.Save(played[0] with { Seconds = 100 + i });
Check(Fps.History() is { Count: 20 } history && history[0].Seconds == 124 && history[^1].Seconds == 105 && history[0].Chart.SequenceEqual(played[0].Chart),
      "keeps the last 20 sessions, newest first");
Settings.SetText("FpsSessions", "{ half writ");
Check(Fps.History().Count == 0, "a damaged session history reads as empty");
Check(new SessionRow(played[0] with { Average = 127.6 }, played[0] with { Average = 112.3 }).Change == "+14%"
      && new SessionRow(played[0], null).Change == "" && new SessionRow(played[0] with { Low1 = null }, null).Low1 == "–",
      "a session compares with that game's one before, and shows a dash for a number it hasn't got");

// Windows' trace sessions (needs admin, like the app).
using (var source = new TestSource())
{
    var before = Stopwatch.GetTimestamp();
    (int Pid, int Tid, ushort Flags, long Qpc, int Value, string? On) first = default;
    var thread = GetCurrentThreadId();
    using var arrived = new ManualResetEventSlim();
    var etw = new Etw("Optimiser Test", e =>
    {
        if (e.Provider != source.Guid || e.Id != 1 || arrived.IsSet) return; // skips ETW's own header event and the manifest
        first = (e.ProcessId, e.ThreadId, e.Flags, e.Qpc, BitConverter.ToInt32(e.Data), Thread.CurrentThread.Name);
        arrived.Set();
    });
    var enabled = etw.Enable(source.Guid, 1);
    for (var i = 0; i < 100 && !arrived.IsSet; i++) // enabling is asynchronous and ETW delivers about once a second
    {
        source.Ping(42);
        arrived.Wait(100);
    }
    etw.Dispose();
    Check(enabled == 0 && arrived.IsSet && first.Value == 42 && first.Pid == Environment.ProcessId && first.Tid == (int)thread
          && (first.Flags & 0x40) != 0 && first.Qpc > before && first.Qpc <= Stopwatch.GetTimestamp(),
          "ETW delivers an event with its process, thread, QPC time and payload");
    Check(first.On == "ETW Optimiser Test" && etw.Finished.Wait(10_000) && etw.Error is null,
          "the handler runs on the session's own thread, and disposing ends that thread");
}

// What a game's frames look like to the counter, end to end: a WARP (software) device presenting to a small window.
using (var swapChain = new WarpSwapChain())
{
    var pid = (uint)Environment.ProcessId;
    var counter = new FrameCounter(Stopwatch.Frequency) { Pids = [pid] };
    var etw = new Etw("Optimiser Test", counter.Handle);
    uint[] errors = [etw.Enable(FrameCounter.DxgKrnl, Environment.OSVersion.Version.Build >= 22000 ? 0x8000001UL : 0x1, 171, 168),
                     etw.Enable(FrameCounter.Dxgi, 0x8000000000000002, 42, 43, 55, 56), etw.Enable(FrameCounter.D3d9, 0x8000000000000002, 1, 2)];
    var frames = 0;
    for (var i = 0; i < 200 && frames < 20; i++)
    {
        swapChain.Present();
        Thread.Sleep(20);
        frames += counter.Drain(pid).Count;
    }
    etw.Dispose();
    Check(errors.All(e => e == 0) && frames >= 20 && etw.Finished.Wait(10_000) && etw.Error is null,
          $"this process's DirectX frames reach the frame counter ({string.Join(", ", errors)}; {frames} frames)");
}

var orphan = new Etw("Optimiser Test", _ => { }); // never disposed, as after a crash
var next = new Etw("Optimiser Test", _ => { });
next.Dispose();                                   // straight away, before its thread may even have started
Check(orphan.Finished.Wait(10_000) && next.Finished.Wait(10_000) && Etw.Stop("Optimiser Test") == 4201,
      "a stale trace session is replaced, and disposing stops it");

string[] tiles = ["GPU load", "CPU load", "Memory", "Power"];
Check(tiles.OrderBy(t => StatTile.Rank(t, "Memory|GPU load")).SequenceEqual(["Memory", "GPU load", "CPU load", "Power"])
      && tiles.OrderBy(t => StatTile.Rank(t, null)).SequenceEqual(tiles),
      "stat tiles follow the saved order, with new ones after it in their usual order");

Training.Folder = Path.Combine(temp, "rl-shot-trainer");
Directory.CreateDirectory(Training.Folder);
Check(!Training.Installed && Training.Load().Count == 0, "no shot trainer: not installed, and its settings read as empty");
File.WriteAllText(Path.Combine(Training.Folder, "settings.json"),
    """{"bot_skill": 35.0, "order": "In order", "repeat": false, "shots": [true, false], "cmd": "", "cmd_id": 1, "later": 7}""");
var training = Training.Load();
training["bot_skill"] = 80.0;
Training.Save(training);
Training.Send("retry");
Training.Send("next");
training = Training.Load();
Check(Training.Number(training, "bot_skill", 0) == 80 && Training.Number(training, "head_start", 0.5) == 0.5
      && !Training.Flag(training, "repeat", true) && Training.Number(training, "later", 0) == 7
      && training["shots"]!.AsArray().Count == 2 && !File.Exists(Path.Combine(Training.Folder, "settings.json.tmp")),
      "shot trainer settings save without losing keys Optimiser doesn't know");
Check(training["cmd"]!.ToString() == "next" && Training.Number(training, "cmd_id", 0) == 3,
      "each shot trainer command bumps cmd_id, so the trainer acts on it once");
Training.EpicManifests = Path.Combine(temp, "Manifests");
Check(!Training.GameUpdating(), "no Epic manifests: Rocket League isn't updating");
Directory.CreateDirectory(Path.Combine(Training.EpicManifests, "Pending"));
File.WriteAllText(Path.Combine(Training.EpicManifests, "rl.item"), """{"AppName": "Sugar", "bIsIncompleteInstall": false}""");
File.WriteAllText(Path.Combine(Training.EpicManifests, "fn.item"), """{"AppName": "Fortnite", "bIsIncompleteInstall": true}""");
Check(!Training.GameUpdating(), "installed Rocket League, another game updating: not updating");
File.Move(Path.Combine(Training.EpicManifests, "rl.item"), Path.Combine(Training.EpicManifests, "Pending", "rl.item"));
Check(Training.GameUpdating(), "Rocket League's manifest in Pending: updating");
Check(Training.Clips().Count == 0, "no clips folder: no clips");
Directory.CreateDirectory(Training.ClipsFolder);
var older = Path.Combine(Training.ClipsFolder, "older.mp4");
var newer = Path.Combine(Training.ClipsFolder, "newer.mp4");
File.WriteAllText(older, "");
File.SetLastWriteTime(older, DateTime.Now.AddHours(-1));
File.WriteAllText(newer, "");
File.WriteAllText(Path.ChangeExtension(newer, ".json"), """{"shot": "6. Wall air dribble", "result": "GOAL!"}""");
File.WriteAllText(Path.Combine(Training.ClipsFolder, "newer.scrub.mp4"), "");
File.WriteAllText(Path.Combine(Training.ClipsFolder, "saving.tmp.mp4"), "");
var clips = Training.Clips();
File.Delete(Path.Combine(Training.ClipsFolder, "newer.scrub.mp4"));
File.Delete(Path.Combine(Training.ClipsFolder, "saving.tmp.mp4"));
Check(clips.Count == 2 && clips[0].Shot == "6. Wall air dribble" && clips[0].Result == "GOAL!" && clips[1].Shot == "older",
      "clips list newest first, named from their .json, or from the file without one, without files still being written");
Check(!clips[1].Favourite && clips[1].Edit == ClipEdit.None && clips[1].Events == null, "a clip without a .json isn't edited");
clips[1].Favourite = true;
clips[1].Edit = ClipEdit.None with { In = 2.5, Saturation = 1.4 };
Training.SaveEdits(clips[1]);
File.WriteAllText(Path.ChangeExtension(newer, ".json"),
    """{"shot": "6. Wall air dribble", "result": "GOAL!", "later": 1, "events": {"length": 20, "touches": [9.5, 6.5, 12], "goal": 14, "shot_start": null}}""");
clips[0].Favourite = true;
Training.SaveEdits(clips[0]);
clips = Training.Clips();
Check(clips[1].Favourite && clips[1].Edit == ClipEdit.None with { In = 2.5, Saturation = 1.4 },
      "favourite and edit save to a new .json for a clip without one");
Check(clips[0].Favourite && clips[0].Shot == "6. Wall air dribble" && clips[0].Edit == ClipEdit.None
      && File.ReadAllText(Path.ChangeExtension(newer, ".json")).Contains("\"later\": 1"),
      "saving a favourite keeps what the trainer wrote");
Check(clips[0].Events is { Goal: 14, ShotStart: null, Touches.Length: 3 } ev && ev.Shot() == (5.5, 16.5) && ev.GoalOnly() == (9, 16),
      "the shot cut runs from 1 s before the first touch to 2.5 s after the goal; the goal cut 5 s before to 2 s after");
Check(new ClipEvents(20, [3, 18.5], null, 1.5).Shot() == (0.5, 20) && new ClipEvents(20, [], null, null).Shot() == null
      && new ClipEvents(20, [], null, null).GoalOnly() == null,
      "without a goal the cut ends 3 s after the last touch, never past the end; without events there's no cut");
var cut = Training.EditArgs("in.mp4", ClipEdit.None with { In = 4.5, Out = 16 }, "out.mp4");
Check(string.Join(" ", cut).Contains("-ss 4.5 -i in.mp4 -t 11.5") && !cut.Contains("-vf") && cut[^1] == "out.mp4",
      "a trim only cuts, from the start time for its length");
var look = Training.EditArgs("in.mp4", ClipEdit.None with { Contrast = 1.25, Warmth = 0.125 }, "out.mp4");
Check(!look.Contains("-ss") && !look.Contains("-t") && look[look.IndexOf("-vf") + 1].StartsWith("eq=brightness=0:contrast=1.25:saturation=1,lutrgb=r='clipval+31.875'")
      && look[look.IndexOf("-vf") + 1].Contains("b='clipval+-31.875'"), "colours go through eq and lutrgb, written with dots whatever the language");
var edited = clips[1] with { };
Directory.CreateDirectory(Path.GetDirectoryName(edited.Original)!);
File.WriteAllText(edited.Original, "recording");
File.WriteAllText(edited.File, "edited");
Check(edited.Source == edited.Original, "once edited, the editor plays the kept recording");
edited.Edit = ClipEdit.None;
Training.SaveVideo(edited).Wait();
Check(File.ReadAllText(edited.File) == "recording" && !File.Exists(edited.Original) && Training.Clips().Single(c => c.File == edited.File).Saved == ClipEdit.None,
      "saving with no edit puts the recording back");
File.WriteAllText(Path.Combine(Training.ClipsFolder, "Flip reset.mp4"), "taken");
var renamed = Training.Rename(Training.Clips().Single(c => c.File == edited.File), " Flip reset? ");
Check(Path.GetFileName(renamed.File) == "Flip reset (2).mp4" && File.Exists(renamed.File) && !File.Exists(edited.File)
      && Training.Clips().Single(c => c.File == renamed.File).Shot == "Flip reset?",
      "renaming moves the video and its .json, leaves out characters files can't have, and doesn't overwrite another clip");
Check(Path.GetFileName(Training.Rename(renamed, "nul").File) == "nul clip.mp4", "a name Windows keeps for devices still works");
File.Delete(Path.Combine(Training.ClipsFolder, "Flip reset.mp4"));
Check(ColourEffect.Shader != null, "the clip colour shader compiles with Windows' d3dcompiler_47.dll");
Training.Delete(clips[0]);
Check(!File.Exists(newer) && !File.Exists(Path.ChangeExtension(newer, ".json")) && Training.Clips().Count == 1,
      "deleting a clip removes its video and its .json");

Directory.Delete(temp, true);
Registry.CurrentUser.DeleteSubKeyTree(sub);
Console.WriteLine("All checks passed");

static void Manifest(string library, int id, string name, string dir) =>
    File.WriteAllText(Path.Combine(library, "steamapps", $"appmanifest_{id}.acf"),
        $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{id}\"\n\t\"name\"\t\t\"{name}\"\n\t\"installdir\"\t\t\"{dir}\"\n}}\n");

static bool Throws(Action action)
{
    try { action(); return false; }
    catch (InvalidOperationException) { return true; }
}

static object? Read(string name) => Registry.GetValue(key, name, null);

static void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    if (!ok) Environment.Exit(1);
}

[System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

[System.Diagnostics.Tracing.EventSource(Name = "Optimiser-Test")]
sealed class TestSource : System.Diagnostics.Tracing.EventSource
{
    public static class Keywords { public const System.Diagnostics.Tracing.EventKeywords Test = (System.Diagnostics.Tracing.EventKeywords)1; }

    [System.Diagnostics.Tracing.Event(1, Keywords = Keywords.Test)] public void Ping(int value) => WriteEvent(1, value);
}
