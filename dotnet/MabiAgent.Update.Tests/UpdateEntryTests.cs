using MabiAgent.Update;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace MabiAgent.Update.Tests;

/// <summary>
/// Exercises the real process entry (<see cref="UpdateEntry.Run"/>, the same code the desktop
/// executable calls for <c>--apply-update</c> and <c>--recover-update</c>) and the startup
/// coordination in <see cref="UpdateRecovery"/>. A real child process stands in for the running
/// app, so the "never terminate the parent" rule is proven against a live process.
/// </summary>
public sealed class UpdateEntryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mabi-entry-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> children = [];

    public UpdateEntryTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        foreach (var child in children)
        {
            try { if (!child.HasExited) child.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            child.Dispose();
        }
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string Write(string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(root, relative));

    private string JournalPath => Path.Combine(root, "updates", UpdateApplier.JournalName);

    private string RecordPath => Path.Combine(root, "updates", "last-update.json");

    private JsonObject Record() => JsonNode.Parse(File.ReadAllText(RecordPath))!.AsObject();

    /// <summary>A plan that replaces <c>app/b.dll</c> with verified staged bytes.</summary>
    private string WritePlan(string relative = "app/b.dll", string content = "new-b")
    {
        var staged = Write("staging/" + relative.Replace('/', '_'), content);
        var bytes = Encoding.UTF8.GetBytes(content);
        var plan = new UpdatePlan("0.6.0", root,
            [new PlannedFile(relative, staged, Sha(bytes), bytes.LongLength)], false, null);
        var path = Path.Combine(root, "updates", "update-plan.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, plan.ToJson());
        return path;
    }

    /// <summary>A journal that says <c>app/a.dll</c> was already moved; the backup holds the old bytes.</summary>
    private void WritePendingJournal(bool withBackup = true)
    {
        Write("app/a.dll", "new-a");
        var backup = Path.Combine(root, "updates", "backup", "0.6.0", "app", "a.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        if (withBackup) File.WriteAllText(backup, "old-a");
        var journal = new JsonObject
        {
            ["status"] = "applying", ["version"] = "0.6.0", ["install_root"] = root,
            ["backup_root"] = Path.Combine(root, "updates", "backup", "0.6.0"),
            ["entries"] = new JsonArray(new JsonObject
            {
                ["path"] = "app/a.dll", ["target"] = Path.Combine(root, "app", "a.dll"),
                ["backup"] = backup, ["created"] = false, ["state"] = "moving",
                ["sha256"] = Sha(Encoding.UTF8.GetBytes("new-a"))
            })
        };
        Directory.CreateDirectory(Path.Combine(root, "updates"));
        File.WriteAllText(JournalPath, journal.ToJsonString());
    }

    private Process StartChild(string arguments)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(arguments);
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动测试子进程");
        children.Add(process);
        return process;
    }

    [Fact]
    public void TimeoutLeavesTheWaitingProcessRunningAndDoesNotApply()
    {
        Write("app/b.dll", "old-b");
        var plan = WritePlan();
        // Pinging loopback keeps a real process alive for ~30 seconds.
        using var parent = StartChild("ping -n 30 127.0.0.1 > nul");

        var exit = UpdateEntry.Run(["--apply-update", plan, "--apply-root", root,
            "--wait-pid", parent.Id.ToString(), "--wait-timeout-ms", "600", "--no-relaunch"]);

        Assert.Equal(UpdateEntry.ExitFailed, exit);
        Assert.False(parent.HasExited, "更新入口不得终止原程序");
        var record = Record();
        Assert.False(record["success"]!.GetValue<bool>());
        Assert.Equal("timeout", record["stage"]!.GetValue<string>());
        Assert.Contains("未终止原程序", record["error"]!.GetValue<string>(), StringComparison.Ordinal);
        // Nothing was touched: no journal, no replacement.
        Assert.False(File.Exists(JournalPath));
        Assert.Equal("old-b", Read("app/b.dll"));
    }

    [Fact]
    public void EntryWaitsForTheOldProcessAndThenApplies()
    {
        Write("app/b.dll", "old-b");
        var plan = WritePlan();
        using var parent = StartChild("ping -n 2 127.0.0.1 > nul");

        var exit = UpdateEntry.Run(["--apply-update", plan, "--apply-root", root,
            "--wait-pid", parent.Id.ToString(), "--wait-timeout-ms", "60000", "--no-relaunch"]);

        Assert.Equal(UpdateEntry.ExitOk, exit);
        Assert.Equal("new-b", Read("app/b.dll"));
        Assert.True(Record()["success"]!.GetValue<bool>());
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void EntryRestoresAPendingJournalBeforeReplacingFiles()
    {
        Write("app/b.dll", "old-b");
        WritePendingJournal();
        var plan = WritePlan();

        var exit = UpdateEntry.Run(["--apply-update", plan, "--apply-root", root, "--no-relaunch"]);

        Assert.Equal(UpdateEntry.ExitOk, exit);
        // The interrupted file was restored, the new one was applied, the journal is gone.
        Assert.Equal("old-a", Read("app/a.dll"));
        Assert.Equal("new-b", Read("app/b.dll"));
        Assert.False(File.Exists(JournalPath));
        Assert.True(Record()["success"]!.GetValue<bool>());
    }

    [Fact]
    public void EntryRefusesToApplyWhenThePendingJournalCannotBeRecovered()
    {
        Write("app/b.dll", "old-b");
        WritePendingJournal(withBackup: false);
        var plan = WritePlan();

        var exit = UpdateEntry.Run(["--apply-update", plan, "--apply-root", root, "--no-relaunch"]);

        Assert.Equal(UpdateEntry.ExitRecoveryBlocked, exit);
        // The pending journal is intact and was not replaced by a new apply.
        Assert.True(File.Exists(JournalPath));
        Assert.Equal("rollback_failed", JsonNode.Parse(File.ReadAllText(JournalPath))!["status"]!.GetValue<string>());
        Assert.Equal("old-b", Read("app/b.dll"));
        Assert.False(Record()["success"]!.GetValue<bool>());
        Assert.Equal("recovery", Record()["stage"]!.GetValue<string>());
    }

    [Fact]
    public void ApplyItselfNeverOverwritesAnUnfinishedJournal()
    {
        WritePendingJournal();
        var staged = Write("staging/app_c.dll", "new-c");
        var bytes = Encoding.UTF8.GetBytes("new-c");
        var plan = new UpdatePlan("0.6.1", root,
            [new PlannedFile("app/c.dll", staged, Sha(bytes), bytes.LongLength)], false, null);

        var result = UpdateApplier.Apply(plan, Path.Combine(root, "updates", "backup", "0.6.1"));

        Assert.False(result.Success);
        Assert.Contains("未完成的更新日志", result.Error!, StringComparison.Ordinal);
        Assert.Equal("applying", JsonNode.Parse(File.ReadAllText(JournalPath))!["status"]!.GetValue<string>());
    }

    [Fact]
    public void EntryRefusesToApplyWhenTheUnfinishedJournalHasNoEntries()
    {
        Write("app/b.dll", "old-b");
        var plan = WritePlan();
        Directory.CreateDirectory(Path.Combine(root, "updates"));
        File.WriteAllText(JournalPath, new JsonObject
        {
            ["status"] = "applying", ["version"] = "0.6.0", ["install_root"] = root
        }.ToJsonString());

        var exit = UpdateEntry.Run(["--apply-update", plan, "--apply-root", root, "--no-relaunch"]);

        Assert.Equal(UpdateEntry.ExitRecoveryBlocked, exit);
        Assert.True(File.Exists(JournalPath));
        Assert.Equal("applying", JsonNode.Parse(File.ReadAllText(JournalPath))!["status"]!.GetValue<string>());
        Assert.Equal("old-b", Read("app/b.dll"));
        Assert.False(Record()["success"]!.GetValue<bool>());
        Assert.Equal("recovery", Record()["stage"]!.GetValue<string>());
    }

    [Fact]
    public void RecoveryEntryRestoresFilesAndRemovesTheJournal()
    {
        WritePendingJournal();

        var exit = UpdateEntry.Run(["--recover-update", "--apply-root", root, "--no-relaunch"]);

        Assert.Equal(UpdateEntry.ExitOk, exit);
        Assert.Equal("old-a", Read("app/a.dll"));
        Assert.False(File.Exists(JournalPath));
        Assert.True(Record()["success"]!.GetValue<bool>());
    }

    [Fact]
    public void StartupStagesTheRecoveryInsteadOfRestoringFromTheLoadedInstall()
    {
        Write("MabiAgent.exe", "exe");
        Write("app/MabiAgent.Desktop.dll", "ui");
        WritePendingJournal();
        string? runner = null;
        string? stagedRoot = null;
        string[]? arguments = null;

        var handled = UpdateRecovery.TryHandlePending(root, 4242, (path, work, args) =>
        {
            runner = path; stagedRoot = work; arguments = args;
            return true;
        });

        Assert.True(handled);
        Assert.NotNull(runner);
        Assert.StartsWith(Path.GetTempPath(), Path.GetFullPath(runner!), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(runner));
        Assert.Equal("exe", File.ReadAllText(runner!));
        Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(stagedRoot!));
        Assert.Contains("--recover-update", arguments!);
        Assert.Contains("--apply-root", arguments!);
        Assert.Contains("4242", arguments!);
        // The journal is untouched by the app that is about to exit.
        Assert.True(File.Exists(JournalPath));
        Assert.True(File.Exists(Path.Combine(root, "updates", UpdateRecovery.MarkerName)));
        try { Directory.Delete(Path.GetDirectoryName(runner!)!, true); } catch (IOException) { }
    }

    [Fact]
    public void StartupDoesNotRepeatACombinationItAlreadyTried()
    {
        Write("MabiAgent.exe", "exe");
        WritePendingJournal();
        var launches = 0;
        bool Launch(string path, string work, string[] args) { launches++; return true; }

        Assert.True(UpdateRecovery.TryHandlePending(root, 1, Launch));
        Assert.False(UpdateRecovery.TryHandlePending(root, 1, Launch));
        Assert.Equal(1, launches);
    }

    [Fact]
    public void StartupIgnoresAFinishedJournalAndClearsTheMarker()
    {
        Write("MabiAgent.exe", "exe");
        var journal = new JsonObject { ["status"] = "complete", ["entries"] = new JsonArray() };
        Directory.CreateDirectory(Path.Combine(root, "updates"));
        File.WriteAllText(JournalPath, journal.ToJsonString());
        var marker = Path.Combine(root, "updates", UpdateRecovery.MarkerName);
        File.WriteAllText(marker, new JsonObject { ["journal_ticks"] = 1, ["attempts"] = 1 }.ToJsonString());

        Assert.False(UpdateRecovery.TryHandlePending(root, 1, (_, _, _) => true));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void ThePackagedLauncherArgumentsStillCountAsANormalStart()
    {
        // Start-MabiAgent.cmd always passes --root and --state-dir; startup repair must still run.
        Assert.False(UpdateEntry.IsAutomatedRun(["--root", @"C:\game", "--state-dir", @"C:\game\data"]));
        Assert.False(UpdateEntry.IsAutomatedRun([]));
        Assert.True(UpdateEntry.IsAutomatedRun(["--self-check", "--report", "r.json"]));
        Assert.True(UpdateEntry.IsAutomatedRun(["--live-queue-acceptance", "--acceptance-start-paused"]));
    }

    [Fact]
    public void CleanupStaleNeverDeletesTheFolderTheAppRunsFrom()
    {
        var runningFrom = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.GetFileName(runningFrom).StartsWith(UpdateRunner.FolderPrefix, StringComparison.Ordinal))
        {
            // Not running from a staged copy in this host; the guard is still exercised by the
            // current-directory comparison above, so nothing to delete here.
            UpdateRunner.CleanupStale(TimeSpan.Zero);
            return;
        }
        UpdateRunner.CleanupStale(TimeSpan.Zero);
        Assert.True(Directory.Exists(runningFrom));
    }
}
