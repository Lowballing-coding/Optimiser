using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualBasic.FileIO;

namespace Optimiser;

// The Rocket League shot trainer (Python and RLBot, in Documents\rl-shot-trainer). It reads settings.json before every
// shot, so the Shot Trainer page only has to write that file. Keys this page doesn't know are kept as they are.
public static class Training
{
    // OneDrive can move Documents, and the trainer may be in either one: the first that has it, else the current one.
    public static string Folder = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"),
    }.Select(d => Path.Combine(d, "rl-shot-trainer")).FirstOrDefault(f => File.Exists(Path.Combine(f, "trainer.py")))
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "rl-shot-trainer");
    static string SettingsFile => Path.Combine(Folder, "settings.json");

    public static bool Installed => File.Exists(Path.Combine(Folder, "trainer.py"));

    public static bool Running
    {
        get
        {
            var found = Process.GetProcessesByName("RLBotServer");
            foreach (var p in found) p.Dispose();
            return found.Length > 0;
        }
    }

    // ponytail: copied from SHOTS in trainer.py; add a name here when a shot is added there
    public static readonly string[] Shots =
    [
        "1. Pass along the ground", "2. Bouncing ball at you", "3. Lob: easy aerial", "4. High pass: fast aerial",
        "5. Side wall pop", "6. Wall air dribble", "7. Ceiling-to-wall air dribble", "8. Pop and air dribble",
        "9. Cross from the corner", "10. Recovery and shoot",
    ];

    public static JsonObject Load() => Read(SettingsFile); // missing or half-written: the trainer's defaults apply

    static JsonObject Read(string file)
    {
        try { return JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? []; }
        catch { return []; }
    }

    // Written to a temp file and swapped in, so the trainer never reads half a file. The swap can fail for a moment
    // while the trainer has the file open.
    public static void Save(JsonObject settings)
    {
        var temp = SettingsFile + ".tmp";
        File.WriteAllText(temp, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        for (var i = 0; ; i++)
        {
            try { File.Move(temp, SettingsFile, true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && i < 9) { Thread.Sleep(20); }
        }
    }

    public static double Number(JsonObject s, string key, double fallback) =>
        s[key] is JsonValue v && v.TryGetValue(out double d) ? d : fallback;

    public static bool Flag(JsonObject s, string key, bool fallback) =>
        s[key] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    public static string ClipsFolder => Path.Combine(Folder, "clips");

    // The trainer saves OBS's replay buffer as clips\<time> <shot>.mp4, with a .json beside it naming the shot and result.
    public static List<ShotClip> Clips()
    {
        if (!Directory.Exists(ClipsFolder)) return [];
        return [.. Directory.GetFiles(ClipsFolder, "*.mp4").Select(file =>
        {
            var about = Read(Path.ChangeExtension(file, ".json"));
            return new ShotClip(file, about["shot"]?.ToString() ?? Path.GetFileNameWithoutExtension(file),
                            about["result"]?.ToString() ?? "", File.GetLastWriteTime(file));
        }).OrderByDescending(c => c.Time)];
    }

    // To the Recycle Bin, so a clip deleted by mistake can be got back.
    public static void Delete(ShotClip clip)
    {
        foreach (var file in new[] { clip.File, Path.ChangeExtension(clip.File, ".json") }.Where(File.Exists))
            FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    // "retry", "next" or "save" (a clip). The trainer acts when cmd_id changes, so the same command can be sent twice in a row.
    public static void Send(string command)
    {
        var s = Load();
        s["cmd"] = command;
        s["cmd_id"] = (int)Number(s, "cmd_id", 0) + 1;
        Save(s);
    }

    public const string WithGameSetting = "TrainWithRocketLeague";

    // Rocket League was opened. RLBot has to start the game itself, so this starts the trainer, which reopens the game
    // with RLBot attached. While RLBot is running, a Rocket League start is its own, so it's left alone.
    public static void OnGameStart(string exe)
    {
        if (Path.GetFileName(exe).Equals("RocketLeague.exe", StringComparison.OrdinalIgnoreCase)
            && Settings.Get(WithGameSetting, false) && Installed && !Running) Start();
    }

    // Rocket League on its own, through Epic (namespace:catalog item:app name).
    public static void LaunchGame() =>
        Tweaks.OpenAsYou("com.epicgames.launcher://apps/9773aa1aa54f4f7b80e44bef04986cea%3A530145df28a24424923f5828cc9031a1%3ASugar?action=launch&silent=true");

    // Like the trainer's start.bat, minus its old settings window, which would overwrite this page's changes.
    // Through Explorer so it runs as Peter, not as admin like Optimiser.
    public static void Start()
    {
        var play = Path.Combine(Folder, "play.bat");
        if (!File.Exists(play)) File.WriteAllText(play, "@echo off\r\ncd /d \"%~dp0\"\r\nvenv\\Scripts\\python run.py\r\npause\r\n");
        Tweaks.OpenAsYou(play);
    }
}

public record ShotClip(string File, string Shot, string Result, DateTime Time)
{
    public string Info => Result == "" ? $"{Time:d MMM, HH:mm}" : $"{Result}   {Time:d MMM, HH:mm}";
}
