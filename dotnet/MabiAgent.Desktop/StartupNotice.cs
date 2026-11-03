using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Media;
using MabiAgent.Capture;
using Ursa.Controls;
namespace MabiAgent.Desktop;
// Risk notice shown once per data directory before the tool can touch the game.
internal static class StartupNotice
{
    public const int Version=1;
    public const string Text=
        "MabiAgent 只用于技术研究和功能测试，与游戏官方无关。\n\n"+
        "· 使用第三方自动化工具可能违反游戏用户协议，账号可能被警告、限制或封禁；识别错误也可能导致道具被误用或误卖。以上风险由你自行承担。\n"+
        "· 建议只在测试账号或可接受损失的角色上使用。\n"+
        "· 本工具禁止任何商业用途，包括付费代挂、出售或出租。\n\n"+
        "点击“确定”表示你已阅读并同意以上内容；点击“取消”将退出程序。";
    static string MarkerPath(string dataPath)=>Path.Combine(dataPath,"notice-accepted.json");
    public static bool IsAccepted(string dataPath)
    {
        try{using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(MarkerPath(dataPath)));return json.RootElement.GetProperty("version").GetInt32()>=Version;}
        catch(Exception){return false;}
    }
    public static async Task<bool> ConfirmAsync(string dataPath,Func<NpcapAvailability>? detect=null,Func<NpcapAvailability,Task<bool>>? show=null)
    {
        if(IsAccepted(dataPath))return true;
        var availability=await Task.Run(detect??(()=>NpcapAvailability.Check()));
        if(!await (show??ShowAsync)(availability))return false;
        Directory.CreateDirectory(dataPath);
        File.WriteAllText(MarkerPath(dataPath),System.Text.Json.JsonSerializer.Serialize(new{version=Version,accepted_at=DateTimeOffset.Now}));
        return true;
    }

    internal sealed record NoticeView(UrsaWindow Window,Button Confirm,Button Cancel,Button? Download);

    internal static NoticeView CreateView(NpcapAvailability availability)
    {
        var window=new UrsaWindow
        {
            Title="使用前须知",Width=580,MinWidth=360,MaxHeight=760,
            CanResize=false,SizeToContent=SizeToContent.Height,
            WindowStartupLocation=WindowStartupLocation.CenterScreen
        };
        var content=new StackPanel{Spacing=16};
        content.Children.Add(new TextBlock{Text=Text,TextWrapping=TextWrapping.Wrap});
        Button? download=null;
        if(!availability.Available)
        {
            var details=new TextBlock{Text=availability.Detail,TextWrapping=TextWrapping.Wrap};
            download=new Button{Content="打开 Npcap 官方下载页",Tag=new Uri(NpcapAvailability.OfficialDownloadUrl),HorizontalAlignment=HorizontalAlignment.Left};
            download.Click+=async (_,_)=>
            {
                try
                {
                    var launcher=TopLevel.GetTopLevel(window)?.Launcher;
                    if(launcher is null||!await launcher.LaunchUriAsync(new Uri(NpcapAvailability.OfficialDownloadUrl)))
                        details.Text="无法打开默认浏览器，请复制下方官方地址进行访问。";
                }
                catch(Exception){details.Text="无法打开默认浏览器，请复制下方官方地址进行访问。";}
            };
            var reminder=new StackPanel{Spacing=8};
            reminder.Children.Add(new TextBlock{Text="抓包依赖尚不可用",FontWeight=FontWeight.SemiBold});
            reminder.Children.Add(details);
            reminder.Children.Add(new TextBlock{Text="库存同步需要 Npcap；完成安装后请重启工具。也可以先进入界面。",TextWrapping=TextWrapping.Wrap});
            reminder.Children.Add(download);
            reminder.Children.Add(new SelectableTextBlock{Text=NpcapAvailability.OfficialDownloadUrl,TextWrapping=TextWrapping.Wrap});
            content.Children.Add(new Banner{Type=NotificationType.Warning,Content=reminder});
        }
        var confirm=new Button{Content="确定",IsDefault=true};confirm.Classes.Add("Primary");
        var cancel=new Button{Content="取消",IsCancel=true};
        confirm.Click+=(_,_)=>{window.Tag=true;window.Close();};
        cancel.Click+=(_,_)=>window.Close();
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Spacing=8,Margin=new Thickness(0,16,0,0),Children={cancel,confirm}};
        var layout=new DockPanel{Margin=new Thickness(24)};
        DockPanel.SetDock(buttons,Dock.Bottom);layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer{MaxHeight=570,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,Content=content});
        window.Content=layout;
        window.Opened+=(_,_)=>Controls.WindowSizing.FitToWorkingArea(window);
        return new(window,confirm,cancel,download);
    }

    private static Task<bool> ShowAsync(NpcapAvailability availability)
    {
        var view=CreateView(availability);
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.Window.Closed+=(_,_)=>completion.TrySetResult(view.Window.Tag is true);
        view.Window.Show();return completion.Task;
    }
}
