using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Tests;
public sealed class QueueEditingTests
{
    private static QueueDocument Empty()=>new(new JsonObject{["schema_version"]=1,["jobs"]=new JsonArray()});
    [Fact] public void ExplicitFishingMisrouteReviewKeepsIntentAndCreditsNoTime()
    {
        var q=Empty();q.Add("fish","idle_fishing",100,1800,"lake",[]);q.Begin("fish","cast","character:fish");
        var before=q.ToJson();Assert.Throws<ArgumentException>(()=>q.ResolveFishingMisroute("fish","cast","",DateTimeOffset.UtcNow));
        Assert.True(JsonNode.DeepEquals(before,q.ToJson()));
        Assert.Throws<InvalidOperationException>(()=>q.ResolveFishingMisroute("fish","other","operator confirmed",DateTimeOffset.UtcNow));
        q.ResolveFishingMisroute("fish","cast","operator confirmed compass navigation",DateTimeOffset.UtcNow);
        Assert.Equal("queued",q.Status("fish"));Assert.Equal(0,q.Jobs[0].Completed);Assert.Equal(1800,q.Jobs[0].Target);
        var review=q.ToJson()["jobs"]![0]!["reviewed_actions"]![0]!;
        Assert.Equal("cast",review["pending"]!["action_id"]!.GetValue<string>());Assert.NotNull(review["evidence"]);
        q=new(q.ToJson());q.Begin("fish","next","character:fish");Assert.True(q.Jobs[0].Pending);
    }
    [Fact] public void ExplicitFishingNoEffectReviewKeepsZeroCreditAndAuditEvidence()
    {
        var q=Empty();q.Add("fish","idle_fishing",100,1800,"lake",[]);q.Begin("fish","cast","character:fish");
        q.ResolveFishingNoEffect("fish","cast","operator confirmed no visible click effect",DateTimeOffset.UtcNow);
        Assert.Equal("queued",q.Status("fish"));Assert.Equal(0,q.Jobs[0].Completed);Assert.False(q.Jobs[0].Pending);
        var review=q.ToJson()["jobs"]![0]!["reviewed_actions"]![0]!;
        Assert.Equal("user_confirmed_no_input_effect",review["outcome"]!.GetValue<string>());Assert.Equal("operator confirmed no visible click effect",review["evidence"]!.GetValue<string>());
    }
    [Fact] public void DeleteRemovesPendingCompletedCancelledAndDependenciesAtomically()
    {
        var q=Empty();q.Add("pending","production",100,10,"r",[]);q.Begin("pending","act","device:m");
        q.Add("done","production",100,1,"r",[]);q.Begin("done","a2","device:n");q.Confirm("done","a2","rec",1,"screen");
        q.Add("cancelled","gathering",1,10,"g",[]);q.Control("cancelled","cancel");
        q.Add("independent","production",100,5,"r",["pending","done"]);
        q.Delete(["pending","done","cancelled","already-gone"]);
        Assert.Single(q.Jobs);Assert.Equal("independent",q.Jobs[0].Id);Assert.Equal("queued",q.Status("independent"));Assert.True(q.IsActionDeleted("act"));Assert.True(q.IsActionDeleted("a2"));
        q=new(q.ToJson());Assert.Single(q.Jobs);q.Delete(["pending"]);Assert.Single(q.Jobs);
    }
    [Fact] public void BatchTargetEditRejectsInvalidMemberWithoutPartialChanges()
    {
        var q=Empty();q.Add("a","production",100,10,"r",[]);q.Add("b","production",100,20,"r",[]);q.Begin("b","act","device:m");
        var before=q.ToJson();Assert.Throws<InvalidOperationException>(()=>q.SetTargets(new Dictionary<string,long>{{"a",40},{"b",40}}));Assert.True(JsonNode.DeepEquals(before,q.ToJson()));
    }
    [Fact] public void QuantityEditKeepsConfirmedProgressAndUnknownFields()
    {
        var q=Empty();q.Add("a","production",100,10,"r",[]);
        q.Begin("a","act","device:m");q.Confirm("a","act","receipt",3,"screen");
        var raw=q.ToJson();raw["jobs"]![0]!["extension"]="retained";q=new(raw);
        q.SetTargetQuantity("a",5);Assert.Equal(5,q.Jobs[0].Target);Assert.Equal(3,q.Jobs[0].Completed);
        Assert.Equal("retained",q.ToJson()["jobs"]![0]!["extension"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(()=>q.SetTargetQuantity("a",2));Assert.Equal(5,q.Jobs[0].Target);
    }
    [Fact] public void PendingCannotBeEditedAndAutomaticQuantityRemainsPlannerOwned()
    {
        var q=Empty();q.Add("a","production",100,10,"r",[]);q.Begin("a","act","device:m");
        Assert.Throws<InvalidOperationException>(()=>q.SetTargetQuantity("a",20));
        q.Add("auto-material:g:1","gathering",1,10,"g",[]);
        Assert.Throws<InvalidOperationException>(()=>q.SetTargetQuantity("auto-material:g:1",20));
        Assert.True(q.Jobs[0].Pending);
    }
    [Fact] public void VisibleNeighborMovePreservesDependenciesAndOtherGroupOrder()
    {
        var q=Empty();q.Add("a","production",100,10,"r",[]);q.Add("auto-material:g:1","gathering",1,10,"g",[]);
        q.Add("hidden","production",200,5,"s",[]);q.Add("b","production",100,5,"r",["a"]);
        q.Move("b",-1,"a");Assert.Equal(new[]{"b","a","auto-material:g:1","hidden"},q.Jobs.Select(j=>j.Id));
        Assert.Equal("waiting_dependencies",q.Status("b"));
        Assert.Throws<ArgumentException>(()=>q.Move("a",1,"auto-material:g:1"));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("bad recipe")]
    public void InvalidProductionRecipeIsRejected(string? recipe)
    {var q=Empty();Assert.Throws<InvalidDataException>(()=>q.Add("a","production",100,1,recipe,[]));Assert.Empty(q.Jobs);}
}
