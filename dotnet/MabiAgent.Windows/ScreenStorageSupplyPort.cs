using MabiAgent.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiAgent.Workflows;
namespace MabiAgent.Windows;

public sealed class ScreenStorageSupplyPort:ScreenWorkflowPort,IStorageSupplyPort
{
    private readonly WithdrawalPlan plan;private readonly JsonNode config,container;private readonly string scopeType;private string? rowTarget;private bool owner,item,dialog;private long? dialogQuantity,capacityQuantity;
    private int menuOpenAttempts;private double lastMenuOpenAt=-1;
    private double lastDialogueAt=-1;private int dialogueSteps;
    private readonly Func<InventorySnapshot>? packetInventory;
    private long AuthorizedQuantity=>Store.Read()?["capacity_limited_quantity"]?.GetValue<long>() is{} q&&q>0&&q<plan.Quantity?q:plan.Quantity;
    private long? CapacityQuantity()
    {
        if(!dialog||dialogQuantity is not >0||Session.LastCapture is not{} capture)return null;
        var spec=config["transfer"]?["capacity"];if(spec is null)return null;
        var bag=Session.QuantityReader.ColorText(capture.Image,Session.Box(spec["bag_region"]!),.75);
        var unit=Session.QuantityReader.ColorText(capture.Image,Session.Box(spec["unit_region"]!),.75);
        return LimitedQuantity(dialogQuantity.Value,bag,unit);
    }
    internal static long? LimitedQuantity(long selected,string? bag,string? unit)
    {
        var weight=bag is null?null:QuantityParser.Parse(bag,"fixed_ratio");
        if(weight?.Capacity is not{} maximum||weight.Quantity>=maximum||unit is null||
            !decimal.TryParse(unit,System.Globalization.NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var each)||each<=0)return null;
        var limit=decimal.Floor((maximum-weight.Quantity)/(each*100m));
        return selected>0&&limit==selected?selected:null;
    }
    private ScreenMaintenancePort? exit,navigator;
    internal NavigationTestTrace? NavigationTrace=>navigator?.LastTestNavigationTrace;
    private int SearchPassLength=>Math.Clamp(config["search"]?["limit"]?.GetValue<int>()??10,1,33);
    public bool CanLeaveAfterTransfer
    {get{var f=Observe();return f.Stable&&f.Foreground&&(f.Page==S(container,"page")||f.Page=="scene");}}
    public bool ReleaseAdvanced{get;private set;}
    public bool ReleaseWaiting=>exit?.ReleaseWaiting==true;
    public bool ReleaseNavigation()
    {exit??=Session.CreateMaintenancePort(BatchId+":exit",Store);exit.Observe();var released=exit.ReleaseStorageNavigation();ReleaseAdvanced=!released&&exit.LastReleaseDispatched;if(released)navigator?.ResetNavigation();return released;}
    public ScreenStorageSupplyPort(ScreenWorkflowSession session,WithdrawalPlan plan,string batchId,IWorkflowStore store,Func<InventorySnapshot>? packetInventory=null):base(session,batchId,store)
    {this.plan=plan;this.packetInventory=packetInventory;config=session.Routes.Profile.Definition["storage_supply"]??throw new ArgumentException("仓库缺少校准");scopeType=plan.Scope.Split(':')[0] switch{"character"=>"character_storage","account"=>"account_storage",var value=>value};container=config["containers"]?[scopeType]??throw new ArgumentException("仓库范围未配置");}
    public static bool ScopeMatches(string scope,SessionScope current)
    {
        if(scope is "account_storage" or "character_storage")return true;
        if(scope.StartsWith("account:",StringComparison.Ordinal))return current.AccountId is {} account&&scope=="account:"+JsonSerializer.Serialize(account);
        if(scope.StartsWith("character:",StringComparison.Ordinal))return current.AccountId is {} a&&current.CharacterId is {} c&&scope=="character:"+JsonSerializer.Serialize(new[]{a,c});
        return false;
    }
    private bool Name(ClientCapture c,JsonNode? region,JsonNode? searchRegion=null)
        =>region is not null&&Session.QuantityReader.ExactColor(c.Image,Session.Box(region),plan.Name,minimum:.75)
            ||searchRegion is not null&&Session.QuantityReader.FindExactColorLine(c.Image,Session.Box(searchRegion),plan.Name,minimum:.75) is not null;
    protected override PageView Decorate(ClientCapture c,PageView view)
    {
        owner=false;item=false;dialog=false;rowTarget=null;dialogQuantity=null;capacityQuantity=null;if(view.Modal)return view;
        var baseScope=scopeType;owner=view.Page==S(container,"page");
        // Qualified owners require a verified account/character identity; process scope cannot invent it.
        var verifiedOwner=ScopeMatches(plan.Scope,Session.VerifiedOwner??Session.Scope);owner&=verifiedOwner;
        var transfer=config["transfer"]!;var selection=config["selection"]!;
        if(Names(transfer["pages"]).Contains(view.Page)||Names(selection["pages"]).Contains(view.Page))
        {
            var spec=Names(transfer["pages"]).Contains(view.Page)?transfer:selection;
            var anchor=S(spec["scope_anchors"],baseScope)??S(transfer["scope_anchors"],baseScope);owner=verifiedOwner&&Session.Hit(c,anchor);item=owner&&Name(c,spec["name_region"],spec["name_search_region"]);
            dialog=ReferenceEquals(spec,transfer)&&item;
            if(dialog){var q=transfer["quantity"]!;dialogQuantity=Session.QuantityReader.Read(c.Image,new(plan.ItemId,"storage_transfer_dialog",Session.Box(q["region"]!),S(q,"format")??"integer",Thresholds:q["thresholds"]?.AsArray().Select(v=>v!.GetValue<int>()).ToArray())).Value?.Quantity;}
            if(dialogQuantity is >0&&dialogQuantity<plan.Quantity)capacityQuantity=CapacityQuantity();
            if(!item){view=Session.RemoveTarget(view,S(selection,"prepare_target")!);view=Session.RemoveTarget(view,S(transfer,"quantity_target")!);}
            if(!dialog||dialogQuantity!=AuthorizedQuantity)view=Session.RemoveTarget(view,S(transfer,"confirm_target")!);
        }
        if(owner&&view.Page==S(container,"page")&&config["rows"] is JsonArray rows)
        {
            foreach(var row in rows)if(Name(c,row!["name_region"]))
            {if(rowTarget is null){rowTarget=S(row,"select_target");view=Session.AddSurface(view,rowTarget!,Session.Box(row["select_region"]!));item=true;}}
        }
        return view;
    }
    public WithdrawalFrame Observe()=>Frame(Read());
    private WithdrawalFrame Frame(UiObservation o)
    {
        var at=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d;var id=Session.LastCapture?.CapturedAt.ToString("R",CultureInfo.InvariantCulture)??"";
        var visible=o.Stable&&Session.InputAvailable&&owner;var search=config["search"];
        var verifiedOwner=ScopeMatches(plan.Scope,Session.VerifiedOwner??Session.Scope);
        var reconciling=Store.Read()?["pending"]?["kind"]?.GetValue<string>() is "confirm_transfer" or "prepare_transfer"&&verifiedOwner;
        var inventory=owner||reconciling?packetInventory?.Invoke():null;var bag=inventory?.Query(plan.ItemId);long? baselineBag=bag is {Confirmed:true,Source:"packet_capture"}?bag.Quantity:null;long? baselineStorage=inventory is not null&&inventory.Storage.TryGetValue(plan.Scope,out var storage)&&storage.TryGetValue(plan.ItemId,out var amount)?amount:null;var bagScope=inventory?.Scope;
        var packetIdentity=reconciling&&o.Stable&&baselineBag.HasValue&&baselineStorage.HasValue;
        var scoped=visible||packetIdentity;var identified=visible&&item||packetIdentity;
        return new(at,id,o.View.Page,Session.InputAvailable,o.Stable,scoped?plan.Scope:null,identified?plan.ItemId:null,identified?plan.Name:null,scoped?baselineStorage:null,scoped?baselineBag:null,scoped?bagScope:null,
            visible&&o.View.Targets.ContainsKey(S(config["selection"],"prepare_target")!),dialog,dialogQuantity,visible&&o.View.Targets.ContainsKey(S(config["transfer"],"confirm_target")!),false,visible&&rowTarget is not null,visible&&search is not null&&o.View.Targets.ContainsKey(S(search,"target")!),SearchPassLength*3,Math.Max(1.2,search?["interval_s"]?.GetFiniteDouble()??.5),scoped?plan.Scope:null,scoped?bagScope:null,scoped&&inventory is not null?"packet_capture":null,scoped?bag?.VerifiedThrough:null,visible?capacityQuantity:null);
    }
    public void Perform(WithdrawalAction action)
    {
        if(action.ItemId!=plan.ItemId||action.Name!=plan.Name||action.Quantity!=AuthorizedQuantity||action.Scope!=plan.Scope)throw new InputBlockedException("取料计划不匹配");
        // These checks precede any native action; consumption itself is wrapped
        // separately by the shared guard, which retains unknown native results.
        var consuming=action.Kind is "confirm_transfer" or "prepare_transfer";
        if(consuming)
        {
            var frame=Frame(Session.LastObservation??throw new InputRejectedBeforeDispatchException("缺少仓库等待观察"));
            if(!frame.Stable||!frame.Foreground||!owner||!item)
                throw new InputRejectedBeforeDispatchException("仓库页面、范围或物品已变化");
            var transfer=config["transfer"]!;
            if(action.Kind=="confirm_transfer")Consume(S(transfer,"confirm_target")!,"confirm_transfer",_=>owner&&item&&dialog&&dialogQuantity==action.Quantity);
            else Consume(S(config["selection"],"prepare_target")!,"prepare_transfer",_=>owner&&item&&!dialog);
            return;
        }
        PerformNavigation(action);
    }
    private void PerformNavigation(WithdrawalAction action)
    {
        var frame=Frame(Session.LastObservation??throw new InputRejectedBeforeDispatchException("缺少仓库等待观察"));if(!frame.Stable||!frame.Foreground)throw new InputBlockedException("等待仓库稳定画面");
        if(action.Kind=="navigate")
        {
            if(frame.Page==S(container,"page")){navigator?.ResetNavigation();return;}
            if(frame.Page.StartsWith("point_detail_",StringComparison.Ordinal)||frame.Page.EndsWith("_nodes",StringComparison.Ordinal)||frame.Page is "character_menu" or "character_life_skills" or "skills" or "category_page" or "recipe_detail" or "bag" or "repair_tools")
            {exit??=Session.CreateMaintenancePort(BatchId+":exit",Store);exit.Observe();exit.ReleaseNavigation();return;}
            if(frame.Page=="npc_dialogue")
            {
                // Dialogue text is mutable content; advance on a bounded cadence
                // and rely on the stable page/target shell instead.
                if(frame.At-lastDialogueAt<1.5)return;
                if(++dialogueSteps>32)throw new InputBlockedException("仓库对话超过步骤上限");
                lastDialogueAt=frame.At;var dialogueTarget=Session.LastObservation!.View.Targets.ContainsKey("dialog.skip")?"dialog.skip":"dialog.advance";
                Session.TraceNavigation(BatchId,$"target={dialogueTarget}; dialogue_step={dialogueSteps}","storage_dialogue_dispatched");Session.NavigateTarget(dialogueTarget);return;
            }
            if(config["containers"]!.AsObject().Any(c=>S(c.Value,"page")==frame.Page)){Session.NavigateTarget(S(container,"switch_target")!);return;}
            if(frame.Page=="storage_dialog")
            {
                // The inn keeper may return to this menu after an intervening
                // dialogue. Opening storage is reversible, so retry only after
                // a fresh observation and a short settle interval.
                if(frame.At-lastMenuOpenAt<1.5)return;
                if(menuOpenAttempts>=3)throw new InputBlockedException("仓库入口菜单三次未打开保管箱");
                Session.NavigateTarget("storage.open");lastMenuOpenAt=frame.At;menuOpenAttempts++;
                Session.TraceNavigation(BatchId,$"target=storage.open; menu_attempt={menuOpenAttempts}","storage_menu_dispatched");return;
            }
            navigator??=Session.CreateMaintenancePort(BatchId+":storage-navigation",Store);
            var reason=navigator.NavigateObserved("storage",Session.LastObservation!);
            if(reason is not null)throw new InputBlockedException(reason);
            return;
        }
        if(!owner)throw new InputBlockedException("仓库范围未确认");
        if(action.Kind=="locate")
        {var search=config["search"]!;Session.ScrollListSearch(Session.LastObservation!,action.SearchIndex,S(search,"target")!,SearchPassLength);return;}
        if(!item)throw new InputBlockedException("物品名称未确认");
        var transfer=config["transfer"]!;
        if(action.Kind=="set_quantity"){if(!dialog)throw new InputBlockedException("取料数量框未确认");Session.Guard.EditQuantity(Session.Guard.ObserveCurrentTarget(S(transfer,"quantity_target")!,Session.LastObservation),action.Quantity.ToString(CultureInfo.InvariantCulture));return;}
        var target=action.Kind switch{"select_item"=>rowTarget,_=>null};
        if(target is null)throw new InputBlockedException("取料操作未配置");Session.NavigateTarget(target);
    }
}
