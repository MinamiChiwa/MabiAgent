using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace MabiAgent.Capture;

public enum InventoryContainer { Backpack, CharacterStorage, AccountStorage }
public sealed record LoginInventory(long CharacterCode, string BodySha256, IReadOnlyDictionary<InventoryContainer, IReadOnlyList<ConsumableStack>> Containers, long? Gold = null);
public sealed record CharacterIdentity(string AccountId, string CharacterId, string Label);
public sealed record InventoryScope(string AccountId, string CharacterId, string SessionId);
public sealed record InventorySnapshot(InventoryScope? Scope, CharacterIdentity? Identity,
    IReadOnlyDictionary<InventoryContainer, IReadOnlyDictionary<uint, long>> Counts, long Revision, long FullSyncRevision, double? FullSyncAt, double? VerifiedThrough, string? Error)
{
    public long? Gold { get; init; }
    public double? GoldAt { get; init; }
    public IReadOnlyDictionary<uint,long> ItemRevisions { get; init; } = new ReadOnlyDictionary<uint,long>(new Dictionary<uint,long>());
    public string? FrameSha256 { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public DateTimeOffset? FullSyncObservedAt { get; init; }
    public bool ExecutionEligible => false;
    public bool PlanningOnly => true;
    public string Source => "packet_capture";
    public long? Quantity(InventoryContainer container, uint item) => VerifiedThrough.HasValue && Counts.TryGetValue(container, out var counts) ? counts.GetValueOrDefault(item) : null;
}
public static class LoginIdentity
{
    public const string AccountCodec = "tw-1675c213-account-slots-v1";
    public const string ClientAssemblySha256 = "1675c2131534d0ab2fc053af8a2e394d326ba7d22eff0019734be96b1808678f";
    public static CharacterIdentity Join(long character, ReadOnlySpan<byte> profile, string codec)
    {
        if (codec != AccountCodec || character <= 0 || profile.Length < 28) throw new InvalidDataException("Unsupported identity codec or prefix");
        if (BinaryPrimitives.ReadInt64LittleEndian(profile[8..]) != character || BinaryPrimitives.ReadInt64LittleEndian(profile[16..]) != character)
            throw new InvalidDataException("Profile character copies differ from login");
        var account = character / 0x100000;
        if (BinaryPrimitives.ReadInt64LittleEndian(profile) != account) throw new InvalidDataException("Profile account mismatch");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(profile[24..]);
        if (length == 0 || length > 256 || length % 2 != 0 || length > profile.Length - 28) throw new InvalidDataException("Invalid profile name length");
        string name;
        try { name = ConsumableCodec.StrictUtf16.GetString(profile.Slice(28, (int)length)); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid profile name UTF16", error); }
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) throw new InvalidDataException("Invalid profile name");
        return new(account.ToString(CultureInfo.InvariantCulture), character.ToString(CultureInfo.InvariantCulture), name);
    }
}

/// <summary>Current planning observation. No packet or inventory observation grants permission to issue input.</summary>
public sealed class StreamingInventory(Func<byte[], LoginInventory> decodeLogin, string captureId, IEnumerable<CharacterIdentity>? knownIdentities=null, CaptureProtocol? protocol=null, Func<byte[],IReadOnlyList<ConsumableStack>>? decodeUpdates=null)
{
    public CaptureProtocol Protocol { get; } = protocol ?? CaptureProtocol.Historical;
    private readonly object gate = new();
    private readonly Dictionary<long, CharacterIdentity> known = IdentityIndex(knownIdentities??[]);
    internal IReadOnlyList<CharacterIdentity> KnownIdentities { get { lock(gate)return known.Values.ToArray(); } }
    private static Dictionary<long,CharacterIdentity> IdentityIndex(IEnumerable<CharacterIdentity> identities)
    {
        var result=new Dictionary<long,CharacterIdentity>();
        foreach(var who in identities)
        {
            if(result.Count>=256)throw new ArgumentException("Known identity budget exceeded");
            if(!long.TryParse(who.CharacterId,NumberStyles.None,CultureInfo.InvariantCulture,out var character)||character<=0||
                who.AccountId!=(character/0x100000).ToString(CultureInfo.InvariantCulture)||string.IsNullOrWhiteSpace(who.Label)||who.Label.Length>128||who.Label.Any(char.IsControl))
                throw new ArgumentException("Invalid known capture identity");
            if(result.TryGetValue(character,out var prior)&&prior!=who)throw new ArgumentException("Conflicting known capture identity");
            result[character]=who;
        }
        return result;
    }
    private Dictionary<InventoryContainer, Dictionary<string, ConsumableStack>> records = [];
    private LoginInventory? pending;
    private string? connection;
    private double pendingAt;
    private InventoryScope? scope;
    private CharacterIdentity? identity;
    private long revision, fullRevision;
    private readonly Dictionary<uint,long> itemRevisions = [];
    private long itemRevision;
    private long? gold;
    private double? fullAt, verifiedThrough;
    private string? frameSha256;
    private DateTimeOffset observedAt, fullSyncObservedAt;
    private string? error;
    internal string? ActiveConnectionId { get { lock (gate) return connection; } }
    public bool ValidateFullSync(GameFrame frame)
    {
        if (frame.Opcode != Protocol.Login) return false;
        try { ValidateLogin(decodeLogin(GameProtocol.DecodeBody(frame.Flag, frame.Body))); return true; }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) { return false; }
    }
    private static void ValidateLogin(LoginInventory login)
    {
        if (login.CharacterCode <= 0 || login.Containers.Count != 3 || Enum.GetValues<InventoryContainer>().Any(c => !login.Containers.ContainsKey(c)))
            throw new InvalidDataException("Full known consumable containers required");
        if (login.Containers.Values.Sum(x => x.Count) > 10000) throw new InvalidDataException("Inventory record budget exceeded");
        foreach (var rows in login.Containers.Values)
            if (rows.Any(x => x.Quantity < 0 || x.ItemId == 0) || rows.Select(x => x.StackKeyHex).Distinct(StringComparer.Ordinal).Count() != rows.Count)
                throw new InvalidDataException("Invalid or duplicate consumable record");
        foreach (var rows in login.Containers.Values) ValidateTotals(rows);
    }
    public void Accept(GameFrame frame, string connectionId, double at)
    {
        if (!double.IsFinite(at) || string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Valid connection and receive time required");
        lock (gate)
        {
            try
            {
                if (frame.Opcode == Protocol.Login)
                {
                    InvalidateCore("Waiting for matching login profile"); pending = null; connection = connectionId;
                    var login = decodeLogin(GameProtocol.DecodeBody(frame.Flag, frame.Body)); ValidateLogin(login);
                    pending = login; pendingAt = at;
                    if (known.TryGetValue(login.CharacterCode, out var who)) Bind(who);
                    return;
                }
                if (connection != connectionId) return;
                if (frame.Opcode == Protocol.Profile && pending is not null)
                {
                    var body = GameProtocol.DecodeBody(frame.Flag, frame.Body);
                    // A profile for another visible character is not our identity join.
                    if (body.Length >= 24 && BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(8)) != pending.CharacterCode) return;
                    var who = LoginIdentity.Join(pending.CharacterCode, body, LoginIdentity.AccountCodec);
                    if (known.Count >= 256 && !known.ContainsKey(pending.CharacterCode)) known.Clear();
                    known[pending.CharacterCode] = who; Bind(who); return;
                }
                if (frame.Opcode != Protocol.Inventory) return;
                if (scope is null || pending is not null || error is not null)
                { pending = null; InvalidateCore("Inventory changed before identity or full sync resolved"); return; }
                var updateBody = GameProtocol.DecodeBody(frame.Flag, frame.Body);
                var updates = decodeUpdates is null ? ConsumableCodec.ParseUpdates(updateBody) : decodeUpdates(updateBody);
                var backpack = new Dictionary<string, ConsumableStack>(records[InventoryContainer.Backpack], StringComparer.Ordinal);
                var touched = new HashSet<uint>();
                foreach (var row in updates)
                {
                    // 完整同步中跨容器可能复用StackableId。只有增量明确延续背包
                    // 已有取得时间且仓库取得时间不同，才可排除仓库归属。
                    var conflicts=new[]{InventoryContainer.AccountStorage,InventoryContainer.CharacterStorage}
                        .Select(container=>records[container].GetValueOrDefault(row.StackKeyHex))
                        .Where(stack=>stack is not null&&stack.StackableId==row.StackableId).ToArray();
                    var knownBackpack=backpack.GetValueOrDefault(row.StackKeyHex);
                    var backpackContinuation=knownBackpack is not null&&knownBackpack.StackableId==row.StackableId&&row.AcquiredTimeRaw>0&&
                        knownBackpack.AcquiredTimeRaw==row.AcquiredTimeRaw&&conflicts.All(stack=>stack!.AcquiredTimeRaw>0&&stack.AcquiredTimeRaw!=row.AcquiredTimeRaw);
                    if(conflicts.Length>0&&!backpackContinuation)
                        throw new InvalidDataException($"Inventory update container ambiguous; item={row.ItemId}; update_stack={row.StackableId}; backpack_stack={records[InventoryContainer.Backpack].GetValueOrDefault(row.StackKeyHex)?.StackableId}; character_stack={records[InventoryContainer.CharacterStorage].GetValueOrDefault(row.StackKeyHex)?.StackableId}; account_stack={records[InventoryContainer.AccountStorage].GetValueOrDefault(row.StackKeyHex)?.StackableId}; full sync required");
                    var existed=backpack.TryGetValue(row.StackKeyHex,out var previous);
                    if(row.Quantity==0?existed:!existed||previous!.Quantity!=row.Quantity||previous.ItemId!=row.ItemId)
                    {touched.Add(row.ItemId);if(previous is not null)touched.Add(previous.ItemId);}
                    if (row.Quantity == 0) backpack.Remove(row.StackKeyHex); else backpack[row.StackKeyHex] = row;
                }
                ValidateTotals(backpack.Values);
                if (backpack.Count > 10000) throw new InvalidDataException("Backpack record limit exceeded");
                if (!backpack.OrderBy(p => p.Key).SequenceEqual(records[InventoryContainer.Backpack].OrderBy(p => p.Key)))
                { records[InventoryContainer.Backpack] = backpack; revision++; }
                foreach(var item in touched)itemRevisions[item]=checked(++itemRevision);
                if(itemRevisions.Count>20000)throw new InvalidDataException("Inventory item revision budget exceeded; full sync required");
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
            { pending = null; InvalidateCore(ex.Message); throw; }
        }
    }
    private static void ValidateTotals(IEnumerable<ConsumableStack> rows)
    {
        foreach (var group in rows.GroupBy(r => r.ItemId)) _ = group.Aggregate(0L, (sum,r) => checked(sum + r.Quantity));
    }
    private void Bind(CharacterIdentity who)
    {
        var login = pending!;
        var next = login.Containers.ToDictionary(p => p.Key, p => p.Value.ToDictionary(r => r.StackKeyHex, StringComparer.Ordinal));
        records = next; itemRevisions.Clear(); identity = who; scope = new(who.AccountId, who.CharacterId, captureId + ":" + connection);
        gold=login.Gold;fullRevision++; revision++; fullAt = pendingAt; verifiedThrough = pendingAt; frameSha256=login.BodySha256; observedAt=DateTimeOffset.UtcNow; fullSyncObservedAt=observedAt; pending = null; error = null;
    }
    public void Invalidate(string reason) { lock (gate) { pending = null; InvalidateCore(reason); } }
    private void InvalidateCore(string reason) { verifiedThrough = null; error = reason[..Math.Min(reason.Length, 400)]; }
    public void Advance(double at, bool healthy)
    {
        if (!double.IsFinite(at)) throw new ArgumentOutOfRangeException(nameof(at));
        // An incomplete trailing frame or temporary TCP reorder only stops extension
        // of the decoded range. It does not erase the full sync already received.
        // Actual loss, malformed traffic and connection closure use Invalidate.
        lock (gate)
            if (scope is not null && healthy && error is null)
            {
                if (!verifiedThrough.HasValue || at > verifiedThrough.Value) observedAt=DateTimeOffset.UtcNow;
                verifiedThrough = verifiedThrough.HasValue ? Math.Max(verifiedThrough.Value, at) : at;
            }
    }
    public InventorySnapshot Snapshot()
    {
        lock (gate)
        {
            var counts = records.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<uint,long>)new ReadOnlyDictionary<uint,long>(p.Value.Values
                .GroupBy(r => r.ItemId).ToDictionary(g => g.Key, g => g.Aggregate(0L, (sum,r) => checked(sum + r.Quantity)))));
            return new(scope, identity, new ReadOnlyDictionary<InventoryContainer,IReadOnlyDictionary<uint,long>>(counts), revision, fullRevision, fullAt, verifiedThrough, error){Gold=verifiedThrough is not null?gold:null,GoldAt=verifiedThrough is not null?fullAt:null,ItemRevisions=new ReadOnlyDictionary<uint,long>(itemRevisions.ToDictionary()),FrameSha256=verifiedThrough is not null?frameSha256:null,ObservedAt=verifiedThrough is not null?observedAt:null,FullSyncObservedAt=verifiedThrough is not null?fullSyncObservedAt:null};
        }
    }
}

