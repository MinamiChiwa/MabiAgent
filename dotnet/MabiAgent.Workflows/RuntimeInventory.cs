using System.Text.Json.Nodes;
using System.Text.Json;
using MabiAgent.Core;
namespace MabiAgent.Workflows;

// A confirmed submission consumes materials immediately for planning. Only a
// newer observation of that material clears its projection, never an unrelated INV.
public sealed class RuntimeInventory(IWorkflowStore store)
{
    private readonly JsonObject state=LoadState(store);
    private static JsonObject LoadState(IWorkflowStore store)
    {
        var value=store.Read();
        // A new ledger has nothing to migrate or persist until its first write.
        if(value is null)return new JsonObject{["debits"]=new JsonObject(),["refreshes"]=new JsonObject(),["scheduler_version"]=2};
        if(value["scheduler_version"]?.GetValue<int>() is >=2)return value;
        // Legacy per-batch inventory barriers are scheduling policy, not
        // unknown consuming intents. Keep debit reservations and other fields.
        if(value["refreshes"] is JsonObject old&&value["seen"] is JsonObject seen)foreach(var key in old.Select(p=>p.Key).ToArray())seen.Remove(key);
        value["refreshes"]=new JsonObject();value["scheduler_version"]=2;store.Flush(value);return value;
    }
    private JsonObject Debits=>state["debits"]!.AsObject();
    private JsonObject Refreshes=>(state["refreshes"]??=new JsonObject()).AsObject();
    public void RequireFresh(string action,StockObservation basis,double? after=null)
    {
        if(after is{} at&&!double.IsFinite(at))throw new ArgumentOutOfRangeException(nameof(after));
        state["seen"]??=new JsonObject();var seen=state["seen"]!.AsObject();var key=action+":inventory";
        if(seen.ContainsKey(key))return;
        if(Refreshes.Count>=10000||seen.Count>=20000)throw new InvalidOperationException("待确认的库存操作过多，请先处理未完成的操作");
        var row=System.Text.Json.JsonSerializer.SerializeToNode(basis)!.AsObject();row["required_through"]=after;
        Refreshes[key]=row;seen[key]=true;store.Flush(state);
    }
    public long ReservedQuantity(long item,string scope)
    {
        long total=0;
        foreach(var entry in Debits)
        {var value=entry.Value;
            foreach(var row in value!.AsArray())
                if(row?["item"]?.GetValue<long>()==item&&row["scope"]?.GetValue<string>()==scope)
                    total=checked(total+row["quantity"]!.GetValue<long>());
        }
        return total;
    }
    public bool HasPendingRefresh(long item,string scope)=>Refreshes.Any(entry=>
        entry.Value?["ItemId"]?.GetValue<long>()==item&&entry.Value["Scope"]?.GetValue<string>()==scope);
    public void Submitted(string action,int submitted,RecipeDefinition recipe,InventorySnapshot basis)
    {
        state["seen"]??=new JsonObject();var seen=state["seen"]!.AsObject();
        var key=action+":"+submitted;if(Debits.ContainsKey(key)||seen.ContainsKey(key)){store.Flush(state);return;}
        if(Debits.Count>=10000||seen.Count>=20000)throw new InvalidOperationException("待确认的库存操作过多，请先处理未完成的操作");
        var rows=new JsonArray();
        foreach(var (item,quantity) in recipe.Inputs)
        {var observation=basis.Query(item);rows.Add(new JsonObject{["item"]=item,["quantity"]=quantity,["scope"]=basis.Scope,["version"]=observation.Version,["source"]=observation.Source});}
        Debits[key]=rows;seen[key]=true;store.Flush(state);
    }
    public InventorySnapshot Apply(InventorySnapshot raw)
    {
        var result=new InventorySnapshot(raw.Scope);var changes=false;var totals=new Dictionary<long,long>();
        foreach(var (key,value) in Debits.ToArray())
        {
            var rows=value!.AsArray();
            for(var i=rows.Count-1;i>=0;i--)
            {
                var r=rows[i]!;if(r["scope"]!.GetValue<string>()!=raw.Scope)continue;
                var item=r["item"]!.GetValue<long>();var observed=raw.Query(item);
                if(observed.Confirmed&&observed.Quantity is not null&&!string.IsNullOrEmpty(observed.Version)&&observed.Source==r["source"]!.GetValue<string>()&&observed.Version!=r["version"]!.GetValue<string>())
                {rows.RemoveAt(i);changes=true;continue;}
                totals[item]=checked(totals.GetValueOrDefault(item)+r["quantity"]!.GetValue<long>());
            }
            if(rows.Count==0){Debits.Remove(key);changes=true;}
        }
        var awaiting=new HashSet<long>();
        foreach(var (key,value) in Refreshes.ToArray())
        {
            var basis=value!.Deserialize<StockObservation>()!;if(basis.Scope!=raw.Scope)continue;var observed=raw.Query(basis.ItemId);
            var requiredThrough=value!["required_through"]?.GetFiniteDouble();
            var continuous=observed.Source=="packet_capture"&&observed.VerifiedThrough is{} through&&double.IsFinite(through)&&requiredThrough is{} required&&through>=required;
            var fresh=continuous||observed.Source!=basis.Source||observed.Version!=basis.Version;
            if(observed.Confirmed&&observed.Quantity is not null&&!string.IsNullOrEmpty(observed.Version)&&fresh)
            {Refreshes.Remove(key);changes=true;}else awaiting.Add(basis.ItemId);
        }
        foreach(var (item,o) in raw.Observations)
            result.Set(awaiting.Contains(item)?o with{Quantity=null,Confirmed=false,UnknownReason="采集批结束，等待新库存"}:o with{Quantity=o.Quantity is null?null:Math.Max(0,o.Quantity.Value-totals.GetValueOrDefault(item))});
        foreach(var item in awaiting.Where(item=>!raw.Observations.ContainsKey(item)))result.Set(raw.Query(item) with{UnknownReason="采集批结束，等待新库存"});
        foreach(var (scope,rows) in raw.Storage)result.SetStorage(scope,rows);
        if(changes)store.Flush(state);return result;
    }
    public void Compact(IReadOnlySet<string> activeActions)
    {
        if(state["seen"] is not JsonObject seen)return;var changed=false;
        foreach(var key in seen.Select(p=>p.Key).ToArray())
        {
            var split=key.LastIndexOf(':');var action=split<0?key:key[..split];
            if(!Debits.ContainsKey(key)&&!Refreshes.ContainsKey(key)&&!activeActions.Contains(action)){seen.Remove(key);changed=true;}
        }
        if(changed)store.Flush(state);
    }
    public void ReleaseUnsubmitted(string action)
    {
        var prefix=action+":";var seen=state["seen"] as JsonObject;var changed=false;
        foreach(var key in Debits.Select(p=>p.Key).Where(key=>key.StartsWith(prefix,StringComparison.Ordinal)).ToArray()){Debits.Remove(key);changed=true;}
        if(seen is not null)foreach(var key in seen.Select(p=>p.Key).Where(key=>key.StartsWith(prefix,StringComparison.Ordinal)).ToArray()){seen.Remove(key);changed=true;}
        if(changed)store.Flush(state);
    }
    public void ReleaseRejectedSubmission(string action,int ordinal)
    {
        if(ordinal<1)throw new ArgumentOutOfRangeException(nameof(ordinal));
        var key=action+":"+ordinal;var changed=Debits.Remove(key);
        if(state["seen"] is JsonObject seen)changed|=seen.Remove(key);
        if(changed)store.Flush(state);
    }
}

// Debit is durable before a craft intent can return to the port. A crash may
// conservatively hold stock; it cannot allocate the same inputs twice.
public sealed class SubmissionStore(IWorkflowStore inner,RuntimeInventory inventory,string action,RecipeDefinition recipe,Func<InventorySnapshot> snapshot):IWorkflowStore
{
    public JsonObject? Read()=>inner.Read();
    public void Flush(JsonObject state)
    {
        if(state["pending"]?["kind"]?.GetValue<string>()=="craft")
            inventory.Submitted(action,(state["submitted"]?.GetValue<int>()??0)+1,recipe,snapshot());
        inner.Flush(state);
        // Persist proof that this exact attempt was never sent before releasing its
        // projection. A restart retries this release without touching prior submissions.
        if(state["pending"] is null&&state["unsent_craft"]?.GetValue<int>() is{} ordinal&&ordinal==(state["submitted"]?.GetValue<int>()??0)+1)
            inventory.ReleaseRejectedSubmission(action,ordinal);
    }
}
