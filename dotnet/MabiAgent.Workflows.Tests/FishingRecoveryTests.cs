using System.Text.Json.Nodes;
using MabiAgent.Workflows;
using MabiAgent.Core;
using Xunit;

namespace MabiAgent.Workflows.Tests;

public sealed class FishingRecoveryTests
{
    [Fact] public void RejectedNavigationCannotResetTheWallClockBudget()
    {
        var clock=new ManualTime();var port=new FishingPort{Error=new InputRejectedBeforeDispatchException("unsent")};
        var runner=new IdleFishingRunner(new("river",120),port,new MemoryStore(),navigationTimeoutSeconds:10,time:clock);
        for(var i=0;i<10;i++){port.Frame=new(clock.Seconds,i.ToString());runner.Tick();clock.Seconds++;}
        Assert.Empty(port.Actions);Assert.Throws<TimeoutException>(()=>runner.Tick());
        runner.Pause();clock.Seconds+=100;runner.Resume();port.Frame=new(clock.Seconds,"resumed");runner.Tick();
        Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());
    }
    private static (IdleFishingRunner Runner, FishingPort Port, MemoryStore Store) AtPoint()
    {
        var port = new FishingPort{ReleaseAllowed=true};
        var store = new MemoryStore();
        var runner = new IdleFishingRunner(new(FishingPlan.CurrentLocation, 120), port, store);
        return (runner, port, store);
    }

    [Fact]
    public void NoRoundForTwentySecondsStartsAFreshCastAndRecordsTheMiss()
    {
        var (runner, port, _) = AtPoint();
        port.Frame = new(1, "cast", "fishing", FishingButton: true);runner.Tick();
        var original=runner.Snapshot()["pending"]!.DeepClone();
        port.Frame = new(20.9, "waiting", "scene", Compass: true);runner.Tick();
        Assert.Single(port.Actions);
        port.Frames.Enqueue(new(21,"retry-scene","scene",Compass:true));
        port.Frames.Enqueue(new(21.1,"retry-cast","fishing",FishingButton:true));runner.Tick();
        var state=runner.Snapshot();
        Assert.Equal("running",state["status"]!.GetValue<string>());
        Assert.Equal(new[]{"start","move","start"},port.Actions.Select(a=>a.Kind));
        Assert.Equal(1,state["unobserved_cast_attempts"]!.GetValue<int>());
        Assert.Equal(original["capture_id"]!.GetValue<string>(),state["last_unobserved_cast"]!["capture_id"]!.GetValue<string>());
        Assert.Equal("retry-cast",state["pending"]!["capture_id"]!.GetValue<string>());
        Assert.Equal(20,state["elapsed_s"]!.GetValue<double>());
        Assert.Equal(0,state["completed"]!.GetValue<int>());
    }

    [Fact]
    public void HookOnRetryDeadlineContinuesTheRoundWithoutAnotherCast()
    {
        var (runner,port,_) = AtPoint();
        port.Frame=new(1,"cast","fishing",FishingButton:true);runner.Tick();
        port.Frame=new(21,"hook","fishing",RoundActive:true,LeisureMode:false,HookVisible:true,Gauge:.7);runner.Tick();
        Assert.Null(runner.Snapshot()["pending"]);Assert.Single(port.Actions);
        Assert.Equal("round",runner.Snapshot()["phase"]!.GetValue<string>());
    }

    [Fact]
    public void UnrecognizedPageHoldsRetryUntilTheCurrentSceneIsReady()
    {
        var (runner,port,_) = AtPoint();
        port.Frame=new(1,"cast","fishing",FishingButton:true);runner.Tick();
        port.Frame=new(21,"loading","unknown",Stable:false);runner.Tick();
        Assert.Single(port.Actions);Assert.NotNull(runner.Snapshot()["pending"]);
        port.Frame=new(22,"ready","fishing",FishingButton:true);runner.Tick();
        Assert.Equal(2,port.Actions.Count);Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());
    }

    [Fact]
    public void ManualPausePreventsRetryAndLateHookOnlyReconciles()
    {
        var (runner,port,_) = AtPoint();
        port.Frame=new(1,"cast","fishing",FishingButton:true);runner.Tick();runner.Pause();
        port.Frame=new(21,"expired","scene",Compass:true);runner.Tick();
        Assert.Single(port.Actions);Assert.NotNull(runner.Snapshot()["pending"]);
        port.Frame=new(22,"late-hook","fishing",RoundActive:true,LeisureMode:false,HookVisible:true,Gauge:.7);runner.ObserveOnly();
        Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("manual",runner.Snapshot()["pause_reason"]!.GetValue<string>());
        Assert.Single(port.Actions);
    }

    [Fact]
    public void LegacyCastIntentUsesTwentySecondBudgetAfterExplicitContinue()
    {
        var (runner,port,store) = AtPoint();
        port.Frame=new(1,"cast","fishing",FishingButton:true);runner.Tick();
        var state=runner.Snapshot();state["pending"]!["deadline"]=16d;store.Flush(state);
        runner=new(new(FishingPlan.CurrentLocation,120),port,store);runner.Resume();
        port.Frame=new(20,"wait","scene",Compass:true);runner.Tick();Assert.Single(port.Actions);
        port.Frame=new(21,"retry","fishing",FishingButton:true);runner.Tick();
        Assert.Equal(2,port.Actions.Count);Assert.Equal(16d,runner.Snapshot()["last_unobserved_cast"]!["deadline"]!.GetValue<double>());
    }

    [Fact]
    public void ArrivalClearsTravelOwnershipBeforeLocalCastPreflightFails()
    {
        var port = new FishingPort{ReleaseAllowed=true};
        var runner = new IdleFishingRunner(new("lake", 120), port, new MemoryStore());
        runner.Tick();
        port.Frame = new(1, "travel", NavigationActive: true);
        runner.Tick();
        port.Frame = new(2, "arrived", NavigationActive: false);
        runner.Tick();
        Assert.False(runner.Snapshot()["navigation_seen"]!.GetValue<bool>());
        Assert.Null(runner.Snapshot()["navigation_deadline"]);
        Assert.True(runner.CanIsolateFault);
    }

    [Fact]
    public void RepeatedUnsentCastPreflightFailuresAreBoundedAcrossSceneFrames()
    {
        var (runner, port, _) = AtPoint();
        for (var i = 0; i < 3; i++)
        {
            port.Frame = new(i * 2 + 1, "cast-" + i, "fishing", FishingButton: true);
            port.Error = new InputRejectedBeforeDispatchException("stale");
            runner.Tick();
            port.Error = null;
            port.Frame = new(i * 2 + 2, "scene-" + i, "scene");
            if (i < 2) runner.Tick();
        }
        Assert.Throws<TimeoutException>(() => runner.Tick());
        Assert.Null(runner.Snapshot()["pending"]);
        Assert.True(runner.CanIsolateFault);
        Assert.Empty(port.Actions);
        runner.Resume();
        port.Frame = new(8, "fresh", "fishing", FishingButton: true);
        runner.Tick();
        Assert.Equal("start", Assert.Single(port.Actions).Kind);
    }

    [Fact]
    public void RepeatedNoResponseCastsKeepTheSameTargetAndContinueTheClock()
    {
        var (runner,port,_) = AtPoint();
        foreach(var at in new[]{1d,21d,41d})
        {port.Frame=new(at,"cast-"+at,"fishing",FishingButton:true);runner.Tick();}
        var state=runner.Snapshot();
        Assert.Equal(3,port.Actions.Count);Assert.Equal(2,state["unobserved_cast_attempts"]!.GetValue<int>());
        Assert.Equal(120d,state["target_s"]!.GetValue<double>());Assert.Equal(40d,state["elapsed_s"]!.GetValue<double>());
        Assert.True(runner.NeedsBackpackCheck);
        Assert.Equal("running",state["status"]!.GetValue<string>());
    }

    [Fact]
    public void UnsentPreflightLimitIsolatesFishingAndContinuesIndependentProduction()
    {
        var (runner,port,store)=AtPoint();var saved=runner.Snapshot();saved["cast_preflight_rejections"]=3;store.Flush(saved);
        runner=new(new(FishingPlan.CurrentLocation,120),port,store);
        var otherPort=new ProductionPort();var other=new ProductionRunner(otherPort,new MemoryStore(),new("iron","metal",60));
        var dispatcher=new WorkflowDispatcher();dispatcher.Register("fish",runner);dispatcher.Register("iron",other,"metal");dispatcher.Continue();
        // Let the dispatcher start this restored runner, then reproduce three current refusals.
        WorkCandidate[] plan=[new("fish","idle_fishing",null,true,0,0),new("iron","production","metal",true,1,1)];
        for(var i=0;i<3;i++)dispatcher.Tick(plan.Take(1),null);
        port.Error=new InputRejectedBeforeDispatchException("stale");
        for(var i=1;i<=3;i++){port.Frame=new(i,"reject-"+i,"fishing",FishingButton:true);dispatcher.Tick(plan.Take(1),null);}
        port.Error=null;port.Frame=new(4,"limit","scene");dispatcher.Tick(plan.Take(1),null);
        for(var i=0;i<5;i++)dispatcher.Tick(plan,null);
        Assert.False(dispatcher.IsPaused);Assert.Empty(port.Actions);Assert.Contains("fish",dispatcher.Faults.Keys);Assert.Contains("queue",otherPort.Navigation);
    }

    [Fact]
    public void DismountDisappearanceStillHonorsFixedTransitionWait()
    {
        var (runner, port, _) = AtPoint();
        port.Frame = new(1, "mounted", Mounted: true, Compass: true);
        runner.Tick();
        port.Frame = new(1.1, "control-gone", "fishing", FishingButton: true);
        runner.Tick();
        Assert.Equal("dismount", Assert.Single(port.Actions).Kind);
        port.Frame = new(2, "settled", "fishing", FishingButton: true);
        runner.Tick();
        Assert.Equal(new[] { "dismount", "start" }, port.Actions.Select(a => a.Kind));
    }

    [Fact]
    public void NavigationBudgetStillExpiresOnUnrecognizedFrames()
    {
        var port = new FishingPort{ReleaseAllowed=true};
        var runner = new IdleFishingRunner(new("lake", 120), port, new MemoryStore(), navigationTimeoutSeconds: 10);
        runner.Tick();
        port.Frame = new(11, "loading", "unknown", Stable: false);
        runner.Tick();
        Assert.Equal("navigation_not_observed", runner.Snapshot()["reason"]!.GetValue<string>());
        Assert.Single(port.Actions);
    }
}
