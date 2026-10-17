using System.Globalization;
using System.Runtime.InteropServices;

namespace Optimiser;

// Clock offsets on an Nvidia GPU, set through the driver's own library (nvml.dll), the supported route nvidia-smi
// uses. An offset slides the whole clock curve up, but the driver keeps the laptop's own voltage, power and heat
// limits, so it can't push more voltage or watts through the card. The driver drops offsets when Windows restarts.
public sealed class NvGpu : IDisposable
{
    readonly IntPtr device;
    public string Name { get; }
    public int TempLimit { get; } // stop a test above this

    NvGpu(IntPtr device, string name, int limit) => (this.device, Name, TempLimit) = (device, name, limit);

    // Null, with the reason, when this PC's GPU or driver doesn't allow offsets.
    public static NvGpu? Open(out string? why)
    {
        why = null;
        try
        {
            if (nvmlInit_v2() != 0) { why = "Optimiser can only overclock Nvidia graphics cards, and it didn't find one."; return null; }
            if (nvmlDeviceGetHandleByIndex_v2(0, out var d) == 0)
            {
                var name = new byte[96];
                nvmlDeviceGetName(d, name, (uint)name.Length);
                // Setting the offset it already has changes nothing, and shows whether the driver lets apps set one.
                if (nvmlDeviceGetGpcClkVfOffset(d, out var core) == 0 && nvmlDeviceSetGpcClkVfOffset(d, core) == 0
                    && nvmlDeviceGetMemClkVfOffset(d, out var memory) == 0 && nvmlDeviceSetMemClkVfOffset(d, memory) == 0)
                {
                    // Laptop GPUs aim to sit at or below 87 °C, so higher means the cooling can't keep up.
                    var limit = nvmlDeviceGetTemperatureThreshold(d, 1, out var slowdown) == 0 ? Math.Min((int)slowdown - 2, 88) : 88;
                    return new NvGpu(d, System.Text.Encoding.ASCII.GetString(name).TrimEnd('\0'), limit);
                }
            }
            nvmlShutdown();
            why = "Your Nvidia driver doesn't let apps change this GPU's clock speeds. Updating the driver from the Nvidia app "
                + "can add it. On an MSI laptop, MSI Center's Extreme Performance mode has MSI's own tested GPU overclock.";
            return null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            why = "Optimiser can only overclock Nvidia graphics cards, and this PC's driver doesn't have the part that does it.";
            return null;
        }
    }

    public (int Core, int Memory) Offsets =>
        (nvmlDeviceGetGpcClkVfOffset(device, out var c) == 0 ? c : 0, nvmlDeviceGetMemClkVfOffset(device, out var m) == 0 ? m : 0);

    public void Set((int Core, int Memory) o)
    {
        if (nvmlDeviceSetGpcClkVfOffset(device, o.Core) is var a and not 0) throw new InvalidOperationException($"The driver refused the core clock (error {a}).");
        if (nvmlDeviceSetMemClkVfOffset(device, o.Memory) is var b and not 0) throw new InvalidOperationException($"The driver refused the memory clock (error {b}).");
    }

    public int? Temp => nvmlDeviceGetTemperature(device, 0, out var t) == 0 ? (int)t : null;
    public int? Clock => nvmlDeviceGetClockInfo(device, 0, out var c) == 0 ? (int)c : null;

    public void Dispose() => nvmlShutdown();

    [DllImport("nvml.dll")] static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] static extern int nvmlShutdown();
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperatureThreshold(IntPtr device, int type, out uint celsius);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetClockInfo(IntPtr device, int clock, out uint mhz);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetGpcClkVfOffset(IntPtr device, out int offset);
    [DllImport("nvml.dll")] static extern int nvmlDeviceSetGpcClkVfOffset(IntPtr device, int offset);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetMemClkVfOffset(IntPtr device, out int offset);
    [DllImport("nvml.dll")] static extern int nvmlDeviceSetMemClkVfOffset(IntPtr device, int offset);
}

// What the Overclock page shows while a tune runs.
public record TuneProgress(string Stage, double Fraction, (int Core, int Memory) Offsets, int? Temp, int? Clock);

// Finds the highest GPU offsets that pass the stress test, in small steps, then backs off one step from the first
// failure for headroom. Anything wrong (an error, a driver reset, too hot, slower, stopped) puts the last good
// offsets back. Offsets only survive a restart if you turn on "Keep after restart", and only ones that passed.
public static class Overclock
{
    public const int CoreStep = 30, CoreCap = 180, MemoryStep = 100, MemoryCap = 800;
    static readonly TimeSpan Warmup = TimeSpan.FromSeconds(20), Baseline = TimeSpan.FromSeconds(30), Step = TimeSpan.FromSeconds(45), Final = TimeSpan.FromMinutes(2);
    const string TestingSetting = "OverclockTesting", CrashedSetting = "OverclockCrashed", SavedSetting = "OverclockSaved";
    public const string KeepSetting = "OverclockKeep";

