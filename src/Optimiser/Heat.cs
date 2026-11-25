using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Optimiser;

// One ACPI thermal zone: Windows' own temperature sensors, which on laptops sit by the CPU, the chipset or the charger.
public record Zone(string Name, double Celsius);

// A moment the hottest sensor passed the spike line, with what was using the CPU then.
public record Spike(DateTime At, string Zone, double Celsius, double CpuPercent, string[] Top);

// The hottest reading of each day, for the weekly report.
public record HeatDay(DateTime Day, double Max);

// Watches Windows' thermal zones every 10 seconds, without waking the RTX GPU, and logs the processes running whenever
// one passes 90 °C, so a spike at idle can be traced to what caused it. Also reads MSI's fan speeds.
public static class Heat
{
    public const string WatchSetting = "HeatWatchdog", SpikesSetting = "HeatSpikes", DaysSetting = "HeatDays";
    public const double SpikeAt = 90;
    public const int KeepSpikes = 50, KeepDays = 28;
    static readonly TimeSpan Every = TimeSpan.FromSeconds(10), Quiet = TimeSpan.FromMinutes(2);
    static Timer? timer;
    static DateTime lastSpike;
    static DateTime noZonesUntil; // after an empty read, ask again in 5 minutes, as a zone can read nonsense after sleep

    public static List<Zone> Latest { get; private set; } = [];

    public static void Start() => timer = new Timer(_ => Tick(), null, TimeSpan.Zero, Every);

    static void Tick()
    {
        try
        {
            Latest = Zones();
            if (Latest.Count == 0 || !Settings.Get(WatchSetting, true)) return;
            var hottest = Latest.MaxBy(z => z.Celsius)!;
            RecordDay(hottest.Celsius);
            // ponytail: one log per hot spell (2 min apart); a rolling log of every reading if a spell needs more detail
            if (hottest.Celsius < SpikeAt || DateTime.Now - lastSpike < Quiet) return;
            lastSpike = DateTime.Now;
            var (cpu, top) = TopProcesses(TimeSpan.FromSeconds(2));
            Save(new Spike(DateTime.Now, hottest.Name, Math.Round(hottest.Celsius), Math.Round(cpu), top));
        }
        catch { } // a watchdog that crashes the app would be worse than a missed spike
    }

    // Every thermal zone Windows reports, by its short name (TZ00 and so on). Empty on PCs without any.
    public static List<Zone> Zones()
    {
        if (DateTime.Now < noZonesUntil) return [];
        try
        {
            using var search = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            var zones = search.Get().Cast<ManagementObject>()
                .Select(z => new Zone(ZoneName((string)z["InstanceName"]), Math.Round(Convert.ToDouble(z["CurrentTemperature"]) / 10 - 273.15, 1)))
                .Where(z => z.Celsius is > 15 and < 115) // anything else is a sensor reading nonsense
                .ToList();
            if (zones.Count == 0) noZonesUntil = DateTime.Now.AddMinutes(5);
            return zones;
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException)
        {
            noZonesUntil = DateTime.Now.AddMinutes(5);
            return [];
        }
    }

    // "ACPI\ThermalZone\TZ00_0" reads as TZ00.
    public static string ZoneName(string instance) => instance.Split('\\')[^1] is var last && last.EndsWith("_0") ? last[..^2] : last;

    public static double? Hottest() => Latest.Count == 0 ? null : Latest.Max(z => z.Celsius);

    // Share of the whole CPU each process used over the wait, busiest five, as "name 12%", and the total.
    public static (double Cpu, string[] Top) TopProcesses(TimeSpan wait)
    {
        static Dictionary<int, (string Name, TimeSpan Time)> Times()
        {
            var times = new Dictionary<int, (string, TimeSpan)>();
            foreach (var p in Process.GetProcesses())
                using (p)
                    try { times[p.Id] = (p.ProcessName, p.TotalProcessorTime); }
                    catch { } // protected processes and ones that just closed
            return times;
        }
        var before = Times();
        Thread.Sleep(wait);
        var used = Times().Where(a => before.ContainsKey(a.Key))
            .Select(a => (a.Value.Name, Percent: 100 * (a.Value.Time - before[a.Key].Time).TotalMilliseconds / wait.TotalMilliseconds / Environment.ProcessorCount))
            .Where(u => u.Percent > 0)
            .ToList();
        return (Math.Min(100, used.Sum(u => u.Percent)), [.. used.OrderByDescending(u => u.Percent).Take(5).Select(u => $"{u.Name} {u.Percent:0.#}%")]);
    }

    // Newest first, kept in HKLM with the other settings.
    public static List<Spike> Spikes() => Load<Spike>(SpikesSetting);
    public static List<HeatDay> Days() => Load<HeatDay>(DaysSetting);

    static void Save(Spike spike) => Settings.SetText(SpikesSetting, JsonSerializer.Serialize<List<Spike>>([spike, .. Spikes().Take(KeepSpikes - 1)]));

    public static void ClearSpikes() => Settings.SetText(SpikesSetting, null);

