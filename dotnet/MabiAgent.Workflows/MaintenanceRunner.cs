using MabiAgent.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace MabiAgent.Workflows;
public sealed record MaintenanceFrame(string CaptureId, string Page, bool Foreground = true, bool Stable = true, long? Weight = null,
    long? Capacity = null, long? Gold = null, long? Quote = null, string? Currency = null, bool ConfirmAvailable = false, bool? BoldSelected = null,
    bool? MaterialsSelected = null, long? TidyCount = null, bool RepairComplete = false, bool TidyComplete = false, bool ToolsEquipped = false,
    bool RepairNotNeeded = false, bool BackpackFull = false, bool QuoteIsUpperBound = false, bool WeightTabRequired = false);
public sealed record MaintenancePolicy(bool RepairEnabled = true, bool BackpackEnabled = true, double IntervalSeconds = 1800,
    long GoldReserve = 40000, int ReadAttempts = 3, int WeightThresholdPercent = 90, bool ReserveInclusive = true, bool SellSurplus = true,bool ProductionFirst=false,double BackpackCheckIntervalSeconds=0);
public interface IMaintenancePort : IForegroundWorkflowPort
{
    MaintenanceFrame Observe();
    /// <summary>Perform one reversible step; return a block reason when the finite route cannot progress.</summary>
    string? Navigate(string destination);
    /// <summary>Review target and selections with shared guard. Never replay consuming actions.</summary>
    void Act(string action);
    void ResetNavigation();
    bool ReleaseNavigation()=>CanReleaseForeground();
}
public interface ISurplusMaintenancePort
{
    string Begin();
    string Tick();
    string Reconcile();
    void ObserveOnly(){}
    JsonObject Snapshot();
}
public sealed class MaintenanceRunner : PersistentWorkflow
{
    private readonly IMaintenancePort port; private readonly ISurplusMaintenancePort? surplus; private readonly double timeout; private MaintenancePolicy policy;
    private bool resumeRequested; private double resumeDeadline;
    private string? waitKey;private double waitAt;private double? cleanupStartedAt;
    public bool CanIsolateFault=>Pending is null&&(surplus?.Snapshot()["pending"] is null)&&S("active") is null&&port.CanReleaseForeground();
    public void Quarantine(){lock(Gate){Repair["status"]="blocked";Set(("paused","module_fault"));Save();}}
    private JsonObject Repair => State["repair"]!.AsObject(); private JsonObject Bag => State["backpack"]!.AsObject();
    private static string? Str(JsonObject obj, string key) => obj[key]?.GetValue<string>();
    private static int Int(JsonObject obj, string key) => obj[key]?.GetValue<int>() ?? 0;
    private static bool Bool(JsonObject obj, string key) => obj[key]?.GetValue<bool>() ?? false;
    private static void Change(JsonObject obj, params (string Key, object? Value)[] changes) { foreach (var (key, value) in changes) obj[key] = value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value); }
    private static void ValidatePolicy(MaintenancePolicy p)
    { ValidatePositive(p.IntervalSeconds, nameof(p.IntervalSeconds)); if (p.GoldReserve < 0 || p.ReadAttempts is < 1 or > 10 || p.WeightThresholdPercent is < 1 or > 100 || !double.IsFinite(p.BackpackCheckIntervalSeconds)||p.BackpackCheckIntervalSeconds<0) throw new ArgumentException("维护设置无效"); }
    public MaintenanceRunner(IMaintenancePort port, IWorkflowStore store, MaintenancePolicy? policy = null, TimeProvider? time = null, double timeoutSeconds = 15, ISurplusMaintenancePort? surplus = null) : base(store, time, new JsonObject())
    {
        this.port = port; this.policy = policy ?? new(); this.surplus = surplus; ValidatePolicy(this.policy); ValidatePositive(timeoutSeconds, nameof(timeoutSeconds)); timeout = timeoutSeconds;
        Set(("schema_version", 1), ("active", null), ("paused", null), ("global_pause_required", false), ("pending", null), ("reason", null),
            ("repair", new JsonObject { ["phase"] = "idle", ["status"] = "idle", ["due_at"] = Now + this.policy.IntervalSeconds, ["quote_reads"] = 0, ["gold_reads"] = 0, ["last_capture"] = null, ["before_gold"] = null, ["quote"] = null, ["post_gold"] = null, ["outcome_seen"] = false, ["reserve_violated"] = false }),
            ("backpack", new JsonObject { ["phase"] = "idle", ["status"] = "idle", ["last_capture"] = null, ["weight"] = null, ["capacity"] = null, ["reads"] = 0, ["outcome_seen"] = false }));
        if (Load() is { } saved)
        {
            if (saved["schema_version"]?.GetValue<int>() != 1 || saved["repair"] is not JsonObject || saved["backpack"] is not JsonObject || saved["active"]?.GetValue<string>() is not (null or "repair" or "backpack")) throw new InvalidDataException("维护记录无效，保留待核对"); State = saved;
        }
        Bag["check_due_at"]??=Now+this.policy.BackpackCheckIntervalSeconds;
        if(Str(Bag,"phase")=="surplus"&&!Bool(Bag,"tidy_checked")&&Pending is null&&surplus?.Snapshot()["pending"] is null)
            Change(Bag,("phase","prepare"),("surplus_checked",false),("cleanup_after_relief",false));
        // Preserve a known capacity failure when reading records from before this flag existed.
        Bag["capacity_blocked"]??=Str(Bag,"status")=="production_relief"||
            S("reason") is "backpack_full_navigation" or "backpack_travel_blocked" or "backpack_still_overweight"||
            Bag["weight"]?.GetValue<long>() is >=0 and var weight&&Bag["capacity"]?.GetValue<long>() is >0 and var capacity&&
            (decimal)weight*100>(decimal)capacity*this.policy.WeightThresholdPercent;
        CancelDisabled();
        if(B("global_pause_required")&&Pending is null&&surplus?.Snapshot()["pending"] is null&&S("reason") is "backpack_weight_unknown" or "backpack_progress_timeout" or "post_production_weight_unknown" or "tidy_count_unknown" or "tidy_options_unknown")Manual(S("reason")!);
        if (this.policy.BackpackEnabled && B("global_pause_required") && Pending is null && S("reason") is "backpack_still_overweight" or "nothing_to_tidy") ProductionRelief(S("reason")!);
        if (S("active") is not null && Pending is null) State["paused"] ??= JsonValue.Create("restart"); Save();
    }
    public JsonObject StatusSnapshot()
    {
        lock (Gate)
        {
            var value = Snapshot(); var active = S("active"); value["navigation_reserved"] = (B("navigation_cleanup") || active is not null && (Str(State[active]!.AsObject(), "status") == "running" || active == "backpack" && Str(Bag, "phase") == "relief_weight")) && S("paused") is null && !B("global_pause_required");
            if (surplus is not null) value["surplus"] = surplus.Snapshot(); return value;
        }
    }
    public void Configure(MaintenancePolicy newPolicy)
    { lock (Gate) { ValidatePolicy(newPolicy); var oldInterval = policy.IntervalSeconds; policy = newPolicy; CancelDisabled(); if (Str(Repair, "status") == "idle") Repair["due_at"] = Repair["due_at"]!.GetFiniteDouble() + policy.IntervalSeconds - oldInterval; Save(); } }
    public void RestartRepairSchedule()
    {
        lock(Gate)
        {
            Repair["due_at"]=Now+policy.IntervalSeconds;
            // Preserve sent payments and equipment completion checkpoints.
            if(Pending is null&&Str(Repair,"phase") is "idle" or "quote"&&!Bool(Repair,"outcome_seen"))
            {
                Change(Repair,("phase","idle"),("status","idle"),("quote_reads",0),("gold_reads",0),("last_capture",null),("quote",null),("before_gold",null));
                Repair.Remove("gold_sync_attempted");
                if(S("active")=="repair")State["active"]=null;
                if(S("reason")?.StartsWith("repair_",StringComparison.Ordinal)==true)State["reason"]=null;
            }
            Save();
        }
    }
    public void RestartBackpackSchedule()
    {
        lock(Gate)
        {
            // Scheduling a check is not an observed backpack weight.
            Bag["check_due_at"]=Now+policy.BackpackCheckIntervalSeconds;
            CancelPeriodicBackpackCheck();Save();
        }
    }
    private void CancelDisabled()
    {
        if (!policy.BackpackEnabled && Str(Bag, "status") is "due" or "running" or "production_relief" && !B("global_pause_required") && !(S("active") == "backpack" && Pending is not null))
        { Change(Bag, ("phase", "idle"), ("status", "idle"), ("reads", 0), ("outcome_seen", false)); if (S("active") == "backpack") { Set(("active", null), ("reason", null)); port.ResetNavigation(); } }
        if (policy.RepairEnabled || Str(Repair, "status") == "idle") return;
        if (S("active") == "repair" && Pending is not null || Str(Repair, "phase") is "result" or "equip" || Str(Repair, "status") is "blocked" or "unknown" && Bool(Repair, "outcome_seen")) return;
        Change(Repair, ("phase", "idle"), ("status", "idle"), ("quote_reads", 0), ("gold_reads", 0), ("last_capture", null), ("before_gold", null), ("quote", null), ("post_gold", null), ("outcome_seen", false), ("reserve_violated", false));
        if (S("active") != "backpack") { State["active"] = null; if (!B("global_pause_required")) State["reason"] = null; port.ResetNavigation(); }
    }
    public bool RepairRequired
    { get { lock (Gate) return policy.RepairEnabled && (Str(Repair, "status") is "due" or "running" or "blocked" or "unknown" || Str(Repair, "status") == "idle" && Now >= Repair["due_at"]!.GetFiniteDouble()); } }
    public bool RequiresTurn
        =>RequiresTurnFor(true);
    public bool RequiresTurnFor(bool allowRepair,bool allowPeriodicBackpackCheck=true)
    {
        lock(Gate)
        {
            var repair=allowRepair&&policy.RepairEnabled&&(Str(Repair,"status") is "due" or "running"||Str(Repair,"status")=="idle"&&Now>=Repair["due_at"]!.GetFiniteDouble());
            var backpack=policy.BackpackEnabled&&Str(Bag,"status") is "due" or "running"
                &&(allowPeriodicBackpackCheck||Str(Bag,"phase")!="inspect"||S("active")=="backpack"||Bool(Bag,"inspection_requested"));
            return repair||backpack;
        }
    }
    public void RequestBackpackInspection()
    {
        lock(Gate)
        {
            if(!policy.BackpackEnabled||Pending is not null||Str(Bag,"status") is not ("idle" or "production_relief"))return;
            Change(Bag,("phase","inspect"),("status","due"),("reads",0),("inspection_requested",true));Save();
        }
    }
    public bool PeriodicBackpackCheckDue(double minimumIntervalSeconds)
    {
        lock(Gate)
        {
            var checkedAt=Bag["checked_at"]?.GetFiniteDouble()??Bag["check_due_at"]!.GetFiniteDouble()-policy.BackpackCheckIntervalSeconds;
            return Now>=checkedAt+minimumIntervalSeconds;
        }
    }
    public bool TryRequestGoldSync()
    {
        lock(Gate)
        {
            if(!policy.RepairEnabled||Pending is not null||S("paused") is not null||B("navigation_cleanup")||B("global_pause_required")||S("active") is not null||Str(Repair,"status")!="blocked"||S("reason")!="repair_gold_unknown"||Bool(Repair,"gold_sync_attempted"))return false;
            Repair["gold_sync_attempted"]=true;Save();return true;
        }
    }
    public bool Blocked(string kind, bool dependsOnRepair = false)
    {
        lock (Gate)
        {
            var acquiring=kind is "gathering" or "idle_fishing";
            var relieving=Str(Bag,"status")=="production_relief";
            var capacityFailure=Str(Bag,"status")=="blocked"&&Bool(Bag,"capacity_blocked");
            var room=Bag["weight"]?.GetValue<long>() is >=0 and var weight&&Bag["capacity"]?.GetValue<long>() is >0 and var capacity&&weight<capacity;
            return B("global_pause_required")||acquiring&&(capacityFailure||Bool(Bag,"movement_blocked")||relieving&&!room)||dependsOnRepair&&RepairRequired;
        }
    }
    public void Pause(string reason = "manual")
    { port.Pause(reason); lock (Gate) { resumeRequested = false; if (S("paused") != "manual" || reason == "manual") State["paused"] = reason; if (Pending is null && Str(Bag, "phase") == "confirm") Bag["phase"] = "prepare"; Save(); } }
    private bool ValidWeight(MaintenanceFrame f) => f.Weight >= 0 && f.Capacity > 0;
    private bool Overweight(MaintenanceFrame f) => (decimal)f.Weight!.Value * 100 > (decimal)f.Capacity!.Value * policy.WeightThresholdPercent;
    private bool ReserveOk(long gold) => policy.ReserveInclusive ? gold >= Math.Max(40000,policy.GoldReserve) : gold > Math.Max(40000,policy.GoldReserve);
    private static bool ReadyFrame(MaintenanceFrame f) => !string.IsNullOrWhiteSpace(f.CaptureId) && Ready(f.Page, f.Foreground, f.Stable);
    public void Resume(bool explicitRequest = false)
    {
        lock (Gate)
        {
            if (!explicitRequest && S("paused") != "focus_lost") return;
            if (explicitRequest) { resumeRequested = true; resumeDeadline = Now + timeout; }
            var f = port.Observe(); if (!ReadyFrame(f)) return;
            ResumeFrame(f);
        }
    }
    private void ResumeFrame(MaintenanceFrame f)
    {
            var explicitRequest = resumeRequested; resumeRequested = false;
            waitKey=null;
            if(explicitRequest&&Pending is null)port.ResetNavigation();
            if(explicitRequest&&Pending is null&&Str(Bag,"phase")=="confirm"&&f.Page!="tidy_confirm")Bag["phase"]="prepare";
            if(explicitRequest&&Pending is null&&Str(Repair,"status")=="blocked"&&!Bool(Repair,"outcome_seen"))RetryRepair();
            if(explicitRequest&&Pending is null&&Str(Bag,"status")=="blocked")
            {Set(("active","backpack"),("reason",null),("navigation_cleanup",false));Change(Bag,("status","running"),("reads",0),("last_capture",null));
                if(Str(Bag,"phase") is "relief" or "prepare"){Change(Bag,("phase","prepare"),("cleanup_after_relief",true),("surplus_checked",false));}}
            if (explicitRequest && B("global_pause_required") && S("reason") == "surplus_result_unknown" && surplus?.Reconcile() == "running")
            { Set(("global_pause_required", false), ("active", "backpack"), ("reason", null)); Change(Bag, ("phase", "surplus"), ("status", "running")); }
            if (explicitRequest && B("global_pause_required") && S("reason") == "tidy_count_unknown" && Pending is null && f.Page == "tidy")
            { Set(("global_pause_required", false), ("active", "backpack"), ("reason", null)); Change(Bag, ("phase", "prepare"), ("status", "running"), ("reads", 0), ("last_capture", null)); }
            if(explicitRequest&&B("global_pause_required")&&Pending?["action"]?.GetValue<string>()=="confirm_tidy"&&
                f.CaptureId!=Pending["capture_id"]?.GetValue<string>()&&(f.TidyComplete||Bool(Bag,"outcome_seen")))
            {
                Set(("global_pause_required",false),("active","backpack"),("reason",null));
                Change(Bag,("phase","verify"),("status","running"),("outcome_seen",true),("reads",0));
            }
            if(explicitRequest&&B("global_pause_required")&&Pending is null&&
                (S("reason")=="backpack_weight_unknown"||S("reason")=="backpack_progress_timeout"&&Str(Bag,"phase")=="inspect"))
            {
                // Explicit retry may reopen the backpack to obtain a fresh weight.
                // It does not authorize cleanup or reuse the saved weight.
                Set(("global_pause_required",false),("active","backpack"),("reason",null));
                Change(Bag,("phase","inspect"),("status","running"),("reads",0),("last_capture",null));
            }
            if (B("global_pause_required"))
            {
                if (!ValidWeight(f) || Overweight(f) || f.CaptureId == Str(Bag, "last_capture")) return;
                // A fresh acceptable weight releases movement, never discards an unaccounted sale.
                if (Pending is not null && !Bool(Bag, "outcome_seen")) return;
                State["global_pause_required"] = false; Change(Bag, ("phase", "idle"), ("status", "idle"), ("last_capture", f.CaptureId), ("weight", f.Weight), ("capacity", f.Capacity), ("outcome_seen", false)); Set(("active", null), ("pending", null), ("reason", null));
            }
            if (explicitRequest && Str(Repair, "status") == "blocked" && Bool(Repair, "outcome_seen") && Pending is null && f.Gold is >= 0 and var gold && ReserveOk(gold))
            { Change(Repair, ("status", "idle"), ("reserve_violated", false), ("post_gold", gold), ("due_at", Now + policy.IntervalSeconds)); State["reason"] = null; }
            State["paused"] = null; port.Resume(); Save();
    }
    public void RetryRepair()
    {
        lock (Gate)
        {
            if (Str(Repair, "status") == "unknown" && Bool(Repair, "outcome_seen") && S("reason") == "post_repair_gold_unknown" && Pending?["action"]?.GetValue<string>() == "confirm_repair")
            { Change(Repair, ("status", "running"), ("gold_reads", 0), ("last_capture", null)); Set(("active", "repair"), ("reason", null)); Save(); return; }
            if (Str(Repair, "status") != "blocked" || Bool(Repair, "outcome_seen")) return;
            if(Pending is null&&Str(Repair,"phase")=="equip")
            { Change(Repair,("status","running"));Set(("active","repair"),("reason",null));Save();return; }
            Change(Repair, ("status", "due"), ("phase", "idle"), ("quote_reads", 0), ("gold_reads", 0), ("last_capture", null), ("before_gold", null), ("quote", null)); State["reason"] = null; Save();
        }
    }
    public void ObserveBackpack(MaintenanceFrame f)
    {
        lock (Gate)
        {
            if (!policy.BackpackEnabled || !ValidWeight(f) || string.IsNullOrWhiteSpace(f.CaptureId) || f.CaptureId == Str(Bag, "last_capture")) return;
            Change(Bag, ("last_capture", f.CaptureId), ("weight", f.Weight), ("capacity", f.Capacity));
            Bag["capacity_blocked"]=Overweight(f);
            Bag["movement_blocked"]=f.Weight>=f.Capacity;
            Bag["checked_at"]=Now;Bag["check_due_at"]=Now+policy.BackpackCheckIntervalSeconds;
            if (Str(Bag, "status") == "production_relief" && !Overweight(f))
            { Change(Bag, ("phase", "idle"), ("status", "idle"), ("reads", 0)); if (S("active") == "backpack") State["active"] = null; if (S("reason") == "backpack_production_relief") State["reason"] = null; }
            else if (Str(Bag, "phase") == "idle" && Overweight(f)) Change(Bag, ("phase", "prepare"), ("status", "due"), ("reads", 0), ("tidy_checked", false), ("surplus_checked", false), ("cleanup_after_relief", false)); Save();
        }
    }
    private void Manual(string reason)
    {
        var local=Pending is null&&surplus?.Snapshot()["pending"] is null;
        Bag["status"] = local?"blocked":"manual_required";
        Set(("global_pause_required", !local), ("reason", reason));
        if(local){State["active"]=null;State["navigation_cleanup"]=!port.CanReleaseForeground();port.ResetNavigation();}
        Save();
    }
    private void ProductionRelief(string reason)
    {
        if (!policy.BackpackEnabled) { Manual(reason); return; }
        if(Bool(Bag,"movement_blocked"))
        {Change(Bag,("phase","prepare"),("status","due"),("cleanup_after_relief",true),("reads",0));Set(("active",null),("reason","backpack_full_navigation"));Save();return;}
        Change(Bag, ("phase", "relief"), ("status", "production_relief"), ("relief_reason", reason), ("reads", 0), ("outcome_seen", false), ("weight_checks", new JsonObject()));
        Set(("active", null), ("pending", null), ("global_pause_required", false), ("reason", "backpack_production_relief")); Save();
    }
    public void CheckProductionWeight(string actionId, int submitted)
    {
        lock (Gate)
        {
            Bag["weight_checks"] ??= new JsonObject(); var checks = Bag["weight_checks"]!;
            if (Str(Bag, "status") == "production_relief" && Pending is null && submitted > (checks[actionId]?.GetValue<int>() ?? 0))
            { if(Str(Bag,"phase")!="relief_weight")port.ResetNavigation(); checks[actionId] = submitted; Change(Bag, ("phase", "relief_weight"), ("reads", 0)); Save(); }
        }
    }
    public void ProductionReliefExhausted()
    {
        lock (Gate)
        {
            if (Str(Bag, "status") != "production_relief") return;
            if (surplus is not null && !Bool(Bag, "cleanup_after_relief")) { Change(Bag, ("status", "due"), ("phase", "prepare"), ("cleanup_after_relief", true), ("surplus_checked", false)); Set(("active", null), ("reason", "backpack_cleanup_after_production")); Save(); }
            else Manual("backpack_relief_exhausted");
        }
    }
    public bool TrySellWhileProductionSlotsBusy()
    {
        lock(Gate)
        {
            if(Str(Bag,"status")!="production_relief"||Pending is not null||surplus is null||!policy.SellSurplus||Bool(Bag,"surplus_checked")||!Bool(Bag,"tidy_checked"))return false;
            Change(Bag,("status","due"),("phase","prepare"),("cleanup_after_relief",true),("waiting_for_slots",true));
            Set(("active",null),("reason","backpack_sell_while_processing"));Save();return true;
        }
    }
    public void ProductionReliefAvailable()
    {
        lock (Gate)
        {
            if (!policy.BackpackEnabled || Pending is not null || surplus?.Snapshot()["pending"] is not null || B("global_pause_required") || Bool(Bag,"movement_blocked")) return;
            if (!(Bool(Bag,"cleanup_after_relief") && Str(Bag,"phase")=="prepare") && S("reason")!="backpack_relief_exhausted") return;
            // A new inventory plan can make production possible while the old
            // cleanup is still exiting. Keep that exit before releasing the turn.
            port.ResetNavigation();State["navigation_cleanup"]=!port.CanReleaseForeground();
            Bag["cleanup_after_relief"]=false;Bag["waiting_for_slots"]=false;ProductionRelief("production_materials_available");
        }
    }
    private void BlockRepair(string reason, bool unknown = false) { Repair["status"] = unknown ? "unknown" : "blocked"; Set(("active", Pending is not null ? "repair" : null), ("reason", reason)); if(Pending is null)State["navigation_cleanup"]=!port.CanReleaseForeground(); Save(); }
    private void Act(string action, MaintenanceFrame f, bool durable = false)
    {
        if (durable) { State["pending"] = new JsonObject { ["action"] = action, ["at"] = Now, ["capture_id"] = f.CaptureId, ["deadline"] = Now + timeout }; Save(); }
        try { port.Act(action); }
        catch(InputRejectedBeforeDispatchException)
        {
            if(durable)State["pending"]=null;
            if(action=="confirm_repair")Repair["phase"]="quote";
            if(action=="confirm_tidy")Bag["phase"]="confirm";
            State["reason"]="target_changed_before_input";Save();
        }
        catch (Exception error) { RememberFault(error,action); if (!durable) throw; State["reason"] = "action_outcome_unknown:" + error.GetType().Name; Save(); }
    }
    private static bool NewCapture(JsonObject record, MaintenanceFrame f) { if (Str(record, "last_capture") == f.CaptureId) return false; record["last_capture"] = f.CaptureId; return true; }
    private void CancelPeriodicBackpackCheck()
    {
        if (Str(Bag, "phase") != "inspect" || Pending is not null || surplus?.Snapshot()["pending"] is not null || Bool(Bag,"inspection_requested")) return;
        // 下一任务的周期限制只决定是否开始检查，已取得维护占用的检查继续完成。
        if(S("active")=="backpack"&&Str(Bag,"status")=="running"&&S("paused") is null)return;
        var inspectionBlocked = S("reason") is "backpack_weight_unknown" or "backpack_progress_timeout";
        if (B("global_pause_required") && !inspectionBlocked) return;
        Change(Bag, ("phase", "idle"), ("status", "idle"), ("reads", 0), ("last_capture", null));
        if (S("active") == "backpack")
        {
            State["active"] = null;
            State["navigation_cleanup"] = B("navigation_cleanup") || !port.CanReleaseForeground();
            port.ResetNavigation();
        }
        if (inspectionBlocked) Set(("global_pause_required", false), ("reason", null));
        Save();
    }
    public JsonObject Tick(bool safeToSwitch = true, bool allowPeriodicBackpackCheck = true, bool deferIdleObservation = false,bool allowRepair = true)
    {
        lock (Gate)
        {
            CancelDisabled(); if (policy.RepairEnabled && Str(Repair, "status") == "idle" && Now >= Repair["due_at"]!.GetFiniteDouble()) { Repair["status"] = "due"; Save(); }
            if (!allowPeriodicBackpackCheck) CancelPeriodicBackpackCheck();
            if(allowPeriodicBackpackCheck&&policy.BackpackEnabled&&policy.BackpackCheckIntervalSeconds>0&&(Str(Bag,"status")=="idle"||Str(Bag,"status")=="production_relief"&&Str(Bag,"phase")=="relief")&&Now>=Bag["check_due_at"]!.GetFiniteDouble())
            {Change(Bag,("phase","inspect"),("status","due"),("reads",0));Save();}
            // An owned native round must not pay for unrelated full-page recognition
            // or OCR. Outstanding maintenance still keeps its reconciliation path.
            if(deferIdleObservation&&!safeToSwitch&&Pending is null&&S("active") is null&&!resumeRequested)
                return StatusSnapshot();
            // Release only a non-consuming cleanup reservation, even if observation itself fails.
            if(B("navigation_cleanup")&&S("paused") is null&&!B("global_pause_required"))
            {
                cleanupStartedAt??=Now;
                if(Pending is null&&surplus?.Snapshot()["pending"] is null&&Now-cleanupStartedAt>=timeout)
                {State["navigation_cleanup"]=false;cleanupStartedAt=null;port.ResetNavigation();Save();return StatusSnapshot();}
            }
            var f = port.Observe();
            if(ReadyFrame(f)&&f.Page is "backpack" or "repair_tools"&&ValidWeight(f))Bag["capacity_blocked"]=Overweight(f);
            if(Pending is{} outstanding&&Now>=outstanding["deadline"]!.GetFiniteDouble())
            {
                if(S("active")=="repair"&&Str(Repair,"status")!="unknown")BlockRepair("repair_result_unknown",true);
                else if(S("active")=="backpack"&&!B("global_pause_required")&&!Bool(Bag,"outcome_seen")&&!(ReadyFrame(f)&&f.TidyComplete))Manual("tidy_result_unknown");
            }
            if (policy.BackpackEnabled && f.BackpackFull && ReadyFrame(f) && Pending is null && S("paused") is null && !B("global_pause_required"))
            {
                Bag["capacity_blocked"]=true;
                if (!Bool(Bag,"movement_blocked")&&Str(Bag, "status") is not ("due" or "running"))
                { if (S("active") == "repair") Repair["status"] = "due"; port.ResetNavigation(); Change(Bag, ("phase", "prepare"), ("status", "due"), ("reads", 0), ("outcome_seen", false), ("tidy_checked", false), ("surplus_checked", false), ("cleanup_after_relief", false)); Set(("active", null), ("reason", "backpack_full_navigation")); Save(); }
                Bag["movement_blocked"]=true;Save();
            }
            if (f.Page is "backpack" or "repair_tools" && Str(Bag, "phase") is not ("relief_weight" or "inspect")) ObserveBackpack(f);
            if (!f.Foreground) { Pause("focus_lost"); return StatusSnapshot(); }
            if (resumeRequested)
            {
                if (ReadyFrame(f)) ResumeFrame(f);
                else if (Now >= resumeDeadline) { resumeRequested = false; Set(("paused", "resume_observation_timeout"), ("reason", "resume_observation_timeout")); Save(); }
            }
            if (S("paused") == "focus_lost" && ReadyFrame(f)) { Resume(false); return StatusSnapshot(); }
            // A timed-out sale may finish later. Reconcile only its fresh visible result;
            // the manual gate must never erase the journal or prevent passive accounting.
            if (B("global_pause_required") && S("active") == "backpack" && Pending is { } pending
                && ReadyFrame(f) && f.CaptureId != pending["capture_id"]!.GetValue<string>())
            {
                if (f.TidyComplete) Bag["outcome_seen"] = true;
                if (Bool(Bag, "outcome_seen") && f.Page is "backpack" or "repair_tools" && ValidWeight(f))
                {
                    BagStep(f,false);
                    if (Pending is null) State["global_pause_required"] = false;
                }
                Save(); return StatusSnapshot();
            }
            if (S("paused") is not null || B("global_pause_required")) return StatusSnapshot();
            if(policy.BackpackEnabled&&safeToSwitch&&ReadyFrame(f)&&f.Page=="tidy"&&Pending is null&&surplus?.Snapshot()["pending"] is null&&Str(Bag,"phase") is "idle" or "inspect" or "relief" or "relief_weight")
            {
                if(S("active")=="repair")Repair["status"]="due";
                port.ResetNavigation();
                Change(Bag,("phase","prepare"),("status","running"),("reads",0),("tidy_checked",false),("surplus_checked",false),("cleanup_after_relief",false));
                Set(("active","backpack"),("navigation_cleanup",false),("reason",null));Save();
            }
            if(S("active") is{} active)
            {
                var key=active+":"+Str(State[active]!.AsObject(),"phase")+":"+f.Page;
                if(key!=waitKey){waitKey=key;waitAt=Now;}
                // Surplus owns a bounded 300-second search/settlement window.
                // Do not interrupt that child workflow with the ordinary page wait.
                else if(Pending is null&&Now-waitAt>=(active=="backpack"&&Str(Bag,"phase")=="surplus"?330:120))
                {if(active=="repair"){BlockRepair("repair_progress_timeout");port.ResetNavigation();}else Manual("backpack_progress_timeout");return StatusSnapshot();}
            }else waitKey=null;
            if(B("navigation_cleanup"))
            {
                // A failed, non-consuming inspection must not reserve the character
                // forever while its exit page is temporarily unrecognizable.
                if(!safeToSwitch||!ReadyFrame(f)||!port.ReleaseNavigation())return StatusSnapshot();
                State["navigation_cleanup"]=false;cleanupStartedAt=null;port.ResetNavigation();Save();return StatusSnapshot();
            }
            cleanupStartedAt=null;
            if(!ReadyFrame(f))return StatusSnapshot();
            if (S("active") is null)
            {
                if (!safeToSwitch) return StatusSnapshot();
                if (policy.BackpackEnabled && (Str(Bag, "status") == "due" || Str(Bag,"status")=="production_relief"&&Str(Bag,"phase")=="relief_weight"))
                { State["active"] = "backpack"; if(Str(Bag,"status")=="due")Bag["status"] = "running"; }
                else if (allowRepair && Str(Bag, "status") != "production_relief" && policy.RepairEnabled && Str(Repair, "status") == "due")
                { State["active"] = "repair"; Change(Repair, ("status", "running"), ("phase", "quote"), ("quote_reads", 0), ("gold_reads", 0), ("last_capture", null), ("before_gold", null), ("quote", null), ("post_gold", null), ("outcome_seen", false), ("reserve_violated", false)); }
                else return StatusSnapshot(); Save();
            }
            if (!safeToSwitch) return StatusSnapshot(); port.Resume(); if (S("active") == "backpack") BagStep(f); else RepairStep(f); Save(); return StatusSnapshot();
        }
    }
    private void NavigateRepair(string destination) { if (port.Navigate(destination) is { } reason) BlockRepair(reason); }
    private void NavigateBag(string destination) { if (port.Navigate(destination) is { } reason) Manual(reason); }
    public void ObserveOnly()
    {
        lock(Gate)
        {
            surplus?.ObserveOnly();
            if(Pending is null)return;
            var f=port.Observe();if(!ReadyFrame(f))return;
            if(S("active")=="repair")RepairStep(f,false);
            else if(S("active")=="backpack")
            {BagStep(f,false);if(Pending is null&&S("reason") is null)State["global_pause_required"]=false;}
            Save();
        }
    }
    private void RepairStep(MaintenanceFrame f,bool allowInput=true)
    {
        if (Pending is { } pending)
        {
            if (f.CaptureId == pending["capture_id"]!.GetValue<string>()) return;
            if (pending["action"]!.GetValue<string>() == "auto_equip")
            {
                if (f.ToolsEquipped) { State["pending"] = null; Repair.Remove("gold_sync_attempted"); Change(Repair, ("phase", "idle"), ("status", "idle"), ("due_at", Now + policy.IntervalSeconds)); Set(("active", null), ("reason", null), ("navigation_cleanup", f.Page!="scene")); if (Bool(Repair, "reserve_violated")) BlockRepair("repair_reserve_violated"); }
                else if (Now >= pending["deadline"]!.GetFiniteDouble()) BlockRepair("auto_equip_result_unknown", true); return;
            }
            var before = Repair["before_gold"]?.GetValue<long>(); var quote = Repair["quote"]?.GetValue<long>();
            var paymentSeen = !Bool(Repair,"quote_is_upper_bound") && f.Page is "backpack" or "repair_tools" && f.Gold >= 0 && before >= 0 && quote > 0 && f.Gold == before - quote;
            if (f.RepairComplete || paymentSeen) Repair["outcome_seen"] = true;
            if (!Bool(Repair, "outcome_seen")) { if (Now >= pending["deadline"]!.GetFiniteDouble()) BlockRepair("repair_result_unknown", true); return; }
            if (f.Gold is null && f.Page is not ("backpack" or "repair_tools")) { if (allowInput&&Str(Repair, "status") == "running") port.Navigate("backpack_weight"); return; }
            if (Str(Repair, "status") == "unknown" && f.Gold is null) return;
            if (NewCapture(Repair, f))
            {
                Repair["gold_reads"] = Int(Repair, "gold_reads") + 1;
                if (f.Gold is >= 0 and var gold)
                {
                    var spent=before is {} balance&&balance>=gold?balance-gold:(long?)null;
                    if(quote is null||spent is null||spent<=0||spent>quote){BlockRepair("repair_payment_amount_mismatch",true);return;}
                    Change(Repair, ("post_gold", gold), ("actual_spent", spent), ("reserve_violated", !ReserveOk(gold)), ("phase", "equip"), ("status", "running")); Set(("pending", null), ("reason", null));
                }
                else if (Int(Repair, "gold_reads") >= policy.ReadAttempts) BlockRepair("post_repair_gold_unknown", true);
            }
            return;
        }
        if(!allowInput)return;
        if (f.RepairNotNeeded && f.Page is "repair_dialog" or "repair_equipment") Change(Repair, ("phase", "equip"), ("status", "running"));
        if (Str(Repair, "phase") == "equip") { if (f.Page != "repair_tools") NavigateRepair("repair_tools"); else Act("auto_equip", f, true); return; }
        if (f.Page != "repair_quote") { NavigateRepair("repair_quote"); return; }
        if (!NewCapture(Repair, f)) return; Repair["quote_reads"] = Int(Repair, "quote_reads") + 1;
        Repair["before_gold"] = f.Gold >= 0 ? f.Gold : null;
        Repair["quote"] = f.Quote >= 0 && f.Currency == "gold" ? f.Quote : null;
        Repair["quote_is_upper_bound"] = f.QuoteIsUpperBound;
        if (f.Currency != "gold") { if (Int(Repair, "quote_reads") >= policy.ReadAttempts) BlockRepair("repair_currency_unknown"); return; }
        var g = Repair["before_gold"]?.GetValue<long>(); var q = Repair["quote"]?.GetValue<long>();
        Repair["gold_reserve"]=Math.Max(40000,policy.GoldReserve);
        if (g is not null && (g<40000||!ReserveOk(g.Value) || q is not null && (g-q<40000||!ReserveOk(g.Value - q.Value)))) { BlockRepair("repair_reserve_insufficient"); return; }
        if (q is null || g is null)
        { if (Int(Repair,"quote_reads")>=policy.ReadAttempts) BlockRepair(g is null?"repair_gold_unknown":"repair_quote_unknown"); return; }
        if(q==0){Change(Repair,("phase","equip"),("status","running"),("actual_spent",0L));Save();return;}
        if (!f.ConfirmAvailable) return;
        Change(Repair, ("phase", "result"), ("gold_reads", 0), ("outcome_seen", false), ("post_gold", null), ("reserve_violated", false)); Act("confirm_repair", f, true);
    }
    private void BagStep(MaintenanceFrame f,bool allowInput=true)
    {
        if(!allowInput&&Pending is null)return;
        if(Pending is null&&Str(Bag,"phase")=="inspect")
        {
            if(f.Page is not ("backpack" or "repair_tools")||f.WeightTabRequired){NavigateBag("backpack_weight");return;}
            if(!NewCapture(Bag,f))return;
            if(!ValidWeight(f)){Bag["reads"]=Int(Bag,"reads")+1;if(Int(Bag,"reads")>=policy.ReadAttempts)Manual("backpack_weight_unknown");return;}
            Change(Bag,("movement_blocked",f.Weight>=f.Capacity),("capacity_blocked",Overweight(f)),("checked_at",Now),("check_due_at",Now+policy.BackpackCheckIntervalSeconds),("weight",f.Weight),("capacity",f.Capacity),("reads",0),("inspection_requested",false));
            if(Overweight(f))Change(Bag,("phase","prepare"),("tidy_checked",false),("surplus_checked",false),("cleanup_after_relief",false));
            else {Change(Bag,("phase","idle"),("status","idle"));Set(("active",null),("navigation_cleanup",f.Page!="scene"));}
            return;
        }
        // Open the game's protected tidy flow before considering individual
        // materials. A completed/empty tidy is remembered for this cleanup only.
        if (Pending is null && Str(Bag, "phase") == "prepare" && Bool(Bag,"tidy_checked") && !Bool(Bag, "cleanup_after_relief") && (surplus is not null||policy.ProductionFirst)) { ProductionRelief("backpack_after_tidy"); return; }
        if (Pending is null && Str(Bag, "phase") == "surplus")
        { var status = surplus!.Tick(); if (status == "unknown") Manual("surplus_result_unknown"); else if (status == "complete") Change(Bag, ("phase", "prepare"), ("reads", 0), ("surplus_checked", true)); return; }
        if (Pending is null && Str(Bag, "phase") == "prepare" && Bool(Bag,"tidy_checked") && !Bool(Bag, "surplus_checked") && policy.SellSurplus && surplus is not null)
        { var status = surplus.Begin(); Bag["surplus_checked"] = true; if (status == "unknown") Manual("surplus_result_unknown"); else Bag["phase"] = "surplus"; return; }
        if(Pending is null&&Str(Bag,"phase")=="prepare"&&Bool(Bag,"surplus_checked")&&ValidWeight(f)&&!Overweight(f))
        {Change(Bag,("weight",f.Weight),("capacity",f.Capacity),("phase","idle"),("status","idle"),("reads",0),("waiting_for_slots",false));Set(("active",null),("reason",null),("navigation_cleanup",f.Page!="scene"));return;}
        if(Pending is null&&Str(Bag,"phase")=="prepare"&&Bool(Bag,"tidy_checked")&&Bool(Bag,"cleanup_after_relief"))
        {
            if(f.Page is not ("backpack" or "repair_tools")||f.WeightTabRequired){NavigateBag("backpack_weight");return;}
            if(!ValidWeight(f)){Bag["reads"]=Int(Bag,"reads")+1;if(Int(Bag,"reads")>=policy.ReadAttempts)Manual("post_tidy_weight_unknown");return;}
            if(Overweight(f)&&Bool(Bag,"waiting_for_slots"))
            {Change(Bag,("weight",f.Weight),("capacity",f.Capacity),("phase","relief"),("status","production_relief"),("waiting_for_slots",false));Set(("active",null),("reason","backpack_production_relief"),("navigation_cleanup",f.Page!="scene"));}
            else if(Overweight(f))Manual("backpack_relief_exhausted");
            else {Change(Bag,("weight",f.Weight),("capacity",f.Capacity),("phase","idle"),("status","idle"),("reads",0),("waiting_for_slots",false));Set(("active",null),("reason",null),("navigation_cleanup",true));}
            return;
        }
        if (Str(Bag, "phase") == "relief_weight" && Pending is null)
        {
            if (f.Page is not ("backpack" or "repair_tools")||f.WeightTabRequired) NavigateBag("backpack_weight");
            else if (NewCapture(Bag, f))
            {
                if (ValidWeight(f)&&f.Weight>=f.Capacity)
                {Change(Bag,("weight",f.Weight),("capacity",f.Capacity),("movement_blocked",true),("capacity_blocked",true),("phase","prepare"),("status","running"),("tidy_checked",false),("surplus_checked",false),("cleanup_after_relief",true),("reads",0));Set(("reason","backpack_full_navigation"));}
                else if (ValidWeight(f)) { var over = Overweight(f); port.ResetNavigation(); Change(Bag, ("checked_at",Now),("check_due_at",Now+policy.BackpackCheckIntervalSeconds),("movement_blocked",false),("capacity_blocked",over),("weight", f.Weight), ("capacity", f.Capacity), ("reads", 0), ("phase", over ? "relief" : "idle"), ("status", over ? "production_relief" : "idle")); Set(("active", null), ("reason", over ? "backpack_production_relief" : null), ("navigation_cleanup", f.Page!="scene")); }
                else { Bag["reads"] = Int(Bag, "reads") + 1; if (Int(Bag, "reads") >= policy.ReadAttempts) Manual("post_production_weight_unknown"); }
            }
            return;
        }
        if (Pending is { } pending)
        {
            if (f.CaptureId == pending["capture_id"]!.GetValue<string>()) return; if (f.TidyComplete) Bag["outcome_seen"] = true;
            if (!Bool(Bag, "outcome_seen")) { if (Now >= pending["deadline"]!.GetFiniteDouble()) Manual("tidy_result_unknown"); return; }
            if (f.Page is not ("backpack" or "repair_tools")||f.WeightTabRequired) { if(allowInput)NavigateBag("backpack_weight"); return; }
            if (ValidWeight(f))
            { Change(Bag, ("weight", f.Weight), ("capacity", f.Capacity),("tidy_checked",true),("checked_at",Now),("check_due_at",Now+policy.BackpackCheckIntervalSeconds)); State["pending"] = null; if (Overweight(f)) ProductionRelief("backpack_still_overweight"); else { Change(Bag, ("phase", "idle"), ("status", "idle"), ("outcome_seen", false)); Set(("active", null), ("reason", null),("navigation_cleanup",f.Page!="scene")); } }
            else if (NewCapture(Bag, f)) { Bag["reads"] = Int(Bag, "reads") + 1; if (Int(Bag, "reads") >= policy.ReadAttempts) Manual("post_tidy_weight_unknown"); } return;
        }
        if (Str(Bag, "phase") == "confirm")
        { if (f.Page == "tidy_confirm" && f.ConfirmAvailable) { Change(Bag, ("phase", "verify"), ("reads", 0), ("outcome_seen", false)); Act("confirm_tidy", f, true); } return; }
        if (f.Page != "tidy") { NavigateBag("backpack_tidy"); return; } if (!NewCapture(Bag, f)) return;
        if (f.BoldSelected == true) { Bag["reads"] = 0; Act("disable_bold", f); }
        else if (f.BoldSelected == false && f.MaterialsSelected == true) { Bag["reads"] = 0; Act("deselect_materials", f); }
        else if (f.BoldSelected == false && f.MaterialsSelected == false)
        {
            if (f.TidyCount == 0) {Bag["tidy_checked"]=true;ProductionRelief("nothing_to_tidy");}
            else if (f.TidyCount > 0) { Bag["phase"] = "confirm"; Save(); Act("prepare_tidy", f); }
            else { Bag["reads"] = Int(Bag, "reads") + 1; if (Int(Bag, "reads") >= policy.ReadAttempts) Manual("tidy_count_unknown"); }
        }
        else { Bag["reads"] = Int(Bag, "reads") + 1; if (Int(Bag, "reads") >= policy.ReadAttempts) Manual("tidy_options_unknown"); }
    }
}


