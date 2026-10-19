using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Optimiser;

public enum Verdict { Passed, Errors, Crashed, Slower, TooHot, Stopped }

// A GPU stress test that checks its own answers, so an unstable overclock shows up as a counted error instead of
// a crash in a game. A compute shader fills a block of video memory with a known pattern and, on the next pass,
// checks every value is still right (memory errors), then runs each value through a chain of maths and back
// again, which must give the value it started with (core errors). It talks to Direct3D 11 through its COM
// tables directly, which keeps the app free of a graphics library for these few calls.
public sealed unsafe class GpuStress : IDisposable
{
    const int Rounds = 64, GroupsX = 1024, Width = GroupsX * 256; // 256 threads a group, matching the shader
    const uint DeviceRemoved = 0x887A0005, DeviceHung = 0x887A0006, DeviceReset = 0x887A0007;

    readonly IntPtr device, context, shader, data, dataView, errors, errorsView, staging, settings;
    readonly int groupsY;
    uint seed;
    public string Adapter { get; }
    public long MegaBytes => (long)groupsY * Width * 4 >> 20;
    public bool Inject { get; set; } // the self-checks use it to make sure a wrong answer is caught

    // The Nvidia GPU, or Windows' software renderer (WARP) for the self-checks on a PC without one.
    public GpuStress(bool software = false)
    {
        long bytes = 8 << 20;
        IntPtr adapter = IntPtr.Zero;
        Adapter = "Microsoft Basic Render Driver";
        if (!software)
        {
            (adapter, var name, var vram) = NvidiaAdapter();
            if (adapter == IntPtr.Zero) throw new InvalidOperationException("No Nvidia graphics card found.");
            Adapter = name;
            bytes = Math.Min(vram / 4, 1L << 30); // a quarter of video memory, up to 1 GB, leaves room for Windows
        }
        groupsY = (int)Math.Max(1, bytes / (Width * 4));
        try
        {
            var level = 0xb000; // feature level 11_0, the first with compute shaders this size
            Check(D3D11CreateDevice(adapter, software ? 5 : 0, IntPtr.Zero, 0, &level, 1, 7, out device, out _, out context));
        }
        finally { if (adapter != IntPtr.Zero) Release(adapter); }

        shader = Compile();
        (data, dataView) = Buffer((uint)(groupsY * Width));
        (errors, errorsView) = Buffer(4);
        var desc = new BufferDesc { ByteWidth = 16, Usage = 3, CpuAccessFlags = 0x20000 }; // staging, CPU reads it
        Check(Call(device, 3, &desc, null, out staging)); // CreateBuffer
        var cb = new BufferDesc { ByteWidth = (uint)sizeof(Constants), BindFlags = 4 }; // constant buffer
        Check(Call(device, 3, &cb, null, out settings));
    }

