using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Tests;
public sealed class MigrationTests
{
    [Fact] public void LegacyManualGatheringPreservesMaterialHistoryAndUnknownPending()
    {
        var q=new QueueDocument(new JsonObject{["schema_version"]=1,["jobs"]=new JsonArray()});q.Add("manual","gathering",1,50,"ore",[]);q.Begin("manual","sent","gathering");
        var raw=q.ToJson();raw["scheduler_version"]=2;raw["jobs"]![0]!.AsObject().Remove("quantity_unit");var migrated=new QueueDocument(raw);
        Assert.True(migrated.IsLegacyGathering("manual"));Assert.True(migrated.Jobs.Single().Pending);Assert.Equal(50,migrated.Jobs.Single().Target);
        migrated.CloseLegacyGathering("manual","sent",true);var row=migrated.ToJson()["jobs"]![0]!;
        Assert.Equal(1L,Assert.Single(row["receipts"]!.AsArray())!["quantity"]!.GetValue<long>());
        Assert.Equal("complete",migrated.Status("manual"));Assert.Equal(50,row["legacy_material_progress"]!["target_quantity"]!.GetValue<long>());Assert.Equal("sent",row["legacy_material_progress"]!["ended_native_action"]!["action_id"]!.GetValue<string>());
        var fresh=new QueueDocument(new JsonObject{["schema_version"]=1,["jobs"]=new JsonArray()});fresh.Add("rounds","gathering",1,3,"ore",[]);Assert.Equal("native_round",fresh.ToJson()["jobs"]![0]!["quantity_unit"]!.GetValue<string>());Assert.Equal(3,fresh.Jobs.Single().Target);
    }
    [Fact] public void LegacyAutomaticMaterialCountsArePreservedButNeverBecomeRounds()
    {
        var q=new QueueDocument(new JsonObject{["schema_version"]=1,["jobs"]=new JsonArray()});q.Add("auto-material:ore","gathering",1,50,"ore",[]);q.Begin("auto-material:ore","a","gathering");q.Confirm("auto-material:ore","a","receipt",27,"old material yield");
        var raw=q.ToJson();raw.Remove("scheduler_version");raw["future_extension"]="keep";raw["jobs"]![0]!.AsObject().Remove("quantity_unit");raw["jobs"]![0]!["custom_note"]="keep";
        var migrated=new QueueDocument(raw);var row=migrated.ToJson()["jobs"]![0]!;
        Assert.Equal(3,migrated.ToJson()["scheduler_version"]!.GetValue<int>());Assert.Equal(0,migrated.Jobs.Single().Completed);Assert.Equal(1,migrated.Jobs.Single().Target);
        Assert.Equal(27,row["legacy_material_progress"]!["completed_quantity"]!.GetValue<long>());Assert.Equal("native_round",row["quantity_unit"]!.GetValue<string>());Assert.Equal("keep",row["custom_note"]!.GetValue<string>());Assert.Equal("keep",migrated.ToJson()["future_extension"]!.GetValue<string>());
        migrated.ReplanGatheringGroup("auto-material:ore",3,"new-stock",false);Assert.Equal(3,migrated.Jobs.Single().Target);
        var again=new QueueDocument(migrated.ToJson());Assert.Equal(27,again.ToJson()["jobs"]![0]!["legacy_material_progress"]!["completed_quantity"]!.GetValue<long>());
    }
    [Fact] public void LegacyPendingGatheringStaysInOriginalUnitAndDeletionCannotResurrectIt()
    {
        var q=new QueueDocument(new JsonObject{["schema_version"]=1,["jobs"]=new JsonArray()});q.Add("auto-material:ore","gathering",1,50,"ore",[]);q.Begin("auto-material:ore","sent","gathering");
        var raw=q.ToJson();raw.Remove("scheduler_version");raw["jobs"]![0]!.AsObject().Remove("quantity_unit");var migrated=new QueueDocument(raw);
        Assert.True(migrated.IsLegacyGathering("auto-material:ore"));Assert.True(migrated.Jobs.Single().Pending);Assert.Equal(50,migrated.Jobs.Single().Target);
        migrated.ReplanGatheringGroup("auto-material:ore",2,"new",false);Assert.Equal(50,migrated.Jobs.Single().Target);
        migrated.Delete(["auto-material:ore"]);var again=new QueueDocument(migrated.ToJson());Assert.Empty(again.Jobs);Assert.True(again.IsActionDeleted("sent"));
    }
    [Fact] public void SatisfiedAutomaticSupplyStillCollectsAlreadyProducedOutput()
    {
        var scheduler=new Scheduler();
        WorkCandidate[] rows=[new("done","production","metal",true,0,0,Collectible:true,Automatic:true,BufferSatisfied:true),
            new("ore","gathering",null,true,0,1)];
        Assert.Equal("done",scheduler.Select(rows,new(null,false,null))!.Id);
    }
    [Fact] public void BottleBatchRequiresTenAndUnknownDoesNotBecomeZero()
    {
        var p = new ConsumablePolicy("空瓶", 1, 10);
        Assert.Equal(SupplyStatus.Unknown, p.Plan(null).Status);
        Assert.Equal(7, p.Plan(3).PurchaseQuantity);
        Assert.Equal(10, p.Plan(10).ReservedQuantity);
        Assert.Equal(SupplyStatus.Ready, p.Plan(10).Status);
        Assert.Equal(0, p.Plan(400).PurchaseQuantity);
    }
    [Fact] public void PartialBatchUsesAttemptCountNotYield()
    {
        var b = new GatheringBatch("water", 10, 10);
        b.ObserveAttempts(4); b.ObserveAttempts(4);
        Assert.Equal(6,b.RemainingConsumables);
        Assert.Null(b.OutputQuantity);
        Assert.Throws<InvalidOperationException>(()=>b.ObserveAttempts(3));
        b.ObserveAttempts(10);b.Complete(27);
        Assert.Equal(27,b.OutputQuantity);
    }
    [Fact] public void InventoryUnknownAndScopeRemainDistinct()
    {
        var i = new InventorySnapshot("bag:character");
        i.Set(new(1,60,"bag:character","packet","v2",true));
        Assert.Null(i.Query(2).Quantity);
        Assert.Throws<ArgumentException>(()=>i.Set(new(1,90,"warehouse","packet","v3",true)));
        i.SetStorage("shared",new Dictionary<long,long?>{{1,500}});
        Assert.Equal(60,i.Query(1).Quantity);
        Assert.Equal(500,i.Storage["shared"][1]);
    }
    [Fact] public void ActiveBatchIsNeverPreemptedAndIdleDeviceWins()
    {
        var s=new Scheduler();
        var c=new[]{new WorkCandidate("full","production","fabric",false,0,2),new WorkCandidate("empty","production","wood",true,5,1),new WorkCandidate("ore","gathering",null,true,0,0)};
        Assert.Equal("empty",s.Select(c,new(null,false,null))?.Id);
        Assert.Equal("ore",s.Select(c,new("ore",true,null))?.Id);
    }
    [Fact] public void CurrentDeviceRefillsAcrossRecipesAndDeficitsRotate()
    {
        var s=new Scheduler();
        Assert.Equal("wood",s.Select(new[]{new WorkCandidate("metal","production","metal",true,5,0),new WorkCandidate("wood","production","wood",true,2,1)},new(null,false,"wood"))?.Id);
        Assert.Equal("fiber",s.Select(new[]{new WorkCandidate("ore","gathering",null,true,0,0,Coverage:2),new WorkCandidate("fiber","gathering",null,true,0,1,Coverage:0.1)},new(null,false,null))?.Id);
    }
    [Fact] public void OrdinaryProductionKeepsUserOrderWithoutChangingOnsiteOrDuePriority()
    {
        var s=new Scheduler();WorkCandidate[] rows=[new("first","production","metal",true,1,0),new("second","production","wood",true,5,1)];
        Assert.Equal("first",s.Select(rows,new(null,false,null))?.Id);
        Assert.Equal("second",s.Select(rows,new(null,false,"wood"))?.Id);
        Assert.Equal("due",s.Select([..rows,new WorkCandidate("due","production","fabric",true,0,2,Collectible:true)],new(null,false,null))?.Id);
    }
    [Fact] public void JsonRoundtripPreservesUnknownFieldsAndExactLargeIds()
    {
        var raw=JsonNode.Parse("""{"schema_version":1,"jobs":[{"job_id":"a","kind":"production","target_item_id":4712562853295720,"target_quantity":10,"recipe_key":"r","depends_on":[],"paused":false,"cancelled":false,"completed_quantity":0,"pending":null,"receipts":[],"future_extension":{"a":true}}]}""")!.AsObject();
        var q=new QueueDocument(raw);
        Assert.Equal(4712562853295720,q.Jobs.Single().ItemId);
        q.Move("a",1);
        Assert.True(q.ToJson()["jobs"]![0]!["future_extension"]!["a"]!.GetValue<bool>());
    }
}
