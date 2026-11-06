using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace MabiAgent.Desktop;

internal static class GuideUiSelfCheck
{
    public static async Task<string[]> Run(MainWindow window, MainViewModel model, string? previews)
    {
        var checks = new List<string>();
        var language = UiLanguageService.Shared;
        var original = language.Culture;
        var theme = Application.Current!.RequestedThemeVariant;
        try
        {
            window.Show();
            await Settle(window);
            Check(!window.TitleUpdateButton.IsVisible, "未检查时更新入口隐藏");
            model.UpdateAvailable = true;
            await Settle(window);
            Check(window.TitleUpdateButton.IsVisible, "发现新版本显示更新入口");
            model.UpdateBusy = true;
            Check(window.TitleUpdateButton.IsVisible, "下载期间保留更新入口");
            model.UpdateBusy = false;
            model.UpdateAvailable = false;
            model.UpdateStatus = "已是最新版本";
            await Settle(window);
            Check(!window.TitleUpdateButton.IsVisible, "无新版本隐藏更新入口");
            model.UpdateStatus = "检查更新失败";
            Check(!window.TitleUpdateButton.IsVisible, "检查失败隐藏更新入口");
            checks.Add("顶栏更新入口覆盖未检查、新版本、下载、无更新与失败状态");
            var icon = window.LanguageButton.GetVisualDescendants().OfType<PathIcon>().Single();
            Check(icon.Data is not null && window.TryFindResource("SemiIconLanguage", out var expected) && Equals(icon.Data, expected), "语言入口使用Semi官方图标");
            checks.Add("Semi语言图标资源正确加载");

            model.ChangeLanguage("zh-CN");
            window.ShowUserGuide();
            var guide = window.Guide!;
            await Settle(guide);
            window.ShowUserGuide();
            Check(ReferenceEquals(guide, window.Guide), "重复打开复用指南窗口");
            Check(guide.Document.Markdown!.Contains("## 安装"), "简体指南");
            Check(guide.Document.GetVisualDescendants().Any(x => x is Control c && c.Classes.Contains("markdown-table")), "Markdown表格渲染");
            Check(guide.Document.GetVisualDescendants().Any(x => x is Control c && c.Classes.Contains("markdown-h1")), "Markdown标题渲染");
            var scroll = (ScrollViewer)guide.Document.Content!;
            Check(scroll.Extent.Height > scroll.Viewport.Height, "指南可滚动");
            scroll.Offset = new Vector(0, (scroll.Extent.Height - scroll.Viewport.Height) * .45);
            model.UseTraditionalChineseCommand.Execute(null);
            await Settle(guide);
            RuntimeLocalization.RefreshAll();
            Check(guide.Document.Markdown!.Contains("## 安裝"), "繁体指南");
            var translated = (ScrollViewer)guide.Document.Content!;
            var fraction = translated.Offset.Y / (translated.Extent.Height - translated.Viewport.Height);
            Check(Math.Abs(fraction - .45) < .03, $"语言切换保留阅读进度：{fraction}");
            guide.Navigate("README.md");
            await Settle(guide);
            Check(language.Culture == "zh-CN" && guide.Document.Markdown!.Contains("## 安装"), "文档内语言链接");
            guide.Navigate("docs/DEVELOPMENT.md");
            await Settle(guide);
            Check(guide.Document.Markdown == UserGuideDocument.Read("zh-CN", "development"), "开发文档内部打开");
            guide.Navigate("LICENSE");
            await Settle(guide);
            Check(guide.Document.Markdown!.Contains("PolyForm"), "许可内部打开");
            guide.Navigate("README.md");
            await Settle(guide);
            guide.Navigate("#mabiagent");
            checks.Add("真实指南窗口：标题、表格、滚动、简繁切换保留阅读位置、内部文档与章节链接");

            RuntimeLocalization.RenderOriginalDocument(() => guide.Document.Markdown = "# Markdown\n\n**粗体**、*斜体*、`code`\n\n- 列表\n\n```text\nhello\n```\n\n[链接](https://example.org)\n");
            await Settle(guide);
            Check(guide.Document.GetVisualDescendants().OfType<Control>().Any(x => x.Classes.Contains("markdown-code-block")), "代码块渲染");
            Check(guide.Document.GetVisualDescendants().OfType<Control>().Any(x => x.Classes.Contains("markdown-list")), "列表渲染");
            checks.Add("Markdown列表与代码块真实渲染");
            guide.Navigate("README.md");
            if (previews is not null) System.IO.Directory.CreateDirectory(previews);
            foreach (var selectedTheme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var culture in new[] { "zh-CN", "zh-TW" })
            {
                Application.Current.RequestedThemeVariant = selectedTheme;
                model.ChangeLanguage(culture);
                guide.Width = culture == "zh-TW" ? 560 : 940;
                guide.Height = 720;
                await Settle(guide);
                Check(guide.Document.Bounds.Width > 300 && guide.Document.Bounds.Height > 300, "窄窗指南布局");
                Check(guide.GuideLanguageButton.Bounds.Width > 40, "语言按钮布局");
                Check(guide.ActualThemeVariant == selectedTheme, "指南跟随主题");
                if (previews is not null)
                {
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)guide.Width, (int)guide.Height));
                    bitmap.Render(guide);
                    bitmap.Save(System.IO.Path.Combine(previews, $"guide-{selectedTheme}-{culture}.png"), PngBitmapEncoderOptions.Default);
                }
            }
            checks.Add("指南简繁、亮暗与窄窗布局及截图");
            guide.Close();
            Check(window.Guide is null, "指南关闭释放引用");
            model.ChangeLanguage("zh-CN");
            window.ShowUserGuide();
            var reopened = window.Guide!;
            await Settle(reopened);
            Check(!ReferenceEquals(guide, reopened), "关闭后可重新打开");
            window.Close();
            Check(!reopened.IsVisible && window.Guide is null, "主窗关闭释放指南");
            checks.Add("重复打开、关闭重开及主窗口关闭释放");
            return checks.ToArray();
        }
        finally
        {
            language.SetLanguage(original);
            Application.Current.RequestedThemeVariant = theme;
            window.Guide?.Close();
        }

        static void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); }
        static async Task Settle(Window target) { await Task.Delay(120); target.UpdateLayout(); }
    }
}
