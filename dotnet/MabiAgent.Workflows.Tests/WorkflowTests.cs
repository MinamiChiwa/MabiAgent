using System.Text.Json.Nodes;
using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Workflows.Tests;

public sealed class WorkflowTests
{
    [Theory][InlineData("craft_unavailable")][InlineData("materials_unknown")][InlineData("device_full")][InlineData("device_batch_not_ready")]
    public void UnsubmittedProductionExitsBeforeReleasingBlockedVisit(string reason)
    {
        var store=new MemoryStore();var port=new ProductionPort{RemotePreview=reason is "materials_unknown" or "device_batch_not_ready"};
        var runner=new ProductionRunner(port,store,new("iron","furnace",30),time:new ManualTime());runner.Resume(true);
        if(reason is "craft_unavailable" or "materials_unknown")runner.Tick();
        port.Frame=reason is "device_full" or "device_batch_not_ready"?new("furnace_panel",EmptySlots:[],ReadySlots:[],SlotCount:2):new("recipe_detail",RecipeKey:"iron",CraftAvailable:false,MaterialsSufficient:reason=="materials_unknown"?null:false);
        runner.Tick();Assert.Equal("leave",runner.Snapshot()["phase"]!.GetValue<string>());Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());
        runner.Tick();Assert.Equal("leave",port.Navigation.Last());Assert.Empty(port.Clicks);Assert.Null(runner.Snapshot()["pending"]);
        port.Frame=new("scene");runner.Tick();Assert.Equal("paused",runner.Snapshot()["status"]!.GetValue<string>());Assert.Equal(reason,runner.Snapshot()["pause_reason"]!.GetValue<string>());
    }


    [Fact]
    public void RecipePageRoundTripsDoNotResetProductionProgressDeadline()
    {
        var time=new ManualTime();var port=new ProductionPort();var store=new MemoryStore();
        var runner=new ProductionRunner(port,store,new("iron","furnace",60),time:time);runner.Resume(true);runner.Tick();
        for(var i=0;i<4;i++)
        {
            time.Seconds=i*30;
            port.Frame=port.Frame with{Page=i%2==0?"recipe_detail":"furnace_panel",AtDevice=false};
            runner.Tick();
        }
        time.Seconds=120;port.Frame=port.Frame with{Page="furnace_panel"};
        Assert.Throws<TimeoutException>(()=>runner.Tick());
        Assert.Empty(port.Clicks);Assert.Equal(0,runner.Snapshot()["submitted"]!.GetValue<int>());
        Assert.Null(runner.Snapshot()["pending"]);
    }
    [Theory][InlineData(false,61)][InlineData(true,30.5)]
    public void VipDurationIsSavedBeforeCraftAndNotRecomputedAfterToggle(bool vip,double expected)
    {
        var port=new ProductionPort();var store=new MemoryStore();var time=new ManualTime();
        var runner=new ProductionRunner(port,store,new("iron","furnace",61),time:time);runner.Resume(true);runner.Tick();
        port.Frame=new("recipe_detail",RecipeKey:"iron",CraftAvailable:true,DurationSeconds:ProductionTiming.Duration(61,vip));runner.Tick();
        var saved=runner.Snapshot();var pending=saved["pending"]!;var due=pending["production_due_at"]!.GetValue<double>();
        Assert.Equal(expected,due-pending["at"]!.GetValue<double>());
        port.Frame=port.Frame with{DurationSeconds=ProductionTiming.Duration(61,!vip)};
        var restored=new ProductionRunner(port,store,new("iron","furnace",61),time:time);
        Assert.Equal(due,restored.Snapshot()["pending"]!["production_due_at"]!.GetValue<double>());
        Assert.Single(port.Clicks);
    }
    [Fact] public void RejectedCraftClearsOnlyUnsentAttemptAndRefreshesQueue()
    {
        var port=new ProductionPort();var store=new MemoryStore();var runner=new ProductionRunner(port,store,new("iron","furnace",30),time:new ManualTime());
        runner.Resume(true);runner.Tick();port.Frame=new("recipe_detail",RecipeKey:"iron",CraftAvailable:true);port.BeforeClick=()=>throw new InputRejectedBeforeDispatchException("changed");runner.Tick();
        Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("queue",runner.Snapshot()["phase"]!.GetValue<string>());Assert.NotEqual("unknown",runner.Snapshot()["status"]!.GetValue<string>());Assert.Empty(port.Clicks);
        port.BeforeClick=null;port.Frame=new("furnace_panel",EmptySlots:[0,1],SlotCount:2);runner.Tick();port.Frame=new("recipe_detail",RecipeKey:"iron",CraftAvailable:true);runner.Tick();
        Assert.Single(port.Clicks);Assert.NotNull(runner.Snapshot()["pending"]);
    }
    [Fact] public void RejectedSecondCraftReleasesItsProjectionButKeepsFirstSubmittedDebit()
    {
        var ledgerStore=new MemoryStore();var ledger=new RuntimeInventory(ledgerStore);var stock=new MabiAgent.Core.InventorySnapshot("character");stock.Set(new(1,100,"character","packet","v1",true));
        var definition=new MabiAgent.Core.RecipeDefinition("iron",3,1,new(){[1]=10},"furnace");var inner=new MemoryStore();var store=new SubmissionStore(inner,ledger,"batch",definition,()=>stock);
        var port=new ProductionPort();var runner=new ProductionRunner(port,store,new("iron","furnace",30),2,new ManualTime());runner.Resume(true);runner.Tick();
        port.Frame=new("recipe_detail",RecipeKey:"iron",CraftAvailable:true);runner.Tick();port.Frame=new("furnace_panel",EmptySlots:[1],SlotCount:2);runner.Tick();runner.Tick();
        port.Frame=new("recipe_detail",RecipeKey:"iron",CraftAvailable:true);port.BeforeClick=()=>throw new InputRejectedBeforeDispatchException("changed");runner.Tick();
        Assert.Null(runner.Snapshot()["pending"]);Assert.Equal(90,ledger.Apply(stock).Query(1).Quantity);Assert.Single(ledgerStore.Read()!["debits"]!.AsObject());Assert.Equal(1,runner.Snapshot()["submitted"]!.GetValue<int>());
        // Crash between durable unsent proof and projection release: constructor
        // replays only the idempotent release, never the native craft.
        ledger.Submitted("batch",2,definition,stock);Assert.Equal(80,ledger.Apply(stock).Query(1).Quantity);
        ledger=new RuntimeInventory(ledgerStore);store=new SubmissionStore(inner,ledger,"batch",definition,()=>stock);
        _=new ProductionRunner(new ProductionPort(),store,new("iron","furnace",30),2,new ManualTime());
        Assert.Equal(90,ledger.Apply(stock).Query(1).Quantity);Assert.Single(ledgerStore.Read()!["debits"]!.AsObject());
    }
    [Fact] public void RejectedGatherStartRetriesFromFreshObservationWithoutPausingSharedInput()
    {
        var port=new GatheringPort{StartError=new InputRejectedBeforeDispatchException("changed")};var runner=new GatheringRunner(port,new MemoryStore(),new("ore",1,"mining","node"),"a",new ManualTime());
        runner.Resume(true);runner.Tick();Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());Assert.Equal(0,port.Pauses);
        port.StartError=null;runner.Tick();Assert.Equal(1,port.Starts);Assert.NotNull(runner.Snapshot()["pending"]);
    }
    [Fact] public void PausedGatheringUsesPassivePortAndRetainsPauseAfterStartConfirmation()
    {
        var port=new GatheringPort();var runner=new GatheringRunner(port,new MemoryStore(),new("ore",1,"mining","node"),"a",new ManualTime());
        runner.Resume(true);runner.Tick();var reads=port.ActiveReads;runner.Pause();
        port.Frame=port.Frame with{TaskId=JsonValue.Create(1),TaskStatus="active"};runner.ObserveOnly();
        Assert.Equal(reads,port.ActiveReads);Assert.Equal("manual",runner.Snapshot()["pause_reason"]!.GetValue<string>());
        Assert.Equal("paused",runner.Snapshot()["status"]!.GetValue<string>());Assert.Null(runner.Snapshot()["pending"]);Assert.Equal(1,port.Starts);
    }
    [Fact] public void ProductionAndGatheringBoundUnknownPageWaitAndAllowExplicitRetry()
    {
        var time=new ManualTime();var production=new ProductionPort{Frame=new("unknown",Stable:false)};
        var p=new ProductionRunner(production,new MemoryStore(),new("iron","furnace",30),time:time);p.Resume(true);p.Tick();time.Seconds+=46;p.Tick();
        Assert.Equal("unrecognized_page",p.Snapshot()["pause_reason"]!.GetValue<string>());Assert.Empty(production.Clicks);
        Assert.Equal(0,production.GlobalPauses);
        p.Resume(true);p.Tick();Assert.NotEqual("paused",p.Snapshot()["status"]!.GetValue<string>());
        var gathering=new GatheringPort{Frame=new("unknown",Stable:false)};var g=new GatheringRunner(gathering,new MemoryStore(),new("ore",1,"mining","node"),"a",time);
        g.Resume(true);g.Tick();time.Seconds+=46;g.Tick();Assert.Equal("unrecognized_page",g.Snapshot()["pause_reason"]!.GetValue<string>());Assert.Equal(0,gathering.Starts);
        g.Resume(true);g.Tick();Assert.NotEqual("paused",g.Snapshot()["status"]!.GetValue<string>());
    }
    [Fact]
    public void ProductionJournalsBeforeClickAndUsesSerialDeviceTail()
    {
        var store = new MemoryStore(); var time = new ManualTime(); var port = new ProductionPort();
        var recipe = new ProductionRecipe("iron", "furnace", 30);
        var runner = new ProductionRunner(port, store, recipe, 2, time, deviceQueueTail: _ => 150);
        runner.Resume(true); runner.Tick();
        port.Frame = new("recipe_detail", RecipeKey: "iron", CraftAvailable: true);
        port.BeforeClick = () => Assert.Equal("craft", store.Value!["pending"]!["kind"]!.GetValue<string>());
        runner.Tick();
        Assert.Single(port.Clicks);
        port.Frame = new("furnace_panel", EmptySlots: [1], SlotCount: 2); runner.Tick();
        Assert.Equal(1, runner.Snapshot()["submitted"]!.GetValue<int>());
        Assert.Equal(180, runner.Snapshot()["production_due_at"]!.GetValue<double>());
    }
    [Fact]
    public void UnknownProductionNeverReplaysAfterRestart()
    {
        var store = new MemoryStore(); var port = new ProductionPort(); var recipe = new ProductionRecipe("iron", "furnace", 30);
        var runner = new ProductionRunner(port, store, recipe, time: new ManualTime());
        runner.Resume(true); runner.Tick(); port.Frame = new("recipe_detail", RecipeKey: "iron", CraftAvailable: true); runner.Tick();
        var restarted = new ProductionRunner(port, store, recipe, time: new ManualTime());
        restarted.Resume(true); restarted.Tick();
        Assert.Single(port.Clicks); Assert.Equal("unknown", restarted.Snapshot()["status"]!.GetValue<string>());
    }
    [Fact]
    public void GatheringCreditsObservedYieldAndKeepsManualPause()
    {
        var store = new MemoryStore(); var port = new GatheringPort();
        var runner = new GatheringRunner(port, store, new("ore", 1, "mining", "node"), "action", new ManualTime());
        runner.Resume(true); runner.Tick();
        port.Frame = new("scene", RouteKey: "ore", TaskId: JsonValue.Create(7), TaskStatus: "active"); runner.Tick();
        runner.Pause(); port.Frame = port.Frame with { Foreground = false }; runner.Tick();
        Assert.Equal("manual", runner.Snapshot()["pause_reason"]!.GetValue<string>());
        port.Frame = port.Frame with { Foreground = true, TaskStatus = "complete", ActualQuantity = 3, Evidence = new JsonObject { ["source"] = "screen" } }; runner.Tick();
        Assert.Null(runner.Snapshot()["actual_quantity"]);Assert.Equal(1,runner.Snapshot()["completed"]!.GetValue<int>()); Assert.Equal(1, port.Starts);
    }
}
internal sealed class MemoryStore : IWorkflowStore
{
    public JsonObject? Value; public JsonObject? Read() => (JsonObject?)Value?.DeepClone();
    public void Flush(JsonObject value) => Value = (JsonObject)value.DeepClone();
}
internal sealed class ManualTime : TimeProvider
{
    public double Seconds = 100; public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Seconds);
}
internal sealed class ProductionPort : IProductionPort
{
    public ProductionFrame Frame = new("furnace_panel", EmptySlots: [0, 1], SlotCount: 2);
    public List<string> Clicks = []; public List<string> Navigation = []; public Action? BeforeClick;public Exception? NavigationFailure;public int GlobalPauses;public List<string> LocalPauses=[];public bool ReleaseAllowed;
    public bool CanReleaseForeground()=>ReleaseAllowed;
    public bool RemotePreview;public bool RemoteDevicePreview => RemotePreview; public ProductionFrame Observe() => Frame;
    public void Click(string kind, ProductionRecipe recipe) { BeforeClick?.Invoke(); Clicks.Add(kind); }
    public void Navigate(string destination, ProductionRecipe recipe) {if(NavigationFailure is{} error)throw error;Navigation.Add(destination);}
    public void Pause(string reason) { GlobalPauses++; } public void PauseLocal(string reason){LocalPauses.Add(reason);} public void Resume() { }
}
internal sealed class GatheringPort : IGatheringPort
{
    public Exception? StartError;public int Pauses;public bool ReleaseAllowed;public bool CanReleaseForeground()=>ReleaseAllowed;
    public GatheringFrame Frame = new("scene", RouteKey: "ore", StartAvailable: true); public int Starts, Navigations;
    public int ActiveReads,PassiveReads,Dismissals;
    public bool DismissMissingConsumables(){Dismissals++;return true;}
    public GatheringFrame Observe(GatheringRoute route, string actionId) {ActiveReads++;return Frame;}
    public GatheringFrame ObserveOnly(GatheringRoute route,string actionId){PassiveReads++;return Frame;}
    public void Navigate(GatheringRoute route) { Navigations++; } public void Start(GatheringRoute route, string actionId) {if(StartError is{} error)throw error;Starts++;}
    public void Pause(string reason) { Pauses++; } public void Resume() { }
}

