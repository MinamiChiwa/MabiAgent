using System.Text.Json;
using MabiAgent.Capture;
using Xunit;

namespace MabiAgent.Capture.Tests;

public class CapturePacketTraceTests
{
    private static readonly TcpEndpoint Endpoint = new("127.0.0.1", 1111, "127.0.0.1", 5678);

    [Fact]
    public async Task TraceRecordsAllowedRawBytesAndStateWithoutChangingFrames()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            var login = ProtocolTests.Frame(GameProtocol.LoginOpcode, [1]);
            var update = ProtocolTests.Frame(GameProtocol.InventoryOpcode, ProtocolTests.Record(quantity: 10));
            var who = LoginIdentity.Join(9007199254740993, InventoryTests.Profile(9007199254740993), LoginIdentity.AccountCodec);
            var inventory = new StreamingInventory(_ => InventoryTests.Login(), "trace", [who]);
            using var trace = new CapturePacketTrace(path);
            using var service = new PassiveCaptureService(inventory, () => new Source(onPacket =>
            {
                onPacket(new(Endpoint with { SourcePort = 2222 }, 10, [99], false, false, false, 1));
                onPacket(new(Endpoint, 100, login, false, false, false, 2));
                onPacket(new(Endpoint, 100u + (uint)login.Length, update[..12], false, false, false, 3));
                onPacket(new(Endpoint, 112u + (uint)login.Length, update[12..], false, false, false, 4));
                Assert.Equal(10, inventory.Snapshot().Quantity(InventoryContainer.Backpack, 123));
                Assert.Equal(4, inventory.Snapshot().VerifiedThrough);
            }), endpoint => endpoint == Endpoint, packetTrace: trace);
            await service.RunAsync();
            var rows = File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Assert.Equal(3, rows.Length);
            Assert.Equal(login, Convert.FromBase64String(rows[0].GetProperty("payload_base64").GetString()!));
            Assert.True(rows[0].GetProperty("after").GetProperty("framed").GetBoolean());
            Assert.Equal(update.Length - 9, rows[1].GetProperty("after").GetProperty("head_body_length").GetInt32());
            Assert.Equal(12, rows[1].GetProperty("after").GetProperty("contiguous_bytes").GetInt32());
            Assert.Equal(0, rows[2].GetProperty("after").GetProperty("contiguous_bytes").GetInt32());
            Assert.Null(trace.WriteError);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TraceStopsAtPacketOrByteBudget(bool packetLimit)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            using var trace = new CapturePacketTrace(path, maxPackets: packetLimit ? 1 : 10, maxBytes: packetLimit ? 16384 : 1);
            var inventory = new StreamingInventory(_ => InventoryTests.Login(), "trace");
            using var service = new PassiveCaptureService(inventory, () => new Source(onPacket =>
            {
                onPacket(new(Endpoint, 100, ProtocolTests.Frame(GameProtocol.LoginOpcode, [1]), false, false, false, 1));
                onPacket(new(Endpoint, 110, ProtocolTests.Frame(123, [2]), false, false, false, 2));
            }), _ => true, packetTrace: trace);
            await service.RunAsync();
            Assert.Equal(2, service.MetricsSnapshot().DecodedFrames);
            Assert.Equal(packetLimit ? 1 : 0, File.Exists(path) ? File.ReadAllLines(path).Length : 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TraceNeverOverwritesEvidenceOrFailsTheCapture()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            File.WriteAllText(path, "existing evidence");
            using var trace = new CapturePacketTrace(path);
            var inventory = new StreamingInventory(_ => InventoryTests.Login(), "trace");
            using var service = new PassiveCaptureService(inventory, () => new Source(onPacket =>
                onPacket(new(Endpoint, 100, ProtocolTests.Frame(GameProtocol.LoginOpcode, [1]), false, false, false, 1))), _ => true, packetTrace: trace);
            await service.RunAsync();
            Assert.Equal(1, service.MetricsSnapshot().DecodedFrames);
            Assert.NotNull(trace.WriteError);
            Assert.Null(service.LastException);
            Assert.Equal("existing evidence", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    private sealed class Source(Action<Action<TcpPacket>> run) : IPassivePacketSource
    {
        public void Run(Action<TcpPacket> onPacket, Action<string> onGap, CancellationToken cancellationToken) => run(onPacket);
        public void Dispose() { }
    }
}
