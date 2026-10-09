using System.Globalization;
using System.Management;
using System.Text.Json;
using Microsoft.Win32;

namespace Optimiser;

// The original value of one registry setting, saved before a tweak changed it.
// Old == null means the value didn't exist before, so undo deletes it.
public record Change(string Tweak, string Key, string Name, RegistryValueKind Kind, string? Old, DateTime At);

// Every registry write goes through Set, which records the original first, so any tweak can be undone
// on its own, even after a restart. Only the first original is kept: re-applying a tweak never
// overwrites the backup with an already-tweaked value.
//
// The record itself lives in the registry under HKLM, which only admins can write. A file somewhere
// a normal user could edit would let anyone choose what this admin app writes back on "undo".
public class Backup(string stateKey = Backup.DefaultKey, bool makeRestorePoint = true)
{
    public const string DefaultKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Optimiser";

    public List<Change> Changes { get; } = Load(stateKey);

    // Set when the automatic restore point before the first change couldn't be made.
    public string? RestorePointError { get; private set; }

    public void Set(string tweak, string key, string name, object value, RegistryValueKind kind)
    {
        if (!Changes.Any(c => c.Key == key && c.Name == name))
        {
            if (makeRestorePoint && Changes.Count == 0) RestorePointError = RestorePoint.Create("Before Optimiser changes");
            using var current = Open(key);
            var old = current?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var oldKind = old == null ? kind : current!.GetValueKind(name);
            Changes.Add(new(tweak, key, name, oldKind, old == null ? null : Encode(old), DateTime.Now));
            Save();
        }
        using var k = Open(key, write: true)!;
        k.SetValue(name, value, kind);
    }

    public void Undo(string tweak)
    {
        foreach (var c in Changes.Where(c => c.Tweak == tweak).Reverse().ToList())
        {
            using (var k = Open(c.Key, write: true)!)
            {
                if (c.Old == null) k.DeleteValue(c.Name, throwOnMissingValue: false);
                else k.SetValue(c.Name, Decode(c.Old, c.Kind), c.Kind);
            }
            Changes.Remove(c);
            Save(); // after each one, so a failure part-way never forgets what is still changed
        }
    }

    public void UndoAll()
    {
        foreach (var tweak in Changes.Select(c => c.Tweak).Distinct().Reverse().ToList()) Undo(tweak);
    }

    static List<Change> Load(string stateKey)
    {
        using var k = Open(stateKey);
        return k?.GetValue("Backup") is string json ? JsonSerializer.Deserialize<List<Change>>(json) ?? [] : [];
    }

    void Save()
    {
        using var k = Open(stateKey, write: true)!;
        k.SetValue("Backup", JsonSerializer.Serialize(Changes), RegistryValueKind.String); // one atomic write
    }

    static RegistryKey? Open(string key, bool write = false)
    {
        var split = key.IndexOf('\\');
        var root = key[..split] switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
            "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
            var hive => throw new ArgumentException($"Unsupported registry hive {hive}"),
        };
        return write ? root.CreateSubKey(key[(split + 1)..]) : root.OpenSubKey(key[(split + 1)..]);
    }

    static string Encode(object value) => value switch
    {
        string[] lines => string.Join('\0', lines),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };

    static object Decode(string s, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(s, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(s, CultureInfo.InvariantCulture),
        RegistryValueKind.MultiString => s.Length == 0 ? Array.Empty<string>() : s.Split('\0'),
        RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(s),
        _ => s,
    };
}

public static class RestorePoint
{
    // Returns null on success, otherwise why it failed. Windows silently skips the new point if one
    // was made in the last 24 hours, which is fine: a recent one already exists.
    public static string? Create(string description)
    {
        try
        {
            using var restore = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            var result = Convert.ToUInt32(restore.InvokeMethod("CreateRestorePoint", [description, 12u, 100u]));
            return result == 0 ? null : $"Windows returned error {result}. System Restore may be turned off for drive C:.";
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}
