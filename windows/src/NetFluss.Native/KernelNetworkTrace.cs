// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NetFluss.Native;

/// <summary>Transport of a traced packet batch.</summary>
public enum TraceProtocol : byte
{
    Tcp,
    Udp,
}

/// <summary>
/// One local↔remote flow and the bytes it moved since the last snapshot. The key the
/// Network Slice groups by: hosts, services and apps are all projections of this.
/// </summary>
public readonly record struct FlowKey(
    int ProcessId,
    TraceProtocol Protocol,
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress RemoteAddress,
    int RemotePort);

/// <summary>Bytes received and sent, per process and per flow, over one snapshot interval.</summary>
public sealed record TrafficSnapshot(
    TimeSpan Elapsed,
    IReadOnlyDictionary<int, (long Received, long Sent)> ByProcess,
    IReadOnlyDictionary<FlowKey, (long Received, long Sent)> ByFlow);

/// <summary>Why the per-process trace is not running.</summary>
public enum TraceStatus
{
    Running,
    Stopped,

    /// <summary>
    /// Starting an ETW session needs administrator rights or membership of the Performance
    /// Log Users group; an ordinary unelevated user has neither. The helper service exists
    /// for exactly this.
    /// </summary>
    AccessDenied,

    Failed,
}

/// <summary>
/// Per-process network traffic from the <c>Microsoft-Windows-Kernel-Network</c> ETW provider.
///
/// <para><b>Why ETW.</b> macOS gets per-process byte counts unprivileged from
/// <c>netstat -n -b -v</c>. Windows has no unprivileged equivalent: the per-connection
/// <c>GetPerTcpConnectionEStats</c> counters need administrator rights to switch on, and
/// Task Manager's own Network column is built on this same provider. It is also richer than
/// the Mac's netstat diffing — every event carries the process, the byte count and both
/// endpoints, so the Network Slice gets real per-flow numbers instead of inferred ones.</para>
///
/// <para><b>Energy.</b> The provider fires for every send and receive the stack completes,
/// which on a busy machine is thousands of events a second. Callers start a trace only while
/// something that shows the result is open — the same rule that fixed the Mac's nettop
/// energy problem — and the callback does nothing but add two numbers under a lock.</para>
///
/// <para><b>Leaks.</b> An ETW real-time session outlives the process that started it, and
/// Windows allows only 64 of them machine-wide. The session therefore has one fixed name:
/// starting it first stops any session a crashed earlier run left behind, so NetFluss can
/// never hold more than one.</para>
///
/// <para>The structures are written into native buffers at their documented x64/ARM64
/// offsets (both are LP64 with 8-byte pointer alignment), each annotated below.</para>
/// </summary>
public sealed unsafe class KernelNetworkTrace : IDisposable
{
    /// <summary>
    /// The helper's session. Each owner has one fixed name, so a leaked session is always
    /// reclaimed — and the app's own trace, under <see cref="AppSessionName"/>, never reclaims
    /// (stops) the helper's: a member of Performance Log Users is allowed to.
    /// </summary>
    public const string SessionName = "NetFluss Kernel Network";

    public const string AppSessionName = "NetFluss Kernel Network (App)";

    private readonly string _name;

    public KernelNetworkTrace(string sessionName = SessionName) => _name = sessionName;

    private static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    private const ulong KeywordIPv4 = 0x10;
    private const ulong KeywordIPv6 = 0x20;
    private const byte LevelInformational = 4;

    private const uint ErrorSuccess = 0;
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorAlreadyExists = 183;
    private const uint ErrorWmiInstanceNotFound = 4201;

    private const int PropertiesSize = 120;
    private const int LogFileSize = 448;

    private readonly object _gate = new();
    private Dictionary<int, (long Received, long Sent)> _byProcess = [];
    private Dictionary<RawFlow, (long Received, long Sent)> _byFlow = [];
    private long _lastSnapshot;

    private ulong _session;
    private ulong _consumer = ulong.MaxValue;
    private nint _loggerName;
    private GCHandle _self;
    private Thread? _thread;

    public TraceStatus Status { get; private set; } = TraceStatus.Stopped;

    /// <summary>The Win32 error behind <see cref="TraceStatus.Failed"/>, for diagnostics.</summary>
    public uint LastError { get; private set; }

