using Microsoft.Win32;

namespace Optimiser;

// Optimiser's own options. They live under HKLM, which only admins can write, so a normal program
// can't change what this admin app does.
public static class Settings
{
    public static string Key = Backup.DefaultKey; // the self-checks point this at a throwaway key

    public static bool Get(string name, bool fallback) => Reg.Read(Key, name) is int v ? v != 0 : fallback;

    public static void Set(string name, bool on)
    {
        using var k = Reg.Open(Key, write: true)!;
        k.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord);
    }

    public static string[] GetList(string name) => Reg.Read(Key, name) as string[] ?? [];

    public static void SetList(string name, string[] values)
    {
        using var k = Reg.Open(Key, write: true)!;
        k.SetValue(name, values, RegistryValueKind.MultiString);
    }

    public static string? GetText(string name) => Reg.Read(Key, name) as string;

    public static void SetText(string name, string? value)
    {
        using var k = Reg.Open(Key, write: true)!;
        if (value == null) k.DeleteValue(name, throwOnMissingValue: false);
        else k.SetValue(name, value, RegistryValueKind.String);
    }
}
