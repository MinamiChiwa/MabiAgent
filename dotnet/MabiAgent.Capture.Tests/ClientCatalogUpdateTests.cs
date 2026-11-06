using System.Text.Json;
using System.Text.Json.Nodes;
using MabiAgent.Capture;
using Xunit;

namespace MabiAgent.Capture.Tests;

public sealed class ClientCatalogUpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mabi-catalog-update-" + Guid.NewGuid().ToString("N"));
    private static JsonElement Catalog(string name = "鐵礦石") => JsonSerializer.SerializeToElement(new
    {
        schema_version = 1, table = "Consumable", index_sha256 = new string('a', 64),
        items = new Dictionary<string, object> { ["123"] = new { name, row = 0 }, ["456"] = new { name = "原木", row = 1 }, ["789"] = new { name = "羊毛", row = 2 } }
    });
    private AccountItemService Service() => new(Path.Combine(root, "accounts"));
    private static void Record(AccountItemService service, string session = "session", int revision = 1) => service.Record(new(
        new("a", "c", session), new("a", "c", "角色"),
        new Dictionary<InventoryContainer, IReadOnlyDictionary<uint, long>>
        {
            [InventoryContainer.Backpack] = new Dictionary<uint, long> { [123] = 7, [456] = 9 },
            [InventoryContainer.CharacterStorage] = new Dictionary<uint, long>(),
            [InventoryContainer.AccountStorage] = new Dictionary<uint, long>()
        }, revision, revision, revision, revision + 1, null));

    [Fact]
    public void UpdateAppliesImmediatelyAndSurvivesRestartWithoutChangingQuantityFiles()
    {
        var service = Service(); Record(service);
        var paths = service.Files("a"); var inventory = File.ReadAllBytes(paths.Inventory);
        var version = service.Version;
        var result = service.UpdateClientCatalog(Catalog());
        Assert.Equal(new ClientCatalogUpdate(3, 2), result);
        Assert.True(service.Version > version);
        Assert.Equal(123u, service.ResolveVerified("a", "鐵礦石"));
        Assert.Equal(2, service.View("a").Items.Count);
        Assert.Equal(inventory, File.ReadAllBytes(paths.Inventory));
        Assert.Equal(123u, Service().ResolveVerified("a", "鐵礦石"));
        Assert.Equal(7L, service.View("a").Items.Single(r => r.ItemId == 123).ObservedTotal);
        Record(service, revision: 3);
        using var reader = File.OpenText(paths.Associations);
        Assert.Equal(123u, ItemNameRegistry.ReadCsv(reader).ResolveVerified("鐵礦石"));
    }

    [Fact]
    public void AutomaticNamesRefreshWhilePlayerDecisionsRemain()
    {
        var service = Service(); Record(service); service.UpdateClientCatalog(Catalog());
        Record(service, revision: 3);
        Assert.Equal(1, service.UpdateClientCatalog(Catalog("新礦石")).ChangedAssociations);
        Assert.Equal(123u, service.ResolveVerified("a", "新礦石"));
        service.UpdateNames("a", [new(123, "玩家名稱")], NameEditOperation.Confirm);
        service.UpdateNames("a", [new(456, null)], NameEditOperation.Clear);
        Assert.Equal(0, service.UpdateClientCatalog(Catalog()).ChangedAssociations);
        Assert.Equal(123u, Service().ResolveVerified("a", "玩家名稱"));
        Assert.Equal(NameAssociation.UserCleared, service.View("a").Items.Single(r => r.ItemId == 456).Association);
    }

    [Fact]
    public void MalformedUnobservedEntryDoesNotReplaceCurrentCatalog()
    {
        var service = Service(); Record(service); service.UpdateClientCatalog(Catalog());
        var path = Path.Combine(root, "accounts", "client-item-names.tw.json"); var saved = File.ReadAllBytes(path); var version = service.Version;
        var invalid = JsonNode.Parse(Catalog().GetRawText())!;
        invalid["items"]!["789"]!["name"] = "";
        Assert.Throws<InvalidDataException>(() => service.UpdateClientCatalog(JsonSerializer.SerializeToElement(invalid)));
        Assert.Equal(saved, File.ReadAllBytes(path)); Assert.Equal(version, service.Version);
        Assert.Equal(123u, service.ResolveVerified("a", "鐵礦石"));
    }

    [Fact]
    public void LocationUsesSelectedExecutableAndManualFallback()
    {
        var game = Path.Combine(root, "appdata"); var starter = Path.Combine(game, "ResourcesPackage", "Starter");
        Directory.CreateDirectory(starter); File.WriteAllBytes(Path.Combine(starter, "index"), []);
        Assert.Equal(game, ClientItemCatalogLocation.Resolve(Path.Combine(game, "Bin", "Game.exe")));
        Assert.Equal(game, ClientItemCatalogLocation.Resolve(Path.Combine(root, "Launcher", "Launcher.exe"), game));
        Assert.Throws<DirectoryNotFoundException>(() => ClientItemCatalogLocation.Resolve(Path.Combine(root, "Launcher.exe")));
    }

    [Fact]
    public void OtherVerifiedSourcesRemainAndAutomaticDuplicateNamesStayAmbiguous()
    {
        var registry = new ItemNameRegistry();
        registry.Set(new(123, "原名", NameAssociation.BuiltinVerified, "packet_identity", true));
        registry.Set(new(456, null, NameAssociation.Unknown, "capture", true));
        registry.LoadClientCatalog(Catalog("原木"));
        Assert.Equal(123u, registry.ResolveVerified("原名"));
        registry.Set(new(123, "旧自动名称", NameAssociation.BuiltinVerified, "client_resource:old", true));
        registry.LoadClientCatalog(Catalog("原木"));
        Assert.Null(registry.ResolveVerified("原木"));
        Assert.All(registry.Snapshot(), row => Assert.Equal("原木", row.Name));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
