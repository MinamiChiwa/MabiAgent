using System.Text.Json.Nodes;
using MabiAgent.Core;
namespace MabiAgent.Workflows;

public sealed record GatheringConsumableRule(long ItemId,string VerifiedName,int PerAttempt=1,int RequiredAttempts=10);
public sealed record GatheringSupplyRequest(string RouteKey,string ActionId,long ItemId,string Name,long Quantity,string Scope,string Version,long TargetStock=10);

// The coordinator owns live reservations. Restored pending intents must be
// supplied at construction; nothing here spends money or alters captured stock.
public sealed class GatheringConsumables:IGatheringConsumables
{
    private readonly object gate=new();
    private readonly IReadOnlyDictionary<string,GatheringConsumableRule> rules;
    private readonly Func<InventorySnapshot> inventory;
    private readonly Func<MaintenancePreferences> preferences;
    private readonly Dictionary<string,JsonObject> reservations=[];
    private readonly Dictionary<string,GatheringSupplyRequest> requests=[];
    public GatheringConsumables(IReadOnlyDictionary<string,GatheringConsumableRule> rules,Func<InventorySnapshot> inventory,
        Func<MaintenancePreferences> preferences,IEnumerable<JsonObject>? restored=null)
    {
        if(rules.Any(p=>string.IsNullOrWhiteSpace(p.Key)||p.Value.ItemId<=0||string.IsNullOrWhiteSpace(p.Value.VerifiedName)||p.Value.PerAttempt<=0||p.Value.RequiredAttempts<=0))throw new ArgumentException("采集耗材配置无效");
        this.rules=new Dictionary<string,GatheringConsumableRule>(rules);this.inventory=inventory;this.preferences=preferences;
        foreach(var entry in restored??[])RestoreReservation(entry);
    }
    public IReadOnlyList<JsonObject> Reservations{get{lock(gate)return reservations.Values.Select(r=>(JsonObject)r.DeepClone()).ToArray();}}
    public void RestoreReservation(JsonObject entry)
    {
        lock(gate)
        {
            var action=entry["action_id"]?.GetValue<string>()??throw new InvalidDataException("耗材预留缺少动作");
            var route=entry["route_key"]?.GetValue<string>()??"";
            if(!rules.TryGetValue(route,out var rule)||entry["item_id"]?.GetValue<long>()!=rule.ItemId||entry["quantity"]?.GetValue<long>()!=checked((long)rule.PerAttempt*rule.RequiredAttempts)
                ||string.IsNullOrWhiteSpace(entry["scope"]?.GetValue<string>()))throw new InvalidDataException("耗材预留记录不匹配");
            if(reservations.TryGetValue(action,out var existing)){if(!JsonNode.DeepEquals(existing,entry))throw new InvalidDataException("耗材预留动作冲突");return;}
            if(reservations.Count>=10000)throw new InvalidDataException("耗材待核账已达上限");
            reservations.Add(action,(JsonObject)entry.DeepClone());
        }
    }
    public IReadOnlyList<GatheringSupplyRequest> PendingRequests{get{lock(gate)return requests.Values.ToArray();}}
    public GatheringSupplyDecision ReserveForBatch(GatheringRoute route,string actionId)
    {
        lock(gate)
        {
            if(string.IsNullOrWhiteSpace(actionId))throw new ArgumentException("缺少采集动作标识");
            if(!rules.TryGetValue(route.Key,out var rule))return new(true);
            if(route.AttemptsPerBatch!=rule.RequiredAttempts)return new(false,"采集次数与耗材配置不一致");
            var snapshot=inventory();
            if(reservations.TryGetValue(actionId,out var existing))
            {
                if(existing["route_key"]!.GetValue<string>()!=route.Key||existing["scope"]!.GetValue<string>()!=snapshot.Scope)return new(false,"耗材预留属于其他路线或角色");
                return new(true,Reservation:(JsonObject)existing.DeepClone());
            }
            requests.Remove(actionId);
            var stock=snapshot.Query(rule.ItemId);
            if(!stock.Confirmed||stock.Quantity is null)return new(false,"空瓶库存待同步");
            var held=reservations.Values.Where(r=>r["scope"]!.GetValue<string>()==snapshot.Scope&&r["item_id"]!.GetValue<long>()==rule.ItemId).Sum(r=>r["quantity"]!.GetValue<long>());
            var supply=new ConsumablePolicy(rule.VerifiedName,rule.PerAttempt,rule.RequiredAttempts).Plan(Math.Max(0,stock.Quantity.Value-held));
            if(supply.Status!=SupplyStatus.Ready)
            {
                if(!preferences().AutoBuyBottles)return new(false,"自动购买空瓶已关闭");
                requests[actionId]=new(route.Key,actionId,rule.ItemId,rule.VerifiedName,supply.PurchaseQuantity,snapshot.Scope,stock.Version);
                return new(false,"等待补充空瓶");
            }
            if(reservations.Count>=10000)return new(false,"耗材待核账已达上限");
            var reservation=new JsonObject{["action_id"]=actionId,["route_key"]=route.Key,["item_id"]=rule.ItemId,["name"]=rule.VerifiedName,
                ["quantity"]=supply.ReservedQuantity,["scope"]=snapshot.Scope,["source"]=stock.Source,["version"]=stock.Version};
            reservations[actionId]=reservation;return new(true,Reservation:(JsonObject)reservation.DeepClone());
        }
    }
    public void ReleaseUnsubmitted(string actionId){lock(gate){reservations.Remove(actionId);requests.Remove(actionId);}}
    // Release a consumed reservation only after a newer same-owner balance.
    public bool ReconcileEndedBatch(string actionId)
    {
        lock(gate)
        {
            if(!reservations.TryGetValue(actionId,out var held))return true;
            var snapshot=inventory();var stock=snapshot.Query(held["item_id"]!.GetValue<long>());
            if(snapshot.Scope!=held["scope"]!.GetValue<string>()||!stock.Confirmed||stock.Quantity is null
                ||stock.Source!=held["source"]?.GetValue<string>()||string.IsNullOrWhiteSpace(stock.Version)||stock.Version==held["version"]!.GetValue<string>())return false;
            reservations.Remove(actionId);requests.Remove(actionId);return true;
        }
    }
}
