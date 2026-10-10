// Self-checks for the parts that touch the system. Run on Windows as admin: dotnet run --project tests/Optimiser.Tests
// Uses a throwaway key under HKEY_CURRENT_USER and a throwaway folder, so it changes nothing real.
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Optimiser;

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
}

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
Manifest(library2, 1091500, "Cyberpunk 2077", "Cyberpunk 2077");
var steamGames = Games.Steam(steam.Replace('\\', '/')).ToList(); // Steam stores its own path with forward slashes
Check(steamGames.Select(g => g.Name).Order().SequenceEqual(["Cyberpunk 2077", "Hades"]) && steamGames.All(g => Directory.Exists(g.Folder)),
      "Steam games found in every library, Steam's own tools skipped");

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
Check(Games.Add(gameDir + @"\") == null && Games.Added().Single() is { Name: "Fake Game", CanRemove: true }, "a game folder can be added");
Games.Remove(gameDir);
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
    Check(changed.WaitOne(15000) && watcher.Running.Count == 0 && Settings.GetText(GameWatcher.SavedPowerMode) == null,
          "the profile switches off and the power mode goes back when the game closes");
}

// The per-game GPU setting keeps Windows' other options in the same value, and undo puts it back.
const string gpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
using (var k = Registry.CurrentUser.CreateSubKey(gpuKey)) k.SetValue(fakeGame, "SwapEffectUpgradeEnable=1;GpuPreference=1;");
GameWatcher.PreferDedicatedGpu(backup, fakeGame);
Check(Registry.GetValue(GameWatcher.GpuPreferences, fakeGame, null) is "SwapEffectUpgradeEnable=1;GpuPreference=2;", "game set to the dedicated GPU");
backup.Undo(Tweaks.GpuTweakName);
Check(Registry.GetValue(GameWatcher.GpuPreferences, fakeGame, null) is "SwapEffectUpgradeEnable=1;GpuPreference=1;", "undo puts the GPU setting back");
using (var k = Registry.CurrentUser.OpenSubKey(gpuKey, writable: true)!) k.DeleteValue(fakeGame);

// Start with Windows: a copy of the app somewhere only admins can change, started by a logon task.
AutoStart.TaskName = "OptimiserSelfTest";
AutoStart.InstallFolder = Path.Combine(temp, "Installed");
AutoStart.TurnOn(fakeGame);
Check(AutoStart.IsOn() && File.Exists(AutoStart.InstalledExe), "start with Windows installs a copy and adds the task");
AutoStart.TurnOff();
Check(!AutoStart.IsOn() && !Directory.Exists(AutoStart.InstallFolder), "turning it off removes both");

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
