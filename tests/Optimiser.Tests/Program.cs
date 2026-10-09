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

var hw = Hardware.Detect();
Console.WriteLine(hw);
Check(hw.Cpu != "Unknown" && hw.PCores > 0 && hw.RamGb > 0 && hw.Gpus.Length > 0, "hardware detected");

Registry.CurrentUser.DeleteSubKeyTree(sub);
Console.WriteLine("All checks passed");

static object? Read(string name) => Registry.GetValue(key, name, null);

static void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    if (!ok) Environment.Exit(1);
}
