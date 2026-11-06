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
    [Theory][InlineData(100u)][InlineData(4294967286u)]
    public void AckKeepAliveGarbageOctetDoesNotResetOrAdvanceTheStream(uint sequence)
    {
        var stream = new RollingTcpStream(_ => true);
        var login = Frame(GameProtocol.LoginOpcode, [42]);
        Assert.Single(stream.Feed(sequence, login));
        var next = unchecked(sequence + (uint)login.Length);
        Assert.Empty(stream.Feed(unchecked(next - 1), [0], acknowledgement: true));
        Assert.True(stream.Healthy);
        Assert.Single(stream.Feed(next, Frame(123, [1, 2])));
    }
    [Fact]
    public void AckDataOverlapStillRejectsConflictingBytes()
    {
        var stream = new RollingTcpStream(_ => true);
        var login = Frame(GameProtocol.LoginOpcode, [42, 43]);
        Assert.Single(stream.Feed(100, login));
        var next = 100u + (uint)login.Length;
        Assert.Throws<InvalidDataException>(() => stream.Feed(next - 2, [0], acknowledgement: true));
        Assert.Throws<InvalidDataException>(() => stream.Feed(next - 1, [0, 1], acknowledgement: true));
        Assert.Throws<InvalidDataException>(() => stream.Feed(next - 1, [0]));
    }
    [Theory][InlineData(100u)][InlineData(4294967280u)]
    public void MidstreamWrapAndChangedSegmentationAfterHistoryRolloverDoNotConflict(uint sequence)
    {
        var stream = new RollingTcpStream(_ => true);
        var first = Frame(GameProtocol.LoginOpcode, [42]);
        Assert.Single(stream.Feed(sequence, first));
        sequence = unchecked(sequence + (uint)first.Length);
        var traffic = Frame(1234, Enumerable.Range(0, 90000).Select(i => (byte)i).ToArray());
        var next = unchecked(sequence + (uint)traffic.Length);
        Assert.Empty(stream.Feed(unchecked(sequence + 5000), traffic[5000..12000]));
        Assert.Empty(stream.Feed(unchecked(sequence + 10000), traffic[10000..]));
        Assert.Single(stream.Feed(sequence, traffic[..7000]));
        Assert.True(stream.Healthy);
        Assert.Empty(stream.Feed(sequence, traffic));
        var tail = Frame(1235, [1, 2, 3]);
        Assert.Single(stream.Feed(unchecked(next - 100), traffic[^100..].Concat(tail).ToArray()));
        Assert.True(stream.Healthy);
        Assert.Throws<InvalidDataException>(() => stream.Feed(unchecked(next - 1), [255]));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ReorderedRetransmissionMayChangeLengthWhenOverlappingBytesAgree(bool shorter)
    {
        var stream=new RollingTcpStream(_=>true);stream.Feed(99,[],true);
        var wire=Frame(GameProtocol.LoginOpcode,Enumerable.Range(0,24).Select(x=>(byte)x).ToArray());
        Assert.Empty(stream.Feed(105,shorter?wire[5..]:wire[5..12]));
        Assert.Empty(stream.Feed(105,shorter?wire[5..12]:wire[5..]));
        var frame=Assert.Single(stream.Feed(100,wire[..5]));
        Assert.Equal(wire[9..],frame.Body);Assert.True(stream.Healthy);
    }
    [Fact] public void ConflictingReorderedOverlapIsRejectedBeforeMissingPrefixArrives()
    {
        var stream=new RollingTcpStream(_=>true);stream.Feed(99,[],true);
        Assert.Empty(stream.Feed(110,[1,2,3,4]));
        Assert.Throws<InvalidDataException>(()=>stream.Feed(112,[8,4,5]));
    }
    [Fact] public void OverlappingReorderedSegmentsAreCountedOnceAndDrainedInOrder()
    {
        var stream=new RollingTcpStream(_=>true);stream.Feed(99,[],true);
        var wire=Frame(GameProtocol.LoginOpcode,new byte[160]);
        for(var offset=10;offset<wire.Length;offset++)Assert.Empty(stream.Feed((uint)(100+offset),wire[offset..]));
        Assert.Equal(wire.Length-10,stream.BufferedBytes);
        Assert.Single(stream.Feed(100,wire[..10]));Assert.True(stream.Healthy);
    }
    [Fact] public void RealReorderGapStillExhaustsBoundedBudget()
    {
        var stream=new RollingTcpStream(_=>true);stream.Feed(99,[],true);
        Assert.Throws<InvalidDataException>(()=>stream.Feed(100u+RollingTcpStream.MaxBuffer+1,[1]));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void MissingPrefixCanArriveWithLargeMatchingOverlapWithoutDoubleCounting(bool partialOverlap)
    {
        var stream=new RollingTcpStream(_=>true);stream.Feed(99,[],true);
        var wire=Frame(GameProtocol.LoginOpcode,new byte[RollingTcpStream.MaxBuffer/2+16384]);
        Assert.Empty(stream.Feed(110,wire[10..]));
        var frames=stream.Feed(100,partialOverlap?wire[..^10]:wire);
        Assert.Equal(wire[9..],Assert.Single(frames).Body);Assert.True(stream.Healthy);
        Assert.True(stream.BufferedBytes<=RollingTcpStream.HistoryLimit);
    }
}
