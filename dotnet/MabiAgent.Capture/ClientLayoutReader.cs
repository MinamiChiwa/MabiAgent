using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MabiAgent.Capture;

public sealed record CaptureProtocol(uint Login, uint Profile, uint Inventory)
{
    // Only explicit offline/test callers use the historical profile.
    public static CaptureProtocol Historical { get; } = new(GameProtocol.LoginOpcode, GameProtocol.ProfileOpcode, GameProtocol.InventoryOpcode);
}
public sealed record ClientCaptureLayout(string AssemblyHash, string MetadataHash, CaptureProtocol Protocol, JsonElement Schema)
{
    public ClientInventoryDecoder CreateDecoder() => new(Schema,requireClientLayout:true);
}

/// <summary>Reads IL2CPP v31 metadata and x64 PE data. Never loads client code or reads process memory.</summary>
public sealed class ClientLayoutReader
{
    private readonly byte[] metadata, assembly;
    private readonly int strings, stringSize, fields, fieldSize, definitions, definitionSize;
    private readonly ulong imageBase;
    private readonly List<(uint Rva, uint Size, uint Offset)> sections = [];
    private readonly Dictionary<string, int> indices = new(StringComparer.Ordinal);
    private readonly string[] names;
    private readonly ulong typeTable, typeCount;
    private readonly CancellationToken cancellation;
    private static int I32(byte[] bytes, int at) => BinaryPrimitives.ReadInt32LittleEndian(Slice(bytes, at, 4));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(bytes, at, 4));
    private static ulong U64(byte[] bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(Slice(bytes, at, 8));
    private static ReadOnlySpan<byte> Slice(byte[] bytes, int at, int count)
    {
        if (at < 0 || count < 0 || at > bytes.Length - count) throw new InvalidDataException("客户端文件数据越界");
        return bytes.AsSpan(at, count);
    }
    private (int Offset, int Size) Pair(int at, int stride = 1)
    {
        var offset = I32(metadata, at); var size = I32(metadata, at + 4);
        _ = Slice(metadata, offset, size);
        if (size % stride != 0 || size / stride > 1_000_000 && stride != 1) throw new InvalidDataException("客户端元数据表格式不支持");
        return (offset, size);
    }
    private string Text(int index)
    {
        if (index < 0 || index >= stringSize) throw new InvalidDataException("客户端字符串索引无效");
        var span = metadata.AsSpan(strings + index, stringSize - index); var end = span.IndexOf((byte)0);
        if (end < 0 || end > 16384) throw new InvalidDataException("客户端字符串长度无效");
        return new UTF8Encoding(false, true).GetString(span[..end]);
    }
    private int Offset(ulong address, int length = 1)
    {
        if (address < imageBase) throw new InvalidDataException("客户端地址无效");
        var rva = address - imageBase;
        foreach (var section in sections)
            if (rva >= section.Rva && rva - section.Rva < section.Size && (ulong)length <= section.Size - (rva - section.Rva))
            {
                var at = checked((int)(section.Offset + rva - section.Rva)); _ = Slice(assembly, at, length); return at;
            }
        throw new InvalidDataException("客户端地址未映射");
    }
    private ulong Pointer(ulong address) => U64(assembly, Offset(address, 8));
    private ClientLayoutReader(byte[] metadata, byte[] assembly, CancellationToken cancellation)
    {
        this.metadata = metadata; this.assembly = assembly; this.cancellation = cancellation;
        if (U32(metadata, 0) != 0xfab11baf || U32(metadata, 4) != 31) throw new InvalidDataException("暂不支持此客户端元数据版本");
        (strings, stringSize) = Pair(24); (fields, fieldSize) = Pair(96, 12); (definitions, definitionSize) = Pair(160, 88);
        if (U32(assembly, 0) % 65536 != 0x5a4d) throw new InvalidDataException("客户端不是有效PE文件");
        var pe = I32(assembly, 60);
        if (U32(assembly, pe) != 0x4550 || BinaryPrimitives.ReadUInt16LittleEndian(Slice(assembly, pe + 4, 2)) != 0x8664 || BinaryPrimitives.ReadUInt16LittleEndian(Slice(assembly, pe + 24, 2)) != 0x20b)
            throw new InvalidDataException("暂不支持此客户端架构");
        imageBase = U64(assembly, pe + 48);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(Slice(assembly, pe + 6, 2));
        var sectionAt = checked(pe + 24 + BinaryPrimitives.ReadUInt16LittleEndian(Slice(assembly, pe + 20, 2)));
        if (count is 0 or > 96) throw new InvalidDataException("客户端PE节数量无效");
        for (var i = 0; i < count; i++)
        {
            var at = checked(sectionAt + i * 40); var size = U32(assembly, at + 16); var raw = U32(assembly, at + 20);
            _ = Slice(assembly, checked((int)raw), checked((int)size)); sections.Add((U32(assembly, at + 12), size, raw));
        }
        names = new string[definitionSize / 88];
        for (var i = 0; i < names.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested(); var at = definitions + i * 88;
            names[i] = Text(I32(metadata, at + 4)) + "." + Text(I32(metadata, at));
            // Generic and nested names can repeat; reject ambiguity only when requested.
            if (!indices.TryAdd(names[i], i)) indices[names[i]] = -1;
        }
        var candidates = new List<(ulong Count, ulong Table)>();
        for (var at = 32; at <= assembly.Length - 32; at += 8)
        {
            if ((at & 0xfffff) == 0) cancellation.ThrowIfCancellationRequested();
            if (U64(assembly, at) != (ulong)names.Length || U64(assembly, at + 16) != (ulong)names.Length) continue;
            try
            {
                var fieldTable = U64(assembly, at + 8); _ = Offset(U64(assembly, at + 24));
                for (var i = 0; i < 16; i++) { var ptr = Pointer(fieldTable + (ulong)i * 8); if (ptr != 0) _ = Offset(ptr); }
                var n = U64(assembly, at - 32); var table = U64(assembly, at - 24);
                if (n is 0 or > 1_000_000) continue;
                _ = Offset(table, checked((int)n * 8)); candidates.Add((n, table));
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException) { }
        }
        if (candidates.Count != 1) throw new InvalidDataException("客户端类型注册表无法唯一定位");
        (typeCount, typeTable) = candidates[0];
    }
    private ulong TypePointer(int index)
    {
        if (index < 0 || (ulong)index >= typeCount) throw new InvalidDataException("客户端类型索引无效");
        return Pointer(typeTable + (ulong)index * 8);
    }
    private string? TypeName(int index) => index < 0 ? null : PointerName(TypePointer(index));
    private string PointerName(ulong pointer, int depth = 0)
    {
        if (depth > 32) throw new InvalidDataException("客户端泛型递归超限");
        var at = Offset(pointer, 12); var data = U64(assembly, at); var bits = U32(assembly, at + 8); var kind = bits >> 16 & 255;
        var name = kind switch
        {
            1 => "void", 2 => "bool", 3 => "char", 4 => "int8", 5 => "uint8", 6 => "int16", 7 => "uint16", 8 => "int32", 9 => "uint32", 10 => "int64", 11 => "uint64", 12 => "float32", 13 => "float64", 14 => "string", 28 => "object",
            17 or 18 => data < (ulong)names.Length ? names[(int)data] : throw new InvalidDataException("客户端类型定义无效"),
            15 or 16 or 29 => PointerName(data, depth + 1) + (kind == 15 ? "*" : kind == 16 ? "&" : "[]"),
            21 => Generic(data, depth + 1),
            19 or 30 => Parameter(data, kind),
            _ => throw new InvalidDataException("不支持的客户端类型：" + kind)
        };
        return (bits & 1u << 29) != 0 ? name + "&" : name;
    }
    private string Parameter(ulong data, uint kind)
    {
        var (offset, size) = Pair(104, 16);
        if (data >= (ulong)(size / 16)) throw new InvalidDataException("客户端泛型参数无效");
        return (kind == 19 ? "T" : "M") + BinaryPrimitives.ReadUInt16LittleEndian(Slice(metadata, checked(offset + (int)data * 16 + 12), 2));
    }
    private string Generic(ulong data, int depth)
    {
        var instance = Pointer(data + 8); var count = Pointer(instance); var args = Pointer(instance + 8);
        if (count > 32) throw new InvalidDataException("客户端泛型参数过多");
        return PointerName(Pointer(data), depth) + "<" + string.Join(',', Enumerable.Range(0, (int)count).Select(i => PointerName(Pointer(args + (ulong)i * 8), depth))) + ">";
    }
    private (int Start, int Count) FieldRange(int index)
    {
        var at = definitions + index * 88; var start = I32(metadata, at + 32); var count = BinaryPrimitives.ReadUInt16LittleEndian(Slice(metadata, at + 68, 2));
        if (count > 0 && (start < 0 || (long)start + count > fieldSize / 12)) throw new InvalidDataException("客户端字段范围无效");
        return (start, count);
    }
    private static IEnumerable<string> Arguments(string name)
    {
        var first = name.IndexOf('<'); if (first < 0) yield break;
        var start = first + 1; var depth = 0;
        for (var i = start; i < name.Length; i++)
        {
            if (name[i] == '<') depth++;
            if (name[i] == '>' && depth-- == 0) { yield return name[start..i]; yield break; }
            if (name[i] == ',' && depth == 0) { yield return name[start..i]; start = i + 1; }
        }
    }
    private JsonElement Schema()
    {
        var result = new Dictionary<string, object>();
        void Collect(string? name, int depth = 0)
        {
            cancellation.ThrowIfCancellationRequested();
            if(depth > 128) throw new InvalidDataException("客户端库存类型依赖过深");
            if (name is null or "object" or "System.Object" or "System.ValueType" or "System.Enum" || result.ContainsKey(name)) return;
            if (name.EndsWith("[]", StringComparison.Ordinal)) { Collect(name[..^2],depth+1); return; }
            if (name.Contains('<')) { foreach (var arg in Arguments(name)) Collect(arg,depth+1); return; }
            if (!indices.TryGetValue(name, out var index)) return;
            if (index < 0 || result.Count >= 4096) throw new InvalidDataException("客户端库存类型重复或过多");
            var parent = TypeName(I32(metadata, definitions + index * 88 + 16));
            var list = new List<object>(); var children = new List<string>(); var range = FieldRange(index);
            for (var i = 0; i < range.Count; i++)
            {
                var at = fields + (range.Start + i) * 12; var typ = I32(metadata, at + 4);
                if ((U32(assembly, Offset(TypePointer(typ), 12) + 8) & 0x10) != 0) continue;
                var fieldType = TypeName(typ)!; list.Add(new { name = Text(I32(metadata, at)), type = fieldType }); children.Add(fieldType);
            }
            result.Add(name, new { parent, fields = list }); Collect(parent,depth+1); foreach (var child in children) Collect(child,depth+1);
        }
        Collect(ClientInventoryDecoder.RootType);
        if (!result.ContainsKey(ClientInventoryDecoder.RootType)) throw new InvalidDataException("客户端缺少库存根类型");
        return JsonSerializer.SerializeToElement(new { types = result });
    }
    private uint Opcode(string field)
    {
        var matches = indices.Where(p => p.Key.EndsWith(".GCProtocol", StringComparison.Ordinal) && p.Value >= 0).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("客户端协议枚举无法唯一定位");
        var range = FieldRange(matches[0].Value); var found = -1;
        for (var i = 0; i < range.Count; i++) if (Text(I32(metadata, fields + (range.Start + i) * 12)) == field) { if (found >= 0) throw new InvalidDataException("客户端协议字段重复"); found = range.Start + i; }
        if (found < 0) throw new InvalidDataException("客户端缺少协议：" + field);
        var defaults = Pair(64, 12); var values = Pair(72); int? value = null;
        for (var at = defaults.Offset; at < defaults.Offset + defaults.Size; at += 12)
        {
            if (I32(metadata, at) != found) continue;
            var index = I32(metadata, at + 8); if (index < 0 || index >= values.Size || value.HasValue) throw new InvalidDataException("客户端协议值无效");
            var start = values.Offset + index; var b = metadata[start]; uint encoded;
            if (b < 0x80) encoded = b;
            else if (b < 0xc0) { _ = Slice(metadata, start, 2); encoded = (uint)((b & 0x3f) << 8 | metadata[start + 1]); }
            else if (b < 0xe0) { _ = Slice(metadata, start, 4); encoded = (uint)((b & 0x1f) << 24 | metadata[start + 1] << 16 | metadata[start + 2] << 8 | metadata[start + 3]); }
            else if (b == 0xf0) encoded = U32(metadata, start + 1);
            else throw new InvalidDataException("客户端协议值压缩格式不支持");
            value = (encoded & 1) != 0 ? -checked((int)(encoded >> 1)) - 1 : checked((int)(encoded >> 1));
        }
        if (value is null or <= 0) throw new InvalidDataException("客户端协议号无效");
        return (uint)value.Value;
    }
    public static ClientCaptureLayout Read(string directory, CancellationToken cancellation = default, ClientCaptureLayout? cached = null)
    {
        directory = Path.GetFullPath(directory);
        var assemblyPath = Path.Combine(directory, "GameAssembly.dll");
        var metadataFiles = Directory.EnumerateDirectories(directory, "*_Data", SearchOption.TopDirectoryOnly)
            .Select(d => Path.Combine(d, "il2cpp_data", "Metadata", "global-metadata.dat")).Where(File.Exists).Take(2).ToArray();
        if (metadataFiles.Length != 1) throw new InvalidDataException("未找到唯一的客户端元数据文件，请选择游戏程序所在目录");
        // Hold both files read-only for the entire extraction, preventing mixed-version reads during an update.
        using var assemblyLock = new FileStream(assemblyPath,FileMode.Open,FileAccess.Read,FileShare.Read);
        using var metadataLock = new FileStream(metadataFiles[0],FileMode.Open,FileAccess.Read,FileShare.Read);
        var dll = ReadFile(assemblyPath, 768 * 1024 * 1024, cancellation);
        var meta = ReadFile(metadataFiles[0], 256 * 1024 * 1024, cancellation);
        var assemblyHash = Convert.ToHexStringLower(SHA256.HashData(dll));
        var metadataHash = Convert.ToHexStringLower(SHA256.HashData(meta));
        if(cached is not null && cached.AssemblyHash == assemblyHash && cached.MetadataHash == metadataHash) return cached;
        var reader = new ClientLayoutReader(meta, dll, cancellation);
        var protocol = new CaptureProtocol(reader.Opcode("CHANNEL_MyCharacterPersonalData_NTF"), reader.Opcode("REPLICATION_GuildMembers_UpdateGuildMemberInfo_REPL"), reader.Opcode("REPLICATION_ConsumableInventory_UpdateConsumableStack_REPL"));
        if (new[] { protocol.Login, protocol.Profile, protocol.Inventory }.Distinct().Count() != 3) throw new InvalidDataException("客户端协议号冲突");
        return new(assemblyHash, metadataHash, protocol, reader.Schema());
    }
    private static byte[] ReadFile(string path, int limit, CancellationToken cancellation)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length <= 0 || input.Length > limit) throw new InvalidDataException("客户端文件大小不支持");
        var bytes = new byte[checked((int)input.Length)];
        for (var at = 0; at < bytes.Length;) { cancellation.ThrowIfCancellationRequested(); var count = input.Read(bytes, at, Math.Min(1024 * 1024, bytes.Length - at)); if (count == 0) throw new EndOfStreamException(); at += count; }
        if (input.Length != bytes.Length) throw new InvalidDataException("解析期间客户端发生更新，请等待更新结束");
        return bytes;
    }
}
