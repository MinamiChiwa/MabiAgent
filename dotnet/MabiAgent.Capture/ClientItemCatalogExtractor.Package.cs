using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace MabiAgent.Capture;

internal sealed class ClientResourcePackage
{
    private const uint IndexMagic = 0xe6a9c3ba;
    private const uint BlobMagic = 0xe5a9c3ba;
    private const int MaxIndexBytes = 64 * 1024 * 1024;
    private const long MaxBlobBytes = 1024L * 1024 * 1024;
    private const int MaxPackedResourceBytes = 64 * 1024 * 1024;
    private const uint MaxDecodedResourceBytes = 100_000_000;
    private const uint MaxIndexEntries = 2_000_000;
    private const uint MaxDependencyGroups = 100_000;
    private const uint MaxHashEntries = 2_000_000;
    private const uint MaxBuckets = 2_000_000;
    private const int MaxIndexTableBytes = 16 * 1024 * 1024;

    private readonly string directory;
    private readonly StableFileStamp indexStamp;

    private ClientResourcePackage(string directory, StableFileStamp indexStamp, byte[] indexBytes, PackageEntry[] entries)
    {
        this.directory = directory;
        this.indexStamp = indexStamp;
        IndexBytes = indexBytes;
        Entries = entries;
    }

    public byte[] IndexBytes { get; }
    public IReadOnlyList<PackageEntry> Entries { get; }

    public static ClientResourcePackage Open(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("客户端 Starter 资源目录不存在");
        var indexPath = Path.Combine(directory, "index");
        var indexStamp = StableFileStamp.Capture(indexPath);
        if (indexStamp.Length > MaxIndexBytes) throw new InvalidDataException("客户端资源索引超过大小预算");
        var indexBytes = ReadStableFile(indexPath, indexStamp, cancellationToken);
        var entries = ParseIndex(directory, indexBytes, cancellationToken);
        return new ClientResourcePackage(directory, indexStamp, indexBytes, entries);
    }

    public DecodedResource Read(PackageEntry entry, StableFileStamp expectedBlobStamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!expectedBlobStamp.Equals(StableFileStamp.Capture(entry.BlobPath)))
            throw Changed();
        if (expectedBlobStamp.Length > MaxBlobBytes) throw new InvalidDataException("客户端资源文件超过大小预算");

        uint offset;
        uint end;
        byte[] packed;
        using (var stream = new FileStream(entry.BlobPath, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess | FileOptions.SequentialScan))
        {
            if (stream.Length != expectedBlobStamp.Length) throw Changed();
            Span<byte> header = stackalloc byte[8];
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != BlobMagic)
                throw new InvalidDataException("客户端资源文件头无效");