    /// <summary>Events the callback processed since start, for diagnostics.</summary>
    public long EventCount => Interlocked.Read(ref _eventCount);

    private long _eventCount;

    /// <summary>Every event delivered to the callback, counted or not, for diagnostics.</summary>
    public long DeliveredCount => Interlocked.Read(ref _delivered);

    private long _delivered;

    /// <summary>
    /// True when the consumer stopped on its own while the trace should be running — the
    /// session was stopped from outside, or ProcessTrace failed. The owner restarts it.
    /// </summary>
    public bool ConsumerExited => _consumerResult >= 0 && Status == TraceStatus.Running;

    private long _consumerResult = -1;

    /// <summary>Events that arrived without a trace to count them on — none, unless broken.</summary>
    private static long s_orphans;

    /// <summary>One line for a diagnostics log or the helper's hello.</summary>
    public string Diagnostics =>
        $"{Status}, delivered {DeliveredCount}, counted {EventCount}" +
        (_consumerResult >= 0 ? $", consumer exited ({_consumerResult})" : _thread is null ? string.Empty : ", consuming") +
        (LastError != 0 ? $", error {LastError}" : string.Empty) +
        (Interlocked.Read(ref s_orphans) is > 0 and var orphans ? $", orphaned {orphans}" : string.Empty);

