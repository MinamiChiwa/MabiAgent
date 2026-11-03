using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace MabiAgent.Core;
public sealed partial class QueueDocument
{
    public long Revision {get;private set;}
    private static bool Token(string? value)=>value is not null&&Regex.IsMatch(value,@"^[A-Za-z0-9_.:-]{1,128}$");
    private static bool Amount(long value)=>value>0&&value<=9007199254740991;
    private JsonObject Row(string id)=>Rows.Select(n=>n!.AsObject()).FirstOrDefault(n=>n["job_id"]!.GetValue<string>()==id)??throw new InvalidOperationException("任务不存在");
    private void Mutate(Action mutation)
    {
        var previous=document;document=(JsonObject)previous.DeepClone();
        try{mutation();Validate();if(!JsonNode.DeepEquals(previous,document))Revision++;}
        catch{document=previous;throw;}
    }
    private void Validate()
    {
        var jobs=Jobs;
        if(jobs.Select(j=>j.Id).Distinct().Count()!=jobs.Count||jobs.Any(j=>!Token(j.Id)||!Amount(j.ItemId)||!Amount(j.Target)||j.Completed<0||j.Completed>9007199254740991||j.Kind is not ("production" or "gathering" or "idle_fishing")
            ||(j.Kind is "production" or "idle_fishing" ? !Token(j.Recipe) : j.Recipe is not null&&!Token(j.Recipe))))throw new InvalidDataException("队列字段无效");
        if(jobs.Count(j=>j.Pending||!j.Cancelled&&j.Completed<j.Target)>256)throw new InvalidDataException("活动任务超过上限");
        HashSet<string> actions=[],receipts=[],resources=[];
        foreach(var row in Rows.Select(n=>n!.AsObject()))
        {
            var deps=row["depends_on"]!.AsArray().Select(n=>n!.GetValue<string>()).ToArray();
            if(deps.Length>256||deps.Distinct().Count()!=deps.Length||deps.Any(d=>!Token(d)))throw new InvalidDataException("依赖字段无效");
            long total=0;var kind=row["kind"]!.GetValue<string>();
            if(row["recovery"]?["confirmed_quantity_baseline"] is {} baseline)
            {
                if(baseline is not JsonValue number||!number.TryGetValue<long>(out total)||total<0||total>9007199254740991||string.IsNullOrWhiteSpace(row["recovery"]?["evidence"]?.GetValue<string>()))throw new InvalidDataException("历史恢复基数或来源无效");
            }
            foreach(var r in row["reviewed_actions"]?.AsArray()??[])
                if(!actions.Add(r!["pending"]!["action_id"]!.GetValue<string>()))throw new InvalidDataException("动作重复");
            foreach(var r in row["receipts"]!.AsArray())
            {
                var action=r!["action_id"]!.GetValue<string>();var receipt=r["receipt_id"]!.GetValue<string>();var qty=r["quantity"]!.GetValue<long>();
                if(!Token(action)||!Token(receipt)||!actions.Add(action)||!receipts.Add(receipt)||qty<0||qty==0&&kind!="gathering"||string.IsNullOrWhiteSpace(r["evidence"]?.GetValue<string>()))throw new InvalidDataException("收据无效或重复");
                total=checked(total+qty);
            }
            if(total!=row["completed_quantity"]!.GetValue<long>())throw new InvalidDataException("产量与确认收据不一致");
            if(row["pending"] is { } p)
            {
                var action=p["action_id"]!.GetValue<string>();var resource=p["resource"]!.GetValue<string>();
                if(!Token(action)||!Token(resource)||!actions.Add(action)||p["resource_released"]?.GetValue<bool>()!=true&&!resources.Add(resource))throw new InvalidOperationException("动作或资源已占用");
            }
        }
        var graph=Rows.ToDictionary(n=>n!["job_id"]!.GetValue<string>(),n=>n!["depends_on"]!.AsArray().Select(x=>x!.GetValue<string>()).ToArray());
        HashSet<string> done=[];
        void Visit(string key,HashSet<string> path)
        {
            if(!graph.ContainsKey(key)||!path.Add(key))throw new InvalidDataException("依赖缺失或循环");
            if(!done.Contains(key)){foreach(var dep in graph[key])Visit(dep,path);done.Add(key);}path.Remove(key);
        }
        foreach(var key in graph.Keys)Visit(key,[]);
    }
    public string Status(string id)
    {
        var j=Row(id);bool Flag(string key)=>j[key]!.GetValue<bool>();
        if(j["pending"]?["balance_reconciled"]?.GetValue<bool>()==true)return "cancelled";
        if(j["pending"] is not null)return j["kind"]?.GetValue<string>()=="idle_fishing"?"active_fishing":"reconciliation_required";
        if(Flag("cancelled"))return "cancelled";
        if(j["completed_quantity"]!.GetValue<long>()>=j["target_quantity"]!.GetValue<long>())return "complete";
        if(Flag("paused"))return "paused";
        if(j["depends_on"]!.AsArray().Any(d=>Status(d!.GetValue<string>())!="complete"))return "waiting_dependencies";
        return "queued";
    }
    public void Add(string id,string kind,long item,long quantity,string? recipe,string[] dependencies)
    {
        var intent=new JsonObject{["job_id"]=id,["kind"]=kind,["target_item_id"]=item,["target_quantity"]=quantity,["recipe_key"]=recipe,["depends_on"]=new JsonArray(dependencies.Select(d=>(JsonNode?)JsonValue.Create(d)).ToArray())};
        if(Jobs.Any(j=>j.Id==id))
        {var old=Row(id);if(intent.Any(p=>!JsonNode.DeepEquals(old[p.Key],p.Value)))throw new InvalidOperationException("任务标识属于其他目标");return;}
        Mutate(()=>{intent["paused"]=false;intent["cancelled"]=false;intent["completed_quantity"]=0L;intent["pending"]=null;intent["receipts"]=new JsonArray();if(kind=="gathering")intent["quantity_unit"]="native_round";Rows.Add(intent);});
    }
    public void Control(string id,string command)=>Mutate(()=>
    {
        var row=Row(id);
        if(command=="cancel"){row["cancelled"]=true;row.Remove("automatic_withdrawn");}
        else if(row["cancelled"]!.GetValue<bool>())throw new InvalidOperationException("已取消目标不能恢复");
        else if(command is "pause" or "continue")row["paused"]=command=="pause";
        else throw new ArgumentException("未知队列操作");
    });
    public void Begin(string id,string action,string resource)=>Mutate(()=>
    {if(Status(id)!="queued")throw new InvalidOperationException("任务尚不可执行");Row(id)["pending"]=new JsonObject{["action_id"]=action,["resource"]=resource};});
    public void Confirm(string id,string action,string receipt,long quantity,string evidence)
    {
        var value=new JsonObject{["action_id"]=action,["receipt_id"]=receipt,["quantity"]=quantity,["evidence"]=evidence};
        foreach(var row in Rows)foreach(var old in row!["receipts"]!.AsArray())
            if(old!["receipt_id"]!.GetValue<string>()==receipt)
            {if(row["job_id"]!.GetValue<string>()!=id||!JsonNode.DeepEquals(old,value))throw new InvalidOperationException("收据已绑定其他结果");return;}
        Mutate(()=>{var row=Row(id);if(row["pending"]?["action_id"]?.GetValue<string>()!=action)throw new InvalidOperationException("收据动作不匹配");
            row["receipts"]!.AsArray().Add(value);row["completed_quantity"]=checked(row["completed_quantity"]!.GetValue<long>()+quantity);row["pending"]=null;});
    }
    public void ReplanAutomatic(string id,long remaining,bool reuseEnded=false)=>Mutate(()=>
    {
        var row=Row(id);if(remaining<0||!id.StartsWith("auto-material:")||row["pending"] is not null||row["cancelled"]!.GetValue<bool>()&&!(reuseEnded&&row["automatic_withdrawn"]?.GetValue<bool>()==true))throw new InvalidOperationException("只能重排未占用的自动目标");
        row["cancelled"]=remaining==0;
        if(remaining==0)row["automatic_withdrawn"]=true;
        else{row.Remove("automatic_withdrawn");row["target_quantity"]=checked(row["completed_quantity"]!.GetValue<long>()+remaining);}
    });
    // A round group is fixed while packet stock is unchanged. Queue progress
    // must not append the original deficit again after every completed round.
    public void ReplanGatheringGroup(string id,long remaining,string stockKey,bool refreshPending,double? verifiedThrough=null)=>Mutate(()=>
    {
        var row=Row(id);if(row["kind"]!.GetValue<string>()!="gathering"||!id.StartsWith("auto-material:")||remaining<0)throw new InvalidOperationException("自动采集轮组无效");
        if(row["pending"] is not null)return;
        var oldKey=row["round_group_stock"]?.GetValue<string>();
        var ended=row["completed_quantity"]!.GetValue<long>()>=row["target_quantity"]!.GetValue<long>();
        var newerCoverage=verifiedThrough is{} through&&double.IsFinite(through)&&through>(row["round_group_through"]?.GetValue<double>()??double.MaxValue);
        if(refreshPending||oldKey==stockKey&&!(ended&&newerCoverage))return;
        row["round_group_stock"]=stockKey;row["round_group_through"]=verifiedThrough;row["cancelled"]=remaining==0;
        if(remaining==0)row["automatic_withdrawn"]=true;
        else{row.Remove("automatic_withdrawn");row["target_quantity"]=checked(row["completed_quantity"]!.GetValue<long>()+remaining);}
    });
}

