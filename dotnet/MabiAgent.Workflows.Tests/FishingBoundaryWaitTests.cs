using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Workflows.Tests;

public sealed class FishingBoundaryWaitTests
{
    [Theory][InlineData(FishingPlan.CurrentLocation,true)][InlineData("point_fishing_03",true)]
    [InlineData(FishingPlan.CurrentLocation,false)][InlineData("point_fishing_03",false)]
    public void UnknownRefreshExpiresAfter45SecondsAndRequiresReleaseProof(string point,bool releasable)
    {
        var (runner,port,_,clock)=Restored(point);port.MayRelease=releasable;
        runner.Tick();clock.Seconds=44;runner.Tick();Assert.Empty(port.Actions);
        clock.Seconds=45;Assert.Throws<TimeoutException>(()=>runner.Tick());
        Assert.Equal(releasable,runner.CanIsolateFault);Assert.Null(runner.Snapshot()["pending"]);
        Assert.Equal(releasable?1:0,port.Releases);Assert.Empty(port.Actions);
    }

    [Theory][InlineData(FishingPlan.CurrentLocation)][InlineData("point_fishing_03")]
    public void SafeYieldOnUnknownPageReleasesOnlyPreparation(string point)
    {
        var (runner,port,_,_)=Restored(point);runner.Tick(higherPriority:true);
        Assert.Equal("yielded",runner.Snapshot()["status"]!.GetValue<string>());
        Assert.Equal(1724.232,runner.Snapshot()["elapsed_s"]!.GetFiniteDouble());
        Assert.Equal(42,runner.Snapshot()["submitted"]!.GetValue<int>());
        Assert.Equal(41,runner.Snapshot()["completed"]!.GetValue<int>());
        Assert.Null(runner.Snapshot()["pending"]);Assert.Empty(port.Actions);Assert.Equal(1,port.Releases);
    }

    [Theory][InlineData("wood_panel")][InlineData("recipe_detail")][InlineData("map")][InlineData("npc_dialogue")]
    public void RememberedArrivalStillExitsAnInheritedMenu(string page)
    {
        var (runner,port,_,clock)=Restored();port.Frame=new(0,"menu",page);
        runner.Tick();Assert.Equal("backpack_exit",runner.Snapshot()["phase"]!.GetValue<string>());
        Assert.Equal(1,port.MenuExits);Assert.Empty(port.Actions);
        clock.Seconds=1;port.Frame=new(1,"scene");runner.Tick();
        Assert.Equal("refresh",runner.Snapshot()["phase"]!.GetValue<string>());Assert.Equal(1,port.MenuExits);
    }

    [Fact] public void ManualPauseDoesNotExpireReleaseOrRetry()
    {
        var (runner,port,_,clock)=Restored();runner.Tick();runner.Pause();clock.Seconds=90;
        runner.Resume(false);runner.Tick(higherPriority:true);
        Assert.Equal("paused",runner.Snapshot()["status"]!.GetValue<string>());
        Assert.Equal("manual",runner.Snapshot()["pause_reason"]!.GetValue<string>());
        Assert.Equal(0,port.Releases);Assert.Empty(port.Actions);
    }