    /// <summary>
    /// Starts the session and its consumer thread. Safe to call when already running.
    /// </summary>
    public TraceStatus Start()
    {
        if (Status == TraceStatus.Running)
        {
            return Status;
        }

        var status = StartSession();
        if (status == ErrorAlreadyExists)
        {
            // A previous NetFluss died without stopping its session. Reclaim the name.
            StopSessionByName();
            status = StartSession();
        }

        if (status != ErrorSuccess)
        {
            LastError = status;
            Status = status == ErrorAccessDenied ? TraceStatus.AccessDenied : TraceStatus.Failed;
            return Status;
        }

        var provider = KernelNetworkProvider;
        status = EnableTraceEx2(_session, &provider, 1, LevelInformational, KeywordIPv4 | KeywordIPv6, 0, 0, nint.Zero);
        if (status != ErrorSuccess)
        {
            LastError = status;
            StopSessionByName();
            Status = status == ErrorAccessDenied ? TraceStatus.AccessDenied : TraceStatus.Failed;
            return Status;
        }

        _self = GCHandle.Alloc(this);
        _loggerName = Marshal.StringToHGlobalUni(_name);

        // EVENT_TRACE_LOGFILEW (448 bytes on x64/ARM64)
        //   0  LPWSTR LogFileName
        //   8  LPWSTR LoggerName
        //  28  ULONG ProcessTraceMode
        // 424  PEVENT_RECORD_CALLBACK EventRecordCallback
        // 440  PVOID Context
        var logFile = (byte*)NativeMemory.AllocZeroed(LogFileSize);
        try
        {
            *(nint*)(logFile + 8) = _loggerName;
            *(uint*)(logFile + 28) = 0x00000100 | 0x10000000; // REAL_TIME | EVENT_RECORD
            *(nint*)(logFile + 424) = (nint)(delegate* unmanaged<byte*, void>)&OnEvent;
            *(nint*)(logFile + 440) = GCHandle.ToIntPtr(_self);

            _consumer = OpenTraceW(logFile);
        }
        finally
        {
            NativeMemory.Free(logFile);
        }

        if (_consumer == ulong.MaxValue)
        {
            LastError = (uint)Marshal.GetLastPInvokeError();
            Cleanup();
            Status = TraceStatus.Failed;
            return Status;
        }

        lock (_gate)
        {
            _byProcess = [];
            _byFlow = [];
            _lastSnapshot = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        var handle = _consumer;
        _thread = new Thread(() =>
        {
            var handles = handle;

            // Blocks until the session stops or the trace is closed.
            var result = ProcessTrace(&handles, 1, nint.Zero, nint.Zero);
            Interlocked.Exchange(ref _consumerResult, result);
        })
        {
            IsBackground = true,
            Name = "NetFluss ETW consumer",
            Priority = ThreadPriority.BelowNormal,
        };

        _thread.Start();
        Status = TraceStatus.Running;
        return Status;
    }

    /// <summary>Stops the session; the consumer thread drains and exits on its own.</summary>
    public void Stop()
    {
        if (Status != TraceStatus.Running)
        {
            return;
        }

        Cleanup();
        Status = TraceStatus.Stopped;
    }

    /// <summary>Everything counted since the previous snapshot, and resets the counters.</summary>
    public TrafficSnapshot TakeSnapshot()
    {
        lock (_gate)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_lastSnapshot, now);
            _lastSnapshot = now;

            var processes = _byProcess;
            var raw = _byFlow;
            _byProcess = [];
            _byFlow = [];

            // Addresses become objects only here, once per distinct flow per snapshot, rather
            // than twice per event on the consumer thread.
            var flows = new Dictionary<FlowKey, (long Received, long Sent)>(raw.Count);
            foreach (var (key, bytes) in raw)
            {
                flows[key.ToFlowKey()] = bytes;
            }

            return new TrafficSnapshot(elapsed, processes, flows);
        }
    }

    /// <summary>An allocation-free flow key for the hot path; addresses held as raw octets.</summary>
    private readonly record struct RawFlow(
        int ProcessId,
        TraceProtocol Protocol,
        bool IsV6,
        UInt128 Local,
        ushort LocalPort,
        UInt128 Remote,
        ushort RemotePort)
    {
        internal FlowKey ToFlowKey() => new(
            ProcessId,
            Protocol,
            ToAddress(Local, IsV6),
            LocalPort,
            ToAddress(Remote, IsV6),
            RemotePort);

        private static IPAddress ToAddress(UInt128 value, bool isV6)
        {
            Span<byte> bytes = stackalloc byte[16];
            MemoryMarshal.Write(bytes, in value);
            return new IPAddress(isV6 ? bytes : bytes[..4]);
        }

        internal static UInt128 Read(byte* source, int length)
        {
            Span<byte> bytes = stackalloc byte[16];
            new ReadOnlySpan<byte>(source, length).CopyTo(bytes);
            return MemoryMarshal.Read<UInt128>(bytes);
        }
    }

    public void Dispose() => Stop();

    private uint StartSession()
    {
        var nameBytes = (_name.Length + 1) * 2;
        var size = PropertiesSize + nameBytes;
        var properties = (byte*)NativeMemory.AllocZeroed((nuint)size);

        try
        {
            WriteProperties(properties, size);

            ulong session;
            var status = StartTraceW(&session, _name, properties);
            if (status == ErrorSuccess)
            {
                _session = session;
            }

            return status;
        }
        finally
        {
            NativeMemory.Free(properties);
        }
    }

    /// <summary>
    /// EVENT_TRACE_PROPERTIES (120 bytes) followed by room for the logger name.
    /// </summary>
    private static void WriteProperties(byte* p, int size)
    {
        // WNODE_HEADER (48 bytes)
        //   0  ULONG BufferSize
        //  40  ULONG ClientContext (1 = QueryPerformanceCounter timestamps)
        //  44  ULONG Flags (WNODE_FLAG_TRACED_GUID)
        *(uint*)(p + 0) = (uint)size;
        *(uint*)(p + 40) = 1;
        *(uint*)(p + 44) = 0x00020000;

        //  48  ULONG BufferSize (KB)
        //  52  ULONG MinimumBuffers
        //  56  ULONG MaximumBuffers
        //  64  ULONG LogFileMode (EVENT_TRACE_REAL_TIME_MODE)
        //  68  ULONG FlushTimer (seconds)
        // 116  ULONG LoggerNameOffset
        *(uint*)(p + 48) = 64;
        *(uint*)(p + 52) = 4;
        *(uint*)(p + 56) = 32;
        *(uint*)(p + 64) = 0x00000100;
        *(uint*)(p + 68) = 1;
        *(uint*)(p + 116) = PropertiesSize;
    }

    private void StopSessionByName()
    {
        var nameBytes = (_name.Length + 1) * 2;
        var size = PropertiesSize + nameBytes;
        var properties = (byte*)NativeMemory.AllocZeroed((nuint)size);

        try
        {
            WriteProperties(properties, size);
            _ = ControlTraceW(0, _name, properties, 1); // EVENT_TRACE_CONTROL_STOP
        }
        finally
        {
            NativeMemory.Free(properties);
        }
    }

    private void Cleanup()
    {
        StopSessionByName();
        _session = 0;

        if (_consumer != ulong.MaxValue)
        {
            _ = CloseTrace(_consumer);
            _consumer = ulong.MaxValue;
        }

        // Let the consumer thread finish its last callback before the handle it reads its
        // owner through is freed.
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;

        if (_self.IsAllocated)
        {
            _self.Free();
        }

        if (_loggerName != nint.Zero)
        {
            Marshal.FreeHGlobal(_loggerName);
            _loggerName = nint.Zero;
        }
    }

    /// <summary>
    /// EVENT_RECORD callback. Runs on the consumer thread for every event, so it does the
    /// minimum: decode the fixed payload and add to two dictionaries.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void OnEvent(byte* record)
    {
        try
        {
            // EVENT_RECORD
            //   0  EVENT_HEADER (80): EventDescriptor.Id at 40
            //  86  USHORT UserDataLength
            //  96  PVOID UserData
            // 104  PVOID UserContext (our GCHandle)
            var context = *(nint*)(record + 104);
            if (context == nint.Zero || GCHandle.FromIntPtr(context).Target is not KernelNetworkTrace trace)
            {
                Interlocked.Increment(ref s_orphans);
                return;
            }

            Interlocked.Increment(ref trace._delivered);
            var id = *(ushort*)(record + 40);
            var length = *(ushort*)(record + 86);
            var data = *(byte**)(record + 96);

            trace.Handle(id, data, length);
        }
        catch
        {
            // An exception must never cross back into the ETW runtime.
        }
    }

    private void Handle(ushort eventId, byte* data, int length)
    {
        // Every data-transfer event of interest starts PID, size, daddr, saddr, dport, sport.
        // daddr/saddr are 4 bytes for IPv4 and 16 for IPv6; ports are in network byte order.
        // "d" is the remote end and "s" the local one for sends and receives alike.
        bool isReceive;
        bool isV6;
        TraceProtocol protocol;

        switch (eventId)
        {
            case 10: isReceive = false; isV6 = false; protocol = TraceProtocol.Tcp; break; // TCP send IPv4
            case 11: isReceive = true; isV6 = false; protocol = TraceProtocol.Tcp; break;  // TCP recv IPv4
            case 26: isReceive = false; isV6 = true; protocol = TraceProtocol.Tcp; break;  // TCP send IPv6
            case 27: isReceive = true; isV6 = true; protocol = TraceProtocol.Tcp; break;   // TCP recv IPv6
            case 42: isReceive = false; isV6 = false; protocol = TraceProtocol.Udp; break; // UDP send IPv4
            case 43: isReceive = true; isV6 = false; protocol = TraceProtocol.Udp; break;  // UDP recv IPv4
            case 58: isReceive = false; isV6 = true; protocol = TraceProtocol.Udp; break;  // UDP send IPv6
            case 59: isReceive = true; isV6 = true; protocol = TraceProtocol.Udp; break;   // UDP recv IPv6
            default: return;
        }

        var addressLength = isV6 ? 16 : 4;
        var needed = 8 + (2 * addressLength) + 4;
        if (length < needed)
        {
            return;
        }

        var pid = *(int*)data;
        var size = *(uint*)(data + 4);
        var remote = RawFlow.Read(data + 8, addressLength);
        var local = RawFlow.Read(data + 8 + addressLength, addressLength);
        var remotePort = Swap(*(ushort*)(data + 8 + (2 * addressLength)));
        var localPort = Swap(*(ushort*)(data + 10 + (2 * addressLength)));

        var key = new RawFlow(pid, protocol, isV6, local, localPort, remote, remotePort);

        lock (_gate)
        {
            ref var process = ref CollectionsMarshal.GetValueRefOrAddDefault(_byProcess, pid, out _);
            ref var flow = ref CollectionsMarshal.GetValueRefOrAddDefault(_byFlow, key, out _);

            if (isReceive)
            {
                process.Received += size;
                flow.Received += size;
            }
            else
            {
                process.Sent += size;
                flow.Sent += size;
            }
        }

        Interlocked.Increment(ref _eventCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort Swap(ushort value) => (ushort)((value >> 8) | (value << 8));

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(ulong* sessionHandle, string sessionName, byte* properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong sessionHandle, string sessionName, byte* properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong sessionHandle, Guid* providerId, uint controlCode, byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, nint enableParameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern ulong OpenTraceW(byte* logFile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong* handleArray, uint handleCount, nint startTime, nint endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);
}
