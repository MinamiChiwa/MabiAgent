using MabiAgent.Core;
using System.Text.Json.Nodes;
namespace MabiAgent.Workflows;
public sealed record WithdrawalPlan(long ItemId, string Name, long Quantity, string Scope, string[]? GoalIds = null);
public sealed record WithdrawalFrame(double At, string CaptureId, string Page = "scene", bool Foreground = true, bool Stable = true,
    string? Scope = null, long? ItemId = null, string? Name = null, long? Available = null, long? BagQuantity = null, string? BagScope = null,
    bool TransferAvailable = false, bool TransferDialog = false, long? DialogQuantity = null, bool ConfirmAvailable = false, bool TransferComplete = false,
    bool SelectAvailable = false, bool LocateAvailable = false, int LocateLimit = 10, double LocateIntervalSeconds = .5,
    string? StorageBalanceScope = null, string? BagBalanceScope = null, string? BalanceSource = null, double? BalanceAt = null);
public sealed record WithdrawalAction(string Kind, long ItemId, string Name, long Quantity, string Scope, int SearchIndex = 0);
public interface IStorageSupplyPort : IForegroundWorkflowPort
{
    WithdrawalFrame Observe();
    bool CanLeaveAfterTransfer=>false;
    bool ReleaseNavigation()=>CanReleaseForeground();
    /// <summary>Revalidate page/item/quantity with the shared input guard; never retry transfer input.</summary>
    void Perform(WithdrawalAction action);
}
public sealed class StorageSupplyRunner : PersistentWorkflow
{
    private const long MaxQuantity = 9007199254740991;
    private readonly WithdrawalPlan[] plans; private readonly IStorageSupplyPort port; private readonly double timeout; private string? lastCapture;
    public bool NavigationReleased=>State["reconciliation_navigation_released"]?.GetValue<bool>()==true;
    public bool SafeToSwitch => Pending is null && (S("status") is "complete" or "blocked" or "unknown" || S("status")=="paused"&&S("pause_reason")=="unrecognized_page"&&port.CanReleaseForeground()) || NavigationReleased;
    public bool ReleaseNavigation()
    {
        lock(Gate)
        {
            if(S("status")=="paused")return false;
            if(Pending is not null)
            {
                if(S("status")!="unknown"||Pending["kind"]?.GetValue<string>() is not ("confirm_transfer" or "prepare_transfer"))return false;
                if(NavigationReleased)return true;
                if(State["reconciliation_navigation_started"]?.GetValue<bool>()!=true)
                {if(!port.CanLeaveAfterTransfer)return false;State["reconciliation_navigation_started"]=true;Save();}
            }
            var released=port.ReleaseNavigation();
            if(released&&Pending is not null){State["reconciliation_navigation_released"]=true;Save();}
            return released;
        }
    }
    public bool RequestRefreshOnce()
    {lock(Gate){if(!NavigationReleased||State["reconciliation_refresh_requested"]?.GetValue<bool>()==true)return false;State["reconciliation_refresh_requested"]=true;Save();return true;}}
    public void ResetRefreshRequest(){lock(Gate){State.Remove("reconciliation_refresh_requested");Save();}}
    public bool CanIsolateFault=>Pending is null&&port.CanReleaseForeground();
    public bool RetireSatisfiedPlans(Func<long,bool> noLongerNeeded)
    {
        lock(Gate)
        {
            if(Pending is not null||!plans.Skip(I("index")).All(p=>noLongerNeeded(p.ItemId)))return false;
            Set(("index",plans.Length),("status","complete"),("reason","material_demand_satisfied"),("wait",null));Save();return true;
        }
    }
    private static bool Quantity(long? value, bool zero = false) => value >= (zero ? 0 : 1) && value <= MaxQuantity;
    private static void Validate(WithdrawalPlan p)
    { if (!Quantity(p.ItemId) || !Quantity(p.Quantity) || string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Scope) || (p.GoalIds ?? []).Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("取料计划无效"); }
    public static WithdrawalPlan[] Combine(IEnumerable<WithdrawalPlan> rows, IEnumerable<string> allowedScopes)
    {
        var allowed = allowedScopes.ToHashSet(StringComparer.Ordinal); var result = new Dictionary<(long, string), WithdrawalPlan>();
        foreach (var p in rows)
        {
            Validate(p); if (!allowed.Contains(p.Scope)) throw new ArgumentException("仓库范围缺少当前完整同步");
            if (result.TryGetValue((p.ItemId, p.Scope), out var old))
            {
                if (old.Name != p.Name) throw new ArgumentException("同物品名称冲突");
                var combined = p with { Quantity = checked(old.Quantity + p.Quantity), GoalIds = (old.GoalIds ?? []).Concat(p.GoalIds ?? []).Distinct().ToArray() }; Validate(combined); result[(p.ItemId, p.Scope)] = combined;
            }
            else result.Add((p.ItemId, p.Scope), p with { GoalIds = (p.GoalIds ?? []).Distinct().ToArray() });
        }
        return result.Values.ToArray();
    }
    public StorageSupplyRunner(IEnumerable<WithdrawalPlan> plans, IStorageSupplyPort port, IWorkflowStore store, double timeoutSeconds = 15, TimeProvider? time = null) : base(store, time, new JsonObject())
    {
        this.plans = plans.ToArray(); foreach (var p in this.plans) Validate(p);
        if (this.plans.Select(p => (p.ItemId, p.Scope)).Distinct().Count() != this.plans.Length) throw new ArgumentException("请先合并同物品同范围取料");
        ValidatePositive(timeoutSeconds, nameof(timeoutSeconds)); this.port = port; timeout = timeoutSeconds;
        var serialized = new JsonArray(this.plans.Select(p => (JsonNode?)new JsonObject { ["item_id"] = p.ItemId, ["name"] = p.Name, ["quantity"] = p.Quantity, ["scope"] = p.Scope, ["goal_ids"] = new JsonArray((p.GoalIds ?? []).Distinct().Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()) }).ToArray());
        Set(("schema_version", 1), ("plans", serialized), ("index", 0), ("status", this.plans.Length > 0 ? "running" : "complete"), ("phase", "navigate"),
            ("pending", null), ("completed", new JsonArray()), ("pause_reason", null), ("reason", null), ("wait", null), ("search", new JsonObject()));
        if (Load() is { } saved)
        {
            if (saved["schema_version"]?.GetValue<int>() != 1 || !JsonNode.DeepEquals(saved["plans"], serialized) || saved["index"]?.GetValue<int>() is not { } index || index < 0 || index > this.plans.Length || saved["completed"] is not JsonArray || saved["pending"] is not (null or JsonObject)) throw new InvalidDataException("取料记录不匹配，保留待核对");
            State = saved; if (S("status") != "complete") Set(("status", "paused"), ("pause_reason", "restart"));
        }
        Save();
    }
    public void Pause(string reason = "manual") { port.Pause(reason); lock (Gate) { if (S("status") != "complete") { if (S("pause_reason") == "manual" && reason == "focus_lost") return; Set(("status", "paused"), ("pause_reason", reason)); Save(); } } }
    public void Resume(bool explicitRequest = true)
    { lock (Gate) { if (S("status") == "complete" || !explicitRequest && S("pause_reason") != "focus_lost") return; ResetPageWait();ResetProgressWait(); Set(("status", "running"), ("pause_reason", null), ("reason", null)); if (Pending is null) {State["wait"] = null;if(explicitRequest)State["search"]=new JsonObject();} lastCapture = null; port.Resume(); Save(); } }
    private void Block(string reason) { Set(("status", "blocked"), ("reason", reason)); Save(); }
    private void BlockPage(string reason)
    {if(Pending is not null)Unknown(reason);else{Set(("status","paused"),("pause_reason",reason),("reason",reason));Save();}}
    private void Unknown(string reason) { port.PauseLocal("reconciliation_required"); Set(("status", "unknown"), ("reason", reason)); Save(); }
    private void Wait(string kind, WithdrawalFrame f, string reason)
    {
        if (State["wait"] is not JsonObject wait || wait["kind"]?.GetValue<string>() != kind) State["wait"] = new JsonObject { ["kind"] = kind, ["deadline"] = f.At + timeout };
        if (f.At >= State["wait"]!["deadline"]!.GetFiniteDouble()) Block(reason); else Save();
    }
    private string? WaitKind => State["wait"]?["kind"]?.GetValue<string>();
    private static bool Identity(WithdrawalFrame f, WithdrawalPlan p) => f.Scope == p.Scope && f.ItemId == p.ItemId && f.Name == p.Name;
    private static bool Balances(WithdrawalFrame f) => f.BalanceSource == "packet_capture" && Quantity(f.Available, true) && Quantity(f.BagQuantity, true) && !string.IsNullOrWhiteSpace(f.BagScope);
    private void Perform(string kind, WithdrawalPlan p, WithdrawalFrame f, bool pending = false, int searchIndex = 0)
    {
        if (pending) State["pending"] = new JsonObject { ["kind"] = kind, ["item_id"] = p.ItemId, ["name"] = p.Name, ["quantity"] = p.Quantity, ["scope"] = p.Scope, ["bag_scope"] = f.BagScope, ["before_storage"] = f.Available, ["before_bag"] = f.BagQuantity, ["storage_balance_scope"] = f.StorageBalanceScope ?? f.Scope, ["bag_balance_scope"] = f.BagBalanceScope ?? f.BagScope, ["balance_source"] = f.BalanceSource, ["balances_at"] = f.BalanceAt, ["capture_id"] = f.CaptureId, ["at"] = f.At, ["deadline"] = f.At + timeout };
        State["phase"] = kind; Save();
        try { port.Perform(new(kind, p.ItemId, p.Name, p.Quantity, p.Scope, searchIndex)); }
        catch (InputRejectedBeforeDispatchException)
        { Set(("pending",null),("phase","navigate"),("status","running"),("reason","target_changed_before_input"),("wait",null));lastCapture=null;Save(); }
        catch (Exception error) { RememberFault(error,kind); if(pending)Unknown("transfer_result_not_observed");else Block("navigation_interrupted"); throw; }
    }
    private void Locate(WithdrawalPlan p, WithdrawalFrame f)
    {
        if (f.LocateLimit is < 1 or > 100 || !double.IsFinite(f.LocateIntervalSeconds) || f.LocateIntervalSeconds is < .2 or > 10) throw new ArgumentException("仓库搜索边界无效");
        var searches = State["search"]!.AsObject(); var key = I("index").ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (searches[key] is not JsonObject) searches[key] = new JsonObject { ["count"] = 0, ["next_at"] = 0 };
        var search = searches[key]!; if (f.At < search["next_at"]!.GetFiniteDouble()) return;
        var count = search["count"]!.GetValue<int>(); if (count >= f.LocateLimit) { Block("storage_item_search_exhausted"); return; }
        search["count"] = count + 1; search["next_at"] = f.At + f.LocateIntervalSeconds; Perform("locate", p, f, searchIndex: count);
    }
    private string Reconcile(WithdrawalFrame f, WithdrawalPlan p)
    {
        var pending = Pending!; var at = pending["at"]!.GetFiniteDouble(); var fresh = f.CaptureId != pending["capture_id"]!.GetValue<string>() && f.At > at; var prepare = pending["kind"]!.GetValue<string>() == "prepare_transfer";
        if (fresh && prepare && Identity(f, p) && f.TransferDialog && Quantity(f.DialogQuantity))
        { Set(("pending", null), ("status", "running"), ("reason", null), ("wait", null)); Save(); return "dialog"; }
        // A port may report a stable transfer completion without exposing balances.
        // Balances remain optional reconciliation evidence, never an execution prerequisite.
        if (fresh && Identity(f, p) && f.TransferComplete)
        {
            State["completed"]!.AsArray().Add(new JsonObject { ["item_id"] = p.ItemId, ["name"] = p.Name, ["scope"] = p.Scope, ["bag_scope"] = f.BagScope, ["quantity"] = p.Quantity, ["goal_ids"] = new JsonArray((p.GoalIds ?? []).Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()), ["capture_id"] = f.CaptureId, ["at"] = f.At, ["evidence"] = "transfer_complete" });
            State["index"] = I("index") + 1; Set(("pending", null), ("phase", "navigate"), ("reason", null), ("wait", null), ("status", I("index") == plans.Length ? "complete" : "running")); Save(); return "completed";
        }
        var same = pending["balance_source"]?.GetValue<string>() == "packet_capture" && Identity(f, p) && Balances(f) && SameBagOwner(f.BagScope,pending["bag_scope"]?.GetValue<string>(),p.Scope) && (f.StorageBalanceScope ?? f.Scope) == (pending["storage_balance_scope"] ?? pending["scope"])?.GetValue<string>() && SameBagOwner(f.BagBalanceScope ?? f.BagScope,(pending["bag_balance_scope"] ?? pending["bag_scope"])?.GetValue<string>(),p.Scope);
        // A newer screen does not make an unchanged packet snapshot fresh.
        // Compare packet horizons with each other; screen and packet clocks differ.
        var freshBalances = f.BalanceAt is {} balanceAt && double.IsFinite(balanceAt) && pending["balances_at"] is {} beforeAt && balanceAt > beforeAt.GetFiniteDouble();
        if (fresh && same && freshBalances)
        {
            if (pending["before_storage"] is null || pending["before_bag"] is null)
            {
                if (f.At >= pending["deadline"]!.GetFiniteDouble()) Unknown("transfer_result_not_observed");
                return "waiting";
            }
            var bagDelta = f.BagQuantity - pending["before_bag"]!.GetValue<long>(); var storageDelta = pending["before_storage"]!.GetValue<long>() - f.Available;
            if (bagDelta == p.Quantity && storageDelta == p.Quantity)
            {
                State["completed"]!.AsArray().Add(new JsonObject { ["item_id"] = p.ItemId, ["name"] = p.Name, ["scope"] = p.Scope, ["bag_scope"] = f.BagScope, ["quantity"] = p.Quantity, ["goal_ids"] = new JsonArray((p.GoalIds ?? []).Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()), ["capture_id"] = f.CaptureId, ["at"] = f.At, ["balance_source"] = f.BalanceSource, ["storage_balance_scope"] = f.StorageBalanceScope ?? f.Scope, ["bag_balance_scope"] = f.BagBalanceScope ?? f.BagScope });
                State["index"] = I("index") + 1; Set(("pending", null), ("phase", "navigate"), ("reason", null), ("wait", null), ("status", I("index") == plans.Length ? "complete" : "running")); Save(); return "completed";
            }
            if (prepare && bagDelta == 0 && storageDelta == 0&&(f.TransferDialog||f.BagScope!=pending["bag_scope"]?.GetValue<string>()&&f.Page is "scene" or "storage_character" or "storage_account"))
            { Set(("pending", null), ("phase",f.TransferDialog?"prepare_transfer":"navigate"), ("status", "running"), ("reason", null)); Save(); return f.TransferDialog?"dialog":"waiting"; }
        }
        if (f.At >= pending["deadline"]!.GetFiniteDouble()) Unknown("transfer_result_not_observed"); return "waiting";
    }
    private static bool SameBagOwner(string? current,string? prior,string storageScope)
    {
        if(current==prior)return true;
        try
        {
            var a=JsonNode.Parse(current??"") as JsonArray;var b=JsonNode.Parse(prior??"") as JsonArray;
            return a is{Count:3}&&b is{Count:3}&&a[0]?.GetValue<string>() is{} account&&a[1]?.GetValue<string>() is{} character&&
                !string.IsNullOrWhiteSpace(account)&&!string.IsNullOrWhiteSpace(character)&&account==b[0]?.GetValue<string>()&&character==b[1]?.GetValue<string>()&&
                (storageScope=="character:"+System.Text.Json.JsonSerializer.Serialize(new[]{account,character})||storageScope=="account:"+System.Text.Json.JsonSerializer.Serialize(account));
        }catch(System.Text.Json.JsonException){return false;}catch(InvalidOperationException){return false;}
    }
    public JsonObject Tick()
    { lock (Gate) { Step(); return Snapshot(); } }
    public void ObserveOnly()
    {
        lock (Gate)
        {
            if (Pending is null || I("index") >= plans.Length) return;
            var frame = port.Observe();
            if (!double.IsFinite(frame.At) || string.IsNullOrWhiteSpace(frame.CaptureId) || !Ready(frame.Page, frame.Foreground, frame.Stable)) return;
            var paused=S("status")=="paused";var reason=S("pause_reason");
            Reconcile(frame, plans[I("index")]);
            if(paused&&S("status") is not ("complete" or "unknown")){Set(("status","paused"),("pause_reason",reason));Save();}
        }
    }
    private void Step()
    {
        if (S("status") == "complete") return; var f = port.Observe();
        if (!double.IsFinite(f.At) || string.IsNullOrWhiteSpace(f.CaptureId)) throw new ArgumentException("需要新截图与时间");
        if (!f.Foreground) { if (S("status") != "paused" || S("pause_reason") == "focus_lost") Pause("focus_lost"); return; }
        if (!PageReady(f.Page, f.Foreground, f.Stable, BlockPage)) { Save(); return; }
        if (S("status") == "paused") return;
        if (f.CaptureId == lastCapture) return; lastCapture = f.CaptureId;
        var p = plans[I("index")]; if (Pending is not null) { if (Reconcile(f, p) != "dialog") return; } else if (S("status") is "unknown" or "blocked") return;
        if (f.Scope != p.Scope) { CheckProgress("navigate:"+f.Page);Perform("navigate", p, f); return; }
        ResetProgressWait();
        if (!f.TransferAvailable && !f.TransferDialog && !f.SelectAvailable && WaitKind == "selection") { Wait("selection", f, "storage_selection_not_observed"); return; }
        if (f.LocateAvailable && !f.SelectAvailable && !f.TransferDialog && !f.TransferAvailable) { Locate(p, f); return; }
        if (!Identity(f, p)) { if (f.SelectAvailable) Perform("select_item", p, f); else Wait("item", f, "storage_item_not_observed"); return; }
        if (f.SelectAvailable && !f.TransferDialog && !f.TransferAvailable)
        {
            if (WaitKind != "selection") { State["wait"] = new JsonObject { ["kind"] = "selection", ["deadline"] = f.At + timeout }; Perform("select_item", p, f); }
            Wait("selection", f, "storage_selection_not_observed"); return;
        }
        if (f.TransferDialog)
        {
            if (f.DialogQuantity is null) { Wait(WaitKind == "quantity_set" ? "quantity_set" : "quantity", f, "transfer_quantity_unknown"); return; }
            if (!Quantity(f.DialogQuantity) || f.DialogQuantity != p.Quantity)
            {
                if (WaitKind != "quantity_set") { State["wait"] = new JsonObject { ["kind"] = "quantity_set", ["deadline"] = f.At + timeout }; Perform("set_quantity", p, f); }
                else Wait("quantity_set", f, "transfer_quantity_not_observed");
            }
            else if (f.ConfirmAvailable) { State["wait"] = null; Perform("confirm_transfer", p, f, true); }
            else Wait("confirm", f, "transfer_confirm_unavailable");
        }
        else if (f.TransferAvailable) { State["wait"] = null; Perform("prepare_transfer", p, f, true); }
        else Wait("transfer", f, "transfer_target_unavailable");
    }
}

