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

    // The trainer writes its shot names (custom shots too) to shots.json when it starts; until then, the built-in ones.
    public static string[] Shots
    {
        get
        {
            try
            {
                var names = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "shots.json"))) as JsonArray;
                if (names?.Select(n => n?.ToString() ?? "").ToArray() is { Length: > 0 } list) return list;
            }
            catch { } // not written yet, or half-written
            return BuiltInShots;
        }
    }

    // SHOTS in trainer.py
    public static readonly string[] BuiltInShots =
    [
        "1. Pass along the ground", "2. Bouncing ball at you", "3. Lob: easy aerial", "4. High pass: fast aerial",
        "5. Side wall pop", "6. Wall air dribble", "7. Ceiling-to-wall air dribble", "8. Pop and air dribble",
        "9. Cross from the corner", "10. Recovery and shoot", "11. Save: low shot", "12. Save: high shot", "13. Kickoff",
        "14. Backboard read", "15. Flip reset setup",
    ];

    // stats.jsonl: one line per attempt, written by the trainer as each shot ends. Bad lines are skipped.
    public static List<Attempt> Attempts()
    {
        var file = Path.Combine(Folder, "stats.jsonl");
        if (!File.Exists(file)) return [];
        List<Attempt> all = [];
        foreach (var line in File.ReadLines(file))
        {
            try
            {
                if (JsonNode.Parse(line) is JsonObject j && DateTime.TryParse(j["time"]?.ToString(), out var time))
                    all.Add(new Attempt(time, j["session"]?.ToString() ?? "", j["shot"]?.ToString() ?? "", Flag(j, "ok", false)));
            }
            catch (JsonException) { }
        }
        return all;
    }

    // Each shot's record, in shot order. The weakest is the lowest success rate among shots tried at least 3 times.
    public static List<ShotStat> PerShot(List<Attempt> all, string[] shots)
    {
        var stats = shots.Select(name => all.Where(a => a.Shot == name).ToList())
            .Select((tries, i) => new ShotStat(shots[i], tries.Count, tries.Count(a => a.Ok))).ToList();
        var weakest = stats.Where(s => s.Tries >= 3).OrderBy(s => s.Rate).FirstOrDefault();
        return [.. stats.Select(s => s with { Weakest = s == weakest })];
    }

    // The n shots to work on: lowest success rate first (3+ tries), then the least tried.
    public static int[] Weakest(List<ShotStat> stats, int n) =>
        [.. stats.Select((s, i) => (s, i)).OrderBy(x => x.s.Tries >= 3 ? 0 : 1).ThenBy(x => x.s.Tries >= 3 ? x.s.Rate : x.s.Tries)
            .Take(n).Select(x => x.i)];

    public static int BestStreak(IEnumerable<Attempt> attempts)
    {
        int best = 0, run = 0;
        foreach (var a in attempts) best = Math.Max(best, run = a.Ok ? run + 1 : 0);
        return best;
    }

    // The trainer's latest run: its attempts, and the clips saved while it ran.
    public static (List<Attempt> Attempts, int Clips)? LastSession(List<Attempt> all)
    {
        if (all.Count == 0) return null;
        var session = all[^1].Session;
        var mine = all.Where(a => a.Session == session).ToList();
        var (from, to) = (mine[0].Time.AddMinutes(-1), mine[^1].Time.AddMinutes(1));
        return (mine, Clips().Count(c => c.Time >= from && c.Time <= to));
    }

    public static JsonObject Load() => Read(SettingsFile); // missing or half-written: the trainer's defaults apply

    static JsonObject Read(string file)
    {
        try { return JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? []; }
        catch { return []; }
    }

    // Written to a temp file and swapped in, so the trainer never reads half a file. The swap can fail for a moment
    // while the trainer has the file open.
    public static void Save(JsonObject settings) => Write(SettingsFile, settings);

    static void Write(string file, JsonObject json)
    {
        var temp = file + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        for (var i = 0; ; i++)
        {
            try { File.Move(temp, file, true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && i < 9) { Thread.Sleep(20); }
        }
    }

    public static double Number(JsonObject s, string key, double fallback) =>
        s[key] is JsonValue v && v.TryGetValue(out double d) ? d : fallback;

    public static bool Flag(JsonObject s, string key, bool fallback) =>
        s[key] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    public static string ClipsFolder => Path.Combine(Folder, "clips");

    // The trainer saves OBS's replay buffer as clips\<time> <shot>.mp4, with a .json beside it naming the shot and result,
    // and "events": Peter's touches, the goal and the shot's start, in seconds from the start of the video. Optimiser adds
    // "favourite" and "edit" (the trim and colours), so the video itself is never changed.
    public static List<ShotClip> Clips()
    {
        if (!Directory.Exists(ClipsFolder)) return [];
        // Not the files still being written: the trainer's re-encode (.scrub.mp4) and SaveVideo's saving.tmp.mp4.
        return [.. Directory.GetFiles(ClipsFolder, "*.mp4").Where(f => !f.EndsWith(".scrub.mp4", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(f).Equals("saving.tmp.mp4", StringComparison.OrdinalIgnoreCase)).Select(file =>
        {
            var about = Read(Path.ChangeExtension(file, ".json"));
            var events = about["events"] as JsonObject;
            return new ShotClip(file, about["name"]?.ToString() ?? about["shot"]?.ToString() ?? Path.GetFileNameWithoutExtension(file),
                            about["result"]?.ToString() ?? "", File.GetLastWriteTime(file))
            {
                Favourite = Flag(about, "favourite", false),
                Edit = ReadEdit(about["edit"] as JsonObject ?? []),
                Saved = ReadEdit(about["saved_edit"] as JsonObject ?? []),
                Events = events == null ? null : new ClipEvents(Number(events, "length", 20),
                    [.. (events["touches"] as JsonArray ?? []).Select(t => t is JsonValue v && v.TryGetValue(out double d) ? d : -1).Where(t => t >= 0)],
                    events["goal"] is JsonValue g && g.TryGetValue(out double goal) ? (double?)goal : null,
                    events["shot_start"] is JsonValue st && st.TryGetValue(out double start) ? (double?)start : null),
            };
        }).OrderByDescending(c => c.Time)];
    }

    static ClipEdit ReadEdit(JsonObject e) => new(Number(e, "in", 0), Number(e, "out", 0), Number(e, "brightness", 0),
        Number(e, "contrast", 1), Number(e, "saturation", 1), Number(e, "warmth", 0));

    // Into the clip's .json, keeping what the trainer wrote. Makes one if an old clip has none.
    public static void SaveEdits(ShotClip clip)
    {
        var file = Path.ChangeExtension(clip.File, ".json");
        var about = Read(file);
        about["favourite"] = clip.Favourite;
        foreach (var (key, e) in new[] { ("edit", clip.Edit), ("saved_edit", clip.Saved) })
        {
            if (e == ClipEdit.None) about.Remove(key);
            else about[key] = new JsonObject
            {
                ["in"] = e.In, ["out"] = e.Out, ["brightness"] = e.Brightness, ["contrast"] = e.Contrast,
                ["saturation"] = e.Saturation, ["warmth"] = e.Warmth,
            };
        }
        Write(file, about);
    }

    // Renames the video and everything kept beside it, and shows the name in the list. Characters Windows doesn't allow
    // in file names are left out of the file name, and a name that's taken gets " (2)".
    public static ShotClip Rename(ShotClip clip, string name)
    {
        name = name.Trim();
        var safe = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim().TrimEnd('.');
        if (safe == "") throw new ArgumentException("Pick a name with letters or numbers in it");
        if (System.Text.RegularExpressions.Regex.IsMatch(safe, @"^(CON|PRN|AUX|NUL|COM\d|LPT\d)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            safe += " clip"; // names Windows keeps for devices
        var folder = Path.GetDirectoryName(clip.File)!;
        var file = Path.Combine(folder, safe + ".mp4");
        for (var n = 2; File.Exists(file) && !string.Equals(file, clip.File, StringComparison.OrdinalIgnoreCase); n++)
            file = Path.Combine(folder, $"{safe} ({n}).mp4");
        var json = Path.ChangeExtension(clip.File, ".json");
        var about = Read(json);
        about["name"] = name;
        Write(json, about);
        var renamed = clip with { File = file, Shot = name };
        if (file == clip.File) return renamed;
        File.Move(clip.File, file);
        File.Move(json, Path.ChangeExtension(file, ".json"));
        if (File.Exists(clip.Original)) File.Move(clip.Original, renamed.Original);
        var trainers = Path.Combine(folder, "originals", Path.GetFileName(clip.File));
        if (File.Exists(trainers)) File.Move(trainers, Path.Combine(folder, "originals", Path.GetFileName(file)));
        return renamed;
    }

    // ffmpeg, from the official build in %LOCALAPPDATA%\Programs\ffmpeg, else on PATH.
    public static string? Ffmpeg = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ffmpeg", "bin", "ffmpeg.exe") }
        .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d.Trim(), "ffmpeg.exe")))
        .FirstOrDefault(File.Exists);

    // The same look as ColourEffect's shader, through ffmpeg: eq for brightness, contrast and saturation, then warmth.
    public static List<string> EditArgs(string source, ClipEdit e, string output)
    {
        var f = System.Globalization.CultureInfo.InvariantCulture;
        List<string> args = ["-y", "-hide_banner", "-loglevel", "error"];
        if (e.In > 0) args.AddRange(["-ss", e.In.ToString(f)]);
        args.AddRange(["-i", source]);
        if (e.Out > e.In) args.AddRange(["-t", (e.Out - e.In).ToString(f)]);
        if (e with { In = 0, Out = 0 } != ClipEdit.None)
        {
            var w = e.Warmth * 255;
            args.AddRange(["-vf", string.Format(f,
                "eq=brightness={0}:contrast={1}:saturation={2},lutrgb=r='clipval+{3}':g='clipval+{4}':b='clipval+{5}',format=yuv420p",
                e.Brightness, e.Contrast, e.Saturation, w, w * 0.3, -w)]);
        }
        args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", "-g", "15", "-bf", "0", // a keyframe every 0.25 s, to scrub smoothly
                       "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", output]);
        return args;
    }

    // Writes the edit into the clip's video. The first save keeps the recording in clips\unedited, every save works from
    // that, and saving no edit puts the recording back.
    public static async Task SaveVideo(ShotClip clip)
    {
        if (clip.Edit == ClipEdit.None)
        {
            if (File.Exists(clip.Original)) File.Move(clip.Original, clip.File, true);
        }
        else
        {
            if (Ffmpeg == null) throw new InvalidOperationException("ffmpeg isn't installed");
            if (!File.Exists(clip.Original))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(clip.Original)!);
                File.Move(clip.File, clip.Original);
            }
            var temp = Path.Combine(Path.GetDirectoryName(clip.File)!, "saving.tmp.mp4");
            var start = new ProcessStartInfo(Ffmpeg) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
            foreach (var a in EditArgs(clip.Original, clip.Edit, temp)) start.ArgumentList.Add(a);
            using var ffmpeg = Process.Start(start)!;
            var errors = ffmpeg.StandardError.ReadToEndAsync();
            await ffmpeg.WaitForExitAsync();
            if (ffmpeg.ExitCode != 0)
            {
                File.Delete(temp);
                throw new InvalidOperationException((await errors).Trim() is { Length: > 0 } e ? e.Split('\n')[^1] : $"ffmpeg exit {ffmpeg.ExitCode}");
            }
            File.Move(temp, clip.File, true);
        }
        clip.Saved = clip.Edit;
        SaveEdits(clip);
    }

    // To the Recycle Bin, so a clip deleted by mistake can be got back.
    public static void Delete(ShotClip clip)
    {
        foreach (var file in new[] { clip.File, Path.ChangeExtension(clip.File, ".json"), clip.Original,
                                   Path.Combine(ClipsFolder, "originals", Path.GetFileName(clip.File)) }.Where(File.Exists))
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
    public bool Favourite { get; set; }
    public ClipEdit Edit { get; set; } = ClipEdit.None;
    public ClipEvents? Events { get; init; }
    public ClipEdit Saved { get; set; } = ClipEdit.None; // the edit that's in the video file
    // clips\originals is the trainer's: the heavy OBS recording from before it re-encodes a clip to scrub smoothly.
    public string Original => Path.Combine(Path.GetDirectoryName(File)!, "unedited", Path.GetFileName(File));
    public string Source => System.IO.File.Exists(Original) ? Original : File; // what the editor plays: the recording
}

