using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Ursa.Controls;
namespace MabiAgent.Desktop;
// Compact window checks: switching, sizing, command wiring and both themes. Never sends desktop or game input.
internal static class CompactUiSelfCheck
{
    public static async Task<string[]> Run(MainWindow window,MainViewModel model,string? previewDirectory=null)
    {
        var passed=new List<string>();
        void Check(bool ok,string text){if(!ok)throw new InvalidOperationException(text);passed.Add(text);}
        var theme=Application.Current!.RequestedThemeVariant;
        var issues=model.RuntimeIssues;
        try
        {
            model.EnterMiniCommand.Execute(null);await Settle(window);
            var mini=window.Mini;
            Check(mini is not null&&mini.IsVisible&&!window.IsVisible,"迷你窗口替换主窗口显示");
            Check(ReferenceEquals(mini!.DataContext,model),"迷你窗口与主窗口共用同一数据");
            var start=mini.FindControl<Button>("MiniStart")!;var pause=mini.FindControl<Button>("MiniPause")!;
            var stop=mini.FindControl<PopConfirm>("StopConfirm")!;
            Check(ReferenceEquals(start.Command,model.StartCommand)&&ReferenceEquals(pause.Command,model.PauseCommand),"开始与暂停连接到主窗口同一命令");
            Check(ReferenceEquals(stop.ConfirmCommand,model.StopAllCommand),"停止全部需要二次确认后才执行");
            model.RuntimeIssues="维修任务等待报价核实\n采购任务等待核账确认";mini.UpdateLayout();
            Check(model.IssueCount==2&&model.IssueBadge=="2",$"待处理问题计数为 2（实际 {model.IssueCount}）");var badge=mini.FindControl<Border>("IssueBadge");Check(badge?.IsEffectivelyVisible==true&&badge.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="2"),$"待处理问题数量显示在角标上（visible={badge?.IsVisible} count={model.IssueBadge}）");
            foreach(var variant in new[]{ThemeVariant.Dark,ThemeVariant.Light})
            foreach(var expanded in new[]{false,true})
            {
                Application.Current!.RequestedThemeVariant=variant;model.MiniExpanded=expanded;await Settle(mini);
                var label=$"迷你窗口 {variant} {(expanded?"展开":"收起")}";
                Check(Math.Abs(mini.Bounds.Width-340)<1&&mini.Bounds.Height<(expanded?560:200),label+$" 尺寸紧凑（{mini.Bounds.Width:0}×{mini.Bounds.Height:0}）");
                UiLayoutRegression.AssertScene(mini,label,passed);
                foreach(var count in new[]{1,12,123})
                {
                    model.RuntimeIssues=string.Join("\n",Enumerable.Range(1,count).Select(i=>"待处理问题 "+i));await Settle(mini);
                    var number=badge!.GetVisualDescendants().OfType<TextBlock>().First(t=>t.Text==count.ToString());
                    var origin=number.TranslatePoint(default,mini)!.Value;
                    Check(origin.X>=0&&origin.Y>=0&&origin.X+number.Bounds.Width<=mini.Bounds.Width&&origin.Y+number.Bounds.Height<=mini.Bounds.Height,label+" 消息角标完整显示 "+count);
                }
                model.RuntimeIssues="维修待确认\n采购待确认";await Settle(mini);
                var buttons=mini.GetVisualDescendants().OfType<Button>().Where(b=>b.IsEffectivelyVisible&&!b.Classes.Contains("mini-expand")&&!b.GetVisualAncestors().OfType<ComboBox>().Any()).ToArray();
                Check(buttons.All(b=>b.Bounds.Height>=28&&b.Bounds.Width>=28&&b.CornerRadius.TopLeft>=b.Bounds.Height/2-.5),label+" 操作尺寸与圆角一致");
                if(previewDirectory is not null)
                {
                    Directory.CreateDirectory(previewDirectory);
                    using var bitmap=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(mini.Bounds.Width),(int)Math.Ceiling(mini.Bounds.Height)));
                    bitmap.Render(mini);bitmap.Save(Path.Combine(previewDirectory,$"mini-{variant}-{(expanded?"expanded":"collapsed")}.png"),PngBitmapEncoderOptions.Default);
                }
            }
            model.ExitMiniCommand.Execute(null);await Settle(window);
            Check(window.IsVisible&&!mini.IsVisible,"返回主窗口后迷你窗口隐藏");
            mini.Close();await Settle(window);
            Check(window.IsVisible&&!model.IsMiniMode,"关闭迷你窗口只回到主窗口，不退出程序");
            model.EnterMiniCommand.Execute(null);await Settle(window);mini.Close();await Settle(window);
            Check(window.IsVisible&&!mini.IsVisible&&!model.IsMiniMode,"从迷你窗口关闭时恢复主窗口");
            return passed.ToArray();
        }
        finally
        {
            model.RuntimeIssues=issues;model.MiniExpanded=false;model.IsMiniMode=false;
            Application.Current!.RequestedThemeVariant=theme;
        }
    }
    static async Task Settle(Window window){window.UpdateLayout();await Task.Delay(260);window.UpdateLayout();}
}
