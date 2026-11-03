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
        p=p with{AutoBuyBottles=false};Assert.False(g.ReserveForBatch(Water,"a").Ready);Assert.Empty(g.PendingRequests);
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
    [Fact] public void NonTenBatchIsRejectedBeforeInput()
    {var g=new GatheringConsumables(Rules(),()=>Stock(20),()=>new());Assert.False(g.ReserveForBatch(Water with{AttemptsPerBatch=20},"a").Ready);}
}
