using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;

namespace Optimiser;

// Asks Nvidia's driver download site for the newest Game Ready driver for this GPU, the same lookup as the
// drivers page on nvidia.com. Only reads: installing stays with the NVIDIA App.
public static class NvidiaDrivers
{
    const string Products = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
    const string Lookup = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php"
                        + "?func=DriverManualLookup&psid={0}&pfid={1}&osID=57&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults=1";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static string? Latest { get; private set; } // null until a check got an answer

    // Windows writes the version as 32.0.15.6636; Nvidia calls that 566.36: the last digit of the third part and the fourth.
    public static string? Installed(string windows) =>
        windows.Split('.') is [_, _, var a, var b] && a.Length > 0 && b.Length <= 4 && int.TryParse(a[^1..], out var x) && int.TryParse(b, out var y)
            ? $"{x * 100 + y / 100}.{y % 100:00}" : null;

    // Newer means a higher number: 566.36 is newer than 560.94.
    public static bool IsNewer(string latest, string installed) =>
        decimal.TryParse(latest, System.Globalization.CultureInfo.InvariantCulture, out var l)
        && decimal.TryParse(installed, System.Globalization.CultureInfo.InvariantCulture, out var i) && l > i;

    // Nvidia's product list: each GPU's name with its series (ParentID) and its own number (Value).
    public static (string Series, string Product)? Find(string xml, string gpu)
    {
        var name = gpu.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase).Trim();
        return XDocument.Parse(xml).Descendants("LookupValue")
            .Where(v => string.Equals(v.Element("Name")?.Value.Trim(), name, StringComparison.OrdinalIgnoreCase))
            .Select(v => ((string?)v.Attribute("ParentID"), v.Element("Value")?.Value))
            .Where(v => int.TryParse(v.Item1, out _) && int.TryParse(v.Item2, out _))
            .Select(v => ((string, string)?)(v.Item1!, v.Item2!)).FirstOrDefault();
    }

    public static string? Version(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("IDS", out var ids) && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0
            && ids[0].TryGetProperty("downloadInfo", out var info) && info.TryGetProperty("Version", out var v)
            && v.GetString() is { } s && IsNewer(s, "0") ? s : null; // a version number, nothing else
    }

    // Null when this GPU isn't in Nvidia's list or the site didn't answer.
    public static async Task<string?> Check(string gpu)
    {
        if (Find(await http.GetStringAsync(Products), gpu) is not { } found) return null;
        return Latest = Version(await http.GetStringAsync(string.Format(Lookup, found.Series, found.Product)));
    }
}