            var start = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var itemIndex = checked(entry.PackageKey >> 16);
            var offsetPosition = CheckedPosition(4, itemIndex);
            var endPosition = CheckedPosition(8, itemIndex);
            offset = itemIndex == 0 ? start : ReadUInt32At(stream, offsetPosition);
            end = ReadUInt32At(stream, endPosition);
            if (offset < 8 || end < offset || end > stream.Length) throw new InvalidDataException("客户端资源块截断或越界");
            var packedLength = checked((long)end - offset);
            if (packedLength > MaxPackedResourceBytes) throw new InvalidDataException("客户端压缩资源超过大小预算");
            packed = new byte[checked((int)packedLength)];
            stream.Position = offset;
            stream.ReadExactly(packed);
        }
        expectedBlobStamp.EnsureUnchanged(entry.BlobPath);
        cancellationToken.ThrowIfCancellationRequested();
        return new DecodedResource(offset, packed.Length, packed, Decode(offset, packed));
    }

    public void EnsureUnchanged() => indexStamp.EnsureUnchanged(Path.Combine(directory, "index"));

    private static PackageEntry[] ParseIndex(string directory, byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length < 8) throw new InvalidDataException("客户端资源索引截断");
        var reader = new IndexReader(bytes);
        if (reader.UInt32() != IndexMagic) throw new InvalidDataException("客户端资源索引头无效");
        var contentsCount = ReadCount(reader, MaxIndexEntries, "资源数量");
        var keys = new uint[contentsCount];
        for (var index = 0; index < keys.Length; index++) keys[index] = reader.UInt32();

        var dependencyGroups = ReadCount(reader, MaxDependencyGroups, "依赖组数量");
        for (var index = 0; index < dependencyGroups; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = reader.UInt32();
            var dependencyCount = reader.UInt32();
            reader.Skip(CheckedBytes(dependencyCount, 4, "依赖数量"));
        }
        var hashCount = ReadCount(reader, MaxHashEntries, "哈希数量");
        reader.Skip(CheckedBytes(hashCount, 4, "哈希表"));
        var pathCount = ReadCount(reader, MaxIndexEntries, "路径数量");
        if (pathCount > keys.Length) throw new InvalidDataException("客户端资源路径缺少对应内容索引");
        var paths = new ulong[pathCount];
        for (var index = 0; index < paths.Length; index++) paths[index] = reader.UInt64();
        var bucketCount = ReadCount(reader, MaxBuckets, "目录桶数量");
        reader.Skip(CheckedBytes(bucketCount, 4, "目录桶"));
        var directories = reader.Bytes(MaxIndexTableBytes, "目录表");
        var names = reader.Bytes(MaxIndexTableBytes, "名称表");
        var postfix = reader.Bytes(MaxIndexTableBytes, "扩展名表");
        reader.EnsureEnd();

        var entries = new PackageEntry[paths.Length];
        for (var index = 0; index < paths.Length; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = paths[index];
            var folder = ReadDirectory(directories, checked((int)(value & 0x00ff_ffff)), 0);
            var name = ClientNameTable.ReadString(names, checked((int)((value >> 24) & 0x0fff_ffff)));
            var postfixValue = ClientNameTable.ReadString(postfix, checked((int)(value >> 52)));
            var path = folder.Length == 0 ? name + postfixValue : folder + "/" + name + postfixValue;
            var blobNumber = keys[index] & 0x7fff;
            var blobPath = Path.Combine(directory, blobNumber.ToString("D4", CultureInfo.InvariantCulture) + ".blob");
            entries[index] = new PackageEntry(path, keys[index], blobPath);
        }
        return entries;
    }

    private static string ReadDirectory(byte[] directories, int offset, int depth)
    {
        if (offset == 0) return string.Empty;
        if (depth > 1024) throw new InvalidDataException("客户端目录层级超过预算");
        if (offset < 0 || offset >= directories.Length) throw new InvalidDataException("客户端目录引用越界");
        var end = offset + 1;
        while (true)
        {
            if (end >= directories.Length) throw new InvalidDataException("客户端目录表截断");
            if (directories[end] <= 4) break;
            end++;
        }
        var parentBytes = directories[end];
        if (parentBytes > 4 || end + 1 + parentBytes > directories.Length)
            throw new InvalidDataException("客户端目录父引用截断");
        var parent = 0;
        if (parentBytes != 0)
        {
            uint parentDistance = 0;
            for (var index = 0; index < parentBytes; index++)
                parentDistance |= (uint)directories[end + 1 + index] << (index * 8);
            if (parentDistance == 0 || parentDistance > (uint)offset)
                throw new InvalidDataException("客户端目录父引用无效");
            parent = offset - checked((int)parentDistance);
        }
        var segment = DecodeUtf8(directories.AsSpan(offset, end - offset));
        var prefix = ReadDirectory(directories, parent, depth + 1);
        return prefix.Length == 0 ? segment : prefix + "/" + segment;
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return ClientNameTable.StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("客户端目录文本不是有效 UTF-8", error);
        }
    }

    private static byte[] ReadStableFile(string path, StableFileStamp expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.SequentialScan);
        if (stream.Length != expected.Length) throw Changed();
        var bytes = new byte[checked((int)expected.Length)];
        stream.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        expected.EnsureUnchanged(path);
        return bytes;
    }

    private static byte[] Decode(uint offset, byte[] data)
    {
        if (data.Length < 4) throw new InvalidDataException("客户端资源解压数据截断");
        var working = (byte[])data.Clone();
        const ulong multiplier = 0xd3833e804f4c574b;
        var size = (ulong)working.Length;
        var v = unchecked((offset ^ (~size & ulong.MaxValue)) + 0x171974e5e5bdda11);
        var x = unchecked(v * multiplier);
        var y = RotateLeft(unchecked(size + 0x849ee64c9720bb37 - offset - 0x708b6ba5fb11f033), 12);
        var z = RotateLeft(unchecked(v - size - 0x849ee64c9720bb37), 44);
        Span<byte> key = stackalloc byte[8];
        for (var at = 0; at < working.Length; at += 8)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(key, x);
            var oldX = x;
            var oldY = y;
            var oldZ = z;
            x = unchecked(oldZ * multiplier);
            y = RotateLeft(unchecked(oldY - oldX), 12);
            z = RotateLeft(unchecked(oldZ - oldY), 44);
            var count = Math.Min(8, working.Length - at);
            for (var index = 0; index < count; index++) working[at + index] ^= key[index];
        }

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(working);
        if (expected >= MaxDecodedResourceBytes) throw new InvalidDataException("客户端解压资源超过大小预算");
        try
        {
            using var compressed = new MemoryStream(working, 4, working.Length - 4, writable: false);
            using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
            using var output = new MemoryStream(checked((int)expected));
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = inflater.Read(buffer);
                if (read == 0) break;
                if ((ulong)output.Length + (uint)read > expected)
                    throw new InvalidDataException("客户端解压资源超过声明长度");
                output.Write(buffer, 0, read);
            }
            if (output.Length != expected) throw new InvalidDataException("客户端解压资源长度不符");
            return output.ToArray();
        }
        catch (InvalidDataException error) when (error.Message is not "客户端解压资源长度不符" and
                                                 not "客户端解压资源超过声明长度")
        {
            throw new InvalidDataException("客户端资源解压失败", error);
        }
    }

    private static ulong RotateLeft(ulong value, int count) => (value << count) | (value >> (64 - count));

    private static ulong CheckedPosition(uint initial, ulong index)
    {
        try { return checked(initial + index * 4); }
        catch (OverflowException error) { throw new InvalidDataException("客户端资源块索引越界", error); }
    }

    private static uint ReadUInt32At(Stream stream, ulong position)
    {
        if (position > (ulong)stream.Length - 4) throw new InvalidDataException("客户端资源块索引截断");
        stream.Position = checked((long)position);
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static int ReadCount(IndexReader reader, uint limit, string label)
    {
        var count = reader.UInt32();
        if (count > limit) throw new InvalidDataException($"客户端{label}超过大小预算");
        return checked((int)count);
    }

    private static int CheckedBytes(int count, int width, string label) =>
        CheckedBytes(checked((ulong)count), width, label);

    private static int CheckedBytes(ulong count, int width, string label)
    {
        try { return checked((int)checked((long)count * width)); }
        catch (OverflowException error) { throw new InvalidDataException($"客户端{label}越界", error); }
    }

    private static InvalidDataException Changed() => new("客户端资源文件在提取期间发生变化");

    internal sealed record PackageEntry(string Path, uint PackageKey, string BlobPath);
    internal sealed record DecodedResource(uint Offset, int PackedBytes, byte[] Packed, byte[] Decoded);
}

