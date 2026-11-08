using MabiAgent.Capture;
using Xunit;

namespace MabiAgent.Capture.Tests;

public sealed class CapturePlanningAdapterTests : IDisposable
{
    [Fact] public void LinkedNonRecipeItemsKeepWireIdNameAndCurrentPacketBalance()
    {
        var accounts=new AccountItemService(directory);accounts.Record(Snapshot());
        accounts.UpdateNames("a",[new(123,"鐵礦石"),new(456,"仓库纪念品")],NameEditOperation.Confirm);
        var catalog=new Dictionary<long,string>{{CatalogOre,"铁矿石"},{9000,"采集点:logging:南侧城墙"}};
        var choices=LinkedItemCatalog.Names(accounts.ReadNames("a"),catalog);
        Assert.Equal(2,choices.Count);Assert.Equal("仓库纪念品",choices[456]);Assert.Contains(CatalogOre,choices.Keys);Assert.DoesNotContain(9000L,choices.Keys);
        var adapter=new CapturePlanningAdapter(accounts,catalog);var first=adapter.Convert(Snapshot()).Inventory!;
        Assert.Equal(20,first.Query(456).Quantity);Assert.True(first.Query(456).Confirmed);Assert.Equal("仓库纪念品",adapter.Name(456,Snapshot()));
        var after=adapter.Convert(Snapshot(other:12,revision:2)).Inventory!;
        Assert.Equal(12,after.Query(456).Quantity);Assert.NotEqual(first.Query(456).Version,after.Query(456).Version);
        accounts.UpdateNames("a",[new(456,null)],NameEditOperation.Clear);
        Assert.DoesNotContain(456L,LinkedItemCatalog.Names(accounts.ReadNames("a"),catalog).Keys);
        Assert.Null(adapter.Convert(Snapshot()).Inventory!.Query(456).Quantity);Assert.Null(adapter.Name(456,Snapshot()));
    }
    [Fact] public void LinkedChoicesExcludeUnseenCandidateClearedAndAmbiguousNames()
    {
        var registry=new ItemNameRegistry();
        registry.Set(new(1,"配方材料",NameAssociation.BuiltinVerified,"test",true));
        registry.Set(new(2,"目录外",NameAssociation.UserConfirmed,"test",true));
        registry.Set(new(3,"未捕获",NameAssociation.BuiltinVerified,"test",false));
        registry.Set(new(4,"候选",NameAssociation.Candidate,"test",true));
        registry.Set(new(5,"清除",NameAssociation.UserCleared,"test",true));
        registry.Set(new(6,"同名",NameAssociation.UserConfirmed,"test",true));registry.Set(new(7,"同名",NameAssociation.UserConfirmed,"test",true));
        var result=LinkedItemCatalog.Names(registry,new Dictionary<long,string>{{100,"配方材料"},{200,"采集点:logging:木材"}});
        Assert.Equal(new long[]{2,100},result.Keys.Order().ToArray());
    }
    private const long CatalogOre = 4560776048983186;
    private const long CatalogOther = 4557102894501861;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mabi-planning-test-" + Guid.NewGuid().ToString("N"));
    private static InventorySnapshot Snapshot(long ore = 7, long other = 20, long revision = 1, long full = 1) => new(new("a", "c", "s"), new("a", "c", "角色"),
        new Dictionary<InventoryContainer, IReadOnlyDictionary<uint, long>>
        { [InventoryContainer.Backpack] = new Dictionary<uint,long> { [123] = ore, [456] = other }, [InventoryContainer.CharacterStorage] = new Dictionary<uint,long> { [123] = 9 }, [InventoryContainer.AccountStorage] = new Dictionary<uint,long> { [123] = 12 } }, revision, full, 1, 2, null);
    private CapturePlanningAdapter Adapter(IReadOnlyDictionary<long, string>? names = null)
    {
        var accounts = new AccountItemService(directory); accounts.Record(Snapshot());
        accounts.UpdateNames("a", [new(123, "鐵礦石"), new(456, "原木")], NameEditOperation.Confirm);
        return new(accounts, names ?? new Dictionary<long, string> { [CatalogOre] = "铁矿石", [CatalogOther] = "原木" });
    }
    [Fact] public void LongCatalogIdsMapByVerifiedNameAndStorageStaysSeparate()
    {
        var adapter = Adapter(); var result = adapter.Convert(Snapshot()); Assert.Empty(result.Issues);
        Assert.NotNull(result.Inventory); Assert.Equal(7, result.Inventory.Query(CatalogOre).Quantity);
        Assert.Equal(2, result.Inventory.Storage.Count); Assert.Equal(21, result.Inventory.Storage.Values.Sum(r => r[CatalogOre]));
        Assert.Equal(CapturePlanningAdapter.ScopeKey(Snapshot().Scope!), result.Scope);
        Assert.Null(result.Inventory.Query(123).Quantity);
    }
    [Fact] public void VersionsOnlyChangeForThatItemOrFullSyncAndTrackRoundTrips()
    {
        var adapter = Adapter(); var first = adapter.Convert(Snapshot()).Inventory!;
        var second = adapter.Convert(Snapshot(other: 21, revision: 2)).Inventory!;
        Assert.Equal(first.Query(CatalogOre).Version, second.Query(CatalogOre).Version);
        Assert.NotEqual(first.Query(CatalogOther).Version, second.Query(CatalogOther).Version);
        var changed = adapter.Convert(Snapshot(ore: 6, revision: 3)).Inventory!;
        Assert.NotEqual(first.Query(CatalogOre).Version, changed.Query(CatalogOre).Version);
        Assert.NotEqual(first.Query(CatalogOre).Version, adapter.Convert(Snapshot(revision: 4)).Inventory!.Query(CatalogOre).Version);
        Assert.NotEqual(second.Query(CatalogOther).Version, adapter.Convert(Snapshot(other: 21, full: 2)).Inventory!.Query(CatalogOther).Version);
    }
    [Fact] public void UnknownAndAmbiguousNamesNeverBecomeZeroOrNumericGuesses()
    {
        var adapter = Adapter(new Dictionary<long, string> { [CatalogOre] = "铁矿石", [123] = "鐵礦石", [456] = "未验证" });
        var result = adapter.Convert(Snapshot()); Assert.Equal(3, result.Issues.Count);
        Assert.All(result.Inventory!.Observations.Values, row => { Assert.Null(row.Quantity); Assert.False(row.Confirmed); });
        Assert.Null(adapter.Convert(Snapshot() with { VerifiedThrough = null }).Inventory);
        Assert.Null(adapter.Convert(Snapshot() with { VerifiedThrough = double.NaN }).Inventory);
        Assert.Null(adapter.Convert(Snapshot() with { Identity = new("other", "c", "wrong") }).Scope);
    }
    [Fact] public void ClearingAssociationMakesCurrentMappingUnknown()
    {
        var adapter = Adapter(); Assert.Equal(7, adapter.Convert(Snapshot()).Inventory!.Query(CatalogOre).Quantity);
        new AccountItemService(directory).UpdateNames("a", [new(123, null)], NameEditOperation.Clear);
        Assert.Null(adapter.Convert(Snapshot()).Inventory!.Query(CatalogOre).Quantity);
    }
    [Fact] public void ContinuousCoverageAndItemChangesHaveIndependentPlanningMeaning()
    {
        var adapter=Adapter();var first=adapter.Convert(Snapshot()).Inventory!;
        var continued=adapter.Convert(Snapshot() with{VerifiedThrough=5}).Inventory!;
        Assert.Equal(5,continued.Query(CatalogOre).VerifiedThrough);Assert.Equal(first.Query(CatalogOre).Version,continued.Query(CatalogOre).Version);
        var roundtrip=adapter.Convert(Snapshot() with{ItemRevisions=new Dictionary<uint,long>{{123,2}}}).Inventory!;
        Assert.NotEqual(first.Query(CatalogOre).Version,roundtrip.Query(CatalogOre).Version);Assert.Equal(first.Query(CatalogOther).Version,roundtrip.Query(CatalogOther).Version);
        var incomplete=Snapshot() with{Counts=new Dictionary<InventoryContainer,IReadOnlyDictionary<uint,long>>{{InventoryContainer.Backpack,new Dictionary<uint,long>()}}};
        Assert.Null(adapter.Convert(incomplete).Inventory);
    }
    [Fact] public void PacketIdentityAssociationUsesStableEvidenceWithoutScreenQuantitySamples()
    {
        var accounts = new AccountItemService(directory); accounts.Record(Snapshot());
        var view = accounts.RecordCaptureAssociation("a", 123, "鐵礦石", "packet-name", "frame-1");
        var row = view.Items.Single(r => r.ItemId == 123);
        Assert.Equal(NameAssociation.UserConfirmed, row.Association);
        Assert.Equal(123u, accounts.ResolveVerified("a", "铁矿石"));
    }
    [Fact] public void CorruptAssociationCsvReturnsAssociationUnavailableWithoutEscaping()
    {
        var adapter=Adapter();var accounts=new AccountItemService(directory);
        File.WriteAllText(accounts.Files("a").Associations,"item_id,name\n\"unclosed,");
        var result=adapter.Convert(Snapshot());
        Assert.NotNull(result.Inventory);var issue=Assert.Single(result.Issues);
        Assert.Equal("association_unavailable",issue.Kind);Assert.Equal(CapturePlanningAdapter.ScopeKey(Snapshot().Scope!),result.Scope);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
