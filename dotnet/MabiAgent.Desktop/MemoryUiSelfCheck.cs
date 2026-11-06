using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using MabiAgent.Windows;
using MabiAgent.Desktop.Editors;
namespace MabiAgent.Desktop;

// Fresh-process diagnostic, isolated state, no capture and no desktop/game input.
internal static class MemoryUiSelfCheck
{
    public static async Task<string[]> Run(string root)
    {
        var directory=Path.Combine(Path.GetTempPath(),"mabi-memory-"+Guid.NewGuid().ToString("N"));
        var workspace=new Workspace(root,directory);var selection=new GameWindowSelection(()=>[]);
        var checks=new List<string>();MainViewModel? model=null;MainWindow? window=null;
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks.Add(message);}
        void Sample(string label){using var process=Process.GetCurrentProcess();process.Refresh();checks.Add($"{label}: working={process.WorkingSet64/1048576d:F1} MiB; private={process.PrivateMemorySize64/1048576d:F1} MiB; managed={GC.GetTotalMemory(false)/1048576d:F1} MiB; threads={process.Threads.Count}");}
        Sample("启动");
        await using var service=new AutomationService(workspace,selection);
        try
        {
            model=new MainViewModel(workspace,selection);await model.RefreshAsync();Sample("基础模型");
            var timer=Stopwatch.StartNew();model.AttachAutomation(service);timer.Stop();Sample($"服务绑定 {timer.Elapsed.TotalMilliseconds:F0}ms");
            Check(service.OcrLoaded&&!model.WorkbenchCreated&&!model.CompatibilityEditorsCreated,"服务绑定预加载OCR，编辑器仍按需创建");
            _=model.HasUnsavedEditorChanges;Check(!model.WorkbenchCreated&&!model.CompatibilityEditorsCreated,"关闭草稿检查不创建编辑器");
            window=new MainWindow(model);window.Show();await Task.Delay(250);window.UpdateLayout();Sample("主窗口");
            Check(service.OcrLoaded&&!model.WorkbenchCreated&&!model.CompatibilityEditorsCreated,"只查看队列保持OCR预加载且不创建编辑工作台");
            model.SelectedPage=2;window.UpdateLayout();await Task.Delay(80);Sample("设置首屏");
            for(var i=0;i<3;i++){var editor=new AnchorWorkbenchWindow(model.Workbench!);editor.Show();await Task.Delay(60);editor.Close();}
            Check(service.OcrLoaded&&!model.CompatibilityEditorsCreated,"浏览配置工作台复用OCR且不创建兼容编辑器");
            Sample("工作台打开关闭3次");
            var engine=service.GetOcr();
            timer.Restart();for(var i=0;i<12;i++){var width=new[]{120,320,640}[i%3];engine.Recognize(width,32,new byte[width*32]);}timer.Stop();Sample($"12次OCR {timer.Elapsed.TotalMilliseconds:F0}ms");
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Sample("诊断完整回收后");
            var imagePath=Path.Combine(directory,"thumbnail.png");Directory.CreateDirectory(directory);
            using(var original=new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(1600,900),new Avalonia.Vector(96,96),Avalonia.Platform.PixelFormat.Bgra8888,Avalonia.Platform.AlphaFormat.Opaque))original.Save(imagePath,Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            AnchorImageCache.Attach(path=>path);
            var thumbnail=AnchorImageCache.Load(imagePath,160)!;
            Check(thumbnail.PixelSize.Width==160&&thumbnail.PixelSize.Height==90,"缩略图按请求宽度直接解码并保持比例");
            Check(ReferenceEquals(thumbnail,AnchorImageCache.Load(imagePath,160)),"仍显示的缩略图复用同一实例");
            File.SetLastWriteTimeUtc(imagePath,DateTime.UtcNow.AddSeconds(2));
            var refreshed=AnchorImageCache.Load(imagePath,160)!;
            Check(!ReferenceEquals(thumbnail,refreshed),"图片更新后不复用旧缩略图");
            thumbnail.Dispose();refreshed.Dispose();
            return checks.ToArray();
        }
        finally{model?.PrepareClose();window?.Close();if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
