using MabiAgent.Workflows;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Workflows.Tests;

public sealed class FishingContinuationTests
{
    [Theory][InlineData("lake")][InlineData(FishingPlan.CurrentLocation)]
    public void InitialModeConfirmationCompletesWithoutWaitingForARound(string point)
    {
        var (runner,port,_)=AtPoint(point,300);
        port.Frame=new(1,"initial-dialog","fishing",ModeConfirm:true);runner.Tick();
        Assert.Equal("confirm_mode",Assert.Single(port.Actions).Kind);
        port.Frame=new(2,"after-confirm",Compass:true);runner.Tick();
        Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());
        Assert.Single(port.Actions,a=>a.Kind=="confirm_mode");
    }
    [Fact] public void ModeConfirmationInterruptsThePendingFirstCastImmediately()
    {
        var (runner,port,_)=AtPoint(FishingPlan.CurrentLocation,300);
        port.Frame=new(1,"cast","fishing",FishingButton:true);runner.Tick();
        port.Frame=new(1.2,"dialog","fishing",ModeConfirm:true,RoundActive:true);runner.Tick();
        Assert.Equal(new[]{"start","confirm_mode"},port.Actions.Select(a=>a.Kind));
        Assert.Equal("confirm_mode",runner.Snapshot()["pending"]!["kind"]!.GetValue<string>());
    }
    [Fact] public void SuccessfulCastResetsConsecutiveNoResponseAttempts()
    {
        var (runner,port,store)=AtPoint(FishingPlan.CurrentLocation,300);var state=runner.Snapshot();state["unobserved_cast_attempts"]=1;store.Flush(state);
        runner=new(new(FishingPlan.CurrentLocation,300),port,store);runner.Resume();
        port.Frame=new(1,"cast","fishing",FishingButton:true);runner.Tick();
        port.Frame=new(2,"round","fishing",RoundActive:true,LeisureMode:false,Gauge:.8);runner.Tick();
        Assert.Equal(0,runner.Snapshot()["unobserved_cast_attempts"]!.GetValue<int>());Assert.False(runner.NeedsBackpackCheck);
    }
    [Fact] public void BackpackExitTimeoutIsIsolatedAndIndependentWorkContinues()
    {
        var time=new ManualTime();var port=new FishingPort{ReleaseAllowed=true,Frame=new(1,"bag","bag")};
        var runner=new IdleFishingRunner(new(FishingPlan.CurrentLocation,300),port,new MemoryStore(),time:time);
        var dispatcher=new WorkflowDispatcher(time);dispatcher.Register("fish",runner);dispatcher.Continue();
        WorkCandidate[] plan=[new("fish","idle_fishing",null,true,5,0)];
        dispatcher.Tick(plan,null);dispatcher.Tick(plan,null);dispatcher.Tick(plan,null);time.Seconds+=16;dispatcher.Tick(plan,null);
        Assert.Contains("fish",dispatcher.Faults.Keys);Assert.False(dispatcher.IsPaused);Assert.Null(runner.Snapshot()["pending"]);
        var otherPort=new ProductionPort{Frame=new("metal_panel",EmptySlots:[0],SlotCount:1)};var other=new ProductionRunner(otherPort,new MemoryStore(),new("other","metal",60),time:time);
        dispatcher.Register("other",other,"metal");for(var i=0;i<4;i++)dispatcher.Tick([new("other","production","metal",true,5,1)],null);
        Assert.Contains("recipe",otherPort.Navigation);Assert.False(dispatcher.IsPaused);
    }
    [Fact] public void ManualPauseKeepsTheCurrentRoundsLowPoint()
    {
        var (runner,port,_)=AtPoint(FishingPlan.CurrentLocation,300);
        port.Frame=new(1,"low","fishing",RoundActive:true,LeisureMode:false,Gauge:.2);runner.Tick();
        runner.Pause();runner.Resume();
        port.Frame=new(10,"rebound","fishing",RoundActive:true,LeisureMode:false,Gauge:.4);runner.Tick();
        Assert.Single(port.Actions,a=>a.Kind=="reel");
    }
    [Theory][InlineData("lake")][InlineData(FishingPlan.CurrentLocation)]
    public void YieldedLowGaugeCannotReelTheNextRound(string point)
    {
        var (runner,port,_)=AtPoint(point,300);
        port.Frame=new(1,"old-low","fishing",RoundActive:true,LeisureMode:false,Gauge:.1);runner.Tick();
        runner.RequestYield();port.Frame=new(2,"yield","fishing",RoundActive:true,LeisureMode:false,Gauge:.1);runner.Tick();
        runner.Resume();Assert.Null(runner.Snapshot()["gauge_min"]);Assert.Null(runner.Snapshot()["sample_at"]);
        port.Frame=new(20,"cast","fishing",FishingButton:true);runner.Tick();
        foreach(var (at,value) in new[]{(21d,.9),(22d,.7),(23d,.4),(24d,.2)})
        {port.Frame=new(at,"falling-"+at,"fishing",RoundActive:true,LeisureMode:false,Gauge:value);runner.Tick();}
        Assert.DoesNotContain(port.Actions,a=>a.Kind=="reel");
        port.Frame=new(25,"real-rebound","fishing",RoundActive:true,LeisureMode:false,Gauge:.4);runner.Tick();runner.Tick();
        Assert.Single(port.Actions,a=>a.Kind=="reel");
    }
    [Theory][InlineData("lake","backpack")][InlineData("lake","bag")][InlineData("lake","repair_tools")]
    [InlineData(FishingPlan.CurrentLocation,"backpack")][InlineData(FishingPlan.CurrentLocation,"bag")][InlineData(FishingPlan.CurrentLocation,"repair_tools")]
    public void RepeatedBackpackInterruptionsClearSamplesAndUseTheSharedExit(string point,string page)
    {
        var (runner,port,_)=AtPoint(point,300);
        for(var cycle=0;cycle<2;cycle++)
        {
            var at=cycle*30;
            port.Frame=new(at+1,"low-"+cycle,"fishing",RoundActive:true,LeisureMode:false,Gauge:.1);runner.Tick();
            port.Frame=new(at+2,"bag-"+cycle,page);runner.Tick();
            Assert.Null(runner.Snapshot()["gauge_min"]);Assert.Null(runner.Snapshot()["sample_at"]);Assert.Null(runner.Snapshot()["pending"]);
            Assert.Equal(cycle+1,port.BackpackExits);
            port.Frames.Enqueue(new(at+4,"scene-"+cycle,Compass:true));
            port.Frames.Enqueue(new(at+4.1,"cast-"+cycle,"fishing",FishingButton:true));runner.Tick();
            Assert.True(port.CastDuringMovement);
            foreach(var (offset,value) in new[]{(5d,.9),(6d,.6),(7d,.2)})
            {port.Frame=new(at+offset,"new-"+cycle+"-"+offset,"fishing",RoundActive:true,LeisureMode:false,Gauge:value);runner.Tick();}
            Assert.DoesNotContain(port.Actions,a=>a.Kind=="reel");
        }
        port.Frame=new(38,"real-rebound","fishing",RoundActive:true,LeisureMode:false,Gauge:.4);runner.Tick();
        Assert.Single(port.Actions,a=>a.Kind=="reel");
    }
    private static (IdleFishingRunner Runner,FishingPort Port,MemoryStore Store) AtPoint(string point,double seconds=120)
    {
        var port=new FishingPort();var store=new MemoryStore();var plan=new FishingPlan(point,seconds);
        var initial=new IdleFishingRunner(plan,port,store).Snapshot();initial["phase"]="refresh";store.Flush(initial);
        var runner=new IdleFishingRunner(plan,port,store);runner.Resume();return(runner,port,store);
    }
    [Theory][InlineData("lake")][InlineData(FishingPlan.CurrentLocation)]
    public void RoundEndRecastsInTheSameTickWithoutMovement(string point)
    {
        var (runner,port,_)=AtPoint(point);
        port.Frames.Enqueue(new(1,"scene",Compass:true));port.Frames.Enqueue(new(1.1,"first","fishing",FishingButton:true));runner.Tick();
        port.Frame=new(2,"hook","fishing",RoundActive:true,LeisureMode:false,Gauge:.6);runner.Tick();
        port.Frame=new(4,"ended","fishing",FishingButton:true,Compass:true);runner.Tick();
        Assert.Equal(new[]{"move","start","start"},port.Actions.Select(a=>a.Kind));
        Assert.False(port.CastDuringMovement);Assert.Equal(1,runner.Snapshot()["completed"]!.GetValue<int>());
        Assert.Equal(2.9,runner.Snapshot()["elapsed_s"]!.GetValue<double>(),6);
    }
    [Theory][InlineData("lake")][InlineData(FishingPlan.CurrentLocation)]
    public void MissedWindowUsesMovementToRestoreCasting(string point)
    {
        var (runner,port,_)=AtPoint(point);
        port.Frame=new(1,"first","fishing",FishingButton:true);runner.Tick();
        port.Frame=new(2,"hook","fishing",RoundActive:true,LeisureMode:false);runner.Tick();
        port.Frames.Enqueue(new(5,"expired",Compass:true));port.Frames.Enqueue(new(5.1,"restored","fishing",FishingButton:true));runner.Tick();
        Assert.Equal(new[]{"start","move","start"},port.Actions.Select(a=>a.Kind));Assert.True(port.CastDuringMovement);
    }
    [Fact] public void ClockIncludesWaitingAndRoundGapsButExcludesBackpackAndManualPause()
    {
        var (runner,port,_)=AtPoint(FishingPlan.CurrentLocation,30);
        port.Frame=new(1,"cast","fishing",FishingButton:true);runner.Tick();
        port.Frame=new(6,"no-hook",Compass:true);runner.Tick();Assert.Equal(5,runner.Snapshot()["elapsed_s"]!.GetValue<double>());
        port.Frame=new(7,"bag","backpack");runner.Tick();port.Frame=new(50,"bag-reading","backpack");runner.Tick();
        Assert.Equal(6,runner.Snapshot()["elapsed_s"]!.GetValue<double>());Assert.Null(runner.Snapshot()["pending"]);
        port.Frames.Enqueue(new(51,"scene",Compass:true));port.Frames.Enqueue(new(51.1,"cast-again","fishing",FishingButton:true));runner.Tick();
        port.Frame=new(56,"waiting",Compass:true);runner.Tick();Assert.Equal(11,runner.Snapshot()["elapsed_s"]!.GetValue<double>());
        runner.Pause();port.Frame=new(100,"paused",Compass:true);runner.Tick();runner.Resume();
        port.Frame=new(101,"resumed",Compass:true);runner.Tick();port.Frame=new(106,"running",Compass:true);runner.Tick();
        Assert.Equal(16,runner.Snapshot()["elapsed_s"]!.GetValue<double>());
        port.Frame=new(121,"deadline",Compass:true);runner.Tick();
        Assert.Equal("complete",runner.Snapshot()["status"]!.GetValue<string>());Assert.Equal(30,runner.Snapshot()["elapsed_s"]!.GetValue<double>());
    }
    [Fact] public void YieldDoesNotWaitForUnobservedCastOrCountTimeWhileAway()
    {
        var (runner,port,_)=AtPoint(FishingPlan.CurrentLocation);
        port.Frame=new(1,"cast","fishing",FishingButton:true);runner.Tick();runner.RequestYield();
        port.Frame=new(4,"waiting",Compass:true);runner.Tick();
        Assert.True(runner.SafeToSwitch);Assert.Null(runner.Snapshot()["pending"]);Assert.Equal(3,runner.Snapshot()["elapsed_s"]!.GetValue<double>());
        runner.Resume();port.Frame=new(100,"restored");runner.Tick();
        Assert.Equal(3,runner.Snapshot()["elapsed_s"]!.GetValue<double>());
    }
    [Fact] public void ReelTimeoutAndPreviouslyUnobservedRoundRemainRecoverable()
    {
        var (runner,port,_)=AtPoint(FishingPlan.CurrentLocation);
        port.Frame=new(1,"existing","fishing",RoundActive:true,LeisureMode:false,Gauge:.2);runner.Tick();
        Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());
        port.Frame=new(2,"rebound","fishing",RoundActive:true,LeisureMode:false,Gauge:.4);runner.Tick();
        Assert.Equal("reel",Assert.Single(port.Actions).Kind);
        port.Frame=new(18,"timeout","fishing",RoundActive:true,LeisureMode:false,Gauge:.5);runner.Tick();
        Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());Assert.Null(runner.Snapshot()["pending"]);Assert.True(runner.NeedsBackpackCheck);
        runner.RequestYield();port.Frame=new(19,"release","fishing",RoundActive:true,LeisureMode:false,Gauge:.5);runner.Tick();Assert.True(runner.SafeToSwitch);
    }
    [Theory][InlineData("lake")][InlineData(FishingPlan.CurrentLocation)]
    public void PeriodicInspectionNeedsTwentyCompletedRoundsAndResetsAfterYield(string point)
    {
        var (runner,port,_)=AtPoint(point,1000);
        for(var round=0;round<20;round++)
        {
            port.Frame=new(round*3+1,"cast-"+round,"fishing",FishingButton:true);runner.Tick();
            port.Frame=new(round*3+2,"hook-"+round,"fishing",RoundActive:true,LeisureMode:false);runner.Tick();
            port.Frame=new(round*3+3,"end-"+round,"fishing",FishingButton:true,Compass:true);runner.Tick();
            Assert.Equal(round==19,runner.PeriodicBackpackCheckAllowed);
        }
        runner.RequestYield();port.Frame=new(62,"yield");runner.Tick();runner.Resume();Assert.False(runner.PeriodicBackpackCheckAllowed);
        port.Frame=new(63,"full",BackpackFull:true);runner.Tick();Assert.True(runner.NeedsBackpackCheck);
    }
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
