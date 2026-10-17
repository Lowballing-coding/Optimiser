using System.Runtime.InteropServices;

namespace Optimiser;

// One second's worth of numbers for the Stats page. GPU values are null when there's no Nvidia driver.
public record Reading(
    double? GpuTemp, double? GpuLoad, double? GpuClock, double? GpuMaxClock, double? GpuWatts, double? GpuWattLimit,
    double? VramUsedGb, double? VramTotalGb, double CpuLoad, double RamUsedGb, double RamTotalGb,
    bool HasBattery, bool OnBattery, int? BatteryPercent,
    ulong? GpuLimits, double? CpuPercentOfBase, double? CpuBaseMhz, int? PowerMode);

// Reads live usage. The GPU goes through Nvidia's own driver library (nvml.dll), which needs no extra drivers.
// Reading it keeps a laptop's RTX GPU awake, so the Stats page only polls while it's on screen.
public sealed class Stats : IDisposable
{
    readonly IntPtr gpu;
    readonly bool hasGpu, hasLimits;
    long idle, kernel, user;

    // CPU speed: Windows' own "% Processor Performance" counter (over 100 while boosting) times the base clock.
    readonly IntPtr cpuQuery, cpuPerf;
    readonly double? baseMhz = Reg.Read(@"HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz") is int mhz and > 0 ? mhz : null;

    // Windows' power mode, pushed to us when it changes. The delegate is a field so it lives as long as the registration.
    readonly PowerModeChanged onPowerMode;
    readonly IntPtr powerModeRegistration;
    volatile int powerMode = -1;

    public Stats()
    {
        try
        {
            hasGpu = nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out gpu) == 0;
            hasLimits = hasGpu && nvmlDeviceGetCurrentClocksEventReasons(gpu, out _) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no Nvidia driver, or one from before mid-2023 without the clock reasons call
        }
        CpuLoad(); // first call only sets the baseline

        if (PdhOpenQueryW(null, IntPtr.Zero, out cpuQuery) == 0
            && PdhAddEnglishCounterW(cpuQuery, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out cpuPerf) == 0)
            PdhCollectQueryData(cpuQuery); // a rate counter: the first sample is only the baseline

        onPowerMode = (mode, _) => powerMode = mode;
        if (PowerRegisterForEffectivePowerModeNotifications(EffectivePowerModeV2, onPowerMode, IntPtr.Zero, out powerModeRegistration) != 0)
            powerModeRegistration = IntPtr.Zero;
    }

    // Whether the CPU speed tile has anything to show on this PC.
    public bool HasCpuSpeed => cpuPerf != IntPtr.Zero && baseMhz != null;

    public Reading Read()
    {
        double? temp = null, load = null, clock = null, maxClock = null, watts = null, limit = null, vramUsed = null, vramTotal = null;
        ulong? limits = null;
        if (hasGpu)
        {
            if (nvmlDeviceGetTemperature(gpu, 0, out var t) == 0) temp = t;
            if (nvmlDeviceGetUtilizationRates(gpu, out var u) == 0) load = u.Gpu;
            if (nvmlDeviceGetClockInfo(gpu, 0, out var c) == 0) clock = c;
            if (nvmlDeviceGetMaxClockInfo(gpu, 0, out var mc) == 0) maxClock = mc;
            if (nvmlDeviceGetPowerUsage(gpu, out var mw) == 0) watts = mw / 1000.0;
            if (nvmlDeviceGetEnforcedPowerLimit(gpu, out var lim) == 0) limit = lim / 1000.0;
            if (nvmlDeviceGetMemoryInfo(gpu, out var m) == 0) (vramUsed, vramTotal) = (m.Used / Gb, m.Total / Gb);
            if (hasLimits && nvmlDeviceGetCurrentClocksEventReasons(gpu, out var r) == 0) limits = r;
        }

        double? cpuPercent = null;
        if (cpuPerf != IntPtr.Zero && PdhCollectQueryData(cpuQuery) == 0
            && PdhGetFormattedCounterValue(cpuPerf, PdhFmtDouble | PdhFmtNoCap100, out _, out var perf) == 0)
            cpuPercent = perf.Double;

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref memory);
        GetSystemPowerStatus(out var power);
        var hasBattery = power.BatteryFlag != 128 && power.BatteryFlag != 255;

        return new Reading(temp, load, clock, maxClock, watts, limit, vramUsed, vramTotal, CpuLoad(),
            (memory.TotalPhys - memory.AvailPhys) / Gb, memory.TotalPhys / Gb,
            hasBattery, power.ACLineStatus == 0, power.BatteryLifePercent <= 100 ? power.BatteryLifePercent : null,
            limits, cpuPercent, baseMhz, powerMode >= 0 ? powerMode : null);
    }

    // What's holding the GPU's clock down, in words, from Nvidia's clock event reason bits (nvml.h nvmlClocksEventReasons).
    // Heat and the power brake come first: they're problems. The power limit is normal for a laptop GPU under full load.
    public static string Limit(ulong reasons) =>
        (reasons & (SwThermalSlowdown | HwThermalSlowdown)) != 0 ? "Heat"
        : (reasons & HwPowerBrakeSlowdown) != 0 ? "Power brake"
        : (reasons & HwSlowdown) != 0 ? "Slowdown"
        : (reasons & SwPowerCap) != 0 ? "Power limit"
        : (reasons & GpuIdle) != 0 ? "Idle"
        : "Nothing";

    public const ulong GpuIdle = 0x1, SwPowerCap = 0x4, HwSlowdown = 0x8, SwThermalSlowdown = 0x20,
        HwThermalSlowdown = 0x40, HwPowerBrakeSlowdown = 0x80;

    // EFFECTIVE_POWER_MODE (powersetting.h), as Windows 11's Settings > Power names them.
    public static string PowerModeName(int mode) => mode switch
    {
        0 => "Battery saver", 1 => "Best power efficiency", 2 => "Balanced", 3 => "High performance",
        4 => "Best performance", 5 => "Game Mode", 6 => "Mixed reality", _ => "",
    };

    public static bool OnBattery() => GetSystemPowerStatus(out var p) && p.ACLineStatus == 0;

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
        if (cpuQuery != IntPtr.Zero) PdhCloseQuery(cpuQuery);
        if (powerModeRegistration != IntPtr.Zero) PowerUnregisterFromEffectivePowerModeNotifications(powerModeRegistration);
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

    // PDH_FMT_COUNTERVALUE: a DWORD status, then a union that's 8-byte aligned on x64.
    [StructLayout(LayoutKind.Explicit)] struct PdhValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Double; }

    const uint PdhFmtDouble = 0x200, PdhFmtNoCap100 = 0x8000, EffectivePowerModeV2 = 2;

    delegate void PowerModeChanged(int mode, IntPtr context);

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
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetCurrentClocksEventReasons(IntPtr device, out ulong reasons);

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus status);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhValue value);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("powrprof.dll")]
    static extern int PowerRegisterForEffectivePowerModeNotifications(uint version, PowerModeChanged callback, IntPtr context, out IntPtr handle);
    [DllImport("powrprof.dll")] static extern int PowerUnregisterFromEffectivePowerModeNotifications(IntPtr handle);
}
