using System.Text.Json.Nodes;
using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Workflows.Tests;
// These fixtures are exact snapshots captured from the retired Python runners using injected ports and clocks.
// They verify migration parity only; they are not current game acceptance evidence.
// Storage balance_source reflects the current packet-only inventory contract.
public sealed class LegacyParityTests
{
    private static void Match(string key, params JsonObject[] trace)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-traces.json")))!;
        var expected = root[key]!.AsArray(); Assert.Equal(expected.Count, trace.Length);
        for (var i = 0; i < trace.Length; i++)
        {
            // JsonNode distinguishes CLR int/long/double nodes; a JSON roundtrip matches their wire representation.
            var actual = JsonNode.Parse(trace[i].ToJsonString());
            // Per-slot timing is new native metadata; legacy control and accounting remain exact.
            if(key=="production"&&actual is JsonObject production)production.Remove("slot_due_at");
            // The user-approved bounded network wait is now 300s, previously 120s.
            // Compare the historical trace using its explicit original timeout.
            if(key=="gathering"&&i==trace.Length-1)
            {
                Assert.Null(actual!["actual_quantity"]);Assert.Equal("packet_reconcile_required",actual["reason"]!.GetValue<string>());
                expected[i]!["actual_quantity"]=null;expected[i]!["reason"]="packet_reconcile_required";
            }
            // The approved movement binding changed; preserve the historical fixture as migration evidence.
            if(key=="fishing")
            {
                // Dynamic cast-button disappearance was retired by the fixed
                // cast script; it is no longer part of migrated intent state.
                expected[i]?["pending"]?.AsObject().Remove("button_disappeared");
                expected[i]!["next_direction"]=expected[i]!["next_direction"]!.GetValue<string>()=="a"?"w":"s";
                // New dismount bookkeeping is covered by native fishing tests.
                if(actual is JsonObject fish&&fish.ContainsKey("dismount_at")){Assert.Null(fish["dismount_at"]);fish.Remove("dismount_at");}
                // Cast latency and preflight retry counters are additive diagnostics.
                if(actual is JsonObject fishState){fishState.Remove("cast_preflight_rejections");fishState.Remove("last_cast_elapsed_ms");}
            }
            if(key=="maintenance"&&actual?["repair"] is JsonObject repair&&repair.ContainsKey("gold_reserve"))
            {Assert.Equal(40000,repair["gold_reserve"]!.GetValue<long>());repair.Remove("gold_reserve");}
            if(key=="maintenance"&&actual?["backpack"] is JsonObject bag)bag.Remove("check_due_at");
            if(key=="maintenance"&&actual?["repair"] is JsonObject payment)
            {
                if(payment.ContainsKey("quote_is_upper_bound")){Assert.False(payment["quote_is_upper_bound"]!.GetValue<bool>());payment.Remove("quote_is_upper_bound");}
                if(payment.ContainsKey("actual_spent")){Assert.Equal(1000,payment["actual_spent"]!.GetValue<long>());payment.Remove("actual_spent");}
            }
            if(key=="maintenance"&&actual is JsonObject maintenance&&maintenance.ContainsKey("navigation_cleanup"))
            {
                Assert.True(maintenance["navigation_cleanup"]!.GetValue<bool>());Assert.True(maintenance["navigation_reserved"]!.GetValue<bool>());
                maintenance.Remove("navigation_cleanup");maintenance["navigation_reserved"]=false;
            }
            Assert.True(JsonNode.DeepEquals(expected[i], actual), $"{key} step {i}: expected {expected[i]} actual {actual}");
        }
    }
    [Fact]
    public void ProductionTraceMatchesPython()
    {
        var port = new ProductionPort(); var runner = new ProductionRunner(port, new MemoryStore(), new("iron", "furnace", 30), 2, new ManualTime(), deviceQueueTail: _ => 150);
        var trace = new List<JsonObject> { runner.Snapshot(), runner.Resume(true), runner.Tick() };
        port.Frame = new("recipe_detail", RecipeKey: "iron", CraftAvailable: true); trace.Add(runner.Tick());
        port.Frame = new("furnace_panel", EmptySlots: [1], SlotCount: 2); trace.Add(runner.Tick());Assert.Equal(180,runner.EarliestCollectionAt);Match("production", trace.ToArray());
    }
    [Fact]
    public void GatheringTraceMatchesPython()
    {
        var port = new GatheringPort(); var runner = new GatheringRunner(port, new MemoryStore(), new("ore", 1, "mining", "node"), "action", new ManualTime(), timeoutSeconds:120);
        var trace = new List<JsonObject> { runner.Snapshot(), runner.Resume(true), runner.Tick() };
        port.Frame = new("scene", RouteKey: "ore", TaskId: JsonValue.Create(7), TaskStatus: "active"); trace.Add(runner.Tick());
        port.Frame = port.Frame with { TaskStatus = "complete", ActualQuantity = 3, Evidence = new JsonObject { ["source"] = "screen" } }; trace.Add(runner.Tick()); Match("gathering", trace.ToArray());
    }
    [Fact]
    public void StorageTraceMatchesPython()
    {
        var port = new StoragePort(); var runner = new StorageSupplyRunner([new(1, "ore", 3, "shared")], port, new MemoryStore());
        var trace = new List<JsonObject> { runner.Snapshot(), runner.Tick() };
        port.Frame = port.Frame with { At = 101, CaptureId = "2", Available = 7, BagQuantity = 5, BalanceAt = 101 }; trace.Add(runner.Tick()); Match("storage", trace.ToArray());
    }
    [Fact]
    public void FishingTraceMatchesPython()
    {
        var port = new FishingPort(); var runner = new IdleFishingRunner(new("lake", 2), port, new MemoryStore()); var trace = new List<JsonObject> { runner.Snapshot(), runner.Tick() };
        FishingFrame[] frames = [new(1, "2", NavigationActive: true), new(2, "3", NavigationActive: false), new(3, "4", "fishing", FishingButton: true),
            new(4, "5", "fishing", LeisureMode: false, RoundActive: true), new(6, "6", "fishing", LeisureMode: false, RoundActive: true), new(7, "7", FishingButton: true, Compass: true)];
        foreach (var frame in frames) { port.Frame = frame; trace.Add(runner.Tick()); } Match("fishing", trace.ToArray());
    }
    [Fact]
    public void MaintenanceTraceMatchesPython()
    {
        var time = new ManualTime(); var port = new MaintenancePort(); var runner = new MaintenanceRunner(port, new MemoryStore(), new(IntervalSeconds: 1), time);
        var trace = new List<JsonObject> { runner.StatusSnapshot() }; time.Seconds = 102; trace.Add(runner.Tick());
        port.Frame = new("2", "repair_tools", Gold: 99000); trace.Add(runner.Tick()); trace.Add(runner.Tick());
        port.Frame = new("3", "repair_tools", Gold: 99000, ToolsEquipped: true); trace.Add(runner.Tick()); Match("maintenance", trace.ToArray());
        port.Frame=new("4","scene");Assert.False(runner.Tick()["navigation_reserved"]!.GetValue<bool>());
    }
}
