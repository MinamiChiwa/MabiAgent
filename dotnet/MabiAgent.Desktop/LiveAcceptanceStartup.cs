using System.Text.Json.Nodes;
namespace MabiAgent.Desktop;

// Startup decisions shared by the live acceptance entry and focused tests.
internal static class LiveAcceptanceStartup
{
    public static bool HasRestoredPendingWork(string dataPath)
    {
        var windowStatePath=Path.Combine(dataPath,"windows");
        if(!Directory.Exists(windowStatePath))return false;
        var restoredNativeBatch=Directory.EnumerateFiles(windowStatePath,"gathering.json",SearchOption.AllDirectories).Any(path=>JsonNode.Parse(File.ReadAllText(path)) is{} state&&state["phase"]?.GetValue<string>()=="gathering"&&state["pending"] is null);
        var restoredCollection=Directory.EnumerateFiles(windowStatePath,"production.json",SearchOption.AllDirectories).Any(path=>JsonNode.Parse(File.ReadAllText(path))?["pending"]?["kind"]?.GetValue<string>()=="collect");
        return restoredNativeBatch||restoredCollection;
    }

    public static async Task<bool> WaitForAsync(Func<bool> condition,DateTimeOffset deadline,Func<int,Task> delay,Func<DateTimeOffset>? clock=null,CancellationToken token=default)
    {
        var now=clock??(()=>DateTimeOffset.UtcNow);
        while(!condition()&&now()<deadline){await delay(100);token.ThrowIfCancellationRequested();}
        return condition();
    }
}
