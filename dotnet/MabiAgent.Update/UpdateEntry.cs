using System.Diagnostics;

namespace MabiAgent.Update;

/// <summary>Outcome of a journal recovery. <see cref="Ok"/> is false only when the journal
/// could not be resolved; the journal is then kept as the recovery evidence.</summary>
public sealed record RecoveryResult(bool Ok, bool HadJournal, string? Note)
{
    public static readonly RecoveryResult None = new(true, false, null);
}

/// <summary>
/// The process entry shared by <c>--apply-update</c> and <c>--recover-update</c>. It runs from
/// a staged copy of the installation (see <see cref="UpdateRunner"/>), so the files it rewrites
/// are not the loaded images of the process that asked for the update.
///
/// Two hard rules live here:
/// 1. Waiting for the old process never terminates it. A timeout aborts the update and writes
///    the reason; the user's application keeps running.
/// 2. A pending journal is always resolved before any file is replaced. When recovery fails,
///    the update does not start, so the journal cannot be overwritten by a fresh apply.
/// </summary>
public static class UpdateEntry
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitRecoveryBlocked = 2;
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(120);

    public static int Run(string[] args)
    {
        var recoveryOnly = args.Contains("--recover-update");
        var planPath = Value(args, "--apply-update");
        var root = Value(args, "--apply-root");
        if (!recoveryOnly && planPath is null) return ExitFailed;

        UpdatePlan? plan = null;
        if (!recoveryOnly)
        {
            try { plan = UpdatePlan.FromJson(File.ReadAllText(planPath!)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                Record(root ?? ParentOf(planPath!), Failure("更新计划无法读取：" + error.Message, "plan"));
                return ExitFailed;
            }
            root ??= plan.InstallRoot;
        }
        if (string.IsNullOrWhiteSpace(root)) return ExitFailed;
        var installRoot = Path.GetFullPath(root);

        // 1. Wait for the old process without ever terminating it.
        var waitPid = Value(args, "--wait-pid");
        if (waitPid is not null && int.TryParse(waitPid, out var waiting) && waiting > 0)
        {
            if (!WaitForExit(waiting, WaitTimeout(args)))
            {
                Record(installRoot, Failure("等待原程序退出超时，更新未开始；未终止原程序，它仍在运行", "timeout"));
                return ExitFailed;
            }
        }

        // 2. Resolve a journal left by an interrupted update before touching any file.
        var recovery = UpdateApplier.Recover(installRoot);
        if (!recovery.Ok)
        {
            Record(installRoot, Failure(recovery.Note ?? "崩溃恢复未完成", "recovery"));
            return ExitRecoveryBlocked;
        }

        if (recoveryOnly || plan is null)
        {
            Record(installRoot, new System.Text.Json.Nodes.JsonObject
            {
                ["success"] = true, ["recovered"] = recovery.HadJournal, ["note"] = recovery.Note,
                ["at"] = DateTimeOffset.Now.ToString("O"), ["game_input"] = false
            }.ToJsonString());
            Relaunch(installRoot, args);
            return ExitOk;
        }

        ApplyResult outcome;
        try { outcome = UpdateApplier.Apply(plan, Path.Combine(installRoot, "updates", "backup", plan.Version)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Record(installRoot, Failure(error.Message, "apply"));
            return ExitFailed;
        }
        Record(installRoot, outcome.ToJson());
        if (outcome.Success) Relaunch(installRoot, args);
        return outcome.Success ? ExitOk : ExitFailed;
    }

    /// <summary>
    /// True when the process is gone. A process that cannot be observed (access denied) counts
    /// as "not gone": the update then stops instead of guessing or terminating anything.
    /// </summary>
    public static bool WaitForExit(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        try
        {
            using var process = Process.GetProcessById(processId);
            while (!process.HasExited)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return false;
                if (process.WaitForExit((int)Math.Min(left.TotalMilliseconds, 500))) return true;
            }
            return true;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public static TimeSpan WaitTimeout(string[] args)
    {
        var value = Value(args, "--wait-timeout-ms");
        return value is not null && int.TryParse(value, out var milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : DefaultWait;
    }

    /// <summary>
    /// Modes that own their own state (preview, acceptance, self-check) and must never repair the
    /// installation. Everything else — including the packaged launcher, which always passes
    /// <c>--root</c> and <c>--state-dir</c> — counts as a normal user start.
    /// </summary>
    public static bool IsAutomatedRun(string[] args) => args.Any(argument => AutomatedFlags.Contains(argument));

    private static readonly string[] AutomatedFlags =
    [
        "--self-check", "--ui-preview", "--close-check", "--live-queue-acceptance",
        "--live-storage-acceptance", "--upgrade-chains", "--observe-shop"
    ];

    private static string Failure(string message, string stage) => new System.Text.Json.Nodes.JsonObject
    {
        ["success"] = false, ["error"] = message, ["stage"] = stage,
        ["at"] = DateTimeOffset.Now.ToString("O"), ["game_input"] = false
    }.ToJsonString();

    private static string? ParentOf(string path)
    {
        try { return Path.GetDirectoryName(Path.GetFullPath(path)); }
        catch (ArgumentException) { return null; }
    }

    private static void Record(string? root, string json)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            var directory = Path.Combine(root, "updates");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "last-update.json"), json);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Only a successful outcome reopens the app; a caller can opt out with <c>--no-relaunch</c>.</summary>
    private static void Relaunch(string installRoot, string[] args)
    {
        if (args.Contains("--no-relaunch")) return;
        try
        {
            var executable = Path.Combine(installRoot, InstallLayout.Entry);
            if (!File.Exists(executable)) return;
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = installRoot });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static string? Value(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
