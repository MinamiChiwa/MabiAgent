using System.Text.Json;
using System.Text.Json.Nodes;
namespace MabiAgent.Core;
public enum PurchaseStage { Idle, UnknownStock, Navigate, Confirm, Reconcile, Exit, Complete, Blocked }
public sealed record ShopObservation(bool Foreground,string Page,bool BottleNameVerified,bool ButtonVerified,
    long? Bottles,long? Gold,long? Quantity,long? TotalPrice,bool DirectPurchase=false);
public sealed record PurchaseIntent(Guid Id,long Quantity,long BeforeBottles,long BeforeGold,long Price,DateTimeOffset CreatedAt);
public sealed record PurchaseState(PurchaseStage Stage,long Quantity=0,long Reserve=40000,PurchaseIntent? Intent=null,string? Reason=null,DateTimeOffset? Deadline=null,bool PaymentConfirmed=false,long TargetStock=10,double? CompletedThrough=null);
public interface IPurchasePort
{
    ShopObservation Observe();
    void NavigateToShop();
    void PrepareQuantity(long quantity);
    void ConfirmPurchase();
    void ExitShop();
}

// Port methods must use the shared foreground input guard. Packet quantities
// may request a visit, but only this current screen can authorize payment.
public sealed class BottlePurchase
{
    private readonly string path;private readonly IPurchasePort port;private readonly TimeProvider time;
    private readonly Func<MaintenancePreferences>? preferences;
    private readonly Func<double?>? coverage;
    private DateTimeOffset? exitDeadline;
    public PurchaseState State {get;private set;}
    public BottlePurchase(string path,IPurchasePort port,TimeProvider? time=null,Func<MaintenancePreferences>? preferences=null,Func<double?>? coverage=null)
    {
        this.path=path;this.port=port;this.time=time??TimeProvider.System;
        this.preferences=preferences;this.coverage=coverage;
        State=File.Exists(path)?JsonSerializer.Deserialize<PurchaseState>(File.ReadAllText(path))??throw new InvalidDataException("采购记录损坏"):new(PurchaseStage.Idle);
        // Exit is persisted only after reconciliation; dialogue pages no longer expose balances.
        if(State.Intent is not null && State.Stage is not (PurchaseStage.Exit or PurchaseStage.Complete))
            State=State with{Stage=State.PaymentConfirmed?PurchaseStage.Exit:PurchaseStage.Reconcile,Reason=State.PaymentConfirmed?"付款已核对，等待退出":"重启后仅核账"};
        if(State.Stage==PurchaseStage.Exit)State=State with{PaymentConfirmed=true};
    }
    private void Save(PurchaseState state){AtomicJson.Write(path,JsonSerializer.SerializeToNode(state)!);State=state;}
    public void Request(long? bottles,long reserve=40000,long targetStock=10)
    {
        if(reserve<0||targetStock<1)throw new ArgumentOutOfRangeException(nameof(reserve));
        if(State.Intent is not null && State.Stage!=PurchaseStage.Complete)return;
        if(State.Stage is PurchaseStage.Navigate or PurchaseStage.Confirm or PurchaseStage.Exit)return;
        var settings=preferences?.Invoke()??new(reserve);
        reserve=settings.GoldReserve;
        var plan=new ConsumablePolicy("采购材料",1,checked((int)targetStock)).Plan(bottles);
        if(plan.Status==SupplyStatus.PurchaseRequired && !settings.AutoBuyBottles)
        {Save(new(PurchaseStage.Blocked,plan.PurchaseQuantity,reserve,Reason:"自动购买空瓶已关闭"));return;}
        Save(new(plan.Status switch{SupplyStatus.Unknown=>PurchaseStage.UnknownStock,SupplyStatus.Ready=>PurchaseStage.Complete,_=>PurchaseStage.Navigate},
            plan.PurchaseQuantity,reserve,Deadline:time.GetUtcNow().AddMinutes(5),TargetStock:targetStock));
    }
    public void ObserveOnly()=>Step(false);
    public void Tick()=>Step(true);
    private void Step(bool allowInput)
    {
        if(State.Stage is PurchaseStage.Idle or PurchaseStage.Complete or PurchaseStage.UnknownStock)return;
        if(State.Stage==PurchaseStage.Blocked&&State.Intent is null&&!State.PaymentConfirmed)return;
        var f=port.Observe();if(!f.Foreground)return;
        if(State.Stage==PurchaseStage.Exit||State.PaymentConfirmed)
        {
            if(f.Page=="scene")
            {
                var observed=coverage?.Invoke();
                Save(State with{Stage=PurchaseStage.Complete,Reason=null,PaymentConfirmed=true,CompletedThrough=observed is{} through&&double.IsFinite(through)?through:null});return;
            }
            if(!allowInput||State.Stage==PurchaseStage.Blocked)return;
            exitDeadline??=time.GetUtcNow().AddSeconds(120);
            if(time.GetUtcNow()>=exitDeadline){Save(State with{Stage=PurchaseStage.Blocked,Reason="采购退出超时，保留已核对付款记录"});return;}
            port.ExitShop();
            return;
        }
        if(State.Intent is { } intent)
        {
            // Both sides must match; unrelated incoming items are not purchase proof.
            if(f.BottleNameVerified && f.Bottles==checked(intent.BeforeBottles+intent.Quantity) && f.Gold==intent.BeforeGold-intent.Price)
                Save(State with{Stage=PurchaseStage.Exit,Reason=null,PaymentConfirmed=true});
            else if(allowInput&&!f.BottleNameVerified)
            {
                exitDeadline??=time.GetUtcNow().AddSeconds(180);
                if(time.GetUtcNow()<exitDeadline)port.NavigateToShop();
            }
            return;
        }
        if(!allowInput)return;
        var preferencesNow=preferences?.Invoke()??new(State.Reserve);
        if(!preferencesNow.AutoBuyBottles){Save(State with{Stage=PurchaseStage.Blocked,Reason="自动购买空瓶已关闭"});return;}
        if(time.GetUtcNow()>State.Deadline){Save(State with{Stage=PurchaseStage.Blocked,Reason="商店导航或报价超时"});return;}
        if(f.Page is not ("shop" or "purchase_confirm")){port.NavigateToShop();return;}
        if(!f.BottleNameVerified||f.Bottles is null||f.Bottles<0)return;
        var required=Math.Max(0,State.TargetStock-f.Bottles.Value);
        if(required==0){Save(State with{Stage=PurchaseStage.Exit,Quantity=0,PaymentConfirmed=true});return;}
        if(!f.ButtonVerified||f.Gold is null||f.Gold<0)return;
        if(f.Quantity!=required||f.TotalPrice is null||f.TotalPrice<0){port.PrepareQuantity(required);return;}
        if(!preferencesNow.CanSpend(f.Gold.Value,f.TotalPrice.Value)){Save(State with{Stage=PurchaseStage.Blocked,Reason="空瓶采购后金币不足留存",Reserve=preferencesNow.GoldReserve});return;}
        if(f.Page=="shop"&&!f.DirectPurchase){port.PrepareQuantity(required);Save(State with{Stage=PurchaseStage.Confirm,Quantity=required});return;}
        var pending=new PurchaseIntent(Guid.NewGuid(),required,f.Bottles.Value,f.Gold.Value,f.TotalPrice.Value,time.GetUtcNow());
        Save(State with{Stage=PurchaseStage.Reconcile,Intent=pending,Reason="等待购买结果"});
        try{port.ConfirmPurchase();}
        catch(InputNotDispatchedException)
        {Save(State with{Stage=PurchaseStage.Confirm,Intent=null,Reason="付款前页面变化，重新核对报价"});}
    }
}
