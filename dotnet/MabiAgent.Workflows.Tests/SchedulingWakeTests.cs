using Xunit;
using Xunit.Abstractions;

namespace MabiAgent.Workflows.Tests;

public sealed class SchedulingWakeTests
{
    private readonly ITestOutputHelper output;
    public SchedulingWakeTests(ITestOutputHelper output)=>this.output=output;

    [Fact] public async Task IdleHealthCadenceReducesActualTimeoutWakeups()
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5.4));
        async Task<int> Count(TimeSpan delay)
        {
            var wake=new SchedulingWake();var count=0;
            try{while(true){await wake.WaitAsync(delay,stop.Token);count++;}}
            catch(OperationCanceledException)when(stop.IsCancellationRequested){return count;}
        }
        var counts=await Task.WhenAll(Count(AutomationCadence.Active),Count(AutomationCadence.Health));
        output.WriteLine("5.4 秒无事件等待：300ms 周期 {0} 次；5s 健康周期 {1} 次。仅统计超时唤醒，不代表 CPU 或游戏吞吐。",counts[0],counts[1]);
        Assert.True(counts[0]>counts[1],"闲置健康周期应减少无事件超时唤醒。");
    }
    [Fact] public async Task SignalsBeforeAndDuringWaitAreObservedAndCoalesced()
    {
        var wake=new SchedulingWake();for(var i=0;i<1000;i++)wake.Signal();
        await wake.WaitAsync(TimeSpan.FromSeconds(5),CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        var waiting=wake.WaitAsync(TimeSpan.FromSeconds(5),CancellationToken.None).AsTask();
        Assert.False(waiting.IsCompleted);wake.Signal();await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }
    [Fact] public async Task ExpiredWaitDoesNotLeaveAReaderToStealTheNextSignal()
    {
        var wake=new SchedulingWake();await wake.WaitAsync(TimeSpan.FromMilliseconds(5),CancellationToken.None);
        wake.Signal();await wake.WaitAsync(TimeSpan.FromSeconds(5),CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
    }
    [Fact] public async Task StopCancelsSleepingWorkerAndOutranksBufferedSignal()
    {
        var wake=new SchedulingWake();using var stop=new CancellationTokenSource();
        var waiting=wake.WaitAsync(TimeSpan.FromMinutes(1),stop.Token).AsTask();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>waiting.WaitAsync(TimeSpan.FromSeconds(1)));
        wake.Signal();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>wake.WaitAsync(TimeSpan.FromMinutes(1),stop.Token).AsTask());
    }
    [Theory]
    [InlineData(100,200,5)]
    [InlineData(100,102,2)]
    [InlineData(100,100.125,.125)]
    [InlineData(100,99,0)]
    [InlineData(200,100,0)]
    public void HealthWaitNeverPassesKnownDueTimeOrClockChange(double now,double due,double seconds)
        =>Assert.Equal(seconds,AutomationCadence.Until(now,due).TotalSeconds,6);
}
