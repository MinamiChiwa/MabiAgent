using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MabiAgent.Capture;

/// <summary>Read-only Npcap adapter. No transmit API is imported. Device selection is explicit.</summary>
public sealed class NpcapPacketSource : IPassivePacketSource
{
    public static IReadOnlyList<string> DiscoverDevices() => DiscoverDeviceDetails().Select(d => d.Name).ToArray();
    public static IReadOnlyList<CaptureDevice> DiscoverDeviceDetails()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Npcap capture requires Windows");
        using var api = new NativeApi(); return api.Devices();
    }
    private readonly string deviceName;
    private readonly object gate = new();
    private bool disposed, running;
    public NpcapPacketSource(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Length > 512 || deviceName.Any(c => c == 0)) throw new ArgumentException("Explicit Npcap device required", nameof(deviceName));
        this.deviceName = deviceName;
    }
    public void Run(Action<TcpPacket> onPacket, Action<string> onGap, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Npcap capture requires Windows");
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); if (running) throw new InvalidOperationException("Capture source already running"); running = true; }
        try
        {
            using var api = new NativeApi();
            var error = new StringBuilder(256);
            var handle = api.Open(deviceName, 262144, 0, 100, error);
            if (handle == IntPtr.Zero) throw new IOException("Npcap open failed: " + error);
            try
            {
                // Configure before announcing the listener or accepting any login sync.
                // Resizing later would discard buffered packets and break stream continuity.
                if (api.SetBuffer(handle, 16 * 1024 * 1024) != 0) throw new IOException("Npcap receive buffer setup failed");
                if (api.SetNonBlock(handle, 1, error) != 0) throw new IOException("Npcap nonblocking mode failed: " + error);
                var link = api.DataLink(handle);
                if (link is not (0 or 1 or 12 or 101 or 228 or 229)) throw new NotSupportedException("Unsupported Npcap data link");
                var filter = default(BpfProgram);
                if (api.Compile(handle, ref filter, "tcp", 1, uint.MaxValue) != 0) throw new IOException("Npcap TCP filter compile failed");
                try { if (api.SetFilter(handle, ref filter) != 0) throw new IOException("Npcap TCP filter install failed"); }
                finally { api.FreeCode(ref filter); }
                var timer = Stopwatch.StartNew(); var lastStats = -1d; uint dropped = 0, interfaceDropped = 0;
                while (!cancellationToken.IsCancellationRequested && !IsDisposed())
                {
                    var result = api.Next(handle, out var headerPointer, out var dataPointer);
                    if (result == -2) return;
                    if (result == 0) cancellationToken.WaitHandle.WaitOne(10);
                    if (result < 0) throw new IOException("Npcap receive failed");
                    if (result == 1)
                    {
                        var header = Marshal.PtrToStructure<PcapHeader>(headerPointer);
                        if (header.CapturedLength > 262144 || header.CapturedLength != header.OriginalLength) { onGap("Npcap truncated packet; full sync required"); continue; }
                        var data = new byte[header.CapturedLength]; Marshal.Copy(dataPointer, data, 0, data.Length);
                        var packet = TcpPacketDecoder.Decode(link, data, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
                        if (packet is not null) onPacket(packet with{CaptureSource=deviceName});
                        else onGap("Unsupported or fragmented TCP packet; full sync required");
                    }
                    var now = timer.Elapsed.TotalSeconds;
                    if (now - lastStats < 1) continue;
                    lastStats = now;
                    var stats = api.ReadStats(handle);
                    if (stats.Dropped != dropped || stats.InterfaceDropped != interfaceDropped)
                        onGap($"Npcap dropped packets on {deviceName}: buffer={unchecked(stats.Dropped - dropped)}, interface={unchecked(stats.InterfaceDropped - interfaceDropped)}; full sync required");
                    dropped = stats.Dropped; interfaceDropped = stats.InterfaceDropped;
                }
            }
            finally { api.Close(handle); }
        }
        finally { lock (gate) running = false; }
    }
    private bool IsDisposed() { lock (gate) return disposed; }
    public void Dispose() { lock (gate) disposed = true; GC.SuppressFinalize(this); }
    [StructLayout(LayoutKind.Sequential)] private struct PcapHeader { public int Seconds, Microseconds; public uint CapturedLength, OriginalLength; }
    [StructLayout(LayoutKind.Sequential)] private struct PcapStats { public uint Received, Dropped, InterfaceDropped; }
    [StructLayout(LayoutKind.Sequential)] private struct BpfProgram { public uint Length; public IntPtr Instructions; }
    [StructLayout(LayoutKind.Sequential)] private struct PcapDevice { public IntPtr Next, Name, Description, Addresses; public uint Flags; }
    private sealed class NativeApi : IDisposable
    {
        private readonly IntPtr module;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet=CharSet.Ansi)] internal delegate IntPtr OpenFn(string device, int snaplen, int promiscuous, int timeout, StringBuilder error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int NextFn(IntPtr handle, out IntPtr header, out IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CloseFn(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int DataLinkFn(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SetBufferFn(IntPtr handle, int bytes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int StatsFn(IntPtr handle, IntPtr stats);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet=CharSet.Ansi)] internal delegate int SetNonBlockFn(IntPtr handle, int nonblock, StringBuilder error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet=CharSet.Ansi)] internal delegate int CompileFn(IntPtr handle, ref BpfProgram program, string filter, int optimize, uint netmask);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SetFilterFn(IntPtr handle, ref BpfProgram program);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FreeCodeFn(ref BpfProgram program);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet=CharSet.Ansi)] private delegate int FindDevicesFn(out IntPtr devices, StringBuilder error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeDevicesFn(IntPtr devices);
        internal readonly OpenFn Open; internal readonly NextFn Next; internal readonly CloseFn Close; internal readonly DataLinkFn DataLink;
        internal readonly SetBufferFn SetBuffer;
        internal readonly SetNonBlockFn SetNonBlock; private readonly StatsFn Stats; internal readonly CompileFn Compile; internal readonly SetFilterFn SetFilter; internal readonly FreeCodeFn FreeCode;
        internal NativeApi()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap", "wpcap.dll");
            module = NativeLibrary.Load(path, typeof(NpcapPacketSource).Assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            try
            {
                Open = Get<OpenFn>("pcap_open_live"); Next = Get<NextFn>("pcap_next_ex"); Close = Get<CloseFn>("pcap_close");
                SetBuffer = Get<SetBufferFn>("pcap_setbuff");
                SetNonBlock = Get<SetNonBlockFn>("pcap_setnonblock"); DataLink = Get<DataLinkFn>("pcap_datalink"); Stats = Get<StatsFn>("pcap_stats");
                Compile = Get<CompileFn>("pcap_compile"); SetFilter = Get<SetFilterFn>("pcap_setfilter"); FreeCode = Get<FreeCodeFn>("pcap_freecode");
            }
            catch { NativeLibrary.Free(module); throw; }
        }
        internal (uint Dropped, uint InterfaceDropped) ReadStats(IntPtr handle)
        {
            var memory = Marshal.AllocHGlobal(64);
            try
            {
                if (Stats(handle, memory) != 0) throw new IOException("Npcap drop counters unavailable");
                return (unchecked((uint)Marshal.ReadInt32(memory, 4)), unchecked((uint)Marshal.ReadInt32(memory, 8)));
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        internal IReadOnlyList<CaptureDevice> Devices()
        {
            var find = Get<FindDevicesFn>("pcap_findalldevs"); var free = Get<FreeDevicesFn>("pcap_freealldevs"); var error = new StringBuilder(256);
            if (find(out var first, error) != 0) throw new IOException("Npcap device discovery failed: " + error);
            try
            {
                var devices = new List<CaptureDevice>(); var seen = new HashSet<IntPtr>();
                for (var next = first; next != IntPtr.Zero;)
                {
                    if (!seen.Add(next) || seen.Count > 64) throw new InvalidDataException("Npcap device budget exceeded");
                    var row = Marshal.PtrToStructure<PcapDevice>(next); var name = Marshal.PtrToStringAnsi(row.Name);
                    var description = row.Description == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(row.Description);
                    if (!string.IsNullOrWhiteSpace(name) && ((row.Flags & 2) != 0 || (row.Flags & 1) != 0)) devices.Add(new(name, string.IsNullOrWhiteSpace(description) ? name : description.Trim())); next = row.Next;
                }
                return devices.AsReadOnly();
            }
            finally { free(first); }
        }
        private T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
        public void Dispose() => NativeLibrary.Free(module);
    }
}


