using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MabiAgent.Capture;

public sealed record AccountInfo(string AccountId, IReadOnlyDictionary<string, string> Characters);
public sealed record AccountCsvPaths(string Associations, string Inventory);
public sealed record ItemNameEdit(uint ItemId, string? Name);
public enum NameEditOperation { Confirm, Clear }
public sealed record CharacterItemQuantity(long? Backpack, long? CharacterStorage)
{ public long? Total => Backpack.HasValue && CharacterStorage.HasValue ? checked(Backpack.Value + CharacterStorage.Value) : null; }
public sealed record AccountItemRow(uint ItemId, string? Name, NameAssociation Association, bool Captured,
    IReadOnlyDictionary<string, CharacterItemQuantity> Characters, long? SharedStorage, long? AccountTotal, long? ObservedTotal);
public sealed record AccountItemView(string AccountId, IReadOnlyDictionary<string, string> Characters, IReadOnlyList<AccountItemRow> Items, bool Complete)
{ public bool Historical => true; public bool ExecutionEligible => false; }

/// <summary>Original account CSV format. Historical totals never become current executable stock.</summary>
public sealed partial class AccountItemService
{
    private readonly object gate = new();
    private readonly string directory;
    public void ReportFault(Exception error,string operation,string? account=null)
    {var log=new MabiAgent.Core.RuntimeDiagnostics(Path.GetDirectoryName(directory)!);log.Write(log.Create(Guid.NewGuid().ToString("N"),"error","account",operation,"account",account,null,null,null,"correct_then_retry",error.Message,error,trigger:operation));}
    private readonly string? verifiedCatalogPath;
    private static readonly string[] AssociationFields = ["item_id", "name", "association", "source", "captured", "capture_source"];
    private static readonly string[] InventoryFields = ["account_id", "character_id", "character", "container", "item_id", "name", "quantity", "observed_at", "source", "evidence", "frame_sha256"];
    public AccountItemService(string directory, string? verifiedCatalogPath = null, string? candidatePath = null)
    { this.directory = Path.GetFullPath(directory); this.verifiedCatalogPath = verifiedCatalogPath; LoadCandidates(candidatePath); }
    private string IndexPath => Path.Combine(directory, "accounts.json");
    private string Folder(string account) => Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(account)))[..24]);
    private JsonObject ReadIndex()
    {
        if (!File.Exists(IndexPath)) return new();
        if (new FileInfo(IndexPath).Length > 4 * 1024 * 1024) throw new InvalidDataException("Account index budget exceeded");
        return JsonNode.Parse(File.ReadAllText(IndexPath)) as JsonObject ?? throw new InvalidDataException("Invalid account index");
    }
    public IReadOnlyList<AccountInfo> Accounts()
    { lock (gate) return ReadIndex().Select(p => new AccountInfo(p.Key, Characters(p.Value))).ToArray(); }
    /// <summary>Identity metadata only. No historical quantity or coverage is imported.</summary>
    public IReadOnlyList<CharacterIdentity> KnownCaptureIdentities()
    {
        lock(gate)return ReadIndex().SelectMany(account=>Characters(account.Value).Select(character=>
            new CharacterIdentity(account.Key,character.Key,character.Value))).Where(who=>
                long.TryParse(who.CharacterId,NumberStyles.None,CultureInfo.InvariantCulture,out var code)&&code>0&&
                who.AccountId==(code/0x100000).ToString(CultureInfo.InvariantCulture)&&!string.IsNullOrWhiteSpace(who.Label)&&who.Label.Length<=128&&!who.Label.Any(char.IsControl)).Take(256).ToArray();
    }
    public AccountCsvPaths Files(string accountId)
    { lock (gate) { RequireAccount(accountId); return new(Path.Combine(Folder(accountId), "associations.csv"), Path.Combine(Folder(accountId), "inventory.csv")); } }
    private static Dictionary<string, string> Characters(JsonNode? metadata) =>
        (metadata?["characters"] as JsonObject ?? new()).ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? p.Key, StringComparer.Ordinal);
    private ItemNameRegistry Registry(string account)
    {
        var path = Path.Combine(Folder(account), "associations.csv");
        ItemNameRegistry registry;
        if (File.Exists(path)) { using var input = File.OpenText(path); registry = ItemNameRegistry.ReadCsv(input); }
        else registry = new();
        if (verifiedCatalogPath is not null && File.Exists(verifiedCatalogPath))
        { using var json = JsonDocument.Parse(File.ReadAllText(verifiedCatalogPath)); registry.LoadVerifiedCatalog(json.RootElement); }
        foreach(var row in registry.Snapshot()) if(row.Association is NameAssociation.Unknown or NameAssociation.Candidate && candidates.TryGetValue(row.ItemId,out var candidate))registry.Set(row with{Name=candidate.Name,Association=NameAssociation.Candidate,Source=candidate.Source});
        return registry;
    }
    public uint? ResolveVerified(string accountId, string name)
    { lock (gate) { RequireAccount(accountId); return Registry(accountId).ResolveVerified(name); } }
    /// <summary>Records a direct identity association supplied by a packet/name evidence source.</summary>
    public AccountItemView RecordCaptureAssociation(string accountId, uint itemId, string name, string source, string evidence)
    {
        if (itemId == 0 || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(evidence))
            throw new ArgumentException("抓包身份关联缺少必要字段");
        _ = ItemNameRegistry.Normalize(name);
        lock (gate)
        {
            RequireAccount(accountId);
            var path = Path.Combine(Folder(accountId), "associations.csv");
            var expected = Fingerprint(path);
            var registry = Registry(accountId);
            var rows = registry.Snapshot().ToDictionary(r => r.ItemId);
            if (!rows.TryGetValue(itemId, out var current) || !current.Captured)
                throw new ArgumentException("抓包中不存在该道具类型");
            var normalized = ItemNameRegistry.Normalize(name);
            if (current.Association == NameAssociation.UserCleared)
                throw new ArgumentException("该道具关联已由用户清除，请通过手动关联恢复");
            if (current.Association is NameAssociation.UserConfirmed or NameAssociation.BuiltinVerified &&
                current.Name is not null && ItemNameRegistry.Normalize(current.Name) != normalized)
                throw new ArgumentException("抓包身份与已确认关联冲突，请先核实");
            if (rows.Values.Any(r => r.ItemId != itemId && r.Name is not null && ItemNameRegistry.Normalize(r.Name) == normalized && r.Association is not NameAssociation.Unknown and not NameAssociation.Candidate))
                throw new ArgumentException("道具名称存在已确认冲突");
            rows[itemId] = current with { Name = name.Trim(), Association = NameAssociation.UserConfirmed, Source = source.Trim() + ":" + evidence.Trim() };
            var csv = ReadCsv(path); MergeAssociations(csv, rows.Values);
            SaveCsv(path, AssociationFields, csv, expected);
            Interlocked.Increment(ref version);
            return View(accountId);
        }
    }
    public ItemNameRegistry ReadNames(string accountId)
    { lock (gate) return Registry(accountId); }
    private void RequireAccount(string account)
    { if (string.IsNullOrWhiteSpace(account) || !ReadIndex().ContainsKey(account)) throw new ArgumentException("请选择已记录的游戏账户"); }
    public void Record(InventorySnapshot snapshot)
    {
        if (snapshot.Scope is not { } scope || snapshot.Identity is not { } identity || snapshot.VerifiedThrough is null || snapshot.Error is not null ||
            !double.IsFinite(snapshot.VerifiedThrough.Value) || string.IsNullOrWhiteSpace(scope.AccountId) || string.IsNullOrWhiteSpace(scope.CharacterId) ||
            scope.AccountId != identity.AccountId || scope.CharacterId != identity.CharacterId || string.IsNullOrWhiteSpace(scope.SessionId) ||
            snapshot.Counts.Count != 3 || Enum.GetValues<InventoryContainer>().Any(c => !snapshot.Counts.ContainsKey(c)))
            throw new ArgumentException("仅保存身份完整的当前库存");
        if (snapshot.Counts.Values.Sum(c => c.Count) > 30000 || snapshot.Counts.Values.Any(c => c.Any(p => p.Key == 0 || p.Value < 0)))
            throw new ArgumentException("Invalid inventory counts");
        lock (gate)
        {
            SetCurrentCapture(snapshot);
            var indexHash = Fingerprint(IndexPath); var index = ReadIndex();
            var metadata = index[scope.AccountId] as JsonObject;
            if (metadata is null) { metadata = new JsonObject { ["account_id"] = scope.AccountId, ["characters"] = new JsonObject() }; index[scope.AccountId] = metadata; }
            if (metadata["characters"] is not JsonObject) metadata["characters"] = new JsonObject();
            metadata["characters"]![scope.CharacterId] = identity.Label;
            var inventoryPath = Path.Combine(Folder(scope.AccountId), "inventory.csv");
            var associationPath = Path.Combine(Folder(scope.AccountId), "associations.csv");
            var inventoryHash = Fingerprint(inventoryPath); var associationHash = Fingerprint(associationPath);
            var inventory = ReadCsv(inventoryPath); var associations = ReadCsv(associationPath); var registry = Registry(scope.AccountId);
            var names = registry.Snapshot().ToDictionary(r => r.ItemId);
            foreach (var id in snapshot.Counts.Values.SelectMany(c => c.Keys).Distinct())
            { var row = names.GetValueOrDefault(id); names[id] = row is null ? candidates.TryGetValue(id,out var candidate)?new(id,candidate.Name,NameAssociation.Candidate,candidate.Source,true):new(id, null, NameAssociation.Unknown, "capture:" + scope.SessionId, true) : row with { Captured = true }; }
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            foreach (var pair in snapshot.Counts)
            {
                var container = ContainerName(pair.Key); var owner = pair.Key == InventoryContainer.AccountStorage ? "" : scope.CharacterId;
                inventory.RemoveAll(r => Field(r, "container") == container && Field(r, "character_id") == owner);
                Dictionary<string, string> Row(string item, string quantity, string source) => new(StringComparer.Ordinal)
                {
                    ["account_id"] = scope.AccountId, ["character_id"] = owner, ["character"] = owner.Length == 0 ? "账号共享" : identity.Label,
                    ["container"] = container, ["item_id"] = item, ["quantity"] = quantity, ["observed_at"] = now, ["source"] = source,
                    ["evidence"] = scope.SessionId, ["frame_sha256"] = ""
                };
                foreach (var count in pair.Value)
                { var row = Row(count.Key.ToString(CultureInfo.InvariantCulture), count.Value.ToString(CultureInfo.InvariantCulture), snapshot.Source); row["name"] = names[count.Key].Name ?? ""; inventory.Add(row); }
                inventory.Add(Row("", "", "full_coverage"));
            }
            MergeAssociations(associations, names.Values);
            SaveCsv(associationPath, AssociationFields, associations, associationHash);
            SaveCsv(inventoryPath, InventoryFields, inventory, inventoryHash);
            AtomicWrite(IndexPath, index.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), indexHash);Interlocked.Increment(ref version);
        }
    }
    public AccountItemView View(string accountId)
    {
        lock (gate)
        {
            RequireAccount(accountId); var characters = Characters(ReadIndex()[accountId]);
            var source = ReadCsv(Path.Combine(Folder(accountId), "inventory.csv"));
            var counts = new Dictionary<(string Owner, string Container, uint Item), long>(); var coverage = new HashSet<(string, string)>();
            foreach (var row in source)
            {
                if (Field(row, "account_id") != accountId) throw new InvalidDataException("Inventory account mismatch");
                var container = Field(row, "container");
                if (container is not ("backpack" or "character_storage" or "account_storage")) throw new InvalidDataException("Unknown inventory container");
                var owner = container == "account_storage" ? "" : Field(row, "character_id");
                if (owner.Length > 0 && !characters.ContainsKey(owner)) throw new InvalidDataException("Inventory character missing from index");
                var id = Field(row, "item_id");
                if (id.Length == 0) { if (Field(row, "source") == "full_coverage") coverage.Add((owner, container)); continue; }
                if (!uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var item) || item == 0 ||
                    !long.TryParse(Field(row, "quantity"), NumberStyles.None, CultureInfo.InvariantCulture, out var quantity)) throw new InvalidDataException("Invalid inventory CSV quantity");
                counts[(owner, container, item)] = quantity;
            }
            var names = Registry(accountId).Snapshot().ToDictionary(r => r.ItemId);
            var complete = characters.Count > 0 && coverage.Contains(("", "account_storage")) && characters.Keys.All(c => coverage.Contains((c, "backpack")) && coverage.Contains((c, "character_storage")));
            var items = names.Keys.Concat(counts.Keys.Select(k => k.Item)).Distinct().Order().Select(item =>
            {
                long? Quantity(string owner, string container) => coverage.Contains((owner, container)) ? counts.GetValueOrDefault((owner, container, item)) : null;
                var columns = characters.Keys.ToDictionary(c => c, c => new CharacterItemQuantity(Quantity(c, "backpack"), Quantity(c, "character_storage")));
                var observed = counts.Where(p => p.Key.Item == item).Select(p => p.Value).ToArray();
                long? sum = observed.Length > 0 ? observed.Aggregate(0L, (a, b) => checked(a + b)) : null;
                var name = names.GetValueOrDefault(item);
                return new AccountItemRow(item, name?.Name, name?.Association ?? NameAssociation.Unknown, name?.Captured ?? true, columns, Quantity("", "account_storage"), complete ? sum ?? 0 : null, sum);
            }).ToArray();
            return new(accountId, characters, items, complete);
        }
    }
    public AccountItemView UpdateNames(string accountId, IReadOnlyList<ItemNameEdit> edits, NameEditOperation operation,string? expectedAssociationVersion=null)
    {
        if (edits.Count is < 1 or > 2000 || !Enum.IsDefined(operation) || edits.Select(e => e.ItemId).Distinct().Count() != edits.Count) throw new ArgumentException("无效的批量关联");
        lock (gate)
        {
            RequireAccount(accountId); var path = Path.Combine(Folder(accountId), "associations.csv"); var hash = Fingerprint(path);
            if(expectedAssociationVersion is not null&&hash!=expectedAssociationVersion)throw new IOException("名称关联已更新，整批未保存");
            var old = Registry(accountId).Snapshot().ToDictionary(r => r.ItemId); var staged = old.ToDictionary();
            foreach (var edit in edits)
            {
                if (!staged.TryGetValue(edit.ItemId, out var row)) throw new ArgumentException("道具不存在");
                var name = operation == NameEditOperation.Clear ? null : edit.Name?.Trim();
                if (name is not null) _ = ItemNameRegistry.Normalize(name);
                staged[edit.ItemId] = row with { Name = name, Association = name is null ? NameAssociation.UserCleared : NameAssociation.UserConfirmed, Source = "player_edit" };
            }
            static Dictionary<string, HashSet<uint>> Groups(IEnumerable<ItemName> rows) => rows.Where(r => r.Name is not null).GroupBy(r => ItemNameRegistry.Normalize(r.Name!)).ToDictionary(g => g.Key, g => g.Select(r => r.ItemId).ToHashSet());
            var oldGroups = Groups(old.Values);
            if (Groups(staged.Values).Any(p => p.Value.Count > 1 && (!oldGroups.TryGetValue(p.Key, out var prior) || !prior.SetEquals(p.Value)))) throw new ArgumentException("存在新增同名道具冲突，整批未保存");
            var rows = ReadCsv(path); MergeAssociations(rows, staged.Values);
            var inventoryPath = Path.Combine(Folder(accountId), "inventory.csv"); var inventoryHash = Fingerprint(inventoryPath); var inventory = ReadCsv(inventoryPath);
            foreach (var row in inventory)
                if (uint.TryParse(Field(row, "item_id"), out var id)) row["name"] = staged.GetValueOrDefault(id)?.Name ?? "";
            SaveCsv(path, AssociationFields, rows, hash);
            if (inventoryHash is not null) SaveCsv(inventoryPath, InventoryFields, inventory, inventoryHash);
            Interlocked.Increment(ref version);return View(accountId);
        }
    }
    public void ExportCsv(string accountId, TextWriter output)
    {
        var view = View(accountId); var characters = view.Characters.ToArray();
        WriteRow(output, new[] { "item_id", "name" }.Concat(characters.Select(c => c.Value + " [" + c.Key + "]")).Concat(["account_storage", "account_total", "observed_total", "coverage"]));
        foreach (var row in view.Items) WriteRow(output, new[] { row.ItemId.ToString(CultureInfo.InvariantCulture), row.Name ?? "" }.Concat(characters.Select(c => Number(row.Characters[c.Key].Total))).Concat([Number(row.SharedStorage), Number(row.AccountTotal), Number(row.ObservedTotal), view.Complete ? "recorded_characters_complete" : "partial"]));
    }
    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string ContainerName(InventoryContainer value) => value switch { InventoryContainer.Backpack => "backpack", InventoryContainer.CharacterStorage => "character_storage", InventoryContainer.AccountStorage => "account_storage", _ => throw new ArgumentOutOfRangeException(nameof(value)) };
    private static string Field(Dictionary<string, string> row, string key) => row.GetValueOrDefault(key, "");
    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        if (!File.Exists(path)) return [];
        using var input = File.OpenText(path); using var rows = ItemNameRegistry.CsvRows(input).GetEnumerator();
        if (!rows.MoveNext()) return [];
        var header = rows.Current.Select(s => s.TrimStart('\uFEFF')).ToArray();
        if (header.Distinct(StringComparer.Ordinal).Count() != header.Length) throw new InvalidDataException("Duplicate CSV headers");
        var result = new List<Dictionary<string, string>>();
        while (rows.MoveNext())
        {
            if (result.Count >= 100000) throw new InvalidDataException("CSV row budget exceeded");
            var row = rows.Current; if (row.Count == 1 && row[0].Length == 0) continue;
            if (row.Count > header.Length) throw new InvalidDataException("CSV columns exceeded");
            result.Add(header.Select((h, i) => (h, v: i < row.Count ? row[i] : "")).ToDictionary(p => p.h, p => p.v, StringComparer.Ordinal));
        }
        return result;
    }
    private static void MergeAssociations(List<Dictionary<string, string>> rows, IEnumerable<ItemName> names)
    {
        foreach (var name in names)
        {
            var id = name.ItemId.ToString(CultureInfo.InvariantCulture); var row = rows.FirstOrDefault(r => Field(r, "item_id") == id);
            if (row is null) { row = new(StringComparer.Ordinal) { ["item_id"] = id }; rows.Add(row); }
            row["name"] = name.Name ?? ""; row["association"] = name.Association switch { NameAssociation.UserConfirmed => "user_confirmed", NameAssociation.BuiltinVerified => "builtin_verified", NameAssociation.Candidate => "preset_candidate", NameAssociation.UserCleared => "user_cleared", _ => "" };
            row["source"] = name.Source; row["captured"] = name.Captured ? "true" : "false";
        }
    }
    private static string? Fingerprint(string path)
    {
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        if (input.Length > 16 * 1024 * 1024) throw new InvalidDataException("Account file budget exceeded");
        return Convert.ToHexString(SHA256.HashData(input));
    }
    private static void SaveCsv(string path, string[] fields, List<Dictionary<string, string>> rows, string? expected)
    {
        var headers = fields.Concat(rows.SelectMany(r => r.Keys)).Distinct(StringComparer.Ordinal).ToArray();
        using var output = new StringWriter(CultureInfo.InvariantCulture); WriteRow(output, headers);
        foreach (var row in rows) WriteRow(output, headers.Select(h => Field(row, h)));
        AtomicWrite(path, output.ToString(), expected);
    }
    private static void WriteRow(TextWriter output, IEnumerable<string> values) => output.WriteLine(string.Join(',', values.Select(v => v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v)));
    private static void AtomicWrite(string path, string content, string? expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { using (var writer = new StreamWriter(file, new UTF8Encoding(true), 4096, true)) { writer.Write(content); writer.Flush(); } file.Flush(true); }
            if (Fingerprint(path) != expected) throw new IOException("账户文件已由其他程序修改，请刷新后重试");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
