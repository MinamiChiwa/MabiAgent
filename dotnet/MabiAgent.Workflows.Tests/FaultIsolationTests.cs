using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Workflows.Tests;

public sealed class FaultIsolationTests
{
    private static ProductionRunner PendingCollection(ProductionPort port,MemoryStore store,ManualTime time)
    {
        var runner=new ProductionRunner(port,store,new("a","wood",60),time:time);var state=runner.Snapshot();
        state["submitted"]=1;state["owned_slots"]=new JsonArray(0);state["status"]="unknown";state["phase"]="confirm_collect";
        state["pending"]=new JsonObject{["kind"]="collect",["before_empty"]=new JsonArray(),["target_slots"]=new JsonArray(0),["at"]=0d,["deadline"]=10d,["collection_id"]="original-collect"};
        store.Flush(state);return new(port,store,new("a","wood",60),time:time);
    }
    [Fact] public void RestartedCollectionReturnsToDeviceAndConfirmsOriginalIntentWithoutReplay()
    {
        var time=new ManualTime();var port=new ProductionPort{RemotePreview=true,Frame=new("scene",AtDevice:false)};var store=new MemoryStore();
        var runner=PendingCollection(port,store,time);var pending=runner.Snapshot()["pending"]!.DeepClone();
        var dispatcher=new WorkflowDispatcher(time);dispatcher.Register("a",runner,"wood");dispatcher.RestoreOwnership("a");dispatcher.Continue();
        WorkCandidate[] plan=[new("a","production","wood",true,5,0)];
        dispatcher.Tick(plan,null);dispatcher.Tick(plan,null);Assert.Equal("queue",Assert.Single(port.Navigation));
        port.Frame=new("wood_panel",EmptySlots:[0],SlotCount:1,AtDevice:false);dispatcher.Tick(plan,null);
        Assert.True(JsonNode.DeepEquals(pending,runner.Snapshot()["pending"]));Assert.Empty(port.Clicks);
        port.Frame=port.Frame with{AtDevice=true};dispatcher.Tick(plan,"wood");
        Assert.Null(runner.Snapshot()["pending"]);Assert.Equal(1,runner.Snapshot()["completed"]!.GetValue<int>());Assert.Empty(port.Clicks);
        Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());
    }
    [Fact] public void UnconfirmedCollectionLeavesDeviceAndReleasesIndependentTask()
    {
        var time=new ManualTime();var port=new ProductionPort{Frame=new("wood_panel",EmptySlots:[],ReadySlots:[0],SlotCount:1)};var store=new MemoryStore();
        var runner=PendingCollection(port,store,time);var pending=runner.Snapshot()["pending"]!.DeepClone();
        Assert.False(runner.RecoverCollection());Assert.Equal("leave",Assert.Single(port.Navigation));
        port.Frame=new("scene",AtDevice:false);Assert.True(runner.RecoverCollection());Assert.True(runner.SafeToSwitch);
        Assert.True(JsonNode.DeepEquals(pending,runner.Snapshot()["pending"]));Assert.Equal(0,runner.Snapshot()["completed"]!.GetValue<int>());Assert.Empty(port.Clicks);
        var otherPort=new ProductionPort{Frame=new("metal_panel",EmptySlots:[0],SlotCount:1)};
        var dispatcher=new WorkflowDispatcher(time);dispatcher.Register("a",runner,"wood");dispatcher.Register("b",new ProductionRunner(otherPort,new MemoryStore(),new("b","metal",60),time:time),"metal");
        dispatcher.RestoreOwnership("a");dispatcher.Continue(false);
        WorkCandidate[] plan=[new("a","production","wood",true,5,0),new("b","production","metal",true,5,1)];
        for(var i=0;i<4;i++)dispatcher.Tick(plan,null);
        Assert.Contains("recipe",otherPort.Navigation);Assert.Empty(port.Clicks);
    }
    [Fact] public void CollectionRecoveryBudgetSurvivesReconstructionAndPausedObservationIsReadOnly()
    {
        var time=new ManualTime();var port=new ProductionPort{Frame=new("scene",AtDevice:false)};var store=new MemoryStore();
        var runner=PendingCollection(port,store,time);runner.RecoverCollection();var deadline=runner.Snapshot()["collection_recovery_deadline"]!.GetFiniteDouble();
        runner=new(port,store,new("a","wood",60),time:time);runner.ObserveOnly();Assert.Single(port.Navigation);
        time.Seconds=deadline+1;port.Frame=new("unknown",Stable:false,AtDevice:false);runner.RecoverCollection();
        Assert.Equal("collection_recovery_timeout",runner.Snapshot()["reason"]!.GetValue<string>());Assert.NotNull(runner.Snapshot()["pending"]);
        time.Seconds+=16;Assert.True(runner.RecoverCollection());Assert.Single(port.Navigation);Assert.Empty(port.Clicks);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void CollectionRecheckWithNoOwnedWorkRefillsAvailableSlots(bool foreignReady)
    {
        var store=new MemoryStore();var time=new ManualTime();
        var port=new ProductionPort{Frame=new("metal_panel",EmptySlots:foreignReady?[1]:[0,1],ReadySlots:foreignReady?[0]:[],CollectAvailable:foreignReady,SlotCount:2)};
        var runner=new ProductionRunner(port,store,new("a","metal",60),time:time);var state=runner.Snapshot();
        state["phase"]="collect";state["status"]="running";state["reason"]="collection_owner_unverified";store.Flush(state);
        runner=new(port,store,new("a","metal",60),time:time);runner.Resume(true);runner.Tick();
        if(foreignReady)
        {
            Assert.Equal("collect",Assert.Single(port.Clicks));port.Frame=port.Frame with{EmptySlots=[0,1],ReadySlots=[]};runner.Tick();
            Assert.Equal(0,runner.Snapshot()["completed"]!.GetValue<int>());Assert.Null(runner.Snapshot()["pending"]);return;
        }
        runner.Tick();
        Assert.Equal("recipe",runner.Snapshot()["phase"]!.GetValue<string>());Assert.Contains("recipe",port.Navigation);Assert.Empty(port.Clicks);
        Assert.Equal(0,runner.Snapshot()["completed"]!.GetValue<int>());Assert.Empty(runner.Snapshot()["owned_slots"]!.AsArray());
    }
    [Fact] public void DeviceTravelTimeoutPreservesPreviouslySubmittedSlotsAndTiming()
    {
        var time=new ManualTime{Seconds=0};var port=new FaultPort{Releasable=true,Frame=new("scene",AtDevice:false,NavigationActive:true)};var store=new MemoryStore();
        var runner=new ProductionRunner(port,store,new("a","metal",60),2,time);var state=runner.Snapshot();
        state["submitted"]=1;state["owned_slots"]=new JsonArray(0);state["production_due_at"]=300d;state["due_at"]=300d;store.Flush(state);
        runner=new ProductionRunner(port,store,new("a","metal",60),2,time);runner.Resume(true);runner.Tick();
        time.Seconds=121;Assert.Throws<TimeoutException>(()=>runner.Tick());Assert.True(runner.CanIsolateFault);
        var after=runner.Snapshot();Assert.Equal(1,after["submitted"]!.GetValue<int>());Assert.Equal(0,Assert.Single(after["owned_slots"]!.AsArray())!.GetValue<int>());
        Assert.Equal(300d,after["production_due_at"]!.GetFiniteDouble());Assert.Null(after["pending"]);Assert.Equal(0,port.Crafts);
    }

    [Fact] public void NativeDeviceTravelHasOneBudgetAcrossAlternatingPagesAndReleasesDispatcher()
    {
        var time=new ManualTime{Seconds=0};var bad=new FaultPort{Releasable=true,Frame=new("scene",AtDevice:false,NavigationActive:true)};var good=new FaultPort();
        var runner=new ProductionRunner(bad,new MemoryStore(),new("a","metal",60),time:time);
        var other=new ProductionRunner(good,new MemoryStore(),new("b","wood",60),time:time);
        var dispatcher=new WorkflowDispatcher(time);dispatcher.Register("a",runner,"metal");dispatcher.Register("b",other,"wood");dispatcher.Continue();
        var plan=new[]{new WorkCandidate("a","production","metal",true,5,0),new WorkCandidate("b","production","wood",true,5,1)};
        dispatcher.Tick(plan,null);dispatcher.Tick(plan,null);
        foreach(var seconds in new[]{30d,60d,90d})
        {
            time.Seconds=seconds;bad.Frame=bad.Frame with{Page=seconds==60?"recipe_detail":"unknown",Stable=seconds==60};dispatcher.Tick(plan,null);
        }
        time.Seconds=121;dispatcher.Tick(plan,null);
        Assert.Contains("a",dispatcher.Faults.Keys);Assert.Null(dispatcher.ActiveId);Assert.False(dispatcher.IsPaused);
        dispatcher.Tick(plan,null);dispatcher.Tick(plan,null);Assert.True(good.Navigations>0);Assert.Equal(0,bad.Crafts);Assert.Null(runner.Snapshot()["pending"]);
    }

    [Fact] public void CompactedCollectionReconcilesOriginalIntentAndMovesPeerSlotsOnce()
    {
        var time=new ManualTime();var store=new MemoryStore();var port=new ProductionPort{Frame=new("wood_panel",EmptySlots:[3,4],ReadySlots:[],SlotCount:5)};
        IReadOnlyDictionary<string,int[]> Owners()=>new Dictionary<string,int[]>{{"a",[0]},{"b",[1,2,3]}};
        var runner=new ProductionRunner(port,store,new("a","wood",60),time:time,collectionOwners:Owners);var state=runner.Snapshot();
        state["submitted"]=1;state["owned_slots"]=new JsonArray(0);state["device_slot_count"]=5;state["status"]="unknown";state["phase"]="confirm_collect";
        state["pending"]=new JsonObject{["kind"]="collect",["before_empty"]=new JsonArray(4),["target_slots"]=new JsonArray(0),["at"]=0d,["deadline"]=10d,["collection_id"]="compact-one",["collection_owners"]=new JsonObject{["a"]=new JsonArray(0)}};store.Flush(state);
        runner=new(port,store,new("a","wood",60),time:time,collectionOwners:Owners);Assert.True(runner.ReconcileVisible());Assert.Null(runner.Snapshot()["pending"]);Assert.Equal(1,runner.Snapshot()["completed"]!.GetValue<int>());Assert.Empty(port.Clicks);
        var receipt=Assert.Single(runner.Snapshot()["collection_receipts"]!.AsArray())!;Assert.True(receipt["compact"]!.GetValue<bool>());Assert.NotNull(receipt["owners"]!["b"]);
        var peerStore=new MemoryStore();var peer=new ProductionRunner(new ProductionPort(),peerStore,new("b","wood",60),3,time);var saved=peer.Snapshot();saved["submitted"]=3;saved["owned_slots"]=new JsonArray(1,2,3);saved["slot_due_at"]=new JsonObject{["1"]=200d,["2"]=300d,["3"]=400d};saved["phase"]="waiting";saved["status"]="waiting";saved["due_at"]=400d;peerStore.Flush(saved);
        peer=new(new ProductionPort(),peerStore,new("b","wood",60),3,time);Assert.True(peer.ReconcilePeerCollection([0],"compact-one",true));Assert.Equal(new[]{0,1,2},peer.Snapshot()["owned_slots"]!.AsArray().Select(n=>n!.GetValue<int>()));Assert.Equal(200,peer.EarliestCollectionAt);Assert.Equal(0,peer.Snapshot()["completed"]!.GetValue<int>());
        peer=new(new ProductionPort(),peerStore,new("b","wood",60),3,time);Assert.False(peer.ReconcilePeerCollection([0],"compact-one",true));Assert.Equal(new[]{0,1,2},peer.Snapshot()["owned_slots"]!.AsArray().Select(n=>n!.GetValue<int>()));Assert.True(peer.HasAppliedCollection("compact-one"));
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void LegacyApprovalExpiryDoesNotBlockCollectionAndOnlyOwnedSlotsAreCredited(bool valid)
    {
        var time=new ManualTime();var store=new MemoryStore();var port=new ProductionPort{Frame=new("metal_panel",EmptySlots:[],ReadySlots:[0,1],CollectAvailable:true,SlotCount:2)};
        var runner=new ProductionRunner(port,store,new("a","metal",60),time:time);var state=runner.Snapshot();
        state["submitted"]=1;state["owned_slots"]=new JsonArray(1);state["phase"]="collect";state["status"]="running";
        state["collect_all_once"]=new JsonObject{["source"]="user_explicit",["device"]="metal",["expires_at"]=valid?10000d:0d};store.Flush(state);
        runner=new(port,store,new("a","metal",60),time:time);runner.Resume(true);runner.Tick();
        Assert.Equal("collect",Assert.Single(port.Clicks));Assert.Null(store.Read()!["collect_all_once"]);
        Assert.Equal(new[]{0,1},store.Read()!["pending"]!["target_slots"]!.AsArray().Select(n=>n!.GetValue<int>()));
        port.Frame=port.Frame with{EmptySlots=[0,1],ReadySlots=[]};runner.Tick();
        Assert.Equal(1,runner.Snapshot()["completed"]!.GetValue<int>());Assert.Null(runner.Snapshot()["pending"]);
    }

    [Fact] public void EmptyOwnedSlotWithoutCollectionIntentNeverCreditsOutput()
    {
        var store=new MemoryStore();var port=new ProductionPort{Frame=new("metal_panel",EmptySlots:[0],SlotCount:1)};var time=new ManualTime();
        var runner=new ProductionRunner(port,store,new("a","metal",60),time:time);var state=runner.Snapshot();
        state["submitted"]=1;state["owned_slots"]=new JsonArray(0);state["phase"]="collect";state["status"]="running";store.Flush(state);
        runner=new(port,store,new("a","metal",60),time:time);runner.Resume(true);runner.Tick();
        Assert.Equal("collection_receipt_missing",runner.Snapshot()["reason"]!.GetValue<string>());Assert.Equal(0,runner.Snapshot()["completed"]!.GetValue<int>());
        Assert.Single(runner.Snapshot()["owned_slots"]!.AsArray());Assert.Empty(port.Clicks);
        runner.ObserveOnly();runner.Resume(true);Assert.Equal(0,runner.Snapshot()["completed"]!.GetValue<int>());Assert.Empty(port.Clicks);
    }
    [Fact] public void MissingCollectionReceiptKeepsEvidenceButReleasesIndependentDevice()
    {
        var time=new ManualTime();var store=new MemoryStore();var port=new ProductionPort{ReleaseAllowed=true,Frame=new("metal_panel",EmptySlots:[0],SlotCount:1)};
        var runner=new ProductionRunner(port,store,new("a","metal",60),time:time);var state=runner.Snapshot();
        state["submitted"]=1;state["owned_slots"]=new JsonArray(0);state["phase"]="collect";state["status"]="running";store.Flush(state);
        runner=new(port,store,new("a","metal",60),time:time);runner.Resume(true);runner.Tick();
        Assert.Equal("unknown",runner.Snapshot()["status"]!.GetValue<string>());Assert.Null(runner.Snapshot()["pending"]);
        Assert.True(runner.SafeToSwitch);Assert.Single(runner.Snapshot()["owned_slots"]!.AsArray());Assert.Equal(0,runner.Snapshot()["completed"]!.GetValue<int>());
        var otherPort=new ProductionPort{Frame=new("wood_panel",EmptySlots:[0],SlotCount:1)};
        var other=new ProductionRunner(otherPort,new MemoryStore(),new("b","wood",60),time:time);
        var dispatcher=new WorkflowDispatcher(time);dispatcher.Register("a",runner,"metal");dispatcher.Register("b",other,"wood");dispatcher.Continue();
        WorkCandidate[] plan=[new("a","production","metal",true,5,0),new("b","production","wood",true,5,1)];
        for(var i=0;i<4;i++)dispatcher.Tick(plan,null);
        Assert.Empty(port.Clicks);Assert.Contains("recipe",otherPort.Navigation);Assert.False(dispatcher.IsPaused);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void OccupiedDeviceCollectsFinishedSlotsAndReleasesOtherDevices(bool ready)
    {
        var time=new ManualTime();var native=new ProductionPort{Frame=new("metal_panel",EmptySlots:[],ReadySlots:ready?[0,1]:[],CollectAvailable:ready,SlotCount:2)};
        var otherPort=new ProductionPort{Frame=new("wood_panel",EmptySlots:[0],SlotCount:1)};
        var first=new ProductionRunner(native,new MemoryStore(),new("a","metal",60),time:time);
        var second=new ProductionRunner(otherPort,new MemoryStore(),new("b","wood",60),time:time);
        var dispatcher=new WorkflowDispatcher(time);dispatcher.Register("a",first,"metal");dispatcher.Register("b",second,"wood");dispatcher.Continue();
        WorkCandidate[] plan=[new("a","production","metal",true,5,0),new("b","production","wood",true,5,1)];
        for(var i=0;i<10;i++)
        {
            dispatcher.Tick(plan,null);
            if(native.Clicks.Count>0){first.StopSubmitting();native.Frame=native.Frame with{EmptySlots=[0,1],ReadySlots=[],CollectAvailable=false};}
            if(native.Navigation.Contains("leave"))native.Frame=new("scene");
        }
        Assert.Equal(ready?1:0,native.Clicks.Count);Assert.Contains("recipe",otherPort.Navigation);Assert.False(dispatcher.IsPaused);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void PlayerOccupiedSlotStillAllowsCraftInFreeSlotWithoutCollectingPlayerTask(bool ready)
    {
        var time=new ManualTime();var store=new MemoryStore();var port=new ProductionPort{Frame=new("metal_panel",EmptySlots:[1],ReadySlots:ready?[0]:[],CollectAvailable:ready,SlotCount:2)};
        var runner=new ProductionRunner(port,store,new("a","metal",60),time:time);
        runner.Resume(true);runner.Tick();Assert.Contains("recipe",port.Navigation);Assert.Empty(port.Clicks);
        port.Frame=port.Frame with{Page="recipe_detail",RecipeKey="a",CraftAvailable=true};runner.Tick();Assert.Equal("craft",Assert.Single(port.Clicks));
        port.Frame=port.Frame with{Page="metal_panel",EmptySlots=[],ReadySlots=ready?[0]:[]};runner.Tick();
        Assert.Equal(new[]{1},runner.Snapshot()["owned_slots"]!.AsArray().Select(n=>n!.GetValue<int>()));Assert.Equal(1,runner.Snapshot()["submitted"]!.GetValue<int>());
    }
    [Fact] public void SlotPredictionAllowsOnsitePartialCollectionBeforeWholeBatchTail()
    {
        var time=new ManualTime();var store=new MemoryStore();var port=new ProductionPort{Frame=new("metal_panel",EmptySlots:[],ReadySlots:[0],CollectAvailable:true,SlotCount:2)};
        var runner=new ProductionRunner(port,store,new("a","metal",60),2,time);var state=runner.Snapshot();state["submitted"]=2;state["owned_slots"]=new JsonArray(0,1);state["slot_due_at"]=new JsonObject{["0"]=90d,["1"]=200d};state["phase"]="waiting";state["due_at"]=200d;state["production_due_at"]=200d;store.Flush(state);
        runner=new(port,store,new("a","metal",60),2,time);runner.Resume(true);runner.Tick();Assert.Equal("collect",Assert.Single(port.Clicks));Assert.Equal(90,runner.EarliestCollectionAt);
        port.Frame=port.Frame with{EmptySlots=[0],ReadySlots=[]};runner.Tick();Assert.Equal(200,runner.EarliestCollectionAt);Assert.Equal(1,runner.Snapshot()["completed"]!.GetValue<int>());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void DueSlotNotYetReadyPreservesRetryAcrossRestart(bool legacy)
    {
        var time=new ManualTime();var store=new MemoryStore();var port=new ProductionPort{Frame=new("metal_panel",EmptySlots:[],ReadySlots:[],SlotCount:2)};
        var runner=new ProductionRunner(port,store,new("a","metal",60),2,time);var state=runner.Snapshot();
        state["submitted"]=2;state["owned_slots"]=new JsonArray(0,1);state["slot_due_at"]=new JsonObject{["0"]=90d,["1"]=200d};state["phase"]="waiting";state["due_at"]=200d;state["production_due_at"]=200d;store.Flush(state);
        runner=new(port,store,new("a","metal",60),2,time);runner.Resume(true);runner.Tick();runner.Tick();port.Frame=new("scene",AtDevice:false);runner.Tick();Assert.Equal("waiting",runner.Snapshot()["phase"]!.GetValue<string>());Assert.Equal(115,runner.NextCollectionAt(true));Assert.Empty(port.Clicks);
        state=runner.Snapshot();if(legacy)state.Remove("collection_retry_at");store.Flush(state);
        runner=new(port,store,new("a","metal",60),2,time);runner.Resume(true);var navigations=port.Navigation.Count;runner.Tick();Assert.Equal(navigations,port.Navigation.Count);Assert.Empty(port.Clicks);Assert.Equal(115,runner.NextCollectionAt(true));Assert.Equal(400,runner.NextCollectionAt());Assert.Equal(200,runner.Snapshot()["production_due_at"]!.GetFiniteDouble());
        port.Frame=new("scene",AtDevice:false);time.Seconds=400;runner.Tick();Assert.Equal("queue",port.Navigation.Last());
    }
    [Fact] public void RepeatedStableNavigationWithoutProgressTimesOutAndIsolates()
    {
        var time=new ManualTime();var port=new FaultPort{Releasable=true};var runner=new ProductionRunner(port,new MemoryStore(),new("a","metal",60),time:time);
        var dispatcher=new WorkflowDispatcher(time);dispatcher.Register("a",runner,"metal");dispatcher.Continue();WorkCandidate[] plan=[new("a","production","metal",true,5,0)];
        dispatcher.Tick(plan,null);dispatcher.Tick(plan,null);time.Seconds+=121;dispatcher.Tick(plan,null);Assert.Contains("a",dispatcher.Faults.Keys);Assert.False(dispatcher.IsPaused);Assert.Equal(0,port.Crafts);
    }
    [Fact] public void CollectAllJournalsPeersAndRestartAppliesPartialReceiptOnlyOnce()
    {
        var time=new ManualTime();var store=new MemoryStore();var port=new ProductionPort{Frame=new("metal_panel",EmptySlots:[],ReadySlots:[0,1],CollectAvailable:true,SlotCount:3)};
        IReadOnlyDictionary<string,int[]> Owners()=>new Dictionary<string,int[]>{{"a",[0]},{"b",[1,2]}};
        var runner=new ProductionRunner(port,store,new("a","metal",60),time:time,collectionOwners:Owners);var saved=runner.Snapshot();saved["submitted"]=1;saved["owned_slots"]=new JsonArray(0);saved["phase"]="waiting";saved["status"]="waiting";saved["due_at"]=90d;saved["production_due_at"]=90d;store.Flush(saved);
        runner=new(port,store,new("a","metal",60),time:time,collectionOwners:Owners);runner.Resume(true);runner.Tick();Assert.NotNull(store.Read()!["pending"]!["collection_owners"]!["b"]);
        port.Frame=port.Frame with{EmptySlots=[0,1],ReadySlots=[]};runner.Tick();Assert.Equal(1,runner.Snapshot()["completed"]!.GetValue<int>());
        var receipt=Assert.Single(runner.Snapshot()["collection_receipts"]!.AsArray())!;Assert.Equal(new[]{0,1},receipt["cleared"]!.AsArray().Select(n=>n!.GetValue<int>()));
        var peerStore=new MemoryStore();var peer=new ProductionRunner(new ProductionPort(),peerStore,new("b","metal",60),2,time);var peerState=peer.Snapshot();peerState["submitted"]=2;peerState["owned_slots"]=new JsonArray(1,2);peerState["phase"]="waiting";peerState["due_at"]=200d;peerStore.Flush(peerState);
        peer=new(new ProductionPort(),peerStore,new("b","metal",60),2,time);Assert.True(peer.ReconcilePeerCollection([1],receipt["id"]!.GetValue<string>()));
        peer=new(new ProductionPort(),peerStore,new("b","metal",60),2,time);Assert.False(peer.ReconcilePeerCollection([1],receipt["id"]!.GetValue<string>()));
        Assert.Equal(1,peer.Snapshot()["completed"]!.GetValue<int>());Assert.Equal(new[]{2},peer.Snapshot()["owned_slots"]!.AsArray().Select(n=>n!.GetValue<int>()));Assert.Equal("waiting",peer.Snapshot()["phase"]!.GetValue<string>());Assert.Equal("paused",peer.Snapshot()["status"]!.GetValue<string>());
        runner.AcknowledgeCollection(receipt["id"]!.GetValue<string>());Assert.Empty(runner.Snapshot()["collection_receipts"]!.AsArray());Assert.Single(port.Clicks);
    }




    [Fact] public void LaterSlotKeepsItsOwnDueWhenEarlierCheckBacksOff()
    {
        var time=new ManualTime();var store=new MemoryStore();var port=new ProductionPort{Frame=new("metal_panel",EmptySlots:[],ReadySlots:[],SlotCount:2)};
        var runner=new ProductionRunner(port,store,new("a","metal",60),2,time);var saved=runner.Snapshot();
        saved["submitted"]=2;saved["owned_slots"]=new JsonArray(0,1);saved["slot_due_at"]=new JsonObject{["0"]=90d,["1"]=200d};saved["due_at"]=200d;saved["production_due_at"]=200d;
        saved["phase"]="waiting";saved["status"]="waiting";
        // The stale retry belongs to slot 0, which was due at 90. Slot 1 becomes due
        // at 200 and must wake then, not at the retry (4590) nor at its 900s cap (990).
        saved["collection_retry_at"]=4590d;store.Flush(saved);
        runner=new(port,store,new("a","metal",60),2,time);
        Assert.Equal(200,runner.NextCollectionAt(true));
        // Off-site the shared 300s trip interval wins over the wake, and it is bounded
        // by the interval rather than by the retry; on-site stays immediate.
        saved=runner.Snapshot();saved["collection_not_before"]=400d;store.Flush(saved);
        runner=new(port,store,new("a","metal",60),2,time);
        Assert.Equal(400,runner.NextCollectionAt());Assert.Equal(200,runner.NextCollectionAt(true));
        // Waking at 200 actually reaches the collect step: one click for our slot.
        port.Frame=port.Frame with{EmptySlots=new int[0],ReadySlots=new[]{0,1},CollectAvailable=true};time.Seconds=201;runner.Resume(true);runner.Tick();
        Assert.Equal("collect",Assert.Single(port.Clicks));
        Assert.Equal(new[]{0,1},store.Read()!["pending"]!["target_slots"]!.AsArray().Select(n=>n!.GetValue<int>()));
    }
    [Fact] public void RetryOnTheLastSlotIsStillCapped()
    {
        var time=new ManualTime();var store=new MemoryStore();var port=new ProductionPort{Frame=new("metal_panel",EmptySlots:[],ReadySlots:[],SlotCount:1)};
        var runner=new ProductionRunner(port,store,new("a","metal",60),time:time);var saved=runner.Snapshot();
        saved["submitted"]=1;saved["owned_slots"]=new JsonArray(0);saved["slot_due_at"]=new JsonObject{["0"]=90d};saved["due_at"]=90d;saved["production_due_at"]=90d;
        saved["collection_retry_at"]=4590d;store.Flush(saved);
        runner=new(port,store,new("a","metal",60),time:time);
        // No later slot to wake for, so the legacy one-hour backoff is still bounded.
        Assert.Equal(990,runner.NextCollectionAt(true));
    }


    [Fact] public void RetryCoversOnlyTheSlotsThatWereChecked()
    {
        var time=new ManualTime();var store=new MemoryStore();var port=new ProductionPort{Frame=new("metal_panel",EmptySlots:[],ReadySlots:[],SlotCount:3)};
        var runner=new ProductionRunner(port,store,new("a","metal",60),3,time);var saved=runner.Snapshot();
        saved["submitted"]=3;saved["owned_slots"]=new JsonArray(0,1,2);saved["slot_due_at"]=new JsonObject{["0"]=90d,["1"]=200d,["2"]=300d};
        saved["due_at"]=300d;saved["production_due_at"]=300d;saved["phase"]="waiting";saved["status"]="waiting";store.Flush(saved);
        runner=new(port,store,new("a","metal",60),3,time);
        // On site at 201 slots 90 and 200 are both due and get checked; neither is ready.
        time.Seconds=201;runner.Resume(true);runner.Tick();
        var state=runner.Snapshot();
        Assert.Equal(201,state["collection_checked_until"]!.GetFiniteDouble());
        Assert.Equal(216,state["collection_retry_at"]!.GetFiniteDouble());
        // The failed check covered 90 and 200 only. Waking at 200 again would re-check
        // the same two slots on every tick on site and on every trip interval off site.
        Assert.Equal(216,runner.NextCollectionAt(true));Assert.Equal(216,runner.NextCollectionAt());
        // Slot 300 was not looked at, so the retry cannot hold it back either.
        state["collection_retry_at"]=4590d;store.Flush(state);runner=new(port,store,new("a","metal",60),3,time);
        Assert.Equal(300,runner.NextCollectionAt(true));
        // Restart keeps the same answer: the cutoff and the retry are both persisted.
        state=runner.Snapshot();state["collection_retry_at"]=216d;store.Flush(state);runner=new(port,store,new("a","metal",60),3,time);
        Assert.Equal(201,runner.Snapshot()["collection_checked_until"]!.GetFiniteDouble());
        Assert.Equal(216,runner.NextCollectionAt(true));
        // Once 300 has been checked too the cutoff moves on instead of sticking at 200.
        state=runner.Snapshot();state["collection_checked_until"]=300d;state["collection_retry_at"]=315d;store.Flush(state);
        runner=new(port,store,new("a","metal",60),3,time);
        Assert.Equal(315,runner.NextCollectionAt(true));
    }


    [Fact] public void SafeNavigationFailureQuarantinesOnlyTaskAndSupplyCannotRetryIt()
    {
        var bad=new FaultPort{FailNavigation=true,Releasable=true};var good=new FaultPort();
        var first=new ProductionRunner(bad,new MemoryStore(),new("a","metal",60));
        var second=new ProductionRunner(good,new MemoryStore(),new("b","wood",60));
        var faults=new List<(string Job,string Boundary)>();var dispatcher=new WorkflowDispatcher(fault:(id,_,boundary)=>faults.Add((id,boundary)));
        dispatcher.Register("a",first,"metal");dispatcher.Register("b",second,"wood");dispatcher.Continue();
        WorkCandidate[] plan=[new("a","production","metal",true,5,0),new("b","production","wood",true,5,1)];
        dispatcher.Tick(plan,null);dispatcher.Tick(plan,null);Assert.Contains("a",dispatcher.Faults.Keys);Assert.False(dispatcher.IsPaused);
        dispatcher.SupplyChanged("a");Assert.Equal("b",dispatcher.Tick(plan,null));dispatcher.Tick(plan,null);Assert.Equal(1,good.Navigations);
        Assert.Equal(new[]{("a","task")},faults);Assert.Equal(1,bad.Navigations);
        dispatcher.Continue(false);Assert.Contains("a",dispatcher.Faults.Keys);
        dispatcher.Continue();Assert.Empty(dispatcher.Faults);
    }
    [Fact] public void NonExplicitContinueDoesNotResumeManualPause()
    {
        var port=new ProductionPort();var runner=new ProductionRunner(port,new MemoryStore(),new("a","metal",60));
        var dispatcher=new WorkflowDispatcher();dispatcher.Register("a",runner,"metal");dispatcher.Continue();runner.Pause("manual");
        dispatcher.Continue(false);dispatcher.Tick([new("a","production","metal",true,5,0)],null);
        Assert.Equal("paused",runner.Snapshot()["status"]!.GetValue<string>());
        Assert.Equal("manual",runner.Snapshot()["pause_reason"]!.GetValue<string>());Assert.Empty(port.Clicks);Assert.Empty(port.Navigation);
        dispatcher.Continue();dispatcher.Tick([new("a","production","metal",true,5,0)],null);
        Assert.NotEqual("paused",runner.Snapshot()["status"]!.GetValue<string>());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void PersistenceFailurePropagatesAndUnprovenReleaseRetainsOwnership(bool persistence)
    {
        var port=new FaultPort{FailNavigation=true,Releasable=persistence,PersistenceFailure=persistence};
        var dispatcher=new WorkflowDispatcher();dispatcher.Register("a",new ProductionRunner(port,new MemoryStore(),new("a","metal",60)),"metal");dispatcher.Continue();
        WorkCandidate[] plan=[new("a","production","metal",true,5,0)];dispatcher.Tick(plan,null);
        Assert.ThrowsAny<Exception>(()=>dispatcher.Tick(plan,null));Assert.Equal("a",dispatcher.ActiveId);
        if(persistence)Assert.Empty(dispatcher.Faults);
        else
        {
            Assert.Contains("a",dispatcher.Faults.Keys);
            for(var i=0;i<5;i++)Assert.Null(Record.Exception(()=>dispatcher.Tick(plan,null)));
            Assert.Equal(1,port.Navigations);Assert.Equal("a",dispatcher.ActiveId);
        }
    }
    [Fact] public void FailedObservationAcrossPendingInputRetainsIntentAndOwnership()
    {
        var store=new MemoryStore();var port=new FaultPort{Releasable=true,Frame=new("metal_panel",EmptySlots:[0],SlotCount:1)};
        var runner=new ProductionRunner(port,store,new("a","metal",60));var dispatcher=new WorkflowDispatcher();dispatcher.Register("a",runner,"metal");dispatcher.Continue();
        WorkCandidate[] plan=[new("a","production","metal",true,1,0)];dispatcher.Tick(plan,null);dispatcher.Tick(plan,null);
        port.Frame=new("recipe_detail",RecipeKey:"a",CraftAvailable:true);dispatcher.Tick(plan,null);Assert.NotNull(store.Read()!["pending"]);Assert.Equal(1,port.Crafts);
        port.FailObserve=true;Assert.Throws<InvalidOperationException>(()=>dispatcher.Tick(plan,null));
        for(var i=0;i<5;i++)Assert.Null(Record.Exception(()=>dispatcher.Tick(plan,null)));
        Assert.Contains("a",dispatcher.Faults.Keys);Assert.Equal("a",dispatcher.ActiveId);Assert.NotNull(store.Read()!["pending"]);Assert.Equal(1,port.Crafts);
    }
    [Fact] public void StalledMaintenanceReleasesOnlyRepairAndBlocksDependentGathering()
    {
        var time=new ManualTime();var port=new MaintenancePort{Frame=new("1","scene")};
        var runner=new MaintenanceRunner(port,new MemoryStore(),new(BackpackEnabled:false,IntervalSeconds:1),time);runner.Resume(true);time.Seconds+=2;runner.Tick();runner.Tick();
        time.Seconds+=121;var state=runner.Tick();Assert.Equal("repair_progress_timeout",state["reason"]!.GetValue<string>());
        Assert.False(state["navigation_reserved"]!.GetValue<bool>());Assert.True(runner.Blocked("gathering",true));Assert.False(runner.Blocked("production"));Assert.Empty(port.Actions);
    }
    [Fact] public void KnownQueueDisappearanceDoesNotBecomeEmptyOrOverwriteFile()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-missing-"+Guid.NewGuid().ToString("N"));
        try{var queue=new RuntimeQueue(dir);queue.Edit(q=>q.Add("a","production",1,1,"a",[]));File.Move(queue.PathName,queue.PathName+".held");
            Assert.Throws<FileNotFoundException>(()=>queue.Read());Assert.Throws<FileNotFoundException>(()=>queue.Edit(_=>{}));Assert.False(File.Exists(queue.PathName));
        }finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    private sealed class FaultPort:IProductionPort
    {
        public bool FailNavigation,FailObserve,Releasable,PersistenceFailure;public int Navigations,Crafts;public ProductionFrame Frame=new("scene");
        public bool RemoteDevicePreview=>false;public bool CanReleaseForeground()=>Releasable;
        public ProductionFrame Observe()=>FailObserve?throw new InvalidOperationException("observe failed"):Frame;
        public void Navigate(string destination,ProductionRecipe recipe){Navigations++;if(PersistenceFailure)throw new IOException("disk failure");if(FailNavigation)throw new InvalidOperationException("missing target");}
        public void Click(string kind,ProductionRecipe recipe)=>Crafts++;public void Pause(string reason){}public void Resume(){}
    }
}
