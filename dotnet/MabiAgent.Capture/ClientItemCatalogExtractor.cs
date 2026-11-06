using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MabiAgent.Capture;

/// <summary>Reads Consumable display names directly from the installed Starter resource package.</summary>
public static partial class ClientItemCatalogExtractor
{
    private const string MetaPath = "Sheetdata_Client/client.sheetmeta.bin";
    private const string TextPath = "Sheetdata_Client/client.sheettext.bin";
    private const string StringsPath = "StringTables/compiled.strings.bin";

    private static readonly IReadOnlyDictionary<string, ResourceKind> WantedResources =
        new Dictionary<string, ResourceKind>(StringComparer.Ordinal)
        {
            [MetaPath] = ResourceKind.Meta,
            [TextPath] = ResourceKind.Text,
            [StringsPath] = ResourceKind.Strings,
        };

    public static JsonElement Extract(string appdataDirectory) => Extract(appdataDirectory, CancellationToken.None);

    public static JsonElement Extract(string appdataDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(appdataDirectory);
        var packageDirectory = Path.Combine(Path.GetFullPath(appdataDirectory), "ResourcesPackage", "Starter");
        var package = ClientResourcePackage.Open(packageDirectory, cancellationToken);
        var resourceEntries = package.Entries
            .Where(entry => WantedResources.ContainsKey(entry.Path))
            .ToArray();
        if (resourceEntries.Length != WantedResources.Count ||
            resourceEntries.Select(entry => entry.Path).Distinct(StringComparer.Ordinal).Count() != WantedResources.Count)
            throw new InvalidDataException("客户端缺少所需名称表");

        var initialFiles = resourceEntries
            .Select(entry => entry.BlobPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, StableFileStamp.Capture, StringComparer.OrdinalIgnoreCase);

        var resources = new Dictionary<ResourceKind, byte[]>();
        var sources = new List<SourceOutput>(resourceEntries.Length);
        foreach (var entry in resourceEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = package.Read(entry, initialFiles[entry.BlobPath], cancellationToken);
            resources.Add(WantedResources[entry.Path], resource.Decoded);
            sources.Add(new SourceOutput(entry.Path, entry.PackageKey, resource.Offset, resource.PackedBytes,
                Convert.ToHexStringLower(SHA256.HashData(resource.Packed)), Convert.ToHexStringLower(SHA256.HashData(resource.Decoded))));
        }

        if (resources.Count != WantedResources.Count)
            throw new InvalidDataException("客户端缺少所需名称表");

        cancellationToken.ThrowIfCancellationRequested();
        if (!ClientNameTable.ReadTables(resources[ResourceKind.Meta], resources[ResourceKind.Text], cancellationToken)
            .TryGetValue("Consumable", out var table))
            throw new InvalidDataException("客户端缺少 Consumable 表");
        var localized = ClientNameTable.ReadStringTables(resources[ResourceKind.Strings], cancellationToken);
        var items = new Dictionary<string, ItemOutput>(StringComparer.Ordinal);
        var excluded = new List<ExcludedOutput>();
        var seenIds = new HashSet<uint>();
        var reader = new ClientDataReader(table.Block);
        for (uint row = 0; row < table.Rows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metaOffset = checked(table.MetaOffset + (ulong)reader.Offset);
            var itemId = reader.UInt32();
            var nameOffset = reader.Var();
            _ = reader.Var();
            if (itemId == 0 || !seenIds.Add(itemId))
                throw new InvalidDataException("物品类型 ID 重复或为零");
            var rawName = ClientNameTable.ReadString(resources[ResourceKind.Text], checked((int)nameOffset));
            try
            {
                var name = DisplayName(rawName, localized);
                items.Add(itemId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new ItemOutput(name, rawName, row, metaOffset, nameOffset, table.DataOffsets[checked((int)row)]));
            }
            catch (DisplayNameException error)
            {
                excluded.Add(new ExcludedOutput(itemId.ToString(System.Globalization.CultureInfo.InvariantCulture), rawName, error.Message));
            }
        }
        reader.EnsureEnd();

        package.EnsureUnchanged();
        foreach (var file in initialFiles)
            file.Value.EnsureUnchanged(file.Key);
        cancellationToken.ThrowIfCancellationRequested();

        return JsonSerializer.SerializeToElement(new CatalogOutput(
            1,
            "Consumable",
            table.Rows,
            Convert.ToHexStringLower(SHA256.HashData(package.IndexBytes)),
            sources,
            items,
            excluded));
    }

    private static string DisplayName(string rawName, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> localized)
    {
        var name = RuneTierPattern().Replace(rawName, match =>
        {
            var value = System.Numerics.BigInteger.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (value <= System.Numerics.BigInteger.Zero) return string.Empty;
            if (!localized.TryGetValue("Common", out var common) ||
                !common.TryGetValue("NoRichText_RuneTierFormat", out var template))
                throw new InvalidDataException("客户端缺少符文层级名称格式");
            return template.Replace("{0}", value > System.Numerics.BigInteger.One ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
                StringComparison.Ordinal);
        });
        name = ColorTagPattern().Replace(name, string.Empty).Trim();
        if (UnresolvedPlaceholderPattern().IsMatch(name) || name.Contains('<') ||
            name.Length == 0 || name.EnumerateRunes().Count() > 80)
            throw new DisplayNameException("尚未解析的物品显示名：" + rawName);
        return name;
    }

    [GeneratedRegex(@"%RuneTier,\s*(Season\d+),\s*(\d+)%", RegexOptions.CultureInvariant)]
    private static partial Regex RuneTierPattern();

    [GeneratedRegex(@"</?color(?:=#[0-9a-fA-F]{6,8})?>", RegexOptions.CultureInvariant)]
    private static partial Regex ColorTagPattern();

    [GeneratedRegex(@"%[A-Za-z]+[,\s]", RegexOptions.CultureInvariant)]
    private static partial Regex UnresolvedPlaceholderPattern();

    private sealed class DisplayNameException(string message) : Exception(message);

    private enum ResourceKind { Meta, Text, Strings }

    private sealed record CatalogOutput(
        [property: JsonPropertyName("schema_version")] int SchemaVersion,
        [property: JsonPropertyName("table")] string Table,
        [property: JsonPropertyName("rows")] uint Rows,
        [property: JsonPropertyName("index_sha256")] string IndexSha256,
        [property: JsonPropertyName("sources")] IReadOnlyList<SourceOutput> Sources,
        [property: JsonPropertyName("items")] IReadOnlyDictionary<string, ItemOutput> Items,
        [property: JsonPropertyName("excluded")] IReadOnlyList<ExcludedOutput> Excluded);

    private sealed record SourceOutput(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("package_key")] uint PackageKey,
        [property: JsonPropertyName("offset")] uint Offset,
        [property: JsonPropertyName("packed_bytes")] int PackedBytes,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("decoded_sha256")] string DecodedSha256);

    private sealed record ItemOutput(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("raw_name")] string RawName,
        [property: JsonPropertyName("row")] uint Row,
        [property: JsonPropertyName("meta_offset")] ulong MetaOffset,
        [property: JsonPropertyName("name_offset")] uint NameOffset,
        [property: JsonPropertyName("data_offset")] ulong DataOffset);

    private sealed record ExcludedOutput(
        [property: JsonPropertyName("wire_id")] string WireId,
        [property: JsonPropertyName("raw_name")] string RawName,
        [property: JsonPropertyName("reason")] string Reason);
}

