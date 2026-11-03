using MabiAgent.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiAgent.Workflows;
namespace MabiAgent.Windows;

public sealed class ScreenStorageSupplyPort:ScreenWorkflowPort,IStorageSupplyPort
{
    private readonly WithdrawalPlan plan;private readonly JsonNode config,container;private readonly string scopeType;private string? rowTarget;private bool owner,item,dialog;private long? dialogQuantity;
    private bool shortcutSent;private double shortcutAt;private int menuOpenAttempts;private double lastMenuOpenAt=-1;
    private double lastDialogueAt=-1;private int dialogueSteps;
    private readonly Func<InventorySnapshot>? packetInventory;
    private ScreenMaintenancePort? exit;
    public bool CanLeaveAfterTransfer
    {get{var f=Observe();return f.Stable&&f.Foreground&&(f.Page==S(container,"page")||f.Page=="scene");}}
    public bool ReleaseNavigation()
    {exit??=Session.CreateMaintenancePort(BatchId+":exit",Store);exit.Observe();return exit.ReleaseNavigation();}
    public ScreenStorageSupplyPort(ScreenWorkflowSession session,WithdrawalPlan plan,string batchId,IWorkflowStore store,Func<InventorySnapshot>? packetInventory=null):base(session,batchId,store)
    {this.plan=plan;this.packetInventory=packetInventory;config=session.Routes.Profile.Definition["storage_supply"]??throw new ArgumentException("仓库缺少校准");scopeType=plan.Scope.Split(':')[0] switch{"character"=>"character_storage","account"=>"account_storage",var value=>value};container=config["containers"]?[scopeType]??throw new ArgumentException("仓库范围未配置");}
    public static bool ScopeMatches(string scope,SessionScope current)
    {
        if(scope is "account_storage" or "character_storage")return true;
        if(scope.StartsWith("account:",StringComparison.Ordinal))return current.AccountId is {} account&&scope=="account:"+JsonSerializer.Serialize(account);
        if(scope.StartsWith("character:",StringComparison.Ordinal))return current.AccountId is {} a&&current.CharacterId is {} c&&scope=="character:"+JsonSerializer.Serialize(new[]{a,c});
        return false;
    }
    private bool Name(ClientCapture c,JsonNode? region)=>region is not null&&Session.QuantityReader.ExactColor(c.Image,Session.Box(region),plan.Name,minimum:.75);
    protected override PageView Decorate(ClientCapture c,PageView view)
    {
        owner=false;item=false;dialog=false;rowTarget=null;dialogQuantity=null;if(view.Modal)return view;
        var baseScope=scopeType;owner=view.Page==S(container,"page");
        // Qualified owners require a verified account/character identity; process scope cannot invent it.
        var verifiedOwner=ScopeMatches(plan.Scope,Session.VerifiedOwner??Session.Scope);owner&=verifiedOwner;
        var transfer=config["transfer"]!;var selection=config["selection"]!;
        if(Names(transfer["pages"]).Contains(view.Page)||Names(selection["pages"]).Contains(view.Page))
        {
            var spec=Names(transfer["pages"]).Contains(view.Page)?transfer:selection;
            var anchor=S(spec["scope_anchors"],baseScope)??S(transfer["scope_anchors"],baseScope);owner=verifiedOwner&&Session.Hit(c,anchor);item=owner&&Name(c,spec["name_region"]);
            dialog=ReferenceEquals(spec,transfer)&&item;
            if(dialog){var q=transfer["quantity"]!;dialogQuantity=Session.QuantityReader.Read(c.Image,new(plan.ItemId,"storage_transfer_dialog",Session.Box(q["region"]!),S(q,"format")??"integer",Thresholds:q["thresholds"]?.AsArray().Select(v=>v!.GetValue<int>()).ToArray())).Value?.Quantity;}
            if(!item){view=Session.RemoveTarget(view,S(selection,"prepare_target")!);view=Session.RemoveTarget(view,S(transfer,"quantity_target")!);}
            if(!dialog||dialogQuantity!=plan.Quantity)view=Session.RemoveTarget(view,S(transfer,"confirm_target")!);
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
            visible&&o.View.Targets.ContainsKey(S(config["selection"],"prepare_target")!),dialog,dialogQuantity,visible&&o.View.Targets.ContainsKey(S(config["transfer"],"confirm_target")!),false,visible&&rowTarget is not null,visible&&search is not null&&o.View.Targets.ContainsKey(S(search,"target")!),search?["limit"]?.GetValue<int>()??10,search?["interval_s"]?.GetFiniteDouble()??.5,scoped?plan.Scope:null,scoped?bagScope:null,scoped&&inventory is not null?"packet_capture":null,scoped?bag?.VerifiedThrough:null);
    }
    public void Perform(WithdrawalAction action)
    {
        if(action.ItemId!=plan.ItemId||action.Name!=plan.Name||action.Quantity!=plan.Quantity||action.Scope!=plan.Scope)throw new InputBlockedException("取料计划不匹配");
        // These checks precede any native action; consumption itself is wrapped
        // separately by the shared guard, which retains unknown native results.
        var consuming=action.Kind is "confirm_transfer" or "prepare_transfer";
        if(consuming)
        {
            var frame=Frame(Session.LastObservation??throw new InputRejectedBeforeDispatchException("缺少仓库等待观察"));
            if(!frame.Stable||!frame.Foreground||!owner||!item)
                throw new InputRejectedBeforeDispatchException("仓库页面、范围或物品已变化");
            var transfer=config["transfer"]!;
            if(action.Kind=="confirm_transfer")Consume(S(transfer,"confirm_target")!,"confirm_transfer",_=>owner&&item&&dialog&&dialogQuantity==plan.Quantity);
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
            if(frame.Page==S(container,"page"))return;
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
            var shortcut=config["shortcut"];if(shortcut is null)throw new InputBlockedException("仓库导航缺少校准");
            if(S(shortcut["steps"],frame.Page) is {} step)
            {
                // The inn keeper may return to this menu after an intervening
                // dialogue. Opening storage is reversible, so retry only after
                // a fresh observation and a short settle interval.
                if(frame.At-lastMenuOpenAt<1.5)return;
                if(menuOpenAttempts>=3)throw new InputBlockedException("仓库入口菜单三次未打开保管箱");
                Session.NavigateTarget(step);lastMenuOpenAt=frame.At;menuOpenAttempts++;
                Session.TraceNavigation(BatchId,$"target={step}; menu_attempt={menuOpenAttempts}","storage_menu_dispatched");return;
            }
            if(frame.Page=="scene"&&!shortcutSent){var proof=Session.Guard.ObserveCurrentTarget(S(shortcut,"target")!,Session.LastObservation);Session.Guard.PressKey(proof,88,TimeSpan.FromMilliseconds(30));shortcutSent=true;shortcutAt=frame.At;return;}
            if(shortcutSent&&frame.At-shortcutAt<60)return;throw new InputBlockedException("仓库入口未确认");
        }
        if(!owner)throw new InputBlockedException("仓库范围未确认");
        if(action.Kind=="locate")
        {var search=config["search"]!;if(action.SearchIndex<0||action.SearchIndex>=(search["limit"]?.GetValue<int>()??10))throw new InputBlockedException("仓库搜索已到上限");Session.ScrollListDown(Session.LastObservation!,S(search,"target")!);return;}
        if(!item)throw new InputBlockedException("物品名称未确认");
        var transfer=config["transfer"]!;
        if(action.Kind=="set_quantity"){if(!dialog)throw new InputBlockedException("取料数量框未确认");Session.Guard.EditQuantity(Session.Guard.ObserveCurrentTarget(S(transfer,"quantity_target")!,Session.LastObservation),plan.Quantity.ToString(CultureInfo.InvariantCulture));return;}
        var target=action.Kind switch{"select_item"=>rowTarget,_=>null};
        if(target is null)throw new InputBlockedException("取料操作未配置");Session.NavigateTarget(target);
    }
}
