using System.Net.NetworkInformation;

namespace Optimiser;

// Ping, jitter and packet loss, measured only: nothing about the network is changed. One ping every 2 seconds while
// the overlay is showing or the Stats page is open, over the last minute. Neither Epic nor Activision publish a
// server address to ping, so by default it measures the route to Cloudflare's nearest server (1.1.1.1), which shows
// Wi-Fi drops and a busy line the same way; Settings takes a game server's address instead.
public static class Network
{
    public const string HostSetting = "PingHost", DefaultHost = "1.1.1.1";
    const int Window = 30; // pings kept: a minute's worth
    static readonly Queue<long?> pings = new(); // round trip in ms, null for a lost one; also the lock
    static Timer? timer;
    static string? lastHost;
    static volatile bool overlay, page;

    public static string Host => Settings.GetText(HostSetting) is { Length: > 0 } h ? h : DefaultHost;

    // Whoever needs the numbers says so; pinging stops when neither does.
    public static bool ForOverlay { set { overlay = value; Wake(); } }
    public static bool ForPage { set { page = value; Wake(); } }

    static void Wake()
    {
        if (!overlay && !page)
        {
            timer?.Dispose();
            timer = null;
            lock (pings) pings.Clear();
        }
        else timer ??= new Timer(_ => Send(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    static void Send()
    {
        long? ms = null;
        var host = Host;
        try
        {
            using var ping = new Ping(); // one each, as a slow name lookup can overlap the next send
            var reply = ping.Send(host, 1000);
            if (reply.Status == IPStatus.Success) ms = reply.RoundtripTime;
        }
        catch (PingException) { } // no network or no such host: counted as lost
        lock (pings)
        {
            if (host != lastHost) pings.Clear(); // a new address starts a fresh minute
            lastHost = host;
            pings.Enqueue(ms);
            if (pings.Count > Window) pings.Dequeue();
        }
    }

    // Null until the first ping is back.
    public static NetStats? Now()
    {
        lock (pings) return Summarise([.. pings]);
    }

    // Ping is the average round trip, jitter the average change between pings in a row (as RFC 3550 counts it, without
    // its smoothing), loss the share that got no answer within a second.
    public static NetStats? Summarise(long?[] samples)
    {
        if (samples.Length == 0) return null;
        var answered = samples.OfType<long>().ToArray();
        var loss = 100.0 * (samples.Length - answered.Length) / samples.Length;
        double? jitter = answered.Length > 1 ? answered.Zip(answered.Skip(1), (a, b) => Math.Abs(b - a)).Average() : null;
        return new(answered.Length > 0 ? answered.Average() : null, jitter, loss);
    }

    // A host name or IP address someone could type, nothing more.
    public static bool IsHost(string text) => text.Length is > 0 and <= 253 && Uri.CheckHostName(text) != UriHostNameType.Unknown;
}

public record NetStats(double? Ping, double? Jitter, double Loss);
