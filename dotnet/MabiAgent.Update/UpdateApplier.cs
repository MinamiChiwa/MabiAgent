using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace MabiAgent.Update;

public sealed record ApplyResult(bool Success, IReadOnlyList<string> Applied, string? Error, string? Rollback,
    IReadOnlyList<string>? Removed = null, IReadOnlyList<string>? Kept = null, bool Recoverable = true)
{
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(new
    {
        success = Success, applied = Applied, removed = Removed ?? [], kept = Kept ?? [], error = Error,
        rollback = Rollback, recoverable = Recoverable, at = DateTimeOffset.Now.ToString("O"), game_input = false
    });
}

/// <summary>
/// Replaces files from a verified staging directory.
///
/// Crash safety: the journal entry for a file is persisted as <c>moving</c> *before* the
/// target is touched, so any interruption leaves a durable record. Recovery only trusts that
/// persisted state: <c>planned</c> means untouched and is skipped, and nothing is inferred
/// from file hashes (an obsolete file's recorded hash is the old content, so a hash match
/// would be ambiguous). A journal write failure aborts the whole apply — the journal is the
/// recovery evidence and is never silently skipped. If a rollback cannot restore everything,
/// the journal is kept instead of being deleted.
/// </summary>
public static class UpdateApplier
{
    public const string JournalName = "journal.json";

    /// <summary>Test seam: called after the file is replaced and before the journal says "applied".</summary>
    public static Action<string>? AfterReplaceForTesting { get; set; }

    public static ApplyResult Apply(UpdatePlan plan, string backupDirectory, CancellationToken token = default)
    {
        var root = Path.GetFullPath(plan.InstallRoot);
        var journalPath = Path.Combine(root, "updates", JournalName);
        var backup = Path.GetFullPath(backupDirectory);
        if (!InstallLayout.TryResolveInside(root, backup, out _))
            return new ApplyResult(false, [], "备份目录必须位于安装目录内：" + backup, null);
        var ordered = OrderForApply(plan.Files);
        // A journal that still describes an unfinished update is the only record of what has to
        // be restored; a new apply must never replace it. The entry resolves it first.
        if (File.Exists(journalPath) && ReadJournalStatus(journalPath) is "applying" or "rollback_failed")
            return new ApplyResult(false, [], "存在未完成的更新日志，已拒绝覆盖：" + journalPath, null);
        var applied = new List<(string Path, string Target, string? Backup, bool Created)>();
        var removed = new List<string>();
        var kept = new List<string>();
        JsonArray entries;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            entries = JournalEntries(root, plan, ordered, backup);
            WriteJournal(journalPath, Journal("applying", plan.Version, root, backup, entries));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new ApplyResult(false, [], "无法写入更新日志，更新未开始：" + error.Message, null);
        }

