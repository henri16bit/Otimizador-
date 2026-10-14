using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PCAnalyzer.Diagnostics;

/// <summary>
/// Native stop for this invocation's unique logger. TraceEventSession.Stop() also resets the
/// global CPU profile interval, an unrelated setting which this DPC/ISR collection must preserve.
/// </summary>
internal static class OwnedEtwControl
{
    public static int Stop(string ownedName)
    {
        const string prefix = "PCAnalyzer-DpcIsr-";
        if (!ownedName.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(ownedName[prefix.Length..], "N", out _))
            throw new ArgumentException("Apenas sessões exclusivas desta captura podem ser encerradas.", nameof(ownedName));
        var structureBytes = Marshal.SizeOf<TraceProperties>();
        var bytes = structureBytes + 4096;
        var memory = Marshal.AllocHGlobal(bytes);
        try
        {
            Marshal.Copy(new byte[bytes], 0, memory, bytes);
            var properties = new TraceProperties
            {
                Wnode = new WnodeHeader { BufferSize = (uint)bytes },
                LoggerNameOffset = (uint)structureBytes,
                LogFileNameOffset = (uint)structureBytes + 2048
            };
            Marshal.StructureToPtr(properties, memory, false);
            var status = ControlTrace(0, ownedName, memory, 1 /* EVENT_TRACE_CONTROL_STOP */);
            if (status == 4201 /* ERROR_WMI_INSTANCE_NOT_FOUND */) return 0;
            if (status != 0) throw new Win32Exception((int)status, "Não foi possível encerrar a sessão ETW própria " + ownedName);
            var completed = Marshal.PtrToStructure<TraceProperties>(memory);
            return (int)Math.Min(completed.EventsLost, int.MaxValue);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint ControlTrace(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long Timestamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceProperties
    {
        public WnodeHeader Wnode;
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
}
