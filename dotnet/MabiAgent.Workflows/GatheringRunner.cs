using MabiAgent.Core;
using System.Text.Json.Nodes;
namespace MabiAgent.Workflows;
public sealed record GatheringRoute(string Key, long TargetItemId, string Skill, string Node, int AttemptsPerBatch = 10, long? ReferenceYieldPerBatch = null);
public sealed record GatheringFrame(string Page, bool Foreground = true, bool Stable = true, string? RouteKey = null,
    bool StartAvailable = false, JsonNode? TaskId = null, string TaskStatus = "unknown", long? ActualQuantity = null,
    JsonNode? Evidence = null, bool YieldPending = false, bool MissingConsumables = false);
public interface IGatheringPort : IForegroundWorkflowPort
{
    GatheringFrame Observe(GatheringRoute route, string actionId);
    GatheringFrame ObserveOnly(GatheringRoute route,string actionId)=>Observe(route,actionId);
    void Navigate(GatheringRoute route);
    /// <summary>Recheck route, foreground and start target through shared input guard. Never retry internally.</summary>
    void Start(GatheringRoute route, string actionId);
    bool DismissMissingConsumables()=>false;
    bool HasExtensionPending=>false;
    bool TickExtension()=>false;
}
public sealed record GatheringSupplyDecision(bool Ready, string? Reason = null, JsonObject? Reservation = null);
/// <summary>Reserve the whole native batch using verified identity and current scoped stock.
/// Water/milk adapters require ten empty bottles for ten attempts. Unknown stock stays unknown.
/// The coordinator may arrange purchase using shared maintenance preferences; this callback issues no input.
/// Repeated calls for an action ID must reuse its reservation.</summary>
public interface IGatheringConsumables
{
    void SetRemainingDemand(IEnumerable<(string RouteKey,long Rounds)> remaining) { }
    GatheringSupplyDecision ReserveForBatch(GatheringRoute route, string actionId);
    void ReleaseUnsubmitted(string actionId) { }
    void RejectMissingBatch(string actionId) { }
    IReadOnlyList<long> AwaitingInventoryItemIds=>[];
    IReadOnlyList<GatheringSupplyRequest> PendingRequests=>[];
    IReadOnlyList<JsonObject> Reservations=>[];
    void RestoreReservation(JsonObject reservation)=>throw new InvalidOperationException("耗材适配不支持恢复预留");
    bool ReconcileEndedBatch(string actionId)=>false;
}
public sealed class GatheringRunner : PersistentWorkflow
{
    private readonly IGatheringPort port; private readonly GatheringRoute route; private readonly string actionId;
    private readonly double timeout; private readonly double batchTimeout; private bool resumeRequested;
    private readonly IGatheringConsumables? consumables;
    private readonly Func<StockObservation>? inventoryBasis;
    public bool NavigationReleased => (State["evidence"] as JsonObject)?["navigation_idle_confirmed"]?.GetValue<bool>() == true;
    public bool SafeToSwitch => S("status") == "complete" || NavigationReleased || Pending is null&&I("submitted")==0&&S("status")=="paused"&&(S("pause_reason") is "unrecognized_page" or "module_fault")&&port.CanReleaseForeground();
    public bool CanIsolateFault => Pending is null && (NavigationReleased || I("submitted")==0 && port.CanReleaseForeground());
    public bool WaitingForConsumables {get;private set;}
    public GatheringRunner(IGatheringPort port, IWorkflowStore store, GatheringRoute route, string actionId,
        TimeProvider? time = null, double timeoutSeconds = 300, double batchTimeoutSeconds = 600, IGatheringConsumables? consumables = null,Func<StockObservation>? inventoryBasis=null) : base(store, time, new JsonObject())
    {
        if (string.IsNullOrWhiteSpace(route.Key) || string.IsNullOrWhiteSpace(route.Skill) || string.IsNullOrWhiteSpace(route.Node) || string.IsNullOrWhiteSpace(actionId) || route.TargetItemId <= 0 || route.AttemptsPerBatch <= 0) throw new ArgumentException("采集路线无效");
        ValidatePositive(timeoutSeconds, nameof(timeoutSeconds)); ValidatePositive(batchTimeoutSeconds, nameof(batchTimeoutSeconds));
        this.port = port; this.route = route; this.actionId = actionId; timeout = timeoutSeconds; batchTimeout = batchTimeoutSeconds; this.consumables = consumables;this.inventoryBasis=inventoryBasis;
        var serialized = new JsonObject { ["key"] = route.Key, ["target_item_id"] = route.TargetItemId, ["skill"] = route.Skill, ["node"] = route.Node, ["attempts_per_batch"] = route.AttemptsPerBatch };
        Set(("schema_version", 1), ("route", serialized), ("action_id", actionId), ("count", 1), ("status", "idle"), ("phase", "navigate"),
            ("pending", null), ("pause_reason", null), ("reason", null), ("submitted", 0), ("completed", 0), ("task_id", null),
            ("actual_quantity", null), ("evidence", null), ("batch_deadline", null), ("due_at", null), ("owned_slots", new JsonArray()), ("stop_requested", false));
        if (Load() is { } saved)
        {
            if (saved["schema_version"]?.GetValue<int>() != 1 || !JsonNode.DeepEquals(saved["route"], serialized) || saved["action_id"]?.GetValue<string>() != actionId
                || saved["count"]?.GetValue<int>() != 1 || saved["owned_slots"] is not JsonArray { Count: 0 }) throw new InvalidDataException("采集记录不匹配，保留待核对");
            State = saved; if (I("completed") < 0 || I("submitted") < I("completed") || I("submitted") > 1) throw new InvalidDataException("采集计数无效");
            if(State["actual_quantity"] is {} legacyQuantity)State["legacy_actual_quantity"]=legacyQuantity.DeepClone();
            State["actual_quantity"]=null;
            if(Pending is null&&I("submitted")==1&&S("phase")=="measure_yield")Set(("status","complete"),("phase","complete"),("completed",1),("reason","packet_reconcile_required"));
            if (Pending is not null) Set(("status", "unknown"), ("reason", NavigationReleased ? "actual_yield_unknown" : "pending_click_after_restart"), ("pause_reason", "reconciliation_required"));
            else if (S("status") is not ("complete" or "unknown")) Set(("status", "paused"), ("pause_reason", "restart"));
        }
        Save();
    }
    private GatheringFrame Observe(bool allowInput=true) => allowInput?port.Observe(route, actionId):port.ObserveOnly(route,actionId);
    // An uncertain native result owns this runner's turn, not the shared input
    // channel. Keep its intent and ownership until passive observation proves
    // completion or an idle boundary; never stop independent input globally.
    private void Unknown(string reason) { Set(("status", "unknown"), ("pause_reason", "reconciliation_required"), ("reason", reason)); Save(); }
    private void BlockLocally(string reason)
    {Set(("status","paused"),("pause_reason",reason),("reason",reason));Save();}
    public JsonObject Pause(string reason = "manual")
    {
        port.Pause(reason); lock (Gate)
        {
            if (S("status") is not ("complete" or "unknown") && !(S("pause_reason") == "manual" && reason == "focus_lost"))
            { resumeRequested = false; Set(("status", "paused"), ("pause_reason", reason)); Save(); }
            return Snapshot();
        }
    }
    private void ResumeFrame() { port.Resume(); resumeRequested = false; Set(("status", I("submitted") > 0 ? "waiting" : "running"), ("pause_reason", null), ("reason", null)); Save(); }
    public JsonObject Resume(bool explicitRequest = false)
    {
        lock (Gate)
        {
            if (S("status") == "complete") return Snapshot();
            if (S("status") == "unknown")
            {
                if (explicitRequest)
                {
                    var f=Observe(Pending is null&&NavigationReleased);
                    if(Ready(f.Page,f.Foreground,f.Stable))
                    {
                        if(Pending is not null)ConfirmStart(f,false);
                        else if(I("submitted")>0)
                        {
                            ObserveBatch(f,true,false);
                        }
                        if(S("status")=="waiting")ResumeFrame();
                    }
                }
                return Snapshot();
            }
            if (!explicitRequest && S("pause_reason") != "focus_lost") return Snapshot();
            ResetPageWait();ResetProgressWait();
            var frame = Observe(); if (!frame.Foreground) return Pause("focus_lost");
            if (Ready(frame.Page, frame.Foreground, frame.Stable)) ResumeFrame();
            else { resumeRequested = true; Set(("status", I("submitted") > 0 ? "waiting" : "running"), ("pause_reason", null)); Save(); }
            return Snapshot();
        }
    }
    public void StopSubmitting()
    {
        lock (Gate)
        {
            State["stop_requested"] = true; if (I("submitted") == 0 && Pending is null) { consumables?.ReleaseUnsubmitted(actionId); Set(("status", "complete"), ("phase", "complete"), ("pause_reason", null), ("reason", null)); } Save();
        }
    }
    private bool PrepareSupply()
    {
        var supply=consumables?.ReserveForBatch(route,actionId);
        WaitingForConsumables=supply is {Ready:false};
        if(!WaitingForConsumables)return true;
        Set(("status","paused"),("pause_reason",supply!.Reason??"consumables_unknown"));Save();return false;
    }
    private bool RejectMissing(GatheringFrame frame,bool allowInput)
    {
        if(!frame.MissingConsumables||I("submitted")!=0||consumables is null)return false;
        // Only the start intent that this runner just persisted can own this
        // refusal. A dialog already present for another route or a manual action
        // must not clear this runner's reservation or be dismissed here.
        if(Pending is not { } pending||pending["kind"]?.GetValue<string>()!="gathering")return true;
        // The game's explicit refusal proves that this start did not consume bottles.
        // An unknown result without that refusal keeps its original intent.
        if(!allowInput||!frame.Foreground||!frame.Stable||!port.DismissMissingConsumables())return true;
        consumables.RejectMissingBatch(actionId);
        Set(("pending",null),("phase","navigate"),("status","running"),("reason",null),("pause_reason",null));Save();
        PrepareSupply();return true;
    }
    private void Start(GatheringFrame f)
    {
        var supply = consumables?.ReserveForBatch(route, actionId);
        if (supply is { Ready: false }) { WaitingForConsumables=true; var reason=supply.Reason ?? "consumables_unknown"; port.PauseLocal(reason); Set(("status","paused"),("pause_reason",reason)); Save(); return; }
        WaitingForConsumables=false;
        if(inventoryBasis is not null)State["inventory_basis"]=System.Text.Json.JsonSerializer.SerializeToNode(inventoryBasis());
        Set(("phase", "confirm_start"), ("pending", new JsonObject { ["kind"] = "gathering", ["action_id"] = actionId, ["before_task_id"] = f.TaskId?.DeepClone(), ["at"] = Now, ["deadline"] = Now + timeout })); Save();
        if (supply?.Reservation is not null) { Pending!["consumables"] = supply.Reservation.DeepClone(); Save(); }
        try { port.Start(route, actionId); Pending!["deadline"] = Now + timeout; Save(); }
        catch (InputRejectedBeforeDispatchException)
        { consumables?.ReleaseUnsubmitted(actionId);Set(("phase", "navigate"), ("pending", null),("status","running"),("reason","target_changed_before_input"));Save(); }
        catch (Exception error) { RememberFault(error,"start"); Unknown("start_outcome_unknown: " + error.Message); }
    }
    private void ConfirmStart(GatheringFrame f, bool allowInput = true)
    {
        var pending = Pending!;
        if (f.RouteKey == route.Key && f.TaskId is not null && f.TaskId.ToJsonString() != "\"\"" && !JsonNode.DeepEquals(f.TaskId, pending["before_task_id"]) && f.TaskStatus is "active" or "complete")
        {
            // Keep the reservation after the start intent is reconciled so
            // restart cannot allocate these same bottles to another batch.
            if(pending["consumables"] is { } held)State["consumables"]=held.DeepClone();
            Set(("submitted", 1), ("task_id", f.TaskId), ("pending", null), ("status", "waiting"), ("phase", "gathering"), ("reason", null), ("pause_reason", null), ("batch_deadline", Now + batchTimeout), ("evidence", null)); Save(); ObserveBatch(f, allowInput: allowInput);
        }
        else if (!IdleWithoutResult(f) && Now >= pending["deadline"]!.GetFiniteDouble()) Unknown("start_result_not_visible");
    }
    private bool IdleWithoutResult(GatheringFrame f)
    {
        if (f.Evidence is JsonObject evidence && evidence["navigation_idle_confirmed"]?.GetValue<bool>() == true) State["evidence"] = evidence.DeepClone();
        if (!NavigationReleased) return false; Unknown("actual_yield_unknown"); return true;
    }
    private void ObserveBatch(GatheringFrame f, bool extendActive = false, bool allowInput = true)
    {
        if (JsonNode.DeepEquals(f.TaskId, State["task_id"]))
        {
            if (f.TaskStatus == "complete")
            {
                if(f.Evidence is JsonObject ended&&ended["navigation_idle_confirmed"]?.GetValue<bool>()==true)State["evidence"]=ended.DeepClone();
                    // Material quantity is reconciled from the next packet capture.
                    // The native batch itself is complete even though no screen count exists.
                    Set(("status", "complete"), ("phase", "complete"), ("completed", 1), ("actual_quantity", null), ("evidence", f.Evidence), ("pause_reason", null), ("reason", "packet_reconcile_required"), ("batch_deadline", null)); Save();
                return;
            }
            if (f.TaskStatus == "active" && extendActive) { Set(("status", "waiting"), ("phase", "gathering"), ("pause_reason", null), ("reason", null), ("batch_deadline", Now + batchTimeout)); Save(); }
        }
        if (!IdleWithoutResult(f) && Now >= D("batch_deadline")) Unknown("gathering_result_not_visible");
    }
    public JsonObject Tick()
    {
        lock (Gate)
        {
            if (S("status")=="idle"||S("status")=="complete"&&!port.HasExtensionPending) return Snapshot();
            var f = Observe(S("status") is not ("paused" or "unknown"));
            if(RejectMissing(f,S("status")!="paused"))return Snapshot();
            if (S("status") == "unknown")
            { if (Ready(f.Page, f.Foreground, f.Stable)) { if (Pending is not null) ConfirmStart(f, false); else if (I("submitted") > 0) ObserveBatch(f, allowInput: false); } return Snapshot(); }
            if (!f.Foreground) { if (S("status") != "paused" || S("pause_reason") == "focus_lost") Pause("focus_lost"); return Snapshot(); }
            if(Pending is null&&S("status")!="paused"&&port.HasExtensionPending)
            {try{if(port.TickExtension())return Snapshot();}catch(Exception error)when(error is not OutOfMemoryException){RememberFault(error,"workbench");BlockLocally("module_fault");return Snapshot();}}
            if(S("phase")=="workbench_finish"&&!port.HasExtensionPending){Set(("status","complete"),("phase","complete"),("reason",null));Save();return Snapshot();}
            if (!PageReady(f.Page, f.Foreground, f.Stable, BlockLocally))
            { if (Pending is { } pending && Now >= pending["deadline"]!.GetFiniteDouble()) Unknown("start_result_not_visible"); else if (I("submitted") > 0 && Now >= D("batch_deadline")) Unknown("gathering_result_not_visible"); return Snapshot(); }
            if (S("status") == "paused")
            {
                if (S("pause_reason") == "focus_lost") ResumeFrame();
                else
                {
                    var reason = S("pause_reason"); if (Pending is not null) ConfirmStart(f, false); else if (I("submitted") > 0) ObserveBatch(f, allowInput: false);
                    if (S("status") is not ("complete" or "unknown")) { Set(("status", "paused"), ("pause_reason", reason)); Save(); }
                }
                return Snapshot();
            }
            if (resumeRequested) { ResumeFrame(); return Snapshot(); }
            if(Pending is null&&I("submitted")==0)CheckProgress(S("phase")+":"+f.Page);else ResetProgressWait();
            if (Pending is not null) ConfirmStart(f);
            else if (I("submitted") > 0) ObserveBatch(f);
            else if (B("stop_requested")) StopSubmitting();
            else if (f.TaskStatus == "active") Pause("active_task_exists");
            else if (f.RouteKey != route.Key || !f.StartAvailable) {if(PrepareSupply())port.Navigate(route);}
            else Start(f);
            if(S("status")=="complete"&&port.HasExtensionPending){Set(("status","running"),("phase","workbench_finish"));Save();}
            return Snapshot();
        }
    }
    public void ObserveOnly()
    {
        lock (Gate)
        {
            if (S("status") == "complete" || Pending is null && I("submitted") == 0) return;
            var frame = Observe(false); if (!Ready(frame.Page, frame.Foreground, frame.Stable)) return;
            var paused=S("status")=="paused";var reason=S("pause_reason");
            if (Pending is not null) ConfirmStart(frame, false); else ObserveBatch(frame, allowInput: false);
            if(paused&&S("status") is not ("complete" or "unknown")){Set(("status","paused"),("pause_reason",reason));Save();}
        }
    }
}

