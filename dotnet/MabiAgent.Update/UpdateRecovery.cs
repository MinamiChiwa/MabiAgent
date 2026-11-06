using System.Text.Json.Nodes;

namespace MabiAgent.Update;

/// <summary>
/// Startup coordination for a journal left behind by an interrupted update.
///
/// The app that detects the journal is running from the very assemblies the update has to move,
/// so it cannot restore them itself on Windows. Instead it stages the runner copy and exits;
/// the copy restores the journal and reopens the app. A marker records the journal timestamp
/// that was already attempted, so a failing restore cannot turn the app into a restart loop —
/// the journal stays as evidence and the app starts normally.
/// </summary>
public static class UpdateRecovery
{
    public const string MarkerName = "recovery-state.json";

    /// <summary>
    /// True when a recovery runner was started and the caller must exit. <paramref name="launch"/>
    /// is a seam for tests; production uses <see cref="UpdateRunner.StartRecovery"/>.
    /// </summary>
    public static bool TryHandlePending(string installRoot, int currentProcessId,
        Func<string, string, string[], bool>? launch = null)
    {
        var root = InstallLayout.ResolveRoot(installRoot);
        var journalPath = Path.Combine(root, "updates", UpdateApplier.JournalName);
        var markerPath = Path.Combine(root, "updates", MarkerName);
        try
        {
            if (!File.Exists(journalPath)) { TryDelete(markerPath); return false; }
            if (Status(journalPath) is not ("applying" or "rollback_failed")) { TryDelete(markerPath); return false; }
            var stamp = File.GetLastWriteTimeUtc(journalPath).Ticks;
            if (Attempted(markerPath) == stamp) return false;
            var runner = UpdateRunner.Stage(root);
            var args = new[]
            {
                "--recover-update", "--apply-root", root,
                "--wait-pid", currentProcessId.ToString(),
                "--wait-timeout-ms", "60000"
            };
            var started = launch is null ? UpdateRunner.StartRecovery(runner, root, args) : launch(runner, root, args);
            if (!started) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, new JsonObject { ["journal_ticks"] = stamp, ["attempts"] = 1 }.ToJsonString());
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or System.ComponentModel.Win32Exception or InvalidOperationException or System.Text.Json.JsonException)
        {
            // Starting the app is more important than a recovery attempt; the journal stays.
            return false;
        }
    }

    private static string? Status(string journalPath)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(journalPath)) is JsonObject journal
                ? journal["status"]?.GetValue<string>()
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    private static long? Attempted(string markerPath)
    {
        try
        {
            if (!File.Exists(markerPath)) return null;
            return JsonNode.Parse(File.ReadAllText(markerPath)) is JsonObject marker
                ? marker["journal_ticks"]?.GetValue<long>()
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
