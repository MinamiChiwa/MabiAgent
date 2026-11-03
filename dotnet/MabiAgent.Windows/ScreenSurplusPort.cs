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
    private readonly JsonNode? config;
    private JsonObject state;
    private bool identity;
    private bool itemsTabIssued;private double itemsTabAt;
    private double quantityEditedAt;
    private int quantityEditAttempts;
    private int selectionScrolls;private double selectionNextScroll;

    private long Item => state["item_id"]?.GetValue<long>() ?? 0;
    private string Name => S(state, "name") ?? "";
    private long Amount => state["amount"]?.GetValue<long>() ?? 0;

    public ScreenSurplusPort(ScreenWorkflowSession session, string batchId, IWorkflowStore store,
        Func<long, long?> requiredReserve, Func<InventorySnapshot>? packetInventory = null) : base(session, batchId, store)
    {
        reserve = requiredReserve;
        inventory = packetInventory ?? (() => new InventorySnapshot(Session.Scope.SessionId));
        config = session.Routes.Configuration["maintenance"]?["surplus"];
        state = store.Read() ?? new JsonObject { ["status"] = "idle", ["phase"] = "plan", ["attempted"] = new JsonArray(), ["receipts"] = new JsonArray() };
        if (state["pending"] is not null) state["status"] = "unknown";
        else if(S(state,"status")=="running") {state["deadline"]=Now+300;Save();}
    }

    private void Save() => Store.Flush(state);
    private static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;

    public void ObserveOnly()
    {
        if (state["pending"] is not JsonObject pending) return;
        var observation = inventory().Query(Item);
        if (MatchesPending(observation, pending)) CompleteSale(observation);
    }

    private bool MatchesPending(StockObservation observation, JsonObject pending)
    {
        var before = pending["before"]?.GetValue<long>();
        var amount = pending["amount"]?.GetValue<long>();
        var version = pending["before_version"]?.GetValue<string>();
        return observation.ItemId == Item && observation.Scope == Session.Scope.SessionId && observation.Source == "packet_capture"
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
        itemsTabIssued=false;itemsTabAt=0;quantityEditedAt=selectionNextScroll=0;quantityEditAttempts=selectionScrolls=0;
        state["status"] = "running"; state["phase"] = "plan"; state["reason"]=null;
        state["attempted"] = new JsonArray(); state["deadline"] = Now + 300; Save(); return "running";
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
        if (phase == "plan")
        {
            var attempted = state["attempted"]!.AsArray().Select(v => v!.GetValue<long>()).ToHashSet();
            var snapshot = inventory();
            var row = snapshot.Observations.Values.FirstOrDefault(o => !attempted.Contains(o.ItemId) && o.Scope == Session.Scope.SessionId
                && o.Source == "packet_capture" && o.Confirmed && o.Quantity is not null && reserve(o.ItemId) is { } keep && keep >= 0 && o.Quantity > keep);
            if (row is null) return Finish("complete");
            var keepAmount = reserve(row.ItemId); if (keepAmount is null) return Finish("complete", "surplus_reserve_unknown");
            quantityEditAttempts=selectionScrolls=0;selectionNextScroll=0;state["item_id"] = row.ItemId; state["name"] = CatalogName(row.ItemId); state["attempted"]!.AsArray().Add(row.ItemId);
            state["before"] = row.Quantity; state["before_version"] = row.Version; state["minimum_keep"]=keepAmount.Value;state["amount"] = row.Quantity!.Value - keepAmount.Value; state["phase"] = "select"; Save(); return "running";
        }
        var observation = Read(); if (!observation.Stable || !Session.InputAvailable || Session.Guard.Paused) return "running";
        if(state["pending"] is null&&(phase is "detail" or "input" or "verify")&&(observation.View.Page is "scene" or "bag" or "repair_tools"))
        {state["phase"]="select";Save();return "running";}
        if(phase=="select"&&observation.View.Page=="scene")
        {Session.NavigateTarget("scene.open_bag");return "running";}
        if(phase=="select"&&observation.View.Page=="repair_tools")
        {
            if(!itemsTabIssued){Session.Guard.PressKey(83,TimeSpan.FromMilliseconds(80));itemsTabIssued=true;itemsTabAt=Now;return "running";}
            if(Now-itemsTabAt>15)return Finish("complete","surplus_items_tab_not_observed");
            return "running";
        }
        if (phase == "select" && observation.View.Page == "bag")
        {
            var grid = Session.Routes.Configuration["surplus_sale"]?["grid"]; if (grid is null) return Finish("complete", "surplus_grid_missing");
            var selected=Session.FindBagItem(Name);
            if(selected is null)
            {
                if(Now<selectionNextScroll)return "running";
                if(selectionScrolls<24)
                {Session.Guard.ScrollAt(Session.Box(new PixelBox(1049,549,1051,551)).Center,Session.Box(new PixelBox(710,170,1260,820)),selectionScrolls++<12?-12:12);selectionNextScroll=Now+(config["selection_scroll_delay_s"]?.GetFiniteDouble()??1.2);return "running";}
                state["phase"]="plan";state["reason"]="surplus_item_not_visible";Save();return "running";
            }
            state["phase"] = "detail"; Save(); Session.Guard.ClickAt(selected.Value);
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
            var current=inventory().Query(Item);
            if(!current.Confirmed||current.Source!="packet_capture"||current.Scope!=Session.Scope.SessionId||current.Quantity is null||current.Quantity-Amount<keep)return Finish("complete","surplus_current_stock_unverified");
            state["before"]=current.Quantity;state["before_version"]=current.Version;
            state["pending"] = new JsonObject { ["kind"] = "sale", ["item_id"] = Item, ["before"] = state["before"]!.DeepClone(), ["before_version"] = state["before_version"]!.DeepClone(), ["amount"] = Amount, ["minimum_keep"]=keep, ["at"] = Now }; state["phase"] = "result"; Save();
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
        else if (phase == "post") { var current = inventory().Query(Item); if (state["pending"] is JsonObject pending && MatchesPending(current, pending)) CompleteSale(current); }
        return "running";
    }

    private string CatalogName(long item)
    {
        var decomposition=Session.Routes.Configuration["decomposition"];
        if(decomposition?["input_wire_id"]?.GetValue<long>()==item)return decomposition["input_name"]?.GetValue<string>()??item.ToString(CultureInfo.InvariantCulture);
        return Session.Routes.Configuration["item_names"]?[item.ToString(CultureInfo.InvariantCulture)]?.GetValue<string>() ?? item.ToString(CultureInfo.InvariantCulture);
    }
    private void EditQuantity(UiObservation observation)
    {Session.Guard.EditQuantity(Session.Guard.ObserveCurrentTarget("bag.sale_quantity",observation),Amount.ToString(CultureInfo.InvariantCulture));quantityEditedAt=Now;quantityEditAttempts++;}
}
