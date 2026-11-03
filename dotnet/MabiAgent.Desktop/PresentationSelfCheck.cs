using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MabiAgent.Desktop.Controls;
namespace MabiAgent.Desktop;
internal static class PresentationSelfCheck
{
    internal static async Task<string[]> RunHelp(MainWindow window,MainViewModel model)
    {
        var passed=new List<string>();
        void Check(bool ok,string text){if(!ok)throw new InvalidOperationException(text);passed.Add(text);}
        model.SelectedPage=2;window.Show();await Task.Delay(100);window.UpdateLayout();
        var buttons=window.GetVisualDescendants().OfType<Button>().Where(b=>b.IsEffectivelyVisible&&b.Bounds.Width>0&&b.Focusable).ToArray();
        var help=window.GetVisualDescendants().OfType<HelpButton>().First(b=>b.IsEffectivelyVisible&&b.GetVisualAncestors().OfType<SettingsPanel>().Any());
        Check(!string.IsNullOrWhiteSpace(help.Message)&&Equals(ToolTip.GetTip(help),help.Message),"问号与帮助文案关联");
        help.Focus(NavigationMethod.Tab);await Task.Delay(40);Check(ToolTip.GetIsOpen(help),"帮助可由键盘聚焦打开");
        buttons.First(b=>b is not HelpButton&&b.Focusable).Focus();Check(!ToolTip.GetIsOpen(help),"帮助在焦点离开后关闭");
        Check(window.GetVisualDescendants().OfType<Border>().Where(b=>b.Classes.Contains("card")).All(b=>ToolTip.GetTip(b) is null),"整块功能区域不弹出悬浮说明");
        help.Focus(NavigationMethod.Tab);window.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.Escape});
        Check(!ToolTip.GetIsOpen(help),"窗口内Esc关闭帮助");
        var pageHelp=window.GetVisualDescendants().OfType<HelpButton>().Single(b=>b.Name=="PageHelp");
        ToolTip.SetIsOpen(pageHelp,true);ToolTip.SetIsOpen(help,true);model.SelectedPage=0;window.UpdateLayout();
        Check(!ToolTip.GetIsOpen(help),"切换页面关闭隐藏区域的帮助");
        Check(!ToolTip.GetIsOpen(pageHelp),"页面说明变更时关闭旧提示");
        model.SelectedPage=2;window.UpdateLayout();ToolTip.SetIsOpen(help,true);
        await Task.Delay(HelpButton.MaximumOpenTime+TimeSpan.FromMilliseconds(150));
        Check(!ToolTip.GetIsOpen(help),"问号说明六秒内自动关闭");
        typeof(HelpButton).GetMethod("OnClick",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(help,null);
        Check(ToolTip.GetIsOpen(help),"点击问号可查看说明");
        typeof(HelpButton).GetMethod("OnClick",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(help,null);
        Check(!ToolTip.GetIsOpen(help),"再次点击问号关闭说明");
        var pointer=new Pointer(33,PointerType.Mouse,true);
        ToolTip.SetIsOpen(help,true);
        help.RaiseEvent(new PointerEventArgs(InputElement.PointerExitedEvent,help,pointer,window,default,0,default,KeyModifiers.None));
        Check(!ToolTip.GetIsOpen(help),"移开指针立即关闭帮助");
        ToolTip.SetIsOpen(help,true);
        var other=buttons.First(b=>b is not HelpButton&&b.Focusable);
        other.RaiseEvent(new PointerPressedEventArgs(other,pointer,window,default,0,default,KeyModifiers.None,1));
        Check(!ToolTip.GetIsOpen(help),"点击其他控件关闭帮助且不吞操作");
        ToolTip.SetIsOpen(help,true);
        var deactivateProbe=new Window{Width=180,Height=100,ShowInTaskbar=false};deactivateProbe.Show(window);deactivateProbe.Activate();await Task.Delay(150);
        Check(!ToolTip.GetIsOpen(help),"窗口失活关闭帮助");deactivateProbe.Close();window.Activate();
        return passed.ToArray();
    }
    public static async Task<string[]> Run(MainWindow window,MainViewModel model)
    {
        var passed=new List<string>();
        void Check(bool ok,string text){if(!ok)throw new InvalidOperationException(text);passed.Add(text);}
        model.SelectedPage=2;window.Show();await Task.Delay(100);window.UpdateLayout();
        var buttons=window.GetVisualDescendants().OfType<Button>().Where(x=>x.IsEffectivelyVisible&&x.Bounds.Width>0&&(x.GetType()==typeof(Button)||x is HelpButton)&&!x.GetVisualAncestors().OfType<ComboBox>().Any()).ToArray();
        Check(buttons.Length>8&&buttons.All(b=>b.CornerRadius.TopLeft>=b.Bounds.Height/2),"所有可见操作按钮使用半高圆角"+string.Join("; ",buttons.Where(b=>b.CornerRadius.TopLeft<b.Bounds.Height/2).Select(b=>$"{b.GetType().Name}/{b.Name}/{b.Content} {b.Bounds} r={b.CornerRadius}")));
        // Sidebar links (Button.side) align left with the navigation items; every other button centers its content.
        static bool Aligned(Button b)=>(b.Classes.Contains("side")||b.HorizontalContentAlignment==Avalonia.Layout.HorizontalAlignment.Center)&&b.VerticalContentAlignment==Avalonia.Layout.VerticalAlignment.Center;
        Check(buttons.All(Aligned),"按钮图文居中对齐（侧栏链接左对齐）");
        passed.AddRange(await RunHelp(window,model));
        Check(window.GetVisualDescendants().OfType<PathIcon>().Any(p=>p.IsEffectivelyVisible&&p.Data is not null),"功能图标使用矢量资源");
        foreach(var theme in new[]{ThemeVariant.Dark,ThemeVariant.Light})
        {
            Application.Current!.RequestedThemeVariant=theme;window.UpdateLayout();
            var surfaceControl=window.GetVisualDescendants().OfType<Border>().First(b=>b.Classes.Contains("card")&&b.IsEffectivelyVisible);
            var section=surfaceControl.Classes.FirstOrDefault(c=>c.StartsWith("section-",StringComparison.Ordinal));
            var resource=section is null?"WorkspaceSurface":"Workspace"+char.ToUpperInvariant(section[8])+section[9..]+"Surface";
            Check(Application.Current!.TryGetResource(resource,theme,out var expectedSurface)&&expectedSurface is ISolidColorBrush,$"{theme} 功能区主题资源可解析：{resource}");
            Check(Application.Current!.TryGetResource("WorkspacePage",theme,out var expectedPage)&&expectedPage is ISolidColorBrush,$"{theme} 页面主题资源可解析");
            var targetSurface=((ISolidColorBrush)expectedSurface!).Color;var targetPage=((ISolidColorBrush)expectedPage!).Color;
            var deadline=System.Diagnostics.Stopwatch.StartNew();
            while(deadline.Elapsed<TimeSpan.FromSeconds(1)&&
                (surfaceControl.Background is not ISolidColorBrush area||area.Color!=targetSurface||window.Background is not ISolidColorBrush background||background.Color!=targetPage))
            {await Task.Delay(20);window.UpdateLayout();}
            var page=window.Background;var surface=surfaceControl.Background;
            Check(surface is ISolidColorBrush settledSurface&&settledSurface.Color==targetSurface&&page is ISolidColorBrush settledPage&&settledPage.Color==targetPage,
                $"{theme} 主题颜色在1秒内收敛：实际页面={page}、功能区={surface}；预期页面={targetPage}、{resource}={targetSurface}");
            static double Luma(IBrush? brush)=>brush is ISolidColorBrush b?.2126*b.Color.R+.7152*b.Color.G+.0722*b.Color.B:0;
            Check(page is IBrush&&surface is IBrush&&Luma(surface)>Luma(page),$"{theme} 功能区比页面略浅：页面={page}、功能区={surface}");
            foreach(var selected in Enumerable.Range(0,6))
            {
                window.Width=1000;window.Height=700;model.SelectedPage=selected;window.UpdateLayout();
                var visibleButtons=window.GetVisualDescendants().OfType<Button>().Where(b=>b.IsEffectivelyVisible&&b.Bounds.Width>0&&(b.GetType()==typeof(Button)||b is HelpButton)&&!b.GetVisualAncestors().OfType<ComboBox>().Any()).ToArray();
                Check(visibleButtons.All(b=>b.CornerRadius.TopLeft>=b.Bounds.Height/2&&Aligned(b)),$"{theme} 页面 {selected+1} 圆角与图文对齐一致");
                Check(visibleButtons.Where(b=>UiIcon.GetKind(b) is not null).All(b=>b.Content is StackPanel p&&p.Children.OfType<PathIcon>().Any()),$"{theme} 页面 {selected+1} 功能图标完整");
                Check(visibleButtons.All(b=>b.Bounds.Height>=28&&b.Bounds.Width>=28),$"{theme} 最小窗口页面 {selected+1} 操作尺寸有效");
            }
        }
        passed.AddRange(await UiLayoutRegression.RunMain(window,model));
        model.SelectedPage=4;
        foreach(var expander in window.GetVisualDescendants().OfType<Expander>().ToArray())expander.IsExpanded=true;
        window.UpdateLayout();
        foreach(var viewer in window.GetVisualDescendants().OfType<ScrollViewer>().ToArray())viewer.ScrollToEnd();
        window.UpdateLayout();
        var advanced=window.GetVisualDescendants().OfType<Button>().Where(b=>b.IsEffectivelyVisible&&b.GetType()==typeof(Button)&&!b.GetVisualAncestors().OfType<ComboBox>().Any()).ToArray();
        Check(advanced.All(b=>b.Bounds.Width>=28&&b.Bounds.Height>=28&&b.CornerRadius.TopLeft>=b.Bounds.Height/2),"高级编辑操作可滚动访问且圆角一致");
        var sponsor=new SponsorWindow();sponsor.Show(window);sponsor.UpdateLayout();
        UiLayoutRegression.AssertScene(sponsor,"赞助窗口",passed);
        var sponsorButtons=sponsor.GetVisualDescendants().OfType<Button>().Where(b=>b.GetType()==typeof(Button)&&b.IsEffectivelyVisible).ToArray();
        Check(sponsorButtons.Length>=3&&sponsorButtons.All(b=>b.CornerRadius.TopLeft>=b.Bounds.Height/2),"赞助窗口操作沿用统一圆角");sponsor.Close();
        using(var picture=new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(200,120),new Vector(96,96),Avalonia.Platform.PixelFormat.Bgra8888,Avalonia.Platform.AlphaFormat.Opaque))
        {
            var crop=new Editors.CropSelectionWindow(picture);crop.Show(window);crop.UpdateLayout();
            UiLayoutRegression.AssertScene(crop,"图片框选窗口",passed);
            var cropButtons=crop.GetVisualDescendants().OfType<Button>().Where(b=>b.GetType()==typeof(Button)&&b.Content is string).ToArray();
            Check(cropButtons.Any(b=>Equals(b.Content,"使用区域")&&!b.IsEnabled)&&cropButtons.All(b=>b.CornerRadius.TopLeft>=b.Bounds.Height/2),"框选先选择区域再确认且样式一致");crop.Close();
        }
        window.Close();return passed.ToArray();
    }
}
