using System.Text.Json.Nodes;
namespace MabiAgent.Core;
public sealed partial class QueueDocument
{
    private JsonObject Pending(string id,string action)
    {
        var p=Row(id)["pending"] as JsonObject;
        if(p?["action_id"]?.GetValue<string>()!=action)throw new InvalidOperationException("待核账动作已变化");
        return p;
    }
    public void ReleaseUnsubmitted(string id,string action)=>Mutate(()=>{Pending(id,action);Row(id)["pending"]=null;});
    public void ReleaseFinishedGathering(string id,string action)=>Mutate(()=>
    {
        if(Row(id)["kind"]!.GetValue<string>()!="gathering")throw new InvalidOperationException("目标不是采集");
        Pending(id,action)["resource_released"]=true;
    });
    public void RetireEndedAutomaticGathering()=>Mutate(()=>
    {
        foreach(var row in Rows.Select(n=>n!.AsObject()))
            if(row["kind"]!.GetValue<string>()=="gathering"&&row["job_id"]!.GetValue<string>().StartsWith("auto-material:")&&row["pending"]?["resource_released"]?.GetValue<bool>()==true)
            {row["pending"]=null;row["cancelled"]=true;row["automatic_withdrawn"]=true;}
    });
    public void ResolveReview(string id,string action,DateTimeOffset now)=>Mutate(()=>
    {
        var row=Row(id);if(row["kind"]!.GetValue<string>()!="production")throw new InvalidOperationException("目标不是生产");
        var pending=Pending(id,action).DeepClone();
        var reviews=row["reviewed_actions"] as JsonArray;
        if(reviews is null){reviews=new();row["reviewed_actions"]=reviews;}
        reviews.Add(new JsonObject{["pending"]=pending,["outcome"]="user_closed_without_credit",["at"]=now.ToUnixTimeMilliseconds()/1000d});
        row["pending"]=null;row["cancelled"]=true;row["paused"]=true;row.Remove("automatic_withdrawn");
    });
    // Explicit operator adjudication, never called by automatic observation or retry.
    public void ResolveFishingMisroute(string id,string action,string evidence,DateTimeOffset now)=>Mutate(()=>
    {
        var row=Row(id);if(row["kind"]!.GetValue<string>()!="idle_fishing")throw new InvalidOperationException("目标不是钓鱼");
        if(string.IsNullOrWhiteSpace(evidence))throw new ArgumentException("需要人工核实依据",nameof(evidence));
        var pending=Pending(id,action).DeepClone();
        var reviews=row["reviewed_actions"] as JsonArray;
        if(reviews is null){reviews=new();row["reviewed_actions"]=reviews;}
        reviews.Add(new JsonObject{["pending"]=pending,["outcome"]="user_confirmed_navigation_misclick",["evidence"]=evidence,["at"]=now.ToUnixTimeMilliseconds()/1000d});
        row["pending"]=null;
    });
    public void ResolveFishingNoEffect(string id,string action,string evidence,DateTimeOffset now,string reviewer="user")=>Mutate(()=>
    {
        var row=Row(id);if(row["kind"]!.GetValue<string>()!="idle_fishing")throw new InvalidOperationException("目标不是钓鱼");
        if(string.IsNullOrWhiteSpace(evidence))throw new ArgumentException("需要人工核实依据",nameof(evidence));
        var pending=Pending(id,action).DeepClone();var reviews=row["reviewed_actions"] as JsonArray;
        if(reviews is null){reviews=new();row["reviewed_actions"]=reviews;}
        if(string.IsNullOrWhiteSpace(reviewer))throw new ArgumentException("需要明确核实者",nameof(reviewer));
        reviews.Add(new JsonObject{["pending"]=pending,["outcome"]=reviewer=="user"?"user_confirmed_no_input_effect":"operator_confirmed_no_input_effect",["reviewer"]=reviewer,["evidence"]=evidence,["at"]=now.ToUnixTimeMilliseconds()/1000d});
        row["pending"]=null;
    });
    public int PruneCancelled()
    {
        var count=0;Mutate(()=>
        {
            // A peer production journal may still need a collect-all receipt after a crash.
            var productionPending=Jobs.Any(j=>j.Kind=="production"&&j.Pending);
            var removable=Jobs.Where(j=>j.Cancelled&&!j.Pending
                &&!(productionPending&&Row(j.Id)["receipts"] is JsonArray{Count:>0})).Select(j=>j.Id).ToHashSet();
            bool changed;
            do
            {
                var required=Rows.Where(n=>!removable.Contains(n!["job_id"]!.GetValue<string>()))
                    .SelectMany(n=>n!["depends_on"]!.AsArray()).Select(n=>n!.GetValue<string>());
                var before=removable.Count;removable.ExceptWith(required.ToArray());changed=before!=removable.Count;
            }while(changed);
            for(var i=Rows.Count-1;i>=0;i--)if(removable.Contains(Rows[i]!["job_id"]!.GetValue<string>())){Rows.RemoveAt(i);count++;}
        });return count;
    }
}