        try
        {
            foreach (var item in plan.Obsolete ?? [])
            {
                token.ThrowIfCancellationRequested();
                var target = ResolveTarget(root, item.RelativePath);
                if (!File.Exists(target)) continue;
                // Only files still identical to what we shipped may be removed; anything the
                // user edited stays and is reported.
                if (item.Sha256 is { Length: 64 } pristine && !string.Equals(Hash(target), pristine, StringComparison.OrdinalIgnoreCase))
                {
                    kept.Add(item.RelativePath + "：本地已修改，保留");
                    continue;
                }
                var backupPath = BackupPath(backup, item.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                if (InstallLayout.AnyReparseBetween(backup, Path.GetDirectoryName(backupPath)!)) throw new InvalidDataException("备份路径包含重解析点");
                MarkMoving(journalPath, entries, item.RelativePath);
                File.Copy(target, backupPath, true);
                File.Delete(target);
                AfterReplaceForTesting?.Invoke(item.RelativePath);
                MarkApplied(journalPath, entries, item.RelativePath);
                applied.Add((item.RelativePath, target, backupPath, false));
                removed.Add(item.RelativePath);
            }

            foreach (var file in ordered)
            {
                token.ThrowIfCancellationRequested();
                var target = ResolveTarget(root, file.RelativePath);
                if (!File.Exists(file.StagedPath)) throw new InvalidDataException("暂存文件缺失：" + file.RelativePath);
                if (new FileInfo(file.StagedPath).Length != file.Size) throw new InvalidDataException("暂存文件大小不符：" + file.RelativePath);
                if (!string.Equals(Hash(file.StagedPath), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("暂存文件哈希不符：" + file.RelativePath);
                var directory = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(directory);
                if (InstallLayout.AnyReparseBetween(root, directory)) throw new InvalidDataException("目标路径包含重解析点：" + directory);
                var temporary = target + ".new";
                if (InstallLayout.AnyReparseBetween(root, temporary)) throw new InvalidDataException("临时目标文件是重解析点或无法确认：" + temporary);
                var exists = File.Exists(target);
                string? backupPath = null;
                if (exists)
                {
                    backupPath = BackupPath(backup, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    if (InstallLayout.AnyReparseBetween(backup, Path.GetDirectoryName(backupPath)!)) throw new InvalidDataException("备份路径包含重解析点");
                }
                // The journal says "moving" before the backup or the write happens.
                MarkMoving(journalPath, entries, file.RelativePath);
                if (exists && backupPath is not null) File.Copy(target, backupPath, true);
                // The old temporary file is from an interrupted attempt; remove it before writing.
                if (File.Exists(temporary)) File.Delete(temporary);
                File.Copy(file.StagedPath, temporary, true);
                File.Move(temporary, target, true);
                AfterReplaceForTesting?.Invoke(file.RelativePath);
                MarkApplied(journalPath, entries, file.RelativePath);
                applied.Add((file.RelativePath, target, backupPath, !exists));
            }
            WriteJournal(journalPath, Journal("complete", plan.Version, root, backup, entries));
            File.Delete(journalPath);
            return new ApplyResult(true, applied.Select(a => a.Path).ToArray(), null, null, removed, kept);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            // Restore from the durable journal rather than the in-memory list: a failure right
            // after File.Move happens before the entry is added here, and the journal is the
            // only place that records it.
            var durable = ReadJournalEntries(journalPath) ?? entries;
            var restored = new List<string>();
            var failures = new List<string>();
            RestoreFrom(root, backup, durable, restored, failures);
            CleanupTemporaries(root, plan);
            if (failures.Count > 0)
            {
                // Keep the journal: it is the only record of what still needs restoring.
                TryWriteJournal(journalPath, Journal("rollback_failed", plan.Version, root, backup, durable, error.Message));
                return new ApplyResult(false, applied.Select(a => a.Path).ToArray(), error.Message, string.Join("；", failures), removed, kept, false);
            }
            TryWriteJournal(journalPath, Journal("rolled_back", plan.Version, root, backup, durable, error.Message));
            TryDelete(journalPath);
            return new ApplyResult(false, applied.Select(a => a.Path).ToArray(), error.Message, string.Join("；", restored), removed, kept);
        }
    }

    /// <summary>
    /// Resolves a journal left behind by an interrupted apply. A journal that cannot be read or
    /// only partly restored is kept and reported as a failure, so the caller refuses to start a
    /// new update on top of it.
    /// </summary>
    public static RecoveryResult Recover(string installRoot)
    {
        var root = Path.GetFullPath(installRoot);
        var journalPath = Path.Combine(root, "updates", JournalName);
        if (!File.Exists(journalPath)) return RecoveryResult.None;
        JsonObject journal;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(journalPath)) is not JsonObject parsed)
                return new RecoveryResult(false, true, "更新日志损坏，已保留：" + journalPath);
            journal = parsed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new RecoveryResult(false, true, "更新日志无法读取，已保留：" + journalPath);
        }
        var status = journal["status"]?.GetValue<string>() ?? "";
        if (status is not ("applying" or "rollback_failed"))
        {
            TryDelete(journalPath);
            return new RecoveryResult(true, true, "已完成的更新日志已清理");
        }
        if (journal["entries"] is not JsonArray entries) return new RecoveryResult(false, true, "更新日志缺少条目，已保留：" + journalPath);
        var backupRoot = journal["backup_root"]?.GetValue<string>();
        var restored = new List<string>();
        var failures = new List<string>();
        RestoreFrom(root, backupRoot, entries, restored, failures);
        if (failures.Count > 0)
        {
            // Record the failed attempt on the same entry list: the journal stays the evidence.
            TryWriteJournal(journalPath, Journal("rollback_failed", journal["version"]?.GetValue<string>() ?? "",
                root, backupRoot ?? "", entries, string.Join("；", failures)));
            return new RecoveryResult(false, true, "崩溃恢复未完成，更新日志已保留：" + string.Join("，", failures));
        }
        TryDelete(journalPath);
        return new RecoveryResult(true, true,
            restored.Count == 0 ? "崩溃恢复：没有需要还原的文件" : "崩溃恢复：" + string.Join("，", restored));
    }

