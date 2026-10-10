using System.Runtime.InteropServices;

namespace Optimiser;

public record Screen(string Device, int Hz, int MaxHz);

// Each screen's refresh rate. Laptops often fall back to 60 Hz after a driver update or on battery.
public static class Display
{
    // Every screen on the desktop, with the highest refresh rate it offers at its current resolution.
    public static List<Screen> Screens()
    {
        var screens = new List<Screen>();
        var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++, device.cb = Marshal.SizeOf<DisplayDevice>())
        {
            if ((device.StateFlags & AttachedToDesktop) == 0 || Current(device.DeviceName) is not { } now) continue;
            var max = now.dmDisplayFrequency;
            var mode = NewMode();
            for (var m = 0; EnumDisplaySettings(device.DeviceName, m, ref mode); m++)
                if (mode.dmPelsWidth == now.dmPelsWidth && mode.dmPelsHeight == now.dmPelsHeight && mode.dmBitsPerPel == now.dmBitsPerPel)
                    max = Math.Max(max, mode.dmDisplayFrequency);
            screens.Add(new(device.DeviceName, now.dmDisplayFrequency, max));
        }
        return screens;
    }

    public static void SetRefreshRate(string device, int hz)
    {
        if (Current(device) is not { } mode) return; // that screen is gone
        mode.dmDisplayFrequency = hz;
        mode.dmFields = DisplayFrequency;
        var result = ChangeDisplaySettingsEx(device, ref mode, IntPtr.Zero, UpdateRegistry, IntPtr.Zero);
        if (result < 0) throw new InvalidOperationException($"Windows wouldn't switch the screen to {hz} Hz (error {result}).");
    }

    static DevMode? Current(string device)
    {
        var mode = NewMode();
        return EnumDisplaySettings(device, CurrentSettings, ref mode) && mode.dmDisplayFrequency > 1 ? mode : null; // 0 or 1 = hardware default
    }

    static DevMode NewMode() => new() { dmSize = (short)Marshal.SizeOf<DevMode>() };

    const int AttachedToDesktop = 1, CurrentSettings = -1, DisplayFrequency = 0x400000, UpdateRegistry = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    // DEVMODEW, display fields only.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string device, int mode, ref DevMode info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string device, ref DevMode mode, IntPtr hwnd, int flags, IntPtr param);
}
