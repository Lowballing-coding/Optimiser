using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Optimiser;

// An RGB app on this PC. Open is what Explorer opens for it; Lighting is how Optimiser sets its colour, if it can.
public record RgbApp(string Name, string? Open, Lighting? Lighting, string Hint)
{
    public bool IsSetting => Open?.StartsWith("ms-settings:") == true;
}

// What the RGB thread did last time round: the colour (null: handed back to each app), and for each app, null if it
// took the colour or why it didn't.
public record RgbRound(Color? Colour, IReadOnlyDictionary<Lighting, string?> Results);

// The RGB page's engine. Finds the RGB apps on this PC and shows one colour on every one that lets other programs set
// it. Each app takes its lighting back once Optimiser goes quiet, so a background thread repeats the colour every few
// seconds for as long as Optimiser runs, and the colour is saved so it comes back after a restart.
public static class Rgb
{
    public const string ColourSetting = "RgbColour"; // "#RRGGBB" while Optimiser is showing a colour
    public static readonly Lighting SteelSeriesGG = new SteelSeries(), RazerChroma = new Razer(), LogitechGHub = new Logitech();
    static readonly Lighting[] Lightings = [SteelSeriesGG, RazerChroma, LogitechGHub];

    // Start menu name, the name to show when it isn't in the Start menu, how Optimiser sets its colour, and a tip.
    const string NoWayIn = "This app doesn't let other programs set its colours, so the button opens it to change them there.";
    static readonly (string Pattern, string Name, Lighting? Lighting, string Hint)[] Known =
    [
        ("^SteelSeries (GG|Engine)", "SteelSeries GG", SteelSeriesGG,
            "Lights MSI laptop keyboards and light bars and SteelSeries gear through GameSense. If nothing changes, check "
            + "Optimiser is switched on under Engine, Apps in GG."),
        ("^Razer Synapse", "Razer Synapse", RazerChroma,
            "Lights Razer gear, and anything linked to it with Chroma Connect, through Razer Chroma. If nothing changes: "
            + "in Synapse 3, add the Chroma Connect module under Synapse, Modules, then under Connect, Apps turn on Chroma "
            + "Apps and Optimiser. In Synapse 4, turn both on under Chroma Apps in the Razer Chroma app."),
        ("^(Logitech )?G ?HUB", "Logitech G HUB", LogitechGHub,
            "Lights Logitech G gear. If nothing changes, turn on \"Allow games & applications to control my lighting\" in "
            + "G HUB's settings."),
        ("^Wootility", "Wootility", null,
            "Optimiser can't set a Wooting keyboard directly, but turn on Chroma Connect in Wootility's settings and it "
            + "follows the Razer Chroma colour."),
        ("Redragon|OemDrv", "Redragon", null,
            "This keyboard's app doesn't let other programs set its colours. Open it with the keyboard on its cable, pick a steady "
            + "effect, untick RGB, set the colour and press Apply: the keyboard keeps it after the app closes."),
        ("^Alienware Command Center", "Alienware Command Center", null, NoWayIn),
        ("^(Corsair )?iCUE", "Corsair iCUE", null, NoWayIn),
        ("^(ASUS )?Armoury Crate", "Armoury Crate", null, NoWayIn),
        ("^OpenRGB", "OpenRGB", null, NoWayIn),
        ("^SignalRGB", "SignalRGB", null, NoWayIn),
    ];

    static readonly object gate = new();
    static Color? colour; // what the user picked; null hands lighting back to each app
    static readonly AutoResetEvent wake = new(false);

    public static Color? Colour { get { lock (gate) return colour; } }
    public static RgbRound Last { get; private set; } = new(null, new Dictionary<Lighting, string?>());
    public static event Action? Changed; // a round finished; raised on the RGB thread

    public static void Start()
    {
        try
        {
            if (Settings.GetText(ColourSetting) is { } saved) colour = (Color)ColorConverter.ConvertFromString(saved);
        }
        catch { } // not a colour; start with each app's own lighting
        new Thread(Loop) { IsBackground = true, Name = "RGB" }.Start();
    }

