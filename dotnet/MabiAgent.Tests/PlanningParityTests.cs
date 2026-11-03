using System.Text.Json;
using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Tests;
public sealed class PlanningParityTests
{
    [Fact] public void GatheringEstimateKeepsUnknownBaselineAsOneRound()
    {
        var estimate=MaterialPlanner.EstimateGatheringRounds(1,0,10,true);
        Assert.Equal(1,estimate.Rounds);Assert.True(estimate.UnknownBaseline);
        Assert.Equal(3,MaterialPlanner.EstimateGatheringRounds(1,21,10).Rounds);
    }
    [Fact] public void UnconfirmedNumericStockCannotAuthorizeProduction()
    {
        var stock=new InventorySnapshot("s");stock.Set(new(1,100,"s","estimate","v",false));
        var plan=new MaterialPlanner([new("r",2,1,new(){[1]=10},"metal")]).Plan([new("job",2,1,RecipeKey:"r")],stock);
        Assert.Equal(0,Assert.Single(plan.Tasks).ReadyRounds);Assert.Contains(plan.Issues,i=>i.Kind=="inventory_unknown"&&i.ItemId==1);
    }
    public static IEnumerable<object[]> Cases()
    {
        var file=Path.Combine(AppContext.BaseDirectory,"Fixtures/material-plans.json");
        foreach(var node in JsonNode.Parse(File.ReadAllText(file))!.AsArray()) yield return [node!.ToJsonString()];
    }
    [Theory,MemberData(nameof(Cases))]
    public void NativePlanMatchesOriginal(string json)
    {
        var c=JsonNode.Parse(json)!;
        var recipes=c["recipes"]!.Deserialize<RecipeDefinition[]>(PlanningJson.Options)!;
        var gathering=c["gathering"]!.Deserialize<Dictionary<long,string>>(PlanningJson.Options)!;
        var goals=c["goals"]!.Deserialize<MaterialGoal[]>(PlanningJson.Options)!;
        var future=c["future"]!.Deserialize<FutureSupply[]>(PlanningJson.Options)!;
        var i=c["inventory"]!;var inventory=new InventorySnapshot(i["scope"]!.GetValue<string>());
        foreach(var row in i["quantities"]!.AsObject()) inventory.Set(new(long.Parse(row.Key),row.Value?.GetValue<long>(),inventory.Scope,"estimate","case",row.Value is not null));
        foreach(var row in i["storage"]!.AsObject()) inventory.SetStorage(row.Key,row.Value!.Deserialize<Dictionary<long,long?>>()!);
        var plan=new MaterialPlanner(recipes,gathering).Plan(goals,inventory,future);
        var actual=JsonSerializer.SerializeToNode(plan,PlanningJson.Options)!;
        Assert.True(JsonNode.DeepEquals(c["expected"],actual),c["name"]+" expected:"+c["expected"]+" actual:"+actual);
    }
}
