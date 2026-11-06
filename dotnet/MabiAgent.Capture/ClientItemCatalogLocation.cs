namespace MabiAgent.Capture;

public static class ClientItemCatalogLocation
{
    public static string Resolve(string executable, string? fallbackDirectory = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable));
        for (var depth = 0; depth < 4 && directory is not null; depth++, directory = Path.GetDirectoryName(directory))
            if (File.Exists(Path.Combine(directory, "ResourcesPackage", "Starter", "index"))) return directory;
        if (!string.IsNullOrWhiteSpace(fallbackDirectory))
        {
            var fallback = Path.GetFullPath(fallbackDirectory);
            if (File.Exists(Path.Combine(fallback, "ResourcesPackage", "Starter", "index"))) return fallback;
        }
        throw new DirectoryNotFoundException("未找到游戏物品资源，请在设置的数据同步中填写游戏程序所在目录后重试");
    }
}