    [Theory][InlineData(true)][InlineData(false)]
    public void UnknownPageKeepsPendingCastOrWitnessedRound(bool pending)
    {
        var (_,port,store,clock)=Restored();var state=store.Read()!;
        state["phase"]=pending?"confirm_start":"round";state["round_seen"]=!pending;
        if(pending)state["pending"]=new JsonObject{["kind"]="start",["at"]=0d,["capture_id"]="sent",["deadline"]=20d};
        store.Flush(state);var runner=new IdleFishingRunner(new(FishingPlan.CurrentLocation,36000),port,store,time:clock);runner.Resume();
        var original=runner.Snapshot()["pending"]?.DeepClone();runner.Tick(higherPriority:true);clock.Seconds=90;runner.Tick();
        Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(original,runner.Snapshot()["pending"]));
        Assert.Equal(!pending,runner.Snapshot()["round_seen"]!.GetValue<bool>());Assert.Equal(0,port.Releases);Assert.Empty(port.Actions);
    }

    [Fact] public void UnknownPageKeepsWitnessedNativeNavigation()
    {
        var (_,port,store,clock)=Restored("point_fishing_03");var state=store.Read()!;
        state["phase"]="navigate";state["navigation_seen"]=true;state["navigation_deadline"]=180d;store.Flush(state);
        var runner=new IdleFishingRunner(new("point_fishing_03",36000),port,store,time:clock);runner.Resume();
        runner.Tick(higherPriority:true);clock.Seconds=45;runner.Tick();
        Assert.True(runner.Snapshot()["navigation_seen"]!.GetValue<bool>());
        Assert.Equal(180,runner.Snapshot()["navigation_deadline"]!.GetFiniteDouble());
        Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());Assert.Equal(0,port.Releases);Assert.Empty(port.Actions);
    }

    [Theory][InlineData(FishingPlan.CurrentLocation)][InlineData("point_fishing_03")]
    public void NavigationFirstSeenOnUnknownPageRetainsItsTotalDeadline(string point)
    {
        var (runner,port,_,clock)=Restored(point);port.Frame=port.Frame with{NavigationActive=true};port.MayRelease=false;
        runner.Tick();clock.Seconds=179;port.Frame=port.Frame with{NavigationActive=null};runner.Tick();
        clock.Seconds=181;Assert.Throws<TimeoutException>(()=>runner.Tick());Assert.Empty(port.Actions);Assert.Equal(0,port.Releases);
    }

    [Theory][InlineData(FishingPlan.CurrentLocation)][InlineData("point_fishing_03")]
    public void NavigationEndingOnUnknownPageStartsOneBoundedPreparationWait(string point)
    {
        var (runner,port,_,clock)=Restored(point);port.Frame=port.Frame with{NavigationActive=true};runner.Tick();
        clock.Seconds=10;port.Frame=port.Frame with{NavigationActive=false};runner.Tick();
        Assert.Null(runner.Snapshot()["unexpected_navigation_deadline"]);
        clock.Seconds=54;runner.Tick();clock.Seconds=55;Assert.Throws<TimeoutException>(()=>runner.Tick());
        Assert.True(runner.CanIsolateFault);Assert.Empty(port.Actions);
    }

    [Fact] public void UnstableLeisurePopupYieldsWithoutDispatchingCancellation()
    {
        var (runner,port,_,_)=Restored();port.Frame=new(0,"popup","fishing",Stable:false,LeisureConfirm:true);
        runner.Tick(higherPriority:true);
        Assert.Equal("yielded",runner.Snapshot()["status"]!.GetValue<string>());
        Assert.Empty(port.Actions);Assert.Equal(1,port.Releases);
    }

    [Fact] public void CurrentHookTakesPriorityOverExpiredPreparationWait()
    {
        var (runner,port,_,clock)=Restored();runner.Tick();clock.Seconds=45;
        port.Frame=new(45,"hook","fishing",LeisureMode:false,RoundActive:true,HookVisible:true,Gauge:.5);runner.Tick();
        Assert.Equal("round",runner.Snapshot()["phase"]!.GetValue<string>());
        Assert.True(runner.Snapshot()["round_seen"]!.GetValue<bool>());Assert.Equal(0,port.Releases);Assert.Empty(port.Actions);
    }

    [Fact] public void ExpiredRefreshIsolatesFishingAndDispatchesIndependentProduction()
    {
        var (runner,port,_,clock)=Restored();var good=new ProductionPort();
        var dispatcher=new WorkflowDispatcher(clock);dispatcher.Register("fish",runner);
        dispatcher.Register("iron",new ProductionRunner(good,new MemoryStore(),new("iron","metal",60),time:clock),"metal");dispatcher.Continue();
        WorkCandidate[] fish=[new("fish","idle_fishing",null,true,0,0)];
        WorkCandidate[] all=[..fish,new("iron","production","metal",true,1,1)];
        for(var i=0;i<4;i++)dispatcher.Tick(fish,null);
        clock.Seconds=45;dispatcher.Tick(fish,null);
        Assert.Contains("fish",dispatcher.Faults.Keys);Assert.False(dispatcher.IsPaused);Assert.Null(dispatcher.ActiveId);
        for(var i=0;i<4;i++)dispatcher.Tick(all,null);
        Assert.Contains("queue",good.Navigation);Assert.Empty(port.Actions);
    }

    private static (IdleFishingRunner Runner,WaitPort Port,MemoryStore Store,ManualTime Clock) Restored(string point=FishingPlan.CurrentLocation)
    {
        var clock=new ManualTime{Seconds=0};var port=new WaitPort(clock);var store=new MemoryStore();
        var runner=new IdleFishingRunner(new(point,36000),port,store,time:clock);var state=runner.Snapshot();
        state["phase"]="refresh";state["elapsed_s"]=1724.232;state["submitted"]=42;state["completed"]=41;
        store.Flush(state);runner=new(new(point,36000),port,store,time:clock);runner.Resume();return(runner,port,store,clock);
    }

    private sealed class WaitPort(ManualTime clock):IFishingPort
    {
        public bool MayRelease=true;public int Releases,MenuExits;public List<FishingAction> Actions=[];
        public FishingFrame Frame=new(0,"unknown","unknown",Stable:false);
        public bool CanResumeAtPoint=>true;
        public FishingFrame Observe()=>Frame with{At=clock.Seconds,CaptureId=clock.Seconds.ToString("R")+":"+Frame.Page};
        public bool TryReleasePreparation(){if(!MayRelease)return false;Releases++;return true;}
        public bool CanReleaseForeground()=>Releases>0;
        public void ReleaseBackpack()=>MenuExits++;
        public void Perform(FishingAction action)=>Actions.Add(action);
        public void Pause(string reason){}public void Resume(){}
    }
}
