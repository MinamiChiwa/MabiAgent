using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MabiAgent.Desktop.Controls;
using MabiAgent.Desktop.Editors;

namespace MabiAgent.Desktop;

// Exercises real controls with isolated self-check data; never sends desktop or game input.
internal static class UiLayoutRegression
{
    static readonly Size[] Sizes = [new(860, 600), new(960, 640), new(1000, 640), new(1120, 700), new(1340, 920), new(1920, 1080), new(2560, 1440)];
    static readonly ThemeVariant[] Themes = [ThemeVariant.Dark, ThemeVariant.Light];

    public static async Task<string[]> RunMain(MainWindow window, MainViewModel model)
    {
        var checks = new List<string>();
        var theme = Application.Current!.RequestedThemeVariant;
        var selected = model.SelectedPage;
        var width = window.Width;
        var height = window.Height;
        var taskKind = model.TaskKind;
        var selectedJob = model.SelectedJob;
        var editedQuantity = model.EditedQuantity;
        var runtime = (model.RuntimeTask, model.RuntimeProgress, model.RuntimeHealth, model.RuntimeIssues, model.Error);
        var workbenchJson = model.Workbench?.Document.ToJson().ToJsonString();
        try
        {
            foreach (var variant in Themes)
            {
                Application.Current!.RequestedThemeVariant = variant;
                await Task.Delay(180);
                CheckThemeTokens(variant, checks);
                foreach (var size in MatrixSizes(window))
                {
                    var offscreen = NeedsOffscreen(window, size);
                    if (!offscreen) await ResizeClient(window, size);
                    foreach (var page in Enumerable.Range(0, 3))
                    {
                        model.SelectedPage = page;
                        await Settle(window);
                        if (offscreen) ArrangeOffscreen(window, size); else AssertRequestedSize(window, size);
                        AssertScene(window, $"主界面 {variant} {size.Width}×{size.Height} 页面 {page + 1}{(offscreen ? " 离屏布局" : " 实际客户区")}", checks, offscreen ? size : null);
                        if (offscreen) CheckOffscreenRender(window, size);
                    }
                }
            }
            model.SelectedPage = 0;
            window.Width = 1000;
            window.Height = 640;
            foreach (var kind in model.TaskKinds)
            {
                model.TaskKind = kind;
                model.OpenAddCommand.Execute(null);
                await Settle(window);
                AssertScene(window, $"新增{kind}对话框 最小客户区", checks);
                if (!model.AddDialogOpen) throw new InvalidOperationException("新增任务入口未打开表单");
                model.CloseAddCommand.Execute(null);
                if (model.AddDialogOpen) throw new InvalidOperationException("新增任务取消未关闭表单");
            }
            checks.Add("新增生产、采集、钓鱼表单可打开、可取消且不写入任务队列");
            model.SelectedPage=0;
            await Settle(window);
            var queue=window.GetVisualDescendants().OfType<QueuePanel>().Single();
            if(queue.GetVisualDescendants().OfType<Border>().Any(b=>b.Name=="DetailsCard"))throw new InvalidOperationException("队列仍包含底部编辑卡片");
            if(queue.GetVisualDescendants().OfType<DataGrid>().Any(g=>g.SelectionMode!=DataGridSelectionMode.Extended))throw new InvalidOperationException("任务表格未启用多选");
            checks.Add("任务直接行内编辑，人工与自动列表均支持多选，无底部编辑卡片");
            model.RuntimeTask = "当前任务：" + string.Concat(Enumerable.Repeat("铁矿石加工为铁锭，等待设备空槽与材料确认；", 5));
            model.RuntimeProgress = string.Concat(Enumerable.Repeat("目标与活动批次正在更新，当前已确认数量以实际结果为准。", 4));
            model.RuntimeHealth = "心跳正常，等待下一次任务执行；" + new string('测', 100);
            model.RuntimeIssues = "维修任务等待报价核实\n采购任务等待核账确认\n独立任务继续执行";
            model.Error = "配置校验信息：" + new string('测', 100);
            await Settle(window);
            foreach (var issue in window.GetVisualDescendants().OfType<Expander>().Where(e => Equals(e.Header, "待处理问题"))) issue.IsExpanded = true;
            await Settle(window);
            AssertScene(window, "长中文状态、异常与展开告警 最小客户区", checks);
            (model.RuntimeTask, model.RuntimeProgress, model.RuntimeHealth, model.RuntimeIssues, model.Error) = runtime;
            await CheckRenderScales(window, "主界面", checks);
            if (model.Workbench?.Document.ToJson().ToJsonString() != workbenchJson) throw new InvalidOperationException("布局回归打开配置工作台时改写了任务链配置");
            checks.Add("配置工作台入口不改写工作台配置，布局回归不写任务队列");
            return checks.ToArray();
        }
        finally
        {
            model.CloseAddCommand.Execute(null);
            model.TaskKind = taskKind;
            model.SelectedJob = selectedJob;
            model.EditedQuantity = editedQuantity;
            (model.RuntimeTask, model.RuntimeProgress, model.RuntimeHealth, model.RuntimeIssues, model.Error) = runtime;
            model.SelectedPage = selected;
            window.Width = width;
            window.Height = height;
            window.UpdateResponsiveLayout();
            foreach (var owned in window.OwnedWindows.OfType<AnchorWorkbenchWindow>().ToArray()) owned.CloseWithOwner();
            Application.Current!.RequestedThemeVariant = theme;
            await Settle(window);
        }
    }

