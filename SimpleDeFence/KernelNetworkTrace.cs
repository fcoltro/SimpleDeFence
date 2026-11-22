using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace SimpleDeFence
{
    /// <summary>
    /// A real-time ETW session on the Microsoft-Windows-Kernel-Network provider: the kernel's own
    /// record of every TCP and UDP send and receive, with the process id and byte count of each.
    /// It is what Resource Monitor's network tab is built on.
    ///
    /// WFP, which the rest of the firewall uses, decides whether a connection may exist but never
    /// sees the bytes that flow over it afterwards, so it cannot count them. This is the only
    /// supported source that can, without a driver.
    ///
    /// Needs administrator rights, which the service has. Only 64-bit processes are supported: the
    /// structures below are laid out for them, and the shipped builds (x64 and ARM64) are both.
    /// </summary>
    internal sealed unsafe class KernelNetworkTrace : IDisposable
    {
        private const string SessionName = "SimpleDeFence Traffic";

        /// <summary>Microsoft-Windows-Kernel-Network.</summary>
        private static readonly Guid KernelNetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");

        /// <summary>Called on the trace thread for every send or receive: process id, bytes, and
        /// true for a send.</summary>
        internal delegate void TrafficHandler(uint pid, uint bytes, bool sent);

        private readonly TrafficHandler _onTraffic;
        private GCHandle _self;
        private ulong _session;
        private ulong _trace = INVALID_PROCESSTRACE_HANDLE;
        private IntPtr _loggerName;
        private Thread? _thread;
        private int _disposed;

        private KernelNetworkTrace(TrafficHandler onTraffic)
        {
            _onTraffic = onTraffic;
        }

        /// <summary>Starts counting, or returns null and logs why not.</summary>
        internal static KernelNetworkTrace? TryStart(TrafficHandler onTraffic)
        {
            var trace = new KernelNetworkTrace(onTraffic);
            try
            {
                trace.Start();
                return trace;
            }
            catch (Exception e)
            {
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
                trace.Dispose();
                return null;
            }
        }

        private void Start()
        {
            // The layouts below are the 64-bit ones. Checked rather than assumed, because a
            // mismatch would not fail loudly: Windows would read and write the wrong fields.
            if (IntPtr.Size != 8
                || sizeof(EVENT_TRACE_PROPERTIES) != 120
                || sizeof(EVENT_TRACE_LOGFILEW) != 448
                || sizeof(EVENT_RECORD) != 112)
                throw new PlatformNotSupportedException("Traffic counting needs a 64-bit process.");

            // A session outlives the process that started it. If the service was killed rather than
            // stopped, the last one is still there under the same name and has to go first.
            uint err = StartSession(out _session);
            if (err == ERROR_ALREADY_EXISTS)
            {
                StopSession(0);
                err = StartSession(out _session);
            }
            if (err != 0)
                throw new Win32Exception((int)err, "StartTrace failed for the traffic session.");

            var provider = KernelNetworkProvider;
            err = EnableTraceEx2(_session, &provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_VERBOSE,
                KERNEL_NETWORK_KEYWORD_IPV4 | KERNEL_NETWORK_KEYWORD_IPV6, 0, 0, IntPtr.Zero);
            if (err != 0)
                throw new Win32Exception((int)err, "EnableTraceEx2 failed for Microsoft-Windows-Kernel-Network.");

            _self = GCHandle.Alloc(this);
            _loggerName = Marshal.StringToHGlobalUni(SessionName);

            var logfile = default(EVENT_TRACE_LOGFILEW);
            logfile.LoggerName = _loggerName;
            logfile.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
            logfile.EventRecordCallback = (IntPtr)(delegate* unmanaged<EVENT_RECORD*, void>)&OnEvent;
            logfile.Context = GCHandle.ToIntPtr(_self);

            _trace = OpenTraceW(&logfile);
            if (_trace == INVALID_PROCESSTRACE_HANDLE)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenTrace failed for the traffic session.");

            // ProcessTrace blocks, delivering events, until the session is stopped.
            _thread = new Thread(() =>
            {
                ulong handle = _trace;
                uint result = ProcessTrace(&handle, 1, IntPtr.Zero, IntPtr.Zero);
                if (result != 0 && result != ERROR_CANCELLED && Volatile.Read(ref _disposed) == 0)
                    Utils.Log($"Traffic counting stopped: ProcessTrace returned {result}.", Utils.LOG_ID_SERVICE);
            })
            {
                IsBackground = true,
                Name = "Traffic counter",
            };
            _thread.Start();
        }

        private uint StartSession(out ulong session)
        {
            int size = sizeof(EVENT_TRACE_PROPERTIES) + (SessionName.Length + 1) * sizeof(char);
            var props = (EVENT_TRACE_PROPERTIES*)NativeMemory.AllocZeroed((nuint)size);
            try
            {
                props->Wnode.BufferSize = (uint)size;
                props->Wnode.Flags = WNODE_FLAG_TRACED_GUID;
                props->Wnode.ClientContext = 1;             // QueryPerformanceCounter timestamps
                props->LogFileMode = EVENT_TRACE_REAL_TIME_MODE;
                props->FlushTimer = 1;                      // deliver at least once a second
                props->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES);
                return StartTraceW(out session, SessionName, props);
            }
            finally
            {
                NativeMemory.Free(props);
            }
        }

        private static void StopSession(ulong session)
        {
            int size = sizeof(EVENT_TRACE_PROPERTIES) + 1024 * sizeof(char);
            var props = (EVENT_TRACE_PROPERTIES*)NativeMemory.AllocZeroed((nuint)size);
            try
            {
                props->Wnode.BufferSize = (uint)size;
                props->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES);
                // By handle when there is one, by name for a session a previous run left behind.
                _ = ControlTraceW(session, session == 0 ? SessionName : null, props, EVENT_TRACE_CONTROL_STOP);
            }
            finally
            {
                NativeMemory.Free(props);
            }
        }

        [UnmanagedCallersOnly]
        private static void OnEvent(EVENT_RECORD* record)
        {
            // Nothing may escape into the native caller.
            try
            {
                bool sent, ipv6;
                switch (record->EventHeader.EventDescriptor.Id)
                {
                    case 10: sent = true; ipv6 = false; break;  // TCPv4 data sent
                    case 11: sent = false; ipv6 = false; break; // TCPv4 data received
                    case 26: sent = true; ipv6 = true; break;   // TCPv6 data sent
                    case 27: sent = false; ipv6 = true; break;  // TCPv6 data received
                    case 42: sent = true; ipv6 = false; break;  // UDPv4 data sent
                    case 43: sent = false; ipv6 = false; break; // UDPv4 data received
                    case 58: sent = true; ipv6 = true; break;   // UDPv6 data sent
                    case 59: sent = false; ipv6 = true; break;  // UDPv6 data received
                    default: return;
                }

                // Every one of these starts PID (UInt32), size (UInt32), daddr, saddr.
                byte* data = (byte*)record->UserData;
                int length = record->UserDataLength;
                if (data == null || length < 8)
                    return;

                uint pid = Unsafe.ReadUnaligned<uint>(data);
                uint bytes = Unsafe.ReadUnaligned<uint>(data + 4);

                // Traffic that never leaves the machine - local servers, IPC over sockets - is
                // not data used, and some apps move gigabytes of it.
                if (IsLoopback(data + 8, length - 8, ipv6))
                    return;

                if (GCHandle.FromIntPtr(record->UserContext).Target is KernelNetworkTrace self)
                    self._onTraffic(pid, bytes, sent);
            }
            catch
            {
            }
        }

        private static bool IsLoopback(byte* address, int available, bool ipv6)
        {
            if (!ipv6)
                return available >= 4 && address[0] == 127;

            if (available < 16)
                return false;

            // ::1
            for (int i = 0; i < 15; i++)
            {
                if (address[i] != 0)
                    return false;
            }
            return address[15] == 1;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            // Stopping the session ends ProcessTrace once it has delivered what was buffered.
            if (_session != 0)
                StopSession(_session);
            if (_trace != INVALID_PROCESSTRACE_HANDLE)
                _ = CloseTrace(_trace);

            // The callback reaches this object through _self, so it is only released once the
            // thread that runs the callback has finished. If it has not, leaking one handle is
            // better than freeing it under a callback.
            bool finished = _thread is null || _thread.Join(TimeSpan.FromSeconds(5));
            if (finished && _self.IsAllocated)
                _self.Free();
            if (finished && _loggerName != IntPtr.Zero)
                Marshal.FreeHGlobal(_loggerName);
        }

        #region Native

        private const uint ERROR_ALREADY_EXISTS = 183;
        private const uint ERROR_CANCELLED = 1223;
        private const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;
        private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
        private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
        private const uint EVENT_TRACE_CONTROL_STOP = 1;
        private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
        private const byte TRACE_LEVEL_VERBOSE = 5;
        private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
        private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
        private const ulong KERNEL_NETWORK_KEYWORD_IPV4 = 0x10;
        private const ulong KERNEL_NETWORK_KEYWORD_IPV6 = 0x20;

        [StructLayout(LayoutKind.Sequential)]
        private struct WNODE_HEADER
        {
            public uint BufferSize;
            public uint ProviderId;
            public ulong HistoricalContext;
            public long TimeStamp;
            public Guid Guid;
            public uint ClientContext;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_TRACE_PROPERTIES
        {
            public WNODE_HEADER Wnode;
            public uint BufferSize;
            public uint MinimumBuffers;
            public uint MaximumBuffers;
            public uint MaximumFileSize;
            public uint LogFileMode;
            public uint FlushTimer;
            public uint EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers;
            public uint FreeBuffers;
            public uint EventsLost;
            public uint BuffersWritten;
            public uint LogBuffersLost;
            public uint RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset;
            public uint LoggerNameOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_TRACE_HEADER
        {
            public ushort Size;
            public ushort FieldTypeFlags;
            public uint Version;
            public uint ThreadId;
            public uint ProcessId;
            public long TimeStamp;
            public Guid Guid;
            public ulong ProcessorTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_TRACE
        {
            public EVENT_TRACE_HEADER Header;
            public uint InstanceId;
            public uint ParentInstanceId;
            public Guid ParentGuid;
            public IntPtr MofData;
            public uint MofLength;
            public uint ClientContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TIME_ZONE_INFORMATION
        {
            public int Bias;
            public fixed char StandardName[32];
            public SYSTEMTIME StandardDate;
            public int StandardBias;
            public fixed char DaylightName[32];
            public SYSTEMTIME DaylightDate;
            public int DaylightBias;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACE_LOGFILE_HEADER
        {
            public uint BufferSize;
            public uint VersionDetail;
            public uint ProviderVersion;
            public uint NumberOfProcessors;
            public long EndTime;
            public uint TimerResolution;
            public uint MaximumFileSize;
            public uint LogFileMode;
            public uint BuffersWritten;
            public Guid LogInstanceGuid;
            public IntPtr LoggerName;
            public IntPtr LogFileName;
            public TIME_ZONE_INFORMATION TimeZone;
            public long BootTime;
            public long PerfFreq;
            public long StartTime;
            public uint ReservedFlags;
            public uint BuffersLost;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_TRACE_LOGFILEW
        {
            public IntPtr LogFileName;
            public IntPtr LoggerName;
            public long CurrentTime;
            public uint BuffersRead;
            public uint ProcessTraceMode;
            public EVENT_TRACE CurrentEvent;
            public TRACE_LOGFILE_HEADER LogfileHeader;
            public IntPtr BufferCallback;
            public uint BufferSize;
            public uint Filled;
            public uint EventsLost;
            public IntPtr EventRecordCallback;
            public uint IsKernelTrace;
            public IntPtr Context;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_DESCRIPTOR
        {
            public ushort Id;
            public byte Version;
            public byte Channel;
            public byte Level;
            public byte Opcode;
            public ushort Task;
            public ulong Keyword;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_HEADER
        {
            public ushort Size;
            public ushort HeaderType;
            public ushort Flags;
            public ushort EventProperty;
            public uint ThreadId;
            public uint ProcessId;
            public long TimeStamp;
            public Guid ProviderId;
            public EVENT_DESCRIPTOR EventDescriptor;
            public ulong ProcessorTime;
            public Guid ActivityId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_RECORD
        {
            public EVENT_HEADER EventHeader;
            public byte ProcessorNumber;
            public byte Alignment;
            public ushort LoggerId;
            public ushort ExtendedDataCount;
            public ushort UserDataLength;
            public IntPtr ExtendedData;
            public IntPtr UserData;
            public IntPtr UserContext;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint StartTraceW(out ulong traceHandle, string instanceName, EVENT_TRACE_PROPERTIES* properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint ControlTraceW(ulong traceHandle, string? instanceName, EVENT_TRACE_PROPERTIES* properties, uint controlCode);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern uint EnableTraceEx2(ulong traceHandle, Guid* providerId, uint controlCode, byte level,
            ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern ulong OpenTraceW(EVENT_TRACE_LOGFILEW* logfile);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern uint ProcessTrace(ulong* handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern uint CloseTrace(ulong traceHandle);

        #endregion
    }
}
