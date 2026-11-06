using System.Globalization;
using System.Text.Json.Nodes;
using MabiAgent.Core;
using MabiAgent.Workflows;
namespace MabiAgent.Windows;

/// <summary>Shop adapter using the shared map navigator and reviewed page coordinates.</summary>
public sealed class ScreenPurchasePort:ScreenWorkflowPort,IPurchasePort
{
    private readonly JsonNode? config;private readonly long itemId;private readonly string itemName;private readonly Func<PurchaseState?> purchase;private readonly Func<MaintenancePreferences> preferences;
    private bool identity,shopNavigationActive;private long? selected,price,bottles,gold;private readonly ScreenQuantityStepper stepper=new();private readonly ScreenMaintenancePort navigator;
    private readonly Func<InventorySnapshot>? packetInventory;
    private sealed class PurchaseStore(Func<PurchaseState?> read):IWorkflowStore
    {
        public JsonObject? Read()=>read()?.Intent is {} p?new JsonObject{["pending"]=new JsonObject{["kind"]="purchase",["id"]=p.Id.ToString(),["quantity"]=p.Quantity,["before_bottles"]=p.BeforeBottles,["before_gold"]=p.BeforeGold,["price"]=p.Price,["at"]=p.CreatedAt.ToUnixTimeMilliseconds()}}:null;
        public void Flush(JsonObject state)=>throw new InvalidOperationException("采购状态由采购流程落盘");
    }
    public ScreenPurchasePort(ScreenWorkflowSession session,long itemId,string verifiedName,string batchId,Func<PurchaseState?> persistedPurchase,Func<MaintenancePreferences> preferences,Func<InventorySnapshot>? packetInventory=null):base(session,batchId,new PurchaseStore(persistedPurchase))
    {this.itemId=itemId;itemName=verifiedName;purchase=persistedPurchase;this.preferences=preferences;this.packetInventory=packetInventory;var common=session.Routes.Configuration["purchase"]??session.Routes.Profile.Definition["purchase"];config=common?.DeepClone();if(config is JsonObject itemConfig&&common?["items"]?[verifiedName] is JsonObject overrides)foreach(var (key,value) in overrides)itemConfig[key]=value?.DeepClone();navigator=session.CreateMaintenancePort(batchId+":shop-navigation",new PurchaseStore(persistedPurchase));}
    public string? CalibrationStatus=>config?["item_name_region"] is not null&&S(config,"confirm_target") is not null&&config["navigation"] is not null?null:"shop_calibration_missing";
    private void Calibrated(){if(itemName is not ("空瓶" or "紙"))throw new InputBlockedException("该材料禁止自动购买，请手动补充");if(CalibrationStatus is {} reason)throw new InputBlockedException(reason);}
    protected override PageView Decorate(ClientCapture c,PageView view)
    {
        identity=false;if(config is null||view.Modal)return view;
        if(Names(config["pages_with_item"]).Contains(view.Page)&&(config["item_name_regions"]?[view.Page]??config["item_name_region"]) is {} region)identity=Session.QuantityReader.ExactColor(c.Image,Session.Box(region),itemName,minimum:.75);
        var enabled=false;if(config["confirm_enabled_region"] is{} enabledRegion)
        {var p=c.Image.Crop(Session.Box(enabledRegion)).Pixels;var green=0;for(var i=0;i<p.Length;i+=3)if(p[i+1]>120&&p[i+1]-p[i]>65&&p[i+1]>p[i+2])green++;enabled=green/(p.Length/3d)>.65;}
        if((!identity||!enabled)&&S(config,"confirm_target") is {} target)view=Session.RemoveTarget(view,target);return view;
    }
    public ShopObservation Observe()
    {
        var o=Read();selected=price=bottles=gold=null;if(config is not null&&o.Stable&&Session.InputAvailable&&identity)
        {
            if(o.View.Page==S(config,"payment_page"))
            {
                // Reaching the selected item completes entry navigation. The
                // paid exit must not inherit its old selection wait.
                if(shopNavigationActive){navigator.ResetNavigation();shopNavigationActive=false;}
                selected=Session.ConfirmedQuantity("purchase.quantity",config["quantity"],itemId,"shop_quantity")?.Quantity;price=Session.ConfirmedQuantity("purchase.price",config["total_price"],1,"shop_price")?.Quantity;
            }
            var packet=packetInventory?.Invoke().Query(itemId);bottles=packet is{Confirmed:true}?packet.Quantity:null;
            gold=Session.ReadGold(()=>Session.ConfirmedQuantity("purchase.gold:"+o.View.Page,config["gold"],1,"gold")?.Quantity);
        }
        return new(Session.InputAvailable&&o.Stable,o.View.Page,identity,config is not null&&o.View.Targets.ContainsKey(S(config,"confirm_target")??""),bottles,gold,selected,price,config?["direct_purchase"]?.GetValue<bool>()==true);
    }
    public void NavigateToShop()
    {
        Calibrated();var o=Read();if(!o.Stable||!Session.InputAvailable)return;
        shopNavigationActive=true;
        if(o.View.Page=="shop_materials")
        {
            var target=config?["select_target"]?.GetValue<string>()??"purchase.select_bottle";
            var result=navigator.TestNavigationStep(new JsonObject{["target"]=target,["expected_page"]="shop",["settle_s"]=0.5});
            if(result.Error is{} error)throw new InputBlockedException(error);return;
        }
        var reason=navigator.NavigateObserved("purchase_shop",o);if(reason is not null)throw new InputBlockedException(reason);
    }
    public void PrepareQuantity(long quantity)
    {
        Calibrated();var observation=Observe();if(!identity||!observation.Foreground)return;
        if(S(config,"quantity_mode")=="stepper")
        {
            var now=Session.LastCapture!.CapturedAt;var delta=stepper.Next(observation.Quantity,quantity,now);if(delta==0)return;
            var target=S(config,delta>0?"increase_target":"decrease_target")??throw new InputBlockedException("shop_quantity_calibration_missing");
            var view=Session.LastObservation!.View;if(!view.Targets.TryGetValue(target,out var point))throw new InputBlockedException("采购数量按钮未配置");
            Session.Guard.ClickAt(point,view.TargetRegions?.GetValueOrDefault(target));stepper.Issued(observation.Quantity!.Value,delta,Session.LastCapture!.CapturedAt);return;
        }
        throw new InputBlockedException("shop_quantity_calibration_missing");
    }
    public void ConfirmPurchase()
    {
        Calibrated();Consume(S(config,"confirm_target")!,"purchase",o=>
        {
            var pending=purchase()?.Intent;if(pending is null||o.View.Page!=S(config,"payment_page")||!identity)return false;
            var allowed=preferences().AutoBuyBottles&&preferences().CanSpend(pending.BeforeGold,pending.Price);
            if(allowed)Session.InvalidatePacketGold();return allowed;
        });
    }
    public void ExitShop(){Calibrated();var o=Read();if(o.View.Page=="scene"||!o.Stable||!Session.InputAvailable)return;var reason=navigator.NavigateObserved("purchase_exit",o);if(reason is not null)throw new InputBlockedException(reason);}
}
