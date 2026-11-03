using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MabiAgent.Capture;
using Xunit;

namespace MabiAgent.Capture.Tests;

public class ProtocolTests
{
    internal static byte[] Frame(uint opcode, byte[] body, byte flag = 0)
    {
        var wire = new byte[9 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(wire, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(wire.AsSpan(4), body.Length);
        wire[8] = flag; body.CopyTo(wire, 9); return wire;
    }
    internal static byte[] Record(uint item = 123, long quantity = 7, long stackId = 9007199254740993, string text = "")
    {
        var name = Encoding.Unicode.GetBytes(text);
        var size = 95 + name.Length;
        var result = new byte[2 * size + 34];
        BinaryPrimitives.WriteUInt32LittleEndian(result, item);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(17), name.Length);
        name.CopyTo(result, 21);
        result.AsSpan(0, size).CopyTo(result.AsSpan(size + 8));
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(size), stackId);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(result.Length - 26), long.MaxValue);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(result.Length - 18), 17);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(result.Length - 10), quantity);
        return result;
    }
    [Fact] public void FramesKeepIncompleteTailAndRejectBadFlags()
    {
        var wire = Frame(0x5028, [1, 2, 3]);
        Assert.Empty(GameProtocol.ParseComplete(wire[..10]).Frames);
        Assert.Equal(12, GameProtocol.ParseComplete(wire).Consumed);
        wire[8] = 2;
        Assert.Throws<InvalidDataException>(() => GameProtocol.ParseComplete(wire));
    }
    [Fact] public void BrotliIsStrictAndBounded()
    {
        using var output = new MemoryStream();
        using (var encoder = new BrotliStream(output, CompressionLevel.SmallestSize, true)) encoder.Write(new byte[10000]);
        var compressed = output.ToArray();
        Assert.Equal(10000, GameProtocol.DecodeBody(1, compressed).Length);
        Assert.Throws<InvalidDataException>(() => GameProtocol.DecodeBody(1, compressed, 100));
        Assert.Throws<InvalidDataException>(() => GameProtocol.DecodeBody(1, compressed[..^1]));
        Assert.Throws<InvalidDataException>(() => GameProtocol.DecodeBody(1, compressed.Concat(new byte[] { 1 }).ToArray()));
    }
    [Fact] public void ConsumableKeepsExactLongsAndChecksDuplicates()
    {
        var bytes = Record(text: "材料");
        var row = ConsumableCodec.ParseRecord(bytes);
        Assert.Equal(9007199254740993, row.StackableId);
        Assert.Equal(long.MaxValue, row.AcquiredTimeRaw);
        Assert.Equal("材料", row.DescriptorText);
        Assert.Equal(7, row.Quantity);
        bytes[103 + 4] ^= 1;
        Assert.Throws<InvalidDataException>(() => ConsumableCodec.ParseRecord(bytes));
    }
    [Fact] public void TcpReorderWrapRetransmitAndRelease()
    {
        var stream = new RollingTcpStream();
        stream.Feed(uint.MaxValue - 2, [], true);
        var wire = Encoding.ASCII.GetBytes("MMCG").Concat(Frame(0x5028, [1])).ToArray();
        Assert.Empty(stream.Feed(3, wire[5..]));
        Assert.Single(stream.Feed(uint.MaxValue - 1, wire[..5]));
        Assert.True(stream.Healthy);
        Assert.Empty(stream.Feed(uint.MaxValue - 1, wire));
        var next = unchecked((uint)(uint.MaxValue - 1 + (ulong)wire.Length));
        var traffic = Frame(1234, new byte[1024]);
        for (var i = 0; i < 1000; i++) { Assert.Single(stream.Feed(next, traffic)); next = unchecked(next + (uint)traffic.Length); }
        Assert.True(stream.BufferedBytes <= 65536);
        Assert.Throws<InvalidDataException>(() => stream.Feed(next - 1, [255]));
    }
    [Fact] public void MidstreamRequiresValidatedFullSync()
    {
        var stream = new RollingTcpStream(f => f.Body.SequenceEqual(new byte[] { 42 }));
        var first = Frame(0x5028, [1]);
        Assert.Empty(stream.Feed(100, first));
        Assert.False(stream.Healthy);
        Assert.Single(stream.Feed((uint)(100 + first.Length), Frame(0x5028, [42])));
        Assert.True(stream.Healthy);
    }
}
