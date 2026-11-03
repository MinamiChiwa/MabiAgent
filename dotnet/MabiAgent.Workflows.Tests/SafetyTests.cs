using System.Text.Json.Nodes;
using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Workflows.Tests;
public sealed class SafetyTests
{
    [Fact] public void StorageNavigationFailureDoesNotCreateUnknownConsumptionOrStopSharedInput()
    {
        var port=new StoragePort{Frame=new(100,"1","scene"),Error=new InvalidOperationException("navigation failed")};
        var runner=new StorageSupplyRunner([new(1,"ore",3,"shared")],port,new MemoryStore());Assert.Throws<InvalidOperationException>(()=>runner.Tick());
        Assert.Equal("blocked",runner.Snapshot()["status"]!.GetValue<string>());Assert.Equal("navigation_interrupted",runner.Snapshot()["reason"]!.GetValue<string>());
        Assert.Null(runner.Snapshot()["pending"]);Assert.Empty(port.GlobalPauses);
    }
    [Fact] public void LocalPageFailuresYieldOnlyAfterSafeSceneAndKeepExplicitRetry()
    {
        var time=new ManualTime();var production=new ProductionPort{Frame=new("unknown",Stable:false)};
        var p=new ProductionRunner(production,new MemoryStore(),new("iron","furnace",30),time:time);p.Resume(true);p.Tick();time.Seconds+=46;p.Tick();Assert.False(p.SafeToSwitch);
        production.ReleaseAllowed=true;Assert.True(p.SafeToSwitch);p.Tick();Assert.Empty(production.Clicks);Assert.Equal("paused",p.Snapshot()["status"]!.GetValue<string>());
        var gather=new GatheringPort{Frame=new("unknown",Stable:false)};var g=new GatheringRunner(gather,new MemoryStore(),new("ore",1,"mining","node"),"a",time);g.Resume(true);g.Tick();time.Seconds+=46;g.Tick();Assert.False(g.SafeToSwitch);gather.ReleaseAllowed=true;Assert.True(g.SafeToSwitch);
        var storage=new StoragePort{Frame=new(100,"1","unknown",Stable:false)};var s=new StorageSupplyRunner([new(1,"ore",3,"shared")],storage,new MemoryStore(),time:time);s.Tick();time.Seconds+=46;s.Tick();Assert.False(s.SafeToSwitch);storage.ReleaseAllowed=true;Assert.True(s.SafeToSwitch);Assert.Empty(storage.GlobalPauses);
        s.Pause();Assert.False(s.SafeToSwitch);Assert.Equal("manual",s.Snapshot()["pause_reason"]!.GetValue<string>());
    }
    [Fact] public void ProductionUnknownResultKeepsIntentWithoutPausingSharedInput()
    {
        var port=new ProductionPort();var time=new ManualTime();var runner=new ProductionRunner(port,new MemoryStore(),new("iron","furnace",30),time:time);
        runner.Resume(true);runner.Tick();port.Frame=new("recipe_detail",RecipeKey:"iron",CraftAvailable:true);runner.Tick();
        port.Frame=new("unknown",Stable:false);runner.Tick();time.Seconds+=16;runner.Tick();
        Assert.Equal("unknown",runner.Snapshot()["status"]!.GetValue<string>());Assert.NotNull(runner.Snapshot()["pending"]);
        Assert.Equal(0,port.GlobalPauses);Assert.Contains("reconciliation_required",port.LocalPauses);Assert.Single(port.Clicks);
        runner.Resume(true);runner.Tick();Assert.Single(port.Clicks);
    }
    [Fact]
    public void ConsumableGateBlocksOnlyUnsubmittedBatchAndNeverStopsAccounting()
    {
        var gate = new ConsumableGate(); var port = new GatheringPort(); var store = new MemoryStore();
        var runner = new GatheringRunner(port, store, new("ore", 1, "mining", "node"), "a", new ManualTime(), consumables: gate);
        runner.Resume(true); runner.Tick(); Assert.Equal(0, port.Starts);
        Assert.Equal("consumables_unknown", runner.Snapshot()["pause_reason"]!.GetValue<string>());
        gate.Decision = new(true, Reservation: new JsonObject { ["verified_name"] = "空瓶", ["quantity"] = 10 });
        runner.Resume(true); runner.Tick(); Assert.Equal(1, port.Starts); Assert.NotNull(store.Value!["pending"]!["consumables"]);
        gate.Decision = new(false, "auto_buy_bottles_disabled");
        port.Frame = new("scene", RouteKey: "ore", TaskId: JsonValue.Create(1), TaskStatus: "complete", ActualQuantity: 7, Evidence: new JsonObject { ["source"] = "screen" }); runner.Tick();
        Assert.Equal("complete", runner.Snapshot()["status"]!.GetValue<string>()); Assert.Equal(2, gate.Calls);
        Assert.Equal(10,runner.Snapshot()["consumables"]!["quantity"]!.GetValue<int>());
    }
    [Fact]
    public void ProductionFocusResumeInvalidatesOldEmptySlotsBeforeCraft()
    {
        var port = new ProductionPort(); var runner = new ProductionRunner(port, new MemoryStore(), new("iron", "furnace", 30), time: new ManualTime());
        runner.Resume(true); runner.Tick(); port.Frame = new("recipe_detail", Foreground: false, RecipeKey: "iron", CraftAvailable: true); runner.Tick();
        port.Frame = port.Frame with { Foreground = true }; runner.Tick(); runner.Tick();
        Assert.Empty(port.Clicks); Assert.Equal("queue", runner.Snapshot()["phase"]!.GetValue<string>());
        Assert.Equal("queue", port.Navigation.Last());
    }
    [Fact]
    public void GatheringUnknownIdleReleasesNavigationWithoutInventingYieldOrReplay()
    {
        var store = new MemoryStore(); var port = new GatheringPort(); var route = new GatheringRoute("ore", 1, "mining", "node");
        var runner = new GatheringRunner(port, store, route, "a", new ManualTime()); runner.Resume(true); runner.Tick();
        port.Frame = new("scene", Evidence: new JsonObject { ["navigation_idle_confirmed"] = true }); runner.Tick();
        Assert.True(runner.SafeToSwitch); Assert.Null(runner.Snapshot()["actual_quantity"]);
        var resumed = new GatheringRunner(port, store, route, "a", new ManualTime()); resumed.Resume(true); resumed.Tick();
        Assert.True(resumed.SafeToSwitch); Assert.Equal(1, port.Starts); Assert.NotNull(resumed.Snapshot()["pending"]);
    }
    [Fact]
    public void GatheringStartTimeoutKeepsIntentWithoutPausingSharedInputAndPassiveIdleReleasesTurn()
    {
        var port=new GatheringPort();var time=new ManualTime();var runner=new GatheringRunner(port,new MemoryStore(),new("ore",1,"mining","node"),"a",time);
        runner.Resume(true);runner.Tick();Assert.Equal(1,port.Starts);
        port.Frame=new("unknown");time.Seconds+=301;runner.Tick();
        Assert.Equal("unknown",runner.Snapshot()["status"]!.GetValue<string>());Assert.Equal(0,port.Pauses);
        Assert.False(runner.SafeToSwitch);Assert.NotNull(runner.Snapshot()["pending"]);
        port.Frame=new("scene",Evidence:new JsonObject{["navigation_idle_confirmed"]=true});runner.ObserveOnly();
        Assert.True(runner.SafeToSwitch);Assert.NotNull(runner.Snapshot()["pending"]);
        Assert.Equal(1,port.Starts);Assert.Equal(0,port.Pauses);Assert.Null(runner.Snapshot()["actual_quantity"]);
    }
    [Fact]
    public void GatheringBatchTimeoutRetainsNativeOwnershipUntilObservedCompletion()
    {
        var port=new GatheringPort();var time=new ManualTime();var runner=new GatheringRunner(port,new MemoryStore(),new("ore",1,"mining","node"),"a",time);
        runner.Resume(true);runner.Tick();port.Frame=new("scene",RouteKey:"ore",TaskId:JsonValue.Create("batch"),TaskStatus:"active");runner.Tick();
        time.Seconds+=601;runner.Tick();
        Assert.Equal("unknown",runner.Snapshot()["status"]!.GetValue<string>());Assert.False(runner.SafeToSwitch);Assert.Equal(0,port.Pauses);
        port.Frame=port.Frame with{TaskStatus="complete",Evidence=new JsonObject{["navigation_idle_confirmed"]=true}};runner.ObserveOnly();
        Assert.Equal("complete",runner.Snapshot()["status"]!.GetValue<string>());Assert.True(runner.SafeToSwitch);
        Assert.Equal(1,port.Starts);Assert.Equal(0,port.Pauses);Assert.Null(runner.Snapshot()["actual_quantity"]);
    }
    [Fact]
    public void GatheringUnknownPageOnlyBlocksLocalRunner()
    {
        var port=new GatheringPort{Frame=new("unknown")};var time=new ManualTime();var runner=new GatheringRunner(port,new MemoryStore(),new("ore",1,"mining","node"),"a",time);
        runner.Resume(true);runner.Tick();time.Seconds+=16;runner.Tick();Assert.Equal("running",runner.Snapshot()["status"]!.GetValue<string>());time.Seconds+=30;runner.Tick();
        Assert.Equal("unrecognized_page",runner.Snapshot()["pause_reason"]!.GetValue<string>());Assert.Equal(0,port.Pauses);Assert.Equal(0,port.Starts);
    }
    [Fact]
    public void StorageNeverCreditsDifferentBagOrStalePacketHorizon()
    {
        var port = new StoragePort(); var runner = new StorageSupplyRunner([new(1, "ore", 3, "shared")], port, new MemoryStore()); runner.Tick();
        port.Frame = port.Frame with { At = 101, CaptureId = "2", Available = 7, BagQuantity = 5, BagScope = "other", BalanceAt = 101 }; runner.Tick(); Assert.NotNull(runner.Snapshot()["pending"]);
        port.Frame = port.Frame with { At = 102, CaptureId = "3", BagScope = "bag:1", BalanceAt = 100 }; runner.Tick(); Assert.NotNull(runner.Snapshot()["pending"]);
        Assert.Single(port.Actions);
    }
    [Fact]
    public void FishingRestartPendingCastNeverRecastsAndDoesNotCountDowntime()
    {
        var port = new FishingPort(); var store = new MemoryStore(); var plan = new FishingPlan("lake", 100);
        var runner = new IdleFishingRunner(plan, port, store); runner.Tick();
        port.Frame = new(1, "2", NavigationActive: true); runner.Tick(); port.Frame = new(2, "3", NavigationActive: false); runner.Tick();
        port.Frame = new(3, "4", "fishing", FishingButton: true); runner.Tick();
        runner = new(plan, port, store); runner.Resume();
        port.Frame = new(1000, "5", "fishing", LeisureMode: false, RoundActive: true); runner.Tick();
        Assert.Equal(0, runner.Snapshot()["elapsed_s"]!.GetValue<double>()); Assert.Equal(1, port.Actions.Count(x => x.Kind == "start"));
    }
    [Fact]
    public void DurableStoreRoundTripsOriginalKeysAndPreservesExtensions()
    {
        var path = Path.Combine(Path.GetTempPath(), "mabi-workflow-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new JsonWorkflowStore(path); var port = new ProductionPort(); var recipe = new ProductionRecipe("iron", "furnace", 30);
            var runner = new ProductionRunner(port, store, recipe); var state = runner.Snapshot(); state["future_extension"] = "keep"; store.Flush(state);
            runner = new(port, store, recipe); Assert.Equal("keep", runner.Snapshot()["future_extension"]!.GetValue<string>());
            Assert.Equal(30, store.Read()!["recipe"]!["duration_s"]!.GetValue<double>());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
internal sealed class ConsumableGate : IGatheringConsumables
{
    public int Calls; public GatheringSupplyDecision Decision = new(false, "consumables_unknown");
    public GatheringSupplyDecision ReserveForBatch(GatheringRoute route, string actionId) { Calls++; return Decision; }
}
