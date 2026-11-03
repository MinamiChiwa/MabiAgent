using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Tests;
public sealed class PurchaseTests : IDisposable
{
    [Fact] public void RestartedUnknownPurchaseCanNavigateForAccountingWithoutPayingAgain()
    {
        var time=new PurchaseTime();var port=new Port{Frame=new(true,"purchase_confirm",true,true,3,50000,7,70)};var file=Path.Combine(directory,"reconcile-navigation.json");
        var runner=new BottlePurchase(file,port,time);runner.Request(3);runner.Tick();Assert.Equal(1,port.Payments);
        port.Frame=new(true,"scene",false,false,null,null,null,null);runner=new BottlePurchase(file,port,time);var actions=port.Actions;runner.ObserveOnly();Assert.Equal(actions,port.Actions);runner.Tick();Assert.True(port.Actions>actions);Assert.Equal(1,port.Payments);
        port.Frame=new(true,"shop_materials",true,false,10,49930,null,null);runner.Tick();Assert.True(runner.State.PaymentConfirmed);Assert.Equal(1,port.Payments);
    }
    [Fact] public void PaperPurchaseUsesPersistedTargetInsteadOfTenBottles()
    {
        var file=Path.Combine(directory,"paper.json");var port=new Port{Frame=new(true,"purchase_confirm",true,true,3,50000,17,170)};
        var runner=new BottlePurchase(file,port,new PurchaseTime());runner.Request(3,targetStock:20);runner.Tick();
        Assert.Equal(17,runner.State.Intent!.Quantity);Assert.Equal(20,runner.State.TargetStock);Assert.Equal(1,port.Payments);
    }
    [Fact] public void PassiveAccountingNeverPaysOrExitsAndLateExitClearsBlockedState()
    {
        var file=Path.Combine(directory,"passive.json");var time=new PurchaseTime();var port=new Port{Frame=new(true,"purchase_confirm",true,true,3,50000,7,70)};
        var runner=new BottlePurchase(file,port,time);runner.Request(3);runner.ObserveOnly();Assert.Equal(0,port.Actions);
        runner.Tick();Assert.Equal(1,port.Payments);var actions=port.Actions;
        port.Frame=port.Frame with{Bottles=10,Gold=49930};runner.ObserveOnly();
        Assert.True(runner.State.PaymentConfirmed);Assert.Equal(PurchaseStage.Exit,runner.State.Stage);Assert.Equal(actions,port.Actions);
        runner.Tick();time.Now=time.Now.AddSeconds(121);runner.Tick();Assert.Equal(PurchaseStage.Blocked,runner.State.Stage);
        var restored=new BottlePurchase(file,port,time);Assert.Equal(PurchaseStage.Exit,restored.State.Stage);Assert.True(restored.State.PaymentConfirmed);
        actions=port.Actions;restored.ObserveOnly();Assert.Equal(actions,port.Actions);
        port.Frame=new(true,"scene",false,false,null,null,null,null);runner.ObserveOnly();
        Assert.Equal(PurchaseStage.Complete,runner.State.Stage);Assert.Equal(1,port.Payments);Assert.Equal(actions,port.Actions);
    }
    [Fact] public void OneSidedBalanceChangeIsNotPaymentConfirmation()
    {
        var port=new Port{Frame=new(true,"purchase_confirm",true,true,3,50000,7,70)};var runner=new BottlePurchase(Path.Combine(directory,"partial.json"),port);
        runner.Request(3);runner.Tick();port.Frame=port.Frame with{Bottles=10};runner.ObserveOnly();Assert.False(runner.State.PaymentConfirmed);
        port.Frame=port.Frame with{Bottles=3,Gold=49930};runner.ObserveOnly();Assert.False(runner.State.PaymentConfirmed);Assert.NotNull(runner.State.Intent);Assert.Equal(1,port.Payments);
        port.Frame=port.Frame with{Bottles=10,BottleNameVerified=false};runner.ObserveOnly();Assert.False(runner.State.PaymentConfirmed);
        port.Frame=port.Frame with{BottleNameVerified=true};runner.ObserveOnly();Assert.True(runner.State.PaymentConfirmed);Assert.Equal(1,port.Payments);
    }
    [Fact] public void StalledExitIsBoundedAndNeverRepeatsPayment()
    {
        var time=new PurchaseTime();var port=new Port{Frame=new(true,"purchase_confirm",true,true,3,50000,7,70)};
        var runner=new BottlePurchase(Path.Combine(directory,"exit-timeout.json"),port,time);runner.Request(3);runner.Tick();Assert.Equal(1,port.Payments);
        port.Frame=port.Frame with{Bottles=10,Gold=49930};runner.Tick();runner.Tick();time.Now=time.Now.AddSeconds(121);runner.Tick();
        Assert.Equal(PurchaseStage.Blocked,runner.State.Stage);Assert.NotNull(runner.State.Intent);Assert.Contains("退出超时",runner.State.Reason);runner.Tick();Assert.Equal(1,port.Payments);
    }
    private sealed class PurchaseTime:TimeProvider{public DateTimeOffset Now=DateTimeOffset.UnixEpoch;public override DateTimeOffset GetUtcNow()=>Now;}
    private readonly string directory=Path.Combine(Path.GetTempPath(),"mabi-test-"+Guid.NewGuid().ToString("N"));
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
    [Fact] public void UnknownPaymentIsNeverReplayedAcrossRestart()
    {
        var file=Path.Combine(directory,"purchase.json");
        var port=new Port();var runner=new BottlePurchase(file,port);
        runner.Request(3,40000);
        port.Frame=new(true,"shop",true,true,3,50000,7,70);
        runner.Tick();Assert.Equal(PurchaseStage.Confirm,runner.State.Stage);
        port.Frame=port.Frame with{Page="purchase_confirm"};
        port.OnPay=()=>Assert.NotNull(new BottlePurchase(file,new Port()).State.Intent);
        runner.Tick();Assert.Equal(1,port.Payments);
        var restarted=new BottlePurchase(file,port);
        restarted.Tick();restarted.Tick();Assert.Equal(1,port.Payments);
        Assert.Equal(PurchaseStage.Reconcile,restarted.State.Stage);
        port.Frame=port.Frame with{Bottles=10,Gold=49930};
        restarted.Tick();Assert.Equal(PurchaseStage.Exit,restarted.State.Stage);
        port.Frame=port.Frame with{Page="scene"};restarted.Tick();
        Assert.Equal(PurchaseStage.Complete,restarted.State.Stage);
    }
    [Fact] public void UnknownStockDoesNotTriggerNavigationAndFocusLossStopsInput()
    {
        var port=new Port();var runner=new BottlePurchase(Path.Combine(directory,"p.json"),port);
        runner.Request(null,40000);runner.Tick();Assert.Equal(0,port.Actions);
        runner.Request(0,40000);port.Frame=new(false,"scene",false,false,0,50000,null,null);
        runner.Tick();Assert.Equal(0,port.Actions);
    }
    [Fact] public void InsufficientGoldOnlyBlocksSupplyBranch()
    {
        var port=new Port{Frame=new(true,"shop",true,true,0,40005,10,10)};
        var runner=new BottlePurchase(Path.Combine(directory,"p.json"),port);
        runner.Request(0,40000);runner.Tick();
        Assert.Equal(PurchaseStage.Blocked,runner.State.Stage);Assert.Equal(0,port.Payments);
    }
    [Fact] public void PurchaseReadsCurrentRepairReserveAndToggle()
    {
        var settings=MaintenancePreferences.FromJson(System.Text.Json.Nodes.JsonNode.Parse("""{"schema_version":1,"repair":{"gold_reserve_min":40000}}""")!.AsObject());
        Assert.True(settings.AutoBuyBottles);
        var port=new Port{Frame=new(true,"purchase_confirm",true,true,0,40100,10,100)};
        var runner=new BottlePurchase(Path.Combine(directory,"p.json"),port,preferences:()=>settings);
        runner.Request(0);runner.Tick();
        Assert.Equal(1,port.Payments); // exact reserve is permitted
        settings=settings with{GoldReserve=39999};runner.Request(0);runner.Tick();
        Assert.Equal(1,port.Payments);
        settings=settings with{AutoBuyBottles=false};
        port.Frame=port.Frame with{Bottles=10,Gold=40000};runner.Tick();
        Assert.Equal(PurchaseStage.Exit,runner.State.Stage); // disabling never cancels reconciliation
        var other=new BottlePurchase(Path.Combine(directory,"other.json"),port,preferences:()=>settings);
        other.Request(0);other.Tick();Assert.Equal(PurchaseStage.Blocked,other.State.Stage);
        Assert.Equal(1,port.Payments);
    }
    [Fact] public void PlanningRefreshDoesNotResetActivePurchase()
    {
        var port=new Port{Frame=new(true,"shop",true,true,0,50000,10,100)};
        var runner=new BottlePurchase(Path.Combine(directory,"p.json"),port);
        runner.Request(0);runner.Tick();var deadline=runner.State.Deadline;
        runner.Request(0);Assert.Equal(PurchaseStage.Confirm,runner.State.Stage);Assert.Equal(deadline,runner.State.Deadline);
    }
    [Fact] public void ExistingEnoughBottlesStillLeavesShopBeforeComplete()
    {
        var port=new Port{Frame=new(true,"shop",true,true,10,50000,0,0)};
        var runner=new BottlePurchase(Path.Combine(directory,"p.json"),port);runner.Request(0);runner.Tick();
        Assert.Equal(PurchaseStage.Exit,runner.State.Stage);runner.Tick();Assert.Equal(1,port.Actions);
        port.Frame=port.Frame with{Page="scene"};runner.Tick();Assert.Equal(PurchaseStage.Complete,runner.State.Stage);
    }
    [Fact] public void EnoughBottlesExitEvenWhenPaymentButtonIsDisabled()
    {
        var port=new Port{Frame=new(true,"shop",true,false,10,0,null,null,DirectPurchase:true)};
        var runner=new BottlePurchase(Path.Combine(directory,"enough.json"),port);runner.Request(0);runner.Tick();runner.Tick();
        Assert.Equal(PurchaseStage.Exit,runner.State.Stage);Assert.Equal(1,port.Actions);Assert.Equal(0,port.Payments);
        port.Frame=port.Frame with{Page="scene"};runner.Tick();Assert.Equal(PurchaseStage.Complete,runner.State.Stage);
    }
    [Fact] public void DirectShopPurchasePersistsIntentWithoutOpeningSecondConfirmation()
    {
        var file=Path.Combine(directory,"direct.json");var port=new Port{Frame=new(true,"shop",true,true,3,50000,7,70,DirectPurchase:true)};
        var runner=new BottlePurchase(file,port);runner.Request(3);
        port.OnPay=()=>Assert.NotNull(new BottlePurchase(file,new Port()).State.Intent);
        runner.Tick();Assert.Equal(PurchaseStage.Reconcile,runner.State.Stage);Assert.Equal(1,port.Actions);Assert.Equal(1,port.Payments);
        new BottlePurchase(file,port).Tick();Assert.Equal(1,port.Payments);
    }
    [Fact] public void DirectShopAdjustsQuantityAndRechecksBeforePayment()
    {
        var port=new Port{Frame=new(true,"shop",true,true,3,50000,1,10,DirectPurchase:true)};
        var runner=new BottlePurchase(Path.Combine(directory,"direct.json"),port);runner.Request(3);runner.Tick();
        Assert.Equal(1,port.Actions);Assert.Equal(0,port.Payments);Assert.Null(runner.State.Intent);
        port.Frame=port.Frame with{Quantity=7,TotalPrice=70};runner.Tick();Assert.Equal(1,port.Payments);
    }
    [Fact] public void RestartAfterReconciliationContinuesExitWithoutReadingShopBalancesAgain()
    {
        var file=Path.Combine(directory,"exit.json");var port=new Port{Frame=new(true,"shop",true,true,3,50000,7,70,DirectPurchase:true)};
        var runner=new BottlePurchase(file,port);runner.Request(3);runner.Tick();
        port.Frame=port.Frame with{Bottles=10,Gold=49930};runner.Tick();Assert.Equal(PurchaseStage.Exit,runner.State.Stage);
        port.Frame=new(true,"shop_farewell",false,false,null,null,null,null);
        var restarted=new BottlePurchase(file,port);var before=port.Actions;restarted.Tick();
        Assert.Equal(before+1,port.Actions);Assert.Equal(1,port.Payments);Assert.NotNull(restarted.State.Intent);
        port.Frame=port.Frame with{Page="scene"};restarted.Tick();Assert.Equal(PurchaseStage.Complete,restarted.State.Stage);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void RejectedPurchaseClearsOnlyProvenUnsentIntent(bool beforeDispatch)
    {
        var port=new Port{Frame=new(true,"shop",true,true,3,50000,7,70,DirectPurchase:true)};
        port.OnPay=()=>{if(beforeDispatch)throw new InputNotDispatchedException("changed");throw new InvalidOperationException("native unknown");};
        var runner=new BottlePurchase(Path.Combine(directory,"rejected.json"),port);runner.Request(3);
        if(beforeDispatch){runner.Tick();Assert.Null(runner.State.Intent);Assert.Equal(PurchaseStage.Confirm,runner.State.Stage);}
        else{Assert.Throws<InvalidOperationException>(()=>runner.Tick());Assert.NotNull(runner.State.Intent);}
        Assert.Equal(0,port.Payments);
    }
    private sealed class Port:IPurchasePort
    {
        public ShopObservation Frame=new(true,"scene",false,false,null,null,null,null);
        public int Payments,Actions;public Action? OnPay;
        public ShopObservation Observe()=>Frame;
        public void NavigateToShop(){Actions++;}
        public void PrepareQuantity(long quantity){Actions++;}
        public void ConfirmPurchase(){OnPay?.Invoke();Actions++;Payments++;}
        public void ExitShop(){Actions++;}
    }
}
