using System.IO;
using System.Text.Json.Nodes;
namespace MabiAgent.Desktop.Editors;
// Bounds editor history on disk. Runtime logs rotate on their own; these folders only ever grew:
// one revision file per chain save, deleted chains kept forever, and an anchor history whose
// entries each carry the full change set.
public static class StorageCleanup
{
    public const int KeptRevisions=20;
    public const int KeptAnchorHistory=50;
    public const string DeletedMarker=".deleted-at";
    public static readonly TimeSpan DeletedRetention=TimeSpan.FromDays(30);
    public sealed record Result(int Revisions,int DeletedChains);
    public static Result Run(string chainsDirectory,DateTimeOffset now)
    {
        var revisions=0;var deleted=0;
        var chains=Path.Combine(chainsDirectory,"chains");
        if(Directory.Exists(chains))foreach(var folder in Directory.EnumerateDirectories(chains))revisions+=PruneRevisions(folder);
        var trash=Path.Combine(chainsDirectory,"deleted");
        if(Directory.Exists(trash))foreach(var folder in Directory.EnumerateDirectories(trash))
        {
            var marker=Path.Combine(folder,DeletedMarker);
            // Folders deleted before this marker existed start their retention period now.
            if(!File.Exists(marker)){MarkDeleted(folder,now);continue;}
            if(!DateTimeOffset.TryParse(File.ReadAllText(marker),out var at)||now-at<DeletedRetention)continue;
            try{Directory.Delete(folder,true);deleted++;}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        }
        return new(revisions,deleted);
    }
    // Keeps the newest revision files of one chain; the current chain.json is never touched.
    public static int PruneRevisions(string chainFolder)
    {
        var directory=Path.Combine(chainFolder,"revisions");if(!Directory.Exists(directory))return 0;
        var removed=0;
        var stale=Directory.EnumerateFiles(directory,"v*.json")
            .Select(path=>(path,number:int.TryParse(Path.GetFileNameWithoutExtension(path)[1..],out var n)?n:-1))
            .Where(x=>x.number>=0).OrderByDescending(x=>x.number).Skip(KeptRevisions);
        foreach(var(path,_)in stale)try{File.Delete(path);removed++;}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        return removed;
    }
    public static void MarkDeleted(string folder,DateTimeOffset now)=>File.WriteAllText(Path.Combine(folder,DeletedMarker),now.ToString("O"));
    // Entry 0 is the installed preset and stays restorable; older user revisions drop first.
    public static void TrimAnchorHistory(JsonArray history){while(history.Count>KeptAnchorHistory)history.RemoveAt(1);}
}
