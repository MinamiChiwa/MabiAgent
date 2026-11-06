using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MabiAgent.Capture;

internal sealed record ClientTable(
    uint Field,
    uint Rows,
    uint Size,
    ulong DataOffset,
    ulong MetaOffset,
    byte[] Block,
    IReadOnlyList<ulong> DataOffsets);

internal static class ClientNameTable
{
    private const uint MaxTables = 4096;
    private const uint MaxTableRows = 1_000_000;
    private const uint MaxStringGroups = 10_000;
    private const uint MaxStringRows = 2_000_000;
    private const int MaxStringReferences = 4096;
    private const int MaxTextBytes = 16 * 1024 * 1024;

    public static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static IReadOnlyDictionary<string, ClientTable> ReadTables(byte[] meta, byte[] texts, CancellationToken cancellationToken)
    {
        var reader = new ClientDataReader(meta);
        var count = reader.Var();
        if (count == 0 || count > MaxTables) throw new InvalidDataException("客户端表数量异常");
        var builders = new List<TableBuilder>(checked((int)count));
        var names = new HashSet<string>(StringComparer.Ordinal);
        var totalRows = 0UL;
        ulong dataOffset = 0;
        for (var index = 0u; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nameOffset = reader.Var();
            var name = ReadString(texts, checked((int)nameOffset));
            var field = reader.Var();
            var rows = reader.Var();
            var size = reader.Var();
            var delta = reader.Var();
            if (!names.Add(name) || rows > MaxTableRows) throw new InvalidDataException("重复表或行数异常");
            totalRows = checked(totalRows + rows);
            if (totalRows > 4_000_000) throw new InvalidDataException("客户端表总行数超过预算");
            builders.Add(new TableBuilder(name, field, rows, size, dataOffset));
            dataOffset = checked(dataOffset + delta);
        }

        foreach (var table in builders)
        {
            var offsets = new List<ulong>(checked((int)table.Rows));
            ulong current = table.DataOffset;
            for (var row = 0u; row < table.Rows; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                offsets.Add(current);
                current = checked(current + reader.Var());
            }
            table.DataOffsets = offsets;
        }

        var result = new Dictionary<string, ClientTable>(StringComparer.Ordinal);
        foreach (var table in builders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metaOffset = checked((ulong)reader.Offset);
            var block = reader.Bytes(table.Size, "客户端表");
            result.Add(table.Name, new ClientTable(table.Field, table.Rows, table.Size, table.DataOffset,
                metaOffset, block, table.DataOffsets));
        }
        reader.EnsureEnd();
        return result;
    }

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadStringTables(
        byte[] data, CancellationToken cancellationToken)
    {
        var reader = new ClientDataReader(data);
        var count = reader.UInt32();
        if (count > MaxStringGroups) throw new InvalidDataException("文本组数量异常");
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var totalRows = 0UL;
        for (var groupIndex = 0u; groupIndex < count; groupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = reader.Text();
            reader.Take(1);
            var rows = ReadStringRows(reader, ref totalRows, cancellationToken, numericKeys: false);
            result[name] = rows;
            var subCount = reader.UInt32();
            if (subCount > MaxStringGroups) throw new InvalidDataException("文本子组数量异常");
            for (var subIndex = 0u; subIndex < subCount; subIndex++)
            {
                var sub = reader.Text();
                result[name + "/" + sub] = ReadStringRows(reader, ref totalRows, cancellationToken, numericKeys: true);
            }
        }
        reader.EnsureEnd();
        return result;
    }

    private static IReadOnlyDictionary<string, string> ReadStringRows(
        ClientDataReader reader, ref ulong totalRows, CancellationToken cancellationToken, bool numericKeys)
    {
        var count = reader.UInt32();
        totalRows = checked(totalRows + count);
        if (totalRows > MaxStringRows) throw new InvalidDataException("文本行数超过预算");
        var rows = new Dictionary<string, string>(checked((int)count), StringComparer.Ordinal);
        for (var index = 0u; index < count; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var key = numericKeys ? reader.UInt32().ToString(CultureInfo.InvariantCulture) : reader.Text();
            rows[key] = reader.Text();
        }
        return rows;
    }

    public static string ReadString(byte[] data, int offset)
    {
        if (offset < 0) throw new InvalidDataException("客户端字符串引用为负偏移");
        using var output = new MemoryStream();
        var at = offset;
        for (var reference = 0; reference < MaxStringReferences; reference++)
        {
            if (at < 0 || at >= data.Length) throw new InvalidDataException("客户端字符串引用截断");
            var token = data[at];
            if (token == 0) return DecodeText(output.ToArray());
            if ((token & 0x80) == 0)
            {
                var length = (int)token;
                if (at > data.Length - 1 - length) throw new InvalidDataException("客户端字符串字面量截断");
                output.Write(data, at + 1, length);
                at += 1 + length;
                continue;
            }

            if (at >= data.Length - 1) throw new InvalidDataException("客户端字符串引用截断");
            var code = ((token & 0x7f) << 8) | data[at + 1];
            var referencedLength = (code & 31) + 1;
            var source = at - 1 - (code >> 5);
            if (source < 0 || source > data.Length - referencedLength)
                throw new InvalidDataException("客户端字符串反向引用越界");
            output.Write(data, source, referencedLength);
            at += 2;
        }
        throw new InvalidDataException("客户端字符串引用次数超过预算");
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("客户端文本不是有效 UTF-8", error); }
    }

    private sealed class TableBuilder(
        string name, uint field, uint rows, uint size, ulong dataOffset)
    {
        public string Name { get; } = name;
        public uint Field { get; } = field;
        public uint Rows { get; } = rows;
        public uint Size { get; } = size;
        public ulong DataOffset { get; } = dataOffset;
        public IReadOnlyList<ulong> DataOffsets { get; set; } = Array.Empty<ulong>();
    }
}

internal sealed class ClientDataReader(byte[] data)
{
    public int Offset { get; private set; }

    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || Offset > data.Length - count) throw new InvalidDataException("客户端表截断");
        var value = data.AsSpan(Offset, count);
        Offset += count;
        return value;
    }

    public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public uint Var()
    {
        uint value = 0;
        for (var shift = 0; shift <= 28; shift += 7)
        {
            var next = Take(1)[0];
            if (shift == 28 && next > 15) throw new InvalidDataException("客户端变长整数溢出");
            value |= (uint)(next & 127) << shift;
            if (next < 128) return value;
        }
        throw new InvalidDataException("客户端变长整数溢出");
    }

    public string Text()
    {
        var length = UInt32();
        if (length > MaxTextBytes) throw new InvalidDataException($"客户端文本超过大小预算（offset {Offset - 4}，length {length}）");
        var size = checked((int)length);
        try { return ClientNameTable.StrictUtf8.GetString(Take(size)); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("客户端文本不是有效 UTF-8", error); }
    }

    public byte[] Bytes(uint count, string label)
    {
        if (count > MaxTextBytes) throw new InvalidDataException($"{label}超过大小预算");
        return Take(checked((int)count)).ToArray();
    }

    public void EnsureEnd()
    {
        if (Offset != data.Length) throw new InvalidDataException("客户端表存在未解析字节");
    }

    private const int MaxTextBytes = 16 * 1024 * 1024;
}
