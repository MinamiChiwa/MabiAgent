using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MabiAgent.Capture;
using Xunit;

namespace MabiAgent.Capture.Tests;

public sealed class ClientItemCatalogExtractorTests : IDisposable
{
    private readonly List<string> temporaryDirectories = [];

    [Fact]
    public void DynamicRuneTierAndExcludedRowsMatchClientDisplayRules()
    {
        var root = SyntheticPackage.Create(NewDirectory(),
        [
            (101u, "%RuneTier, Season02, 10% 超월의 정수"),
            (102u, "룬새김(%RuneTier, Season02, 1%)"),
            (103u, "<color=#AABBCC>白色</color>"),
            (104u, "%Unknown, 12"),
            (105u, "%RuneTier, Season00, 0% Zero"),
        ]);
        var output = ClientItemCatalogExtractor.Extract(root);
        Assert.Equal(1, output.GetProperty("schema_version").GetInt32());
        Assert.Equal("Consumable", output.GetProperty("table").GetString());
        Assert.Equal(5u, output.GetProperty("rows").GetUInt32());
        Assert.Equal(3, output.GetProperty("sources").GetArrayLength());
        Assert.Equal(4, output.GetProperty("items").EnumerateObject().Count());
        Assert.Equal("★10 超월의 정수", output.GetProperty("items").GetProperty("101").GetProperty("name").GetString());
        Assert.Equal("룬새김(★)", output.GetProperty("items").GetProperty("102").GetProperty("name").GetString());
        Assert.Equal("白色", output.GetProperty("items").GetProperty("103").GetProperty("name").GetString());
        Assert.Equal("Zero", output.GetProperty("items").GetProperty("105").GetProperty("name").GetString());
        var excluded = Assert.Single(output.GetProperty("excluded").EnumerateArray());
        Assert.Equal("104", excluded.GetProperty("wire_id").GetString());
        Assert.Equal("%Unknown, 12", excluded.GetProperty("raw_name").GetString());
        Assert.Contains("尚未解析", excluded.GetProperty("reason").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "ResourcesPackage", "Starter", "index")))),
            output.GetProperty("index_sha256").GetString());
    }

    [Fact]
    public void TruncatedResourceIsRejected()
    {
        var root = SyntheticPackage.Create(NewDirectory(), [(201u, "Name")]);
        var blob = Path.Combine(root, "ResourcesPackage", "Starter", "0000.blob");
        using (var stream = new FileStream(blob, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(stream.Length - 1);
        Assert.Throws<InvalidDataException>(() => ClientItemCatalogExtractor.Extract(root));
    }

    [Fact]
    public void TruncatedIndexIsRejected()
    {
        var root = SyntheticPackage.Create(NewDirectory(), [(301u, "Name")]);
        var index = Path.Combine(root, "ResourcesPackage", "Starter", "index");
        File.WriteAllBytes(index, File.ReadAllBytes(index)[..10]);
        Assert.Throws<InvalidDataException>(() => ClientItemCatalogExtractor.Extract(root));
    }

    [Fact]
    public void DuplicateWireIdIsRejectedBeforeRendering()
    {
        var root = SyntheticPackage.Create(NewDirectory(), [(401u, "First"), (401u, "Second")]);
        var error = Assert.Throws<InvalidDataException>(() => ClientItemCatalogExtractor.Extract(root));
        Assert.Contains("重复", error.Message);
    }

    [Fact]
    public void PreCanceledTokenStopsBeforeReadingClientFiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ClientItemCatalogExtractor.Extract(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N")), cancellation.Token));
    }

    [LocalClientFact]
    public void LocalStarterCatalogMatchesCommittedCatalogEveryField()
    {
        var client = Environment.GetEnvironmentVariable("MABI_CLIENT_APPDATA")!;
        Assert.True(Directory.Exists(Path.Combine(client, "ResourcesPackage", "Starter")), "MABI_CLIENT_APPDATA 缺少 Starter 资源");
        var output = ClientItemCatalogExtractor.Extract(client);
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "config/client-item-names.tw.json")));
        var expectedRoot = expected.RootElement;
        Assert.Equal(expectedRoot.EnumerateObject().Select(property => property.Name),
            output.EnumerateObject().Select(property => property.Name));
        Assert.Equal(expectedRoot.GetProperty("schema_version").GetInt32(), output.GetProperty("schema_version").GetInt32());
        Assert.Equal(expectedRoot.GetProperty("table").GetString(), output.GetProperty("table").GetString());
        Assert.Equal(expectedRoot.GetProperty("rows").GetUInt32(), output.GetProperty("rows").GetUInt32());
        Assert.Equal(expectedRoot.GetProperty("index_sha256").GetString(), output.GetProperty("index_sha256").GetString());

        var expectedSources = expectedRoot.GetProperty("sources").EnumerateArray().ToArray();
        var actualSources = output.GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(expectedSources.Length, actualSources.Length);
        for (var index = 0; index < expectedSources.Length; index++)
        {
            var expectedSource = expectedSources[index];
            var actualSource = actualSources[index];
            Assert.Equal(expectedSource.EnumerateObject().Select(property => property.Name),
                actualSource.EnumerateObject().Select(property => property.Name));
            Assert.Equal(expectedSource.GetProperty("path").GetString(), actualSource.GetProperty("path").GetString());
            Assert.Equal(expectedSource.GetProperty("package_key").GetUInt32(), actualSource.GetProperty("package_key").GetUInt32());
            Assert.Equal(expectedSource.GetProperty("offset").GetUInt32(), actualSource.GetProperty("offset").GetUInt32());
            Assert.Equal(expectedSource.GetProperty("packed_bytes").GetInt32(), actualSource.GetProperty("packed_bytes").GetInt32());
            Assert.Equal(expectedSource.GetProperty("sha256").GetString(), actualSource.GetProperty("sha256").GetString());
            Assert.Equal(expectedSource.GetProperty("decoded_sha256").GetString(), actualSource.GetProperty("decoded_sha256").GetString());
        }

        var expectedItems = expectedRoot.GetProperty("items").EnumerateObject().ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        var actualItems = output.GetProperty("items").EnumerateObject().ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        Assert.Equal(expectedItems.Keys.Order(StringComparer.Ordinal), actualItems.Keys.Order(StringComparer.Ordinal));
        foreach (var expectedItem in expectedItems)
        {
            Assert.True(actualItems.TryGetValue(expectedItem.Key, out var actualItem), "缺少客户端类型 ID " + expectedItem.Key);
            Assert.Equal(expectedItem.Value.EnumerateObject().Select(property => property.Name),
                actualItem.EnumerateObject().Select(property => property.Name));
            Assert.Equal(expectedItem.Value.GetProperty("name").GetString(), actualItem.GetProperty("name").GetString());
            Assert.Equal(expectedItem.Value.GetProperty("raw_name").GetString(), actualItem.GetProperty("raw_name").GetString());
            Assert.Equal(expectedItem.Value.GetProperty("row").GetUInt32(), actualItem.GetProperty("row").GetUInt32());
            Assert.Equal(expectedItem.Value.GetProperty("meta_offset").GetUInt64(), actualItem.GetProperty("meta_offset").GetUInt64());
            Assert.Equal(expectedItem.Value.GetProperty("name_offset").GetUInt32(), actualItem.GetProperty("name_offset").GetUInt32());
            Assert.Equal(expectedItem.Value.GetProperty("data_offset").GetUInt64(), actualItem.GetProperty("data_offset").GetUInt64());
        }

        var expectedExcluded = expectedRoot.GetProperty("excluded").EnumerateArray().ToArray();
        var actualExcluded = output.GetProperty("excluded").EnumerateArray().ToArray();
        Assert.Equal(expectedExcluded.Length, actualExcluded.Length);
        for (var index = 0; index < expectedExcluded.Length; index++)
        {
            Assert.Equal(expectedExcluded[index].EnumerateObject().Select(property => property.Name),
                actualExcluded[index].EnumerateObject().Select(property => property.Name));
            Assert.Equal(expectedExcluded[index].GetProperty("wire_id").GetString(), actualExcluded[index].GetProperty("wire_id").GetString());
            Assert.Equal(expectedExcluded[index].GetProperty("raw_name").GetString(), actualExcluded[index].GetProperty("raw_name").GetString());
            Assert.Equal(expectedExcluded[index].GetProperty("reason").GetString(), actualExcluded[index].GetProperty("reason").GetString());
        }
        Assert.Equal(5913, actualItems.Count);
    }

    public void Dispose()
    {
        foreach (var directory in temporaryDirectories)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mabi-client-item-catalog-" + Guid.NewGuid().ToString("N"));
        temporaryDirectories.Add(directory);
        return directory;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "config/client-item-names.tw.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("缺少客户端名称配置");
    }

    private static class SyntheticPackage
    {
        private const string MetaPath = "Sheetdata_Client/client.sheetmeta.bin";
        private const string TextPath = "Sheetdata_Client/client.sheettext.bin";
        private const string StringsPath = "StringTables/compiled.strings.bin";

        public static string Create(string root, IReadOnlyList<(uint Id, string Name)> rows)
        {
            var starter = Path.Combine(root, "ResourcesPackage", "Starter");
            Directory.CreateDirectory(starter);
            var text = new MemoryStream();
            var consumableOffset = WriteClientString(text, "Consumable");
            var nameOffsets = rows.Select(row => WriteClientString(text, row.Name)).ToArray();
            var textBytes = text.ToArray();

            var block = new MemoryStream();
            var rowLengths = new uint[rows.Count];
            for (var index = 0; index < rows.Count; index++)
            {
                var rowStart = block.Position;
                WriteUInt32(block, rows[index].Id);
                WriteVar(block, nameOffsets[index]);
                WriteVar(block, 0);
                rowLengths[index] = checked((uint)(block.Position - rowStart));
            }
            var blockBytes = block.ToArray();
            var metaBytes = BuildMeta(consumableOffset, blockBytes, rowLengths);
            var stringsBytes = BuildStrings();
            var resources = new[] { Compress(textBytes), Compress(stringsBytes), Compress(metaBytes) };
            File.WriteAllBytes(Path.Combine(starter, "0000.blob"), BuildBlob(resources));
            File.WriteAllBytes(Path.Combine(starter, "index"),
                BuildIndex(new[] { TextPath, StringsPath, MetaPath }, new uint[] { 0, 65536, 131072 }));
            return root;
        }

        private static byte[] BuildMeta(uint consumableOffset, byte[] block, IReadOnlyList<uint> rowLengths)
        {
            var meta = new MemoryStream();
            WriteVar(meta, 1);
            WriteVar(meta, consumableOffset);
            WriteVar(meta, 0);
            WriteVar(meta, checked((uint)rowLengths.Count));
            WriteVar(meta, checked((uint)block.Length));
            WriteVar(meta, checked((uint)block.Length));
            foreach (var rowLength in rowLengths) WriteVar(meta, rowLength);
            meta.Write(block);
            return meta.ToArray();
        }

        private static byte[] BuildStrings()
        {
            var strings = new MemoryStream();
            WriteUInt32(strings, 1);
            WriteText(strings, "Common");
            strings.WriteByte(1);
            WriteUInt32(strings, 1);
            WriteText(strings, "NoRichText_RuneTierFormat");
            WriteText(strings, "★{0}");
            WriteUInt32(strings, 1);
            WriteText(strings, "UI");
            WriteUInt32(strings, 1);
            WriteUInt32(strings, 1);
            WriteText(strings, "SubtableValue");
            return strings.ToArray();
        }

        private static byte[] Compress(byte[] decoded)
        {
            using var packed = new MemoryStream();
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)decoded.Length));
            packed.Write(length);
            using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(decoded);
            return packed.ToArray();
        }

        private static byte[] BuildBlob(IReadOnlyList<byte[]> resources)
        {
            var positions = new uint[resources.Count + 1];
            var position = checked((uint)(8 + resources.Count * 4));
            for (var index = 0; index < resources.Count; index++)
            {
                positions[index] = position;
                position = checked(position + (uint)resources[index].Length);
            }
            positions[^1] = position;
            var blob = new MemoryStream();
            WriteUInt32(blob, 0xe5a9c3ba);
            foreach (var edge in positions) WriteUInt32(blob, edge);
            for (var index = 0; index < resources.Count; index++)
            {
                var stored = (byte[])resources[index].Clone();
                Obfuscate(positions[index], stored);
                blob.Write(stored);
            }
            return blob.ToArray();
        }

        private static void Obfuscate(uint offset, byte[] data)
        {
            const ulong multiplier = 0xd3833e804f4c574b;
            var size = (ulong)data.Length;
            var v = unchecked((offset ^ (~size & ulong.MaxValue)) + 0x171974e5e5bdda11);
            var x = unchecked(v * multiplier);
            var y = RotateLeft(unchecked(size + 0x849ee64c9720bb37 - offset - 0x708b6ba5fb11f033), 12);
            var z = RotateLeft(unchecked(v - size - 0x849ee64c9720bb37), 44);
            Span<byte> key = stackalloc byte[8];
            for (var at = 0; at < data.Length; at += 8)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(key, x);
                var oldX = x;
                var oldY = y;
                var oldZ = z;
                x = unchecked(oldZ * multiplier);
                y = RotateLeft(unchecked(oldY - oldX), 12);
                z = RotateLeft(unchecked(oldZ - oldY), 44);
                var count = Math.Min(8, data.Length - at);
                for (var index = 0; index < count; index++) data[at + index] ^= key[index];
            }
        }

        private static ulong RotateLeft(ulong value, int count) => (value << count) | (value >> (64 - count));

        private static byte[] BuildIndex(IReadOnlyList<string> paths, IReadOnlyList<uint> keys)
        {
            var names = new MemoryStream();
            var pathValues = new ulong[paths.Count];
            for (var index = 0; index < paths.Count; index++)
                pathValues[index] = (ulong)WriteClientString(names, paths[index]) << 24;
            var indexStream = new MemoryStream();
            WriteUInt32(indexStream, 0xe6a9c3ba);
            WriteUInt32(indexStream, checked((uint)keys.Count));
            foreach (var key in keys) WriteUInt32(indexStream, key);
            WriteUInt32(indexStream, 0);
            WriteUInt32(indexStream, 0);
            WriteUInt32(indexStream, checked((uint)pathValues.Length));
            foreach (var path in pathValues) WriteUInt64(indexStream, path);
            WriteUInt32(indexStream, 0);
            WriteBytes(indexStream, []);
            WriteBytes(indexStream, names.ToArray());
            WriteBytes(indexStream, [0]);
            return indexStream.ToArray();
        }

        private static uint WriteClientString(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length >= 128) throw new InvalidOperationException("测试字符串必须使用字面量编码");
            var offset = checked((uint)stream.Position);
            stream.WriteByte(checked((byte)bytes.Length));
            stream.Write(bytes);
            stream.WriteByte(0);
            return offset;
        }

        private static void WriteText(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteUInt32(stream, checked((uint)bytes.Length));
            stream.Write(bytes);
        }

        private static void WriteBytes(Stream stream, byte[] value)
        {
            WriteUInt32(stream, checked((uint)value.Length));
            stream.Write(value);
        }

        private static void WriteVar(Stream stream, uint value)
        {
            while (value >= 128)
            {
                stream.WriteByte(checked((byte)((value & 127) | 128)));
                value >>= 7;
            }
            stream.WriteByte(checked((byte)value));
        }

        private static int VarLength(uint value)
        {
            var length = 1;
            while (value >= 128)
            {
                value >>= 7;
                length++;
            }
            return length;
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            stream.Write(buffer);
        }

        private static void WriteUInt64(Stream stream, ulong value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            stream.Write(buffer);
        }
    }
}

internal sealed class LocalClientFactAttribute : FactAttribute
{
    public LocalClientFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MABI_CLIENT_APPDATA")))
            Skip = "未设置 MABI_CLIENT_APPDATA";
    }
}
