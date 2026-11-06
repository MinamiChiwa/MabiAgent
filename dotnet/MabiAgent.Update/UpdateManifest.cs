using System.Text.Json;
using System.Text.Json.Nodes;

namespace MabiAgent.Update;

public sealed record ManifestFile(string Path, long Size, string Sha256, string Blob)
{
    public string BlobSha256 => Sha256;
}

/// <summary>
/// Per-file release manifest. Every path is relative to the install root and every
/// entry carries its own size and SHA-256, so a client can fetch only what changed
/// and still verify each byte it writes.
/// </summary>
public sealed record UpdateManifest(string AppId, string Version, string Channel, IReadOnlyList<ManifestFile> Files)
{
    public const string FileName = "update-manifest.json";
    public const string ExpectedAppId = "MabiAgent";
    public const int Schema = 1;

    public static UpdateManifest Parse(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("更新清单不是 JSON 对象");
        if (node["schema"]?.GetValue<int>() != Schema) throw new InvalidDataException("更新清单版本不受支持");
        var app = node["app_id"]?.GetValue<string>() ?? "";
        if (!string.Equals(app, ExpectedAppId, StringComparison.Ordinal)) throw new InvalidDataException("更新清单属于其他应用：" + app);
        var version = node["version"]?.GetValue<string>() ?? throw new InvalidDataException("更新清单缺少版本号");
        if (!UpdateVersion.TryParse(version, out _)) throw new InvalidDataException("更新清单版本号格式无效：" + version);
        var channel = node["channel"]?.GetValue<string>() ?? "stable";
        var files = new List<ManifestFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (node["files"] is not JsonArray array || array.Count == 0) throw new InvalidDataException("更新清单没有文件条目");
        foreach (var entry in array)
        {
            if (entry is not JsonObject file) throw new InvalidDataException("更新清单文件条目无效");
            var path = file["path"]?.GetValue<string>() ?? throw new InvalidDataException("更新清单文件缺少路径");
            if (!InstallPaths.IsSafeRelative(path, out var reason)) throw new InvalidDataException("更新清单路径被拒绝：" + path + "（" + reason + "）");
            if (!seen.Add(path)) throw new InvalidDataException("更新清单包含重复路径：" + path);
            var size = file["size"]?.GetValue<long>() ?? -1;
            if (size < 0) throw new InvalidDataException("更新清单文件大小无效：" + path);
            var sha = (file["sha256"]?.GetValue<string>() ?? "").ToLowerInvariant();
            if (!IsSha256(sha)) throw new InvalidDataException("更新清单文件哈希无效：" + path);
            var blob = file["blob"]?.GetValue<string>() ?? "blob-" + sha + ".bin";
            if (blob.Contains('/') || blob.Contains('\\') || blob.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException("更新清单资产名无效：" + blob);
            files.Add(new ManifestFile(path.Replace('\\', '/'), size, sha, blob));
        }
        return new UpdateManifest(app, UpdateVersion.Normalize(version), channel, files);
    }
    public static bool IsSha256(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public string ToJson() => new JsonObject
    {
        ["app_id"] = AppId, ["schema"] = Schema, ["version"] = Version, ["channel"] = Channel,
        ["files"] = new JsonArray(Files.Select(f => (JsonNode?)new JsonObject
        {
            ["path"] = f.Path, ["size"] = f.Size, ["sha256"] = f.Sha256, ["blob"] = f.Blob
        }).ToArray())
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}

public static class UpdateVersion
{
    public static bool TryParse(string text, out Version version)
    {
        version = new Version(0, 0);
        var trimmed = Normalize(text);
        if (!System.Version.TryParse(trimmed, out var parsed)) return false;
        version = parsed; return true;
    }
    /// <summary>"v0.6.0" → "0.6.0"; a leading tag prefix must not defeat numeric comparison.</summary>
    public static string Normalize(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var dash = value.IndexOfAny(['-', '+']);
        return dash > 0 ? value[..dash] : value;
    }
    /// <summary>Negative when local is older, zero when equal, positive when local is newer.</summary>
    public static int Compare(string local, string remote)
    {
        if (!TryParse(local, out var a) || !TryParse(remote, out var b)) return 0;
        return a.CompareTo(b);
    }
}
