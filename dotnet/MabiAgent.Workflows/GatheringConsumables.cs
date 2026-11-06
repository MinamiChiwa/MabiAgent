using System.Text.Json.Nodes;
using MabiAgent.Core;
namespace MabiAgent.Workflows;

public sealed record GatheringConsumableRule(long ItemId,string VerifiedName,int PerAttempt=1,int RequiredAttempts=10);
public sealed record GatheringSupplyRequest(string RouteKey,string ActionId,long ItemId,string Name,long Quantity,string Scope,string Version,long TargetStock=10);

// The coordinator owns live reservations. Restored pending intents must be
// supplied at construction; nothing here spends money or alters captured stock.
public sealed class GatheringConsumables:IGatheringConsumables
{
    private const long MaxSupplyTarget=999;
    public static bool RequiresBottles(GatheringRoute route)=>route.Node is "水" or "水井" or "乳牛" or "牛奶";
    public static IReadOnlyDictionary<string,GatheringConsumableRule> RulesFor(RuntimeCatalog catalog)
    {
        if(!catalog.Gathering.Values.Any(RequiresBottles))return new Dictionary<string,GatheringConsumableRule>();
        var bottle=catalog.Names.Where(p=>p.Value=="空瓶").Select(p=>p.Key).ToArray();
        if(bottle.Length!=1)throw new InvalidDataException("空瓶目录身份缺失或重复");
        return catalog.Gathering.Values.Where(RequiresBottles).ToDictionary(r=>r.Key,r=>new GatheringConsumableRule(bottle[0],"空瓶"));
    }
    private readonly object gate=new();
    private readonly IReadOnlyDictionary<string,GatheringConsumableRule> rules;
    private readonly Func<InventorySnapshot> inventory;
    private readonly Func<MaintenancePreferences> preferences;
    private readonly Dictionary<string,JsonObject> reservations=[];
    private readonly Dictionary<string,string> rejectedVersions=[];
    private readonly Dictionary<string,long> awaitingInventory=[];
    public IReadOnlyList<long> AwaitingInventoryItemIds {get{lock(gate)return awaitingInventory.Values.Distinct().ToArray();}}
    private readonly Dictionary<string,GatheringSupplyRequest> requests=[];
    private readonly Dictionary<long,long> queuedDemand=[];
    // Remaining queue rounds include a pending batch. Its reservation is already
    // part of this demand, so it must not be added a second time.
    public void SetRemainingDemand(IEnumerable<(string RouteKey,long Rounds)> remaining)
    {
        lock(gate)
        {
            queuedDemand.Clear();
            foreach(var (route,rounds) in remaining)
                if(rounds>0&&rules.TryGetValue(route,out var rule))
                {
                    var current=queuedDemand.GetValueOrDefault(rule.ItemId);var perRound=checked((long)rule.PerAttempt*rule.RequiredAttempts);var room=MaxSupplyTarget-current;
                    if(room>0)queuedDemand[rule.ItemId]=current+(rounds>=(room+perRound-1)/perRound?room:checked(rounds*perRound));
                }
            var snapshot=inventory();
            foreach(var (action,request) in requests.ToArray())
            {
                var queued=queuedDemand.GetValueOrDefault(request.ItemId);
                if(queued<=0||snapshot.Scope!=request.Scope||!rules.TryGetValue(request.RouteKey,out var rule))
                {requests.Remove(action);awaitingInventory.Remove(action);continue;}
                var stock=snapshot.Query(request.ItemId);
                if(!stock.Confirmed||stock.Quantity is null)
                {awaitingInventory[action]=request.ItemId;continue;}
                awaitingInventory.Remove(action);
                var held=reservations.Values.Where(r=>r["scope"]!.GetValue<string>()==request.Scope&&r["item_id"]!.GetValue<long>()==request.ItemId).Sum(r=>r["quantity"]!.GetValue<long>());
                var target=Math.Min(MaxSupplyTarget,Math.Max(queued,checked(held+(long)rule.PerAttempt*rule.RequiredAttempts)));
                var deficit=Math.Max(0,target-stock.Quantity.Value);
                if(deficit==0)requests.Remove(action);
                else requests[action]=request with{Quantity=deficit,Version=stock.Version,TargetStock=target};
            }
        }
    }
    public GatheringConsumables(IReadOnlyDictionary<string,GatheringConsumableRule> rules,Func<InventorySnapshot> inventory,
        Func<MaintenancePreferences> preferences,IEnumerable<JsonObject>? restored=null)
    {
        if(rules.Any(p=>string.IsNullOrWhiteSpace(p.Key)||p.Value.ItemId<=0||string.IsNullOrWhiteSpace(p.Value.VerifiedName)||p.Value.PerAttempt<=0||p.Value.RequiredAttempts<=0||(long)p.Value.PerAttempt*p.Value.RequiredAttempts>MaxSupplyTarget))throw new ArgumentException("采集耗材配置无效");
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
            var stock=snapshot.Query(rule.ItemId);
            var existingRequest=requests.GetValueOrDefault(actionId);
            if(existingRequest is not null&&existingRequest.RouteKey==route.Key&&existingRequest.Scope==snapshot.Scope&&(!stock.Confirmed||stock.Quantity is null))
            {awaitingInventory[actionId]=rule.ItemId;return new(false,"空瓶库存待同步");}
            requests.Remove(actionId);awaitingInventory.Remove(actionId);
            if(rejectedVersions.GetValueOrDefault(actionId)==stock.Version||!stock.Confirmed||stock.Quantity is null)
            {awaitingInventory[actionId]=rule.ItemId;return new(false,"空瓶库存待同步");}
            rejectedVersions.Remove(actionId);
            var held=reservations.Values.Where(r=>r["scope"]!.GetValue<string>()==snapshot.Scope&&r["item_id"]!.GetValue<long>()==rule.ItemId).Sum(r=>r["quantity"]!.GetValue<long>());
            var batchQuantity=checked((long)rule.PerAttempt*rule.RequiredAttempts);
            var supply=new ConsumablePolicy(rule.VerifiedName,rule.PerAttempt,rule.RequiredAttempts).Plan(Math.Max(0,stock.Quantity.Value-held));
            var batchTarget=checked(held+batchQuantity);
            var target=Math.Min(MaxSupplyTarget,Math.Max(batchTarget,queuedDemand.GetValueOrDefault(rule.ItemId)));
            var deficit=Math.Max(0,target-stock.Quantity.Value);
            var stored=snapshot.Storage.Values.Sum(s=>Math.Max(0,s.GetValueOrDefault(rule.ItemId)??0));
            // Prefer one supply trip for all currently runnable rounds when the
            // warehouse or enabled purchase can complete that target. If only
            // the current batch is available, reserve it instead of starving it.
            var mustSupplyCurrent=stock.Quantity.Value<batchTarget;
            var canCompleteTarget=stock.Quantity.Value+stored>=target||preferences().AutoBuyBottles;
            if(deficit>0&&(mustSupplyCurrent||canCompleteTarget))
            {
                requests[actionId]=new(route.Key,actionId,rule.ItemId,rule.VerifiedName,deficit,snapshot.Scope,stock.Version,target);
                return new(false,preferences().AutoBuyBottles?"等待补充空瓶":"等待仓库补充空瓶（自动购买已关闭）");
            }
            if(reservations.Count>=10000)return new(false,"耗材待核账已达上限");
            var reservation=new JsonObject{["action_id"]=actionId,["route_key"]=route.Key,["item_id"]=rule.ItemId,["name"]=rule.VerifiedName,
                ["quantity"]=supply.ReservedQuantity,["scope"]=snapshot.Scope,["source"]=stock.Source,["version"]=stock.Version};
            reservations[actionId]=reservation;return new(true,Reservation:(JsonObject)reservation.DeepClone());
        }
    }
    public void RejectMissingBatch(string actionId)
    {
        lock(gate)
        {
            if(reservations.TryGetValue(actionId,out var held))rejectedVersions[actionId]=held["version"]!.GetValue<string>();
            reservations.Remove(actionId);requests.Remove(actionId);
        }
    }
    public void ReleaseUnsubmitted(string actionId){lock(gate){reservations.Remove(actionId);requests.Remove(actionId);awaitingInventory.Remove(actionId);rejectedVersions.Remove(actionId);}}
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
