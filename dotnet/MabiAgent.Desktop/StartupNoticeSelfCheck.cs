using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MabiAgent.Capture;

namespace MabiAgent.Desktop;

internal static class StartupNoticeSelfCheck
{
    public static async Task<string[]> Run()
    {
        var passed=new List<string>();
        void Check(bool condition,string label){if(!condition)throw new InvalidOperationException(label);passed.Add(label);}
        var directory=Path.Combine(Path.GetTempPath(),"mabi-startup-check-"+Guid.NewGuid().ToString("N"));
        try
        {
            var available=StartupNotice.CreateView(new(true,null));
            Check(available.Download is null,"已有可用Npcap时须知不创建安装提醒或下载按钮");
            Check(!available.Window.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text?.Contains("Npcap")==true),"已有Npcap时不展示Npcap文案");
            available.Window.Show();await Task.Delay(120);available.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!available.Window.IsVisible&&available.Window.Tag is not true,"取消须知关闭窗口且不视为同意");

            var calls=0;
            NpcapAvailability Detect(){calls++;return new(false,"未检测到可用的 Npcap 组件。请从官网下载安装。");}
            async Task<bool> Choose(NpcapAvailability status,bool accept)
            {
                var view=StartupNotice.CreateView(status);
                Check(view.Download?.Tag is Uri uri&&uri.AbsoluteUri==NpcapAvailability.OfficialDownloadUrl,"缺少Npcap时下载按钮指向HTTPS官方下载页");
                view.Window.Show();await Task.Delay(120);view.Window.UpdateLayout();
                if(Environment.GetEnvironmentVariable("MABI_WORKBENCH_PREVIEW_DIR") is{} preview)
                {
                    Directory.CreateDirectory(preview);
                    using var bitmap=new RenderTargetBitmap(new PixelSize((int)view.Window.Bounds.Width,(int)view.Window.Bounds.Height));
                    bitmap.Render(view.Window);bitmap.Save(Path.Combine(preview,"startup-npcap-missing.png"),PngBitmapEncoderOptions.Default);
                }
                (accept?view.Confirm:view.Cancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return view.Window.Tag is true;
            }
            Check(!await StartupNotice.ConfirmAsync(directory,Detect,s=>Choose(s,false)),"首次启动取消须知返回退出");
            Check(!StartupNotice.IsAccepted(directory),"取消不会保存首次须知确认记录");
            Check(await StartupNotice.ConfirmAsync(directory,Detect,s=>Choose(s,true)),"首次启动可确认含Npcap提示的须知");
            Check(StartupNotice.IsAccepted(directory),"确认后保存须知状态");
            Check(await StartupNotice.ConfirmAsync(directory,()=>throw new InvalidOperationException("不应重复检测"),_=>throw new InvalidOperationException("不应重复弹窗")),"再次启动跳过Npcap检测与须知弹窗");
            Check(calls==2,"仅未确认的启动执行一次依赖检测");
            return passed.ToArray();
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
