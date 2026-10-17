using System.Runtime.InteropServices;

namespace Optimiser.Tests;

// A 64x64 window presented to by a D3D11 device on WARP (Windows' software renderer, so no GPU is needed):
// each Present() makes DXGI log the same Present_Start/Present_Stop ETW events a game's frames do.
sealed unsafe class WarpSwapChain : IDisposable
{
    public readonly nint Pointer; // the IDXGISwapChain*, which DXGI logs as the event's first field
    readonly nint window, device, context;

    public WarpSwapChain()
    {
        window = CreateWindowExW(0, "STATIC", "Optimiser self-check", 0x90000000 /* WS_POPUP | WS_VISIBLE */, 0, 0, 64, 64, 0, 0, 0, 0);
        if (window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        var desc = new SwapChainDesc
        {
            Width = 64, Height = 64, Format = 87 /* DXGI_FORMAT_B8G8R8A8_UNORM */, SampleCount = 1,
            BufferUsage = 0x20 /* DXGI_USAGE_RENDER_TARGET_OUTPUT */, BufferCount = 1, OutputWindow = window, Windowed = 1,
        };
        nint swapChain, dev, ctx;
        var hr = D3D11CreateDeviceAndSwapChain(0, 5 /* D3D_DRIVER_TYPE_WARP */, 0, 0, null, 0, 7 /* D3D11_SDK_VERSION */,
            &desc, &swapChain, &dev, null, &ctx);
        if (hr < 0) { DestroyWindow(window); Marshal.ThrowExceptionForHR(hr); }
        (Pointer, device, context) = (swapChain, dev, ctx);
    }

    // IDXGISwapChain::Present(SyncInterval 0, Flags 0) is vtable slot 8 (dxgi.h IDXGISwapChainVtbl).
    public int Present() => ((delegate* unmanaged<nint, uint, uint, int>)(*(nint**)Pointer)[8])(Pointer, 0, 0);

    public void Dispose()
    {
        Release(context);
        Release(Pointer);
        Release(device);
        DestroyWindow(window);
    }

    static void Release(nint com) => ((delegate* unmanaged<nint, uint>)(*(nint**)com)[2])(com); // IUnknown::Release

    struct SwapChainDesc // DXGI_SWAP_CHAIN_DESC, 72 bytes
    {
        public uint Width, Height, RefreshNumerator, RefreshDenominator, Format, ScanlineOrdering, Scaling; // DXGI_MODE_DESC 0-27
        public uint SampleCount, SampleQuality;                                                            // DXGI_SAMPLE_DESC 28
        public uint BufferUsage, BufferCount;                                                              // 36, 40
        public nint OutputWindow;                                                                          // 48
        public int Windowed;                                                                               // 56
        public uint SwapEffect, Flags;                                                                     // 60 (0 = DISCARD), 64
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int D3D11CreateDeviceAndSwapChain(nint adapter, int driverType, nint software, uint flags, int* featureLevels,
        uint featureLevelCount, uint sdkVersion, SwapChainDesc* desc, nint* swapChain, nint* device, int* featureLevel, nint* context);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern bool DestroyWindow(nint window);
}
