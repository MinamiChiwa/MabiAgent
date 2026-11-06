using System.Buffers.Binary;
using System.Net;

namespace MabiAgent.Capture;

public sealed record TcpEndpoint(string SourceAddress, ushort SourcePort, string DestinationAddress, ushort DestinationPort);
public sealed record TcpPacket(TcpEndpoint Endpoint, uint Sequence, byte[] Payload, bool Syn, bool Fin, bool Reset, double ReceivedAt, string CaptureSource="")
{
    public bool Acknowledgement { get; init; }
}
public sealed record CaptureMetrics(long Packets=0,long PayloadBytes=0,long DecodedFrames=0,int Connections=0,int BufferedBytes=0,long Invalidations=0)
{
    public const int ConnectionLimit=64,BufferLimit=48*1024*1024;
}
public interface IPassivePacketSource : IDisposable
{
    void Run(Action<TcpPacket> onPacket, Action<string> onGap, CancellationToken cancellationToken);
}

public static class TcpPacketDecoder
{
    /// <summary>Ethernet/VLAN, NULL loopback, and raw IPv4/IPv6. Fragmented traffic is never guessed.</summary>
    public static TcpPacket? Decode(int linkType, ReadOnlySpan<byte> frame, double receivedAt)
    {
        if (!double.IsFinite(receivedAt)) throw new ArgumentOutOfRangeException(nameof(receivedAt));
        var offset = 0;
        if (linkType == 1)
        {
            if (frame.Length < 14) return null;
            var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]); offset = 14;
            for (var vlan = 0; etherType is 0x8100 or 0x88a8; vlan++)
            {
                if (vlan >= 2 || frame.Length - offset < 4) return null;
                etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[(offset + 2)..]); offset += 4;
            }
            if (etherType is not (0x0800 or 0x86dd)) return null;
        }
        else if (linkType == 0) { if (frame.Length < 4) return null; offset = 4; }
        else if (linkType is not (12 or 101 or 228 or 229)) return null;
        if (offset >= frame.Length) return null;
        var ip = frame[offset..]; ReadOnlySpan<byte> tcp; string source, destination;
        if (ip[0] >> 4 == 4)
        {
            if (ip.Length < 20 || ip[9] != 6) return null;
            var header = (ip[0] & 15) * 4; var total = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
            if (header < 20 || total < header || total > ip.Length || (BinaryPrimitives.ReadUInt16BigEndian(ip[6..]) & 0x3fff) != 0) return null;
            source = new IPAddress(ip.Slice(12,4)).ToString(); destination = new IPAddress(ip.Slice(16,4)).ToString();
            tcp = ip.Slice(header,total - header);
        }
        else if (ip[0] >> 4 == 6)
        {
            if (ip.Length < 40) return null;
            var total = 40 + BinaryPrimitives.ReadUInt16BigEndian(ip[4..]); if (total > ip.Length) return null;
            var protocol = ip[6]; var header = 40; var extensions = 0;
            while (protocol is 0 or 43 or 60)
            {
                if (++extensions > 8 || total - header < 8) return null;
                var next = ip[header]; var size = (ip[header + 1] + 1) * 8;
                if (size > total - header) return null; header += size; protocol = next;
            }
            if (protocol != 6) return null;
            source = new IPAddress(ip.Slice(8,16)).ToString(); destination = new IPAddress(ip.Slice(24,16)).ToString(); tcp = ip.Slice(header, total - header);
        }
        else return null;
        if (tcp.Length < 20) return null;
        var tcpHeader = (tcp[12] >> 4) * 4; if (tcpHeader < 20 || tcpHeader > tcp.Length) return null;
        return new(new(source, BinaryPrimitives.ReadUInt16BigEndian(tcp), destination, BinaryPrimitives.ReadUInt16BigEndian(tcp[2..])),
            BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]), tcp[tcpHeader..].ToArray(), (tcp[13] & 2) != 0, (tcp[13] & 1) != 0, (tcp[13] & 4) != 0, receivedAt)
        { Acknowledgement = (tcp[13] & 0x10) != 0 };
    }
}

