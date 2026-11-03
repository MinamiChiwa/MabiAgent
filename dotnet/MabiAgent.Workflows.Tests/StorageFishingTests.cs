using System.Text.Json.Nodes;
using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Workflows.Tests;
public sealed class StorageFishingTests
{
    [Fact] public void SatisfiedDemandRetiresOnlyUnsentWithdrawalAndPreservesPendingTransfer()
    {
        var port=new StoragePort();var first=new StorageSupplyRunner([new(1,"ore",3,"shared")],port,new MemoryStore());
        Assert.True(first.RetireSatisfiedPlans(_=>true));Assert.Equal("complete",first.Snapshot()["status"]!.GetValue<string>());Assert.Empty(port.Actions);
        var active=new StorageSupplyRunner([new(1,"ore",3,"shared")],port,new MemoryStore());active.Tick();
        Assert.False(active.RetireSatisfiedPlans(_=>true));Assert.NotNull(active.Snapshot()["pending"]);Assert.Single(port.Actions);
    }
    [Theory][InlineData("prepare_transfer",true)][InlineData("confirm_transfer",false)]
    public void FreshUnchangedBalancesCanResetOnlyPreparationAfterReturningToScene(string kind,bool resets)
    {
        var time=new ManualTime();var store=new MemoryStore();var owner="character:[\"account\",\"character\"]";
        var port=new StoragePort{Frame=new(100,"before","storage",Scope:owner,ItemId:1,Name:"ore",Available:10,BagQuantity:2,BagScope:"[\"account\",\"character\",\"old\"]",TransferAvailable:true,BalanceSource:"packet_capture",BalanceAt:100)};
        var runner=new StorageSupplyRunner([new(1,"ore",3,owner)],port,store,time:time);runner.Tick();
        var state=store.Read()!;state["pending"]!["kind"]=kind;store.Flush(state);
        runner=new([new(1,"ore",3,owner)],port,store,time:time);
        port.Frame=port.Frame with{At=117,CaptureId="fresh",Page="scene",BalanceAt=117,BagScope="[\"account\",\"character\",\"new\"]"};runner.ObserveOnly();
        Assert.Equal(resets,runner.Snapshot()["pending"] is null);Assert.Single(port.Actions);
        if(resets)Assert.Equal("navigate",runner.Snapshot()["phase"]!.GetValue<string>());
    }
    [Fact] public void UnknownFinalTransferReleasesOnlyNavigationAndRequestsOneRefreshWithoutReplay()
    {
        var store=new MemoryStore();var port=new StoragePort{LeaveAllowed=true};var plan=new WithdrawalPlan(1,"ore",3,"shared");
        var runner=new StorageSupplyRunner([plan],port,store);runner.Tick();
        port.Frame=port.Frame with{At=101,CaptureId="dialog",TransferDialog=true,DialogQuantity=3,ConfirmAvailable=true};runner.Tick();
        port.Frame=port.Frame with{At=117,CaptureId="list",TransferDialog=false};runner.Tick();
        Assert.Equal("unknown",runner.Snapshot()["status"]!.GetValue<string>());var pending=runner.Snapshot()["pending"]!.DeepClone();
        Assert.True(runner.ReleaseNavigation());Assert.True(runner.SafeToSwitch);Assert.True(runner.RequestRefreshOnce());Assert.False(runner.RequestRefreshOnce());
        Assert.True(JsonNode.DeepEquals(pending,runner.Snapshot()["pending"]));Assert.Equal(2,port.Actions.Count);
        runner=new([plan],port,store);Assert.False(runner.ReleaseNavigation());runner.Resume();runner.ObserveOnly();
        port.Frame=port.Frame with{At=118,CaptureId="fresh",Available=7,BagQuantity=5,BalanceAt=118};runner.ObserveOnly();
        Assert.Equal("complete",runner.Snapshot()["status"]!.GetValue<string>());Assert.Null(runner.Snapshot()["pending"]);Assert.Equal(2,port.Actions.Count);
    }
    [Theory][InlineData(true)][InlineData(false)] public void UnknownPreparationReleasesOnlyAfterReturningToSafeList(bool safeList)
    {
        var port=new StoragePort{LeaveAllowed=safeList};var runner=new StorageSupplyRunner([new(1,"ore",3,"shared")],port,new MemoryStore());runner.Tick();
        port.Frame=port.Frame with{At=116,CaptureId="late"};runner.Tick();var pending=runner.Snapshot()["pending"]!.DeepClone();
        Assert.Equal(safeList,runner.ReleaseNavigation());Assert.Equal(safeList,runner.RequestRefreshOnce());Assert.Equal(safeList?1:0,port.Releases);Assert.Single(port.Actions);
        Assert.True(JsonNode.DeepEquals(pending,runner.Snapshot()["pending"]));
    }
    [Theory][InlineData("character",true)][InlineData("other",false)]
    public void TransferReconciliationAcrossPacketRefreshKeepsVerifiedCharacterBoundary(string currentCharacter,bool accepted)
    {
        var store=new MemoryStore();var scope="character:[\"account\",\"character\"]";
        var port=new StoragePort{Frame=new(100,"before","storage",Scope:scope,ItemId:1,Name:"ore",Available:10,BagQuantity:2,BagScope:"[\"account\",\"character\",\"old-session\"]",TransferAvailable:true,BalanceSource:"packet_capture",BalanceAt:100)};
        var runner=new StorageSupplyRunner([new(1,"ore",3,scope)],port,store);runner.Tick();
        port.Frame=port.Frame with{At=101,CaptureId="after",Available=7,BagQuantity=5,BagScope="[\"account\",\""+currentCharacter+"\",\"new-session\"]",BalanceAt=101};runner.Tick();
        Assert.Equal(accepted,runner.Snapshot()["status"]?.GetValue<string>()=="complete");Assert.Single(port.Actions);
        if(!accepted)Assert.NotNull(runner.Snapshot()["pending"]);
    }
    [Fact] public void ExplicitStorageRetryResetsOnlyReversibleSearchAndKeepsPendingTransfer()
    {
        var store=new MemoryStore();var port=new StoragePort();var plan=new WithdrawalPlan(1,"ore",3,"shared");
        var runner=new StorageSupplyRunner([plan],port,store);runner.Tick();var state=runner.Snapshot();var pending=state["pending"]!.DeepClone();
        state["search"]=new JsonObject{["0"]=new JsonObject{["count"]=12,["next_at"]=999}};store.Flush(state);
        runner=new([plan],port,store);runner.Resume();Assert.True(JsonNode.DeepEquals(pending,runner.Snapshot()["pending"]));
        state=runner.Snapshot();state["pending"]=null;state["status"]="paused";store.Flush(state);
        runner=new([plan],port,store);runner.Resume();Assert.Empty(runner.Snapshot()["search"]!.AsObject());Assert.Single(port.Actions);
    }
    [Fact] public void InPlaceFishingStartsWithoutNavigationAndReturnsInPlaceAfterBackpackYield()
    {
        var port=new FishingPort();var store=new MemoryStore();var plan=new FishingPlan(FishingPlan.CurrentLocation,60);var runner=new IdleFishingRunner(plan,port,store);
        port.Frames.Enqueue(new(1,"scene",Compass:true));port.Frames.Enqueue(new(1.1,"tap","fishing",FishingButton:true));runner.Tick();
        Assert.Equal(new[]{"move","start"},port.Actions.Select(a=>a.Kind));
        port.Frame=new(2,"hook","fishing",LeisureMode:false,RoundActive:true,HookVisible:true,Gauge:.5);runner.Tick();runner.RequestYield();
        port.Frame=new(3,"ended",Compass:true);runner.Tick();Assert.Equal("yielded",runner.Snapshot()["status"]!.GetValue<string>());
        runner.Resume();Assert.Equal("refresh",runner.Snapshot()["phase"]!.GetValue<string>());
        port.Frames.Enqueue(new(4,"scene-after-tidy",Compass:true));port.Frames.Enqueue(new(4.1,"tap-after-tidy","fishing",FishingButton:true));runner.Tick();
        Assert.Equal(new[]{"move","start","move","start"},port.Actions.Select(a=>a.Kind));
        var pending=runner.Snapshot()["pending"]!.ToJsonString();runner.Pause();runner=new(plan,port,store);runner.Resume();port.Frame=new(10,"unchanged",Compass:true);runner.Tick();
        Assert.Equal(pending,runner.Snapshot()["pending"]!.ToJsonString());Assert.DoesNotContain(port.Actions,a=>a.Kind=="navigate");
    }
    [Fact] public void RejectedTransferNeverBecomesUnknownButNativeFailureKeepsIntent()
    {
        var port=new StoragePort{Error=new InputRejectedBeforeDispatchException("changed")};var runner=new StorageSupplyRunner([new(1,"ore",3,"shared")],port,new MemoryStore(),time:new ManualTime());
        runner.Tick();Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());
        port.Error=null;port.Frame=port.Frame with{At=101,CaptureId="2"};runner.Tick();Assert.NotNull(runner.Snapshot()["pending"]);Assert.Single(port.Actions);
        var other=new StorageSupplyRunner([new(1,"ore",3,"shared")],new StoragePort{Error=new InvalidOperationException("native")},new MemoryStore(),time:new ManualTime());
        Assert.Throws<InvalidOperationException>(()=>other.Tick());Assert.NotNull(other.Snapshot()["pending"]);Assert.Equal("unknown",other.Snapshot()["status"]!.GetValue<string>());
    }
    [Fact] public void LegacyPreparationFlagDoesNotSendSettingsActions()
    {
        var port=new FishingPort{Frame=new(1,"scene",PreparationComplete:false)};
        var runner=new IdleFishingRunner(new("lake",30),port,new MemoryStore());
        runner.Tick();Assert.Equal("navigate",Assert.Single(port.Actions).Kind);Assert.Null(runner.Snapshot()["pending"]);
    }
    [Fact] public void FishingContinuesAfterConfirmedNearbyArrivalWithoutTravelTask()
    {
        var port=new FishingPort();var runner=new IdleFishingRunner(new("lake",30),port,new MemoryStore());runner.Tick();
        port.Frame=new(2,"arrived",NavigationActive:false);runner.Tick();
        Assert.Equal("refresh",runner.Snapshot()["phase"]!.GetValue<string>());Assert.Equal("navigate",Assert.Single(port.Actions).Kind);
        port.Frames.Enqueue(new(3,"compass",Compass:true));port.Frames.Enqueue(new(3.1,"cast","fishing",FishingButton:true));
        runner.Tick();Assert.Equal(new[]{"navigate","move","start"},port.Actions.Select(a=>a.Kind));
        Assert.NotNull(runner.Snapshot()["pending"]);Assert.Equal(0,runner.Snapshot()["submitted"]!.GetValue<int>());
    }
    [Fact] public void FishingYieldsBeforeRecheckingNicknameSettings()
    {
        var port=new FishingPort{Frame=new(1,"scene",PreparationComplete:false)};
        var runner=new IdleFishingRunner(new("lake",30),port,new MemoryStore());runner.RequestYield();runner.Tick();
        Assert.Equal("yielded",runner.Snapshot()["status"]!.GetValue<string>());Assert.Empty(port.Actions);
    }
    [Fact] public void FishingYieldCanBeSignalledBeforeTheMaintenanceTick()
    {
        var port=new FishingPort();var store=new MemoryStore();var runner=new IdleFishingRunner(new("lake",30),port,store);
        var saved=runner.Snapshot();saved["phase"]="refresh";store.Flush(saved);runner=new(new("lake",30),port,store);runner.Resume();
        runner.RequestYield();
        Assert.Equal("yielded",runner.Tick()["status"]!.GetValue<string>());
        Assert.Empty(port.Actions);
    }
    [Fact] public void FishingPreflightRejectionsRetryOnFreshFramesThenKeepTheTaskRunning()
    {
        var port=new FishingPort{Frame=new(1,"1","fishing",FishingButton:true),Error=new InputRejectedBeforeDispatchException("target changed")};var store=new MemoryStore();
        var saved=new IdleFishingRunner(new("lake",30),port,store).Snapshot();saved["phase"]="refresh";store.Flush(saved);
        var runner=new IdleFishingRunner(new("lake",30),port,store);runner.Resume();
        for(var i=1;i<=4;i++){port.Frame=port.Frame with{At=i,CaptureId=i.ToString()};runner.Tick();}
        Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());
        Assert.Equal("cast_preflight_retry_limit",runner.Snapshot()["reason"]!.GetValue<string>());
        Assert.Equal(3,runner.Snapshot()["cast_preflight_rejections"]!.GetValue<int>());
        Assert.NotNull(runner.Snapshot()["last_cast_elapsed_ms"]);
        Assert.Null(runner.Snapshot()["pending"]);Assert.Empty(port.Actions);
    }
    [Fact]
    public void MovementActivationCastsInSameTickAfterFreshStableFrame()
    {
        var port=new FishingPort();var store=new MemoryStore();var plan=new FishingPlan("lake",30);
        var runner=new IdleFishingRunner(plan,port,store);var saved=runner.Snapshot();saved["phase"]="refresh";store.Flush(saved);
        runner=new(plan,port,store);runner.Resume();
        port.Frames.Enqueue(new(1,"compass",Compass:true));
        port.Frames.Enqueue(new(1.1,"transition","fishing",Stable:false,FishingButton:true));
        port.Frames.Enqueue(new(1.2,"cast","fishing",FishingButton:true));
        runner.Tick();
        Assert.Equal(new[]{"move","start"},port.Actions.Select(a=>a.Kind));
        Assert.True(port.CastDuringMovement);Assert.False(port.Moving);
        Assert.Equal("cast",runner.Snapshot()["pending"]!["capture_id"]!.GetValue<string>());
        Assert.Equal(0,runner.Snapshot()["submitted"]!.GetValue<int>());
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public void MovementActivationWaitsThroughFirstFreshFrameWithoutCasting(bool compass)
    {
        var port=new FishingPort();var store=new MemoryStore();var plan=new FishingPlan("lake",30);
        var runner=new IdleFishingRunner(plan,port,store);var saved=runner.Snapshot();saved["phase"]="refresh";store.Flush(saved);
        runner=new(plan,port,store);runner.Resume();
        port.Frames.Enqueue(new(1,"before",Compass:true));
        port.Frames.Enqueue(new(1.1,"movement_not_rendered",Compass:compass));
        port.Frames.Enqueue(new(1.2,"cast","fishing",FishingButton:true));
        runner.Tick();Assert.Equal(new[]{"move","start"},port.Actions.Select(a=>a.Kind));
        Assert.Equal("cast",runner.Snapshot()["pending"]!["capture_id"]!.GetValue<string>());
        Assert.Equal(0,runner.Snapshot()["submitted"]!.GetValue<int>());
    }
    [Theory]
    [InlineData("scene",true,true,false)]
    [InlineData("fishing",true,false,false)]
    [InlineData("fishing",true,true,true)]
    [InlineData("fishing",false,true,false)]
    public void MovementActivationNeverCastsOnMissingInactiveOrChangedPage(string page,bool button,bool foreground,bool active)
    {
        var port=new FishingPort();var store=new MemoryStore();var plan=new FishingPlan("lake",30);
        var runner=new IdleFishingRunner(plan,port,store);var saved=runner.Snapshot();saved["phase"]="refresh";store.Flush(saved);
        runner=new(plan,port,store);runner.Resume();
        port.Frames.Enqueue(new(1,"compass",Compass:true));
        port.Frames.Enqueue(new(1.1,"after",page,Foreground:foreground,FishingButton:button,RoundActive:active));
        runner.Tick();Assert.Equal("move",Assert.Single(port.Actions).Kind);Assert.Null(runner.Snapshot()["pending"]);
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void NicknamePreparationDoesNotOpenSettingsDuringExistingFishingInteraction(bool active, bool confirm)
    {
        var port=new FishingPort();var store=new MemoryStore();var plan=new FishingPlan("lake",30);
        var runner=new IdleFishingRunner(plan,port,store);var saved=runner.Snapshot();saved["phase"]="refresh";store.Flush(saved);
        runner=new(plan,port,store);runner.Resume();
        port.Frame=new(1,"active","fishing",RoundActive:active,ModeConfirm:confirm,PreparationComplete:false);
        runner.Tick();
        Assert.Empty(port.Actions);
        Assert.Null(runner.Snapshot()["pending"]);
    }
    [Fact] public void LegacyPreparationFlagDoesNotDelayTravelAfterMaintenanceYield()
    {
        var port=new FishingPort{Frame=new(1,"a",PreparationComplete:false)};var runner=new IdleFishingRunner(new("lake",30),port,new MemoryStore());runner.Tick();
        Assert.Equal("navigate",Assert.Single(port.Actions).Kind);Assert.Null(runner.Snapshot()["pending"]);
        port.Frame=port.Frame with{At=2,CaptureId="b",PreparationComplete=true,NavigationActive=false};runner.Tick();Assert.Equal("navigate",port.Actions.Last().Kind);
        runner.RequestYield();port.Frame=port.Frame with{At=3,CaptureId="c"};runner.Tick();Assert.True(runner.SafeToSwitch);runner.Resume();Assert.Equal(1,port.PreparationInvalidations);
    }
    [Fact] public void FishingRejectBeforeDispatchRefreshesButUnknownNativeFailureKeepsIntent()
    {
        var port=new FishingPort();var store=new MemoryStore();var plan=new FishingPlan("lake",30);var runner=new IdleFishingRunner(plan,port,store);
        var saved=runner.Snapshot();saved["phase"]="refresh";store.Flush(saved);runner=new(plan,port,store);runner.Resume();
        port.Frame=new(1,"a","fishing",FishingButton:true);port.Error=new InputRejectedBeforeDispatchException("changed");runner.Tick();
        Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("refresh",runner.Snapshot()["phase"]!.GetValue<string>());Assert.Empty(port.Actions);
        port.Frame=port.Frame with{At=2,CaptureId="b"};port.Error=new InvalidOperationException("native failure");Assert.Throws<InvalidOperationException>(()=>runner.Tick());
        Assert.NotNull(runner.Snapshot()["pending"]);Assert.Equal("unknown",runner.Snapshot()["status"]!.GetValue<string>());
    }
    [Fact] public void FishingDismountsOnceBeforeMovementOrCastingAndWaitsForVisibleResult()
    {
        var port=new FishingPort();var store=new MemoryStore();var plan=new FishingPlan("lake",30);var runner=new IdleFishingRunner(plan,port,store);
        var saved=runner.Snapshot();saved["phase"]="refresh";store.Flush(saved);runner=new(plan,port,store);runner.Resume();
        port.Frame=new(1,"a",Compass:true,Mounted:true);runner.Tick();port.Frame=port.Frame with{At=2,CaptureId="b"};runner.Tick();
        Assert.Equal("dismount",Assert.Single(port.Actions).Kind);
        port.Frame=new(3,"c","fishing",FishingButton:true);runner.Tick();Assert.Equal(new[]{"dismount","start"},port.Actions.Select(a=>a.Kind));
    }
    [Fact] public void FishingRestartRebuildsOnlyUnsentMenuNavigationAndClearsFinishedPriorityRequest()
    {
        var plan=new FishingPlan("lake",30);var store=new MemoryStore();var port=new FishingPort();var runner=new IdleFishingRunner(plan,port,store);
        runner.Tick();runner.RequestYield();Assert.Single(port.Actions);
        runner=new(plan,port,store);runner.RequestYield(false);runner.Resume();port.Frame=new(5,"fresh");runner.Tick();
        Assert.Equal(new[]{"navigate","navigate"},port.Actions.Select(a=>a.Kind));Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());
    }
    [Fact] public void FishingYieldBeforeTravelDoesNotSendMovementToMenu()
    {
        var port=new FishingPort{Frame=new(1,"1","character_menu")};var runner=new IdleFishingRunner(new("lake",30),port,new MemoryStore());
        runner.RequestYield();runner.Tick();Assert.Empty(port.Actions);Assert.True(runner.SafeToSwitch);
    }
    [Theory]
    [InlineData("a", "w", "s")]
    [InlineData("d", "s", "w")]
    [InlineData("left", "w", "s")]
    [InlineData("right", "s", "w")]
    [InlineData("w", "w", "s")]
    [InlineData("s", "s", "w")]
    public void FishingMovementUsesWsAndRestoresLegacyDirection(string savedDirection, string first, string second)
    {
        var port=new FishingPort();var store=new MemoryStore();var plan=new FishingPlan("lake",60);
        var runner=new IdleFishingRunner(plan,port,store);var saved=runner.Snapshot();
        saved["phase"]="refresh";saved["next_direction"]=savedDirection;saved["elapsed_s"]=12d;store.Flush(saved);
        runner=new(plan,port,store);runner.Resume();port.Frame=new(1,"2",Compass:true);runner.Tick();
        port.Frame=new(1.3,"3",Compass:true);runner.Tick();
        Assert.Equal(new[]{first,second},port.Actions.Select(a=>a.Direction));
        Assert.All(port.Actions,a=>{Assert.Equal("move",a.Kind);Assert.Equal(.06,a.DurationSeconds);});
        Assert.Equal(12,runner.Snapshot()["elapsed_s"]!.GetValue<double>());
    }
    [Fact] public void PausedTransferDialogCannotResumeConfirmationByObservation()
    {
        var port=new StoragePort();var runner=new StorageSupplyRunner([new(1,"ore",3,"shared")],port,new MemoryStore());runner.Tick();runner.Pause();
        port.Frame=port.Frame with{At=101,CaptureId="2",TransferDialog=true,DialogQuantity=3,ConfirmAvailable=true};runner.ObserveOnly();
        Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("paused",runner.Snapshot()["status"]!.GetValue<string>());Assert.Equal("manual",runner.Snapshot()["pause_reason"]!.GetValue<string>());
        runner.Tick();Assert.Single(port.Actions);runner.Resume();port.Frame=port.Frame with{At=102,CaptureId="3"};runner.Tick();Assert.Equal(2,port.Actions.Count);
        runner.Pause();port.Frame=port.Frame with{At=103,CaptureId="4",Available=7,BagQuantity=5,BalanceAt=103};runner.ObserveOnly();runner.ObserveOnly();
        Assert.Equal("complete",runner.Snapshot()["status"]!.GetValue<string>());Assert.Single(runner.Snapshot()["completed"]!.AsArray());Assert.Equal(2,port.Actions.Count);
    }
    [Fact] public void FishingYieldResumesFromSameProgressWhenScheduledAgain()
    {
        var port=new FishingPort();var store=new MemoryStore();var plan=new FishingPlan("lake",30);var runner=new IdleFishingRunner(plan,port,store);
        var saved=runner.Snapshot();saved["status"]="yielded";saved["phase"]="yielded";saved["elapsed_s"]=10d;store.Flush(saved);runner=new(plan,port,store);
        var dispatcher=new WorkflowDispatcher();dispatcher.Continue();dispatcher.Register("fish",runner);
        dispatcher.Tick([new("fish","idle_fishing",null,true,0,0)],null);Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());Assert.Equal(10,runner.Snapshot()["elapsed_s"]!.GetValue<double>());
        dispatcher.Tick([new("fish","idle_fishing",null,true,0,0)],null);Assert.Equal("navigate",Assert.Single(port.Actions).Kind);
    }
    [Fact] public void FishingKeepsObservingUnknownPageAndResumesWhenStable()
    {
        var time=new ManualTime();var port=new FishingPort{Frame=new(0,"1",Stable:false)};
        var runner=new IdleFishingRunner(new("lake",30),port,new MemoryStore(),time:time);
        runner.Tick();Assert.Empty(port.Actions);Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());
        time.Seconds+=16;port.Frame=port.Frame with{At=16,CaptureId="2"};runner.Tick();
        Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());
        port.Frame=port.Frame with{At=17,Stable=true,CaptureId="3"};runner.Tick();Assert.Single(port.Actions);
    }
    [Fact] public void FishingCastWaitHasNoBiteDeadlineAndNeverReplays()
    {
        var port=new FishingPort();var runner=new IdleFishingRunner(new("lake",30),port,new MemoryStore());
        runner.Tick();port.Frame=new(1,"2",NavigationActive:true);runner.Tick();port.Frame=new(2,"3",NavigationActive:false);runner.Tick();
        port.Frame=new(3,"4","fishing",FishingButton:true);runner.Tick();
        port.Frame=port.Frame with{At=19,CaptureId="5"};runner.Tick();Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());Assert.NotNull(runner.Snapshot()["pending"]);Assert.Equal(2,port.Actions.Count);
        port.Frame=new(20,"6","fishing",RoundActive:true,LeisureMode:false);runner.ObserveOnly();
        Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("paused",runner.Snapshot()["status"]!.GetValue<string>());
        port.Frame=new(25,"7",Compass:true);runner.ObserveOnly();
        Assert.True(runner.SafeToSwitch);Assert.Equal(1,runner.Snapshot()["completed"]!.GetValue<int>());Assert.Equal(0,runner.Snapshot()["elapsed_s"]!.GetValue<double>());Assert.Equal(2,port.Actions.Count);
    }
    [Fact] public void FishingFocusReturnRequiresExplicitContinue()
    {
        var port=new FishingPort{Frame=new(0,"1",Foreground:false)};var runner=new IdleFishingRunner(new("lake",30),port,new MemoryStore());runner.Tick();
        port.Frame=new(1,"2");runner.Tick();Assert.Empty(port.Actions);Assert.Equal("paused",runner.Snapshot()["status"]!.GetValue<string>());
        runner.Resume();runner.Tick();Assert.Single(port.Actions);
    }
    [Fact] public void DispatcherObservesHeldFishingThroughRoundEndWithoutRecasting()
    {
        var port=new FishingPort();var runner=new IdleFishingRunner(new("lake",30),port,new MemoryStore());
        runner.Tick();port.Frame=new(1,"2",NavigationActive:true);runner.Tick();port.Frame=new(2,"3",NavigationActive:false);runner.Tick();
        port.Frame=new(3,"4","fishing",FishingButton:true);runner.Tick();
        var dispatcher=new WorkflowDispatcher();dispatcher.Register("fish",runner);dispatcher.Continue();dispatcher.Tick([new("fish","idle_fishing",null,true,0,0)],null);
        port.Frame=port.Frame with{At=19,CaptureId="5"};dispatcher.Tick([new("fish","idle_fishing",null,true,0,0)],null);
        // A task-local hold permits passive accounting; a manual pause stops
        // the dispatcher and requires explicit continue.
        dispatcher.SetHeld("fish",true);port.Frame=new(20,"6","fishing",RoundActive:true,LeisureMode:false);dispatcher.Tick([new("fish","idle_fishing",null,true,0,0)],null);
        Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("paused",runner.Snapshot()["status"]!.GetValue<string>());
        port.Frame=new(25,"7",Compass:true);dispatcher.Tick([new("fish","idle_fishing",null,true,0,0)],null);
        Assert.True(runner.SafeToSwitch);Assert.Equal(2,port.Actions.Count);Assert.False(dispatcher.IsPaused);
    }
    [Fact] public void StorageFirstUnstableFrameDoesNotPauseOrTransfer()
    {
        var port=new StoragePort();port.Frame=port.Frame with{Stable=false};var runner=new StorageSupplyRunner([new(1,"ore",3,"shared")],port,new MemoryStore());
        runner.Tick();Assert.Empty(port.Actions);Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());
        port.Frame=port.Frame with{Stable=true,CaptureId="2"};runner.Tick();Assert.Single(port.Actions);
    }
    [Fact] public void StorageUnknownPageTimesOutAndManualPauseIsRetained()
    {
        var time=new ManualTime();var port=new StoragePort();port.Frame=port.Frame with{Stable=false,Page="unknown"};
        var runner=new StorageSupplyRunner([new(1,"ore",3,"shared")],port,new MemoryStore(),time:time);runner.Tick();time.Seconds+=16;runner.Tick();
        Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());time.Seconds+=30;runner.Tick();
        Assert.Equal("unrecognized_page",runner.Snapshot()["pause_reason"]!.GetValue<string>());Assert.Empty(port.Actions);
        Assert.Empty(port.GlobalPauses);
        runner.Resume();runner.Tick();Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());
        runner.Pause();time.Seconds+=16;runner.Tick();Assert.Equal("manual",runner.Snapshot()["pause_reason"]!.GetValue<string>());
    }
    [Fact]
    public void TransferNeedsBothScopedBalanceDeltasAndFreshCapture()
    {
        var store = new MemoryStore(); var port = new StoragePort();
        var runner = new StorageSupplyRunner([new(1, "ore", 3, "shared")], port, store);
        runner.Tick(); Assert.Equal("prepare_transfer", store.Value!["pending"]!["kind"]!.GetValue<string>());
        port.Frame = port.Frame with { At = 101, CaptureId = "2", Available = 7, BagQuantity = 5, BalanceAt = 101 }; runner.Tick();
        Assert.Equal("complete", runner.Snapshot()["status"]!.GetValue<string>()); Assert.Single(port.Actions);
    }
    [Fact]
    public void TransferMissingPacketBalancesNeverBecomeZeroOrReplay()
    {
        var port=new StoragePort();port.Frame=port.Frame with{Available=null,BagQuantity=null};
        var runner=new StorageSupplyRunner([new(1,"ore",3,"shared")],port,new MemoryStore());runner.Tick();
        port.Frame=port.Frame with{At=116,CaptureId="2",Available=0,BagQuantity=3};runner.Tick();runner.Tick();
        Assert.Equal("unknown",runner.Snapshot()["status"]!.GetValue<string>());Assert.NotNull(runner.Snapshot()["pending"]);Assert.Single(port.Actions);
        Assert.Empty(port.GlobalPauses);Assert.Contains("reconciliation_required",port.LocalPauses);
    }
    [Fact]
    public void StorageRestartDoesNotRepeatUncertainTransfer()
    {
        var store = new MemoryStore(); var port = new StoragePort(); var plan = new WithdrawalPlan(1, "ore", 3, "shared");
        new StorageSupplyRunner([plan], port, store).Tick();
        var runner = new StorageSupplyRunner([plan], port, store); runner.Resume();
        port.Frame = port.Frame with { At = 200, CaptureId = "2", TransferComplete = true }; runner.Tick(); runner.Tick();
        Assert.Single(port.Actions); Assert.Equal("complete", runner.Snapshot()["status"]!.GetValue<string>());
    }
    [Fact]
    public void FishingUsesFocusedActiveTimeAndWaitsForRoundBoundary()
    {
        var port = new FishingPort(); var store = new MemoryStore(); var runner = new IdleFishingRunner(new("lake", 2), port, store);
        runner.Tick(); port.Frame = new(1, "2", NavigationActive: true); runner.Tick();
        port.Frame = new(2, "3", NavigationActive: false); runner.Tick();
        port.Frame = new(3, "4", "fishing", FishingButton: true); runner.Tick();
        Assert.Equal("start", store.Value!["pending"]!["kind"]!.GetValue<string>());
        port.Frame = new(4, "5", "fishing", LeisureMode: false, RoundActive: true); runner.Tick();
        port.Frame = port.Frame with { At = 6, CaptureId = "6" }; runner.Tick();
        Assert.NotEqual("complete", runner.Snapshot()["status"]!.GetValue<string>());
        port.Frame = new(7, "7", FishingButton: true, Compass: true); runner.Tick();
        Assert.Equal("complete", runner.Snapshot()["status"]!.GetValue<string>());
        Assert.Equal(1, port.Actions.Count(x => x.Kind == "start"));
    }
}
internal sealed class StoragePort : IStorageSupplyPort
{
    public bool LeaveAllowed;public int Releases;public bool CanLeaveAfterTransfer=>LeaveAllowed;public bool ReleaseAllowed;public bool CanReleaseForeground()=>ReleaseAllowed;
    public bool ReleaseNavigation(){Releases++;return true;}
    public List<string> GlobalPauses=[];public List<string> LocalPauses=[];
    public Exception? Error;
    public WithdrawalFrame Frame = new(100, "1", "storage", Scope: "shared", ItemId: 1, Name: "ore", Available: 10, BagQuantity: 2, BagScope: "bag:1", TransferAvailable: true, BalanceSource: "packet_capture", BalanceAt: 100);
    public List<WithdrawalAction> Actions = []; public WithdrawalFrame Observe() => Frame;
    public void Perform(WithdrawalAction action) {if(Error is{} error)throw error;Actions.Add(action);} public void Pause(string reason) { GlobalPauses.Add(reason); } public void PauseLocal(string reason){LocalPauses.Add(reason);} public void Resume() { }
}
internal sealed class FishingPort : IFishingPort
{
    public bool Moving,CastDuringMovement;
    public void MoveWhileObserving(FishingAction action,Func<FishingFrame,bool> observe)
    {Perform(action);Moving=true;try{for(var i=0;i<2;i++)if(observe(Observe()))break;}finally{Moving=false;}}
    public Exception? Error;
    public int PreparationInvalidations;public void InvalidatePreparation()=>PreparationInvalidations++;
    public FishingFrame Frame = new(0, "1"); public Queue<FishingFrame> Frames=[]; public List<FishingAction> Actions = []; public FishingFrame Observe() => Frames.Count>0?Frame=Frames.Dequeue():Frame;
    public void Perform(FishingAction action) {if(Error is {} e)throw e;if(action.Kind=="start")CastDuringMovement=Moving;Actions.Add(action);} public void Pause(string reason) { } public void Resume() { }
}
