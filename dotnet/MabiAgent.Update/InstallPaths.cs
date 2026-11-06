namespace MabiAgent.Update;

/// <summary>
/// Update targets come from a downloaded manifest, so they are untrusted input:
/// no absolute paths, no traversal, no reserved device names, nothing under
/// <c>data/</c>, and no writing through a symlink or reparse point. Anything that cannot be
/// verified — including an unreadable attribute on the install root — is rejected rather
/// than allowed: a fail-open check here would let the manifest choose the destination.
/// </summary>
public static class InstallPaths
{
    public const string ProtectedPrefix = "data";
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };
    private static readonly char[] Invalid = ['<', '>', ':', '"', '|', '?', '*'];

    public static bool IsSafeRelative(string path, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(path)) { reason = "路径为空"; return false; }
        if (path.Length > 240) { reason = "路径过长"; return false; }
        if (path.StartsWith('/') || path.StartsWith('\\')) { reason = "不允许绝对路径"; return false; }
        if (path.Contains(':')) { reason = "不允许盘符或流记法"; return false; }
        if (Path.IsPathRooted(path)) { reason = "不允许绝对路径"; return false; }
        var parts = path.Replace('\\', '/').Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) { reason = "路径含空段"; return false; }
            if (part is "." or "..") { reason = "不允许目录穿越"; return false; }
            if (part.EndsWith('.') || part.EndsWith(' ')) { reason = "路径段以点或空格结尾"; return false; }
            if (part.Any(char.IsControl)) { reason = "路径含控制字符"; return false; }
            if (part.IndexOfAny(Invalid) >= 0) { reason = "路径含非法字符"; return false; }
            var stem = part.Split('.')[0];
            if (Reserved.Contains(stem)) { reason = "路径使用系统保留名"; return false; }
        }
        if (string.Equals(parts[0], ProtectedPrefix, StringComparison.OrdinalIgnoreCase)) { reason = "不允许更新用户数据目录"; return false; }
        return true;
    }

    public static bool TryResolve(string root, string relative, out string full, out string reason)
    {
        full = "";
        if (!IsSafeRelative(relative, out reason)) return false;
        string basePath;
        try { basePath = Path.GetFullPath(root); }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException)
        { reason = "安装目录无效：" + error.Message; return false; }
        // The install root itself must not be a link: everything below would follow it.
        if (!TryReadAttributes(basePath, out var rootAttributes) || (rootAttributes & FileAttributes.ReparsePoint) != 0)
        { reason = "安装目录是符号链接、重解析点或无法确认"; return false; }
        var candidate = Path.GetFullPath(Path.Combine(basePath, relative.Replace('\\', '/')));
        var prefix = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { reason = "路径落在安装目录之外"; return false; }
        var leaves = candidate[prefix.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = basePath;
        for (var i = 0; i < leaves.Length; i++)
        {
            current = Path.Combine(current, leaves[i]);
            if (!TryReadAttributes(current, out var attributes)) { reason = "无法确认路径属性，拒绝写入：" + leaves[i]; return false; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) { reason = "目标路径包含符号链接或重解析点：" + leaves[i]; return false; }
        }
        full = candidate;
        return true;
    }

    /// <summary>
    /// True when the attributes could be read; a missing path counts as "no link" because it
    /// cannot redirect anything yet. Any IO or permission failure is "cannot verify".
    /// </summary>
    private static bool TryReadAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = 0;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            attributes = 0;
            return false;
        }
    }
}
