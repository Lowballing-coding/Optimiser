using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Optimiser;

public record Game(string Name, string Folder, string Source)
{
    public string Detail => $"{Source}: {Folder}";
    public bool CanRemove => Source == Games.AddedByYou;
}

// Finds installed games: Steam and Epic libraries, plus folders added by hand. A process counts as a game
// when its .exe lives inside one of these folders.
public static partial class Games
{
    public const string AddedByYou = "Added by you";
    const string FoldersSetting = "GameFolders";

    public static List<Game> Find()
    {
        var steam = Reg.Read(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath") as string;
        var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\EpicGamesLauncher\Data\Manifests");
        return [.. Steam(steam).Concat(Epic(epic)).Concat(Added())
            .Where(g => Directory.Exists(g.Folder))
            .DistinctBy(g => Path.GetFullPath(g.Folder).TrimEnd('\\').ToLowerInvariant())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    // Steam keeps a list of library folders, and one appmanifest_<id>.acf per installed game in each.
    public static IEnumerable<Game> Steam(string? steamPath)
    {
        if (steamPath == null || !Directory.Exists(steamPath)) yield break;
        var libraries = new List<string> { steamPath };
        var list = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(list))
            libraries.AddRange(VdfValues(File.ReadAllText(list), "path"));

        foreach (var library in libraries.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(manifest);
                var name = VdfValues(text, "name").FirstOrDefault();
                var dir = VdfValues(text, "installdir").FirstOrDefault();
                if (name == null || dir == null || SteamTool().IsMatch(name)) continue;
                yield return new Game(name, Path.Combine(apps, "common", dir), "Steam");
            }
        }
    }

    // Epic writes one JSON .item file per install.
    public static IEnumerable<Game> Epic(string manifests)
    {
        if (!Directory.Exists(manifests)) yield break;
        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            Game? game = null;
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                var root = json.RootElement;
                var isGame = !root.TryGetProperty("AppCategories", out var categories)
                             || categories.EnumerateArray().Any(c => c.GetString() == "games");
                if (isGame && root.TryGetProperty("DisplayName", out var name) && root.TryGetProperty("InstallLocation", out var folder))
                    game = new Game(name.GetString()!, Path.GetFullPath(folder.GetString()!), "Epic Games");
            }
            catch (JsonException) { } // a half-written manifest; skip it
            if (game != null) yield return game;
        }
    }

    public static IEnumerable<Game> Added() =>
        Settings.GetList(FoldersSetting).Select(f => new Game(Path.GetFileName(f.TrimEnd('\\')), f, AddedByYou));

    // Returns why a folder was refused. Every program inside a game folder counts as a game, so a drive or a
    // folder like Program Files would put the whole PC on the gaming profile.
    public static string? Add(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd('\\');
        var system = new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                             Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile }
            .Select(f => Environment.GetFolderPath(f).TrimEnd('\\')).Where(f => f != "");
        if (Path.GetPathRoot(full)?.TrimEnd('\\') == full
            || system.Any(s => s.Equals(full, StringComparison.OrdinalIgnoreCase) || s.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)))
            return "That folder has more than games in it. Choose the folder one game is installed in.";
        Settings.SetList(FoldersSetting, [.. Settings.GetList(FoldersSetting).Append(full).Distinct(StringComparer.OrdinalIgnoreCase)]);
        return null;
    }

    public static void Remove(string folder) =>
        Settings.SetList(FoldersSetting, [.. Settings.GetList(FoldersSetting).Where(f => !f.Equals(folder, StringComparison.OrdinalIgnoreCase))]);

    // The game an exe belongs to, if any. Launchers, crash reporters, installers and anti-cheat don't count.
    public static Game? ForExe(string exe, IEnumerable<Game> games) =>
        IsGameExe(exe) ? games.FirstOrDefault(g => exe.StartsWith(g.Folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) : null;

    public static bool IsGameExe(string exe) => !NotAGame().IsMatch(Path.GetFileName(exe));

    // The game's own .exe files, for the per-app GPU setting. Unreal games keep theirs a few folders down.
    public static IEnumerable<string> Exes(Game game) =>
        Directory.Exists(game.Folder)
            ? Directory.EnumerateFiles(game.Folder, "*.exe",
                new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
              .Where(IsGameExe)
            : [];

    static IEnumerable<string> VdfValues(string vdf, string key) =>
        Regex.Matches(vdf, $"\"{key}\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.Replace(@"\\", @"\").Replace("\\\"", "\""));

    [GeneratedRegex(@"Steamworks|Redistributable|Steam Linux Runtime|^Proton", RegexOptions.IgnoreCase)]
    private static partial Regex SteamTool();

    [GeneratedRegex(@"crash|report|unins|setup|redist|prereq|anticheat|battleye|^eac|^be_|helper|launcher|dotnet|dxweb|^steam",
        RegexOptions.IgnoreCase)]
    private static partial Regex NotAGame();
}
