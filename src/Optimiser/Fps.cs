using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Optimiser;

// One play of one game, as the Stats page shows it. Seconds counts time played: in front and drawing. Chart is the FPS
// of each second played, Step seconds to a point (at most 600 points). A number is null when there weren't enough frames.
public record FpsSession(string Game, DateTime Start, int Seconds, double? Average, int? High, double? Low1, double? Low01,
    bool Profile, int[] Chart, int Step);

// Records the frame rate of every game on the Games list while it runs, window open or not. Windows logs an event for
// each frame a program shows (ETW, which PresentMon and CapFrameX read too): nothing is loaded into the game, its process
// isn't opened and the GPU isn't woken. Frames only count while the game is the window in front. When a game closes,
// its session is summed up and kept with the last 20.
public static class Fps
{
    // Windows keeps a session running until it's stopped by name, and names are machine-wide: one per signed-in user.
    public static readonly string SessionName = "Optimiser FPS " + Process.GetCurrentProcess().SessionId;
    const string Setting = "FpsSessions";
    public const int Keep = 20, MinSeconds = 10;
    const ulong RuntimeKeywords = 0x8000000000000002; // DXGI's and D3D9's "Events" keyword plus their analytic channel
    public static readonly FrameCounter Counter = new(Stopwatch.Frequency);
    static readonly Dictionary<string, Play> plays = []; // by game name; also the lock
    static Etw? etw;
    static bool profileOn;

    public static string? Error { get; private set; } // why frames can't be counted, if they can't

    sealed class Play
    {
        public readonly FrameStats Stats = new();
        public readonly DateTime Start = DateTime.Now;
        public uint[] Pids = [];
        public double? Live; // FPS over the last second, in front or not; null before the first frames
        public long LiveAt, Frames, ProfileFrames;
        public bool InFront;
        public int FrontTicks; // ticks in a row it's been in front
    }

    // About once a second, from App: hands each game's new frames to its stats, from whichever of its processes drew the
    // most (a launcher in the game's folder can be a second process that draws a few).
    public static void Tick() => Tick(Foreground());

    public static void Tick(uint foreground)
    {
        lock (plays)
        {
            if (plays.Count > 0 && etw?.Finished.IsCompleted == true) Start(); // stopped from outside
            foreach (var p in plays.Values)
            {
                var ms = p.Pids.Select(Counter.Drain).MaxBy(m => m.Count) ?? [];
                var now = Stopwatch.GetTimestamp();
                if (ms.Count > 0) (p.Live, p.LiveAt) = (1000 / ms.Average(), now);
                else if (p.Live != null && now - p.LiveAt > Stopwatch.Frequency * 5 / 2) p.Live = 0; // frames arrive about once a second, so one tick can miss them
                p.InFront = p.Pids.Contains(foreground);
                p.FrontTicks = p.InFront ? p.FrontTicks + 1 : 0;
                // ponytail: frames arrive up to 2 s late, so they only count once the game has been in front for the 2 s
                // before this tick too; that loses ~2 s per alt-tab. Per-frame times against foreground changes if it matters.
                if (p.FrontTicks < 3) continue;
                foreach (var m in ms) p.Stats.Add(m);
                p.Frames += ms.Count;
                if (profileOn) p.ProfileFrames += ms.Count;
            }
        }
    }

    // The games on the list that are running now, from GameWatcher, on the UI thread. Counts frames while any runs.
    // Returns the sessions of games that closed, if they were played long enough to keep.
    public static List<FpsSession> Update(Dictionary<uint, Game> playing, bool profileOn)
    {
        lock (plays)
        {
            Fps.profileOn = profileOn;
            var byGame = playing.GroupBy(p => p.Value.Name).ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToArray());
            foreach (var (game, pids) in byGame)
            {
                if (!plays.TryGetValue(game, out var p)) plays[game] = p = new();
                p.Pids = pids;
            }
            var closed = plays.Where(p => !byGame.ContainsKey(p.Key)).ToList();
            foreach (var (game, _) in closed) plays.Remove(game);
            uint[] all = [.. playing.Keys];
            foreach (var pid in Counter.Pids.Except(all)) Counter.Forget(pid);
            Counter.Pids = all;
            if (all.Length == 0) Stop();
            else if (etw == null || etw.Finished.IsCompleted) Start();
            return [.. closed.Select(c => Summarise(c.Key, c.Value)).Where(s => s.Seconds >= MinSeconds)];
        }
    }

    static void Start()
    {
        etw?.Dispose();
        try
        {
            etw = new Etw(SessionName, Counter.Handle);
            // Windows 11's kernel can be asked for its present events alone (Base | Present); Windows 10 only has Base.
            var kernel = Environment.OSVersion.Version.Build >= 22000 ? 0x8000001UL : 0x1;
            uint[] errors = [etw.Enable(FrameCounter.DxgKrnl, kernel, 171, 168),
                             etw.Enable(FrameCounter.Dxgi, RuntimeKeywords, 42, 43, 55, 56),
                             etw.Enable(FrameCounter.D3d9, RuntimeKeywords, 1, 2)];
            // Any one still counts most games. 1450 = 8 other sessions (overlays, recorders) already have that one on.
            if (errors.All(e => e != 0)) throw new Win32Exception((int)errors[0]);
            Error = null;
        }
        catch (Exception e)
        {
            etw?.Dispose();
            etw = null;
            Error = e.Message;
        }
    }

    static void Stop()
    {
        etw?.Dispose();
        (etw, Error) = (null, null);
    }

    // The game being played now: its session so far, its FPS over the last second, and whether it's the window in front.
    // Null when no game on the list runs.
    public static (FpsSession Session, double? Current, bool InFront)? Now()
    {
        lock (plays)
        {
            if (plays.Count == 0) return null;
            var (game, p) = plays.MaxBy(x => (x.Value.InFront, x.Value.Frames));
            return (Summarise(game, p), p.Live, p.InFront);
        }
    }

    static FpsSession Summarise(string game, Play p)
    {
        var s = p.Stats.Summary();
        return new(game, p.Start, s.Seconds, s.Average, s.High, s.OnePercentLow, s.PointOnePercentLow,
            p.ProfileFrames * 2 > p.Frames, [.. s.Points.Select(v => (int)Math.Round(v))], s.SecondsPerPoint);
    }

    // Newest first. Kept with Optimiser's other settings in HKLM, like the backup, so only admins can change it.
    public static List<FpsSession> History()
    {
        try { return JsonSerializer.Deserialize<List<FpsSession>>(Settings.GetText(Setting) ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    public static void Save(FpsSession session) =>
        Settings.SetText(Setting, JsonSerializer.Serialize<List<FpsSession>>([session, .. History().Take(Keep - 1)]));

    static uint Foreground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
