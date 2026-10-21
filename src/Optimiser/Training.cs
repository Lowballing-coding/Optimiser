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
    public static void OnGameStart(uint pid, string exe)
    {
        if (!Path.GetFileName(exe).Equals("RocketLeague.exe", StringComparison.OrdinalIgnoreCase)) return;
        lock (games) games.Add(pid);
        OpenShift();
        if (Settings.Get(WithGameSetting, false) && Installed && !Running && !Leaving) Start();
    }

    // leave.bat (Leave training) reopens Rocket League without RLBot, and marks that here so this doesn't start
    // training again as the game reopens.
    static bool Leaving
    {
        get
        {
            var flag = Path.Combine(Folder, "leaving.flag");
            return File.Exists(flag) && DateTime.Now - File.GetLastWriteTime(flag) < TimeSpan.FromSeconds(60);
        }
    }

    // Back to normal Rocket League: stops RLBot and reopens the game without it, for online play. False when the
    // trainer has no leave.bat.
    public static bool Leave()
    {
        var leave = Path.Combine(Folder, "leave.bat");
        if (!File.Exists(leave)) return false;
        Tweaks.OpenAsYou(leave);
        return true;
    }

    static readonly HashSet<uint> games = []; // Rocket League processes seen starting

    // The last Rocket League closed. RLBot closes and reopens the game when training starts, so Shift only closes if
    // the game is still gone a little later.
    public static void OnGameStop(uint pid)
    {
        lock (games) if (!games.Remove(pid) || games.Count > 0) return;
        Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ =>
        {
            lock (games) if (games.Count > 0) return;
            CloseShift();
        });
    }

    public const string ShiftSetting = "ShiftWithRocketLeague";
    public static string ShiftExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shift", "Shift.exe");
    public static bool ShiftInstalled => File.Exists(ShiftExe);

    // Shift swaps map files as the game loads them, so the buttons here open it before Rocket League.
    static void OpenShift()
    {
        if (!Settings.Get(ShiftSetting, false) || !ShiftInstalled) return;
        var open = Process.GetProcessesByName("Shift");
        foreach (var p in open) p.Dispose();
        if (open.Length > 0) return;
        Tweaks.OpenAsYou(ShiftExe);
        Task.Run(MinimiseShift);
    }

    // Shift has no start-minimised option, so its window is minimised as soon as it appears, without taking focus.
    static async Task MinimiseShift()
    {
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(250);
            foreach (var p in Process.GetProcessesByName("Shift"))
                using (p)
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, 7 /* SW_SHOWMINNOACTIVE */);
                        return;
                    }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr window, int command);

    static void CloseShift()
    {
        if (!Settings.Get(ShiftSetting, false)) return;
        foreach (var p in Process.GetProcessesByName("Shift"))
            using (p)
            {
                try
                {
                    if (!p.CloseMainWindow() || !p.WaitForExit(5000)) p.Kill();
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } // already gone
            }
    }

    public static string EpicManifests = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    // While Epic patches Rocket League ("Sugar"), its install manifest moves to Manifests\Pending and is marked incomplete.
    // Epic then refuses to launch the game, so starting training would look like nothing happened.
    public static bool GameUpdating()
    {
        try
        {
            return Directory.EnumerateFiles(EpicManifests, "*.item", System.IO.SearchOption.AllDirectories).Any(file =>
            {
                var item = Read(file);
                return item["AppName"]?.ToString() == "Sugar"
                    && (Path.GetFileName(Path.GetDirectoryName(file)) == "Pending" || Flag(item, "bIsIncompleteInstall", false));
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public const string UpdatingNote = "Rocket League is updating in the Epic Games Launcher.";

    // Rocket League on its own, through Epic (namespace:catalog item:app name).
    public static void LaunchGame()
    {
        OpenShift();
        Tweaks.OpenAsYou("com.epicgames.launcher://apps/9773aa1aa54f4f7b80e44bef04986cea%3A530145df28a24424923f5828cc9031a1%3ASugar?action=launch&silent=true");
    }

    // A plain 1v1 against Necto (match.bat in the trainer folder). False when the trainer has no match mode.
    public static bool Match()
    {
        var match = Path.Combine(Folder, "match.bat");
        if (!File.Exists(match)) return false;
        OpenShift();
        Tweaks.OpenAsYou(match);
        return true;
    }

    // Like the trainer's start.bat, minus its old settings window, which would overwrite this page's changes.
    // Through Explorer so it runs as Peter, not as admin like Optimiser.
    public static void Start()
    {
        var play = Path.Combine(Folder, "play.bat");
        if (!File.Exists(play)) File.WriteAllText(play, "@echo off\r\ncd /d \"%~dp0\"\r\nvenv\\Scripts\\python run.py\r\npause\r\n");
        OpenShift();
        Tweaks.OpenAsYou(play);
    }
}

public record ShotClip(string File, string Shot, string Result, DateTime Time)
{
    public string Info => Result == "" ? $"{Time:d MMM, HH:mm}" : $"{Result}   {Time:d MMM, HH:mm}";
}