public sealed record AccountTotal(long? Quantity, long? ObservedQuantity, bool Complete);
public sealed class AccountInventory
{
    private readonly object gate = new();
    private readonly Dictionary<string, HashSet<string>> characters = [];
    private readonly Dictionary<(string Account, string Owner, InventoryContainer Container), StoredContainer> containers = [];
    private sealed record StoredContainer(Dictionary<uint,long> Counts, bool Full, string Source, double At);
    public void Record(string account, string character, InventoryContainer container, IReadOnlyDictionary<uint,long> counts, bool fullCoverage, string source, double observedAt)
    {
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(character) || string.IsNullOrWhiteSpace(source) || !double.IsFinite(observedAt) || counts.Count > 10000 || counts.Any(x => x.Key == 0 || x.Value < 0))
            throw new ArgumentException("Invalid scoped inventory observation");
        lock (gate)
        {
            if (!characters.TryGetValue(account, out var owners)) characters[account] = owners = [];
            owners.Add(character);
            var key = (account, container == InventoryContainer.AccountStorage ? "" : character, container);
            if (containers.TryGetValue(key, out var old) && old.At > observedAt) return;
            // A full sync replaces the entire scope, including an explicitly empty warehouse.
            containers[key] = new(counts.ToDictionary(), fullCoverage, source, observedAt);
        }
    }
    public AccountTotal Total(string account, uint item)
    {
        lock (gate)
        {
            if (!characters.TryGetValue(account, out var owners)) return new(null, null, false);
            bool Full(string owner, InventoryContainer c) => containers.TryGetValue((account,owner,c), out var entry) && entry.Full;
            var complete = Full("", InventoryContainer.AccountStorage) && owners.All(o => Full(o,InventoryContainer.Backpack) && Full(o,InventoryContainer.CharacterStorage));
            var entries = containers.Where(p => p.Key.Account == account).Select(p => p.Value).ToArray();
            var observed = entries.Any(e => e.Counts.ContainsKey(item)) ? entries.Aggregate(0L, (sum,e) => checked(sum + e.Counts.GetValueOrDefault(item))) : (long?)null;
            return new(complete ? observed ?? 0 : null, observed, complete);
        }
    }
}

