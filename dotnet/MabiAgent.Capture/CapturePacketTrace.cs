using System.Diagnostics;
using System.Text.Json;

namespace MabiAgent.Capture;

/// <summary>Explicit, bounded receive-only diagnostic; never participates in decoding or inventory.</summary>
public sealed class CapturePacketTrace : IDisposable
{
    public const string PathEnvironmentVariable = "MABI_CAPTURE_TRACE_PATH";
    private readonly string path;
    private readonly int maxPackets, maxBytes;
    private readonly TimeSpan duration;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private FileStream? output;
    private int packets, bytes;
    private bool stopped;

    public CapturePacketTrace(string path, int maxPackets = 20000, int maxBytes = 16 * 1024 * 1024, TimeSpan? duration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxPackets <= 0 || maxBytes <= 0 || duration is { } limit && limit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxPackets));
        this.path = Path.GetFullPath(path);
        this.maxPackets = maxPackets; this.maxBytes = maxBytes; this.duration = duration ?? TimeSpan.FromMinutes(3);
    }

    public string? WriteError { get; private set; }
    internal static CapturePacketTrace? FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return new(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return null; }
    }

    internal void Record(TcpPacket packet, string? connection, object? before, object? after, string? error = null)
    {
        if (stopped) return;
        if (packets >= maxPackets || elapsed.Elapsed >= duration) { Dispose(); return; }
        try
        {
            // Account for base64 and metadata before serializing a large packet.
            if (4L * ((packet.Payload.Length + 2L) / 3) > maxBytes - bytes) { Dispose(); return; }
            var row = JsonSerializer.SerializeToUtf8Bytes(new
            {
                at = DateTimeOffset.UtcNow, packet.ReceivedAt, packet.CaptureSource, packet.Endpoint,
                packet.Sequence, packet.Syn, packet.Fin, packet.Reset, packet.Acknowledgement,
                payload_base64 = Convert.ToBase64String(packet.Payload), connection, before, after, error
            });
            if (row.Length + 1 > maxBytes - bytes) { Dispose(); return; }
            if (output is null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            output.Write(row); output.WriteByte((byte)'\n'); output.Flush();
            bytes += row.Length + 1; packets++;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        { WriteError = failure.Message; Dispose(); }
    }

    public void Dispose()
    {
        stopped = true;
        try { output?.Dispose(); }
        catch (IOException failure) { WriteError = failure.Message; }
        output = null;
    }
}
