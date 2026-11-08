using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Workflows.Tests;

public sealed class ProductionProcessRecoveryTests:IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"production-recovery-"+Guid.NewGuid().ToString("N"));
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