public enum NameAssociation { Unknown, Candidate, UserConfirmed, BuiltinVerified, UserCleared }
public sealed record ItemName(uint ItemId, string? Name, NameAssociation Association, string Source, bool Captured);
public sealed partial class ItemNameRegistry
{
    private readonly Dictionary<uint, ItemName> rows = [];
    private readonly object gate = new();
    public static bool IsPlaceholderName(string? name)
    {
        if(string.IsNullOrWhiteSpace(name))return false;
        var compact=string.Concat(name.Normalize(NormalizationForm.FormKC).Where(c=>!char.IsWhiteSpace(c)));
        return compact.StartsWith("道具",StringComparison.Ordinal)&&compact.Length>2&&compact.Skip(2).All(c=>c is >= '0' and <= '9');
    }
    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ArgumentException("请输入不超过80字的道具名称");
        var key = name.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        return key switch { "铁矿石" => "鐵礦石", "铁锭" => "鐵錠", _ => key };
    }
    public void Set(ItemName row)
    {
        if (row.ItemId == 0) throw new ArgumentException("Invalid item ID");
        if(row.Association is NameAssociation.Unknown or NameAssociation.Candidate && IsPlaceholderName(row.Name))row=row with{Name=null,Association=NameAssociation.Unknown};
        if (row.Name is not null) _ = Normalize(row.Name);
        lock (gate) rows[row.ItemId] = row;
    }
    public uint? ResolveVerified(string name)
    {
        var key = Normalize(name);
        lock (gate)
        {
            var matches = rows.Values.Where(r => r.Name is not null && Normalize(r.Name) == key).ToArray();
            return matches.Length == 1 && matches[0].Association is NameAssociation.UserConfirmed or NameAssociation.BuiltinVerified ? matches[0].ItemId : null;
        }
    }
    public IReadOnlyList<ItemName> Snapshot() { lock (gate) return rows.Values.ToArray(); }
}


