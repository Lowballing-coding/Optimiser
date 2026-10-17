using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Optimiser;

// One ETW event, valid only inside the handler call (it points into ETW's buffer, so it's a ref struct).
public ref struct EtwEvent
{
    public Guid Provider;
    public ushort Id;
    public byte Version, Opcode;
    public ushort Flags;             // EVENT_HEADER_FLAG_*: 0x40 = logged by a 64-bit process, so pointer fields in Data are 8 bytes (else 4)
    public int ProcessId, ThreadId;
    public long Qpc;                 // QueryPerformanceCounter ticks: same clock as Stopwatch.GetTimestamp(), Stopwatch.Frequency per second
    public ReadOnlySpan<byte> Data;  // the event's payload
}

// A real-time ETW session (Windows' built-in event tracing, the same feed PresentMon reads) that hands every event
// from the given providers to a handler. Plain advapi32 calls, no driver, nothing injected. Needs admin.
// The handler runs on this session's own background thread, one event at a time: keep it short, lock what it shares,
// and never call Dispose from inside it. Sessions are named and outlive a crashed process, so starting one clears a
// stale one of the same name; keep at most one live instance per name.
public sealed unsafe class Etw : IDisposable
{
    public delegate void Handler(EtwEvent e);

    readonly string name;
    readonly Handler handler;
    readonly ulong session;
    readonly Guid source; // the session's own id, which StartTrace writes back
    readonly TaskCompletionSource<uint> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool stopped;

    // Completes with ProcessTrace's result when the session's thread ends; no handler call runs after that.
    public Task<uint> Finished => finished.Task;

    // The first exception the handler threw. They're caught because one must not unwind into ETW's native code.
    public Exception? Error { get; private set; }

    // Starts the session with no providers on; Enable turns them on.
    public Etw(string name, Handler handler)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("The ETW structs below are the 64-bit layouts");
        (this.name, this.handler) = (name, handler);

        var props = NewProperties();
        var err = StartTraceW(out var session, name, &props.P);
        for (var i = 0; err == ERROR_ALREADY_EXISTS && i < 20; i++) // left running by a crash, or still stopping
        {
            Stop(name);
            Thread.Sleep(100);
            props = NewProperties();
            err = StartTraceW(out session, name, &props.P);
        }
        if (err != 0) throw new Win32Exception((int)err); // 5 = not admin, 1450 = Windows' session limit (64) reached
        (this.session, source) = (session, props.P.Wnode.Guid);

