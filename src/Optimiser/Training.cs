using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Optimiser;

// The Rocket League shot trainer (Python and RLBot, in Documents\rl-shot-trainer). It reads settings.json before every
// shot, so the Shot Trainer page only has to write that file. Keys this page doesn't know are kept as they are.
public static class Training
{
    public static string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "rl-shot-trainer");
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

    public static JsonObject Load()
    {
        try { return JsonNode.Parse(File.ReadAllText(SettingsFile)) as JsonObject ?? []; }
        catch { return []; } // missing or half-written: the trainer's defaults apply
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

    // "retry" or "next". The trainer acts when cmd_id changes, so the same command can be sent twice in a row.
    public static void Send(string command)
    {
        var s = Load();
        s["cmd"] = command;
        s["cmd_id"] = (int)Number(s, "cmd_id", 0) + 1;
        Save(s);
    }

    // play.bat starts the game and the trainer without the trainer's old settings window, which would overwrite
    // this page's changes. Through Explorer so it runs as Peter, not as admin like Optimiser.
    public static void Start()
    {
        var play = Path.Combine(Folder, "play.bat");
        Tweaks.OpenAsYou(File.Exists(play) ? play : Path.Combine(Folder, "start.bat"));
    }
}
