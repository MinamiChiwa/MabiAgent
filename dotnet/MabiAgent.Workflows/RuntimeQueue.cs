using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json.Nodes;
using MabiAgent.Core;
namespace MabiAgent.Workflows;

// Short queue leases allow UI editing between ticks. A durable batch is bound
// to the queue before any runner can send input. Paths never use raw job IDs.
public sealed class RuntimeQueue(string directory)
{
    public string DirectoryPath {get;}=Path.GetFullPath(directory);
    public string PathName=>Path.Combine(DirectoryPath,"work-queue.json");
    public void ResetAllExecution()
    {
        // Caller has stopped the worker and disposed every writer before this explicit destructive action.
        var root=Path.GetFullPath(DirectoryPath);
        var marker=Path.Combine(root,"stop-reset.json");
        AtomicJson.Write(marker,new JsonObject{["requested_at"]=DateTimeOffset.UtcNow.ToString("O")});
        var windows=Path.Combine(root,"windows");
        if(Directory.Exists(windows))
        {
            if((File.GetAttributes(windows)&FileAttributes.ReparsePoint)!=0)throw new IOException("执行状态目录不得为链接");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"batch.json","production.json","gathering.json","idle-fishing.json","storage-supply.json","maintenance.json","surplus-sale.json"};
            var options=new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint,IgnoreInaccessible=false};
            foreach(var file in Directory.EnumerateFiles(windows,"*.json",options).Where(f=>names.Contains(Path.GetFileName(f))))File.Delete(file);
        }
        foreach(var name in new[]{"maintenance.json","native-input-intents.json","runtime-inventory.json","storage-supply.json","travel-wings.json","relogin.json","surplus-sale.json","gathering-reservations.json","bottle-purchase.json","bottle-purchase-context.json","decomposition.json"})File.Delete(Path.Combine(root,name));
        Edit(q=>q.ResetAllExecution());
        File.Delete(marker);
    }
    private bool queueKnown=File.Exists(Path.Combine(Path.GetFullPath(directory),"work-queue.json"));
    public QueueDocument Read()
    {
        if(File.Exists(PathName)){queueKnown=true;return QueueDocument.Read(PathName);}
        if(queueKnown)throw new FileNotFoundException("既有队列暂不可读，保留运行批次，不按空队列处理",PathName);
        return new(new JsonObject{["schema_version"]=1,["jobs"]=new JsonArray()});
    }
    public void Edit(Action<QueueDocument> change){_=Read();using var q=new QueueStore(PathName,requireExisting:queueKnown);q.Update(change);queueKnown|=File.Exists(PathName);}
    private static string Hash(string s)=>Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));
    public string BatchDirectory(string scope,string job,string action)=>Path.Combine(DirectoryPath,"windows",Hash(scope),"targets",Hash(job),Hash(action));
    public JsonObject Claim(JobView job,string scope,string kind,string resource,JsonObject details)
    {
        var action="native-"+Guid.NewGuid().ToString("N");
        var folder=BatchDirectory(scope,job.Id,action);
        var batch=(JsonObject)details.DeepClone();batch["schema_version"]=1;batch["job_id"]=job.Id;batch["action_id"]=action;
        batch["scope"]=scope;batch["kind"]=kind;batch["resource"]=resource+":batch:"+action;batch["target_item_id"]=job.ItemId;
        AtomicJson.Write(Path.Combine(folder,"batch.json"),batch);
        try{Edit(q=>q.Begin(job.Id,action,batch["resource"]!.GetValue<string>()));}
        catch{File.Delete(Path.Combine(folder,"batch.json"));Directory.Delete(folder);throw;}
        return batch;
    }
    public (JsonObject Batch,string Folder)? Restore(JsonObject job,string scope)
    {
        if(job["pending"] is not JsonObject pending)return null;
        var action=pending["action_id"]!.GetValue<string>();var id=job["job_id"]!.GetValue<string>();
        var folder=BatchDirectory(scope,id,action);
        if(!File.Exists(Path.Combine(folder,"batch.json")))return null;
        var batch=JsonNode.Parse(File.ReadAllText(Path.Combine(folder,"batch.json")))!.AsObject();
        if(batch["job_id"]?.GetValue<string>()!=id||batch["action_id"]?.GetValue<string>()!=action||batch["resource"]?.GetValue<string>()!=pending["resource"]?.GetValue<string>()||batch["target_item_id"]?.GetValue<long>()!=job["target_item_id"]!.GetValue<long>())throw new InvalidDataException("批次与队列不一致，保留核账");
        if(batch["scope"]?.GetValue<string>()!=scope||batch["kind"]?.GetValue<string>()!=job["kind"]?.GetValue<string>())throw new InvalidDataException("批次角色范围或类型不匹配");
        var key=batch["kind"]!.GetValue<string>() switch{"production"=>batch["recipe"]?["key"],"gathering"=>batch["route_key"],_=>batch["point_key"]};
        if(key?.GetValue<string>()!=job["recipe_key"]?.GetValue<string>())throw new InvalidDataException("批次配方或采集点不匹配");
        return (batch,folder);
    }
    public void Finish(JsonObject batch,long quantity,string evidence)
        =>Edit(q=>q.Confirm(batch["job_id"]!.GetValue<string>(),batch["action_id"]!.GetValue<string>(),batch["action_id"]!.GetValue<string>()+":complete",quantity,evidence));
    public void Release(JsonObject batch)=>Edit(q=>q.ReleaseUnsubmitted(batch["job_id"]!.GetValue<string>(),batch["action_id"]!.GetValue<string>()));
    public bool ReleaseRetiredUnsubmitted(JsonObject job,Func<string,bool>? isRetired=null)
    {
        if(job["pending"] is not JsonObject pending||pending["action_id"]?.GetValue<string>() is not {} action||!action.StartsWith("native-",StringComparison.Ordinal))return false;
        var root=Path.Combine(DirectoryPath,"windows");if(!Directory.Exists(root))return false;
        var scopes=Directory.EnumerateDirectories(root).Take(10001).ToArray();if(scopes.Length>10000)return false;
        var id=job["job_id"]!.GetValue<string>();
        var matches=scopes.Select(scope=>Path.Combine(scope,"targets",Hash(id),Hash(action))).Where(folder=>File.Exists(Path.Combine(folder,"batch.json"))).Take(2).ToArray();
        if(matches.Length!=1)return false;
        var batch=JsonNode.Parse(File.ReadAllText(Path.Combine(matches[0],"batch.json")))!.AsObject();
        var oldScope=batch["scope"]?.GetValue<string>();
        if(oldScope is null||!(isRetired??ProcessIsRetired)(oldScope))return false;
        var verified=Restore(job,oldScope);if(verified is null||!Path.GetFullPath(verified.Value.Folder).Equals(Path.GetFullPath(matches[0]),StringComparison.OrdinalIgnoreCase))return false;
        var file=batch["kind"]?.GetValue<string>() switch{"production"=>"production.json","gathering"=>"gathering.json","idle_fishing"=>"idle-fishing.json",_=>null};
        if(file is null||!File.Exists(Path.Combine(matches[0],file)))return false;
        var state=JsonNode.Parse(File.ReadAllText(Path.Combine(matches[0],file)))!.AsObject();
        if(file=="production.json"&&(!JsonNode.DeepEquals(state["recipe"],batch["recipe"])||state["count"]?.GetValue<int>()!=batch["rounds"]?.GetValue<int>()))return false;
        if(file=="gathering.json"&&(state["route"]?["key"]?.GetValue<string>()!=batch["route_key"]?.GetValue<string>()||state["action_id"]?.GetValue<string>()!=action))return false;
        if(file=="idle-fishing.json"&&(state["plan"]?["point_key"]?.GetValue<string>()!=batch["point_key"]?.GetValue<string>()
            ||state["plan"]?["duration_s"]?.GetFiniteDouble()!=batch["duration_s"]?.GetFiniteDouble()
            ||state["elapsed_s"]?.GetFiniteDouble()!=0||state["round_seen"]?.GetValue<bool>()!=false||state["navigation_seen"]?.GetValue<bool>()!=false))return false;
        if(state["schema_version"]?.GetValue<int>()!=1||state["submitted"]?.GetValue<int>()!=0||state["completed"]?.GetValue<int>()!=0
            ||!state.ContainsKey("pending")||state["pending"] is not null||state["owned_slots"] is not JsonArray {Count:0}
            ||state["status"]?.GetValue<string>() is not ("idle" or "running" or "paused" or "complete"))return false;
        Release(batch);return true;
    }
    private static bool ProcessIsRetired(string scope)
    {
        var pieces=scope.Split(':');if(pieces.Length<4||pieces[0]!="process"||!int.TryParse(pieces[1],out var pid)||pid<=0||!long.TryParse(pieces[2],out var ticks)||ticks<=0)return false;
        try{using var process=Process.GetProcessById(pid);return process.HasExited||process.StartTime.ToUniversalTime().Ticks!=ticks;}
        catch(ArgumentException){return true;}
        catch(Exception error) when(error is InvalidOperationException or System.ComponentModel.Win32Exception){return false;}
    }
}