    // Runs until the time is up, an error is counted, the GPU driver resets, or `check` gives a reason to stop
    // (too hot, stopped by you). Score is passes a second, to spot an overclock that makes the GPU slower.
    public (Verdict Verdict, double Score) Run(TimeSpan time, Func<Verdict?>? check = null)
    {
        var watch = Stopwatch.StartNew();
        long passes = 0;
        var views = stackalloc IntPtr[] { dataView, errorsView };
        var cbs = stackalloc IntPtr[] { settings };
        while (true)
        {
            var ctx = (void**)*(void**)context;
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)ctx[69])(context, shader, null, 0); // CSSetShader
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, void>)ctx[68])(context, 0, 2, views, null); // CSSetUnorderedAccessViews
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)ctx[71])(context, 0, 1, cbs); // CSSetConstantBuffers
            for (var i = 0; i < 4; i++)
            {
                var c = Constants.For(seed++, Inject);
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void*, void*, uint, uint, void>)ctx[48])(context, settings, 0, null, &c, 0, 0); // UpdateSubresource
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, void>)ctx[41])(context, GroupsX, (uint)groupsY, 1); // Dispatch
            }
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)ctx[47])(context, staging, errors); // CopyResource
            Mapped map;
            var hr = (uint)((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, Mapped*, int>)ctx[14])(context, staging, 0, 1, 0, &map); // Map, waits for the GPU
            var score = (passes += 4) / watch.Elapsed.TotalSeconds;
            if (hr is DeviceRemoved or DeviceHung or DeviceReset) return (Verdict.Crashed, score);
            Check((int)hr);
            var wrong = ((uint*)map.Data)[0] + ((uint*)map.Data)[1];
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)ctx[15])(context, staging, 0); // Unmap
            if (wrong > 0) return (Verdict.Errors, score);
            if (check?.Invoke() is { } stop) return (stop, score);
            if (watch.Elapsed >= time) return (Verdict.Passed, score);
        }
    }

    // Mixed pattern for each value so stuck bits and neighbouring cells both show up.
    const string Hlsl = """
        cbuffer Settings : register(b0) { uint seed; uint rounds; uint factor; uint inverse; uint add; uint flip; uint inject; uint width; };
        RWStructuredBuffer<uint> data : register(u0);
        RWStructuredBuffer<uint> errors : register(u1);

        uint Pattern(uint i, uint s)
        {
            uint x = i ^ (s * 0x9E3779B9u);
            x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16;
            return x;
        }

        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            uint i = id.y * width + id.x;
            uint v = data[i];
            if (inject != 0 && i == 7) v ^= 1;
            if (seed != 0 && v != Pattern(i, seed - 1)) InterlockedAdd(errors[0], 1);
            uint x = v;
            [loop] for (uint r = 0; r < rounds; r++) { x = x * factor + add; x = (x << 13) | (x >> 19); x ^= flip; }
            if (inject != 0 && i == 7) x ^= 1;
            [loop] for (uint q = 0; q < rounds; q++) { x ^= flip; x = (x >> 13) | (x << 19); x = (x - add) * inverse; }
            if (x != v) InterlockedAdd(errors[1], 1);
            data[i] = Pattern(i, seed);
        }
        """;

    // The maths constants come in from outside, so the shader compiler can't see that the second loop undoes
    // the first and skip both.
    [StructLayout(LayoutKind.Sequential)]
    struct Constants
    {
        public uint Seed, Rounds, Mul, Inverse, Add, Flip, Inject, Width;

        public static Constants For(uint seed, bool inject)
        {
            const uint mul = 0x2545F491; // odd, so it has an inverse
            uint inverse = mul;
            for (var i = 0; i < 5; i++) inverse *= 2 - mul * inverse; // Newton's method, mod 2^32
            return new() { Seed = seed, Rounds = GpuStress.Rounds, Mul = mul, Inverse = inverse, Add = 0x6A09E667, Flip = 0xBB67AE85, Inject = inject ? 1u : 0, Width = GpuStress.Width };
        }
    }

    IntPtr Compile()
    {
        var source = Encoding.ASCII.GetBytes(Hlsl);
        fixed (byte* s = source)
        {
            var hr = D3DCompile(s, (nuint)source.Length, null, IntPtr.Zero, IntPtr.Zero, "main", "cs_5_0", 0, 0, out var code, out var log);
            if (hr < 0)
            {
                var why = log != IntPtr.Zero ? Marshal.PtrToStringAnsi(BlobPointer(log)) : null;
                throw new InvalidOperationException($"The GPU test didn't compile: {why}");
            }
            if (log != IntPtr.Zero) Release(log);
            try
            {
                var size = ((delegate* unmanaged[Stdcall]<IntPtr, nuint>)(*(void***)code)[4])(code); // GetBufferSize
                Check(Call(device, 18, (void*)BlobPointer(code), size, IntPtr.Zero, out var cs)); // CreateComputeShader
                return cs;
            }
            finally { Release(code); }
        }
    }

    (IntPtr Buffer, IntPtr View) Buffer(uint count)
    {
        var desc = new BufferDesc { ByteWidth = count * 4, BindFlags = 0x80, MiscFlags = 0x40, StructureByteStride = 4 }; // UAV, structured
        var zeros = new uint[count == 4 ? 4 : 0];
        fixed (uint* z = zeros)
        {
            var init = new SubresourceData { SysMem = z };
            Check(Call(device, 3, &desc, count == 4 ? &init : null, out var buffer)); // only the error counts need to start at 0
            var view = new UavDesc { Dimension = 1, NumElements = count };
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, UavDesc*, out IntPtr, int>)(*(void***)device)[8])(device, buffer, &view, out var uav)); // CreateUnorderedAccessView
            return (buffer, uav);
        }
    }

    // Integrated graphics come first on most laptops, so pick the Nvidia one by its vendor id.
    static (IntPtr, string, long) NvidiaAdapter()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        Check(CreateDXGIFactory1(&iid, out var factory));
        try
        {
            for (uint i = 0; ; i++)
            {
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)(*(void***)factory)[12])(factory, i, out var adapter) < 0) // EnumAdapters1
                    return (IntPtr.Zero, "", 0);
                AdapterDesc desc;
                if (((delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc*, int>)(*(void***)adapter)[10])(adapter, &desc) >= 0 && desc.VendorId == 0x10DE) // GetDesc1
                    return (adapter, new string(desc.Description).TrimEnd('\0'), (long)desc.DedicatedVideoMemory);
                Release(adapter);
            }
        }
        finally { Release(factory); }
    }

    public void Dispose()
    {
        foreach (var o in new[] { settings, staging, errorsView, errors, dataView, data, shader, context, device })
            if (o != IntPtr.Zero) Release(o);
    }

    static IntPtr BlobPointer(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)(*(void***)blob)[3])(blob);
    static void Release(IntPtr o) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)o)[2])(o);

    // ID3D11Device::CreateBuffer and CreateComputeShader, the two shapes used more than once.
    static int Call(IntPtr device, int slot, BufferDesc* desc, SubresourceData* init, out IntPtr result) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, BufferDesc*, SubresourceData*, out IntPtr, int>)(*(void***)device)[slot])(device, desc, init, out result);
    static int Call(IntPtr device, int slot, void* code, nuint size, IntPtr linkage, out IntPtr result) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, void*, nuint, IntPtr, out IntPtr, int>)(*(void***)device)[slot])(device, code, size, linkage, out result);

    static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [StructLayout(LayoutKind.Sequential)] struct BufferDesc { public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride; }
    [StructLayout(LayoutKind.Sequential)] struct SubresourceData { public void* SysMem; public uint Pitch, SlicePitch; }
    [StructLayout(LayoutKind.Sequential)] struct UavDesc { public uint Format, Dimension, FirstElement, NumElements, Flags; }
    [StructLayout(LayoutKind.Sequential)] struct Mapped { public void* Data; public uint RowPitch, DepthPitch; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct AdapterDesc
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long Luid;
        public uint Flags;
    }

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, int* levels, uint count,
        uint sdk, out IntPtr device, out int level, out IntPtr context);
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(Guid* iid, out IntPtr factory);
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    static extern int D3DCompile(byte* source, nuint size, string? name, IntPtr defines, IntPtr include, string entry,
        string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);
}