/// <summary>One capture worker and bounded live streams. Raw diagnostics require an explicit trace opt-in.</summary>
public sealed class PassiveCaptureService(StreamingInventory inventory, Func<IPassivePacketSource> sourceFactory, Func<TcpEndpoint,bool> allowServerEndpoint,Action<GameFrame,string,double>? observeFrame=null,CapturePacketTrace? packetTrace=null) : IDisposable
{
    private sealed record Connection(RollingTcpStream Stream, string Id, double LastSeen, uint? SynSequence);
    private readonly object gate = new();
    private readonly Dictionary<(string Source,TcpEndpoint Endpoint),Connection> connections = [];
    private CancellationTokenSource? cancellation;
    private bool disposed;
    private long lifetime;
    private long payloadBytes,decodedFrames,invalidations;
    private readonly CapturePacketTrace? trace = packetTrace ?? CapturePacketTrace.FromEnvironment();
    public CaptureMetrics MetricsSnapshot()
    {lock(gate)return new(CapturedPackets,payloadBytes,decodedFrames,connections.Count,connections.Values.Sum(c=>c.Stream.BufferedBytes),invalidations);}
    public bool IsRunning { get { lock (gate) return cancellation is not null; } }
    public long CapturedPackets { get; private set; }
    public int BufferedBytes { get { lock (gate) return connections.Values.Sum(c => c.Stream.BufferedBytes); } }
    public string? LastError { get; private set; }
    public Exception? LastException{get;private set;}
    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource owned;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cancellation is not null) throw new InvalidOperationException("Capture already running");
            cancellation = owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); LastError = null;LastException=null; CapturedPackets = 0;payloadBytes=0;decodedFrames=0;invalidations=0;
        }
        return Task.Run(() =>
        {
            try { using var source = sourceFactory(); source.Run(Accept, Invalidate, owned.Token); }
            catch (OperationCanceledException) when (owned.IsCancellationRequested) { }
            catch (Exception error) { Invalidate(error.Message); throw; }
            finally
            {
                lock (gate) { connections.Clear(); inventory.Invalidate("Capture stopped"); cancellation = null; owned.Dispose(); trace?.Dispose(); }
            }
        }, CancellationToken.None);
    }
    private void Accept(TcpPacket packet)
    {
        lock (gate)
        {
            if (cancellation?.IsCancellationRequested != false || !allowServerEndpoint(packet.Endpoint)) return;
            var streamKey=(packet.CaptureSource,packet.Endpoint);
            CapturedPackets++;payloadBytes+=packet.Payload.Length;
            foreach (var expired in connections.Where(p => packet.ReceivedAt - p.Value.LastSeen > 120).Select(p => p.Key).ToArray())
            { var prior = connections[expired]; connections.Remove(expired); InvalidateConnection(prior.Id, "Connection expired; full sync required"); }
            if (packet.Reset || packet.Fin)
            {
                if (connections.Remove(streamKey, out var closed))
                { trace?.Record(packet, closed.Id, closed.Stream.TraceState(), null); InvalidateConnection(closed.Id, "Connection closed"); }
                return;
            }
            if (packet.Payload.Length == 0 && !packet.Syn) return;
            if (!connections.TryGetValue(streamKey, out var connection) || packet.Syn && connection.SynSequence != packet.Sequence)
            {
                if (connection is not null) InvalidateConnection(connection.Id, "Connection replaced; full sync required");
                if (connections.Count >= CaptureMetrics.ConnectionLimit) { connections.Clear(); inventory.Invalidate("Connection budget exceeded");invalidations++; }
                connection = new(new RollingTcpStream(inventory.ValidateFullSync,inventory.Protocol.Login), (++lifetime).ToString(System.Globalization.CultureInfo.InvariantCulture), packet.ReceivedAt, packet.Syn ? packet.Sequence : null);
                connections[streamKey] = connection;
            }
            var before = trace is null ? null : connection.Stream.TraceState();
            string? traceError = null;
            try
            {
                foreach (var frame in connection.Stream.Feed(packet.Sequence, packet.Payload, packet.Syn, packet.Acknowledgement)){observeFrame?.Invoke(frame,connection.Id,packet.ReceivedAt);inventory.Accept(frame, connection.Id, packet.ReceivedAt);decodedFrames++;}
                connections[streamKey] = connection with { LastSeen = packet.ReceivedAt };
                if (connections.Values.Sum(c => c.Stream.BufferedBytes) > CaptureMetrics.BufferLimit)
                    throw new InvalidDataException("Capture decoder byte budget exceeded");
                if(connection.Id==inventory.ActiveConnectionId)
                    inventory.Advance(packet.ReceivedAt, connection.Stream.Healthy);
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
            {
                traceError = error.Message;
                error.Data["capture_source"]=packet.CaptureSource;error.Data["endpoint"]=packet.Endpoint.ToString();
                error.Data["sequence"]=packet.Sequence;error.Data["payload_bytes"]=packet.Payload.Length;error.Data["buffered_bytes"]=connection.Stream.BufferedBytes;
                connections.Remove(streamKey);InvalidateConnection(connection.Id,error.Message);LastError=error.Message;LastException=error;
                // A missed connection replacement can reuse the endpoint with a new sequence.
                // Discard the damaged stream, but keep this packet: it can contain the map's
                // only full sync. The fresh decoder accepts only a validated full-sync frame.
                if(error.Message is "Conflicting TCP reorder" or "Conflicting TCP retransmit" or "TCP reorder budget exceeded")
                {
                    var replacement=new Connection(new RollingTcpStream(inventory.ValidateFullSync,inventory.Protocol.Login),(++lifetime).ToString(System.Globalization.CultureInfo.InvariantCulture),packet.ReceivedAt,null);
                    try
                    {
                        foreach(var frame in replacement.Stream.Feed(packet.Sequence,packet.Payload))
                        {observeFrame?.Invoke(frame,replacement.Id,packet.ReceivedAt);inventory.Accept(frame,replacement.Id,packet.ReceivedAt);decodedFrames++;}
                        connections[streamKey]=replacement;
                        if(replacement.Id==inventory.ActiveConnectionId)inventory.Advance(packet.ReceivedAt,replacement.Stream.Healthy);
                    }
                    catch(Exception recoveryError)when(recoveryError is InvalidDataException or ArgumentException or OverflowException)
                    {InvalidateConnection(replacement.Id,recoveryError.Message);}
                }
            }
            finally { trace?.Record(packet, connection.Id, before, connection.Stream.TraceState(), traceError); }
        }
    }
    private void InvalidateConnection(string connectionId, string reason)
    { if (inventory.ActiveConnectionId == connectionId){inventory.Invalidate(reason);invalidations++;} }
    private void Invalidate(string reason)
    { lock (gate) { connections.Clear(); inventory.Invalidate(reason); LastError = reason;invalidations++; } }
    public void Stop() { lock (gate) cancellation?.Cancel(); }
    public void Dispose() { lock (gate) { disposed = true; cancellation?.Cancel(); if(cancellation is null)trace?.Dispose(); } GC.SuppressFinalize(this); }
}

