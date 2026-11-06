using MabiAgent.Core;
using System.Text.Json.Nodes;
namespace MabiAgent.Workflows;

public sealed record ProductionRecipe(string Key, string Device, double DurationSeconds);
public sealed record ProductionFrame(string Page, bool Foreground = true, bool Stable = true,
    int[]? EmptySlots = null, string? RecipeKey = null, bool CraftAvailable = false, bool CollectAvailable = false,
    double? DurationSeconds = null, int[]? ReadySlots = null, bool MaterialsPending = false, bool AtDevice = true,
    int? SlotCount = null, bool? MaterialsSufficient = null, double? DeviceRemainingSeconds = null,bool NavigationActive = false);
/// <summary>Every action must revalidate current foreground, page and target via the shared guard.
/// Craft also verifies the recipe and an empty slot; navigation issues at most one reversible step.</summary>
public interface IProductionPort : IForegroundWorkflowPort
{
    bool RemoteDevicePreview { get; }
    ProductionFrame? Observe();
    void Navigate(string destination, ProductionRecipe recipe);
    void Click(string kind, ProductionRecipe recipe);
    bool HasExtensionPending=>false;
    bool TickExtension()=>false;
}
public sealed class ProductionRunner : PersistentWorkflow
{
    public const double MinimumCollectionTripSeconds=300;
    // A readiness retry belongs to the slot that just came due. It is capped so
    // one missed observation can never postpone every later slot for an hour.
    public const double MaximumCollectionRetrySeconds=900;
    // A device whose finished slots all belong to the player has nothing this
    // batch may claim. Re-check slowly instead of a 15 second round trip.
    public const double ForeignSlotRecheckSeconds=300;
    private readonly IProductionPort port;
    private readonly ProductionRecipe recipe;
    private readonly double timeout;
    private readonly Func<string, int, int?>? availableRounds;
    private readonly Func<string, int?>? deviceReadyRounds;
    private readonly Func<string, double>? deviceQueueTail;
    private readonly Func<bool>? allowPartialRefill;
    private readonly Func<IReadOnlyDictionary<string,int[]>>? collectionOwners;
    private bool resumeRequested;
    public int[]? ConfirmedCollection { get; private set; }
    public ProductionFrame? QueueObservation { get; private set; }
    public double? LegacyTiming { get; }
    public double? EarliestCollectionAt
    {get{var state=Snapshot();var values=state["slot_due_at"]?.AsObject().Select(p=>p.Value!.GetFiniteDouble()).ToArray();return values is{Length:>0}?values.Min():state["due_at"]?.GetFiniteDouble();}}
    public double? NextCollectionAt(bool onsite=false)=>CollectionWakeAt(Snapshot(),onsite);
    internal static double? CollectionWakeAt(JsonObject state,bool onsite=false)
    {
        var dues=state["slot_due_at"] is JsonObject {Count:>0} slots
            ? slots.Select(p=>p.Value!.GetFiniteDouble()).Order().ToArray()
            : [];
        var due=dues.Length>0?dues[0]:state["due_at"]?.GetFiniteDouble();
        if(due is{} at&&state["collection_retry_at"] is{} retry)
        {
            var bounded=Math.Max(at,Math.Min(retry.GetFiniteDouble(),at+MaximumCollectionRetrySeconds));
            // A readiness retry belongs to the slots that had already come due when
            // the check failed, so the cutoff is when that check happened. Records
            // written before the cutoff existed fall back to the earliest due, which
            // is exactly what they used to mean.
            var checkedUntil=state["collection_checked_until"]?.GetFiniteDouble()??at;
            // Slots past the cutoff were never looked at: each keeps its own due, so a
            // new completion is not postponed by an old backoff or by its 900s cap.
            // Slots up to the cutoff share one retry, so a due in the past cannot make
            // the wait fire again the moment it returns.
            due=dues.Select(slot=>slot>checkedUntil?slot:bounded).DefaultIfEmpty(bounded).Min();
        }
        return !onsite&&due is{} wake&&state["collection_not_before"] is{} trip?Math.Max(wake,trip.GetFiniteDouble()):due;
    }
    public void DeferOffsiteCollection(double notBefore)
    {lock(Gate){if(notBefore>D("collection_not_before")){State["collection_not_before"]=notBefore;Save();}}}
    public bool SafeToSwitch => Pending is null && (S("phase") == "waiting"
        || S("status")=="unknown"&&port.CanReleaseForeground()
        || S("status")=="paused"&&(S("pause_reason") is "unrecognized_page" or "module_fault")&&port.CanReleaseForeground()
        || I("submitted")==0 && S("status")=="paused" && S("pause_reason") is "craft_unavailable" or "materials_unknown" or "device_full" or "device_batch_not_ready");
    public bool CanIsolateFault => Pending is null && (S("phase")=="waiting" || S("phase")=="leave"&&I("submitted")>0 || I("submitted")==0 && port.CanReleaseForeground());
    public ProductionRunner(IProductionPort port, IWorkflowStore store, ProductionRecipe recipe, int count = 1,
        TimeProvider? time = null, double timeoutSeconds = 12, Func<string, int, int?>? availableRounds = null,
        Func<string, int?>? deviceReadyRounds = null, Func<string, double>? deviceQueueTail = null, Func<bool>? allowPartialRefill = null,Func<IReadOnlyDictionary<string,int[]>>? collectionOwners=null)
        : base(store, time, new JsonObject())
    {
        if (string.IsNullOrWhiteSpace(recipe.Key) || string.IsNullOrWhiteSpace(recipe.Device) || count < 1) throw new ArgumentException("配方与生产次数无效");
        ValidatePositive(recipe.DurationSeconds, nameof(recipe)); ValidatePositive(timeoutSeconds, nameof(timeoutSeconds));
        this.port = port; this.recipe = recipe; timeout = timeoutSeconds; this.availableRounds = availableRounds;
        this.deviceReadyRounds = deviceReadyRounds; this.deviceQueueTail = deviceQueueTail; this.allowPartialRefill = allowPartialRefill;
        this.collectionOwners=collectionOwners;
        var serialized = new JsonObject { ["key"] = recipe.Key, ["device"] = recipe.Device, ["duration_s"] = recipe.DurationSeconds };
        Set(("schema_version", 1), ("recipe", serialized), ("count", count), ("status", "idle"),
            ("phase", port.RemoteDevicePreview ? "preview_queue" : "queue"), ("submitted", 0), ("completed", 0),
            ("owned_slots", new JsonArray()), ("before_empty", null), ("pending", null), ("due_at", null),
            ("timing_version", 2), ("production_due_at", null), ("pause_reason", null), ("reason", null), ("completed_steps", new JsonArray()));
        if (Load() is { } saved)
        {
            if (saved["schema_version"]?.GetValue<int>() != 1 || !JsonNode.DeepEquals(saved["recipe"], serialized) || saved["count"]?.GetValue<int>() != count
                || saved["owned_slots"] is not JsonArray || saved["completed_steps"] is not JsonArray) throw new InvalidDataException("生产记录不匹配，保留待核对");
            State = saved;
            // Older waits stored a failed readiness check in due_at. Preserve
            // that bounded retry while separating it from the production tail.
            if(State["collection_retry_at"] is null&&State["slot_due_at"] is JsonObject {Count:>0}&&State["due_at"] is{} wake&&State["production_due_at"] is{} tail&&wake.GetFiniteDouble()!=tail.GetFiniteDouble())
                State["collection_retry_at"]=wake.DeepClone();
            if (I("completed") < 0 || I("completed") > I("submitted") || I("submitted") > count) throw new InvalidDataException("生产计数无效");
            if (I("timing_version") != 2)
            {
                LegacyTiming = Slots(State["owned_slots"]).Count > 0 ? State["due_at"]?.GetFiniteDouble() : null;
                var due = LegacyTiming + (Slots(State["owned_slots"]).Count - 1) * recipe.DurationSeconds;
                Set(("due_at", due), ("production_due_at", due), ("timing_version", 2));
            }
            if (port.RemoteDevicePreview && Pending is null && I("submitted") == 0 && S("phase") is "queue" or "recipe") State["phase"] = "preview_queue";
            if (Pending is not null) Set(("status", "unknown"), ("reason", "pending_click_after_restart"), ("pause_reason", "reconciliation_required"));
            else if (S("status") is not ("complete" or "unknown") && !UnusedIdle) Set(("status", "paused"), ("pause_reason", "restart"));
        }
        Save();
    }
    private bool UnusedIdle => S("status") == "idle" && I("submitted") == 0 && Pending is null && Slots(State["owned_slots"]).Count == 0;
    private bool QueuePage(ProductionFrame f) => f.Page == recipe.Device + "_panel";
    private double Duration(ProductionFrame f) => f.DurationSeconds is > 0 and var n && double.IsFinite(n) ? n : recipe.DurationSeconds;
    private double QueueTail => Math.Max(D("production_due_at"), deviceQueueTail?.Invoke(recipe.Device) ?? 0);
    public JsonObject Pause(string reason = "manual")
    {
        port.Pause(reason);
        lock (Gate)
        {
            if (S("status") is not ("complete" or "unknown") && !(reason == "closed" && UnusedIdle) && !(S("pause_reason") == "manual" && reason == "focus_lost"))
            { resumeRequested = false; Set(("status", "paused"), ("pause_reason", reason)); Save(); }
            return Snapshot();
        }
    }
    private void ResumeFrame()
    {
        port.Resume();
        if (port.RemoteDevicePreview && S("phase") == "preview_recipe") State["phase"] = "preview_queue";
        if (S("phase") == "recipe" && Pending is null && (S("status") == "paused" || resumeRequested)) Set(("phase", "queue"), ("before_empty", null));
        resumeRequested = false; Set(("status", S("phase") == "waiting" ? "waiting" : "running"), ("pause_reason", null)); Save();
    }
    public JsonObject Resume(bool explicitRequest = false)
    {
        lock (Gate)
        {
            if (S("status") == "complete") return Snapshot();
            if (S("status") == "unknown")
            {
                if (explicitRequest && Pending is not null && port.Observe() is { } f && Ready(f.Page, f.Foreground, f.Stable))
                {
                    if (QueuePage(f) && f.AtDevice && f.EmptySlots is not null) { Confirm(f, true); if (Pending is null) ResumeFrame(); }
                    else if (f.Page == "production_complete" && Pending["kind"]?.GetValue<string>() == "collect") { Pending["deadline"] = Now + timeout; ResumeFrame(); }
                }
                return Snapshot();
            }
            if (!explicitRequest && S("pause_reason") != "focus_lost") return Snapshot();
            ResetPageWait();ResetProgressWait();
            var frame = port.Observe();
            if (frame is { Foreground: false }) return Pause("focus_lost");
            if (frame is not null && Ready(frame.Page, frame.Foreground, frame.Stable)) ResumeFrame();
            else { resumeRequested = true; Set(("status", S("phase") == "waiting" ? "waiting" : "running"), ("pause_reason", null)); Save(); }
            return Snapshot();
        }
    }
    public void StopSubmitting()
    {
        lock (Gate)
        {
            State["stop_requested"] = true;
            if (Pending is null && S("phase") is "queue" or "recipe")
            {
                State["phase"] = "leave";
                if (S("status") == "paused" && S("pause_reason") is "craft_unavailable" or "materials_unknown" or "device_full" or "device_batch_not_ready")
                    Set(("status", "running"), ("pause_reason", null));
            }
            Save();
        }
    }
    public void RestoreDeviceTiming(double dueAt) { lock (Gate) { Set(("production_due_at", dueAt), ("due_at", dueAt)); Save(); } }
    public bool HasAppliedCollection(string actionId)=>Snapshot()["completed_steps"]!.AsArray().Any(s=>s?.GetValue<string>()=="peer_collect:"+actionId);
    private void CompactSlots(IEnumerable<int> removed)
    {
        var cleared=removed.ToHashSet();int Move(int slot)=>slot-cleared.Count(c=>c<slot);
        State["owned_slots"]=Array(Slots(State["owned_slots"]).Select(Move));
        if(State["slot_due_at"] is JsonObject values)
        {var shifted=new JsonObject();foreach(var (slot,due) in values)shifted[Move(int.Parse(slot,System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture)]=due?.DeepClone();State["slot_due_at"]=shifted;}
    }
    public bool ReconcilePeerCollection(IEnumerable<int> clearedSlots, string actionId,bool compact=false)
    {
        lock (Gate)
        {
            var targets = Slots(State["owned_slots"]);targets.IntersectWith(clearedSlots);
            if (Pending is not null || targets.Count==0&&!compact || HasAppliedCollection(actionId)) return false;
            var wasPaused=S("status")=="paused";var pauseReason=S("pause_reason");
            State["completed"] = I("completed") + targets.Count; State["completed_steps"]!.AsArray().Add("peer_collect:" + actionId);
            var remaining=Slots(State["owned_slots"]).Except(targets).ToArray();State["owned_slots"]=Array(remaining);
            RemoveSlotTiming(targets);
            if(targets.Count>0){State.Remove("collection_retry_at");State.Remove("collection_checked_until");}
            if(compact)CompactSlots(clearedSlots);
            if(targets.Count>0&&remaining.Length==0)Set(("due_at",null),("production_due_at",null),("phase","leave"),("status","running"));
            Set(("pause_reason",null),("reason",null));if(wasPaused)Set(("status","paused"),("pause_reason",pauseReason));Save();return true;
        }
    }
    private void Unknown(string reason) { port.PauseLocal("reconciliation_required"); Set(("status", "unknown"), ("pause_reason", "reconciliation_required"), ("reason", reason)); Save(); }
    private void BlockPage(string reason)
    {if(Pending is not null)Unknown(reason);else{Set(("status","paused"),("pause_reason",reason),("reason",reason));Save();}}
    private bool? VisitReady(ProductionFrame f)
    {
        var available = availableRounds?.Invoke(recipe.Key, I("count") - I("submitted"));
        if (available == 0)
        {
            if (Slots(State["owned_slots"]).Count > 0) { Set(("phase", "leave"), ("reason", "craft_unavailable")); Save(); } else Pause("craft_unavailable"); return false;
        }
        var ready = deviceReadyRounds?.Invoke(recipe.Device) ?? available;
        var atDevice = f.AtDevice && (QueuePage(f) || f.Page == "recipe_detail" && f.RecipeKey == recipe.Key);
        if (B("visit_occupied") && !(B("visit_at_device") || atDevice) && !(allowPartialRefill?.Invoke() ?? false) && (ready is null || ready <= 0)) { Pause("device_batch_not_ready"); return false; }
        return available is not null ? true : null;
    }
    private bool TryCollectReady(ProductionFrame frame,HashSet<int> empty,HashSet<int> owned)
    {
        if(!frame.CollectAvailable||frame.ReadySlots is null)return false;
        var ready=frame.ReadySlots.ToHashSet();
        if(ready.Count==0)return false;
        var owners=collectionOwners?.Invoke();
        var known=owners is null?owned:owners.Values.SelectMany(s=>s).ToHashSet();
        var ambiguous=owners?.Values.SelectMany(s=>s).GroupBy(s=>s).Any(g=>g.Count()>1)==true;
        var approval=State["collect_all_once"] as JsonObject;
        var approved=approval?["device"]?.GetValue<string>()==recipe.Device&&approval?["source"]?.GetValue<string>()=="user_explicit"
            &&approval?["expires_at"]?.GetFiniteDouble()>Now&&owned.Count>0&&ready.Overlaps(owned);
        if(ambiguous){State["reason"]="collection_owner_ambiguous";Save();return false;}
        // One click collects every completed slot, the player's included. Claim
        // only what this batch and its peers own, and click only when that is the
        // whole ready set unless the user explicitly approved clearing the rest.
        var ours=ready.Intersect(known).ToHashSet();
        var foreign=ready.Except(ours).Order().ToArray();
        if(foreign.Length>0)State["foreign_ready"]=Array(foreign);else State.Remove("foreign_ready");
        if(ours.Count==0||foreign.Length>0&&!approved)
        {
            State["reason"]="collection_owner_unverified";Save();return false;
        }
        if(approved&&ready.Count>ours.Count)ours=ready;
        Consume("collect",empty,ours);return true;
    }
    private void RemoveSlotTiming(IEnumerable<int> slots)
    {if(State["slot_due_at"] is JsonObject values)foreach(var slot in slots)values.Remove(slot.ToString(System.Globalization.CultureInfo.InvariantCulture));}
    public void AcknowledgeCollection(string id)
    {lock(Gate){if(State["collection_receipts"] is not JsonArray receipts)return;var index=receipts.Select((r,i)=>(r,i)).FirstOrDefault(p=>p.r?["id"]?.GetValue<string>()==id, (null,-1)).i;if(index>=0){receipts.RemoveAt(index);Save();}}}
    private void Consume(string kind, IEnumerable<int> empty, IEnumerable<int>? targets = null, double? duration = null)
    {
        var pending = new JsonObject { ["kind"] = kind, ["before_empty"] = Array(empty), ["target_slots"] = Array(targets ?? []), ["at"] = Now, ["deadline"] = Now + timeout };
        if (kind == "craft") pending["production_due_at"] = Math.Max(Now, QueueTail) + (duration ?? recipe.DurationSeconds);
        if(kind=="collect")
        {
            pending["collection_id"]=Guid.NewGuid().ToString("N");var ownership=new JsonObject();
            foreach(var (action,slots) in collectionOwners?.Invoke()??new Dictionary<string,int[]>())
            {var claimed=slots.Intersect(Slots(pending["target_slots"])).ToArray();if(claimed.Length>0)ownership[action]=Array(claimed);}
            pending["collection_owners"]=ownership;
            if(State["foreign_ready"] is JsonArray foreign)pending["foreign_ready"]=foreign.DeepClone();
            if(State["collect_all_once"] is JsonObject approval){pending["collect_all_approval"]=approval.DeepClone();State.Remove("collect_all_once");}
        }
        State.Remove("unsent_craft");
        Set(("pending", pending), ("phase", "confirm_" + kind)); Save();
        try { port.Click(kind, recipe); }
        catch (InputRejectedBeforeDispatchException error)
        {
            State["last_rejection"]=error.Message;
            if(kind=="craft")State["unsent_craft"]=I("submitted")+1;
            Set(("pending",null),("phase",kind=="craft"?"queue":"collect"),("before_empty",null),("reason","target_changed_before_input"));Save();
        }
        catch (Exception error) { RememberFault(error,kind); Unknown("click_outcome_unknown: " + error.Message); }
    }
    public bool ReconcileVisible(ProductionFrame? frame = null)
    {
        lock (Gate)
        {
            if (S("status") != "unknown" || Pending?["kind"]?.GetValue<string>() != "collect") return false;
            frame ??= port.Observe(); if (frame is null || !Ready(frame.Page, frame.Foreground, frame.Stable) || !QueuePage(frame) || !frame.AtDevice || frame.EmptySlots is null) return false;
            Confirm(frame, true); if (Pending is not null) return false; Set(("status", "running"), ("pause_reason", null), ("reason", null)); Save(); return true;
        }
    }
    public void ObserveOnly()
    {
        lock (Gate)
        {
            if (Pending is null || port.Observe() is not { } frame || !Ready(frame.Page, frame.Foreground, frame.Stable)) return;
            if (QueuePage(frame) && frame.AtDevice && frame.EmptySlots is not null)
            {
                Confirm(frame, true);
                if (Pending is null && S("status") == "unknown") { Set(("status", "paused"), ("pause_reason", "reconciled")); Save(); }
            }
        }
    }
    private void Confirm(ProductionFrame f, bool passive = false)
    {
        var pending = Pending!;
        if (QueuePage(f) && f.AtDevice && f.EmptySlots is not null)
        {
            var empty = f.EmptySlots.ToHashSet(); var before = Slots(pending["before_empty"]);
            if (pending["kind"]!.GetValue<string>() == "craft")
            {
                var added = before.Except(empty).ToArray();
                if (added.Length == 1 && empty.IsSubsetOf(before))
                {
                    State["owned_slots"]!.AsArray().Add(added[0]); State["submitted"] = I("submitted") + 1;
                    var due = pending["production_due_at"]?.GetFiniteDouble() ?? Math.Max(pending["at"]!.GetFiniteDouble(), QueueTail) + Duration(f);
                    State["slot_due_at"]??=new JsonObject();State["slot_due_at"]![added[0].ToString(System.Globalization.CultureInfo.InvariantCulture)]=due;
                    Set(("production_due_at", due), ("due_at", Math.Max(D("due_at"), due))); State["completed_steps"]!.AsArray().Add("craft:" + I("submitted"));
                    Set(("pending", null), ("phase", "queue"), ("before_empty", null), ("reason", null)); Save(); return;
                }
            }
            else
            {
                var targets = Slots(pending["target_slots"]);
                var capacity=f.SlotCount??State["device_slot_count"]?.GetValue<int>();
                var compact=targets.Count>0&&capacity is >0&&empty.SetEquals(Enumerable.Range(capacity.Value-empty.Count,empty.Count))&&empty.Count==before.Count+targets.Count;
                if ((targets.IsSubsetOf(empty)||compact) && (targets.Count > 0 || empty.Except(before).Any()))
                {
                    ConfirmedCollection = compact?targets.Order().ToArray():empty.Except(before).Order().ToArray();var ownTargets=Slots(State["owned_slots"]);ownTargets.IntersectWith(targets);State["completed"] = I("completed") + ownTargets.Count;
                    State["owned_slots"] = Array(Slots(State["owned_slots"]).Except(ownTargets)); State["completed_steps"]!.AsArray().Add("collect:" + I("completed"));
                    RemoveSlotTiming(ownTargets);
                    if(compact)
                    {
                        CompactSlots(targets);State["completed_steps"]!.AsArray().Add("peer_collect:"+pending["collection_id"]!.GetValue<string>());
                        pending["collection_owners"]??=new JsonObject();foreach(var (action,slots) in collectionOwners?.Invoke()??new Dictionary<string,int[]>())
                            if(pending["collection_owners"]![action] is null)pending["collection_owners"]![action]=Array(slots.Intersect(targets));
                    }
                    if(pending["collection_owners"] is JsonObject ownership&&ownership.Count>0)
                    {State["collection_receipts"]??=new JsonArray();State["collection_receipts"]!.AsArray().Add(new JsonObject{["id"]=pending["collection_id"]!.DeepClone(),["owners"]=ownership.DeepClone(),["cleared"]=Array(ConfirmedCollection),["compact"]=compact});}
                    State.Remove("collection_retry_at");State.Remove("collection_checked_until");
                    Set(("pending", null), ("phase", targets.Count > 0 ? "leave" : "queue"), ("due_at", null));
                    if (Slots(State["owned_slots"]).Count == 0) State["production_due_at"] = null;else State["due_at"]=State["production_due_at"]?.DeepClone();Save(); return;
                }
            }
        }
        if (passive) return;
        if (Now >= pending["deadline"]!.GetFiniteDouble()) Unknown("consuming_result_not_visible");
        else if (!QueuePage(f) || f.EmptySlots is null) port.Navigate("queue", recipe);
    }
    private void Step(ProductionFrame f)
    {
        if (Pending is not null) { Confirm(f); return; }
        var phase = S("phase");
        if (phase == "waiting") { if (Now < (NextCollectionAt(f.AtDevice&&QueuePage(f))??D("due_at"))) return; phase = port.RemoteDevicePreview ? "preview_collect" : "collect"; Set(("status", "running"), ("phase", phase)); Save(); }
        if (phase is "preview_queue" or "preview_collect")
        {
            if (!QueuePage(f) || f.EmptySlots is null) { port.Navigate("queue_preview", recipe); return; }
            if (f.SlotCount is not { } capacity) return;
            State["device_slot_count"] = capacity; State["device_busy_until"] = f.DeviceRemainingSeconds is > 0 and var rem && double.IsFinite(rem) ? Now + rem : null;
            var occupied = Enumerable.Range(0, capacity).Except(f.EmptySlots).ToHashSet(); var allReady = occupied.Count > 0 && f.ReadySlots is not null && occupied.IsSubsetOf(f.ReadySlots);
            if (phase == "preview_collect")
            {
                if (occupied.Count == 0 || f.ReadySlots is {Length:>0} || f.AtDevice) State["phase"] = "collect";
                else { var attempts = Math.Min(7, I("collection_checks") + 1); var retry=State["device_busy_until"]?.GetFiniteDouble() ?? Now + Math.Min(3600, 60 * Math.Pow(2, attempts - 1)); Set(("phase", "leave"), ("collection_checks", attempts), ("due_at", retry), ("collection_retry_at",retry),("collection_checked_until",Now)); }
            }
            else if (allReady) Set(("phase", "queue"), ("collect_existing", true));
            else if (f.EmptySlots.Length == 0) { Pause("device_batch_not_ready"); return; }
            else { Set(("phase", "preview_recipe"), ("visit_empty", f.EmptySlots.Length), ("visit_occupied", occupied.Count > 0), ("visit_at_device", f.AtDevice)); if (VisitReady(f) == true) State["phase"] = "queue"; }
            Save(); return;
        }
        if (phase == "preview_recipe")
        {
            var decision = VisitReady(f); if (decision == false) return;
            if (decision == true) { Set(("phase", "queue"), ("reason", null)); Save(); return; }
            if (f.Page != "recipe_detail" || f.RecipeKey != recipe.Key) { port.Navigate("recipe_preview", recipe); return; }
            if (f.MaterialsPending) return;
            if (!(f.MaterialsSufficient == true || f.CraftAvailable)) { Pause(f.MaterialsSufficient == false ? "craft_unavailable" : "materials_unknown"); return; }
            Set(("phase", "queue"), ("reason", null)); Save(); return;
        }
        var owned = Slots(State["owned_slots"]);
        if (phase is "queue" or "collect")
        {
            if (phase == "queue" && !B("collect_existing") && I("submitted") < I("count") && !B("stop_requested") && VisitReady(f) == false) return;
            if (!QueuePage(f) || !f.AtDevice || f.EmptySlots is null) { port.Navigate("queue", recipe); return; }
            var empty = f.EmptySlots.ToHashSet();
            if (phase == "queue" && B("collect_existing") && TryCollectReady(f,empty,owned)) { State["collect_existing"] = false;Save(); }
            else if (phase == "collect")
            {
                if (owned.Count > 0 && owned.IsSubsetOf(empty))
                {
                    Unknown(B("retired_collection")?"retired_production_output_not_visible":"collection_receipt_missing");return;
                }
                else if (TryCollectReady(f,empty,owned)) {}
                else
                {
                    // Nothing of ours is ready. A device that only shows the
                    // player's finished slots gets a slow recheck, not a 15
                    // second navigation round trip that starves other work.
                    var foreign=S("reason") is "collection_owner_unverified" or "collection_owner_ambiguous";
                    var retry=Now+(foreign?ForeignSlotRecheckSeconds:Math.Min(Duration(f), 15));
                    // Leaving the device would bounce back at once while an empty
                    // slot remains, so a foreign-only device waits instead.
                    if(foreign)Set(("status","waiting"),("phase","waiting"),("due_at",retry),("collection_retry_at",retry),("collection_checked_until",Now),("collect_existing",false));
                    else Set(("phase", "leave"), ("due_at", retry), ("collection_retry_at",retry),("collection_checked_until",Now));
                    Save();
                }
            }
            else if (B("stop_requested") || I("submitted") >= I("count") || empty.Count == 0 && owned.Count > 0) { State["phase"] = "leave"; Save(); }
            else if (empty.Count == 0)
            {
                if(TryCollectReady(f,empty,owned)){}
                else if(S("reason") is "collection_owner_unverified" or "collection_owner_ambiguous")
                {
                    // The device is full of finished slots that are not ours.
                    // Keep the batch, stop holding the turn, re-check slowly.
                    var retry=Now+ForeignSlotRecheckSeconds;
                    Set(("collect_existing",false),("status","waiting"),("phase","waiting"),("due_at",retry),("collection_retry_at",retry),("collection_checked_until",Now));Save();
                }
                else Pause("device_full");
            }
            else { Set(("before_empty", Array(empty)), ("phase", "recipe")); Save(); port.Navigate("recipe", recipe); }
        }
        else if (phase == "recipe")
        {
            if (B("stop_requested")) { State["phase"] = "leave"; Save(); }
            else if (f.Page != "recipe_detail" || !f.AtDevice) port.Navigate("recipe", recipe);
            else if (f.RecipeKey != recipe.Key) Pause("recipe_mismatch");
            else if (!f.CraftAvailable) { if (f.MaterialsPending) return; if (owned.Count > 0) { Set(("phase", "leave"), ("reason", "craft_unavailable")); Save(); } else Pause("craft_unavailable"); }
            else Consume("craft", Slots(State["before_empty"]), duration: Duration(f));
        }
        else if (phase == "leave")
        {
            if(f.Page=="scene")DeferOffsiteCollection(Now+MinimumCollectionTripSeconds);
            if(owned.Count==0&&!B("stop_requested")&&I("submitted")<I("count")&&QueuePage(f)&&f.AtDevice&&f.EmptySlots is {Length:>0}
                &&availableRounds is not null)
            {Set(("phase","queue"),("status","running"),("reason",null));Save();}
            else if (f.Page != "scene") port.Navigate("leave", recipe);
            else if (owned.Count > 0) { Set(("status", "waiting"), ("phase", "waiting")); Save(); }
            else if (B("stop_requested") || I("completed") >= I("count")) { Set(("status", "complete"), ("reason", null)); Save(); }
            else if (S("reason") == "craft_unavailable") Pause("craft_unavailable");
            else { Set(("phase", "queue"), ("status", "running")); Save(); }
        }
    }
    public bool HoldForDeviceRefill()
    {
        lock (Gate)
        {
            if (S("phase") != "leave" || Pending is not null || S("status") != "running") return false;
            if (Slots(State["owned_slots"]).Count == 0) { if (I("completed") < I("count") && !B("stop_requested")) return false; Set(("status", "complete"), ("reason", null)); Save(); return true; }
            if (D("due_at") <= Now) return false; Set(("status", "waiting"), ("phase", "waiting")); Save(); return true;
        }
    }
    public JsonObject Tick(ProductionFrame? frame = null)
    {
        lock (Gate)
        {
            ConfirmedCollection = null; QueueObservation = null;
            if (S("status") is "idle" or "unknown" || S("status")=="complete"&&!port.HasExtensionPending || S("status") == "paused" && S("pause_reason") != "focus_lost") return Snapshot();
            frame ??= port.Observe(); if (frame is null) { PageReady("unknown",true,false,BlockPage);return Snapshot(); }
            if (!frame.Foreground) return Pause("focus_lost");
            if(Pending is null&&frame.NavigationActive)
            {
                if(frame.Page=="device_travel_confirm"&&frame.Stable)port.Navigate("queue",recipe);
                ResetPageWait();ResetProgressWait();return Snapshot();
            }
            if(Pending is null&&port.HasExtensionPending)
            {try{if(port.TickExtension())return Snapshot();}catch(Exception error)when(error is not OutOfMemoryException){RememberFault(error,"workbench");BlockPage("module_fault");return Snapshot();}}
            if(S("phase")=="workbench_finish"&&!port.HasExtensionPending){Set(("status","complete"),("reason",null));Save();return Snapshot();}
            if (!PageReady(frame.Page, frame.Foreground, frame.Stable, BlockPage)) { if (Pending is { } pending && Now >= pending["deadline"]!.GetFiniteDouble()) Unknown("consuming_result_not_visible"); return Snapshot(); }
            if (S("status") == "paused" || resumeRequested) { ResumeFrame(); return Snapshot(); }
            if(Pending is null&&S("phase")!="waiting")CheckProgress(S("phase")+":"+frame.Page+":"+I("submitted")+":"+I("completed"));else ResetProgressWait();
            if (QueuePage(frame) && frame.AtDevice && frame.EmptySlots is not null && frame.SlotCount is not null) QueueObservation = frame;
            Step(frame);if(S("status")=="complete"&&port.HasExtensionPending){Set(("status","running"),("phase","workbench_finish"));Save();}return Snapshot();
        }
    }
}

