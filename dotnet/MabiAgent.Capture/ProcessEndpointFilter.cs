using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace MabiAgent.Capture;

/// <summary>Read-only OS TCP owner table. PID and process start time must both remain unchanged.</summary>
public sealed class ProcessEndpointFilter : IDisposable
{
    private readonly Process process;
    private readonly DateTime started;
    private readonly object gate = new();
    private readonly Stopwatch timer = Stopwatch.StartNew();
    private HashSet<TcpEndpoint> endpoints = [];
    private double refreshed = double.NegativeInfinity;
    public ProcessEndpointFilter(int processId, DateTime? expectedStartedUtc = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Process endpoint discovery requires Windows");
        process = Process.GetProcessById(processId); started = process.StartTime.ToUniversalTime();
        if (expectedStartedUtc.HasValue && started != expectedStartedUtc.Value)
        { process.Dispose(); throw new IOException("Bound capture process lifetime changed"); }
    }
    public bool Allows(TcpEndpoint endpoint)
    {
        lock (gate)
        {
            process.Refresh();
            if (process.HasExited || process.StartTime.ToUniversalTime() != started) throw new IOException("Bound capture process lifetime ended");
            var now = timer.Elapsed.TotalSeconds;
            if (now - refreshed >= 0.25)
            {
                var next = ReadTable(false, process.Id); next.UnionWith(ReadTable(true, process.Id));
                endpoints = next; refreshed = now;
            }
            return endpoints.Contains(endpoint);
        }
    }
    private static HashSet<TcpEndpoint> ReadTable(bool ipv6, int pid)
    {
        uint size = 0; var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, ipv6 ? 23 : 2, 5, 0);
        if (result != 122 && result != 0) throw new Win32Exception((int)result);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size < 4 || size > 16 * 1024 * 1024) throw new InvalidDataException("TCP owner table budget exceeded");
            var allocated = size; var memory = Marshal.AllocHGlobal((int)allocated);
            try
            {
                result = GetExtendedTcpTable(memory, ref size, false, ipv6 ? 23 : 2, 5, 0);
                if (result == 122) continue;
                if (result != 0) throw new Win32Exception((int)result);
                if (size > allocated) throw new InvalidDataException("TCP table extent changed");
                var bytes = new byte[size]; Marshal.Copy(memory,bytes,0,bytes.Length); return ParseOwnerTable(bytes,ipv6,pid);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        throw new IOException("TCP owner table unstable");
    }
    public static HashSet<TcpEndpoint> ParseOwnerTable(ReadOnlySpan<byte> table, bool ipv6, int processId)
    {
        if (processId <= 0 || table.Length < 4) throw new InvalidDataException("Invalid owner table");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table); var stride = ipv6 ? 56 : 24;
        if (count > 65536 || (long)count * stride > table.Length - 4) throw new InvalidDataException("Truncated owner table");
        var result = new HashSet<TcpEndpoint>();
        for (var i=0; i<count; i++)
        {
            var row = table.Slice(4+i*stride,stride);
            var pid = BinaryPrimitives.ReadUInt32LittleEndian(row[(ipv6 ? 52 : 20)..]);
            var state = BinaryPrimitives.ReadUInt32LittleEndian(row[(ipv6 ? 48 : 0)..]);
            if (pid != processId || state is not (1 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 11)) continue;
            var local = ipv6 ? new IPAddress(row[..16], BinaryPrimitives.ReadUInt32LittleEndian(row[16..])) : new IPAddress(row.Slice(4,4));
            var remote = ipv6 ? new IPAddress(row.Slice(24,16), BinaryPrimitives.ReadUInt32LittleEndian(row[40..])) : new IPAddress(row.Slice(12,4));
            var localPort = BinaryPrimitives.ReadUInt16BigEndian(row[(ipv6 ? 20 : 8)..]);
            var remotePort = BinaryPrimitives.ReadUInt16BigEndian(row[(ipv6 ? 44 : 16)..]);
            result.Add(new(remote.ToString(),remotePort,local.ToString(),localPort));
        }
        return result;
    }
    [DllImport("iphlpapi.dll", SetLastError=true)] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
    public void Dispose() { lock (gate) process.Dispose(); GC.SuppressFinalize(this); }
}
