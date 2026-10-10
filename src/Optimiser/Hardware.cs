using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Optimiser;

// What the app found on this PC. Tweaks use the flags at the bottom to decide whether they apply.
public record Hardware(string Cpu, int PCores, int ECores, int Threads, string[] Gpus, double RamGb,
                       string Maker, string Model, bool IsLaptop)
{
    public bool HasNvidia => Gpus.Any(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
    public bool HasDedicatedGpu => Gpus.Any(IsDedicated);
    // Intel graphics, or AMD's built-in "Radeon Graphics". MSI laptops hide it in Discrete Graphics Mode.
    public bool HasIntegratedGpu => Gpus.Any(g => !IsDedicated(g)
        && (g.Contains("Intel", StringComparison.OrdinalIgnoreCase) || g.Contains("Radeon", StringComparison.OrdinalIgnoreCase)));

    // Names like "NVIDIA GeForce RTX 3070 Ti Laptop GPU", "AMD Radeon RX 6800M", "Radeon Pro W6800" or
    // "Intel Arc A770". Plain "Intel Arc Graphics" and "AMD Radeon(TM) Graphics" are built into the CPU.
    static bool IsDedicated(string gpu) =>
        Regex.IsMatch(gpu, @"NVIDIA|Radeon (RX|Pro|VII)|Arc\(TM\) [AB]\d|Arc [AB]\d", RegexOptions.IgnoreCase);

    public bool IsHybridCpu => ECores > 0;
    public bool IsMsi => Maker.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase)
                      || Maker.StartsWith("MSI", StringComparison.OrdinalIgnoreCase);

    public static Hardware Detect()
    {
        var installed = All("Win32_PhysicalMemory", "Capacity").Sum(long.Parse);
        if (installed == 0) installed = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var (p, e) = CoreCounts();
        return new(
            Cpu: First("Win32_Processor", "Name"),
            PCores: p, ECores: e, Threads: Environment.ProcessorCount,
            Gpus: All("Win32_VideoController", "Name"),
            RamGb: Math.Round(installed / (double)(1L << 30)),
            Maker: First("Win32_ComputerSystem", "Manufacturer"),
            Model: First("Win32_ComputerSystem", "Model"),
            IsLaptop: IsPortable());
    }

    // Uses the chassis type, not "has a battery", because desktops on a USB UPS report a battery too.
    static bool IsPortable()
    {
        using var search = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
        return search.Get().Cast<ManagementBaseObject>()
            .Any(o => o["ChassisTypes"] is ushort[] types && types.Any(t => t is 8 or 9 or 10 or 14 or 30 or 31 or 32));
    }

    static string[] All(string wmiClass, string property)
    {
        using var search = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
        return search.Get().Cast<ManagementBaseObject>()
            .Select(o => o[property]?.ToString()?.Trim() ?? "")
            .Where(v => v != "")
            .ToArray();
    }

    static string First(string wmiClass, string property) => All(wmiClass, property).FirstOrDefault() ?? "Unknown";

    // Counts performance and efficiency cores. Intel 12th gen and later report a higher
    // EfficiencyClass for P-cores; on non-hybrid CPUs every core has the same class, so all count as P.
    static (int p, int e) CoreCounts()
    {
        const int RelationProcessorCore = 0;
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                return (Environment.ProcessorCount, 0);
            var classes = new List<byte>();
            for (var offset = 0; offset < length; offset += Marshal.ReadInt32(buffer, offset + 4))
                classes.Add(Marshal.ReadByte(buffer, offset + 9)); // PROCESSOR_RELATIONSHIP.EfficiencyClass
            var fastest = classes.Max();
            var p = classes.Count(c => c == fastest);
            return (p, classes.Count - p);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}
