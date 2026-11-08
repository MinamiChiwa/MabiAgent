using System.Reflection;
using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Desktop.Tests;
public sealed class GlobalStopServiceTests
{
    [Fact] public async Task GlobalPausePreservesProgressAndPendingRecordsAndDoesNotToggleResume()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"config/screen_routes.1280x960.tw.json")))root=root.Parent;
        Assert.NotNull(root);var data=Path.Combine(Path.GetTempPath(),"mabi-global-pause-"+Guid.NewGuid().ToString("N"));
        try
        {
            var workspace=new Workspace(root.FullName,data);workspace.ReadSettings();workspace.SaveStopHotkey("Ctrl+Alt+Shift+F18");workspace.SavePauseHotkey("Ctrl+Alt+Shift+F20");
            File.WriteAllText(Path.Combine(data,"work-queue.json"),"{\"schema_version\":1,\"jobs\":[]}");
            File.WriteAllText(Path.Combine(data,"input-intents.json"),"{\"sent\":true,\"result\":\"unknown\"}");
            var saved=Directory.GetFiles(data,"*.json").ToDictionary(p=>p,File.ReadAllBytes);
            await using var service=new AutomationService(workspace,new GameWindowSelection(()=>[]));
            for(var i=0;i<100&&service.ActivePauseHotkey is null;i++)await Task.Delay(20);
            Assert.Equal("Ctrl+Alt+Shift+F20",service.ActivePauseHotkey);
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(()=>service.ApplyPauseHotkeyAsync("Ctrl+Alt+Shift+F18"));
            Assert.Equal("Ctrl+Alt+Shift+F20",service.ActivePauseHotkey);
            var receiver=typeof(AutomationService).GetField("pauseHotkey",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(service)!;
            var trigger=(Action)typeof(GlobalStopHotkey).GetField("stop",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(receiver)!;
            var gate=(SemaphoreSlim)typeof(AutomationService).GetField("gate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(service)!;
            var execution=(CancellationTokenSource)typeof(AutomationService).GetField("executionLifetime",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(service)!;
            var pending=typeof(AutomationService).GetField("hotkeyPausePending",BindingFlags.Instance|BindingFlags.NonPublic)!;
            await gate.WaitAsync();
            try{trigger();trigger();Assert.Equal(1,pending.GetValue(service));Assert.False(execution.IsCancellationRequested);}
            finally{gate.Release();}
            for(var i=0;i<100&&(int)pending.GetValue(service)! !=0;i++)await Task.Delay(20);
            Assert.Equal(0,pending.GetValue(service));Assert.False(service.IsRunning);
            trigger();
            for(var i=0;i<100&&(int)pending.GetValue(service)! !=0;i++)await Task.Delay(20);
            Assert.False(service.IsRunning);Assert.False(execution.IsCancellationRequested);
            foreach(var (path,bytes) in saved)Assert.Equal(bytes,File.ReadAllBytes(path));
            Assert.False(File.Exists(Path.Combine(data,"stop-reset.json")));
        }
        finally{if(Directory.Exists(data))Directory.Delete(data,true);}
    }
    [Fact] public async Task RepeatedStopsCancelExecutionBeforeWaitingForTheWorkflowGateAndPreserveSettings()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"config/screen_routes.1280x960.tw.json")))root=root.Parent;
        Assert.NotNull(root);var data=Path.Combine(Path.GetTempPath(),"mabi-global-stop-"+Guid.NewGuid().ToString("N"));
        try
        {
            var workspace=new Workspace(root.FullName,data);workspace.ReadSettings();workspace.SaveStopHotkey("Ctrl+Alt+Shift+F19");
            var saved=File.ReadAllBytes(Path.Combine(data,"settings.json"));
            await using var service=new AutomationService(workspace,new GameWindowSelection(()=>[]));
            var gate=(SemaphoreSlim)typeof(AutomationService).GetField("gate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(service)!;
            var execution=(CancellationTokenSource)typeof(AutomationService).GetField("executionLifetime",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(service)!;
            await gate.WaitAsync();Task first;
            try
            {
                first=service.StopAllAsync();var repeated=service.StopAllAsync();
                Assert.True(execution.IsCancellationRequested);Assert.Same(first,repeated);Assert.False(first.IsCompleted);
            }
            finally{gate.Release();}
            await first.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(service.IsRunning);
            Assert.Equal(saved,File.ReadAllBytes(Path.Combine(data,"settings.json")));
            Assert.False(File.Exists(Path.Combine(data,"stop-reset.json")));
        }
        finally{if(Directory.Exists(data))Directory.Delete(data,true);}
    }
}
