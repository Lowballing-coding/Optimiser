using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Optimiser;

public record Release(Version Version, string Url, long Size, string? Sha256, string Notes);

// Checks GitHub for a newer build and installs it in place of the running exe. Nothing installs on its own.
public static class Updates
{
    const string LatestRelease = "https://api.github.com/repos/Lowballing-coding/Optimiser/releases/tags/latest";
    const string Downloads = "https://github.com/Lowballing-coding/Optimiser/releases/download/";
    public const string AutoCheckSetting = "CheckForUpdates";

    public static readonly Version Current = Trim(Assembly.GetExecutingAssembly().GetName().Version!);

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    static Updates() => http.DefaultRequestHeaders.UserAgent.ParseAdd($"Optimiser/{Current}"); // GitHub requires one

    // A release newer than this build, or null when this is the latest.
    public static async Task<Release?> Check() => Parse(await http.GetStringAsync(LatestRelease), Current);

    // CI names each release "Optimiser 1.0.<build>" and uses the merge commit's message as its notes.
    public static Release? Parse(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var number = Regex.Match(root.GetProperty("name").GetString() ?? "", @"\d+\.\d+\.\d+");
        if (!number.Success) return null;
        var version = Version.Parse(number.Value);
        if (version <= current) return null;

        var asset = root.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == "Optimiser.exe");
        if (asset.ValueKind != JsonValueKind.Object) return null;
        var url = asset.GetProperty("browser_download_url").GetString() ?? "";
        if (!url.StartsWith(Downloads)) return null; // only ever this repo's own releases
        var digest = asset.TryGetProperty("digest", out var d) && d.GetString() is { } s && s.StartsWith("sha256:") ? s[7..] : null;
        var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var notes = string.Join("\n", body.Split('\n').Select(l => l.Trim()).Where(l => l != "" && !l.StartsWith("Merge pull request")));
        return new Release(version, url, asset.GetProperty("size").GetInt64(), digest, notes);
    }

    // Downloads next to the running exe, swaps the files (Windows lets a running exe be renamed, just not
    // overwritten) and starts the new one, which closes this one. The leftover .old file goes on the next start.
    public static async Task Install(Release release, IProgress<int> progress)
    {
        var exe = Environment.ProcessPath!;
        var download = exe + ".download";
        using (var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var from = await response.Content.ReadAsStreamAsync();
            await using var to = File.Create(download);
            var buffer = new byte[1 << 16];
            long done = 0;
            for (int n; (n = await from.ReadAsync(buffer)) > 0;)
            {
                await to.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                progress.Report((int)(100 * done / Math.Max(release.Size, 1)));
            }
        }

        bool intact;
        using (var file = File.OpenRead(download))
            intact = file.Length == release.Size
                     && (release.Sha256 == null || Convert.ToHexString(SHA256.HashData(file)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase));
        if (!intact)
        {
            File.Delete(download);
            throw new InvalidDataException("the download was incomplete or damaged, so nothing was changed. Try again.");
        }

        var old = exe + ".old";
        File.Delete(old);
        File.Move(exe, old);
        try { File.Move(download, exe); }
        catch
        {
            File.Move(old, exe);
            throw;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose();
    }

    public static void CleanUp()
    {
        foreach (var leftover in new[] { ".old", ".download" })
            try { File.Delete(Environment.ProcessPath + leftover); }
            catch { } // the old copy may still be closing; next start gets it
    }

    static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
