using MabiAgent.Core;
using System.Globalization;
using System.Text.Json.Nodes;
using MabiAgent.Workflows;

namespace MabiAgent.Windows;

/// <summary>
/// Sells surplus materials using packet inventory as the only quantity source.
/// The screen path selects the item and submits the sale; the result is closed
/// only after a newer packet observation confirms the expected balance.
/// </summary>
public sealed class ScreenSurplusPort : ScreenWorkflowPort, ISurplusMaintenancePort
{
    private readonly Func<long, long?> reserve;
    private readonly Func<InventorySnapshot> inventory;
    private readonly Func<long,string?>? itemName;
    private readonly ScreenMaintenancePort navigation;
    private readonly JsonNode? config;
    private JsonObject state;
    private bool identity;
    private readonly ScreenBagItemSearch search;
    private double quantityEditedAt;
    private int quantityEditAttempts;


    private long Item => state["item_id"]?.GetValue<long>() ?? 0;
    private string Name => S(state, "name") ?? "";
    private long Amount => state["amount"]?.GetValue<long>() ?? 0;

    public ScreenSurplusPort(ScreenWorkflowSession session, string batchId, IWorkflowStore store,
        Func<long, long?> requiredReserve, Func<InventorySnapshot>? packetInventory = null,Func<long,string?>? itemName=null) : base(session, batchId, store)
    {
        search=new(session);
        reserve = requiredReserve;
        inventory = packetInventory ?? (() => new InventorySnapshot(Session.Scope.SessionId));
        this.itemName=itemName;
        navigation=session.CreateMaintenancePort(batchId+":selection",store);
        config = session.Routes.Configuration["maintenance"]?["surplus"];
        state = store.Read() ?? new JsonObject { ["status"] = "idle", ["phase"] = "plan", ["attempted"] = new JsonArray(), ["receipts"] = new JsonArray() };
        if (state["pending"] is not null) state["status"] = "unknown";
        else if(S(state,"status")=="running") {state["deadline"]=Now+300;state["selection_deadline"]=Now+45;Save();}
    }

    private void Save() => Store.Flush(state);
    private static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;

    public void ObserveOnly()
    {
        if (state["pending"] is not JsonObject pending) return;
        var snapshot = inventory();var observation = snapshot.Query(Item);
        if (MatchesPending(observation, pending, snapshot.Scope)) CompleteSale(observation);
    }

    private bool MatchesPending(StockObservation observation, JsonObject pending, string inventoryScope)
    {
        var before = pending["before"]?.GetValue<long>();
        var amount = pending["amount"]?.GetValue<long>();
        var version = pending["before_version"]?.GetValue<string>();
        return observation.ItemId == Item && observation.Scope == inventoryScope
            && (pending["inventory_scope"] is null || pending["inventory_scope"]!.GetValue<string>() == inventoryScope) && observation.Source == "packet_capture"
            && observation.Confirmed && observation.Quantity is not null && version is not null && observation.Version != version
            && before is not null && amount is not null && observation.Quantity == before - amount;
    }

    private void CompleteSale(StockObservation observation)
    {
        state["receipts"]!.AsArray().Add(new JsonObject { ["item_id"] = Item, ["amount"] = Amount, ["after"] = observation.Quantity, ["capture"] = observation.Version, ["minimum_keep"] = state["pending"]?["minimum_keep"]?.DeepClone() });
        while (state["receipts"]!.AsArray().Count > 100) state["receipts"]!.AsArray().RemoveAt(0);
        state.Remove("pending"); state["phase"] = "plan"; state["status"] = "running"; state["reason"] = null; Save();
    }

    public JsonObject Snapshot() => (JsonObject)state.DeepClone();
    public string Begin()
    {
        if (state["pending"] is not null) return "unknown";
        navigation.ResetNavigation();search.Reset();quantityEditedAt=0;quantityEditAttempts=0;
        state["status"] = "running"; state["phase"] = "plan"; state["reason"]=null;
        state.Remove("search_finish");state["attempted"] = new JsonArray(); state["deadline"] = Now + 300; Save(); return "running";
    }
    public string Reconcile() { if (state["pending"] is null) return S(state, "status")!; state["status"] = "running"; state["phase"] = "reconcile"; state["deadline"] = Now + 120; Save(); return "running"; }

    private string Finish(string status, string? reason = null)
    {
        state["status"] = status; state["reason"] = reason; Save();
        if (reason is not null) Session.ReportWarning("surplus", reason, BatchId, state["pending"] is not null);
        return status;
    }