    public static async Task<string[]> RunWorkbench(AnchorWorkbenchWindow window, AnchorWorkbenchViewModel model)
    {
        var checks = new List<string>();
        var theme = Application.Current!.RequestedThemeVariant;
        var selected = model.Category;
        var width = window.Width;
        var height = window.Height;
        var initial = model.Document.ToJson().ToJsonString();
        var status = model.Status;
        try
        {
            foreach (var variant in Themes)
            {
                Application.Current!.RequestedThemeVariant = variant;
                await Task.Delay(180);
                foreach (var size in MatrixSizes(window))
                {
                    var offscreen = NeedsOffscreen(window, size);
                    if (!offscreen) await ResizeClient(window, size);
                    foreach (var category in Enumerable.Range(0, 6))
                    {
                        model.Category = category;
                        await Settle(window);
                        if (offscreen) ArrangeOffscreen(window, size); else AssertRequestedSize(window, size);
                        AssertScene(window, $"工作台 {variant} {size.Width}×{size.Height} 分类 {category + 1}{(offscreen ? " 离屏布局" : " 实际客户区")}", checks, offscreen ? size : null);
                        if (offscreen) CheckOffscreenRender(window, size);
                        if (category == 4)
                        {
                            foreach (var tabs in window.GetVisualDescendants().OfType<TabControl>().Where(c => c.IsEffectivelyVisible).ToArray())
                            {
                                var original = tabs.SelectedIndex;
                                for (var tab = 0; tab < tabs.ItemCount; tab++)
                                {
                                    tabs.SelectedIndex = tab;
                                    await Settle(window);
                                    if (offscreen) ArrangeOffscreen(window, size);
                                    AssertScene(window, $"工作台策略 {variant} {size.Width}×{size.Height} 页签 {tab + 1}{(offscreen ? " 离屏布局" : " 实际客户区")}", checks, offscreen ? size : null);
                                }
                                tabs.SelectedIndex = original;
                            }
                        }
                    }
                }
            }
            var choices = AnchorWorkbenchViewModel.Actions.Concat(AnchorWorkbenchViewModel.Conditions)
                .Concat(AnchorWorkbenchViewModel.PageRules).Concat(AnchorWorkbenchViewModel.Modes)
                .Concat(AnchorWorkbenchViewModel.Shapes).Concat(AnchorWorkbenchViewModel.RandomModes);
            if (choices.Any(c => !HasChinese(c.Label))) throw new InvalidOperationException("工作台选项未完整使用中文展示名称");
            checks.Add("工作台操作、条件、页面规则、识别模式、形状与随机模式使用中文展示名称");
            var untranslated = model.Document.Strategies.Where(s => !HasChinese(s.ParameterLabel) || !HasChinese(s.CategoryLabel)).Select(s => s.Name).Take(8).ToArray();
            if (untranslated.Length > 0) throw new InvalidOperationException("策略参数中文展示不完整：" + string.Join("、", untranslated));
            var unreadable = model.Document.Strategies.Where(s => s.ParameterLabel.Contains('\ufffd') || s.ParameterLabel.Contains("自定义 自定义", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(s.ParameterLabel) || s.ParameterLabel == "未设置" || s.ParameterLabel == "自定义").Select(s => s.Name).Take(8).ToArray();
            if (unreadable.Length > 0) throw new InvalidOperationException("策略参数出现乱码或不可辨识名称：" + string.Join("、", unreadable));
            var collisions = model.Document.Strategies.GroupBy(s => (s.Category, s.ParameterLabel)).Where(g => g.Select(s => s.Name).Distinct().Count() > 1 && g.Key.ParameterLabel.Contains("自定义", StringComparison.Ordinal)).Select(g => g.Key.ParameterLabel).Take(8).ToArray();
            if (collisions.Length > 0) throw new InvalidOperationException("未识别参数显示名称发生歧义：" + string.Join("、", collisions));
            checks.Add("全部调度策略参数和分类具有中文展示名称，内部键保持原值");
            model.Category = 0;
            await ResizeClient(window, new Size(Math.Max(760, window.MinWidth), 600));
            model.Status = string.Join("\n", Enumerable.Range(0, 24).Select(i => $"步骤{i + 1}：" + new string('测', 50)));
            await Settle(window);
            AssertScene(window, "工作台长状态与任务步骤 最小实际客户区", checks);
            var statusViewer = window.FindControl<ScrollViewer>("WorkbenchStatusViewer") ?? throw new InvalidOperationException("工作台状态滚动区未连接");
            var steps = window.FindControl<ListBox>("StepList") ?? throw new InvalidOperationException("任务链步骤列表未连接");
            if (statusViewer.Bounds.Height > 64.1 || statusViewer.Extent.Height <= statusViewer.Viewport.Height || steps.Bounds.Height < 36)
                throw new InvalidOperationException($"长状态挤占任务步骤：状态视口{statusViewer.Bounds.Height:0.#}，步骤视口{steps.Bounds.Height:0.#}");
            var priorOffset = statusViewer.Offset;
            statusViewer.ScrollToEnd();
            window.UpdateLayout();
            if (statusViewer.Offset.Y < statusViewer.Extent.Height - statusViewer.Viewport.Height - 1) throw new InvalidOperationException("工作台长状态无法滚动至末尾");
            statusViewer.Offset = priorOffset;
            model.Status = status;
            checks.Add("工作台千字多行状态限制64高度且可滚动，任务步骤保留可用视口");
            model.Category = 2;
            await CheckRenderScales(window, "工作台", checks);
            if (model.Document.ToJson().ToJsonString() != initial) throw new InvalidOperationException("布局回归改变了工作台配置");
            checks.Add("工作台切换分类、页签、主题和窗口尺寸不改写配置");
            return checks.ToArray();
        }
        finally
        {
            model.Category = selected;
            model.Status = status;
            window.Width = width;
            window.Height = height;
            window.UpdateResponsiveLayout();
            Application.Current!.RequestedThemeVariant = theme;
            await Settle(window);
        }
    }

    public static void AssertScene(Window window, string scene, List<string> checks, Size? offscreen = null)
    {
        var failures = new List<string>();
        if (window.Content is not Control root || root.Bounds.Width <= 0 || root.Bounds.Height <= 0)
            throw new InvalidOperationException(scene + "：窗口客户区布局为空");
        failures.AddRange(UiChromeRegression.Inspect(window, offscreen));
        var controls = window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible).ToArray();
        var fields = controls.Where(IsCheckedControl).Where(c => c.Bounds.Width > .1 && c.Bounds.Height > .1).ToArray();
        foreach (var missing in controls.Where(IsCheckedControl).Where(c => c is TextBox or ComboBox or DataGrid or Button).Where(c => c.Bounds.Width <= .1 || c.Bounds.Height <= .1))
            failures.Add(Label(missing) + " 可见组件被压缩为零尺寸");
        foreach (var control in fields)
        {
            var origin = control.TranslatePoint(default, root);
            if (origin is not { } p) continue;
            var ancestors = control.GetVisualAncestors().OfType<Control>().TakeWhile(c => c != root).ToArray();
            var scrolls = ancestors.OfType<ScrollViewer>().ToArray();
            var horizontalScroll = scrolls.Any(s => s.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled && s.Extent.Width > s.Viewport.Width + 1);
            if (!horizontalScroll && (p.X < -1 || p.X + control.Bounds.Width > root.Bounds.Width + 1))
                failures.Add($"{Label(control)} 横向越界 {p.X:0.#}…{p.X + control.Bounds.Width:0.#} / {root.Bounds.Width:0.#}");
            if (scrolls.Length == 0 && (p.Y < -1 || p.Y + control.Bounds.Height > root.Bounds.Height + 1))
                failures.Add($"{Label(control)} 纵向越界 {p.Y:0.#}…{p.Y + control.Bounds.Height:0.#} / {root.Bounds.Height:0.#}");
            if (control is TextBox or ComboBox && (control.Bounds.Width < 28 || control.Bounds.Height < 28))
                failures.Add(Label(control) + " 输入区域过小 " + control.Bounds);
            if (control is DataGrid && (control.Bounds.Width < 120 || control.Bounds.Height < 38))
                failures.Add(Label(control) + " 表格没有可用视口 " + control.Bounds);
            if (control is TextBlock text && text.FontSize < 12 && !string.IsNullOrWhiteSpace(text.Text))
                failures.Add(Label(control) + " 正文或标签字号低于 12");
            if (control is Button button && button.GetType() == typeof(Button) && (button.Bounds.Width < 28 || button.Bounds.Height < 28))
                failures.Add(Label(control) + " 操作区域过小 " + control.Bounds);
            if (!control.UseLayoutRounding) failures.Add(Label(control) + " 未启用像素布局取整");
            if (control is Button or TextBox or ComboBox || control is Border step && step.Classes.Contains("step"))
            {
                var transitions = control.Transitions?.OfType<TransitionBase>().ToArray();
                if (transitions is not { Length: > 0 } || transitions.Any(t => t.Duration <= TimeSpan.Zero || t.Duration > TimeSpan.FromMilliseconds(250)))
                    failures.Add(Label(control) + " 缺少轻量过渡或过渡耗时超出250毫秒");
            }
        }
        foreach (var row in fields.OfType<Button>().GroupBy(b => b.GetVisualParent()).Where(g => g.Key is StackPanel or WrapPanel or DockPanel))
        {
            var buttons = row.ToArray();
            for (var i = 0; i < buttons.Length; i++)
            for (var j = i + 1; j < buttons.Length; j++)
            {
                var overlap = buttons[i].Bounds.Intersect(buttons[j].Bounds);
                if (overlap.Width > 1 && overlap.Height > 1) failures.Add($"{Label(buttons[i])} 与 {Label(buttons[j])} 操作区域重叠");
            }
        }
        foreach (var scroll in controls.OfType<ScrollViewer>().Where(s => s.Viewport.Height > 0 && s.Extent.Height > s.Viewport.Height + 1 && !s.GetVisualAncestors().OfType<ScrollViewer>().Any()))
        {
            var offset = scroll.Offset;
            scroll.ScrollToEnd();
            if (offscreen is { } target) ArrangeOffscreen(window, target); else window.UpdateLayout();
            var bottom = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            if (scroll.Offset.Y < bottom - 1) failures.Add($"{Label(scroll)} 无法滚动到末尾 {scroll.Offset.Y:0.#} / {bottom:0.#}");
            scroll.Offset = offset;
            if (offscreen is { } original) ArrangeOffscreen(window, original); else window.UpdateLayout();
        }
        if (failures.Count > 0) throw new InvalidOperationException(scene + "：" + string.Join("；", failures.Take(18)));
        checks.Add(scene + $"：{fields.Length} 个输入、表格、标题、导航及操作组件边界有效");
    }

