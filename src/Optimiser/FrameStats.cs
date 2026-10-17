namespace Optimiser;

// FPS numbers for one game session, fed one frame time at a time (ms between this present and the one before).
// Memory is fixed (about 120 KB) however long the session runs. Add and Summary may be called from different threads.
public sealed class FrameStats
{
    public const double PauseMs = 1000;  // a longer gap is alt-tab, a loading freeze or a minimised game: left out entirely
    public const int MaxPoints = 600;    // chart points kept; when full, neighbours merge and each point covers twice the time
    const double MinMs = 0.05;           // frame times histogram: 0.1%-wide bins from 0.05 ms (20,000 FPS) up to PauseMs
    static readonly double LogStep = Math.Log(1.001);
    static readonly int Bins = (int)(Math.Log(PauseMs / MinMs) / LogStep) + 1;

    readonly Lock gate = new();
    readonly int[] count = new int[Bins];
    readonly double[] total = new double[Bins];
    readonly List<float> points = [];
    int frames, thisSecond, high = -1, last = -1, merged, mergedSeconds, span = 1;
    double elapsed, secondEnd = 1000;

    public void Add(double ms)
    {
        if (!(ms > 0) || ms > PauseMs) return; // zero, negative, NaN, or a pause
        lock (gate)
        {
            var b = (int)Math.Max(0, Math.Log(ms / MinMs) / LogStep);
            count[b]++;
            total[b] += ms;
            frames++;
            thisSecond++; // a frame belongs to the second it started in
            elapsed += ms;
            while (elapsed >= secondEnd) CloseSecond();
        }
    }

    void CloseSecond()
    {
        last = thisSecond;
        high = Math.Max(high, thisSecond);
        thisSecond = 0;
        secondEnd += 1000;
        merged += last;
        if (++mergedSeconds < span) return;
        points.Add((float)merged / span);
        merged = mergedSeconds = 0;
        if (points.Count < MaxPoints) return;
        for (var i = 0; i < points.Count / 2; i++) points[i] = (points[2 * i] + points[2 * i + 1]) / 2;
        points.RemoveRange(points.Count / 2, points.Count / 2);
        span *= 2;
    }

    // Average of the slowest k frames as FPS: their total time, not the mean of their FPS (same as CapFrameX "x% low average").
    double Low(int k)
    {
        double ms = 0;
        for (int b = Bins - 1, need = k; need > 0; b--)
        {
            var take = Math.Min(count[b], need);
            ms += take == count[b] ? total[b] : total[b] / count[b] * take; // only the bin the cut falls in is approximated
            need -= take;
        }
        return 1000.0 * k / ms;
    }

    public FrameSummary Summary()
    {
        lock (gate)
        {
            var started = high >= 0; // nothing shows until the first full second
            return new(started ? frames * 1000.0 / elapsed : null,
                       started ? high : null,
                       frames >= 100 ? Low(frames / 100) : null,
                       frames >= 1000 ? Low(frames / 1000) : null,
                       started ? last : null,
                       frames, (int)(elapsed / 1000), [.. points], span);
        }
    }

    // How a number reads on screen: whole FPS, or a dash while there isn't enough to say.
    public static string Show(double? fps) => fps is { } f ? Math.Round(f, MidpointRounding.AwayFromZero).ToString("0") : "–";
}

public sealed record FrameSummary(double? Average, int? High, double? OnePercentLow, double? PointOnePercentLow,
                                  int? Last, int Frames, int Seconds, float[] Points, int SecondsPerPoint);
