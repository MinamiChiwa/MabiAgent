using System.Diagnostics;

namespace MabiAgent.Update;

/// <summary>
/// Starts the file replacement from a copy of the installation. Windows refuses to move a
/// file whose image is loaded, and the update replaces exactly those files, so the process
/// that applies the update must not run from the install directory. The copy lives in a
/// controlled temp folder (<c>%TEMP%/MabiAgent-apply-&lt;id&gt;</c>) and is removed on the
/// next normal start.
/// </summary>
public static class UpdateRunner
{
    public const string FolderPrefix = "MabiAgent-apply-";

    /// <summary>Copies the executable and its <c>app/</c> assemblies, then returns the copied executable.</summary>
    public static string Stage(string installRoot)
    {
        var root = InstallLayout.ResolveRoot(installRoot);
        var source = Path.Combine(root, InstallLayout.Entry);
        if (!File.Exists(source)) throw new FileNotFoundException("找不到主程序，无法准备更新进程", source);
        var target = Path.Combine(Path.GetTempPath(), FolderPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            File.Copy(source, Path.Combine(target, InstallLayout.Entry), true);
            var app = Path.Combine(root, InstallLayout.AppFolder);
            if (Directory.Exists(app)) CopyDirectory(app, Path.Combine(target, InstallLayout.AppFolder));
            return Path.Combine(target, InstallLayout.Entry);
        }
        catch
        {
            Cleanup(target);
            throw;
        }
    }

    /// <summary>Best effort: a runner directory from an earlier update is not needed any more.</summary>
    public static void CleanupStale(TimeSpan? olderThan = null)
    {
        var threshold = DateTime.UtcNow - (olderThan ?? TimeSpan.FromHours(6));
        var runningFrom = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string[] folders;
        try { folders = Directory.GetDirectories(Path.GetTempPath(), FolderPrefix + "*"); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        foreach (var folder in folders)
        {
            try
            {
                // Never delete the copy the current process is running from.
                if (string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    runningFrom, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.GetCreationTimeUtc(folder) > threshold) continue;
                Cleanup(folder);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Cleanup(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    /// <summary>Starts the staged runner and never waits for it: the caller exits right after.</summary>
    public static Process Start(string runner, string planPath, int waitForProcessId, string installRoot)
    {
        var start = new ProcessStartInfo(runner)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = installRoot
        };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(planPath);
        start.ArgumentList.Add("--wait-pid");
        start.ArgumentList.Add(waitForProcessId.ToString());
        start.ArgumentList.Add("--apply-root");
        start.ArgumentList.Add(installRoot);
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动更新进程");
    }

    /// <summary>Starts the staged copy for journal recovery only; it reopens the app when done.</summary>
    public static bool StartRecovery(string runner, string installRoot, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(runner)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = installRoot
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) is not null;
    }
}
