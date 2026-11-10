using System.Text.Json.Nodes;
using Xunit;
namespace MabiAgent.Workflows.Tests;

public sealed class CompletedDeviceCollectionTests
{
    [Theory]
    [InlineData("metal")][InlineData("wood")][InlineData("fabric")]
    [InlineData("leather")][InlineData("chemical")][InlineData("food")]
    public void FullReadyDeviceCollectsUnknownAndOwnedSlotsButCreditsOnlyKnownBatch(string device)
    {
        var time=new ManualTime{Seconds=2000};var store=new MemoryStore();
        var port=new ProductionPort{Frame=new(device+"_panel",EmptySlots:[],ReadySlots:[0,1,2,3,4],CollectAvailable:true,SlotCount:5)};
        var recipe=new ProductionRecipe("fuel",device,60);var runner=new ProductionRunner(port,store,recipe,4,time);
        var state=runner.Snapshot();state["submitted"]=3;state["owned_slots"]=new JsonArray(2,3,4);
        state["phase"]="waiting";state["status"]="waiting";state["reason"]="collection_owner_unverified";
        state["production_due_at"]=90d;state["due_at"]=2300d;state["collection_retry_at"]=2300d;
        store.Flush(state);runner=new(port,store,recipe,4,time);runner.Resume(true);runner.Tick();
        Assert.Equal("collect",Assert.Single(port.Clicks));
        Assert.Equal(new[]{0,1,2,3,4},runner.Snapshot()["pending"]!["target_slots"]!.AsArray().Select(n=>n!.GetValue<int>()));
        // Restart with a sent intent observes the result instead of replaying it.
        runner=new(port,store,recipe,4,time);runner.Resume(true);Assert.Single(port.Clicks);
        port.Frame=port.Frame with{EmptySlots=[0,1,2,3,4],ReadySlots=[]};runner.ObserveOnly();
        Assert.Null(runner.Snapshot()["pending"]);Assert.Equal(3,runner.Snapshot()["completed"]!.GetValue<int>());
        Assert.Empty(runner.Snapshot()["owned_slots"]!.AsArray());Assert.Single(port.Clicks);
    }

    [Fact]
    public void UnknownCompletedSlotCompactsKnownRunningSlotsWithoutCreditingThem()
    {
        var time=new ManualTime();var store=new MemoryStore();
        var port=new ProductionPort{Frame=new("metal_panel",EmptySlots:[3,4],ReadySlots:[0],CollectAvailable:true,SlotCount:5)};
        IReadOnlyDictionary<string,int[]> Owners()=>new Dictionary<string,int[]>{{"peer",[1,2]}};
        var runner=new ProductionRunner(port,store,new("a","metal",60),time:time,collectionOwners:Owners);
        var state=runner.Snapshot();state["phase"]="collect";store.Flush(state);
        runner=new(port,store,new("a","metal",60),time:time,collectionOwners:Owners);runner.Resume(true);runner.Tick();
        Assert.Equal("collect",Assert.Single(port.Clicks));
        port.Frame=port.Frame with{EmptySlots=[2,3,4],ReadySlots=[]};runner.Tick();
        Assert.Equal(0,runner.Snapshot()["completed"]!.GetValue<int>());
        var receipt=Assert.Single(runner.Snapshot()["collection_receipts"]!.AsArray());
        Assert.True(receipt!["compact"]!.GetValue<bool>());Assert.Empty(receipt["owners"]!["peer"]!.AsArray());
        Assert.Equal(new[]{0},receipt["cleared"]!.AsArray().Select(n=>n!.GetValue<int>()));
    }
}
