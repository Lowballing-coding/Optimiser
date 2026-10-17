using System.Buffers.Binary;

namespace Optimiser;

// Turns ETW present events into frame times per game process, the way PresentMon counts them. Handle runs on the ETW
// thread; Drain(pid) is called about once a second. Only processes in Pids are counted.
public sealed class FrameCounter(long qpcFrequency)
{
    public enum Kind : byte { Dxgi, D3d9, Kernel171, Kernel168 } // runtime kinds first

    public static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D"),
        Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9"), D3d9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    public volatile uint[] Pids = [];

    // DXGI 42/55 Present_Start (swap chain, flags), 43/56 Present_Stop (HRESULT); D3D9 1 Start, 2 Stop; DxgKrnl
    // 171 PresentHistory_Start (adapter, token, model) for Vulkan and OpenGL in a window, 168 Flip_Info in fullscreen.
    // Pointer fields are 8 bytes from a 64-bit process (header flag 0x40), else 4.
    public void Handle(EtwEvent e)
    {
        var (pid, tid) = ((uint)e.ProcessId, (uint)e.ThreadId);
        var d = e.Data;
        if (Array.IndexOf(Pids, pid) < 0) return;
        var ptr = (e.Flags & 0x40) != 0 ? 8 : 4;
        if (e.Provider == Dxgi || e.Provider == D3d9)
        {
            var dxgi = e.Provider == Dxgi;
            if ((dxgi ? e.Id is 42 or 55 : e.Id == 1) && d.Length >= ptr + (dxgi ? 4 : 0))
                RuntimeStart(tid, pid, dxgi ? Kind.Dxgi : Kind.D3d9, Pointer(d, ptr), dxgi ? BinaryPrimitives.ReadUInt32LittleEndian(d[ptr..]) : 0, e.Qpc);
            else if ((dxgi ? e.Id is 43 or 56 : e.Id == 2) && d.Length >= 4)
                RuntimeStop(tid, BinaryPrimitives.ReadInt32LittleEndian(d));
        }
        else if (e.Provider == DxgKrnl)
        {
            if (e.Id == 171 && d.Length >= 2 * ptr + 4 && BinaryPrimitives.ReadUInt32LittleEndian(d[(2 * ptr)..]) is var model and not (1 or 7))
                Kernel(pid, Kind.Kernel171, Pointer(d, ptr) ^ ((ulong)model << 56), e.Qpc); // 1 = GDI, 7 = composition: not the game's frame
            else if (e.Id == 168) Kernel(pid, Kind.Kernel168, 0, e.Qpc);
        }
    }

    static ulong Pointer(ReadOnlySpan<byte> d, int size) =>
        size == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(d) : BinaryPrimitives.ReadUInt32LittleEndian(d);

    sealed class Stream { public long LastTs = long.MinValue; public int Frames; public List<double> Ms = []; }

    readonly Dictionary<(uint Pid, Kind Kind, ulong Id), Stream> streams = [];
    readonly Dictionary<uint, (uint Pid, Kind Kind, ulong Swapchain, long Ts)> pending = []; // by thread id
    readonly long gap = 4 * qpcFrequency; // PresentMon forgets a swap chain after 4 s without presents

    public void RuntimeStart(uint tid, uint pid, Kind kind, ulong swapchain, uint flags, long ts)
    {
        lock (streams)
            if (kind == Kind.Dxgi && (flags & 1) != 0) pending.Remove(tid); // DXGI_PRESENT_TEST presents nothing
            else pending[tid] = (pid, kind, swapchain, ts);
    }

    // Start and Stop come on the same thread; a failed Present (e.g. DO_NOT_WAIT -> WAS_STILL_DRAWING) is not a frame.
    public void RuntimeStop(uint tid, int hresult)
    {
        lock (streams)
            if (pending.Remove(tid, out var p) && hresult >= 0) Add(p.Pid, p.Kind, p.Swapchain, p.Ts);
    }

    public void Kernel(uint pid, Kind kind, ulong id, long ts) { lock (streams) Add(pid, kind, id, ts); }

    void Add(uint pid, Kind kind, ulong id, long ts)
    {
        if (!streams.TryGetValue((pid, kind, id), out var s)) streams[(pid, kind, id)] = s = new();
        s.Frames++;
        if (ts <= s.LastTs) return; // delivered out of order: no frame time
        if (s.LastTs != long.MinValue && ts - s.LastTs <= gap) s.Ms.Add((ts - s.LastTs) * 1000.0 / qpcFrequency);
        s.LastTs = ts;
    }

    // Frame times (ms) that arrived since the last call: the busiest runtime stream, else the busiest kernel stream.
    // Streams are never summed, so one present seen by two providers (or two adapters) is never counted twice.
    public List<double> Drain(uint pid)
    {
        lock (streams)
        {
            Stream? best = null;
            var bestKind = Kind.Kernel168;
            foreach (var (k, s) in streams)
            {
                if (k.Pid != pid || s.Frames == 0) continue;
                bool runtime = k.Kind <= Kind.D3d9, bestRuntime = best != null && bestKind <= Kind.D3d9;
                if (best == null || (runtime && !bestRuntime) || (runtime == bestRuntime && s.Frames > best.Frames))
                    (best, bestKind) = (s, k.Kind);
            }
            var result = best?.Ms ?? [];
            foreach (var (k, s) in streams)
                if (k.Pid == pid)
                {
                    s.Frames = 0;
                    if (s == best) s.Ms = []; else s.Ms.Clear();
                }
            return result;
        }
    }

    public void Forget(uint pid)
    {
        lock (streams) foreach (var k in streams.Keys.Where(k => k.Pid == pid).ToList()) streams.Remove(k);
    }
}
