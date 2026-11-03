using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Tests;
public sealed class QueueStoreTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"mabi-queue-"+Guid.NewGuid().ToString("N"));
    private string PathName=>Path.Combine(directory,"queue.json");
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
    [Fact] public void KnownQueueMissingAfterLeaseCannotBeRecreated()
    {
        Directory.CreateDirectory(directory);Assert.Throws<FileNotFoundException>(()=>new QueueStore(PathName,requireExisting:true));Assert.False(File.Exists(PathName));
    }
    [Fact] public async Task TransientLeaseContentionWaitsBeforeRunningEditOnce()
    {
        var held=new QueueStore(PathName);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        var edit=Task.Run(()=>{entered.SetResult();using var next=new QueueStore(PathName);next.Update(q=>{calls++;q.Add("b","gathering",1,2,"g",[]);});});
        await entered.Task;await Task.Delay(75);Assert.False(edit.IsCompleted);held.Dispose();await edit;
        Assert.Equal(1,calls);Assert.Equal("b",QueueDocument.Read(PathName).Jobs.Single().Id);
    }
    [Fact] public void DurableIntentSurvivesInputFailureAndCannotRepeat()
    {
        using var store=new QueueStore(PathName);
        store.Update(q=>q.Add("a","production",100,1,"r",[]));
        Assert.Throws<IOException>(()=>store.ExecuteClaimed("a","act","device:metal",()=>
        {Assert.True(QueueDocument.Read(PathName).Jobs.Single().Pending);throw new IOException("input interrupted");}));
        Assert.True(store.Snapshot().Jobs.Single().Pending);
        Assert.Throws<InvalidOperationException>(()=>store.ExecuteClaimed("a","another","device:metal",()=>Assert.Fail("must not replay")));
    }
    [Fact] public void OnlyOneWriterAndExternalChangesCannotBeOverwritten()
    {
        using var store=new QueueStore(PathName);
        Assert.Throws<IOException>(()=>new QueueStore(PathName));
        store.Update(q=>q.Add("a","gathering",1,10,"g",[]));
        var external=QueueDocument.Read(PathName);external.Add("b","production",2,1,"r",[]);external.Save(PathName);
        Assert.Throws<IOException>(()=>store.Update(q=>q.Control("a","cancel")));
        Assert.Equal(2,QueueDocument.Read(PathName).Jobs.Count);
        Assert.False(store.Snapshot().Jobs.Single().Cancelled);
    }
    [Fact] public void FailedChangePreservesDataAndNoopDoesNotWrite()
    {
        using var store=new QueueStore(PathName);
        store.Update(q=>q.Add("a","gathering",1,10,"g",[]));
        var before=File.ReadAllText(PathName);var revision=store.Revision;
        Assert.Throws<InvalidOperationException>(()=>store.Update(q=>q.Begin("missing","act","character")));
        Assert.Equal(before,File.ReadAllText(PathName));Assert.Equal(revision,store.Revision);
        store.Update(q=>q.Add("a","gathering",1,10,"g",[]));Assert.Equal(revision,store.Revision);
    }
    [Fact] public void GatheringRoundGroupDoesNotGrowWithoutNewStockAndRefreshBlocksReplay()
    {
        using var store=new QueueStore(PathName);
        store.Update(q=>{q.Add("auto-material:g","gathering",1,3,"g",[]);q.ReplanGatheringGroup("auto-material:g",3,"v1",false,99);q.Begin("auto-material:g","a","gathering");q.Confirm("auto-material:g","a","r",1,"native round");});
        var persisted=QueueDocument.Read(PathName);
        Assert.Equal(3,persisted.ToJson()["scheduler_version"]!.GetValue<int>());
        Assert.Equal("native_round",persisted.ToJson()["jobs"]![0]!["quantity_unit"]!.GetValue<string>());
        Assert.Equal(1,persisted.Jobs.Single().Completed);
        store.Update(q=>q.ReplanGatheringGroup("auto-material:g",3,"v1",false,100));
        Assert.Equal(3,store.Snapshot().Jobs.Single().Target);
        store.Update(q=>{q.Begin("auto-material:g","b","gathering");q.Confirm("auto-material:g","b","s",2,"native rounds");q.ReplanGatheringGroup("auto-material:g",3,"v2",true,101);});
        Assert.Equal(3,store.Snapshot().Jobs.Single().Target);
        store.Update(q=>q.ReplanGatheringGroup("auto-material:g",2,"v1",false,101));
        Assert.Equal(5,store.Snapshot().Jobs.Single().Target);
        store.Update(q=>
        {
            q.ReplanGatheringGroup("auto-material:g",0,"v2",false,102);
            Assert.True(q.Jobs.Single().Cancelled);
        });
        // QueueStore prunes withdrawn automatic jobs after the edit is validated.
        Assert.Empty(store.Snapshot().Jobs);
        Assert.Empty(QueueDocument.Read(PathName).Jobs);
    }
    [Fact] public void EndedAutomaticBatchRetiresWithoutCreditingUnknownYield()
    {
        using var store=new QueueStore(PathName);
        store.Update(q=>{q.Add("auto-material:g:1","gathering",1,10,"g",[]);q.Begin("auto-material:g:1","act","character");});
        store.Update(q=>q.ReleaseFinishedGathering("auto-material:g:1","act"));
        Assert.True(store.Snapshot().Jobs.Single().Pending);
        store.Update(q=>{
            q.RetireEndedAutomaticGathering();
            var retired=Assert.Single(q.Jobs);Assert.True(retired.Cancelled);Assert.False(retired.Pending);Assert.Equal(0,retired.Completed);
        });
        Assert.Empty(store.Snapshot().Jobs); // Current policy prunes empty cancelled history.
        Assert.Empty(QueueDocument.Read(PathName).Jobs);
        store.Update(q=>q.Add("auto-material:g:1","gathering",1,5,"g",[]));
        Assert.Equal("queued",store.Snapshot().Status("auto-material:g:1"));
    }
}