internal readonly record struct StableFileStamp(long Length, long LastWriteUtcTicks)
{
    public static StableFileStamp Capture(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists) throw new InvalidDataException($"客户端资源文件不存在：{info.Name}");
        return new StableFileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    public void EnsureUnchanged(string path)
    {
        if (!Equals(Capture(path))) throw new InvalidDataException("客户端资源文件在提取期间发生变化");
    }
}

internal sealed class IndexReader
{
    private readonly byte[] data;

    public IndexReader(byte[] data) => this.data = data;

    public int Offset { get; private set; }

    public uint UInt32()
    {
        if (Offset > data.Length - 4) throw new InvalidDataException("客户端资源索引截断");
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Offset, 4));
        Offset += 4;
        return value;
    }

    public ulong UInt64()
    {
        if (Offset > data.Length - 8) throw new InvalidDataException("客户端资源索引截断");
        var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(Offset, 8));
        Offset += 8;
        return value;
    }

    public void Skip(int count)
    {
        if (count < 0 || Offset > data.Length - count) throw new InvalidDataException("客户端资源索引截断");
        Offset += count;
    }

    public byte[] Bytes(int limit, string label)
    {
        var length = UInt32();
        if (length > limit) throw new InvalidDataException($"客户端{label}超过大小预算");
        var size = checked((int)length);
        if (Offset > data.Length - size) throw new InvalidDataException($"客户端{label}截断");
        var result = data.AsSpan(Offset, size).ToArray();
        Offset += size;
        return result;
    }

    public void EnsureEnd()
    {
        if (Offset != data.Length) throw new InvalidDataException("客户端资源索引存在未解析字节");
    }
}