    // Only written when today's highest goes up, which after the first warm hour is rare.
    static void RecordDay(double celsius)
    {
        var days = Days();
        if (days.FirstOrDefault() is { } today && today.Day == DateTime.Today)
        {
            if (celsius <= today.Max) return;
            days[0] = today with { Max = celsius };
        }
        else days.Insert(0, new(DateTime.Today, celsius));
        Settings.SetText(DaysSetting, JsonSerializer.Serialize(days.Take(KeepDays).ToList()));
    }

    static List<T> Load<T>(string setting)
    {
        try { return JsonSerializer.Deserialize<List<T>>(Settings.GetText(setting) ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    // MSI's fan speeds, through the same ACPI method MSI Center reads (MSI_ACPI.Get_Fan, documented with Linux's
    // msi-wmi-platform driver). Read only: nothing is written to the fans or the embedded controller. Null on PCs without it.
    static readonly object fanLock = new(); // the firmware method isn't safe to call twice at once
    static bool noFans;

    public static int[]? FanRpm()
    {
        lock (fanLock)
        {
            if (noFans) return null;
            try
            {
                using var acpi = new ManagementClass(@"root\WMI", "MSI_ACPI", null);
                using var instance = acpi.GetInstances().Cast<ManagementObject>().FirstOrDefault();
                if (instance == null) throw new ManagementException();
                using var package = new ManagementClass(@"root\WMI", "Package_32", null);
                using var data = package.CreateInstance();
                var bytes = data.Properties.Cast<PropertyData>().First(p => p.IsArray).Name;
                data[bytes] = new byte[32]; // byte 0 = 0: the fan speed readings
                using var args = instance.GetMethodParameters("Get_Fan");
                args["Data"] = data;
                using var result = instance.InvokeMethod("Get_Fan", args, null);
                return ParseFans((byte[])((ManagementBaseObject)result["Data"])[bytes]);
            }
            catch (Exception e) when (e is ManagementException or COMException or InvalidOperationException or InvalidCastException or NullReferenceException)
            {
                noFans = true;
                return null;
            }
        }
    }

    // Byte 0 is 0 on failure; then up to four 16-bit big-endian readings, each 480,000 / RPM. Two fans on MSI laptops.
    public static int[]? ParseFans(byte[] b)
    {
        if (b.Length < 5 || b[0] == 0) return null;
        return [.. Enumerable.Range(0, 2).Select(i => (b[1 + 2 * i] << 8 | b[2 + 2 * i]) is var raw and > 0 ? 480000 / raw : 0)];
    }

    // The fans lag behind the heat when the hottest sensor climbed 5 °C or more over the samples while the fastest fan
    // didn't speed up. Samples are oldest first, a couple of seconds apart.
    public static bool FansLag(IReadOnlyList<(double Temp, int Rpm)> samples) =>
        samples.Count >= 5 && samples[^1].Temp >= 80 && samples[^1].Temp - samples[0].Temp >= 5 && samples[^1].Rpm <= samples[0].Rpm + 100;
}

// The quiet / cool profile: on the desktop the CPU's turbo boost is switched off in the active power plan and Windows'
// power mode goes to Best power efficiency. Both are put back the moment a game from the list starts, when the switch
// goes off, and when Optimiser closes. What was there before is saved first, so a crash still gets it put back.
public static class QuietProfile
{
    public const string Setting = "QuietDesktop", SavedSetting = "QuietBefore";
    static readonly Guid Processor = new("54533251-82be-4824-96c1-47b60b740d00"), BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7"),
        Efficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    static readonly object gate = new();

    public static bool Capped => Settings.GetText(SavedSetting) != null;

    // The switch: on caps now unless a game is running; off puts everything back.
    public static void Switch(bool on)
    {
        Settings.Set(Setting, on);
        if (on) App.Watcher?.ApplyRates();
        else Lift();
    }

    public static void Cap()
    {
        lock (gate)
        {
            if (Capped || ActiveScheme() is not { } scheme) return;
            var (sub, boost) = (Processor, BoostMode);
            if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref boost, out var ac) != 0
                || PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref boost, out var dc) != 0) return;
            PowerGetEffectiveOverlayScheme(out var mode);
            Settings.SetText(SavedSetting, $"{scheme}|{ac}|{dc}|{mode}"); // first, so a crash part-way still puts it back
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref boost, 0); // 0 = boost off
            PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref boost, 0);
            PowerSetActiveScheme(IntPtr.Zero, ref scheme); // makes the plan's new values take effect
            PowerSetActiveOverlayScheme(Efficiency);
        }
    }

    public static void Lift()
    {
        lock (gate)
        {
            if (Settings.GetText(SavedSetting)?.Split('|') is not [var s, var a, var d, var m]) return;
            if (Guid.TryParse(s, out var scheme) && uint.TryParse(a, out var ac) && uint.TryParse(d, out var dc))
            {
                var (sub, boost) = (Processor, BoostMode);
                PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref boost, ac);
                PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref boost, dc);
                if (ActiveScheme() == scheme) PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            }
            // Only if it's still the one Optimiser picked: a game's Best performance or your own choice since then stays.
            if (Guid.TryParse(m, out var mode) && PowerGetEffectiveOverlayScheme(out var now) == 0 && now == Efficiency)
                PowerSetActiveOverlayScheme(mode);
            Settings.SetText(SavedSetting, null);
        }
    }

    static Guid? ActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
    }

    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid mode);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid mode);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);
}