    static CancellationTokenSource? running;
    public static bool Running => running != null;

    // Offsets a test was running when Optimiser last stopped without finishing it, which usually means the PC froze.
    public static (int Core, int Memory)? Crashed => Parse(Settings.GetText(CrashedSetting));
    public static (int Core, int Memory)? Saved => Parse(Settings.GetText(SavedSetting));

    // At startup: after an unfinished test, go back to stock; otherwise put back saved offsets if you asked to keep them.
    public static void Start()
    {
        try
        {
            if (Settings.GetText(TestingSetting) is { } unfinished)
            {
                Settings.SetText(CrashedSetting, unfinished);
                Settings.SetText(TestingSetting, null);
                using var gpu = NvGpu.Open(out _);
                gpu?.Set((0, 0));
            }
            else if (Settings.Get(KeepSetting, false) && Saved is { } saved)
            {
                using var gpu = NvGpu.Open(out _);
                gpu?.Set(saved);
            }
        }
        catch { } // the GPU stays at whatever the driver has, which after a restart is stock
    }

    public static void Stop() => running?.Cancel();

    // Closing Optimiser mid-test: stop it and wait for the GPU to be put back.
    public static void Shutdown()
    {
        Stop();
        SpinWait.SpinUntil(() => running == null, TimeSpan.FromSeconds(5));
    }

    // Puts the GPU back to Nvidia's own clocks and forgets saved offsets.
    public static void Reset()
    {
        using var gpu = NvGpu.Open(out var why) ?? throw new InvalidOperationException(why);
        gpu.Set((0, 0));
        Settings.SetText(SavedSetting, null);
    }

    // Takes up to about 15 minutes. Returns what to tell you; the GPU is left on offsets that passed, or stock.
    public static Task<string> Tune(Action<TuneProgress> report)
    {
        if (running != null) throw new InvalidOperationException("A test is already running.");
        var cancel = running = new CancellationTokenSource();
        return Task.Run(() =>
        {
            try { return TuneOn(report, cancel.Token); }
            finally { running = null; }
        });
    }

