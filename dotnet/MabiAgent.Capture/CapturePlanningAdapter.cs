using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MabiAgent.Capture;

public sealed record CaptureMappingIssue(long? CatalogItemId, string Kind, string Message);
public sealed record CapturePlanningResult(MabiAgent.Core.InventorySnapshot? Inventory, string? Scope, IReadOnlyList<CaptureMappingIssue> Issues);

/// <summary>Joins catalog IDs to wire IDs only by unique verified names. Capture remains planning evidence.</summary>
public sealed class CapturePlanningAdapter(AccountItemService accounts, IReadOnlyDictionary<long, string> catalogNames)
{
    private readonly Dictionary<long, string> names = catalogNames.ToDictionary();
    private readonly object gate = new();
    private readonly Dictionary<long, (string Basis, long Revision)> versions = [];
    private readonly string instance = Guid.NewGuid().ToString("N");
    public static string ScopeKey(InventoryScope scope) => JsonSerializer.Serialize(new[] { scope.AccountId, scope.CharacterId, scope.SessionId });
    public CapturePlanningResult Convert(InventorySnapshot? capture)
    {
        if (capture?.Scope is not { } scope || capture.Identity is not { } identity || capture.VerifiedThrough is null || capture.Error is not null ||
            !double.IsFinite(capture.VerifiedThrough.Value) || string.IsNullOrWhiteSpace(scope.AccountId) || string.IsNullOrWhiteSpace(scope.CharacterId) || string.IsNullOrWhiteSpace(scope.SessionId) ||
            scope.AccountId != identity.AccountId || scope.CharacterId != identity.CharacterId || capture.FullSyncRevision <= 0 ||
            capture.Counts.Count != 3 || Enum.GetValues<InventoryContainer>().Any(c=>!capture.Counts.ContainsKey(c)))
            return new(null, null, [new(null, "capture_unavailable", "当前抓包会话尚未完整同步")]);
        var scopeKey = ScopeKey(scope); var inventory = new MabiAgent.Core.InventorySnapshot(scopeKey); var issues = new List<CaptureMappingIssue>();
        ItemNameRegistry registry;
        try { registry = accounts.ReadNames(scope.AccountId); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(inventory, scopeKey, [new(null, "association_unavailable", "道具关联不可用：" + error.Message)]); }
        var duplicates = names.Where(p => p.Key > 0 && !string.IsNullOrWhiteSpace(p.Value)).GroupBy(p => ItemNameRegistry.Normalize(p.Value))
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var characterStorage = new Dictionary<long, long?>(); var accountStorage = new Dictionary<long, long?>();
        foreach (var (catalogId, name) in names)
        {
            string? reason = null; uint? wire = null;
            if (catalogId <= 0 || string.IsNullOrWhiteSpace(name)) reason = "目录名称无效";
            else if (duplicates.Contains(ItemNameRegistry.Normalize(name))) reason = "目录存在同名编号，无法唯一关联";
            else if ((wire = registry.ResolveVerified(name)) is null) reason = "没有唯一可信的道具名称关联";
            if (catalogId <= 0) { issues.Add(new(catalogId, "invalid_catalog_id", reason!)); continue; }
            var quantity = wire.HasValue ? capture.Quantity(InventoryContainer.Backpack, wire.Value) : null;
            if (reason is null && quantity is null) reason = "当前背包覆盖未知";
            if (reason is not null) issues.Add(new(catalogId, "mapping_unknown", reason));
            var version = wire.HasValue && quantity.HasValue ? Version(catalogId, scopeKey, capture.FullSyncRevision, wire.Value, quantity.Value, capture.ItemRevisions.GetValueOrDefault(wire.Value)) : "";
            inventory.Set(new(catalogId, quantity, scopeKey, "packet_capture", version, quantity.HasValue, reason){VerifiedThrough=capture.VerifiedThrough});
            characterStorage[catalogId] = wire.HasValue ? capture.Quantity(InventoryContainer.CharacterStorage, wire.Value) : null;
            accountStorage[catalogId] = wire.HasValue ? capture.Quantity(InventoryContainer.AccountStorage, wire.Value) : null;
        }
        inventory.SetStorage("character:" + JsonSerializer.Serialize(new[] { scope.AccountId, scope.CharacterId }), characterStorage);
        inventory.SetStorage("account:" + JsonSerializer.Serialize(scope.AccountId), accountStorage);
        return new(inventory, scopeKey, issues.AsReadOnly());
    }
    private string Version(long catalogId, string scope, long fullRevision, uint wireId, long quantity, long itemRevision)
    {
        var text = scope + ":" + fullRevision.ToString(CultureInfo.InvariantCulture) + ":" + wireId.ToString(CultureInfo.InvariantCulture) + ":" + quantity.ToString(CultureInfo.InvariantCulture) + ":" + itemRevision.ToString(CultureInfo.InvariantCulture);
        lock (gate)
        {
            var prior = versions.GetValueOrDefault(catalogId);
            var revision = prior.Basis == text ? prior.Revision : checked(prior.Revision + 1);
            versions[catalogId] = (text, revision);
            return instance + ":" + revision.ToString(CultureInfo.InvariantCulture) + ":" + System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }
    }
}
