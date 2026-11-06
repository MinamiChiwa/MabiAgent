using MabiAgent.Desktop;
using Xunit;
namespace MabiAgent.Tests;

public sealed class LiveAcceptanceStartupTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"mabi-live-acceptance-"+Guid.NewGuid().ToString("N"));
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}

    [Fact] public void EmptyDataDirectoryHasNoRestoredWork()
    {
        Directory.CreateDirectory(root);
        Assert.False(LiveAcceptanceStartup.HasRestoredPendingWork(root));
    }

    [Fact] public void RestoredNativeBatchAndPendingCollectionAreBothRecognized()
    {
        var native=Path.Combine(root,"windows","native","targets","skill");Directory.CreateDirectory(native);
        File.WriteAllText(Path.Combine(native,"gathering.json"),"""{"phase":"gathering","pending":null}""");
        Assert.True(LiveAcceptanceStartup.HasRestoredPendingWork(root));

        var collection=Path.Combine(root,"windows","collection");Directory.CreateDirectory(collection);
        File.WriteAllText(Path.Combine(root,"windows","native","targets","skill","gathering.json"),"""{"phase":"complete","pending":null}""");
        File.WriteAllText(Path.Combine(collection,"production.json"),"""{"pending":{"kind":"collect"}}""");
        Assert.True(LiveAcceptanceStartup.HasRestoredPendingWork(root));
    }

    [Fact] public async Task WaitKeepsPollingUntilListeningBeforeDeadline()
    {
        var now=DateTimeOffset.UnixEpoch;var polling=0;var listening=false;
        var result=await LiveAcceptanceStartup.WaitForAsync(()=>listening,now.AddSeconds(2),milliseconds=>{polling++;now=now.AddMilliseconds(milliseconds);if(polling==2)listening=true;return Task.CompletedTask;},()=>now);
        Assert.True(result);Assert.Equal(2,polling);
    }

    [Fact] public async Task WaitFailsAtDeadlineWithoutListening()
    {
        var now=DateTimeOffset.UnixEpoch;
        var result=await LiveAcceptanceStartup.WaitForAsync(()=>false,now.AddSeconds(2),milliseconds=>{now=now.AddMilliseconds(milliseconds);return Task.CompletedTask;},()=>now);
        Assert.False(result);Assert.Equal(TimeSpan.FromSeconds(2),now-DateTimeOffset.UnixEpoch);
    }
}
