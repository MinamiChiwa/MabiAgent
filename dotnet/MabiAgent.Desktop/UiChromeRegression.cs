using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MabiAgent.Desktop.Editors;

namespace MabiAgent.Desktop;

internal static class UiChromeRegression
{
    public static async Task<string[]> Run(string root)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mabi-chrome-check-" + Guid.NewGuid().ToString("N"));
        var workspace = new Workspace(root, directory);
        var host = new MainViewModel(workspace, new MabiAgent.Windows.GameWindowSelection(() => []));
        await host.RefreshAsync();
        var repository = new EditorRepository(root, directory);
        host.InitializeEditorSelfCheck(repository, new MabiAgent.Capture.AccountItemService(Path.Combine(directory, "accounts")));
        using var model = new AnchorWorkbenchViewModel(host, repository);
        var main = new MainWindow(host);
        var workbench = new AnchorWorkbenchWindow(model);
        var sponsor = new SponsorWindow();
        using var image = new WriteableBitmap(new PixelSize(200, 120), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        var crop = new CropSelectionWindow(image);
        var checks = new List<string>();
        var failures = new List<string>();
        try
        {
            foreach (var window in new Window[] { main, workbench, sponsor, crop })
            {
                window.Show();
                await Task.Delay(80);
                window.UpdateLayout();
                var issues = Inspect(window);
                if (issues.Count > 0) failures.AddRange(issues.Select(i => window.Title + "：" + i));
                else checks.Add(window.Title + "：标题栏避让、混合操作行布局与视觉中心有效；" +
                    string.Join("，", CaptionRegions(window, null).Select(c => c.Source + "=" + c.Region)));
            }
            if (failures.Count > 0) throw new InvalidOperationException("窗口及混合行回归失败\n" + string.Join("\n", failures.Take(35)));
            return checks.ToArray();
        }
        finally
        {
            crop.Close(); sponsor.Close(); workbench.CloseWithOwner(); main.Close(); host.PrepareClose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    public static List<string> Inspect(Window window, Size? offscreen = null)
    {
        var failures = new List<string>();
        if (window.Content is not Control root) return ["缺少内容根节点"];
        var controls = root.GetVisualDescendants().Prepend(root).OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0).ToArray();
        var actions = controls.Where(IsAction).Where(c => !c.GetVisualAncestors().OfType<DataGrid>().Any()).ToArray();
        var captions = CaptionRegions(window, offscreen).ToArray();
        if (window is Ursa.Controls.UrsaWindow && captions.Length == 0)
            failures.Add("Ursa标题栏控制区未找到，不能把未知系统chrome视作通过；" + DumpTop(window));
        foreach (var action in actions)
        {
            var bounds = InWindow(action, window);
            foreach (var (region, source) in captions)
            {
                var overlap = bounds.Intersect(region);
                if (overlap.Width > .5 && overlap.Height > .5)
                    failures.Add($"标题栏遮挡：{Describe(action)} bounds={bounds} 与 {source}={region} 重叠 {overlap}；contentOrigin={root.TranslatePoint(default, window)}");
            }
        }
        for (var i = 0; i < actions.Length; i++)
        for (var j = i + 1; j < actions.Length; j++)
        {
            var a = actions[i]; var b = actions[j];
            if (a.IsVisualAncestorOf(b) || b.IsVisualAncestorOf(a)) continue;
            var common = a.GetVisualAncestors().OfType<Panel>().FirstOrDefault(p => p.IsVisualAncestorOf(b));
            if (common is null || !IsHorizontalRow(common, a, b)) continue;
            var ra = InWindow(a, window); var rb = InWindow(b, window);
            if (ra.Right > rb.Left + .5 && rb.Right > ra.Left + .5) continue;
            var overlap = Math.Min(ra.Bottom, rb.Bottom) - Math.Max(ra.Top, rb.Top);
            if (overlap < Math.Min(ra.Height, rb.Height) * .5) continue;
            var centerDelta = Math.Abs(ra.Center.Y - rb.Center.Y);
            var va = VisualContent(a, window); var vb = VisualContent(b, window);
            var visualDelta = Math.Abs(va.Center.Y - vb.Center.Y);
            if (centerDelta > 1.1 || visualDelta > 2)
                failures.Add($"同行错位：{Describe(a)} layout={ra} visual={va}；{Describe(b)} layout={rb} visual={vb}；布局中心差={centerDelta:0.##}，视觉中心差={visualDelta:0.##}，行={common.GetType().Name}/{common.Name}");
        }
        return failures;
    }

    static bool IsAction(Control c) => c is CheckBox or TextBox or ComboBox || c is Button b && (b.GetType() == typeof(Button) || b is Controls.HelpButton);
    static bool IsHorizontalRow(Panel panel, Control a, Control b)
    {
        if (panel is StackPanel stack) return stack.Orientation == Orientation.Horizontal;
        if (panel is WrapPanel wrap) return wrap.Orientation == Orientation.Horizontal;
        if (panel is Grid grid)
        {
            // A page's side-by-side columns are independent forms, not one toolbar.
            // Only cross columns through inline wrappers; never jump across a vertical
            // field stack, scrolling pane or multirow subgrid to infer a semantic row.
            bool Inline(Control child) => !child.GetVisualAncestors().OfType<Control>().TakeWhile(c => c != grid)
                .Any(c => c is StackPanel { Orientation: Orientation.Vertical } or DockPanel or ScrollViewer or ListBox
                    || c is Grid nested && nested.RowDefinitions.Count > 1);
            if (!Inline(a) || !Inline(b)) return false;
            Control Branch(Control child) => child.GetVisualAncestors().OfType<Control>().TakeWhile(c => c != grid).LastOrDefault() ?? child;
            var aa = Branch(a); var bb = Branch(b);
            return Grid.GetRow(aa) == Grid.GetRow(bb) && Grid.GetColumn(aa) != Grid.GetColumn(bb) && Grid.GetRowSpan(aa) == 1 && Grid.GetRowSpan(bb) == 1;
        }
        return panel is DockPanel && a.GetVisualParent() == panel && b.GetVisualParent() == panel;
    }
    static Rect InWindow(Control control, Window window)
    { var point = control.TranslatePoint(default, window) ?? default; return new Rect(point, control.Bounds.Size); }
    static Rect VisualContent(Control control, Window window)
    {
        var texts = control.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text) && t.Bounds.Width > 0 && t.Bounds.Height > 0).ToArray();
        if (texts.Length > 0) return texts.Select(t => InWindow(t, window)).Aggregate((a, b) => a.Union(b));
        var presenter = control.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(p => p.IsEffectivelyVisible && p.Bounds.Height > 0);
        return presenter is null ? InWindow(control, window) : InWindow(presenter, window);
    }
    static IEnumerable<(Rect Region, string Source)> CaptionRegions(Window window, Size? offscreen)
    {
        var root = window.Content as Control;
        var regions = window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0
                && c.Name is "PART_MinimizeButton" or "PART_MaximizeButton" or "PART_CloseButton" or "PART_OverlayPanel")
            .Where(c => root is null || !root.IsVisualAncestorOf(c)).Select(c => (InWindow(c, window), "模板" + c.Name)).ToArray();
        foreach (var (rect, name) in regions)
        {
            var adjusted = offscreen is { } size ? rect.Translate(new Vector(size.Width - window.Bounds.Width, 0)) : rect;
            yield return (adjusted, name);
        }
        if (regions.Length > 0) yield break;
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || !OperatingSystem.IsWindows()) yield break;
        if (DwmGetWindowAttribute(handle, 5, out var caption, Marshal.SizeOf<NativeRect>()) != 0 || caption.Right <= caption.Left || caption.Bottom <= caption.Top || !GetWindowRect(handle, out var frame)) yield break;
        var start = window.PointToClient(new PixelPoint(frame.Left + caption.Left, frame.Top + caption.Top));
        var end = window.PointToClient(new PixelPoint(frame.Left + caption.Right, frame.Top + caption.Bottom));
        var native = new Rect(start, end);
        if (offscreen is { } target) native = native.Translate(new Vector(target.Width - window.Bounds.Width, 0));
        yield return (native, "原生DWM_CAPTION_BUTTON_BOUNDS");
    }
    static string Describe(Control c)
    {
        var text = (c as ContentControl)?.Content as string;
        text ??= string.Join("", c.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
        if (text.Length == 0) text = (c as TextBox)?.PlaceholderText ?? "";
        return c.GetType().Name + "/" + (c.Name ?? text);
    }
    static string DumpTop(Window window) => string.Join(" | ", window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Height > 0 && InWindow(c, window).Top < 70).Take(35).Select(c => Describe(c) + " " + InWindow(c, window)));
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect rectangle, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
}