// In and Out are seconds into the video; Out 0 means the end.
public record ClipEdit(double In, double Out, double Brightness, double Contrast, double Saturation, double Warmth)
{
    public static readonly ClipEdit None = new(0, 0, 0, 1, 1, 0);
}

public record ClipEvents(double Length, double[] Touches, double? Goal, double? ShotStart)
{
    // From just before the shot (or Peter's first touch) to just after the goal (or his last touch).
    public (double In, double Out)? Shot()
    {
        var from = ShotStart ?? (Touches.Length > 0 ? (double?)Touches.Min() : null);
        if (from == null) return null;
        var to = Goal + 2.5 ?? (Touches.Length > 0 ? Touches.Max() + 3 : Length);
        return (Math.Max(0, from.Value - 1), Math.Min(Length, to));
    }

    public (double In, double Out)? GoalOnly() => Goal is double g ? (Math.Max(0, g - 5), Math.Min(Length, g + 2)) : null;
}

public record Attempt(DateTime Time, string Session, string Shot, bool Ok);

public record ShotStat(string Shot, int Tries, int Made)
{
    public bool Weakest { get; init; }
    public double Rate => Tries == 0 ? 0 : (double)Made / Tries;
    public string Info => Tries == 0 ? "Not tried yet" : $"{Made} of {Tries}   {Rate:P0}";
}
