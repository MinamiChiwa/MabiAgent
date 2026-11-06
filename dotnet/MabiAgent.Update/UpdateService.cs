using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MabiAgent.Update;

public sealed record PlannedFile(string RelativePath, string StagedPath, string Sha256, long Size);

/// <summary>A file the new release no longer ships. It is deleted only when still pristine.</summary>
public sealed record ObsoleteFile(string RelativePath, string? Sha256);

/// <summary>
/// A staged update. <paramref name="Conflicts"/> lists local files that differ from
/// both the install baseline and the new manifest: they are kept, never overwritten.
/// </summary>
public sealed record UpdatePlan(string Version, string InstallRoot, IReadOnlyList<PlannedFile> Files, bool FullPackage,
    string? PackagePath, IReadOnlyList<string>? Conflicts = null, IReadOnlyList<ObsoleteFile>? Obsolete = null)
{
    public string ToJson() => new JsonObject
    {
        ["version"] = Version, ["install_root"] = InstallRoot, ["full_package"] = FullPackage, ["package_path"] = PackagePath,
        ["files"] = new JsonArray(Files.Select(f => (JsonNode?)new JsonObject
        {
            ["path"] = f.RelativePath, ["staged"] = f.StagedPath, ["sha256"] = f.Sha256, ["size"] = f.Size
        }).ToArray()),
        ["conflicts"] = new JsonArray((Conflicts ?? []).Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        ["obsolete"] = new JsonArray((Obsolete ?? []).Select(o => (JsonNode?)new JsonObject
        {
            ["path"] = o.RelativePath, ["sha256"] = o.Sha256
        }).ToArray())
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static UpdatePlan FromJson(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("更新计划不是 JSON 对象");
        var files = new List<PlannedFile>();
        if (node["files"] is JsonArray array)
            foreach (var item in array)
            {
                if (item is not JsonObject file) continue;
                files.Add(new PlannedFile(
                    file["path"]?.GetValue<string>() ?? throw new InvalidDataException("更新计划缺少路径"),
                    file["staged"]?.GetValue<string>() ?? throw new InvalidDataException("更新计划缺少暂存路径"),
                    file["sha256"]?.GetValue<string>() ?? throw new InvalidDataException("更新计划缺少哈希"),
                    file["size"]?.GetValue<long>() ?? 0));
            }
        var obsolete = new List<ObsoleteFile>();
        if (node["obsolete"] is JsonArray dropped)
            foreach (var item in dropped)
            {
                if (item is JsonObject entry) obsolete.Add(new ObsoleteFile(entry["path"]?.GetValue<string>() ?? "", entry["sha256"]?.GetValue<string>()));
                else if (item?.GetValue<string>() is { Length: > 0 } legacy) obsolete.Add(new ObsoleteFile(legacy, null));
            }
        static string[] Strings(JsonNode? value) => value is JsonArray a ? a.Select(v => v?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray() : [];
        return new UpdatePlan(
            node["version"]?.GetValue<string>() ?? "",
            node["install_root"]?.GetValue<string>() ?? throw new InvalidDataException("更新计划缺少安装目录"),
            files,
            node["full_package"]?.GetValue<bool>() ?? false,
            node["package_path"]?.GetValue<string>(),
            Strings(node["conflicts"]), obsolete);
    }
}

public sealed record UpdateProgress(int Done, int Total, long Bytes, string Current);

public sealed record UpdateCheck(string LocalVersion, string? RemoteVersion, bool Available, bool HasManifest, string Summary,
    ReleaseInfo? Release = null, UpdateManifest? Manifest = null);

/// <summary>Install layout: the launcher sits at the root, the assemblies live in app/.</summary>
public static class InstallLayout
{
    public const string Entry = "MabiAgent.exe";
    public const string AppFolder = "app";
    public static string ResolveRoot(string baseDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        if (directory.Name.Equals(AppFolder, StringComparison.OrdinalIgnoreCase) && directory.Parent is { } parent
            && File.Exists(Path.Combine(parent.FullName, Entry)))
            return parent.FullName;
        return directory.FullName;
    }
    public static bool TryResolveInside(string root, string path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(path);
        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        full = candidate;
        return true;
    }
    public static bool IsReparse(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return false;
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    /// <summary>
    /// True when the root itself, or any component between the root and
    /// <paramref name="path"/> (inclusive), is a junction or reparse point, or cannot be
    /// verified at all. Used before deleting or writing anything, so a linked directory can
    /// never redirect the update; an unreadable attribute is treated as unsafe.
    /// </summary>
    public static bool AnyReparseBetween(string root, string path)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        var inside = full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!inside) return true;
        var current = full;
        while (current.Length > rootFull.Length)
        {
            if (!IsClean(current)) return true;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Length >= current.Length) return true;
            current = parent;
        }
        return !IsClean(rootFull);
    }

    /// <summary>False when the path is a link or its attributes cannot be read.</summary>
    private static bool IsClean(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// The per-install baseline written by the packager (<c>tools/native_portable.py</c>):
/// <c>release-manifest.json</c> at the install root, with
/// <c>files[{path,bytes,sha256}]</c> in posix form and no <c>data/</c> entries.
/// </summary>
public static class InstallManifest
{
    public const string FileName = "release-manifest.json";
    public static IReadOnlyDictionary<string, string>? Load(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject node || node["files"] is not JsonArray files) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in files)
            {
                if (item is not JsonObject file) continue;
                var name = file["path"]?.GetValue<string>()?.Replace('\\', '/');
                var hash = file["sha256"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name) || hash is not { Length: 64 }) continue;
                if (name.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) continue;
                map[name] = hash.ToLowerInvariant();
            }
            return map.Count == 0 ? null : map;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Client side of the update channel: read the official release, fetch and verify the
/// per-file manifest, then stage only the files whose hash differs from the install.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string Repository = "MinamiChiwa/MabiAgent";
    public const string PackagePrefix = "MabiAgent-";
    private readonly UpdateTransport transport;
    private readonly string root;
    public UpdateService(string installRoot, HttpMessageHandler handler, TransportOptions? options = null)
    {
        root = InstallLayout.ResolveRoot(installRoot);
        transport = new UpdateTransport(handler, options);
    }
    public string Root => root;
    public IReadOnlyList<string> Notes => transport.Notes;
    public bool MirrorConfigured => transport.MirrorConfigured;

    public async Task<UpdateCheck> CheckAsync(string localVersion, CancellationToken token = default)
    {
        var release = await transport.LatestReleaseAsync(Repository, token).ConfigureAwait(false);
        var comparison = UpdateVersion.Compare(localVersion, release.Version);
        if (comparison >= 0)
        {
            var summary = comparison == 0
                ? $"已是最新版本 {localVersion}"
                : $"本地 {localVersion} 高于已发布 {release.Version}：视为已是最新（本地版本可能尚未发布）";
            return new UpdateCheck(localVersion, release.Version, false, false, summary, release);
        }
        var asset = release.Asset(UpdateManifest.FileName);
        if (asset is null)
            return new UpdateCheck(localVersion, release.Version, true, false,
                $"发现 {release.Version}，但该发布没有逐文件清单，只能按完整包更新", release);
        var manifest = await ReadManifestAsync(asset, token).ConfigureAwait(false);
        if (!string.Equals(manifest.Version, release.Version, StringComparison.Ordinal))
            throw new InvalidDataException($"清单版本 {manifest.Version} 与发布版本 {release.Version} 不一致");
        return new UpdateCheck(localVersion, release.Version, true, true, $"发现新版本 {release.Version}", release, manifest);
    }

    private async Task<UpdateManifest> ReadManifestAsync(ReleaseAsset asset, CancellationToken token)
    {
        // The manifest never goes through the mirror, and without a GitHub digest the only
        // remaining anchor is the official HTTPS host itself.
        var expected = DigestValue(asset.Digest);
        if (expected is null) transport.Notes.Add("发布未提供清单摘要，仅依赖官方 HTTPS 通道");
        var temporary = Path.Combine(Path.GetTempPath(), "mabi-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await transport.DownloadVerifiedAsync(asset, expected, asset.Size > 0 ? asset.Size : null, temporary, false, token).ConfigureAwait(false);
            return UpdateManifest.Parse(await File.ReadAllTextAsync(temporary, token).ConfigureAwait(false));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string? DigestValue(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var value = digest.Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        return UpdateManifest.IsSha256(value) ? value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Three-way compare: install baseline (<c>release-manifest.json</c>) vs local file vs
    /// new manifest. Only pristine files are replaced; anything the user changed, deleted
    /// or added is kept and reported as a conflict. The new baseline itself is part of the
    /// plan and is written last by the applier, so a second update never mistakes the
    /// previous version's files for user edits. Staging always lives under the tool's own
    /// <c>updates/staging/&lt;version&gt;</c> directory.
    /// </summary>
    public async Task<UpdatePlan?> PrepareAsync(UpdateCheck check, IProgress<UpdateProgress>? progress = null, CancellationToken token = default)
    {
        var manifest = check.Manifest ?? throw new InvalidOperationException("没有逐文件清单，不能按增量准备");
        var release = check.Release ?? throw new InvalidOperationException("缺少发布信息");
        var staging = StagingDirectory(manifest.Version);
        ResetStaging(staging);
        var baseline = InstallManifest.Load(root);
        var conflicts = new List<string>();
        var changed = new List<ManifestFile>();
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!InstallPaths.TryResolve(root, file.Path, out var target, out var reason))
                throw new InvalidDataException("拒绝更新路径：" + file.Path + "（" + reason + "）");
            var local = File.Exists(target) ? await HashFileAsync(target, token).ConfigureAwait(false) : null;
            if (string.Equals(local, file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            // The baseline is tool-owned metadata, not user content: it is always replaced.
            if (UpdateApplier.IsBaseline(file.Path)) { changed.Add(file); continue; }
            if (local is null)
            {
                if (baseline is not null && baseline.ContainsKey(file.Path))
                {
                    conflicts.Add(file.Path + "：本地已删除，保留现状");
                    continue;
                }
                changed.Add(file);
                continue;
            }
            if (baseline is null) { conflicts.Add(file.Path + "：缺少安装基准清单，保留本地文件"); continue; }
            if (baseline.TryGetValue(file.Path, out var pristine) && string.Equals(local, pristine, StringComparison.OrdinalIgnoreCase)) { changed.Add(file); continue; }
            conflicts.Add(file.Path + "：本地已修改，保留本地文件");
        }
        var obsolete = baseline is null
            ? new List<ObsoleteFile>()
            : baseline.Where(pair => manifest.Files.All(f => !string.Equals(f.Path, pair.Key, StringComparison.OrdinalIgnoreCase)))
                .Where(pair => !pair.Key.StartsWith("data/", StringComparison.OrdinalIgnoreCase)
                    && !pair.Key.StartsWith("updates/", StringComparison.OrdinalIgnoreCase)
                    && !UpdateApplier.IsBaseline(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new ObsoleteFile(pair.Key, pair.Value)).ToList();
        var planned = new List<PlannedFile>();
        long bytes = 0;
        for (var i = 0; i < changed.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var file = changed[i];
            var asset = release.Asset(file.Blob) ?? throw new InvalidDataException("发布缺少文件资产：" + file.Blob);
            if (!InstallPaths.TryResolve(staging, file.Path, out var staged, out var why))
                throw new InvalidDataException("暂存路径被拒绝：" + file.Path + "（" + why + "）");
            progress?.Report(new UpdateProgress(i, changed.Count, bytes, file.Path));
            await transport.DownloadVerifiedAsync(asset, file.Sha256, file.Size, staged, true, token).ConfigureAwait(false);
            bytes += new FileInfo(staged).Length;
            planned.Add(new PlannedFile(file.Path, staged, file.Sha256, file.Size));
        }
        progress?.Report(new UpdateProgress(changed.Count, changed.Count, bytes, ""));
        if (planned.Count == 0 && obsolete.Count == 0 && conflicts.Count == 0) return null;
        await EnsureBaselineAsync(manifest, staging, planned, token).ConfigureAwait(false);
        return new UpdatePlan(manifest.Version, root, planned, false, null, conflicts, obsolete);
    }

    /// <summary>
    /// Guarantees the update carries its own <c>release-manifest.json</c>. The release
    /// normally ships it, but a hand-built asset set may not: without it the next update
    /// would see every installed file as "modified by the user".
    /// </summary>
    private static Task EnsureBaselineAsync(UpdateManifest manifest, string staging, List<PlannedFile> planned, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (planned.Any(p => UpdateApplier.IsBaseline(p.RelativePath))) return Task.CompletedTask;
        if (manifest.Files.Any(f => UpdateApplier.IsBaseline(f.Path))) return Task.CompletedTask;
        var rows = new JsonArray();
        foreach (var file in manifest.Files)
            if (!UpdateApplier.IsBaseline(file.Path))
                rows.Add(new JsonObject { ["path"] = file.Path, ["bytes"] = file.Size, ["sha256"] = file.Sha256 });
        var document = new JsonObject
        {
            ["schema_version"] = 1,
            ["built_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["entry"] = "Start-MabiAgent.cmd",
            ["runtime"] = "win-x64",
            ["self_contained"] = true,
            ["game_input"] = false,
            ["pointer_changed"] = false,
            ["files"] = rows
        };
        var bytes = System.Text.Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var target = Path.Combine(staging, InstallManifest.FileName);
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(target, bytes);
        planned.Add(new PlannedFile(InstallManifest.FileName, target, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength));
        return Task.CompletedTask;
    }

    private string StagingDirectory(string version)
    {
        var rootFull = Path.GetFullPath(root);
        if (InstallLayout.AnyReparseBetween(rootFull, rootFull)) throw new InvalidOperationException("安装目录是符号链接或重解析点：" + rootFull);
        var updates = Path.Combine(rootFull, "updates");
        var allowed = Path.Combine(updates, "staging");
        if (InstallLayout.AnyReparseBetween(rootFull, allowed)) throw new InvalidOperationException("更新目录或暂存目录包含符号链接或重解析点");
        var staging = Path.GetFullPath(Path.Combine(allowed, UpdateVersion.Normalize(version)));
        var prefix = allowed.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!staging.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("暂存目录必须位于 updates/staging 之下");
        if (InstallLayout.AnyReparseBetween(rootFull, staging)) throw new InvalidOperationException("暂存目录包含符号链接或重解析点");
        return staging;
    }

    /// <summary>Only ever deletes inside <c>updates/staging/&lt;version&gt;</c>, after re-checking every link.</summary>
    private void ResetStaging(string staging)
    {
        if (InstallLayout.AnyReparseBetween(root, staging))
            throw new InvalidOperationException("拒绝清理包含重解析点的暂存目录：" + staging);
        if (!Directory.Exists(staging)) { Directory.CreateDirectory(staging); return; }
        Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
    }

    /// <summary>Older releases have no manifest; the complete zip is fetched and verified, never called incremental.</summary>
    public async Task<(string Path, string Sha256)?> DownloadFullPackageAsync(ReleaseInfo release, string directory, CancellationToken token = default)
    {
        var asset = release.Assets.FirstOrDefault(a => a.Name.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));
        if (asset is null) return null;
        var target = Path.GetFullPath(Path.Combine(directory, asset.Name));
        var expected = DigestValue(asset.Digest);
        if (expected is null) expected = await ReadSumsAsync(release, asset.Name, token).ConfigureAwait(false);
        if (expected is null) throw new InvalidDataException("完整包没有摘要也没有 SHA256SUMS.txt，拒绝使用未校验的包");
        var hash = await transport.DownloadVerifiedAsync(asset, expected, asset.Size > 0 ? asset.Size : null, target, false, token).ConfigureAwait(false);
        return (target, hash);
    }

    private async Task<string?> ReadSumsAsync(ReleaseInfo release, string name, CancellationToken token)
    {
        var sums = release.Asset("SHA256SUMS.txt");
        if (sums is null) return null;
        var temporary = Path.Combine(Path.GetTempPath(), "mabi-sums-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await transport.DownloadVerifiedAsync(sums, DigestValue(sums.Digest), sums.Size > 0 ? sums.Size : null, temporary, false, token).ConfigureAwait(false);
            foreach (var line in await File.ReadAllLinesAsync(temporary, token).ConfigureAwait(false))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[^1].TrimStart('*') == name && UpdateManifest.IsSha256(parts[0].ToLowerInvariant()))
                    return parts[0].ToLowerInvariant();
            }
            return null;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
    public void Dispose() => transport.Dispose();
}