    public static void Set(Color? c)
    {
        Settings.SetText(ColourSetting, c is { } x ? Hex(x) : null);
        lock (gate) colour = c;
        wake.Set();
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static void Loop()
    {
        var showing = new HashSet<Lighting>();
        while (true)
        {
            var c = Colour;
            var results = new Dictionary<Lighting, string?>();
            foreach (var lighting in Lightings)
            {
                try
                {
                    if (c is { } x)
                    {
                        showing.Add(lighting);
                        if ((results[lighting] = lighting.Missing) == null) lighting.Show(x);
                    }
                    else if (showing.Remove(lighting)) lighting.Release();
                }
                catch (Exception e)
                {
                    results[lighting] = e switch
                    {
                        HttpRequestException { StatusCode: { } code } => $"It turned Optimiser down (error {(int)code}).",
                        HttpRequestException or TaskCanceledException => "It isn't running, or didn't answer.",
                        _ => e.Message,
                    };
                }
            }
            var changed = c != Last.Colour || !results.SequenceEqual(Last.Results);
            Last = new(c, results);
            if (changed) Changed?.Invoke(); // not every 5 seconds, so the page isn't redrawn for nothing
            wake.WaitOne(c == null ? Timeout.Infinite : 5000); // Razer gives up after 15 seconds without a word
        }
    }

    // The RGB apps on this PC, plus Windows' own RGB settings on Windows 11 23H2 and later.
    public static List<RgbApp> Find()
    {
        List<(string, string)> startApps;
        try { startApps = StartMenu.Apps(); }
        catch { startApps = []; } // still list the apps Optimiser can reach
        var apps = Match(startApps, Lightings.Where(l => l.Missing == null));
        if (Environment.OSVersion.Version.Build >= 22631)
            apps.Add(new("Windows Dynamic Lighting", "ms-settings:personalization-lighting", null,
                "Windows' own RGB control. When it's on it can take devices over from their apps, so if colours keep "
                + "changing back, turn it off for those devices here."));
        return apps;
    }

    // Known apps found in the Start menu, plus the ones Optimiser can reach that aren't in it.
    public static List<RgbApp> Match(IEnumerable<(string Name, string AppId)> startApps, IEnumerable<Lighting> reachable)
    {
        // By the shortcut's program too, so an updater (Redragon's rewrites the keyboard's firmware) is never picked.
        var usable = startApps.Where(a => !Regex.IsMatch(a.Name + " " + Path.GetFileName(a.AppId),
                                                         "unins|update|upgrade|firmware|readme|help|manual", RegexOptions.IgnoreCase)
                                          && !a.AppId.Contains('"')).ToList();
        var apps = new List<RgbApp>();
        foreach (var known in Known)
        {
            // The program's path as well as the name: Redragon's shortcut may just say OemDrv or the keyboard's model.
            var found = usable.FirstOrDefault(a => Regex.IsMatch(a.Name, known.Pattern, RegexOptions.IgnoreCase)
                                                || Regex.IsMatch(a.AppId, known.Pattern, RegexOptions.IgnoreCase));
            if (found.Name != null) apps.Add(new(found.Name, $@"shell:AppsFolder\{found.AppId}", known.Lighting, known.Hint));
            else if (known.Lighting != null && reachable.Contains(known.Lighting)) apps.Add(new(known.Name, null, known.Lighting, known.Hint));
        }
        return apps;
    }

    // Lighting apps run small web servers on this PC. No proxy, and a short wait: they answer at once or not at all.
    static readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };

