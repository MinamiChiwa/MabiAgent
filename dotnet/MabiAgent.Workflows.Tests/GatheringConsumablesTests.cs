using MabiAgent.Core;
using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Workflows.Tests;
public sealed class GatheringConsumablesTests
{
    private static readonly GatheringRoute Water=new("water",2,"daily","水");
    private static readonly GatheringRoute Milk=new("milk",3,"daily","牛奶");
    private static Dictionary<string,GatheringConsumableRule> Rules()=>new(){["water"]=new(1,"空瓶"),["milk"]=new(1,"空瓶")};
    private static InventorySnapshot Stock(long? n,string version="1",string scope="bag:a:c")
    {var s=new InventorySnapshot(scope);s.Set(new(1,n,scope,"packet",version,true));return s;}
    [Fact] public void WaterAndMilkReserveOnceAndCannotDoubleAllocate()
    {
        var stock=Stock(10);var g=new GatheringConsumables(Rules(),()=>stock,()=>new());
        var first=g.ReserveForBatch(Water,"a");Assert.True(first.Ready);Assert.True(g.ReserveForBatch(Water,"a").Ready);
        Assert.False(g.ReserveForBatch(Milk,"b").Ready);Assert.Equal(10,g.PendingRequests.Single().Quantity);
        Assert.Equal(10,stock.Query(1).Quantity);
        g.ReleaseUnsubmitted("a");Assert.True(g.ReserveForBatch(Milk,"b").Ready);Assert.Empty(g.PendingRequests);
    }
    [Fact] public void UnknownAndDisabledPurchasesDoNotBlockUnrelatedGathering()
    {
        var stock=Stock(null);var p=new MaintenancePreferences();var g=new GatheringConsumables(Rules(),()=>stock,()=>p);
        Assert.False(g.ReserveForBatch(Water,"a").Ready);Assert.Empty(g.PendingRequests);
        stock=Stock(3);Assert.False(g.ReserveForBatch(Water,"a").Ready);Assert.Equal(7,g.PendingRequests.Single().Quantity);
        p=p with{AutoBuyBottles=false};Assert.False(g.ReserveForBatch(Water,"a").Ready);Assert.Equal(7,g.PendingRequests.Single().Quantity);
        Assert.True(g.ReserveForBatch(new("ore",4,"mining","铁矿"),"other").Ready);
    }
    [Fact] public void PendingReservationRestoresAndNeedsNewSameOwnerBalanceToRelease()
    {
        var stock=Stock(10);var g=new GatheringConsumables(Rules(),()=>stock,()=>new());
        var held=g.ReserveForBatch(Water,"a").Reservation!;
        var restarted=new GatheringConsumables(Rules(),()=>stock,()=>new(AutoBuyBottles:false),[held]);
        Assert.True(restarted.ReserveForBatch(Water,"a").Ready);Assert.False(restarted.ReconcileEndedBatch("a"));
        stock=Stock(0,"2","bag:a:other");Assert.False(restarted.ReconcileEndedBatch("a"));
        stock=Stock(0,"2");Assert.True(restarted.ReconcileEndedBatch("a"));
        Assert.False(restarted.ReserveForBatch(Water,"new").Ready);
    }
    [Theory][InlineData(0,10)][InlineData(9,1)][InlineData(10,0)]
    public void BatchNeedsTenBottlesBeforeNavigation(long quantity,long deficit)
    {
        var g=new GatheringConsumables(Rules(),()=>Stock(quantity),()=>new());
        var port=new GatheringPort{Frame=new("scene")};var runner=new GatheringRunner(port,new MemoryStore(),Water,"preflight",consumables:g);
        runner.Resume(true);runner.Tick();Assert.Equal(deficit==0,!runner.WaitingForConsumables);Assert.Equal(0,port.Starts);
        Assert.Equal(deficit,g.PendingRequests.Sum(r=>r.Quantity));
    }
    [Fact] public void MissingDialogCancelsOnlyRefusedIntentAndLeavesIndependentWorkAvailable()
    {
        var stock=Stock(10);var g=new GatheringConsumables(Rules(),()=>stock,()=>new());
        var port=new GatheringPort{Frame=new("detail",RouteKey:"water",StartAvailable:true)};
        var runner=new GatheringRunner(port,new MemoryStore(),Water,"missing",consumables:g);runner.Resume(true);runner.Tick();Assert.Equal(1,port.Starts);
        port.Frame=new("gathering_missing_consumables",MissingConsumables:true);runner.Tick();
        Assert.Null(runner.Snapshot()["pending"]);Assert.True(runner.WaitingForConsumables);Assert.Equal(1,port.Dismissals);Assert.Empty(g.Reservations);
        runner.Tick();Assert.Equal(1,port.Starts);Assert.Equal(1,port.Dismissals);
    }
    [Fact] public void MissingDialogWithoutOwnedStartIntentIsNotDismissed()
    {
        var stock=Stock(10);var g=new GatheringConsumables(Rules(),()=>stock,()=>new());
        var port=new GatheringPort{Frame=new("gathering_missing_consumables",RouteKey:"water",StartAvailable:false,MissingConsumables:true)};
        var runner=new GatheringRunner(port,new MemoryStore(),Water,"foreign",consumables:g);runner.Resume(true);runner.Tick();
        Assert.Null(runner.Snapshot()["pending"]);Assert.Equal(0,port.Dismissals);Assert.Equal(0,port.Starts);Assert.Equal(0,port.Navigations);
    }
    [Fact] public void ExplicitMissingRefusalRequiresNewPacketBeforeAnotherStart()
    {
        var stock=Stock(10);var g=new GatheringConsumables(Rules(),()=>stock,()=>new());
        Assert.True(g.ReserveForBatch(Water,"a").Ready);g.RejectMissingBatch("a");
        Assert.Empty(g.Reservations);Assert.False(g.ReserveForBatch(Water,"a").Ready);Assert.Equal(new long[]{1},g.AwaitingInventoryItemIds);
        stock=Stock(0,"2");Assert.False(g.ReserveForBatch(Water,"a").Ready);Assert.Equal(10,g.PendingRequests.Single().Quantity);
        stock=Stock(10,"3");Assert.True(g.ReserveForBatch(Water,"a").Ready);Assert.Empty(g.AwaitingInventoryItemIds);
    }
    [Fact] public void NonTenBatchIsRejectedBeforeInput()
    {var g=new GatheringConsumables(Rules(),()=>Stock(20),()=>new());Assert.False(g.ReserveForBatch(Water with{AttemptsPerBatch=20},"a").Ready);}
    [Fact] public void TwoBottleRoundsAreSuppliedTogetherButReservationsStayPerBatch()
    {
        var stock=Stock(0);stock.SetStorage("shared",new Dictionary<long,long?>{[1]=20});
        var g=new GatheringConsumables(Rules(),()=>stock,()=>new(AutoBuyBottles:false));
        g.SetRemainingDemand(new[]{("water",2L)});
        Assert.False(g.ReserveForBatch(Water,"a").Ready);
        var request=Assert.Single(g.PendingRequests);Assert.Equal(20,request.Quantity);Assert.Equal(20,request.TargetStock);
        stock=Stock(20,"2");g.SetRemainingDemand(new[]{("water",2L)});
        var ready=g.ReserveForBatch(Water,"a");Assert.True(ready.Ready);Assert.Equal(10,ready.Reservation!["quantity"]!.GetValue<long>());
    }
    [Fact] public void ExistingBatchIsNotStarvedWhenRemainingDemandCannotBeSupplied()
    {
        var g=new GatheringConsumables(Rules(),()=>Stock(10),()=>new(AutoBuyBottles:false));
        g.SetRemainingDemand(new[]{("water",2L)});
        Assert.True(g.ReserveForBatch(Water,"a").Ready);Assert.Empty(g.PendingRequests);
    }
    [Fact] public void SharedBottleDemandCombinesRoutesAndClearsWhenQueueStops()
    {
        var g=new GatheringConsumables(Rules(),()=>Stock(0),()=>new(AutoBuyBottles:false));
        g.SetRemainingDemand(new[]{("water",1L),("milk",1L)});
        Assert.False(g.ReserveForBatch(Water,"a").Ready);Assert.Equal(20,g.PendingRequests.Single().TargetStock);
        g.SetRemainingDemand(Array.Empty<(string,long)>());Assert.Empty(g.PendingRequests);
    }
    [Fact] public void WarehouseCompletingRemainingDemandIsPreferredBeforeCurrentBatch()
    {
        var stock=Stock(10);stock.SetStorage("shared",new Dictionary<long,long?>{[1]=10});
        var g=new GatheringConsumables(Rules(),()=>stock,()=>new(AutoBuyBottles:false));
        g.SetRemainingDemand(new[]{("water",2L)});
        Assert.False(g.ReserveForBatch(Water,"a").Ready);
        var request=Assert.Single(g.PendingRequests);Assert.Equal(10,request.Quantity);Assert.Equal(20,request.TargetStock);
    }
    [Fact] public void UnreconciledConsumedBatchIsNotCountedAsAvailableForNextRound()
    {
        var stock=Stock(10);var g=new GatheringConsumables(Rules(),()=>stock,()=>new(AutoBuyBottles:false));
        Assert.True(g.ReserveForBatch(Water,"old").Ready);
        g.SetRemainingDemand(new[]{("water",1L)});
        Assert.False(g.ReserveForBatch(Water,"next").Ready);Assert.Equal(20,g.PendingRequests.Single().TargetStock);Assert.Equal(10,g.PendingRequests.Single().Quantity);
        g.SetRemainingDemand(new[]{("water",1L)});Assert.Equal(20,g.PendingRequests.Single().TargetStock);
    }
    [Fact] public void UnknownInventoryPreservesRequestAndTrustedStockReservesOnce()
    {
        var stock=Stock(3);var g=new GatheringConsumables(Rules(),()=>stock,()=>new());
        g.SetRemainingDemand(new[]{("water",1L)});Assert.False(g.ReserveForBatch(Water,"a").Ready);
        var request=Assert.Single(g.PendingRequests);Assert.Equal(7,request.Quantity);
        stock=Stock(null,"2");g.SetRemainingDemand(new[]{("water",1L)});
        Assert.Equal(request,Assert.Single(g.PendingRequests));
        Assert.False(g.ReserveForBatch(Water,"a").Ready);Assert.Equal(request,Assert.Single(g.PendingRequests));Assert.Equal(new long[]{1},g.AwaitingInventoryItemIds);
        stock=Stock(10,"3");g.SetRemainingDemand(new[]{("water",1L)});
        Assert.Empty(g.PendingRequests);Assert.Empty(g.AwaitingInventoryItemIds);
        Assert.True(g.ReserveForBatch(Water,"a").Ready);Assert.Single(g.Reservations);
        Assert.True(g.ReserveForBatch(Water,"a").Ready);Assert.Single(g.Reservations);
    }
    [Fact] public void ExplicitlyClearedDemandWithdrawsRequestWhileStockIsUnknown()
    {
        var stock=Stock(3);var g=new GatheringConsumables(Rules(),()=>stock,()=>new());
        Assert.False(g.ReserveForBatch(Water,"a").Ready);
        stock=Stock(null,"2");g.SetRemainingDemand(new[]{("water",1L)});
        Assert.Single(g.PendingRequests);Assert.Equal(new long[]{1},g.AwaitingInventoryItemIds);
        g.SetRemainingDemand(Array.Empty<(string,long)>());
        Assert.Empty(g.PendingRequests);Assert.Empty(g.AwaitingInventoryItemIds);
    }
    [Fact] public void ScopeChangeCannotRetainRequestWhenStockIsUnknown()
    {
        var stock=Stock(3);var g=new GatheringConsumables(Rules(),()=>stock,()=>new());
        Assert.False(g.ReserveForBatch(Water,"a").Ready);
        stock=Stock(null,"2","bag:b:c");g.SetRemainingDemand(new[]{("water",1L)});
        Assert.Empty(g.PendingRequests);Assert.Empty(g.AwaitingInventoryItemIds);
    }
    [Fact] public void FirstUnknownStockDoesNotInventRequestQuantity()
    {
        var g=new GatheringConsumables(Rules(),()=>Stock(null),()=>new());
        var decision=g.ReserveForBatch(Water,"a");
        Assert.False(decision.Ready);Assert.Equal("空瓶库存待同步",decision.Reason);
        Assert.Empty(g.PendingRequests);Assert.Equal(new long[]{1},g.AwaitingInventoryItemIds);
    }
}
