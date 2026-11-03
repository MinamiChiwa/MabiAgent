using System.Text.Json;
using System.Text.Json.Nodes;
namespace MabiAgent.Core;

public sealed record JobView(string Id,string Kind,long ItemId,long Target,long Completed,string? Recipe,bool Paused,bool Cancelled,bool Pending);

// Preserve extension fields in the existing schema instead of rebuilding user data.
public sealed partial class QueueDocument
{
    private JsonObject document;
    internal bool Migrated {get;private set;}
    private JsonArray Rows=>document["jobs"]!.AsArray();
    public void ResetAllExecution()=>Mutate(()=>
    {
        var automatic=Rows.OfType<JsonObject>().Where(r=>r["job_id"]!.GetValue<string>().StartsWith("auto-material:",StringComparison.Ordinal)).Select(r=>r["job_id"]!.GetValue<string>()).ToHashSet();
        foreach(var row in Rows.OfType<JsonObject>().ToArray())
        {
            if(row["job_id"]!.GetValue<string>().StartsWith("auto-material:",StringComparison.Ordinal)){Rows.Remove(row);continue;}
            row["completed_quantity"]=0L;row["pending"]=null;row["receipts"]=new JsonArray();row["paused"]=false;row["cancelled"]=false;
            foreach(var key in new[]{"recovery","reviewed_actions","legacy_material_progress","round_group_stock","round_group_through"})row.Remove(key);
            var deps=row["depends_on"]!.AsArray();foreach(var dep in deps.Where(n=>automatic.Contains(n!.GetValue<string>())).ToArray())deps.Remove(dep);
        }
        document.Remove("deleted_action_ids");
    });
    public QueueDocument(JsonObject source)
    {
        document=(JsonObject)source.DeepClone();
        if(document["schema_version"]?.GetValue<int>()!=1 || document["jobs"] is not JsonArray) throw new InvalidDataException("不支持的队列格式");
        Validate();
        MigrateSchedulerState();
    }
    private void MigrateSchedulerState()
    {
        if(document["scheduler_version"]?.GetValue<int>() is >=3)return;
        foreach(var row in Rows.Select(n=>n!.AsObject()).Where(r=>r["kind"]?.GetValue<string>()=="gathering"))
        {
            if(row["quantity_unit"]?.GetValue<string>()=="native_round")continue;
            if(row["pending"] is not null){row["quantity_unit"]="legacy_material";continue;}
            ConvertLegacyGathering(row);
        }
        document["scheduler_version"]=3;
        Migrated=true;
        Validate();
    }
    private static void ConvertLegacyGathering(JsonObject row)
    {
        var finished=row["completed_quantity"]!.GetValue<long>()>=row["target_quantity"]!.GetValue<long>();
        row["legacy_material_progress"]=new JsonObject{["target_quantity"]=row["target_quantity"]?.DeepClone(),["completed_quantity"]=row["completed_quantity"]?.DeepClone(),["receipts"]=row["receipts"]?.DeepClone(),["recovery"]=row["recovery"]?.DeepClone()};
        row["quantity_unit"]="native_round";row["target_quantity"]=1L;row["completed_quantity"]=finished?1L:0L;row["receipts"]=new JsonArray();row.Remove("recovery");row.Remove("round_group_stock");row.Remove("round_group_through");
        if(finished)row["recovery"]=new JsonObject{["confirmed_quantity_baseline"]=1L,["evidence"]="legacy_completed_gathering_migrated_to_native_round"};
    }
    public bool IsLegacyGathering(string id)=>Row(id)["quantity_unit"]?.GetValue<string>()=="legacy_material";
    public void CloseLegacyGathering(string id,string action,bool completedRound=false)=>Mutate(()=>
    {
        var row=Row(id);if(!IsLegacyGathering(id)||row["pending"]?["action_id"]?.GetValue<string>()!=action)throw new InvalidOperationException("旧采集记录不匹配");
        var ended=row["pending"]!.DeepClone();row["pending"]=null;ConvertLegacyGathering(row);row["legacy_material_progress"]!["ended_native_action"]=ended;
        if(completedRound&&!id.StartsWith("auto-material:",StringComparison.Ordinal)&&row["completed_quantity"]!.GetValue<long>()==0)
        {
            row["completed_quantity"]=1L;
            row["receipts"]!.AsArray().Add(new JsonObject{["action_id"]=action,["receipt_id"]=action+":native-round",["quantity"]=1L,["evidence"]="confirmed_native_gathering_round_after_legacy_migration"});
        }
    });
    public IReadOnlyList<JobView> Jobs=>Rows.Select(n=>new JobView(
        n!["job_id"]!.GetValue<string>(),n["kind"]!.GetValue<string>(),n["target_item_id"]!.GetValue<long>(),
        n["target_quantity"]!.GetValue<long>(),n["completed_quantity"]!.GetValue<long>(),n["recipe_key"]?.GetValue<string>(),
        n["paused"]!.GetValue<bool>(),n["cancelled"]!.GetValue<bool>(),n["pending"] is not null)).ToArray();
    public JsonObject ToJson()=>(JsonObject)document.DeepClone();
    public void Move(string id,int direction,string? neighborId=null)
    {
        if(direction is not (-1 or 1)) throw new ArgumentException("排序方向无效");
        Mutate(()=>
        {
            var jobs=Jobs;
            var indices=jobs.Select((j,i)=>(j,i)).Where(x=>x.j.Id.StartsWith("auto-material:")==id.StartsWith("auto-material:")).Select(x=>x.i).ToArray();
            var current=Array.FindIndex(indices,i=>jobs[i].Id==id);
            if(current<0)throw new ArgumentException("任务不存在");
            var other=current+direction;
            if(neighborId is not null)
            {
                other=Array.FindIndex(indices,i=>jobs[i].Id==neighborId);
                if(other<0||other==current||Math.Sign(other-current)!=direction)throw new ArgumentException("排序目标不在同组或方向无效");
            }
            if(other<0||other>=indices.Length)return;
            var row=Rows[indices[current]];Rows.RemoveAt(indices[current]);Rows.Insert(indices[other],row);
        });
    }
    public static QueueDocument Read(string path)
    {
        var raw=File.ReadAllBytes(path);if(raw.Length>4*1024*1024)throw new InvalidDataException("队列文件超过大小限制");
        return new(JsonNode.Parse(raw)?.AsObject() ?? throw new InvalidDataException("空队列文件"));
    }
    public void Save(string path)=>AtomicJson.Write(path,document);
    // Explicit removal is independent of execution/reconciliation state.
    public void Delete(IEnumerable<string> ids)=>Mutate(()=>
    {
        var selected=ids.ToHashSet(StringComparer.Ordinal);
        var removed=Rows.Where(n=>selected.Contains(n!["job_id"]!.GetValue<string>())).ToArray();
        var actions=document["deleted_action_ids"] as JsonArray??new JsonArray();
        if(actions.Parent is null)document["deleted_action_ids"]=actions;
        foreach(var row in removed)
        {
            var removedActions=(row!["receipts"]?.AsArray()??[]).Select(r=>r?["action_id"]?.GetValue<string>())
                .Concat((row["reviewed_actions"]?.AsArray()??[]).Select(r=>r?["pending"]?["action_id"]?.GetValue<string>()))
                .Append(row["pending"]?["action_id"]?.GetValue<string>());
            foreach(var action in removedActions.OfType<string>().Distinct())if(!actions.Any(n=>n?.GetValue<string>()==action))actions.Add(action);
            Rows.Remove(row);
        }
        foreach(var row in Rows)
        {
            var deps=row!["depends_on"]!.AsArray();
            foreach(var dep in deps.Where(d=>selected.Contains(d!.GetValue<string>())).ToArray())deps.Remove(dep);
        }
    });
    public bool IsActionDeleted(string action)=>document["deleted_action_ids"]?.AsArray().Any(n=>n?.GetValue<string>()==action)==true;
    public void SetTargets(IReadOnlyDictionary<string,long> targets)=>Mutate(()=>
    {foreach(var (id,quantity) in targets)SetTarget(id,quantity);});
    public void SetTargetQuantity(string id,long quantity)=>Mutate(()=>SetTarget(id,quantity));
    private void SetTarget(string id,long quantity)
    {
        var row=Row(id);
        if(!Amount(quantity)||quantity<row["completed_quantity"]!.GetValue<long>())throw new InvalidOperationException("目标数量必须为正整数，且不能低于已确认数量");
        if(row["pending"] is not null||row["cancelled"]!.GetValue<bool>())throw new InvalidOperationException("请先处理当前批次或待核账，再修改数量");
        if(id.StartsWith("auto-material:"))throw new InvalidOperationException("自动补充数量由需求计算，请修改对应的用户目标");
        row["target_quantity"]=quantity;
    }
}

public static class AtomicJson
{
    public static void Write(string path,JsonNode value)
    {
        path=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
            {
                JsonSerializer.Serialize(file,value,new JsonSerializerOptions{WriteIndented=true});file.Flush(true);
            }
            File.Move(temp,path,true);
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
}
