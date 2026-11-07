using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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
        var initialSettings=window.FindControl<SettingsPanel>("SettingsPage")!;var operationSection=initialSettings.FindControl<DeferredSettingsSection>("OperationSettingsSection")!;
        for(var attempt=0;attempt<50&&!operationSection.IsBuilt;attempt++){operationSection.BringIntoView();window.UpdateLayout();await Task.Delay(20);}
        var hotkey=operationSection.GetVisualDescendants().OfType<HotkeyRecorder>().Single(b=>b.Name=="GlobalStopHotkeyInput");
        var originalTheme=window.RequestedThemeVariant;
        var applyHotkey=operationSection.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="GlobalStopHotkeyApply");
        Check(hotkey.IsEffectivelyVisible&&hotkey.Bounds.Width>80&&hotkey.Text==model.StopHotkeyText&&applyHotkey.Command==model.ApplyStopHotkeyCommand,"全局停止快捷键输入与应用入口绑定可用");
        foreach(var theme in new[]{ThemeVariant.Dark,ThemeVariant.Light})
        {
            window.RequestedThemeVariant=theme;window.UpdateLayout();await Task.Delay(220);
            var presenter=hotkey.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
            Check(hotkey.Template is not null&&presenter is {IsEffectivelyVisible:true}&&presenter.Bounds.Width>0&&presenter.Bounds.Height>0&&presenter.Text==model.StopHotkeyText,"快捷键录入框"+theme+"主题模板及可见文字完整");
            if(Environment.GetEnvironmentVariable("MABI_HOTKEY_PREVIEW_DIR") is{} previewPath)
            {
                Directory.CreateDirectory(previewPath);
                using var image=new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)operationSection.Bounds.Width,(int)operationSection.Bounds.Height));
                image.Render(operationSection);image.Save(Path.Combine(previewPath,"hotkey-"+theme+".png"),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        window.RequestedThemeVariant=originalTheme;
        var recorded=new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.F11,KeyModifiers=KeyModifiers.Control|KeyModifiers.Shift};
        var originalHotkey=model.StopHotkeyText;hotkey.BeginRecording();hotkey.RaiseEvent(recorded);
        Check(hotkey.IsReadOnly&&recorded.Handled&&hotkey.Text=="Ctrl+Shift+F11"&&model.StopHotkeyText==hotkey.Text,"快捷键直接按键录入并同步设置草稿");
        var draft=hotkey.Text;hotkey.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.F9});
        Check(hotkey.Text==draft&&!hotkey.IsRecording,"未点击录入时普通按键不修改快捷键");
        var recordButton=operationSection.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="GlobalStopHotkeyRecord");
        typeof(Button).GetMethod("OnClick",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(recordButton,null);
        window.UpdateLayout();Check(hotkey.IsRecording&&!applyHotkey.IsEnabled,"录入按钮进入捕获状态并阻止提前应用");
        hotkey.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.F12});
        Check(hotkey.Text=="F12"&&model.StopHotkeyText=="F12"&&!hotkey.IsRecording,"单键F12可录入且不要求修饰键");
        hotkey.BeginRecording();
        var mouse=new Pointer(44,PointerType.Mouse,true);
        var click=new PointerPressedEventArgs(hotkey,mouse,window,default,0,new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.XButton2Pressed),KeyModifiers.Control,1);
        hotkey.RaiseEvent(click);
        Check(click.Handled&&hotkey.Text=="Ctrl+MouseX2"&&model.StopHotkeyText==hotkey.Text,"鼠标侧键与修饰键可录入并同步草稿");
        model.StopHotkeyText=originalHotkey;
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
        var helpDeadline=System.Diagnostics.Stopwatch.StartNew();
        while(ToolTip.GetIsOpen(help)&&helpDeadline.Elapsed<HelpButton.MaximumOpenTime+TimeSpan.FromSeconds(2))await Task.Delay(60);
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
        window.Activate();
        for(var i=0;i<20&&!window.IsActive;i++)await Task.Delay(50);
        ToolTip.SetIsOpen(help,true);
        var deactivateProbe=new Window{Width=180,Height=100,ShowInTaskbar=false};deactivateProbe.Show(window);deactivateProbe.Activate();
        for(var i=0;i<30&&ToolTip.GetIsOpen(help);i++)await Task.Delay(50);
        Check(!ToolTip.GetIsOpen(help),"窗口失活关闭帮助");deactivateProbe.Close();window.Activate();
        return passed.ToArray();
    }
    public static async Task<string[]> Run(MainWindow window,MainViewModel model)
    {
        var passed=new List<string>();
        void Check(bool ok,string text){if(!ok)throw new InvalidOperationException(text);passed.Add(text);}
        model.SelectedPage=2;window.Show();await Task.Delay(100);window.UpdateLayout();
        var initialSettings=window.FindControl<SettingsPanel>("SettingsPage")!;var operationSection=initialSettings.FindControl<DeferredSettingsSection>("OperationSettingsSection")!;
        for(var attempt=0;attempt<50&&!operationSection.IsBuilt;attempt++){operationSection.BringIntoView();window.UpdateLayout();await Task.Delay(20);}
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
            foreach(var selected in Enumerable.Range(0,3))
            {
                window.Width=1000;window.Height=700;model.SelectedPage=selected;window.UpdateLayout();
                var visibleButtons=window.GetVisualDescendants().OfType<Button>().Where(b=>b.IsEffectivelyVisible&&b.Bounds.Width>0&&(b.GetType()==typeof(Button)||b is HelpButton)&&!b.GetVisualAncestors().OfType<ComboBox>().Any()).ToArray();
                Check(visibleButtons.All(b=>b.CornerRadius.TopLeft>=b.Bounds.Height/2&&Aligned(b)),$"{theme} 页面 {selected+1} 圆角与图文对齐一致");
                Check(visibleButtons.Where(b=>UiIcon.GetKind(b) is not null).All(b=>b.Content is StackPanel p&&p.Children.OfType<PathIcon>().Any()),$"{theme} 页面 {selected+1} 功能图标完整");
                Check(visibleButtons.All(b=>b.Bounds.Height>=28&&b.Bounds.Width>=28),$"{theme} 最小窗口页面 {selected+1} 操作尺寸有效");
            }
        }
        passed.AddRange(await UiLayoutRegression.RunMain(window,model));
        passed.AddRange(await CompactUiSelfCheck.Run(window,model,Environment.GetEnvironmentVariable("MABI_MINI_PREVIEW")));
        model.SelectedPage=2;
        foreach(var expander in window.GetVisualDescendants().OfType<Expander>().ToArray())expander.IsExpanded=true;
        window.UpdateLayout();
        foreach(var viewer in window.GetVisualDescendants().OfType<ScrollViewer>().ToArray())viewer.ScrollToEnd();
        window.UpdateLayout();
        var advanced=window.GetVisualDescendants().OfType<Button>().Where(b=>b.IsEffectivelyVisible&&b.GetType()==typeof(Button)&&!b.GetVisualAncestors().OfType<ComboBox>().Any()).ToArray();
        Check(advanced.All(b=>b.Bounds.Width>=28&&b.Bounds.Height>=28&&b.CornerRadius.TopLeft>=b.Bounds.Height/2),"设置长内容操作可滚动访问且圆角一致");
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
