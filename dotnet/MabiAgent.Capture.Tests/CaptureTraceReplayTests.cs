using System.Text.Json;
using MabiAgent.Capture;
using Xunit;
using Xunit.Abstractions;

namespace MabiAgent.Capture.Tests;

public sealed class CaptureTraceReplayTests(ITestOutputHelper output)
{
    // Explicit offline replay of an existing opt-in trace. No Npcap or input backend is created.
    [CaptureTraceReplayFact]
    public async Task RecordedPacketsKeepTheirStreamAndInventoryCoverage()
    {
        var path = Environment.GetEnvironmentVariable("MABI_CAPTURE_TRACE_REPLAY")!;
        var layout = ClientLayoutReader.Read(Environment.GetEnvironmentVariable("MABI_CLIENT_DIRECTORY")!);
        var decoder = layout.CreateDecoder();
        var inventory = new StreamingInventory(decoder.Decode, "offline-trace-replay", protocol: layout.Protocol, decodeUpdates: decoder.DecodeUpdates);
        var packets = new List<TcpPacket>();
        using (var input = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            while (input.ReadLine() is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var row = json.RootElement;
                packets.Add(new(JsonSerializer.Deserialize<TcpEndpoint>(row.GetProperty("Endpoint"))!,
                    row.GetProperty("Sequence").GetUInt32(), Convert.FromBase64String(row.GetProperty("payload_base64").GetString()!),
                    row.GetProperty("Syn").GetBoolean(), row.GetProperty("Fin").GetBoolean(), row.GetProperty("Reset").GetBoolean(),
                    row.GetProperty("ReceivedAt").GetDouble(), row.GetProperty("CaptureSource").GetString()!)
                { Acknowledgement = row.GetProperty("Acknowledgement").GetBoolean() });
            }
        Assert.NotEmpty(packets);
        var counts = new Dictionary<uint, int>();
        var syncs = new List<object>();
        InventorySnapshot? final = null;
        CaptureMetrics? liveMetrics = null;
        var source = new ReplaySource(packets, inventory, (snapshot, metrics) => { final = snapshot; liveMetrics = metrics; });
        PassiveCaptureService? service = null;
        using (service = new PassiveCaptureService(inventory, () => source, _ => true, (frame, connection, at) =>
        {
            counts[frame.Opcode] = counts.GetValueOrDefault(frame.Opcode) + 1;
            if (frame.Opcode == layout.Protocol.Login)
            {
                var login = decoder.Decode(GameProtocol.DecodeBody(frame.Flag, frame.Body));
                syncs.Add(new { connection, at, login.CharacterCode, login.Gold, decoder.WalletDiagnostic,
                    bottle = login.Containers.ToDictionary(p => p.Key.ToString(), p => p.Value.Where(r => r.ItemId == 1202292919).Sum(r => r.Quantity)) });
            }
        }))
        {
            source.Metrics = service.MetricsSnapshot;
            await service.RunAsync();
            Assert.Null(service.LastException);
        }
        Assert.NotNull(final); Assert.NotNull(liveMetrics);
        Assert.Null(final.Error); Assert.NotNull(final.VerifiedThrough);
        Assert.True(final.FullSyncRevision > 0);
        Assert.True(final.VerifiedThrough > final.FullSyncAt);
        Assert.Equal(counts.Values.Sum(), liveMetrics.DecodedFrames);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            offline_replay = true, packets = packets.Count, layout.Protocol, metrics = liveMetrics,
            inventory_frames = counts.GetValueOrDefault(layout.Protocol.Inventory),
            final.Revision, final.FullSyncRevision, final.FullSyncAt, final.VerifiedThrough,
            bottle_bag = final.Quantity(InventoryContainer.Backpack, 1202292919),
            bottle_storage = final.Quantity(InventoryContainer.CharacterStorage, 1202292919), syncs
        }));
    }

    private sealed class ReplaySource(IReadOnlyList<TcpPacket> packets, StreamingInventory inventory, Action<InventorySnapshot, CaptureMetrics> completed) : IPassivePacketSource
    {
        public Func<CaptureMetrics> Metrics { private get; set; } = () => throw new InvalidOperationException("Replay service not attached");
        public void Run(Action<TcpPacket> onPacket, Action<string> onGap, CancellationToken cancellationToken)
        {
            foreach (var packet in packets) onPacket(packet);
            completed(inventory.Snapshot(), Metrics());
        }
        public void Dispose() { }
    }
}

public sealed class CaptureTraceReplayFactAttribute : FactAttribute
{
    public CaptureTraceReplayFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MABI_CAPTURE_TRACE_REPLAY"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MABI_CLIENT_DIRECTORY")))
            Skip = "Set MABI_CAPTURE_TRACE_REPLAY and MABI_CLIENT_DIRECTORY for an explicit offline packet replay.";
    }
}