    static bool IsCheckedControl(Control control)
    {
        // Cells may be wider than the table viewport and are intentionally reached with its scrollbar.
        if (control.GetVisualAncestors().OfType<DataGrid>().Any()) return false;
        return control is TextBox or ComboBox or DataGrid or HelpButton
            || control is Button button && button.GetType() == typeof(Button)
            || control is ListBox list && list.Classes.Contains("nav")
            || control is Border border && border.Classes.Contains("card")
            || control is TextBlock text && text.Classes.Any(c => c is "title" or "heading" or "field" or "error" or "muted");
    }

    static string Label(Control control) => control.GetType().Name + "/" + (control.Name ?? (control is TextBlock text ? text.Text : control is Button button ? button.Content?.ToString() : ""));
    static IEnumerable<Size> MatrixSizes(Window window) => Sizes.Prepend(new Size(Math.Max(760, window.MinWidth), Math.Max(600, window.MinHeight))).Where(s => s.Width >= window.MinWidth && s.Height >= window.MinHeight).Distinct();
    static void AssertRequestedSize(Window window, Size requested)
    {
        if (Math.Abs(window.ClientSize.Width - requested.Width) > 1 || Math.Abs(window.ClientSize.Height - requested.Height) > 1)
            throw new InvalidOperationException($"请求客户区 {requested.Width}×{requested.Height}，实际客户区 {window.ClientSize.Width}×{window.ClientSize.Height}，布局边界 {window.Bounds.Width}×{window.Bounds.Height}，内容 {(window.Content as Control)?.Bounds}，不能将受约束尺寸记为通过");
    }
    static bool HasChinese(string text) => text.Any(c => c is >= '\u3400' and <= '\u9fff');
    static async Task Settle(Window window) { window.UpdateLayout(); await Task.Delay(20); window.UpdateLayout(); }
    static bool NeedsOffscreen(Window window, Size size)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is null) return false;
        // Reserve native borders/title bar and desktop work-area exclusions, matching
        // WindowSizing. A constrained native window cannot verify the requested client size.
        var availableWidth = Math.Max(0, screen.WorkingArea.Width / screen.Scaling - 24);
        var availableHeight = Math.Max(0, screen.WorkingArea.Height / screen.Scaling - 56);
        return size.Width > availableWidth || size.Height > availableHeight;
    }
    static void ArrangeOffscreen(Window window, Size size)
    {
        if (window.Content is not Control content) throw new InvalidOperationException("离屏布局缺少根容器");
        if (window is MainWindow main) main.UpdateResponsiveLayout(size);
        if (window is AnchorWorkbenchWindow workbench) workbench.UpdateResponsiveLayout(size);
        content.Measure(size);
        content.Arrange(new Rect(default, size));
        if (content.Bounds.Size != size) throw new InvalidOperationException($"离屏请求 {size}，实际内容 {content.Bounds.Size}");
    }
    static void CheckOffscreenRender(Window window, Size size)
    {
        using var image = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
        image.Render((Control)window.Content!);
        if (image.PixelSize.Width != (int)size.Width || image.PixelSize.Height != (int)size.Height) throw new InvalidOperationException("超屏离屏渲染尺寸不符");
    }
    static async Task ResizeClient(Window window, Size size)
    {
        // Ursa's extended title bar participates in Bounds; Width/Height and ClientSize
        // therefore differ. Correct the measured delta instead of allowing a tolerance.
        window.Width = size.Width;
        window.Height = size.Height;
        await Settle(window);
        for (var pass = 0; pass < 3; pass++)
        {
            var dx = size.Width - window.ClientSize.Width;
            var dy = size.Height - window.ClientSize.Height;
            if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1) return;
            window.Width += dx;
            window.Height += dy;
            await Settle(window);
        }
    }

    static void CheckThemeTokens(ThemeVariant variant, List<string> checks)
    {
        Color Resource(string key)
        {
            if (!Application.Current!.TryGetResource(key, variant, out var value) || value is not ISolidColorBrush brush)
                throw new InvalidOperationException($"{variant} 缺少颜色资源 {key}");
            return brush.Color;
        }
        var surface = Resource("WorkspaceSurface");
        foreach (var name in new[] { "Muted", "Success", "Warning", "Info", "Danger" })
        {
            var contrast = Contrast(Resource("Workspace" + name), surface);
            if (contrast < 4.5) throw new InvalidOperationException($"{variant} {name} 文本对比度仅 {contrast:0.00}，低于4.5");
        }
        var surfaces = new List<Color>();
        foreach (var name in new[] { "Runtime", "Queue", "Catalog", "Accounts", "Settings", "Workflow", "Anchor", "Library" })
        {
            var area = Resource("Workspace" + name + "Surface");
            var accent = Resource("Workspace" + name + "Accent");
            if (Contrast(accent, area) < 4.5) throw new InvalidOperationException($"{variant} {name} 强调色对比不足");
            surfaces.Add(area);
        }
        if (surfaces.Distinct().Count() < 6) throw new InvalidOperationException(variant + " 模块颜色缺少层级区分");
        checks.Add(variant + " 辅助/成功/警告/信息/错误与模块强调色对比度≥4.5，模块颜色具备区分");
    }

    static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel) { var c = channel / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Light(Color c) => .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        var a = Light(first);
        var b = Light(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    static async Task CheckRenderScales(Window window, string label, List<string> checks)
    {
        await Settle(window);
        var original = window.Bounds.Size;
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            var size = new PixelSize((int)Math.Ceiling(original.Width * scale), (int)Math.Ceiling(original.Height * scale));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);
            if (bitmap.PixelSize != size || window.Bounds.Size != original) throw new InvalidOperationException(label + " 渲染倍率改变了逻辑布局");
            checks.Add(label + $" {scale * 100:0}% 离屏渲染可完成，逻辑布局保持不变（非系统 DPI 实机测试）");
        }
    }
}
