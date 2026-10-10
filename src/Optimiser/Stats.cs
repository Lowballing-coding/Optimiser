using System.Runtime.InteropServices;

namespace Optimiser;

// One second's worth of numbers for the Stats page. GPU values are null when there's no Nvidia driver.
public record Reading(
    double? GpuTemp, double? GpuLoad, double? GpuClock, double? GpuMaxClock, double? GpuWatts, double? GpuWattLimit,
    double? VramUsedGb, double? VramTotalGb, double CpuLoad, double RamUsedGb, double RamTotalGb,
    bool HasBattery, bool OnBattery, int? BatteryPercent);

// Reads live usage. The GPU goes through Nvidia's own driver library (nvml.dll), which needs no extra drivers.
// Reading it keeps a laptop's RTX GPU awake, so the Stats page only polls while it's on screen.
public sealed class Stats : IDisposable
{
    readonly IntPtr gpu;
    readonly bool hasGpu;
    long idle, kernel, user;

    public Stats()
    {
        try
        {
            hasGpu = nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out gpu) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            hasGpu = false; // no Nvidia driver
        }
        CpuLoad(); // first call only sets the baseline
    }

    public Reading Read()
    {
        double? temp = null, load = null, clock = null, maxClock = null, watts = null, limit = null, vramUsed = null, vramTotal = null;
        if (hasGpu)
        {
            if (nvmlDeviceGetTemperature(gpu, 0, out var t) == 0) temp = t;
            if (nvmlDeviceGetUtilizationRates(gpu, out var u) == 0) load = u.Gpu;
            if (nvmlDeviceGetClockInfo(gpu, 0, out var c) == 0) clock = c;
            if (nvmlDeviceGetMaxClockInfo(gpu, 0, out var mc) == 0) maxClock = mc;
            if (nvmlDeviceGetPowerUsage(gpu, out var mw) == 0) watts = mw / 1000.0;
            if (nvmlDeviceGetEnforcedPowerLimit(gpu, out var lim) == 0) limit = lim / 1000.0;
            if (nvmlDeviceGetMemoryInfo(gpu, out var m) == 0) (vramUsed, vramTotal) = (m.Used / Gb, m.Total / Gb);
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref memory);
        GetSystemPowerStatus(out var power);
        var hasBattery = power.BatteryFlag != 128 && power.BatteryFlag != 255;

        return new Reading(temp, load, clock, maxClock, watts, limit, vramUsed, vramTotal, CpuLoad(),
            (memory.TotalPhys - memory.AvailPhys) / Gb, memory.TotalPhys / Gb,
            hasBattery, power.ACLineStatus == 0, power.BatteryLifePercent <= 100 ? power.BatteryLifePercent : null);
    }

    const double Gb = 1L << 30;

    // Share of time the CPU wasn't idle since the last call. Kernel time includes idle time.
    double CpuLoad()
    {
        GetSystemTimes(out var i, out var k, out var u);
        var (di, dk, du) = (i - idle, k - kernel, u - user);
        (idle, kernel, user) = (i, k, u);
        return dk + du == 0 ? 0 : Math.Clamp(100.0 * (1 - (double)di / (dk + du)), 0, 100);
    }

    public void Dispose()
    {
        if (hasGpu) nvmlShutdown();
    }

    [StructLayout(LayoutKind.Sequential)] struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] struct NvmlMemory { public ulong Total, Free, Used; }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("nvml.dll")] static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] static extern int nvmlShutdown();
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetClockInfo(IntPtr device, int clock, out uint mhz);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, int clock, out uint mhz);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus status);
}