    protected override PageView Decorate(ClientCapture capture, PageView view)
    {
        identity = false;
        if (config is null || Item <= 0 || view.Modal) return view;
        var region = view.Page switch{"bag_item_detail"=>config["detail_title"],"bag_sale"=>config["sale_title_region"],_=>null};
        identity = region is not null && (view.Page=="bag_item_detail"
            ?Session.BagItemNameMatches(Name)
            :Session.QuantityReader.FindGridLabel(capture.Image,Session.Box(region),1,Name) is not null);
        if (view.Page == "bag_sale" && !identity) view = Session.RemoveTarget(view, "bag.sale_confirm");
        return view;
    }

    public string Tick()
    {
        if (S(state, "status") != "running") return S(state, "status")!;
        if(state["pending"] is null&&S(state,"phase")!="plan"&&reserve(Item) is null)return Finish("complete","surplus_material_not_authorized");
        if (config is null) return Finish("complete", "surplus_calibration_missing");
        if (Now > (state["deadline"]?.GetFiniteDouble() ?? 0)) return Finish(state["pending"] is null ? "complete" : "unknown", "surplus_check_timeout");
        var phase = S(state, "phase");
        if(phase is "search_clear_apply" or "search_cleared"&&Now>(state["selection_deadline"]?.GetFiniteDouble()??0))return Finish("complete","surplus_search_clear_timeout");
        if (phase == "plan")
        {
            var attempted = state["attempted"]!.AsArray().Select(v => v!.GetValue<long>()).ToHashSet();
            var snapshot = inventory();
            var row = snapshot.Observations.Values.FirstOrDefault(o => !attempted.Contains(o.ItemId) && o.Scope == snapshot.Scope
                && o.Source == "packet_capture" && o.Confirmed && o.Quantity is not null && reserve(o.ItemId) is { } keep && keep >= 0 && o.Quantity > keep);
            if (row is null) {if(state["search_dirty"]?.GetValue<bool>()==true){state["phase"]="search_clear";state["search_finish"]=true;state["selection_deadline"]=Now+30;Save();return "running";}return Finish("complete");}
            var keepAmount = reserve(row.ItemId); if (keepAmount is null) return Finish("complete", "surplus_reserve_unknown");
            search.Reset();quantityEditAttempts=0;state["selection_deadline"]=Now+45;state["item_id"] = row.ItemId; state["name"] = CatalogName(row.ItemId); state["attempted"]!.AsArray().Add(row.ItemId);
            state["before"] = row.Quantity; state["before_version"] = row.Version; state["minimum_keep"]=keepAmount.Value;state["amount"] = row.Quantity!.Value - keepAmount.Value; state["phase"] = "select"; Save(); return "running";
        }
        if(state["pending"] is null&&phase=="select"
            &&Now>(state["selection_deadline"]?.GetFiniteDouble()??double.MaxValue))
        {state["phase"]="search_clear";state["reason"]="surplus_item_not_visible";state["selection_deadline"]=Now+30;Save();phase="search_clear";}
        var observation = Read(); if (!observation.Stable || !Session.InputAvailable || Session.Guard.Paused) return "running";
        if(phase=="select"&&observation.View.Page is not ("scene" or "bag" or "repair_tools" or "bag_search")
            &&Session.Routes.Configuration["maintenance"]?["exit_navigation"]?[observation.View.Page] is not null)
        {
            var error=navigation.NavigateObserved("release",observation);
            return error is null?"running":Finish("complete",error);
        }
        if(state["pending"] is null&&(phase is "detail" or "input" or "verify")&&(observation.View.Page is "scene" or "bag" or "repair_tools"))
        {state["phase"]="select";search.Reset();Save();return "running";}
        if(phase=="select")
        {
            state["search_dirty"]=true;Save();
            var result=search.Tick(observation,Name);
            if(result.Item is{} selected){state["phase"]="detail";Save();Session.Guard.ClickAt(selected);}
            else if(result.Status=="missing"){state["phase"]="plan";state["reason"]="surplus_item_not_visible";Save();}
        }
        else if(phase=="search_clear")
        {
            if(Now>(state["selection_deadline"]?.GetFiniteDouble()??0))return Finish("complete","surplus_search_clear_timeout");
            if(observation.View.Page=="bag")
            {
                if(!observation.View.Targets.ContainsKey("bag.items_selected")){Session.NavigateTarget("bag.open_items");return "running";}
                Session.NavigateTarget("bag.search_open");
            }
            else if(observation.View.Page=="bag_search")
            {Session.NavigateTarget("bag.search_reset");state["phase"]="search_clear_apply";Save();}
            else if(observation.View.Page=="scene")return Finish("complete");
        }
        else if(phase=="search_clear_apply"&&observation.View.Page=="bag_search")
        {Session.NavigateTarget("bag.search_apply");state["phase"]="search_cleared";Save();}
        else if(phase=="search_cleared"&&observation.View.Page=="bag")
        {
            state["search_dirty"]=false;state["phase"]="plan";Save();
            if(state["search_finish"]?.GetValue<bool>()==true){state.Remove("search_finish");return Finish("complete");}
        }
        else if (phase == "detail" && observation.View.Page == "bag_item_detail") { if (!identity) return Finish("complete", "surplus_identity_unknown"); state["phase"] = "input"; Save(); Session.NavigateTarget("bag.sell_open"); }
        else if (phase == "input" && observation.View.Page == "bag_sale") { if (!identity) return Finish("complete", "surplus_identity_unknown"); state["phase"] = "verify"; Save(); EditQuantity(observation); }
        else if (phase == "verify" && observation.View.Page == "bag_sale" && identity)
        {
            if(Now-quantityEditedAt<1)return "running";
            if(Session.ReadQuantity(config["quantity"])?.Value?.Quantity!=Amount)
            {
                if(Now-quantityEditedAt<2)return "running";
                if(quantityEditAttempts<4){EditQuantity(observation);return "running";}
                if(Now-quantityEditedAt<8)return "running";return Finish("complete","surplus_selected_quantity_unverified");
            }
            var keep = reserve(Item); if (keep is null || state["before"]!.GetValue<long>() - Amount < keep) return Finish("complete", "surplus_reserve_changed");
            var snapshot=inventory();var current=snapshot.Query(Item);
            if(!current.Confirmed||current.Source!="packet_capture"||current.Scope!=snapshot.Scope||current.Quantity is null||current.Quantity-Amount<keep)return Finish("complete","surplus_current_stock_unverified");
            state["before"]=current.Quantity;state["before_version"]=current.Version;
            state["pending"] = new JsonObject { ["kind"] = "sale", ["item_id"] = Item, ["before"] = state["before"]!.DeepClone(), ["before_version"] = state["before_version"]!.DeepClone(), ["amount"] = Amount, ["minimum_keep"]=keep, ["inventory_scope"]=snapshot.Scope, ["at"] = Now }; state["phase"] = "result"; Save();
            try { Consume("bag.sale_confirm", "sale", _ => identity && reserve(Item) is { } current && state["before"]!.GetValue<long>() - Amount >= current); }
            catch (InputRejectedBeforeDispatchException) { state.Remove("pending"); state["phase"] = "verify"; state["reason"] = "target_changed_before_input"; Save(); }
            catch { Finish("unknown", "surplus_result_unknown"); throw; }
        }
        else if (phase is "result" or "reconcile")
        {
            if (observation.View.Page == "bag_sale") { if (phase == "reconcile") Session.NavigateTarget("bag.sale_cancel"); }
            else if (observation.View.Page == "bag_item_detail") Session.NavigateTarget("bag.detail_close");
            else if (observation.View.Page == "bag") { var current = inventory().Query(Item); if (current.Version == state["before_version"]?.GetValue<string>()) return "running"; state["phase"] = "post"; Save(); }
        }
        else if (phase == "post") { var snapshot = inventory();var current = snapshot.Query(Item); if (state["pending"] is JsonObject pending && MatchesPending(current, pending, snapshot.Scope)) CompleteSale(current); }
        return "running";
    }

    private string CatalogName(long item)
    {
        if(itemName?.Invoke(item) is{} linked)return linked;
        var decomposition=Session.Routes.Configuration["decomposition"];
        if(decomposition?["input_wire_id"]?.GetValue<long>()==item)return decomposition["input_name"]?.GetValue<string>()??item.ToString(CultureInfo.InvariantCulture);
        return Session.Routes.Configuration["item_names"]?[item.ToString(CultureInfo.InvariantCulture)]?.GetValue<string>() ?? item.ToString(CultureInfo.InvariantCulture);
    }
    private void EditQuantity(UiObservation observation)
    {Session.Guard.EditQuantity(Session.Guard.ObserveCurrentTarget("bag.sale_quantity",observation),Amount.ToString(CultureInfo.InvariantCulture));quantityEditedAt=Now;quantityEditAttempts++;}
}
