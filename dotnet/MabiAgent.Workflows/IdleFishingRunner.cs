using MabiAgent.Core;
using System.Diagnostics;
using System.Text.Json.Nodes;
namespace MabiAgent.Workflows;
public sealed record FishingPlan(string PointKey, double DurationSeconds)
{
    public const string CurrentLocation="current_location";
    public bool InPlace=>PointKey==CurrentLocation;
}
public sealed record FishingFrame(double At, string CaptureId, string Page = "scene", bool Foreground = true, bool Stable = true,
    bool? NavigationActive = null, bool FishingButton = false, bool? LeisureMode = null, bool ModeConfirm = false, bool RoundActive = false,
    bool HookVisible = false, double? Gauge = null, bool Compass = false, bool Mounted = false, bool PreparationComplete = true);
public sealed record FishingAction(string Kind, string? PointKey = null, string? Direction = null, double? DurationSeconds = null);
public interface IFishingPort : IForegroundWorkflowPort
{
    FishingFrame Observe();
    FishingFrame ObserveOnly() => Observe();
    void InvalidatePreparation(){}
    bool CanResumeAtPoint=>false;
    /// <summary>Actions share system input boundaries; casting uses the fixed movement script.</summary>
    void Perform(FishingAction action);
    void MoveWhileObserving(FishingAction action,Func<FishingFrame,bool> observe)
    {Perform(action);for(var i=0;i<2;i++)if(observe(Observe()))break;}
}
public sealed class IdleFishingRunner : PersistentWorkflow
{
    private readonly FishingPlan plan; private readonly IFishingPort port;
    private readonly double navigationTimeout, actionTimeout, castObservationTimeout, reboundThreshold, lowWatermark;
    private double? lastAt; private string? lastCapture; private bool wasActive, castWaitActive;
    public bool SafeToSwitch => S("status") is "complete" or "yielded" || S("status") == "paused" && Pending is null && !B("round_seen") && S("phase") == "refresh";
    public bool CanIsolateFault => Pending is null && !B("round_seen") && !B("navigation_seen") && port.CanReleaseForeground();
    public IdleFishingRunner(FishingPlan plan, IFishingPort port, IWorkflowStore store, double navigationTimeoutSeconds = 180,
        double actionTimeoutSeconds = 15, double reboundThreshold = .12, double lowWatermark = .30, TimeProvider? time = null,
        double castObservationTimeoutSeconds = 20) : base(store, time, new JsonObject())
    {
        if (string.IsNullOrWhiteSpace(plan.PointKey)) throw new ArgumentException("钓鱼点无效");
        ValidatePositive(plan.DurationSeconds, nameof(plan)); ValidatePositive(navigationTimeoutSeconds, nameof(navigationTimeoutSeconds)); ValidatePositive(actionTimeoutSeconds, nameof(actionTimeoutSeconds)); ValidatePositive(castObservationTimeoutSeconds, nameof(castObservationTimeoutSeconds)); ValidatePositive(reboundThreshold, nameof(reboundThreshold));
        if (!double.IsFinite(lowWatermark) || lowWatermark is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(lowWatermark));
        this.plan = plan; this.port = port; navigationTimeout = navigationTimeoutSeconds; actionTimeout = actionTimeoutSeconds; castObservationTimeout = castObservationTimeoutSeconds; this.reboundThreshold = reboundThreshold; this.lowWatermark = lowWatermark;
        var serialized = new JsonObject { ["point_key"] = plan.PointKey, ["duration_s"] = plan.DurationSeconds };
        Set(("schema_version", 1), ("plan", serialized), ("status", "running"), ("phase", "navigate"), ("elapsed_s", 0d),
            ("target_s", plan.DurationSeconds), ("pending", null), ("owned_slots", new JsonArray()), ("submitted", 0), ("completed", 0),
            ("pause_reason", null), ("reason", null), ("navigation_seen", false), ("navigation_deadline", null), ("next_direction", "w"),
            ("last_move_at", null), ("sample_at", null), ("gauge_min", null), ("round_seen", false), ("yield_requested", false), ("stop_requested", false), ("hook_seen", false),
            ("cast_preflight_rejections", 0), ("last_cast_elapsed_ms", null), ("unobserved_cast_attempts", 0), ("last_unobserved_cast", null));
        if(plan.InPlace)State["phase"]="refresh";
        if (Load() is { } saved)
        {
            if (saved["schema_version"]?.GetValue<int>() != 1 || !JsonNode.DeepEquals(saved["plan"], serialized) || saved["target_s"]?.GetFiniteDouble() != plan.DurationSeconds || saved["owned_slots"] is not JsonArray { Count: 0 } || saved["elapsed_s"]?.GetFiniteDouble() is not { } elapsed || !double.IsFinite(elapsed) || elapsed < 0) throw new InvalidDataException("钓鱼记录不匹配，保留待核对");
            State = saved; if (I("completed") < 0 || I("completed") > I("submitted")) throw new InvalidDataException("钓鱼计数无效");
            if (S("next_direction") is "left" or "a") State["next_direction"] = "w";
            if (S("next_direction") is "right" or "d") State["next_direction"] = "s";
            if (S("next_direction") is not ("w" or "s")) throw new InvalidDataException("钓鱼移动方向无效");
            if (S("status") is not ("complete" or "yielded")) Set(("status", "paused"), ("pause_reason", "restart"));
        }
        Save();
    }
    private void ResetClock() { lastAt = null; wasActive = false; castWaitActive = false; }
    public void Pause(string reason = "manual")
    {
        port.Pause(reason); lock (Gate) { if (S("status") == "complete" || S("pause_reason") == "manual" && reason == "focus_lost") return; Set(("status", "paused"), ("pause_reason", reason)); ResetClock(); Save(); }
    }
    public void Resume(bool explicitRequest = true)
    {
        lock (Gate)
        {
            if (S("status") == "complete" || !explicitRequest && S("pause_reason") != "focus_lost") return;
            if (S("status") == "yielded") {port.InvalidatePreparation();Set(("phase", plan.InPlace||port.CanResumeAtPoint?"refresh":"navigate"), ("navigation_seen", false), ("navigation_deadline", null), ("round_seen", false), ("yield_requested", false));}
            // Recreate only reversible menu navigation after explicit resume/restart.
            // A witnessed native trip or unresolved fishing action remains observation-only.
            if(explicitRequest&&S("phase")=="navigate"&&!B("navigation_seen")&&Pending is null)State["navigation_deadline"]=null;
            if(explicitRequest&&Pending is null)State["cast_preflight_rejections"]=0;
            Set(("status", "running"), ("pause_reason", null), ("reason", null)); ResetClock(); ResetPageWait(); port.Resume(); Save();
        }
    }
    public void StopSubmitting() { lock (Gate) { State["stop_requested"] = true; Save(); } }
    public void RequestYield(bool requested=true) { lock (Gate) { if(B("yield_requested")==requested)return;State["yield_requested"] = requested; Save(); } }
    private void Unknown(string reason) { Set(("status", "unknown"), ("reason", reason)); ResetClock(); Save(); }
    private static bool FishingUi(FishingFrame f) => f.Page is "fishing" or "fishing_ui";
    private static bool Focused(FishingFrame f) => FishingUi(f) && f.LeisureMode == false;
    private static bool RoundEnded(FishingFrame f) => !f.RoundActive && !f.ModeConfirm && !f.HookVisible && f.Gauge is null && (f.Compass || f.FishingButton);
    private bool Perform(FishingAction action, FishingFrame f, bool pending = false)
    {
        if (pending) State["pending"] = new JsonObject { ["kind"] = action.Kind, ["at"] = f.At, ["capture_id"] = f.CaptureId, ["deadline"] = f.At + (action.Kind == "start" ? castObservationTimeout : actionTimeout) };
        Save();
        try { port.Perform(action); return true; }
        catch (InputRejectedBeforeDispatchException) { Set(("pending", null), ("status", "running"), ("reason", action.Kind=="start"&&I("cast_preflight_rejections")>=2?"cast_preflight_retry_limit":"target_changed_before_input")); if(action.Kind=="start"){State["phase"]="refresh";State["cast_preflight_rejections"]=I("cast_preflight_rejections")+1;}if(action.Kind=="navigate")State["navigation_deadline"]=null;Save();return false; }
        catch (Exception error) { RememberFault(error,action.Kind); Unknown(pending ? "action_result_not_observed" : "input_interrupted"); throw; }
    }
    private void FinishRound()
    {
            Set(("completed", I("completed") + 1), ("pending", null), ("phase", "refresh"), ("round_seen", false), ("cast_preflight_rejections", 0),
            ("gauge_min", null), ("sample_at", null), ("hook_seen", false), ("status", "running"), ("reason", null)); wasActive = false;
    }
    private bool ReconcilePending(FishingFrame f, bool passive = false)
    {
        if (Pending is not { } pending) return true; var kind = pending["kind"]!.GetValue<string>(); var confirmed = false;
        if (f.CaptureId == pending["capture_id"]!.GetValue<string>() || f.At <= pending["at"]!.GetFiniteDouble()) return false;
        switch (kind)
        {
            case "start":
                if (f.RoundActive)
                {
                    if (Focused(f)) State["elapsed_s"] = D("elapsed_s") + (pending["observed_wait_s"]?.GetFiniteDouble() ?? 0);
                    Set(("submitted", I("submitted") + 1), ("round_seen", true), ("phase", "round")); confirmed = true;
                }
                break;
            case "switch_mode": confirmed = f.ModeConfirm || Focused(f); break;
            case "confirm_mode": confirmed = Focused(f); break;
            case "reel": if (RoundEnded(f)) { FinishRound(); return true; } break;
        }
        if (confirmed) { Set(("pending", null), ("status", "running"), ("reason", null)); return true; }
        // The user authorizes a fresh movement/cast after twenty seconds without
        // a round. Observe first so a hook on this frame takes priority over retry.
        if (!passive && f.At >= PendingDeadline(pending))
        {
            if (kind == "start" && !f.RoundActive && !f.HookVisible && f.Gauge is null && !f.ModeConfirm
                && f.Page is "scene" or "fishing" or "fishing_ui" && !f.Mounted)
            {
                var attempt=pending.DeepClone().AsObject();attempt["ended_at"]=f.At;attempt["reason"]="no_round_after_20s";
                Set(("last_unobserved_cast",attempt),("unobserved_cast_attempts",I("unobserved_cast_attempts")+1),
                    ("pending",null),("phase","refresh"),("reason","cast_retry_no_response"));
                ResetClock();Save();return true;
            }
            if(kind!="start")Unknown(kind + "_result_not_observed");
        }
        return false;
    }
    private double PendingDeadline(JsonObject pending) => pending["kind"]?.GetValue<string>() == "start"
        ? pending["at"]!.GetFiniteDouble() + castObservationTimeout : pending["deadline"]!.GetFiniteDouble();
    private bool Boundary(FishingFrame f, bool roundCompleted = false)
    {
        if (D("elapsed_s") >= D("target_s") || B("stop_requested")) Set(("status", "complete"), ("phase", "complete"), ("pause_reason", null));
        else if (B("yield_requested")) Set(("status", "yielded"), ("phase", "yielded"), ("pause_reason", null));
        else return false; ResetClock(); if(roundCompleted&&!plan.InPlace||FishingUi(f)||B("navigation_seen")||B("round_seen"))Perform(new("leave"), f);return true;
    }
    public JsonObject Tick(bool higherPriority = false)
    { lock (Gate) { if (higherPriority) State["yield_requested"] = true; Step(); return Snapshot(); } }
    public void ObserveOnly()
    {
        lock (Gate)
        {
            if (S("status") is "complete" or "yielded") return;
            if (Pending is null && !B("round_seen") && !(S("phase") == "navigate" && B("navigation_seen"))) return;
            var f = port.ObserveOnly();
            if (!double.IsFinite(f.At) || string.IsNullOrWhiteSpace(f.CaptureId) || !Ready(f.Page, f.Foreground, f.Stable)) return;
            var reason = S("pause_reason") ?? "fishing_action_confirmed";
            if (Pending is not null) ReconcilePending(f, true);
            if (Pending is null && B("round_seen") && RoundEnded(f)) FinishRound();
            else if (S("phase") == "navigate" && B("navigation_seen") && f.NavigationActive == false) Set(("phase", "refresh"), ("navigation_seen", false), ("navigation_deadline", null), ("reason", null));
            if (Pending is null && S("phase") is "round" or "refresh")
                Set(("status", B("stop_requested") && !B("round_seen") ? "complete" : "paused"), ("pause_reason", reason));
            ResetClock(); Save();
        }
    }
    private void Step()
    {
        if (S("status") is "complete" or "yielded") { Save(); return; }
        var f = port.Observe(); if (!double.IsFinite(f.At) || string.IsNullOrWhiteSpace(f.CaptureId)) throw new ArgumentException("需要新截图与时间");
        if (!f.Foreground) { if (S("status") != "paused" || S("pause_reason") == "focus_lost") Pause("focus_lost"); return; }
        if (!PageReady(f.Page, f.Foreground, f.Stable, Pause, pauseOnUnrecognized: false))
        {
            if(S("status") == "running" && Pending is {} waiting && waiting["kind"]?.GetValue<string>() != "start" && f.At >= PendingDeadline(waiting))Unknown(waiting["kind"]!.GetValue<string>() + "_result_not_observed");
            else if(S("status") == "running" && S("phase") == "navigate" && State["navigation_deadline"] is not null && f.At >= D("navigation_deadline"))Unknown("navigation_not_observed");
            ResetClock(); Save(); return;
        }
        if (S("status") == "paused") { ObserveOnly(); return; }
        if (S("status") == "unknown") { ObserveOnly(); return; }
        if (lastCapture == f.CaptureId) return; lastCapture = f.CaptureId;
        var active = f.RoundActive && Focused(f) && S("status") != "unknown";
        var castWait = Pending?["kind"]?.GetValue<string>() == "start" && (FishingUi(f) || f.Page == "scene") && f.LeisureMode != true && S("status") != "unknown";
        if (castWait && castWaitActive && lastAt is not null) Pending!["observed_wait_s"] = (Pending["observed_wait_s"]?.GetFiniteDouble() ?? 0) + Math.Max(0, f.At - lastAt.Value);
        castWaitActive = castWait; if (active && wasActive && lastAt is not null) State["elapsed_s"] = D("elapsed_s") + Math.Max(0, f.At - lastAt.Value);
        lastAt = f.At; wasActive = active; var completedBefore=I("completed");
        if (!ReconcilePending(f)) { Save(); return; }
        if(I("completed")!=completedBefore){Boundary(f, roundCompleted: true);Save();return;}
        if(!f.RoundActive&&!f.ModeConfirm&&!B("round_seen")&&Pending is null&&Boundary(f)){Save();return;}
        if (S("phase") == "navigate")
        {
            if (Boundary(f)) return;
            if (State["navigation_deadline"] is null) { State["navigation_deadline"] = f.At + navigationTimeout; Perform(new("navigate", plan.PointKey), f); }
            else if (f.NavigationActive == false) Set(("phase", "refresh"), ("navigation_seen", false), ("navigation_deadline", null), ("status", "running"), ("reason", null));
            else if (f.At >= D("navigation_deadline")) Unknown("navigation_not_observed");
            else if (f.NavigationActive == true) State["navigation_seen"] = true; Save(); return;
        }
        // Refresh/re-entry can move the character without ending this runner. Re-check
        // the port's existing point proof only at this declared boundary. A sent action
        // was reconciled above and returned before this probe, so it cannot be replayed.
        if (S("phase") == "refresh" && !port.CanResumeAtPoint)
        {
            Set(("phase", "navigate"), ("navigation_seen", false), ("navigation_deadline", null), ("reason", null));
            Save();
            return;
        }
        if (B("round_seen") && RoundEnded(f)) { FinishRound(); Boundary(f, roundCompleted: true); Save(); return; }
        if (!B("round_seen") && Boundary(f)) return;
        if(!B("round_seen")&&Pending is null)
        {
            if(f.Mounted)
            {
                if(State["dismount_at"] is null){if(Perform(new("dismount"),f))State["dismount_at"]=f.At;}
                else if(f.At-D("dismount_at")>=actionTimeout)Unknown("dismount_not_observed");
                Save();return;
            }
            // The mount control can disappear before the dismount animation
            // releases movement. Keep the fixed script out of that transition.
            if(State["dismount_at"] is not null&&f.At-D("dismount_at")<1){Save();return;}
            State["dismount_at"]=null;
        }
        if (f.ModeConfirm && B("round_seen")) { Perform(new("confirm_mode"), f, true); return; }
        if (f.RoundActive)
        {
            if (!B("round_seen")) { Unknown("unowned_fishing_round"); return; }
            if (f.ModeConfirm) Perform(new("confirm_mode"), f, true);
            else if (FishingUi(f) && f.LeisureMode == true) Perform(new("switch_mode"), f, true);
            else if (Focused(f)) { State["phase"] = "round"; if(f.HookVisible)State["hook_seen"]=true; if(f.HookVisible||f.Gauge is not null)SampleGauge(f); }
            Save(); return;
        }
        if (S("status") == "unknown") { Save(); return; }
        if(B("round_seen")){Save();return;}
        if(I("cast_preflight_rejections")>=3)throw new TimeoutException("抛竿前检查连续三次未通过，请核对任务提示后继续");
        if (f.FishingButton) StartCast(f);
        else if (f.Compass && (State["last_move_at"] is null || f.At - D("last_move_at") >= .25))
        {
            var direction = S("next_direction")!; Set(("next_direction", direction == "w" ? "s" : "w"), ("last_move_at", f.At), ("phase", "refresh"));
            try
            {
                var prior=f;
                port.MoveWhileObserving(new("move",Direction:direction,DurationSeconds:.06),current=>
                {
                    if(!double.IsFinite(current.At)||current.At<=prior.At||current.CaptureId==prior.CaptureId||!current.Foreground)return true;
                    prior=current;
                    if(!current.Stable)return false;
                    if(current.RoundActive||current.ModeConfirm||current.Mounted||current.Page is not ("scene" or "fishing" or "fishing_ui"))return true;
                    if(FishingUi(current)&&current.FishingButton){StartCast(current);return true;}
                    return false;
                });
            }
            catch(InputRejectedBeforeDispatchException){State["reason"]="target_changed_before_input";}
            catch(Exception error){RememberFault(error,"move");Unknown("input_interrupted");throw;}
        }
        Save();
    }
    private void StartCast(FishingFrame f)
    {
        if(I("cast_preflight_rejections")>=3)return;
        var elapsed=Stopwatch.StartNew();State["phase"]="confirm_start";Perform(new("start"),f,true);
        State["last_cast_elapsed_ms"]=(long)Math.Ceiling(elapsed.Elapsed.TotalMilliseconds);
    }
    private void SampleGauge(FishingFrame f)
    {
        if (f.Gauge is not { } gauge || !double.IsFinite(gauge) || gauge is < 0 or > 1) return;
        var oldMin = State["gauge_min"]?.GetFiniteDouble(); if (oldMin is null || gauge < oldMin) State["gauge_min"] = gauge;
        if (State["sample_at"] is not null && f.At - D("sample_at") < .1) return; State["sample_at"] = f.At;
        if (oldMin <= lowWatermark && gauge - oldMin >= reboundThreshold) { State["phase"] = "confirm_reel"; Perform(new("reel"), f, true); }
    }
}

