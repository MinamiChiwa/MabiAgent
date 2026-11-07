using System.Reflection;
using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Desktop.Tests;
public sealed class GlobalStopServiceTests
{
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
