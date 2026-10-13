using System.Runtime.InteropServices;

namespace Optimiser;

// A window without a title bar maximises over the taskbar; this keeps it inside the screen's work area.
static class WorkArea
{
    const int WM_GETMINMAXINFO = 0x0024;
    const uint MONITOR_DEFAULTTONEAREST = 2;

    public static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        (mmi.ptMaxPosition.X, mmi.ptMaxPosition.Y) = (info.rcWork.Left - info.rcMonitor.Left, info.rcWork.Top - info.rcMonitor.Top);
        (mmi.ptMaxSize.X, mmi.ptMaxSize.Y) = (info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top);
        Marshal.StructureToPtr(mmi, lParam, false);
        return IntPtr.Zero; // not handled, so WPF still applies the minimum size
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
