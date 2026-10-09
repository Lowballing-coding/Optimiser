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

    public List<Change> Snapshot()
    {
        lock (Changes) return [.. Changes];
    }

    public bool Owns(string tweak)
    {
        lock (Changes) return Changes.Any(c => c.Tweak == tweak);
    }

    // ponytail: one lock for everything; changes are rare and quick apart from the first restore point.
    public void Set(string tweak, string key, string name, object value, RegistryValueKind kind)
    {
        lock (Changes) SetLocked(tweak, key, name, value, kind);
    }

    void SetLocked(string tweak, string key, string name, object value, RegistryValueKind kind)
    {
        var saved = Changes.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                                             && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        // Two tweaks sharing one setting couldn't be undone independently, so that's a bug in the tweak list.
        if (saved != null && saved.Tweak != tweak)
            throw new InvalidOperationException($"{key}\\{name} is already changed by \"{saved.Tweak}\".");
        if (saved == null)
        {
            if (makeRestorePoint && Changes.Count == 0) RestorePointError = RestorePoint.Create("Before Optimiser changes");
            using var current = Reg.Open(key);
            var old = current?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var oldKind = old == null ? kind : current!.GetValueKind(name);
            if (oldKind == RegistryValueKind.Unknown) // e.g. REG_RESOURCE_LIST, which .NET can't write back
                throw new InvalidOperationException($"{key}\\{name} has a type Optimiser can't restore, so it won't change it.");
            Changes.Add(new(tweak, key, name, oldKind, old == null ? null : Encode(old), DateTime.Now));
            Save();
        }
        using var k = Reg.Open(key, write: true)!;
        k.SetValue(name, value, kind);
    }

    public void Undo(string tweak)
    {
        lock (Changes) UndoLocked(tweak);
    }

    void UndoLocked(string tweak)
    {
        foreach (var c in Changes.Where(c => c.Tweak == tweak).Reverse().ToList())
        {
            using (var k = Reg.Open(c.Key, write: true)!)
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
        lock (Changes)
            foreach (var tweak in Changes.Select(c => c.Tweak).Distinct().Reverse().ToList()) UndoLocked(tweak);
    }

    static List<Change> Load(string stateKey)
    {
        using var k = Reg.Open(stateKey);
        return k?.GetValue("Backup") is string json ? JsonSerializer.Deserialize<List<Change>>(json) ?? [] : [];
    }

    void Save()
    {
        using var k = Reg.Open(stateKey, write: true)!;
        k.SetValue("Backup", JsonSerializer.Serialize(Changes), RegistryValueKind.String); // one atomic write
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

public static class Reg
{
    // Opens a full key path such as HKEY_LOCAL_MACHINE\SOFTWARE\X (HKLM and HKCU short forms work too).
    // Write mode creates the key.
    public static RegistryKey? Open(string key, bool write = false)
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

    public static object? Read(string key, string name)
    {
        using var k = Open(key);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }
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