        var self = GCHandle.Alloc(this);
        try
        {
            var log = new EVENT_TRACE_LOGFILEW
            {
                ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_RAW_TIMESTAMP,
                EventRecordCallback = &OnEvent,
                Context = (void*)GCHandle.ToIntPtr(self),
            };
            ulong trace;
            fixed (char* n = name)
            {
                log.LoggerName = n;
                trace = OpenTraceW(&log); // copies what it needs, as TraceEvent and PresentMon rely on
            }
            if (trace == INVALID_PROCESSTRACE_HANDLE) throw new Win32Exception(Marshal.GetLastPInvokeError());

            new Thread(() =>
            {
                var t = trace;
                var result = ProcessTrace(&t, 1, null, null); // returns once the session stops (Dispose, or anyone else)
                CloseTrace(t);                                // only here, so the handle is never closed before ProcessTrace has it
                self.Free();                                  // no more callbacks can arrive
                finished.SetResult(result);
            }) { IsBackground = true, Name = "ETW " + name }.Start();
        }
        catch
        {
            self.Free();
            Stop(name);
            throw;
        }
    }

    // Turns on a provider's events at every level: only events that carry all of these keywords (as PresentMon asks),
    // and only the listed event ids when there are any. 0 when it worked; 1450 = 8 sessions already have it on.
    public uint Enable(Guid provider, ulong keywords, params ReadOnlySpan<ushort> ids)
    {
        var filter = stackalloc ushort[2 + ids.Length]; // EVENT_FILTER_EVENT_ID: FilterIn, Reserved, Count, Events[]
        filter[0] = 1;
        filter[1] = (ushort)ids.Length;
        ids.CopyTo(new Span<ushort>(filter + 2, ids.Length));
        var desc = new EVENT_FILTER_DESCRIPTOR { Ptr = filter, Size = (uint)(4 + 2 * ids.Length), Type = EVENT_FILTER_TYPE_EVENT_ID };
        var parameters = new ENABLE_TRACE_PARAMETERS
        {
            Version = 2, EnableProperty = EVENT_ENABLE_PROPERTY_IGNORE_KEYWORD_0, SourceId = source,
            EnableFilterDesc = ids.IsEmpty ? null : &desc, FilterDescCount = ids.IsEmpty ? 0u : 1,
        };
        return EnableTraceEx2(session, provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, 0, keywords, keywords, 0, &parameters);
    }

    // Stops the session without waiting: ProcessTrace may take a moment (Microsoft says up to several seconds) to
    // return, and events it still delivers are dropped. Await Finished if you need it fully gone.
    public void Dispose()
    {
        if (stopped) return;
        stopped = true;
        if (!finished.Task.IsCompleted) Stop(name); // already completed = someone else stopped it, maybe to start their own
    }

    // Stops a session by name: 0 when it stopped, 4201 (ERROR_WMI_INSTANCE_NOT_FOUND) when it wasn't running.
    public static uint Stop(string name)
    {
        var props = NewProperties();
        return ControlTraceW(0, name, &props.P, EVENT_TRACE_CONTROL_STOP);
    }

    [UnmanagedCallersOnly]
    static void OnEvent(EVENT_RECORD* r)
    {
        Etw? self = null;
        try
        {
            self = (Etw)GCHandle.FromIntPtr((nint)r->UserContext).Target!;
            if (self.stopped) return;
            ref var h = ref r->EventHeader;
            self.handler(new EtwEvent
            {
                Provider = h.ProviderId, Id = h.Id, Version = h.Version, Opcode = h.Opcode, Flags = h.Flags,
                ProcessId = (int)h.ProcessId, ThreadId = (int)h.ThreadId, Qpc = h.TimeStamp,
                Data = new ReadOnlySpan<byte>(r->UserData, r->UserDataLength),
            });
        }
        catch (Exception e)
        {
            if (self != null) self.Error ??= e;
        }
    }

    static Properties NewProperties()
    {
        var p = new Properties();
        p.P.Wnode.BufferSize = (uint)sizeof(Properties);
        p.P.Wnode.Flags = WNODE_FLAG_TRACED_GUID;  // Wnode.Guid stays zero: ETW makes one for a normal session
        p.P.Wnode.ClientContext = 1;               // QPC timestamps
        p.P.LogFileMode = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_INDEPENDENT_SESSION_MODE; // other sessions' write failures don't cost us events
        p.P.BufferSize = 32;                       // KB; Microsoft's advice for small events at a moderate rate
        p.P.MaximumBuffers = 128;                  // 4 MB ceiling for bursts; ETW picks the minimum (2 per CPU)
        p.P.FlushTimer = 1;                        // deliver at least once a second
        p.P.LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES); // StartTrace / ControlTrace write the name here
        return p;                                  // LogFileNameOffset = 0: real-time only, no .etl file
    }

    // ---- advapi32 (evntrace.h / evntcons.h). 64-bit layouts: offsets in comments. ----

    const uint WNODE_FLAG_TRACED_GUID = 0x00020000, EVENT_TRACE_REAL_TIME_MODE = 0x00000100,
        EVENT_TRACE_INDEPENDENT_SESSION_MODE = 0x08000000, EVENT_TRACE_CONTROL_STOP = 1,
        EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1, PROCESS_TRACE_MODE_REAL_TIME = 0x00000100,
        PROCESS_TRACE_MODE_RAW_TIMESTAMP = 0x00001000, PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000, ERROR_ALREADY_EXISTS = 183,
        EVENT_ENABLE_PROPERTY_IGNORE_KEYWORD_0 = 0x10, EVENT_FILTER_TYPE_EVENT_ID = 0x80000200;
    const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint StartTraceW(out ulong traceId, string instanceName, EVENT_TRACE_PROPERTIES* properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint ControlTraceW(ulong traceId, string? instanceName, EVENT_TRACE_PROPERTIES* properties, uint controlCode);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    static extern uint EnableTraceEx2(ulong traceId, in Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, ENABLE_TRACE_PARAMETERS* enableParameters);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    static extern ulong OpenTraceW(EVENT_TRACE_LOGFILEW* logfile);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    static extern uint ProcessTrace(ulong* handleArray, uint handleCount, long* startTime, long* endTime);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    static extern uint CloseTrace(ulong traceHandle);

#pragma warning disable CS0649 // fields Windows fills in
    struct WNODE_HEADER                      // 48 bytes
    {
        public uint BufferSize;              // 0
        public uint ProviderId;              // 4
        public ulong HistoricalContext;      // 8
        public long TimeStamp;               // 16
        public Guid Guid;                    // 24
        public uint ClientContext;           // 40
        public uint Flags;                   // 44
    }

    struct EVENT_TRACE_PROPERTIES            // 120 bytes
    {
        public WNODE_HEADER Wnode;           // 0
        public uint BufferSize;              // 48
        public uint MinimumBuffers;          // 52
        public uint MaximumBuffers;          // 56
        public uint MaximumFileSize;         // 60
        public uint LogFileMode;             // 64
        public uint FlushTimer;              // 68
        public uint EnableFlags;             // 72
        public int AgeLimit;                 // 76
        public uint NumberOfBuffers;         // 80
        public uint FreeBuffers;             // 84
        public uint EventsLost;              // 88
        public uint BuffersWritten;          // 92
        public uint LogBuffersLost;          // 96
        public uint RealTimeBuffersLost;     // 100
        public nint LoggerThreadId;          // 104
        public uint LogFileNameOffset;       // 112
        public uint LoggerNameOffset;        // 116
    }

    struct Properties                        // 120 + 2048 bytes: the struct plus room for a 1,024-char session name
    {
        public EVENT_TRACE_PROPERTIES P;
        public fixed char LoggerName[1024];
    }

    struct ENABLE_TRACE_PARAMETERS           // 48 bytes
    {
        public uint Version;                 // 0
        public uint EnableProperty;          // 4
        public uint ControlFlags;            // 8
        public Guid SourceId;                // 12
        public EVENT_FILTER_DESCRIPTOR* EnableFilterDesc; // 32
        public uint FilterDescCount;         // 40
    }

    struct EVENT_FILTER_DESCRIPTOR           // 16 bytes
    {
        public void* Ptr;                    // 0
        public uint Size;                    // 8
        public uint Type;                    // 12
    }

    struct EVENT_TRACE_LOGFILEW              // 448 bytes
    {
        public char* LogFileName;            // 0
        public char* LoggerName;             // 8
        public long CurrentTime;             // 16
        public uint BuffersRead;             // 24
        public uint ProcessTraceMode;        // 28 (union with LogFileMode)
        public fixed byte CurrentEvent[88];  // 32  EVENT_TRACE, output only
        public fixed byte LogfileHeader[280];// 120 TRACE_LOGFILE_HEADER, output only
        public void* BufferCallback;         // 400
        public uint BufferSize;              // 408
        public uint Filled;                  // 412
        public uint EventsLost;              // 416
        public delegate* unmanaged<EVENT_RECORD*, void> EventRecordCallback; // 424 (union with EventCallback)
        public uint IsKernelTrace;           // 432
        public void* Context;                // 440 -> EVENT_RECORD.UserContext
    }

    struct EVENT_HEADER                      // 80 bytes
    {
        public ushort Size;                  // 0
        public ushort HeaderType;            // 2
        public ushort Flags;                 // 4
        public ushort EventProperty;         // 6
        public uint ThreadId;                // 8
        public uint ProcessId;               // 12
        public long TimeStamp;               // 16 raw QPC with ClientContext = 1 + PROCESS_TRACE_MODE_RAW_TIMESTAMP
        public Guid ProviderId;              // 24
        public ushort Id;                    // 40 EVENT_DESCRIPTOR (16 bytes) starts here
        public byte Version;                 // 42
        public byte Channel;                 // 43
        public byte Level;                   // 44
        public byte Opcode;                  // 45
        public ushort Task;                  // 46
        public ulong Keyword;                // 48
        public ulong ProcessorTime;          // 56 (union KernelTime/UserTime)
        public Guid ActivityId;              // 64
    }

    struct EVENT_RECORD                      // 112 bytes
    {
        public EVENT_HEADER EventHeader;     // 0
        public ushort ProcessorIndex;        // 80 ETW_BUFFER_CONTEXT (4 bytes)
        public ushort LoggerId;              // 82
        public ushort ExtendedDataCount;     // 84
        public ushort UserDataLength;        // 86
        public void* ExtendedData;           // 88
        public void* UserData;               // 96
        public void* UserContext;            // 104
    }
#pragma warning restore CS0649
}
