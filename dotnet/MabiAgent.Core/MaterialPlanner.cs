using System.Text.Json;
namespace MabiAgent.Core;
public static class PlanningJson
{
    public static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower};
}
public sealed record RecipeDefinition(string RecipeKey,long OutputItemId,long OutputPerRound,Dictionary<long,long> Inputs,string Device);
public sealed record MaterialGoal(string GoalId,long ItemId,long Quantity,string Kind="production",string? RecipeKey=null,string? RouteKey=null);
public sealed record FutureSupply(string SupplyId,long ItemId,long Quantity,string Scope,string? GoalId=null);
public sealed record DemandShare(string GoalId,string? ConsumerId,long Quantity,string Kind);
public sealed record MaterialAllocation(long ItemId,long Quantity,string SourceKind,string? SourceId,string ConsumerId,string GoalId);
public sealed record MaterialIssue(string Kind,long ItemId,long? Quantity,long RequiredQuantity,string? GoalId,string? ConsumerId,string[] Path);
public sealed record PlannedGoal(string GoalId,string Kind,long ItemId,long Quantity,long FutureQuantity,long NewQuantity,string? TaskId);
public sealed record PlannedTask(string TaskId,string Kind,long ItemId,string? RecipeKey,string? RouteKey,string? Device,long RequiredQuantity,long OutputQuantity,long? OutputPerRound,long Rounds,long ReadyRounds,long UnreadyRounds,long WaitingRounds,long ReadyQuantity,Dictionary<long,long> Inputs,DemandShare[] Demands,string[] DependsOn);
public sealed record MaterialPlan(string Scope,PlannedGoal[] Goals,PlannedTask[] Tasks,MaterialAllocation[] Allocations,MaterialIssue[] Issues,int SchemaVersion=1);
public sealed record GatheringRoundEstimate(long ItemId,long MissingQuantity,long ReferenceYield,long Rounds,bool UnknownBaseline);

public sealed class MaterialPlanner
{
    private readonly Dictionary<string,RecipeDefinition> recipes;
    private readonly Dictionary<long,string> byItem=[];
    private readonly Dictionary<long,string> gathering;
    private readonly Dictionary<string,long> gatheringRoutes;
    public MaterialPlanner(IEnumerable<RecipeDefinition> definitions,IReadOnlyDictionary<long,string>? gatheringSources=null,
        IReadOnlyDictionary<long,string>? preferredRecipes=null,IReadOnlyDictionary<string,long>? routes=null)
    {
        recipes=definitions.ToDictionary(r=>r.RecipeKey);
        foreach(var r in recipes.Values)
        {
            if(string.IsNullOrWhiteSpace(r.RecipeKey)||string.IsNullOrWhiteSpace(r.Device)||r.OutputItemId<=0||r.OutputPerRound<=0||r.Inputs.Any(p=>p.Key<=0||p.Value<=0)) throw new ArgumentException("配方无效");
            byItem.TryAdd(r.OutputItemId,r.RecipeKey);
        }
        gathering=new(gatheringSources??new Dictionary<long,string>());
        gatheringRoutes=gathering.ToDictionary(p=>p.Value,p=>p.Key);
        foreach(var p in routes??new Dictionary<string,long>())gatheringRoutes[p.Key]=p.Value;
        foreach(var p in preferredRecipes??new Dictionary<long,string>())
        {
            if(!recipes.TryGetValue(p.Value,out var r)||r.OutputItemId!=p.Key)throw new ArgumentException("首选配方产物不匹配");
            byItem[p.Key]=p.Value;
        }
    }
    public MaterialPlan Plan(IEnumerable<MaterialGoal> goals,InventorySnapshot inventory,IEnumerable<FutureSupply>? future=null)
    {
        var list=goals.ToArray();var supplies=(future??[]).ToArray();
        if(list.Select(g=>g.GoalId).Distinct().Count()!=list.Length||list.Any(g=>g.ItemId<=0||g.Quantity<=0||string.IsNullOrWhiteSpace(g.GoalId)||g.Kind is not ("production" or "gathering")))throw new ArgumentException("目标无效或重复");
        if(supplies.Select(s=>s.SupplyId).Distinct().Count()!=supplies.Length||supplies.Any(s=>s.Quantity<=0||s.ItemId<=0))throw new ArgumentException("在产供给无效");
        return new Planning(this,inventory,supplies).Build(list);
    }

