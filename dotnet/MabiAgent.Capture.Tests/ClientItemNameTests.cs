using System.Text.Json;
using MabiAgent.Capture;
using Xunit;

namespace MabiAgent.Capture.Tests;

public sealed class ClientItemNameTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mabi-client-names-" + Guid.NewGuid().ToString("N"));
    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "config/client-item-names.tw.json"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("缺少客户端名称表");
    }

    private static JsonDocument Catalog() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "config/client-item-names.tw.json")));

    [Fact]
    public void ClientRowsMatchDirectPacketIdentitiesAndRenderTier()
    {
        using var catalog = Catalog();
        Assert.Equal(5913, catalog.RootElement.GetProperty("items").EnumerateObject().Count());
        var registry = new ItemNameRegistry();
        foreach (var id in new uint[] { 198875762, 1156816717, 612255730, 639212127, 1776567393, 1973547631 })
            registry.Set(new(id, null, NameAssociation.Unknown, "capture", true));
        registry.LoadClientCatalog(catalog.RootElement);
        Assert.Equal(6, registry.Snapshot().Count);
        Assert.Equal(198875762u, registry.ResolveVerified("鐵礦石"));
        Assert.Equal(1156816717u, registry.ResolveVerified("鐵錠"));
        Assert.Equal(612255730u, registry.ResolveVerified("高級原木+"));
        Assert.Equal(639212127u, registry.ResolveVerified("厚實羊毛"));
        Assert.Equal(1776567393u, registry.ResolveVerified("完美繃帶"));
        Assert.Equal(1973547631u, registry.ResolveVerified("★6 附魔卷軸：白薔薇"));
        Assert.All(registry.Snapshot(), r => Assert.StartsWith("client_resource:", r.Source));
    }

    [Fact]
    public void ConfirmationsClearsAndUncapturedRowsTakePriority()
    {
        using var catalog = Catalog();
        var registry = new ItemNameRegistry();
        registry.Set(new(198875762, "保留的名称", NameAssociation.UserConfirmed, "player_edit", true));
        registry.Set(new(1156816717, null, NameAssociation.UserCleared, "player_edit", true));
        registry.Set(new(612255730, "候选", NameAssociation.Candidate, "candidate", false));
        registry.LoadClientCatalog(catalog.RootElement);
        Assert.Equal(198875762u, registry.ResolveVerified("保留的名称"));
        Assert.Equal(NameAssociation.UserCleared, registry.Snapshot().Single(r => r.ItemId == 1156816717).Association);
        Assert.Equal(NameAssociation.Candidate, registry.Snapshot().Single(r => r.ItemId == 612255730).Association);
    }

    [Fact]
    public void CapturedSameNameTypesRemainAmbiguous()
    {
        using var catalog = Catalog();
        var registry = new ItemNameRegistry();
        foreach (var id in new uint[] { 1128414187, 1830952176 }) registry.Set(new(id, null, NameAssociation.Unknown, "capture", true));
        registry.LoadClientCatalog(catalog.RootElement);
        Assert.All(registry.Snapshot(), r => Assert.Equal("堅硬原木", r.Name));
        Assert.Null(registry.ResolveVerified("堅硬原木"));
    }

    [Fact]
    public void FirstPacketPersistsClientNameAndKeepsPacketQuantity()
    {
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(Root(), "config/client-item-names.tw.json"), Path.Combine(directory, "client-item-names.tw.json"));
        var verified = Path.Combine(directory, "verified.json");
        File.WriteAllText(verified, "{\"items\":{}}");
        var service = new AccountItemService(Path.Combine(directory, "accounts"), verified);
        service.Record(new InventorySnapshot(new("a", "c", "capture"), new("a", "c", "角色"),
            new Dictionary<InventoryContainer, IReadOnlyDictionary<uint, long>>
            {
                [InventoryContainer.Backpack] = new Dictionary<uint, long> { [165937923] = 17 },
                [InventoryContainer.CharacterStorage] = new Dictionary<uint, long>(),
                [InventoryContainer.AccountStorage] = new Dictionary<uint, long>()
            }, 1, 1, 1, 2, null));
        var paths = service.Files("a");
        using (var input = File.OpenText(paths.Associations))
            Assert.Equal(165937923u, ItemNameRegistry.ReadCsv(input).ResolveVerified("植物纖維"));
        var inventory = File.ReadAllText(paths.Inventory);
        var row = Assert.Single(service.View("a").Items);
        Assert.Equal(17L, row.ObservedTotal);
        service.UpdateNames("a", [new(165937923, null)], NameEditOperation.Clear);
        Assert.Null(service.ResolveVerified("a", "植物纖維"));
        Assert.Equal(inventory.Replace("植物纖維", ""), File.ReadAllText(paths.Inventory));
        Assert.Equal(17L, Assert.Single(service.View("a").Items).ObservedTotal);
    }

    [Fact]
    public void CurrentGapIdsAllResolveWithoutImportingUnobservedItems()
    {
        using var catalog = Catalog();
        using var gaps = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "docs/ITEM_ASSOCIATION_GAPS.json")));
        var ids = gaps.RootElement.GetProperty("client_resource_resolution").GetProperty("resolved_wire_ids")
            .EnumerateArray().Select(x => uint.Parse(x.GetString()!)).ToArray();
        Assert.Equal(56, ids.Length);
        var registry = new ItemNameRegistry();
        foreach (var id in ids) registry.Set(new(id, null, NameAssociation.Unknown, "capture", true));
        registry.LoadClientCatalog(catalog.RootElement);
        Assert.Equal(ids.Length, registry.Snapshot().Count);
        Assert.All(registry.Snapshot(), r => Assert.Equal(NameAssociation.BuiltinVerified, r.Association));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
