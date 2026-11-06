using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Workflows.Tests;

public sealed class FishingContinuationTests
{
    [Fact] public void CompletedRoundStartsAnotherCastWithoutReplayingUnknownCast()
    {
        var port=new FishingPort();var store=new MemoryStore();var runner=new IdleFishingRunner(new("lake",60),port,store);
        var state=runner.Snapshot();state["phase"]="refresh";store.Flush(state);runner=new(new("lake",60),port,store);runner.Resume();
        port.Frame=new(1,"cast-1","fishing",FishingButton:true);runner.Tick();
        port.Frame=new(2,"wait-1","scene",Compass:true);runner.Tick();runner.Tick();
        Assert.Equal(1,port.Actions.Count(a=>a.Kind=="start"));Assert.NotNull(runner.Snapshot()["pending"]);
        port.Frame=new(3,"round-1","fishing",LeisureMode:false,RoundActive:true,Gauge:.8);runner.Tick();
        port.Frame=new(4,"end-1","scene",Compass:true);runner.Tick();Assert.Equal(1,runner.Snapshot()["completed"]!.GetValue<int>());
        port.Frame=new(5,"cast-2","fishing",FishingButton:true);runner.Tick();
        Assert.Equal(2,port.Actions.Count(a=>a.Kind=="start"));Assert.Equal("start",runner.Snapshot()["pending"]!["kind"]!.GetValue<string>());
    }
}