    public static string Send(HttpMethod method, string url, object? body = null)
    {
        // A plain string body, so it goes with a length: chunked uploads trip up some of these servers.
        using var request = new HttpRequestMessage(method, url)
            { Content = body == null ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        using var response = http.Send(request);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(response.Content.ReadAsStream());
        return reader.ReadToEnd();
    }

    // Only ever talk to this PC: the addresses come from files and replies other programs could write.
    public static bool IsLocal(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp && u.IsLoopback;
}

// One RGB app's way in for other programs. Show runs every few seconds while a colour is on, which also stops the
// app taking its lighting back; it throws with a reason when the colour couldn't be set.
public abstract class Lighting
{
    public abstract string? Missing { get; } // why Optimiser can't reach the app, or null when it can
    public abstract void Show(Color c);
    public abstract void Release();
}

// SteelSeries GG's GameSense: a web server whose address GG writes to coreProps.json when it starts. Optimiser signs up
// as a "game" with one event whose colour lights every key and every zone.
public class SteelSeries : Lighting
{
    const string Game = "OPTIMISER", Event = "COLOUR";
    static readonly string Data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    static readonly string[] Props = [Path.Combine(Data, @"SteelSeries\SteelSeries Engine 3\coreProps.json"),
                                      Path.Combine(Data, @"SteelSeries\GG\coreProps.json")];
    string? url; // GameSense's address, once Optimiser is signed up
    int count;   // a fresh value each time, so GG never skips an event as a repeat

    public override string? Missing => Props.Any(File.Exists) ? null : "It isn't running.";

    public override void Show(Color c)
    {
        try
        {
            if (url == null)
            {
                var props = Props.Where(File.Exists).MaxBy(File.GetLastWriteTimeUtc) ?? throw new InvalidOperationException("It isn't running.");
                var address = Address(File.ReadAllText(props)) ?? throw new InvalidOperationException("Its address isn't on this PC, so Optimiser didn't use it.");
                Rgb.Send(HttpMethod.Post, address + "game_metadata", new Dictionary<string, object>
                    { ["game"] = Game, ["game_display_name"] = "Optimiser", ["developer"] = "Optimiser", ["deinitialize_timer_length_ms"] = 30000 });
                Rgb.Send(HttpMethod.Post, address + "bind_game_event", new Dictionary<string, object>
                    { ["game"] = Game, ["event"] = Event, ["value_optional"] = true, ["handlers"] = Handlers() });
                url = address;
            }
            Rgb.Send(HttpMethod.Post, url + "game_event", new Dictionary<string, object>
            {
                ["game"] = Game, ["event"] = Event,
                ["data"] = new { value = count = count % 100 + 1, frame = new { colour = new { red = c.R, green = c.G, blue = c.B } } },
            });
        }
        catch
        {
            url = null; // sign up again next time, in case GG restarted on a new port
            throw;
        }
    }

    public override void Release()
    {
        if (url != null) Rgb.Send(HttpMethod.Post, url + "stop_game", new { game = Game });
        url = null;
    }

    // Every key and LED on per-key keyboards, and each zone of zoned devices like mice, headsets and mouse mats.
    static List<Dictionary<string, string>> Handlers()
    {
        Dictionary<string, string> Handler(string device, string zone) => new()
            { ["device-type"] = device, ["zone"] = zone, ["mode"] = "context-color", ["context-frame-key"] = "colour" };
        string[] zones = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
                          "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
                          "twenty-one", "twenty-two", "twenty-three", "twenty-four"];
        // Each zoned type by name: the catch-all "rgb-zoned-device" didn't reach an MSI laptop's light bar, which GameSense
        // lists as rgb-24-zone (Mystic Light). Mice, headsets and mouse mats are the smaller ones.
        int[] sizes = [1, 2, 3, 5, 8, 12, 17, 24];
        return [Handler("rgb-per-key-zones", "all"), .. sizes.SelectMany(n => zones.Take(n).Select(z => Handler($"rgb-{n}-zone", z)))];
    }

    // "address" in coreProps.json, only if it's on this PC. The file is in ProgramData, where any program can write.
    public static string? Address(string coreProps)
    {
        try
        {
            using var json = JsonDocument.Parse(coreProps);
            var url = $"http://{json.RootElement.GetProperty("address").GetString()}/";
            return Rgb.IsLocal(url) ? url : null;
        }
        catch { return null; }
    }
}

// Razer Chroma's web API, which comes with Synapse. A session lapses 15 seconds after its last heartbeat.
public class Razer : Lighting
{
    static readonly string[] Devices = ["keyboard", "mouse", "headset", "mousepad", "keypad", "chromalink"];
    static readonly string[] Folders = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)];
    string? session;

    public override string? Missing =>
        Folders.Any(f => Directory.Exists(Path.Combine(f, "Razer Chroma SDK"))) ? null : "Razer Chroma isn't installed with Synapse.";

    public override void Show(Color c)
    {
        List<long> results;
        try
        {
            if (session == null)
            {
                var reply = Rgb.Send(HttpMethod.Post, "http://localhost:54235/razer/chromasdk", new
                {
                    title = "Optimiser",
                    description = "One colour on all your RGB",
                    author = new { name = "Optimiser", contact = "https://github.com/Lowballing-coding/Optimiser" },
                    device_supported = Devices,
                    category = "application",
                });
                if (Result(reply) is var refused and not 0) throw new InvalidOperationException(Why(refused));
                session = Session(reply) ?? throw new InvalidOperationException("Razer Chroma gave an address that isn't on this PC.");
                Thread.Sleep(1500); // a new session ignores effects for a moment
            }
            // Every round, not once: Chroma can drop an effect, or hand the lighting to another app for a moment.
            var effect = new { effect = "CHROMA_STATIC", param = new { color = Bgr(c) } };
            results = Devices.Select(device =>
            {
                try { return Result(Rgb.Send(HttpMethod.Put, $"{session}/{device}", effect)); }
                catch (HttpRequestException e) when (e.StatusCode != null) { return NotConnected; }
            }).ToList();
            Rgb.Send(HttpMethod.Put, session + "/heartbeat");
        }
        catch
        {
            try { Release(); } // don't leave a stray session holding Chroma
            catch { session = null; }
            throw;
        }
        // A device type that isn't plugged in is turned down as a matter of course; a refusal like Chroma Apps being off
        // hits every one. The session stays, so the colour goes through the round after it's switched on.
        if (!results.Contains(0)) throw new InvalidOperationException(Why(results.FirstOrDefault(r => r is not (NotConnected or NotAvailable))));
    }

    public override void Release()
    {
        if (session != null) Rgb.Send(HttpMethod.Delete, session);
        session = null;
    }

    public static int Bgr(Color c) => c.B << 16 | c.G << 8 | c.R;

    const long NotConnected = 1167, NotAvailable = 4319, Disabled = 4309; // Chroma's RZRESULT codes

    // Chroma answers 200 with {"result": N}, 0 when it's done; replies without one (a new session) count as done.
    public static long Result(string reply)
    {
        try
        {
            using var json = JsonDocument.Parse(reply);
            return json.RootElement.TryGetProperty("result", out var r) ? r.GetInt64() : 0;
        }
        catch { return 0; }
    }

    static string Why(long result) => result switch
    {
        0 => "Razer Chroma answered, but no Razer device took the colour.",
        Disabled => "Razer Chroma turned it down: Chroma Apps is off.",
        _ => $"Razer Chroma turned it down (error {result}).",
    };

    // The session's own address from Chroma's reply, only if it's on this PC.
    public static string? Session(string reply)
    {
        try
        {
            using var json = JsonDocument.Parse(reply);
            var uri = json.RootElement.GetProperty("uri").GetString()!.TrimEnd('/');
            return Rgb.IsLocal(uri) ? uri : null;
        }
        catch { return null; }
    }
}

