using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Tests;
public sealed class QueueTests
{
    [Fact]public void FishingActivityIsNotInventoryReconciliation()
    {
        var q=Empty();q.Add("fish","idle_fishing",1,60,"lake",[]);q.Begin("fish","f","character:idle_fishing");
        Assert.Equal("active_fishing",q.Status("fish"));q.Control("fish","cancel");Assert.True(q.Jobs.Single().Pending);
        q.Add("craft","production",2,1,"r",[]);q.Begin("craft","p","device:metal");Assert.Equal("reconciliation_required",q.Status("craft"));
    }
    [Fact]public void EmptyWithdrawnAutomaticHistoryIsDeletedButDependenciesRemain()
    {
        var q=Empty();q.Add("auto-material:empty","gathering",1,10,"g",[]);q.ReplanAutomatic("auto-material:empty",0);
        Assert.Equal(1,q.PruneCancelled());Assert.Empty(q.Jobs);
        q.Add("auto-material:needed","gathering",1,10,"g",[]);q.Add("dependent","production",2,1,"r",["auto-material:needed"]);
        q.ReplanAutomatic("auto-material:needed",0);Assert.Equal(0,q.PruneCancelled());Assert.Equal(2,q.Jobs.Count);
    }
    private static QueueDocument Empty()=>new(JsonNode.Parse("""{"schema_version":1,"jobs":[]}""")!.AsObject());
    [Fact]public void ConfirmationIsIdempotentAndCancellingNeverErasesIntent()
    {
        var q=Empty();q.Add("a","production",100,10,"r",[]);q.Begin("a","act","device:metal");
        q.Control("a","cancel");Assert.True(q.Jobs.Single().Pending);
        q.Confirm("a","act","receipt",3,"screen empty slots");q.Confirm("a","act","receipt",3,"screen empty slots");
        Assert.Equal(3,q.Jobs.Single().Completed);Assert.False(q.Jobs.Single().Pending);
        Assert.Throws<InvalidOperationException>(()=>q.Confirm("a","act","receipt",4,"screen empty slots"));
    }
    [Fact]public void DependenciesAndSharedResourcesBlockOnlyRelevantWork()
    {
        var q=Empty();q.Add("a","production",100,1,"r",[]);q.Add("b","production",200,1,"s",["a"]);q.Add("c","gathering",1,10,"g",[]);
        Assert.Equal("waiting_dependencies",q.Status("b"));
        Assert.Throws<InvalidOperationException>(()=>q.Begin("b","b1","device:other"));
        q.Begin("a","a1","device:metal");
        Assert.Throws<InvalidOperationException>(()=>q.Begin("c","c1","device:metal"));
        Assert.False(q.Jobs.Single(j=>j.Id=="c").Pending);
        q.Begin("c","c1","character:gathering");q.Confirm("a","a1","r1",1,"screen");
        Assert.Equal("queued",q.Status("b"));
    }
    [Fact]public void AutomaticRowsReuseOnlyWithdrawnLifecycleAndSortWithinGroup()
    {
        var q=Empty();q.Add("a","production",100,1,"r",[]);q.Add("auto-material:x:1","gathering",1,10,"g",[]);q.Add("b","production",200,1,"r2",[]);
        q.Move("a",1);Assert.Equal(new[]{"auto-material:x:1","b","a"},q.Jobs.Select(j=>j.Id));
        q.ReplanAutomatic("auto-material:x:1",0);q.ReplanAutomatic("auto-material:x:1",20,true);
        Assert.False(q.Jobs[0].Cancelled);Assert.Equal(20,q.Jobs[0].Target);
        q.Control("auto-material:x:1","cancel");
        Assert.Throws<InvalidOperationException>(()=>q.ReplanAutomatic("auto-material:x:1",10,true));
    }
    [Fact]public void ActiveCapacityIgnoresHistory()
    {
        var q=Empty();for(var n=0;n<260;n++){q.Add("j"+n,"gathering",1,1,null,[]);q.Control("j"+n,"cancel");}
        q.Add("live","gathering",1,1,null,[]);Assert.Equal(261,q.Jobs.Count);
    }
    [Fact]public void CancelledHistoryWithConfirmedOutputIsRemovedWhenSettled()
    {
        var q=Empty();q.Add("auto-material:x:1","gathering",1,10,"g",[]);
        q.Begin("auto-material:x:1","a","gathering");q.Confirm("auto-material:x:1","a","r",3,"screen");
        q.ReplanAutomatic("auto-material:x:1",0);Assert.Equal(1,q.PruneCancelled());Assert.Empty(q.Jobs);
    }
    [Fact]public void PendingProductionKeepsPeerReceiptsUntilAccountingEnds()
    {
        var q=Empty();q.Add("a","production",100,10,"r",[]);q.Begin("a","a1","device:m");q.Confirm("a","a1","r1",3,"screen");
        q.Add("b","production",200,10,"r2",[]);q.Begin("b","b1","device:n");q.Control("a","cancel");
        Assert.Equal(0,q.PruneCancelled());q.ReleaseUnsubmitted("b","b1");Assert.Equal(1,q.PruneCancelled());Assert.Equal("b",Assert.Single(q.Jobs).Id);
    }
    [Fact]public void ClosedReviewDoesNotKeepAnEmptyCancelledGoal()
    {var q=Empty();q.Add("a","production",100,10,"r",[]);q.Begin("a","a1","device:m");q.ResolveReview("a","a1",DateTimeOffset.UtcNow);Assert.Equal(1,q.PruneCancelled());Assert.Empty(q.Jobs);}
    [Fact]public void RecoveredHistoricalProgressIsSeparateFromNewReceipts()
    {
        var q=Empty();q.Add("auto-material:x","gathering",1,100,"g",[]);var raw=q.ToJson();var row=raw["jobs"]![0]!;
        row["completed_quantity"]=31L;row["recovery"]=new JsonObject{["confirmed_quantity_baseline"]=31L,["evidence"]="Pre-loss queue summary, preserved recovery report"};
        q=new(raw);Assert.Empty(q.ToJson()["jobs"]![0]!["receipts"]!.AsArray());
        q.Begin("auto-material:x","new-action","gathering");q.Confirm("auto-material:x","new-action","new-receipt",2,"fresh screen");
        Assert.Equal(33,Assert.Single(q.Jobs).Completed);Assert.Single(q.ToJson()["jobs"]![0]!["receipts"]!.AsArray());
        Assert.Equal(31,q.ToJson()["jobs"]![0]!["recovery"]!["confirmed_quantity_baseline"]!.GetValue<long>());
    }
    [Theory][InlineData(-1,"source")][InlineData(3,"")][InlineData(9007199254740992,"source")]
    public void InvalidRecoveryBaselineDoesNotBypassReceiptValidation(long baseline,string evidence)
    {
        var q=Empty();q.Add("a","gathering",1,100,"g",[]);var raw=q.ToJson();raw["jobs"]![0]!["recovery"]=new JsonObject{["confirmed_quantity_baseline"]=baseline,["evidence"]=evidence};
        Assert.Throws<InvalidDataException>(()=>new QueueDocument(raw));
    }
}
