using System.Security.Cryptography;
using System.Text.Json.Nodes;
namespace MabiAgent.Core;

// One writer owns a queue. All edits are cloned, validated and flushed before
// becoming visible. Input callbacks run only after their intent is durable.
public sealed class QueueStore : IDisposable
{
    private readonly object gate=new();
    private readonly string path;
    private readonly FileStream lease;
    private QueueDocument document;
    private string? fingerprint;
    private bool disposed;
    public long Revision {get;private set;}
    public QueueStore(string path,string? expectedFingerprint=null,bool checkVersion=false,bool requireExisting=false)
    {
        this.path=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        lease=AcquireLease(this.path+".lock");
        try
        {
            if(requireExisting&&!File.Exists(this.path))throw new FileNotFoundException("既有队列暂不可读，禁止重建为空队列",this.path);
            document=File.Exists(this.path)?QueueDocument.Read(this.path):new(new JsonObject{["schema_version"]=1,["jobs"]=new JsonArray()});
            fingerprint=Fingerprint();
            if(checkVersion&&fingerprint!=expectedFingerprint)throw new IOException("队列已有更新，请刷新后再操作");
            if(document.Migrated&&File.Exists(this.path)){document.Save(this.path);fingerprint=Fingerprint();}
            Update(q=>q.PruneCancelled());
        }
        catch{lease.Dispose();throw;}
    }
    private string? Fingerprint()=>File.Exists(path)?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))):null;
    // Retry acquisition only: the edit/input callback has not run yet.
    private static FileStream AcquireLease(string path)
    {
        var started=System.Diagnostics.Stopwatch.StartNew();
        while(true)
        {
            try{return new(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose);}
            catch(IOException error) when((error.HResult&0xffff) is 32 or 33 && started.ElapsedMilliseconds<750)
            {Thread.Sleep(25);}
        }
    }
    public QueueDocument Snapshot(){lock(gate){ObjectDisposedException.ThrowIf(disposed,this);return new(document.ToJson());}}
    public void Update(Action<QueueDocument> change)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(fingerprint!=Fingerprint())throw new IOException("队列被其他程序修改，请重新加载后再操作");
            var next=new QueueDocument(document.ToJson());change(next);next.PruneCancelled();
            if(JsonNode.DeepEquals(next.ToJson(),document.ToJson()))return;
            next.Save(path);fingerprint=Fingerprint();document=next;Revision++;
        }
    }
    public void ExecuteClaimed(string job,string action,string resource,Action input)
    {
        lock(gate){Update(q=>q.Begin(job,action,resource));input();}
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;lease.Dispose();}}
}
