using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Workflows.Tests;

public sealed class ProductionProcessRecoveryTests:IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"production-recovery-"+Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData(null,true,true,true)]
    [InlineData("pending",true,true,false)]
    [InlineData("round_seen",true,true,false)]
    [InlineData("navigation_seen",true,true,false)]
    [InlineData("manual",true,true,false)]
    [InlineData(null,false,true,false)]
    [InlineData(null,true,false,false)]
    public void RetiredYieldedFishingPreservesDurationAndRequiresSafeSameCharacter(string? change,bool sameOwner,bool retired,bool restored)
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("fish","idle_fishing",3,36000,"sea",[]));
        var prior="process:123:456:789:owner:[\"account\",\"character\"]";
        var next="process:234:567:890:owner:[\"account\",\""+(sameOwner?"character":"other")+"\"]";
        var batch=queue.Claim(queue.Read().Jobs[0],prior,"idle_fishing","character:idle_fishing",new(){["point_key"]="sea",["duration_s"]=36000d});
        var source=queue.BatchDirectory(prior,"fish",batch["action_id"]!.GetValue<string>());
        var state=JsonNode.Parse("""{"schema_version":1,"plan":{"point_key":"sea","duration_s":36000},"status":"yielded","phase":"yielded","elapsed_s":4029.66,"submitted":180,"completed":173,"pending":null,"owned_slots":[],"round_seen":false,"navigation_seen":false}""")!.AsObject();
        if(change=="pending")state["pending"]=new JsonObject{["kind"]="reel"};
        else if(change=="manual"){state["status"]="paused";state["pause_reason"]="manual";}
        else if(change is not null)state[change]=true;
        AtomicJson.Write(Path.Combine(source,"idle-fishing.json"),state);var journal=File.ReadAllText(Path.Combine(source,"idle-fishing.json"));
        var result=queue.Restore(queue.Read().ToJson()["jobs"]![0]!.AsObject(),next,_=>retired);
        Assert.Equal(restored,result is not null);
        Assert.Equal(journal,File.ReadAllText(Path.Combine(result?.Folder??source,"idle-fishing.json")));
        Assert.True(queue.Read().Jobs[0].Pending);
    }
    [Theory]
    [InlineData("[\"account\",\"character\"]",true,true)]
    [InlineData("[\"account\",\"other\"]",true,false)]
    [InlineData("[\"account\",\"character\"]",false,false)]
    [InlineData(null,true,false)]
    public void RestoresOnlyRetiredProductionForVerifiedSameCharacter(string? owner,bool retired,bool restored)
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));
        var prior="process:123:456:789:owner:[\"account\",\"character\"]";
        var next="process:234:567:890"+(owner is null?"":":owner:"+owner);
        var batch=queue.Claim(queue.Read().Jobs[0],prior,"production","device:metal",new(){["recipe"]=new JsonObject{["key"]="iron"}});
        var source=queue.BatchDirectory(prior,"iron",batch["action_id"]!.GetValue<string>());
        var journal="""{"status":"unknown","phase":"collect","submitted":2,"completed":0,"owned_slots":[3,4],"pending":null,"reason":"collection_receipt_missing"}""";
        File.WriteAllText(Path.Combine(source,"production.json"),journal);
        var job=queue.Read().ToJson()["jobs"]![0]!.AsObject();
        var result=queue.Restore(job,next,_=>retired);
        Assert.Equal(restored,result is not null);
        if(result is{} current)
        {
            Assert.Equal(next,current.Batch["scope"]!.GetValue<string>());
            Assert.Equal(journal,File.ReadAllText(Path.Combine(current.Folder,"production.json")));
            Assert.False(Directory.Exists(source));Assert.NotNull(queue.Restore(job,next));
        }
        else Assert.Equal(journal,File.ReadAllText(Path.Combine(source,"production.json")));
        Assert.True(queue.Read().Jobs[0].Pending);
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