    /// <summary>
    /// Undoes every entry the journal marked <c>moving</c> or <c>applied</c>, newest first.
    /// <c>planned</c> entries were never touched and are skipped.
    /// </summary>
    private static void RestoreFrom(string root, string? backupRoot, JsonArray entries, List<string> restored, List<string> failures)
    {
        foreach (var item in entries.Reverse())
        {
            if (item is not JsonObject entry) continue;
            var state = entry["state"]?.GetValue<string>() ?? "";
            if (state is not ("moving" or "applied")) continue;
            var relative = entry["path"]?.GetValue<string>() ?? "";
            if (!InstallPaths.TryResolve(root, relative, out var target, out var reason)) { failures.Add(relative + "（" + reason + "）"); continue; }
            var created = entry["created"]?.GetValue<bool>() ?? false;
            try
            {
                if (InstallLayout.AnyReparseBetween(root, target)) { failures.Add(relative + "（目标路径包含重解析点）"); continue; }
                if (created)
                {
                    if (File.Exists(target)) File.Delete(target);
                }
                else
                {
                    var backup = entry["backup"]?.GetValue<string>();
                    if (backup is null) { failures.Add(relative + "（日志缺少备份路径）"); continue; }
                    if (backupRoot is null || !InstallLayout.TryResolveInside(backupRoot, backup, out var safeBackup))
                    { failures.Add(relative + "（备份路径不在安装目录内）"); continue; }
                    if (InstallLayout.AnyReparseBetween(Path.GetFullPath(backupRoot), safeBackup)) { failures.Add(relative + "（备份路径包含重解析点）"); continue; }
                    if (!File.Exists(safeBackup)) { failures.Add(relative + "（备份文件不存在）"); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(safeBackup, target, true);
                }
                restored.Add(relative);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failures.Add(relative + "（" + error.Message + "）");
            }
        }
    }

    private static JsonArray JournalEntries(string root, UpdatePlan plan, IReadOnlyList<PlannedFile> ordered, string backup)
    {
        var array = new JsonArray();
        foreach (var item in plan.Obsolete ?? [])
        {
            if (!InstallPaths.TryResolve(root, item.RelativePath, out var target, out _)) continue;
            if (!File.Exists(target)) continue;
            array.Add(new JsonObject
            {
                ["path"] = item.RelativePath, ["target"] = target,
                ["backup"] = BackupPath(backup, item.RelativePath), ["created"] = false,
                ["sha256"] = item.Sha256, ["state"] = "planned", ["kind"] = "obsolete"
            });
        }
        foreach (var file in ordered)
        {
            var target = ResolveTarget(root, file.RelativePath);
            var exists = File.Exists(target);
            array.Add(new JsonObject
            {
                ["path"] = file.RelativePath, ["target"] = target,
                ["backup"] = exists ? BackupPath(backup, file.RelativePath) : null,
                ["created"] = !exists, ["sha256"] = file.Sha256, ["state"] = "planned", ["kind"] = "file"
            });
        }
        return array;
    }

    /// <summary>The install baseline is written last so a half-applied update never claims the new version.</summary>
    private static List<PlannedFile> OrderForApply(IReadOnlyList<PlannedFile> files) =>
        files.Where(f => !IsBaseline(f.RelativePath)).Concat(files.Where(f => IsBaseline(f.RelativePath))).ToList();

    public static bool IsBaseline(string relative) => string.Equals(relative.Replace('\\', '/'), InstallManifest.FileName, StringComparison.OrdinalIgnoreCase);

    private static void MarkMoving(string journalPath, JsonArray entries, string relative) =>
        SetEntryState(journalPath, entries, relative, "moving");

    private static void MarkApplied(string journalPath, JsonArray entries, string relative) =>
        SetEntryState(journalPath, entries, relative, "applied");

    /// <summary>Persists the state before the corresponding file operation. IO errors abort the update.</summary>
    private static void SetEntryState(string journalPath, JsonArray entries, string relative, string state)
    {
        var journal = JsonNode.Parse(File.ReadAllText(journalPath)) as JsonObject
            ?? throw new InvalidDataException("更新日志已损坏");
        var found = false;
        if (journal["entries"] is JsonArray stored)
            foreach (var item in stored)
                if (item is JsonObject entry && string.Equals(entry["path"]?.GetValue<string>(), relative, StringComparison.OrdinalIgnoreCase))
                {
                    entry["state"] = state;
                    found = true;
                }
        foreach (var item in entries)
            if (item is JsonObject entry && string.Equals(entry["path"]?.GetValue<string>(), relative, StringComparison.OrdinalIgnoreCase))
                entry["state"] = state;
        if (!found) return;
        WriteJournal(journalPath, journal);
    }

    /// <summary>The status of an existing journal, or null when it cannot be read.</summary>
    private static string? ReadJournalStatus(string journalPath)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(journalPath)) is JsonObject journal
                ? journal["status"]?.GetValue<string>()
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    private static JsonArray? ReadJournalEntries(string journalPath)    {
        try
        {
            if (!File.Exists(journalPath)) return null;
            return JsonNode.Parse(File.ReadAllText(journalPath)) is JsonObject journal && journal["entries"] is JsonArray entries
                ? (JsonArray)entries.DeepClone()
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    private static void CleanupTemporaries(string root, UpdatePlan plan)
    {
        var paths = plan.Files.Select(f => f.RelativePath).Concat((plan.Obsolete ?? []).Select(o => o.RelativePath));
        foreach (var relative in paths)
        {
            try
            {
                if (!InstallPaths.TryResolve(root, relative, out var target, out _)) continue;
                var temporary = target + ".new";
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string ResolveTarget(string root, string relative)
    {
        if (!InstallPaths.TryResolve(root, relative, out var target, out var reason))
            throw new InvalidDataException("拒绝写入路径：" + relative + "（" + reason + "）");
        return target;
    }

    private static string BackupPath(string backupDirectory, string relative)
    {
        if (!InstallPaths.TryResolve(backupDirectory, relative, out var backup, out var reason))
            throw new InvalidDataException("备份路径被拒绝：" + relative + "（" + reason + "）");
        return backup;
    }

    private static JsonObject Journal(string status, string version, string root, string backup, JsonArray entries, string? error = null) => new()
    {
        ["status"] = status, ["version"] = version, ["install_root"] = root, ["backup_root"] = backup,
        ["error"] = error, ["entries"] = entries.DeepClone()
    };

    private static void WriteJournal(string path, JsonObject journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        File.WriteAllText(temporary, journal.ToJsonString());
        File.Move(temporary, path, true);
    }

    private static void TryWriteJournal(string path, JsonObject journal, string? error = null)
    {
        try { WriteJournal(path, journal); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
