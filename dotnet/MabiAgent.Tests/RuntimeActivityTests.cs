using MabiAgent.Desktop;
using Xunit;
namespace MabiAgent.Tests;

public sealed class RuntimeActivityTests
{
    [Fact] public async Task BusyCommandTimesOutWithoutAcquiringOrReplaying()
    {
        var directory=Path.Combine(Path.GetTempPath(),"mabi-activity-"+Guid.NewGuid().ToString("N"));
        try
        {
            var activity=new RuntimeActivity(directory);using var gate=new SemaphoreSlim(0,1);
            using(activity.Enter("识别页面"))
            {
                var error=await Assert.ThrowsAsync<TimeoutException>(()=>activity.AcquireAsync(gate,"开始",CancellationToken.None,TimeSpan.FromMilliseconds(30)));
                Assert.Contains("识别页面",error.Message);Assert.Equal(0,gate.CurrentCount);
            }
            Assert.Equal("空闲",activity.Current.Name);gate.Release();
            await activity.AcquireAsync(gate,"继续",CancellationToken.None);Assert.Equal(0,gate.CurrentCount);gate.Release();
            using var canceled=new CancellationTokenSource();canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>activity.AcquireAsync(gate,"开始",canceled.Token));
            Assert.Equal(1,gate.CurrentCount);
            Assert.Contains("TimeoutException",File.ReadAllText(Path.Combine(directory,"runtime-progress.log")));
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
