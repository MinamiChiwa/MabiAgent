using System.Text.Json.Nodes;
using System.Text.Json;
using MabiAgent.Core;
namespace MabiAgent.Workflows;

public sealed class RuntimeCatalog
{
    public JsonObject Configuration {get;}
    public IReadOnlyDictionary<string,RecipeDefinition> Recipes {get;}
    public IReadOnlyDictionary<string,ProductionRecipe> Production {get;}
    public IReadOnlyDictionary<string,GatheringRoute> Gathering {get;}
    public IReadOnlyDictionary<long,string> Names {get;}
    public RuntimeCatalog(JsonObject config)
    {
        Configuration=(JsonObject)config.DeepClone();
        Names=(config["item_names"]?.AsObject()??[]).ToDictionary(p=>long.Parse(p.Key),p=>p.Value!.GetValue<string>());
        var recipes=new Dictionary<string,RecipeDefinition>();var production=new Dictionary<string,ProductionRecipe>();
        foreach(var (key,node) in config["recipes"]?.AsObject()??[])
        {
            if(config["custom_chain_issues"]?[key] is not null)continue;
            var r=node!;var inputs=new Dictionary<long,long>();
            foreach(var m in r["materials"]?.AsArray()??[])
            {var id=Integer(m!["item_id"]!);inputs[id]=checked(inputs.GetValueOrDefault(id)+Integer(m["quantity"]!));}
            var device=r["device"]!.GetValue<string>();
            recipes[key]=new(key,Integer(r["output_item_id"]!),Integer(r["output_per_round"]!),inputs,device);
            production[key]=new(key,device,r["duration_s"]!.GetFiniteDouble());
        }
        Recipes=recipes;Production=production;
        Gathering=(config["gathering_routes"]?.AsObject()??[]).Where(p=>p.Value?["target_item_id"] is not null&&config["custom_chain_issues"]?[p.Key] is null).ToDictionary(p=>p.Key,p=>new GatheringRoute(p.Key,Integer(p.Value!["target_item_id"]!),p.Value["skill"]!.GetValue<string>(),p.Value["node"]!.GetValue<string>(),p.Value["attempts_per_batch"]?.GetValue<int>()??10,p.Value["reference_yield_per_batch"] is {} reference?Integer(reference):(long?)null));
        if(Gathering.Values.Any(r=>r.ReferenceYieldPerBatch is <=0))throw new InvalidDataException("采集每轮参考产量须为正数");
    }
    public MaterialPlanner Planner()=>new(Recipes.Values,Gathering.Values.GroupBy(r=>r.TargetItemId).ToDictionary(g=>g.Key,g=>g.First().Key),routes:Gathering.Values.ToDictionary(r=>r.Key,r=>r.TargetItemId));
    public static RuntimeCatalog Load(string root)=>new(new CatalogLoader(root).Load("config/screen_catalog.1280x960.tw.json"));
    // JsonValue created by configuration editors can hold Int32; parsed JSON
    // holds JsonElement. Deserialize preserves exact Int64 validation for both.
    private static long Integer(JsonNode node)=>node.Deserialize<long>();
}

public interface IAutomationPorts:IDisposable
{
    string Scope {get;}
    string? CurrentDevice {get;}
    string? OnsiteDevice=>CurrentDevice;
    string? InputPauseReason=>null;
    string? CurrentPage=>null;
    void Start();
    void Continue();
    void Pause(string reason);
    InventorySnapshot Inventory();
    bool PreferBulkInventorySync=>false;
    // Prefer the shared theater script; confirmed missing item uses existing same-character relogin. Never scan bag quantities.
    void RequestInventorySync(){}
    void CancelInventorySync(){}
    void ResumeAfterInventorySync()=>Continue();
    bool PrivateTheaterRefreshAvailable=>PreferBulkInventorySync;
    PrivateTheaterRefreshStatus PrivateTheaterRefreshStatus=>PrivateTheaterRefreshStatus.Idle;
    bool TickPrivateTheaterRefresh(DateTimeOffset now)=>false;
    void BeginPrivateTheaterRefresh(DateTimeOffset now){}
    IProductionPort Production(string recipeKey,string batchId,IWorkflowStore store);
    IGatheringPort Gathering(string routeKey,string batchId,IWorkflowStore store);
    IStorageSupplyPort Storage(WithdrawalPlan plan,string batchId,IWorkflowStore store);
    IFishingPort Fishing(string pointKey,string batchId,IWorkflowStore store);
    IMaintenancePort Maintenance(string batchId,IWorkflowStore store);
    IGatheringConsumables? GatheringConsumables=>null;
    bool IsExplicitSurplusMaterial(long item)=>false;
    ISurplusMaintenancePort? Surplus(string batchId,IWorkflowStore store,Func<long,long?> requiredReserve)=>null;
    IDecompositionPort? Decomposition(string batchId,IWorkflowStore store)=>null;
    IPurchasePort? Purchase(GatheringSupplyRequest request,string batchId,Func<PurchaseState?> persisted)=>null;
    MaintenancePreferences PurchasePreferences=>new();
    // One guarded auxiliary step. False means no input. Never called across an owned native batch or consuming intent.
    bool BeforeTick(bool safeBoundary)=>false;
    // Only the offer belonging to an already dispatched navigation may use
    // its consumable while the current workflow still owns the character.
    bool BeforeNavigationTick()=>false;
}