// Logitech's LED SDK, the DLL G HUB installs for games to light its devices. Loaded into Optimiser, so G HUB puts its
// own lighting back by itself when Optimiser closes.
public class Logitech : Lighting
{
    const string Lib = "GHubLedSdk"; // stands for G HUB's file, found by Dll()
    const int AllDevices = 7;          // LOGI_DEVICETYPE_ALL: single-colour, RGB and per-key RGB
    static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    bool started;

    // Never falls back to Windows' usual search for Lib, which looks next to Optimiser.exe first.
    static Logitech() => NativeLibrary.SetDllImportResolver(typeof(Logitech).Assembly, (name, _, _) =>
        name != Lib ? IntPtr.Zero : NativeLibrary.Load(Dll() ?? throw new DllNotFoundException("G HUB's lighting add-on for games wasn't found.")));

    public override string? Missing => Dll() != null ? null : "G HUB's lighting add-on for games wasn't found.";

    public override void Show(Color c)
    {
        if (!started)
        {
            bool ok;
            try { ok = LogiLedInitWithName("Optimiser"); }
            catch (EntryPointNotFoundException) { ok = LogiLedInit(); } // older SDKs
            if (!ok) throw new InvalidOperationException("It isn't running.");
            started = true;
            LogiLedSetTargetDevice(AllDevices);
        }
        if (!LogiLedSetLighting(Percent(c.R), Percent(c.G), Percent(c.B)))
        {
            Release(); // start over next time, in case G HUB restarted
            throw new InvalidOperationException("It didn't take the colour.");
        }
    }

    public override void Release()
    {
        if (started) LogiLedShutdown();
        started = false;
    }

    static int Percent(byte value) => (value * 100 + 127) / 255;

    // G HUB registers the SDK under this class ID for games to find. Only HKLM and only a file in Program Files are
    // trusted: both need admin rights to change, so nothing else can get its own DLL loaded into this admin app.
    static string? Dll()
    {
        var registered = Reg.Read(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\{a6519e67-7632-4375-afdf-caa889744403}\ServerBinary", "") as string;
        var gHub = Path.Combine(ProgramFiles, "LGHUB");
        return new[] { registered, Path.Combine(gHub, @"sdks\sdk_legacy_led_x64.dll"), Path.Combine(gHub, "sdk_legacy_led_x64.dll") }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(Environment.ExpandEnvironmentVariables(p!.Trim().Trim('"'))))
            .FirstOrDefault(p => InProgramFiles(p) && File.Exists(p));
    }

    public static bool InProgramFiles(string path) =>
        Path.GetFullPath(path).StartsWith(ProgramFiles.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
    static extern bool LogiLedInit();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
    static extern bool LogiLedInitWithName([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
    static extern bool LogiLedSetTargetDevice(int targetDevice);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
    static extern bool LogiLedSetLighting(int redPercentage, int greenPercentage, int bluePercentage);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern void LogiLedShutdown();
}
