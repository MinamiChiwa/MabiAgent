using System.Text.Json.Nodes;
using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Workflows.Tests;
public sealed class TransitionTests
{
    [Fact] public void OccupiedRemoteDeviceAcceptsOneReadyRoundWithoutWaitingToFillAllSlots()
    {
        var port=new RemoteProductionPort{Frame=new("furnace_panel",EmptySlots:[1,2,3,4],AtDevice:false,SlotCount:5,ReadySlots:[])};
        var runner=new ProductionRunner(port,new MemoryStore(),new("iron","furnace",30),availableRounds:(_,_)=>1,deviceReadyRounds:_=>1);
        runner.Resume(true);runner.Tick();Assert.Equal("queue",runner.Snapshot()["phase"]!.GetValue<string>());
        Assert.NotEqual("paused",runner.Snapshot()["status"]!.GetValue<string>());Assert.Empty(port.Actions);
        runner.Tick();Assert.Equal("queue",Assert.Single(port.Actions));Assert.Null(runner.Snapshot()["pending"]);
    }
    [Fact] public void NativeDeviceTripWaitsForAutomaticPageWithoutGenericTimeoutAndKeepsManualPause()
    {
        var time=new ManualTime();var port=new ProductionPort{Frame=new("scene",AtDevice:false,NavigationActive:true)};
        var runner=new ProductionRunner(port,new MemoryStore(),new("iron","furnace",30),time:time);runner.Resume(true);
        foreach(var page in new[]{"scene","unknown","skills","unknown"})
        {time.Seconds+=180;port.Frame=port.Frame with{Page=page,Stable=page!="unknown"};runner.Tick();Assert.Null(runner.Snapshot()["pause_reason"]);}
        Assert.Empty(port.Navigation);Assert.Empty(port.Clicks);
        runner.Pause();time.Seconds+=180;runner.Tick();Assert.Equal("manual",runner.Snapshot()["pause_reason"]!.GetValue<string>());
        runner.Resume(false);Assert.Equal("manual",runner.Snapshot()["pause_reason"]!.GetValue<string>());
        runner.Resume(true);runner.Tick();Assert.Empty(port.Navigation);
        port.Frame=new("furnace_panel",EmptySlots:[0,1],SlotCount:2);runner.Tick();runner.Tick();Assert.Null(runner.Snapshot()["pause_reason"]);
        Assert.NotEmpty(port.Navigation);
    }
    [Fact] public void NativeDeviceTripAllowsDeclaredConfirmationWithoutCompletingArrival()
    {
        var port=new ProductionPort{Frame=new("device_travel_confirm",AtDevice:false,NavigationActive:true)};
        var runner=new ProductionRunner(port,new MemoryStore(),new("iron","furnace",30));runner.Resume(true);runner.Tick();
        Assert.Equal("queue",Assert.Single(port.Navigation));Assert.Empty(port.Clicks);
        Assert.NotEqual("confirm_craft",runner.Snapshot()["phase"]!.GetValue<string>());
        port.Frame=port.Frame with{Page="scene"};runner.Tick();Assert.Single(port.Navigation);
    }
    [Fact]
    public void ProductionFillsSerialSlotsLeavesThenCollectsAllOwnedJobs()
    {
        var time = new ManualTime(); var port = new ProductionPort(); var runner = new ProductionRunner(port, new MemoryStore(), new("iron", "furnace", 30), 2, time);
        runner.Resume(true); runner.Tick(); port.Frame = new("recipe_detail", RecipeKey: "iron", CraftAvailable: true); runner.Tick();
        port.Frame = new("furnace_panel", EmptySlots: [1], SlotCount: 2); runner.Tick(); runner.Tick();
        port.Frame = new("recipe_detail", RecipeKey: "iron", CraftAvailable: true); runner.Tick();
        port.Frame = new("furnace_panel", EmptySlots: [], SlotCount: 2); runner.Tick(); Assert.Equal(160, runner.Snapshot()["due_at"]!.GetValue<double>());
        runner.Tick(); port.Frame = new("scene"); runner.Tick(); Assert.True(runner.SafeToSwitch);
        time.Seconds = 160; runner.Tick(); port.Frame = new("furnace_panel", EmptySlots: [], SlotCount: 2, CollectAvailable: true, ReadySlots: [0, 1]); runner.Tick();
        port.Frame = port.Frame with { EmptySlots = [0, 1] }; runner.Tick();
        Assert.Equal(new[] { 0, 1 }, runner.ConfirmedCollection); Assert.Equal(2, runner.Snapshot()["completed"]!.GetValue<int>());
        port.Frame = new("scene"); runner.Tick(); Assert.Equal("complete", runner.Snapshot()["status"]!.GetValue<string>()); Assert.Equal(new[] { "craft", "craft", "collect" }, port.Clicks);
    }
    [Fact]
    public void ProductionCollectsReadySubsetAtDeviceAndPreservesRunningSlots()
    {
        var store = new MemoryStore(); var port = new ProductionPort(); var time = new ManualTime(); var recipe = new ProductionRecipe("iron", "furnace", 30);
        var runner = new ProductionRunner(port, store, recipe, 2, time); var state = runner.Snapshot();
        state["submitted"] = 2; state["owned_slots"] = new JsonArray(0, 1); state["status"] = "waiting"; state["phase"] = "waiting"; state["due_at"] = 100; state["production_due_at"] = 100; store.Flush(state);
        runner = new(port, store, recipe, 2, time); port.Frame = new("furnace_panel", EmptySlots: [], CollectAvailable: true, ReadySlots: [0], SlotCount: 2); runner.Resume(true); runner.Tick();
        Assert.Equal("collect",Assert.Single(port.Clicks));Assert.Equal(new[]{0},runner.Snapshot()["pending"]!["target_slots"]!.AsArray().Select(n=>n!.GetValue<int>()));
        port.Frame=port.Frame with{EmptySlots=[0],ReadySlots=[]};runner.Tick();Assert.Equal(1,runner.Snapshot()["completed"]!.GetValue<int>());Assert.Equal(new[]{1},runner.Snapshot()["owned_slots"]!.AsArray().Select(n=>n!.GetValue<int>()));Assert.NotNull(runner.Snapshot()["production_due_at"]);
    }
    [Fact]
    public void RemoteFacilityWithReadySubsetAllowsTravelBeforeOtherSlotFinishes()
    {
        var port = new RemoteProductionPort(); var store = new MemoryStore(); var recipe = new ProductionRecipe("iron", "furnace", 30); var time = new ManualTime();
        var runner = new ProductionRunner(port, store, recipe, time: time); var state = runner.Snapshot();
        state["submitted"] = 1; state["owned_slots"] = new JsonArray(0); state["status"] = "waiting"; state["phase"] = "waiting"; state["due_at"] = 100; store.Flush(state);
        runner = new(port, store, recipe, time: time); runner.Resume(true); runner.Tick();
        Assert.Equal("collect", runner.Snapshot()["phase"]!.GetValue<string>()); Assert.Empty(port.Actions);
        runner.Tick();Assert.Equal("queue",Assert.Single(port.Actions));Assert.Null(runner.Snapshot()["pending"]);
    }
    [Fact]
    public void RemoteFacilityWithoutReadySlotsKeepsBoundedWait()
    {
        var port=new RemoteProductionPort{Frame=new("furnace_panel",EmptySlots:[],AtDevice:false,SlotCount:2,ReadySlots:[],DeviceRemainingSeconds:40)};
        var store=new MemoryStore();var recipe=new ProductionRecipe("iron","furnace",30);var time=new ManualTime();
        var runner=new ProductionRunner(port,store,recipe,time:time);var state=runner.Snapshot();
        state["submitted"]=1;state["owned_slots"]=new JsonArray(0);state["status"]="waiting";state["phase"]="waiting";state["due_at"]=100;store.Flush(state);
        runner=new(port,store,recipe,time:time);runner.Resume(true);runner.Tick();
        Assert.Equal("leave",runner.Snapshot()["phase"]!.GetValue<string>());Assert.Equal(140,runner.Snapshot()["due_at"]!.GetValue<double>());Assert.Empty(port.Actions);
    }
    [Fact]
    public void FailedDurableFlushPreventsConsumingInput()
    {
        var store = new FailingStore(); var port = new ProductionPort(); var runner = new ProductionRunner(port, store, new("iron", "furnace", 30));
        runner.Resume(true); runner.Tick(); port.Frame = new("recipe_detail", RecipeKey: "iron", CraftAvailable: true); store.Fail = true;
        Assert.Throws<IOException>(() => runner.Tick()); Assert.Empty(port.Clicks);
    }
    [Fact]
    public void PlayerGatheringBatchIsNeverReplaced()
    {
        var port = new GatheringPort { Frame = new("scene", RouteKey: "ore", StartAvailable: true, TaskStatus: "active", TaskId: JsonValue.Create(99)) };
        var runner = new GatheringRunner(port, new MemoryStore(), new("ore", 1, "mining", "node"), "a"); runner.Resume(true); runner.Tick();
        Assert.Equal(0, port.Starts); Assert.Equal("active_task_exists", runner.Snapshot()["pause_reason"]!.GetValue<string>());
    }
    [Fact]
    public void StorageDialogUsesFreshBaselineAndPersistsSeparateConfirmation()
    {
        var port = new StoragePort(); var store = new MemoryStore(); var runner = new StorageSupplyRunner([new(1, "ore", 3, "shared")], port, store); runner.Tick();
        port.Frame = port.Frame with { At = 101, CaptureId = "2", TransferDialog = true, DialogQuantity = 3, ConfirmAvailable = true, Available = null, BagQuantity = null }; runner.Tick();
        Assert.Equal(new[] { "prepare_transfer", "confirm_transfer" }, port.Actions.Select(a => a.Kind)); Assert.Equal("confirm_transfer", runner.Snapshot()["pending"]!["kind"]!.GetValue<string>());
        port.Frame = port.Frame with { At = 102, CaptureId = "3", TransferComplete = true }; runner.Tick();
        Assert.Equal("complete", runner.Snapshot()["status"]!.GetValue<string>());
    }
    [Fact]
    public void DisabledRepairRetainsAlreadyIssuedPayment()
    {
        var time = new ManualTime(); var port = new MaintenancePort(); var store = new MemoryStore(); var runner = new MaintenanceRunner(port, store, new(IntervalSeconds: 1), time);
        time.Seconds = 102; runner.Tick(); runner.Configure(new(RepairEnabled: false));
        Assert.Equal("confirm_repair", runner.Snapshot()["pending"]!["action"]!.GetValue<string>());
        port.Frame = new("2", "repair_tools", Gold: 99000); runner.Tick(); runner.Tick();
        Assert.Equal(new[] { "confirm_repair", "auto_equip" }, port.Actions);
    }
    [Fact]
    public void FishingGaugeRequiresLowSampleAndMeasuredReboundBeforeReel()
    {
        var port = new FishingPort(); var store = new MemoryStore(); var plan = new FishingPlan("lake", 100); var runner = new IdleFishingRunner(plan, port, store);
        var state = runner.Snapshot(); state["phase"] = "round"; state["round_seen"] = true; state["submitted"] = 1; store.Flush(state); runner = new(plan, port, store); runner.Resume();
        port.Frame = new(1, "1", "fishing", LeisureMode: false, RoundActive: true, HookVisible: true, Gauge: .5); runner.Tick();Assert.Empty(port.Actions);
        port.Frame = port.Frame with{At=1.01,CaptureId="missed-hook",RoundActive=false,HookVisible=false,Compass=true};runner.Tick();Assert.Empty(port.Actions);Assert.False(runner.SafeToSwitch);
        port.Frame = port.Frame with{At=1.02,CaptureId="low",Page="fishing",RoundActive=true,Compass=false,Gauge=.25};runner.Tick();
        port.Frame = port.Frame with { At = 1.05, CaptureId = "2", Gauge = .39 }; runner.Tick(); Assert.Empty(port.Actions);
        port.Frame = port.Frame with { At = 1.2, CaptureId = "3", Gauge = .39 }; runner.Tick(); Assert.Equal("reel", Assert.Single(port.Actions).Kind);
        port.Frame = port.Frame with { At = 3.1, CaptureId = "4", Gauge = .7 }; runner.Tick(); Assert.Single(port.Actions);
    }
}
internal sealed class RemoteProductionPort : IProductionPort
{
    public List<string> Actions = []; public bool RemoteDevicePreview => true;
    public ProductionFrame Frame=new("furnace_panel", EmptySlots: [], AtDevice: false, SlotCount: 2, ReadySlots: [0], DeviceRemainingSeconds: 40);
    public ProductionFrame Observe() => Frame;
    public void Navigate(string destination, ProductionRecipe recipe) => Actions.Add(destination); public void Click(string kind, ProductionRecipe recipe) => Actions.Add(kind); public void Pause(string reason) { } public void Resume() { }
}
internal sealed class FailingStore : IWorkflowStore
{
    public bool Fail; public JsonObject? Read() => null; public void Flush(JsonObject state) { if (Fail) throw new IOException("test write failure"); }
}
