using System.Text.Json;
using MabiAgent.Capture;
using Xunit;

namespace MabiAgent.Capture.Tests;

public sealed class AssociationIdentityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mabi-association-" + Guid.NewGuid().ToString("N"));

    private AccountItemService Service()
    {
        Directory.CreateDirectory(directory);
        var candidates = Path.Combine(directory, "candidates.json");
        File.WriteAllText(candidates, JsonSerializer.Serialize(new { source = "third-party", names = new Dictionary<uint, string> { [123] = "鐵礦石" } }));
        return new(Path.Combine(directory, "accounts"), candidatePath: candidates);
    }

    private static InventorySnapshot Stock() => new(
        new("a", "c", "capture"), new("a", "c", "角色"),
        new Dictionary<InventoryContainer, IReadOnlyDictionary<uint, long>>
        {
            [InventoryContainer.Backpack] = new Dictionary<uint, long> { [123] = 7 },
            [InventoryContainer.CharacterStorage] = new Dictionary<uint, long>(),
            [InventoryContainer.AccountStorage] = new Dictionary<uint, long>()
        }, 1, 1, 1, 2, null);

    [Fact]
    public void ThirdPartyCandidateCannotBecomeVerifiedIdentity()
    {
        var service = Service();
        service.Record(Stock());
        Assert.Null(service.ResolveVerified("a", "鐵礦石"));
    }

    [Fact]
    public void DirectPacketAssociationIsImmediatelyVerifiable()
    {
        var service = Service();
        service.Record(Stock());
        service.RecordCaptureAssociation("a", 123, "鐵礦石", "packet-name", "frame-1");
        Assert.Equal(123u, service.ResolveVerified("a", "铁矿石"));
    }

    [Fact]
    public void PacketEvidenceCannotRestoreClearedOrOverwriteConfirmedIdentity()
    {
        var service = Service(); service.Record(Stock());
        service.RecordCaptureAssociation("a", 123, "鐵礦石", "packet-name", "frame-1");
        Assert.Throws<ArgumentException>(() => service.RecordCaptureAssociation("a", 123, "原木", "packet-name", "frame-2"));
        Assert.Equal(123u, service.ResolveVerified("a", "铁矿石"));
        service.UpdateNames("a", [new(123, null)], NameEditOperation.Clear);
        Assert.Throws<ArgumentException>(() => service.RecordCaptureAssociation("a", 123, "鐵礦石", "packet-name", "frame-3"));
        service.Record(Stock());
        Assert.Equal(NameAssociation.UserCleared, service.View("a").Items.Single(r => r.ItemId == 123).Association);
    }

    [Fact]
    public void VerifiedCatalogReplacesCandidateAndPreservesCapturedFlag()
    {
        var service = Service(); service.Record(Stock());
        var verified = Path.Combine(directory, "verified.json");
        File.WriteAllText(verified, "{\"items\":{\"123\":{\"name\":\"原木\",\"status\":\"verified\"}}}");
        service = new(Path.Combine(directory, "accounts"), verifiedCatalogPath: verified);
        var row = service.View("a").Items.Single(r => r.ItemId == 123);
        Assert.Equal(NameAssociation.BuiltinVerified, row.Association);
        Assert.True(row.Captured);
        Assert.Equal(123u, service.ResolveVerified("a", "原木"));
        service.UpdateNames("a", [new(123, null)], NameEditOperation.Clear);
        Assert.Null(service.ResolveVerified("a", "原木"));
    }

    [Fact]
    public void UserConfirmedCatalogPreservesSourceAndDoesNotWriteAccountFiles()
    {
        var service = Service(); service.Record(Stock());
        var files = service.Files("a");
        var associationBefore = File.ReadAllBytes(files.Associations);
        var inventoryBefore = File.ReadAllBytes(files.Inventory);
        var verified = Path.Combine(directory, "confirmed.json");
        File.WriteAllText(verified, """{"items":{"123":{"name":"鐵礦石","status":"verified","association":"user_confirmed","source":"user_confirmed:2026-10-31:batch"}}}""");
        service = new(Path.Combine(directory, "accounts"), verifiedCatalogPath: verified);
        var row = service.ReadNames("a").Snapshot().Single(r => r.ItemId == 123);
        Assert.Equal(NameAssociation.UserConfirmed, row.Association);
        Assert.Equal("user_confirmed:2026-10-31:batch", row.Source);
        Assert.True(row.Captured);
        Assert.Equal(123u, service.ResolveVerified("a", "鐵礦石"));
        Assert.Equal(associationBefore, File.ReadAllBytes(files.Associations));
        Assert.Equal(inventoryBefore, File.ReadAllBytes(files.Inventory));
    }

    [Fact]
    public void UserConfirmedCatalogPreservesExistingConfirmationAndClear()
    {
        var registry = new ItemNameRegistry();
        registry.Set(new(123, "原木", NameAssociation.UserConfirmed, "manual", true));
        registry.Set(new(456, null, NameAssociation.UserCleared, "manual", true));
        using var json = JsonDocument.Parse("""{"items":{"123":{"name":"鐵礦石","status":"verified","association":"user_confirmed","source":"batch"},"456":{"name":"皮革","status":"verified","association":"user_confirmed","source":"batch"}}}""");
        registry.LoadVerifiedCatalog(json.RootElement);
        Assert.Equal(123u, registry.ResolveVerified("原木"));
        Assert.Null(registry.ResolveVerified("鐵礦石"));
        Assert.Equal(NameAssociation.UserCleared, registry.Snapshot().Single(r => r.ItemId == 456).Association);
    }

    [Fact]
    public void CurrentUserConfirmedCatalogContainsOnlyReviewedListAndKeepsAmbiguousNames()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "config/items.verified.tw.json"))) root = root.Parent;
        Assert.NotNull(root);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "config/items.verified.tw.json")));
        var registry = new ItemNameRegistry(); registry.LoadVerifiedCatalog(json.RootElement);
          Assert.Equal(235, registry.Snapshot().Count);
          Assert.Equal(639212127u,registry.ResolveVerified("厚實羊毛"));
        Assert.Equal(228, registry.Snapshot().Count(r => r.Association == NameAssociation.UserConfirmed && r.Source.StartsWith("user_confirmed:2026-10-31:", StringComparison.Ordinal)));
        Assert.Equal(612255730u, registry.ResolveVerified("高級原木+"));
        var log = registry.Snapshot().Single(r => r.ItemId == 612255730u);
        Assert.StartsWith("user_confirmed:2026-11-02:", log.Source);
        Assert.False(log.Captured);
        Assert.Equal(1804591185u, registry.ResolveVerified("皮革"));
        Assert.Equal(545807217u, registry.ResolveVerified("黏土"));
        Assert.Equal(384237528u, registry.ResolveVerified("煤炭"));
        Assert.Null(registry.ResolveVerified("小銀幣箱"));
        Assert.DoesNotContain(registry.Snapshot(), r => r.ItemId == 1776567393u);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
