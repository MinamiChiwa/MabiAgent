using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MabiAgent.Capture;

public sealed record GameFrame(int Offset, uint Opcode, byte Flag, byte[] Body);
public sealed record FrameBatch(IReadOnlyList<GameFrame> Frames, int Consumed);

public static class GameProtocol
{
    public const uint LoginOpcode = 0x5028, ProfileOpcode = 0x1d731, InventoryOpcode = 0x1d668;
    public const int MaxBody = 8 * 1024 * 1024;
    public static FrameBatch ParseComplete(ReadOnlySpan<byte> bytes, int start = 0, int maxBody = MaxBody, int maxFrames = 200000)
    {
        if (start < 0 || start > bytes.Length || maxBody <= 0 || maxFrames <= 0) throw new ArgumentOutOfRangeException(nameof(start));
        var frames = new List<GameFrame>(); var offset = start;
        while (bytes.Length - offset >= 9)
        {
            if (frames.Count == maxFrames) throw new InvalidDataException("Frame count limit exceeded");
            var header = bytes[offset..];
            var opcode = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]); var flag = header[8];
            if (flag > 1 || length > maxBody) throw new InvalidDataException("Invalid frame flag or body limit");
            if (length > bytes.Length - offset - 9) break;
            frames.Add(new(offset, opcode, flag, bytes.Slice(offset + 9, (int)length).ToArray()));
            offset += 9 + (int)length;
        }
        return new(frames.AsReadOnly(), offset);
    }
    public static byte[] DecodeBody(byte flag, ReadOnlySpan<byte> body, int maxOutput = MaxBody)
    {
        if (maxOutput <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutput));
        if (flag == 0) return body.Length <= maxOutput ? body.ToArray() : throw new InvalidDataException("Raw body limit exceeded");
        if (flag != 1) throw new InvalidDataException("Unknown compression flag");
        using var decoder = new BrotliDecoder();
        using var output = new MemoryStream();
        var chunk = new byte[Math.Min(65536, maxOutput + (maxOutput < int.MaxValue ? 1 : 0))];
        while (true)
        {
            var allowed = (int)Math.Min(chunk.Length, (long)maxOutput - output.Length + 1);
            var status = decoder.Decompress(body, chunk.AsSpan(0, allowed), out var consumed, out var written);
            body = body[consumed..]; output.Write(chunk, 0, written);
            if (output.Length > maxOutput) throw new InvalidDataException("Decompressed body limit exceeded");
            if (status == OperationStatus.Done)
            {
                if (!body.IsEmpty) throw new InvalidDataException("Trailing compressed bytes");
                return output.ToArray();
            }
            if (status == OperationStatus.InvalidData || (status == OperationStatus.NeedMoreData && body.IsEmpty) || consumed + written == 0)
                throw new InvalidDataException("Invalid or truncated compressed body");
        }
    }
}

public sealed record ConsumableStack(uint ItemId, string StackKeyHex, long StackableId, long Quantity,
    long AcquiredTimeRaw, long LastAmountChangedTimeRaw, bool IsJunked, bool IsLocked,
    string DescriptorText, int Offset, int ByteLength)
{
    public const string KeyKind = "consumable-stack-key-wire-v1";
}

public static class ConsumableCodec
{
    internal static readonly Encoding StrictUtf16 = new UnicodeEncoding(false, false, true);
    public static ConsumableStack ParseRecord(ReadOnlySpan<byte> body, int offset = 0, int? end = null)
    {
        var limit = end ?? body.Length;
        if (offset < 0 || limit > body.Length || limit - offset < 224) throw new InvalidDataException("Truncated consumable record");
        var bytes = body.Slice(offset, limit - offset);
        var textLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[17..]);
        if (textLength > 4096 || textLength % 2 != 0) throw new InvalidDataException("Invalid descriptor text length");
        var size = 95 + (int)textLength; var recordLength = 2 * size + 34;
        if (recordLength > bytes.Length) throw new InvalidDataException("Truncated consumable descriptor");
        var key = bytes[..size]; var item = BinaryPrimitives.ReadUInt32LittleEndian(key);
        if (item == 0 || key[12] > 1 || key[45 + (int)textLength] > 1 || !key.SequenceEqual(bytes.Slice(size + 8, size)))
            throw new InvalidDataException("Consumable key invalid or copies disagree");
        string text;
        try { text = StrictUtf16.GetString(key.Slice(21, (int)textLength)); }
        catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid UTF16 descriptor", e); }
        var tail = bytes.Slice(2 * size + 8, 26);
        var quantity = BinaryPrimitives.ReadInt64LittleEndian(tail[16..]);
        if (quantity < 0 || tail[24] > 1 || tail[25] > 1) throw new InvalidDataException("Invalid consumable amount or flags");
        return new(item, Convert.ToHexStringLower(key), BinaryPrimitives.ReadInt64LittleEndian(bytes[size..]), quantity,
            BinaryPrimitives.ReadInt64LittleEndian(tail), BinaryPrimitives.ReadInt64LittleEndian(tail[8..]),
            tail[24] == 1, tail[25] == 1, text, offset, recordLength);
    }
    public static IReadOnlyList<ConsumableStack> ParseUpdates(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty || body.Length > GameProtocol.MaxBody) throw new InvalidDataException("Inventory update body limit");
        var rows = new List<ConsumableStack>(); var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset < body.Length;)
        {
            if (rows.Count >= 10000) throw new InvalidDataException("Inventory record limit");
            var row = ParseRecord(body, offset);
            if (!keys.Add(row.StackKeyHex)) throw new InvalidDataException("Duplicate inventory key");
            rows.Add(row); offset += row.ByteLength;
        }
        return rows.AsReadOnly();
    }
}