    // Planning-only estimate. It never writes inventory and deliberately keeps
    // the expected yield separate from observed packet quantities.
    public static GatheringRoundEstimate EstimateGatheringRounds(long itemId,long missingQuantity,long referenceYield,bool unknownBaseline=false)
    {
        if(itemId<=0||missingQuantity<0||referenceYield<=0)throw new ArgumentOutOfRangeException();
        var rounds=missingQuantity==0?0:checked((missingQuantity+referenceYield-1)/referenceYield);
        if(unknownBaseline&&rounds==0)rounds=1;
        return new(itemId,missingQuantity,referenceYield,rounds,unknownBaseline);
    }
    private sealed class TaskState(string id,long item,RecipeDefinition? recipe,string? route)
    {
        public string Id=id;public long Item=item;public RecipeDefinition? Recipe=recipe;public string? Route=route;
        public long Root,Material,Output,Rounds;public List<DemandShare> Demands=[];
    }
    private sealed class Lot(long quantity,string kind,string source)
    {public long Quantity=quantity;public string Kind=kind,Source=source;}
    private sealed class Planning
    {
        private readonly MaterialPlanner owner;private readonly InventorySnapshot inventory;
        private readonly Dictionary<long,long?> stock;private readonly Dictionary<string,Dictionary<long,long?>> storage;
        private readonly Dictionary<string,TaskState> tasks=[];private readonly Dictionary<long,List<Lot>> lots=[];
        private readonly List<MaterialAllocation> allocations=[];private readonly List<MaterialIssue> issues=[];private readonly List<FutureSupply> supplies=[];
        public Planning(MaterialPlanner owner,InventorySnapshot inventory,FutureSupply[] future)
        {
            this.owner=owner;this.inventory=inventory;stock=inventory.Observations.ToDictionary(p=>p.Key,p=>p.Value.Confirmed?p.Value.Quantity:null);
            storage=inventory.Storage.ToDictionary(p=>p.Key,p=>new Dictionary<long,long?>(p.Value));
            foreach(var s in future)
                if(s.Scope!=inventory.Scope)issues.Add(new("supply_scope_mismatch",s.ItemId,null,s.Quantity,s.GoalId,null,[]));
                else {supplies.Add(s);AddLot(s.ItemId,s.Quantity,"future",s.SupplyId);}
        }
        private void AddLot(long item,long quantity,string kind,string source)
        {if(!lots.TryGetValue(item,out var list))lots[item]=list=[];list.Add(new(quantity,kind,source));}
        private TaskState GetTask(long item,RecipeDefinition? recipe,string? route)
        {
            var id=recipe is not null ? "production:"+recipe.RecipeKey : $"gathering:{item}:{route}";
            if(!tasks.TryGetValue(id,out var t))tasks[id]=t=new(id,item,recipe,route);return t;
        }
        private long TakeLots(long item,long quantity,string consumer,string goal)
        {
            foreach(var lot in lots.GetValueOrDefault(item)??[])
            {
                var take=Math.Min(quantity,lot.Quantity);if(take==0)continue;lot.Quantity-=take;quantity-=take;
                allocations.Add(new(item,take,lot.Kind,lot.Source,consumer,goal));
                if(lot.Kind is "production" or "gathering")
                {var source=tasks[lot.Source];source.Material=checked(source.Material+take);source.Demands.Add(new(goal,consumer,take,"material"));}
                if(quantity==0)break;
            }return quantity;
        }
        private void Unavailable(string kind,long item,long quantity,string consumer,string goal,string[] path)
        {issues.Add(new(kind,item,kind=="inventory_unknown"?null:quantity,quantity,goal,consumer,path));allocations.Add(new(item,quantity,kind,null,consumer,goal));}
        private void Need(long item,long quantity,string consumer,string goal,string[] path)
        {
            var available=stock.GetValueOrDefault(item);
            if(available is not null)
            {
                var take=Math.Min(quantity,available.Value);stock[item]-=take;quantity-=take;
                if(take>0)allocations.Add(new(item,take,"inventory",inventory.Scope,consumer,goal));
            }
            quantity=TakeLots(item,quantity,consumer,goal);if(quantity==0)return;
            if(available is null){Unavailable("inventory_unknown",item,quantity,consumer,goal,path);return;}
            foreach(var (scope,rows) in storage)
            {
                var take=Math.Min(quantity,rows.GetValueOrDefault(item)??0);
                if(take>0){rows[item]-=take;quantity-=take;allocations.Add(new(item,take,"storage",scope,consumer,goal));issues.Add(new("storage_withdrawal_required",item,take,take,goal,consumer,path));}
                if(quantity==0)return;
            }
            var key=owner.byItem.GetValueOrDefault(item);
            if(key is not null && path.Contains(key)){Unavailable("recipe_cycle",item,quantity,consumer,goal,[..path,key]);return;}
            var recipe=key is null?null:owner.recipes.GetValueOrDefault(key);var route=owner.gathering.GetValueOrDefault(item);
            if(recipe is null&&route is null){Unavailable("no_source",item,quantity,consumer,goal,path);return;}
            var task=GetTask(item,recipe,recipe is null?route:null);
            Grow(task,Math.Max(task.Root,checked(task.Material+quantity)),goal,path);
            if(TakeLots(item,quantity,consumer,goal)!=0)throw new InvalidOperationException("生成供给未覆盖需求");
        }
        private void Grow(TaskState task,long required,string goal,string[] path)
        {
            long output;
            if(task.Recipe is not { } recipe)output=required;
            else
            {
                var rounds=checked(required+recipe.OutputPerRound-1)/recipe.OutputPerRound;var extra=rounds-task.Rounds;
                output=checked(rounds*recipe.OutputPerRound);task.Rounds=rounds;
                foreach(var (item,perRound) in recipe.Inputs)if(extra>0)Need(item,checked(extra*perRound),task.Id,goal,[..path,recipe.RecipeKey]);
            }
            var added=output-task.Output;task.Output=output;if(added>0)AddLot(task.Item,added,task.Recipe is null?"gathering":"production",task.Id);
        }
        public MaterialPlan Build(MaterialGoal[] goals)
        {
            List<PlannedGoal> result=[];
            foreach(var goal in goals)
            {
                var matching=supplies.Where(s=>s.GoalId==goal.GoalId).ToArray();
                if(matching.Any(s=>s.ItemId!=goal.ItemId))throw new ArgumentException("在产供给产物不匹配");
                var credit=Math.Min(goal.Quantity,matching.Sum(s=>s.Quantity));var quantity=goal.Quantity-credit;TaskState? task=null;
                if(quantity>0)
                {
                    var key=goal.RecipeKey??owner.byItem.GetValueOrDefault(goal.ItemId);
                    var recipe=goal.Kind=="production"&&key is not null?owner.recipes.GetValueOrDefault(key):null;
                    var route=goal.RouteKey??owner.gathering.GetValueOrDefault(goal.ItemId);
                    if(recipe is not null&&recipe.OutputItemId!=goal.ItemId)throw new ArgumentException("配方产物不匹配");
                    if(goal.Kind=="production"&&recipe is null||goal.Kind=="gathering"&&route is null)issues.Add(new("no_source",goal.ItemId,quantity,quantity,goal.GoalId,null,[]));
                    else
                    {
                        if(goal.Kind=="gathering"&&owner.gatheringRoutes.GetValueOrDefault(route!)!=goal.ItemId)throw new ArgumentException("采集路线与产物不匹配");
                        task=GetTask(goal.ItemId,recipe,recipe is null?route:null);task.Root=checked(task.Root+quantity);task.Demands.Add(new(goal.GoalId,null,quantity,"root"));
                        Grow(task,Math.Max(task.Root,task.Material),goal.GoalId,[]);
                    }
                }result.Add(new(goal.GoalId,goal.Kind,goal.ItemId,goal.Quantity,credit,quantity,task?.Id));
            }
            return new(inventory.Scope,result.ToArray(),tasks.Values.Select(Finish).ToArray(),allocations.ToArray(),issues.ToArray());
        }
        private PlannedTask Finish(TaskState task)
        {
            var rows=allocations.Where(a=>a.ConsumerId==task.Id).ToArray();
            var dependencies=rows.Where(a=>a.SourceKind is "future" or "production" or "gathering" && (a.SourceKind=="future"||a.SourceId!=task.Id))
                .Select(a=>a.SourceKind=="future"?"future:"+a.SourceId:a.SourceId!).Distinct().ToArray();
            var recipe=task.Recipe;var ready=task.Rounds;var possible=task.Rounds;
            if(recipe is not null)foreach(var (item,perRound) in recipe.Inputs)
            {
                var hand=rows.Where(a=>a.ItemId==item&&a.SourceKind=="inventory").Sum(a=>a.Quantity);
                var unknown=rows.Where(a=>a.ItemId==item&&a.SourceKind=="inventory_unknown").Sum(a=>a.Quantity);
                ready=Math.Min(ready,hand/perRound);possible=Math.Min(possible,checked(hand+unknown)/perRound);
            }
            return new(task.Id,recipe is null?"gathering":"production",task.Item,recipe?.RecipeKey,task.Route,recipe?.Device,
                Math.Max(task.Root,task.Material),task.Output,recipe?.OutputPerRound,task.Rounds,ready,possible-ready,task.Rounds-possible,
                recipe is null?task.Output:checked(ready*recipe.OutputPerRound),recipe is null?[]:new(recipe.Inputs),task.Demands.ToArray(),dependencies);
        }
    }
}
