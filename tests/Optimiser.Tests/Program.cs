// Self-checks for the parts that touch the system. Run on Windows: dotnet run --project tests/Optimiser.Tests
// Uses a throwaway key under HKEY_CURRENT_USER, so it needs no admin rights and changes nothing real.
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

var hw = Hardware.Detect();
Console.WriteLine(hw);
Check(hw.Cpu != "Unknown" && hw.PCores > 0 && hw.RamGb > 0 && hw.Gpus.Length > 0, "hardware detected");

Registry.CurrentUser.DeleteSubKeyTree(sub);
Console.WriteLine("All checks passed");

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