    static string TuneOn(Action<TuneProgress> report, CancellationToken stop)
    {
        using var gpu = NvGpu.Open(out var why) ?? throw new InvalidOperationException(why);
        if (Stats.OnBattery()) return "Plug your laptop in first. On battery the GPU runs slower, so the test wouldn't find its real limit.";
        var stress = new GpuStress();
        var before = gpu.Offsets;
        var (coreCap, memoryCap) = (CoreCap, MemoryCap);
        if (Crashed is { } c)
        {
            // ponytail: a freeze during the memory steps also lowers the core cap; fine, it only errs on the safe side
            if (c.Core > 0) coreCap = Math.Min(coreCap, c.Core - CoreStep);
            if (c.Memory > 0) memoryCap = Math.Min(memoryCap, c.Memory - MemoryStep);
        }
        var steps = coreCap / CoreStep + memoryCap / MemoryStep + 3;
        var done = 0;
        var (temps, clocks) = (new List<int>(), new List<int>());

        (Verdict Verdict, double Score) Test((int Core, int Memory) o, string stage, TimeSpan time)
        {
            Settings.SetText(TestingSetting, $"{o.Core},{o.Memory}");
            gpu.Set(o);
            var fraction = (double)done++ / steps;
            temps.Clear();
            clocks.Clear();
            var result = stress.Run(time, () =>
            {
                var (t, mhz) = (gpu.Temp, gpu.Clock);
                if (t is { } tv) temps.Add(tv);
                if (mhz is { } cv) clocks.Add(cv);
                report(new(stage, fraction, o, t, mhz));
                return stop.IsCancellationRequested ? Verdict.Stopped : t >= gpu.TempLimit ? Verdict.TooHot : null;
            });
            if (result.Verdict == Verdict.Crashed)
            {
                // The driver reset the GPU, which ends the test's connection to it. Give it a moment, then reconnect.
                stress.Dispose();
                gpu.Set((0, 0));
                Thread.Sleep(3000);
                stress = new GpuStress();
            }
            return result;
        }

        double stockScore = 0;
        try
        {
            // A cold GPU boosts higher for its first seconds, which would make every later step look slower.
            var (verdict, _) = Test((0, 0), "Warming up the GPU", Warmup);
            if (verdict == Verdict.Passed) (verdict, stockScore) = Test((0, 0), "Testing at Nvidia's own clocks", Baseline);
            if (verdict != Verdict.Passed) return Ended(gpu, before, verdict, "at Nvidia's own clocks");
            var stockClock = Average(clocks);

            // Each step has to pass and be no slower than the best so far (memory that's too fast starts
            // correcting its own errors, which shows up as lost speed before it shows up as errors).
            var best = (Core: 0, Memory: 0);
            var bestScore = stockScore;
            for (var core = CoreStep; core <= coreCap; core += CoreStep)
            {
                (verdict, var score) = Test((core, 0), $"Testing the core at +{core} MHz", Step);
                if (verdict is Verdict.TooHot or Verdict.Stopped) return Ended(gpu, before, verdict, $"at +{core} MHz");
                if (verdict is Verdict.Errors or Verdict.Crashed) { best.Core = Math.Max(0, best.Core - CoreStep); break; }
                if (score < bestScore * 0.97) break;
                (best.Core, bestScore) = (core, Math.Max(bestScore, score));
            }
            for (var memory = MemoryStep; memory <= memoryCap; memory += MemoryStep)
            {
                (verdict, var score) = Test((best.Core, memory), $"Testing the memory at +{memory} MHz", Step);
                if (verdict is Verdict.TooHot or Verdict.Stopped) return Ended(gpu, before, verdict, $"at +{memory} MHz");
                if (verdict is Verdict.Errors or Verdict.Crashed) { best.Memory = Math.Max(0, best.Memory - MemoryStep); break; }
                if (score < bestScore * 0.97) break;
                (best.Memory, bestScore) = (memory, Math.Max(bestScore, score));
            }

            if (best == (0, 0)) return Ended(gpu, (0, 0), Verdict.Slower, "");
            (verdict, var finalScore) = Test(best, "Checking the result for two minutes", Final);
            if (verdict is not Verdict.Passed) return Ended(gpu, verdict is Verdict.Stopped or Verdict.TooHot ? before : (0, 0), verdict, "in the final check");
            var gain = (finalScore / stockScore - 1) * 100;
            if (gain < 1) return Ended(gpu, (0, 0), Verdict.Slower, "");

            Settings.SetText(SavedSetting, $"{best.Core},{best.Memory}");
            Settings.SetText(CrashedSetting, null);
            var clock = Average(clocks) is { } fc && stockClock is { } sc ? $" The GPU averaged {fc:0} MHz in the test, up from {sc:0} MHz." : "";
            return $"Found a stable overclock: +{best.Core} MHz core and +{best.Memory} MHz memory, "
                 + $"{gain:0}% faster in Optimiser's test.{clock} Peak temperature {temps.DefaultIfEmpty().Max()} °C.";
        }
        catch
        {
            Restore(gpu, before);
            throw;
        }
        finally
        {
            stress.Dispose();
            Settings.SetText(TestingSetting, null);
        }
    }

    static string Ended(NvGpu gpu, (int, int) back, Verdict verdict, string where)
    {
        Restore(gpu, back);
        var now = back == (0, 0) ? "Your GPU is back on Nvidia's own clocks." : "Your GPU is back where it was.";
        return verdict switch
        {
            Verdict.Stopped => $"Stopped. {now}",
            Verdict.TooHot => $"Stopped {where} because the GPU reached {gpu.TempLimit} °C. {now} Let the laptop cool, raise it for airflow, "
                            + "or turn the fans up in MSI Center, then try again.",
            Verdict.Slower => "Overclocking didn't make this GPU any faster, most likely because the laptop's power limit is already "
                            + "holding it back. Your GPU is on Nvidia's own clocks, which is the best setting for it.",
            Verdict.Errors or Verdict.Crashed when where.StartsWith("at Nvidia") =>
                $"The test found errors {where}, before any overclock. {now} That can mean a driver problem or a GPU running too hot.",
            _ => $"The test found errors {where}. {now} Nothing was saved.",
        };
    }

    static void Restore(NvGpu gpu, (int, int) offsets)
    {
        try { gpu.Set(offsets); }
        catch { gpu.Set((0, 0)); }
    }

    static double? Average(List<int> values) => values.Count > 0 ? values.Average() : null;

    public static (int Core, int Memory)? Parse(string? text) =>
        text?.Split(',') is [var a, var b] && int.TryParse(a, CultureInfo.InvariantCulture, out var core)
            && int.TryParse(b, CultureInfo.InvariantCulture, out var memory) ? (core, memory) : null;
}
