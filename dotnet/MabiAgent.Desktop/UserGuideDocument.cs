namespace MabiAgent.Desktop;

public static class UserGuideDocument
{
    public static string Read(string culture, string document = "guide")
    {
        var resource = document switch
        {
            "development" => "Guide.Development.md",
            "license" => "Guide.License.md",
            _ => culture == UiLanguageService.TraditionalChinese ? "Guide.zh-TW.md" : "Guide.zh-CN.md"
        };
        using var stream = typeof(UserGuideDocument).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"缺少内置文档：{resource}");
        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static string? RelativeDocument(string link) => link.Split('#')[0].TrimStart('.', '/') switch
    {
        "README.md" or "用户指南.md" => "zh-CN",
        "README.zh-TW.md" or "使用指南.zh-TW.md" => "zh-TW",
        "docs/DEVELOPMENT.md" or "DEVELOPMENT.md" or "开发指南.md" => "development",
        "LICENSE" or "LICENSE.md" => "license",
        _ => null
    };
}
