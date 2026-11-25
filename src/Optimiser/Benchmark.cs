using System.Diagnostics;
using System.Text.Json;

namespace Optimiser;

// One benchmark run: the scores, how hot it got, and every switch that was on, so two runs show what changed between them.
public record BenchRun(DateTime At, int Score, int Gpu, int Cpu, double? MaxGpuTemp, string Ending, string[] On);

// A repeatable 60-second test that loads the GPU and every CPU thread at once, the way a game does: the GPU runs the
// overclock page's self-checking shader and the CPU threads a fixed integer workload. It only runs when you press the
// button, as the fans get loud. Run it, change something, run it again, and the list shows the difference.
public static class Benchmark
{
    public const string Setting = "Benchmarks";
    public const int Keep = 20;
    public static readonly TimeSpan Length = TimeSpan.FromSeconds(60);
    static CancellationTokenSource? running;

    public static bool Running => running != null;
    public static void Stop() => running?.Cancel();

    public static Task<BenchRun> Run(string[] on, Action<double> progress) => Task.Run(() =>
    {
        running = new();
        var token = running.Token;
        try
        {
            if (Stats.OnBattery()) throw new InvalidOperationException("Plug the laptop in first: on battery the scores would be much lower.");
            if (Overclock.Running) throw new InvalidOperationException("The overclock test is running.");
            double? maxTemp = null;
            var ending = "Finished";
            long cpuWork = 0;
            var watch = Stopwatch.StartNew();
            var cpu = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Factory.StartNew(() =>
            {
                long done = 0;
                while (watch.Elapsed < Length && !token.IsCancellationRequested) done += Work(50_000);
                Interlocked.Add(ref cpuWork, done);
            }, TaskCreationOptions.LongRunning)).ToArray();

            double gpu = 0;
            using var stats = new Stats();
            Verdict? Check()
            {
                progress(watch.Elapsed / Length);
                if (stats.Read().GpuTemp is { } t) maxTemp = Math.Max(maxTemp ?? 0, t);
                return token.IsCancellationRequested ? Verdict.Stopped : maxTemp >= 88 ? Verdict.TooHot : null;
            }
            try
            {
                using var stress = new GpuStress();
                var (verdict, score) = stress.Run(Length, Check);
                gpu = score;
                if (verdict != Verdict.Passed) ending = verdict switch
                {
                    Verdict.TooHot => "Stopped at 88 °C",
                    Verdict.Stopped => "Stopped",
                    _ => "The GPU made errors. If it's overclocked, go back to stock",
                };
            }
            catch (InvalidOperationException) // no Nvidia GPU: the CPU half still runs
            {
                while (watch.Elapsed < Length && !token.IsCancellationRequested) { Thread.Sleep(500); Check(); }
                ending = token.IsCancellationRequested ? "Stopped" : "CPU only: no Nvidia GPU";
            }
            running.Cancel(); // the CPU threads stop with the GPU
            Task.WaitAll(cpu);
            var seconds = watch.Elapsed.TotalSeconds;
            var (g, c) = ((int)Math.Round(gpu * 10), (int)Math.Round(cpuWork / seconds / 1e5));
            var run = new BenchRun(DateTime.Now, Combine(g, c), g, c, maxTemp, ending, on);
            if (ending == "Finished" || ending.StartsWith("CPU only")) Save(run); // a stopped run isn't comparable
            return run;
        }
        finally { running = null; }
    });

    // The overall score: the geometric mean, so a 10% gain in either half moves it the same amount.
    public static int Combine(int gpu, int cpu) => gpu <= 0 ? cpu : (int)Math.Round(Math.Sqrt((double)gpu * cpu));

    // Fixed integer maths the compiler can't skip: returns how many rounds ran.
    static long Work(int rounds)
    {
        var x = (ulong)rounds;
        for (var i = 0; i < rounds; i++) x = (x ^ (x >> 31)) * 0x9E3779B97F4A7C15 + (ulong)i;
        return x == 42 ? rounds + 1 : rounds; // uses x, so the loop stays
    }

    // Newest first.
    public static List<BenchRun> History()
    {
        try { return JsonSerializer.Deserialize<List<BenchRun>>(Settings.GetText(Setting) ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    static void Save(BenchRun run) => Settings.SetText(Setting, JsonSerializer.Serialize<List<BenchRun>>([run, .. History().Take(Keep - 1)]));

    // What changed from the run before: switches turned on and off.
    public static string Changes(BenchRun run, BenchRun? before) =>
        before == null ? "First run" : string.Join(", ", run.On.Except(before.On).Select(n => $"turned on {n}")
            .Concat(before.On.Except(run.On).Select(n => $"turned off {n}"))) is { Length: > 0 } c ? char.ToUpper(c[0]) + c[1..] : "Nothing changed";
}
