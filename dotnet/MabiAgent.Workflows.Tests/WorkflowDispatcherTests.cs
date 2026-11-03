using System.Text.Json.Nodes;
using MabiAgent.Core;
using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Workflows.Tests;
public sealed class WorkflowDispatcherTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void ShortSlotWaitsForTripIntervalButCollectsImmediatelyOnsite(bool onsite)
    {
        var time=new ManualTime{Seconds=100};var store=new MemoryStore();var port=new ProductionPort();var run=new ProductionRunner(port,store,new("a","metal",60),2,time);
        var state=run.Snapshot();state["submitted"]=2;state["owned_slots"]=new JsonArray(0,1);state["slot_due_at"]=new JsonObject{["0"]=110d,["1"]=170d};state["due_at"]=170d;state["production_due_at"]=170d;state["phase"]="waiting";store.Flush(state);
        run=new(port,store,new("a","metal",60),2,time);run.DeferOffsiteCollection(400);var dispatcher=new WorkflowDispatcher(time);dispatcher.Continue();dispatcher.Register("a",run,"metal");
        WorkCandidate[] plan=[new("a","production","metal",true,0,0)];time.Seconds=110;
        if(onsite)Assert.Equal("a",dispatcher.Tick(plan,"metal",onsiteDevice:"metal",locationVerified:true));
        else{Assert.Null(dispatcher.Tick(plan,"metal",onsiteDevice:null,locationVerified:true));Assert.Empty(port.Navigation);time.Seconds=400;Assert.Equal("a",dispatcher.Tick(plan,null));}
        Assert.Empty(port.Clicks);Assert.Equal(170,run.Snapshot()["production_due_at"]!.GetFiniteDouble());
    }
    [Theory][InlineData(null)][InlineData("wood")][InlineData("metal")]
    public void FirstSlotWakesRestoredBatchBeforeTailRegardlessOfLocation(string? location)
    {
        var time=new ManualTime{Seconds=100};var store=new MemoryStore();var port=new ProductionPort();
        var run=new ProductionRunner(port,store,new("a","metal",60),2,time);var state=run.Snapshot();
        state["submitted"]=2;state["owned_slots"]=new JsonArray(0,1);state["slot_due_at"]=new JsonObject{["0"]=110d,["1"]=170d};
        state["due_at"]=170d;state["production_due_at"]=170d;state["phase"]="waiting";store.Flush(state);
        run=new(port,store,new("a","metal",60),2,time);var dispatcher=new WorkflowDispatcher(time);dispatcher.Continue();dispatcher.Register("a",run,"metal");
        WorkCandidate[] plan=[new("a","production","metal",false,0,0)];Assert.Null(dispatcher.Tick(plan,location));Assert.Empty(port.Navigation);
        time.Seconds=110;Assert.Equal("a",dispatcher.Tick(plan,location));dispatcher.Tick(plan,location);
        Assert.Equal("queue",Assert.Single(port.Navigation));Assert.Empty(port.Clicks);Assert.Equal(170,run.Snapshot()["production_due_at"]!.GetFiniteDouble());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void DeferredGatheringRestoresOnlyAfterStartAndWithoutUnknownConsumption(bool started)
    {
        var time=new ManualTime();var port=new GatheringPort();var store=new MemoryStore();
        _=new GatheringRunner(port,store,new("ore",1,"mining","node"),"g",time);
        var restored=new GatheringRunner(port,store,new("ore",1,"mining","node"),"g",time);
        var d=new WorkflowDispatcher(time);if(started)d.Continue();d.Register("g",restored);
        Assert.Equal(started,d.CanSchedule("g"));Assert.Equal(0,port.Starts);
    }
    [Fact] public void DeferredRestoredProductionUsesExplicitStartWithoutResumingManualPause()
    {
        var time=new ManualTime{Seconds=100};var port=new ProductionPort();
        var restored=Waiting(port,time,"restored","wood");restored.RestoreDeviceTiming(110);restored.Pause("restart");
        var manual=Waiting(new ProductionPort(),time,"manual","metal");manual.Pause("manual");
        var d=new WorkflowDispatcher(time);d.Continue();d.Register("restored",restored,"wood");d.Register("manual",manual,"metal");
        Assert.True(d.CanSchedule("restored"));Assert.False(d.CanSchedule("manual"));
        WorkCandidate[] plan=[new("restored","production","wood",true,0,0)];
        Assert.Null(d.Tick(plan,null));Assert.Empty(port.Navigation);
        time.Seconds=111;Assert.Equal("restored",d.Tick(plan,null));
        Assert.Equal("waiting",restored.Snapshot()["status"]!.GetValue<string>());Assert.Null(restored.Snapshot()["pause_reason"]);
        Assert.Equal("manual",manual.Snapshot()["pause_reason"]!.GetValue<string>());
    }
    [Fact] public void ConfirmedProductionExitFaultIsIsolatedAndOtherDeviceAndGatheringContinue()
    {
        var time=new ManualTime();var port=new ProductionPort{Frame=new("chemical_panel",EmptySlots:[1,2,3,4],SlotCount:5)};
        var glue=Waiting(port,time,"glue","chemical",leaving:true);port.NavigationFailure=new InvalidOperationException("device close failed");
        var ironPort=new ProductionPort();var iron=new ProductionRunner(ironPort,new MemoryStore(),new("iron","metal",60),time:time);
        var gatherPort=new GatheringPort();var gather=new GatheringRunner(gatherPort,new MemoryStore(),new("ore",1,"mining","node"),"g",time);
        var faults=0;var d=new WorkflowDispatcher(time,(_,_,_)=>faults++);d.Register("glue",glue,"chemical");d.Register("iron",iron,"metal");d.Register("g",gather);d.Continue();
        WorkCandidate[] plan=[new("glue","production","chemical",true,1,0),new("iron","production","metal",true,5,1),new("g","gathering",null,true,0,2)];
        d.RestoreOwnership("glue");d.Tick(plan,"chemical");Assert.Null(d.Tick(plan,"chemical"));Assert.Equal(1,faults);Assert.Contains("glue",d.Faults.Keys);
        Assert.Equal(1,glue.Snapshot()["submitted"]!.GetValue<int>());Assert.Single(glue.Snapshot()["owned_slots"]!.AsArray());Assert.Equal(10000d,glue.Snapshot()["due_at"]!.GetValue<double>());
        Assert.Equal("iron",d.Tick(plan,"chemical"));d.Remove("iron");Assert.Equal("g",d.Tick(plan,"chemical"));d.Tick(plan,"chemical");Assert.Equal(1,gatherPort.Starts);Assert.Equal(1,faults);
    }
    [Fact] public void CollectionUsesOwnBatchDueBeforeLaterRecipeTail()
    {
        var time=new ManualTime{Seconds=100};var firstPort=new ProductionPort();var lastPort=new ProductionPort();
        var first=Waiting(firstPort,time,"a","metal");var last=Waiting(lastPort,time,"b","metal");first.RestoreDeviceTiming(90);
        var d=new WorkflowDispatcher(time);d.Register("a",first,"metal");d.Register("b",last,"metal");d.Continue();
        Assert.Equal("a",d.Tick([new("a","production","metal",true,0,0),new("b","production","metal",true,0,1)],null));
        Assert.Empty(firstPort.Navigation);Assert.Empty(lastPort.Navigation);
    }
    [Fact] public void AtDeviceCollectionDoesNotWaitForOtherRecipesTail()
    {
        var time=new ManualTime{Seconds=100};var first=Waiting(new ProductionPort(),time,"a","metal");var last=Waiting(new ProductionPort(),time,"b","metal");first.RestoreDeviceTiming(90);
        var dispatcher=new WorkflowDispatcher(time);dispatcher.Register("a",first,"metal");dispatcher.Register("b",last,"metal");dispatcher.Continue();
        Assert.Equal("a",dispatcher.Tick([new("a","production","metal",true,0,0),new("b","production","metal",true,0,1)],"metal"));
    }
    [Fact] public void RemoteDevicePanelAllowsDueBatchWithoutWaitingForLaterTail()
    {
        var time=new ManualTime{Seconds=100};var first=Waiting(new ProductionPort(),time,"a","metal");var last=Waiting(new ProductionPort(),time,"b","metal");first.RestoreDeviceTiming(90);
        var dispatcher=new WorkflowDispatcher(time);dispatcher.Register("a",first,"metal");dispatcher.Register("b",last,"metal");dispatcher.Continue();
        Assert.Equal("a",dispatcher.Tick([new("a","production","metal",true,0,0),new("b","production","metal",true,0,1)],"metal"));
    }
    [Fact] public void MissingMaterialsYieldAndOnlySupplyChangeResumesBranch()
    {
        var time=new ManualTime();var port=new ProductionPort();var available=0;
        var blocked=new ProductionRunner(port,new MemoryStore(),new("r1","furnace",60),time:time,availableRounds:(_,_)=>available);
        var other=new ProductionRunner(new ProductionPort(),new MemoryStore(),new("r2","wood",60),time:time);
        var d=new WorkflowDispatcher(time);d.Register("blocked",blocked,"furnace");d.Register("other",other,"wood");d.Continue();
        WorkCandidate b=new("blocked","production","furnace",true,5,0);d.Tick([b],null);d.Tick([b],null);
        Assert.True(blocked.SafeToSwitch);Assert.Empty(port.Navigation);
        Assert.Equal("other",d.Tick([b,new("other","production","wood",true,5,1)],null));
        Assert.Equal("paused",blocked.Snapshot()["status"]!.GetValue<string>());
        available=1;d.SupplyChanged("blocked");Assert.Equal("paused",blocked.Snapshot()["status"]!.GetValue<string>());
        Assert.Equal("other",d.Tick([b,new("other","production","wood",true,5,1)],null));
    }
    [Fact] public void BusyDeviceYieldsToReadyOtherDeviceWithoutPolling()
    {
        var time=new ManualTime();var busyPort=new ProductionPort();var otherPort=new ProductionPort();
        var busy=Waiting(busyPort,time,"r1","fabric");
        var other=new ProductionRunner(otherPort,new MemoryStore(),new("r2","wood",60),time:time);
        var d=new WorkflowDispatcher(time);d.Register("busy",busy,"fabric");d.Register("other",other,"wood");d.Continue();
        WorkCandidate[] plan=[new("busy","production","fabric",true,0,0),new("other","production","wood",true,5,1)];
        Assert.Equal("other",d.Tick(plan,null));d.Tick(plan,null);
        Assert.Empty(busyPort.Navigation);Assert.Single(otherPort.Navigation);
    }
    private static ProductionRunner Waiting(ProductionPort port,ManualTime time,string key,string device,bool leaving=false)
    {
        var store=new MemoryStore();_=new ProductionRunner(port,store,new(key,device,60),time:time);
        store.Value!["status"]="running";store.Value["phase"]=leaving?"leave":"waiting";store.Value["submitted"]=1;
        store.Value["owned_slots"]=new JsonArray(0);store.Value["due_at"]=10000d;store.Value["production_due_at"]=10000d;
        var run=new ProductionRunner(port,store,new(key,device,60),time:time);run.Resume(true);return run;
    }
    [Fact] public void LongProductionDoesNotPollAndAnotherDeviceThenGatheringCanRun()
    {
        var time=new ManualTime();var fabricPort=new ProductionPort();var woodPort=new ProductionPort();var gatherPort=new GatheringPort();
        var fabric=Waiting(fabricPort,time,"fabric","fabric");var wood=Waiting(woodPort,time,"wood","wood");
        var gather=new GatheringRunner(gatherPort,new MemoryStore(),new("ore",1,"mining","node"),"g",time);
        var dispatcher=new WorkflowDispatcher(time);dispatcher.Register("fabric",fabric,"fabric");dispatcher.Register("wood",wood,"wood");dispatcher.Register("g",gather);dispatcher.Continue();
        WorkCandidate[] plan=[new("fabric","production","fabric",true,0,0),new("wood","production","wood",true,0,1),new("g","gathering",null,true,0,2)];
        Assert.Equal("g",dispatcher.Tick(plan,null));dispatcher.Tick(plan,null);Assert.Equal(1,gatherPort.Starts);
        for(var i=0;i<5;i++)dispatcher.Tick(plan,null);
        Assert.Empty(fabricPort.Navigation);Assert.Empty(woodPort.Navigation);
    }
    [Fact] public void SameDevicePeerRefillsBeforeLeavingAndOtherDeviceRemainsAvailable()
    {
        var time=new ManualTime();var firstPort=new ProductionPort();var secondPort=new ProductionPort();var otherPort=new ProductionPort();
        firstPort.Frame=new("metal_panel",EmptySlots:[0,1,2,3,4],SlotCount:5);
        var first=new ProductionRunner(firstPort,new MemoryStore(),new("r1","metal",60),time:time);
        var second=new ProductionRunner(secondPort,new MemoryStore(),new("r2","metal",60),time:time);
        var other=new ProductionRunner(otherPort,new MemoryStore(),new("r3","wood",60),time:time);
        var d=new WorkflowDispatcher(time);d.Register("first",first,"metal");d.Register("second",second,"metal");d.Register("other",other,"wood");d.Continue();
        WorkCandidate firstCandidate=new("first","production","metal",true,2,0);
        d.Tick([firstCandidate],"metal");d.Tick([firstCandidate],"metal");
        firstPort.Frame=new("recipe_detail",RecipeKey:"r1",CraftAvailable:true);d.Tick([firstCandidate],"metal");
        firstPort.Frame=new("metal_panel",EmptySlots:[1,2,3,4],SlotCount:5);d.Tick([firstCandidate],"metal");d.Tick([firstCandidate],"metal");
        WorkCandidate[] plan=[firstCandidate,new("second","production","metal",true,2,1),new("other","production","wood",true,5,2)];
        Assert.Equal("second",d.Tick(plan,"metal"));Assert.DoesNotContain("leave",firstPort.Navigation);
        Assert.Equal("waiting",first.Snapshot()["phase"]!.GetValue<string>());
    }
    [Fact] public void ActiveNativeBatchCannotBePreemptedAndManualPausePersists()
    {
        var time=new ManualTime();var port=new GatheringPort();var gathering=new GatheringRunner(port,new MemoryStore(),new("ore",1,"mining","node"),"g",time);
        var production=new ProductionRunner(new ProductionPort(),new MemoryStore(),new("r","wood",60),time:time);
        var d=new WorkflowDispatcher(time);d.Register("g",gathering);d.Register("p",production,"wood");d.Continue();
        WorkCandidate g=new("g","gathering",null,true,0,0);d.Tick([g],null);d.Tick([g],null);
        port.Frame=port.Frame with{TaskId=JsonValue.Create(1),TaskStatus="active"};
        Assert.Equal("g",d.Tick([g,new("p","production","wood",true,5,1)],null));
        d.Pause();var before=port.Starts;d.Tick([g],null);Assert.Equal(before,port.Starts);Assert.Equal("已暂停",d.Status);
    }
}
