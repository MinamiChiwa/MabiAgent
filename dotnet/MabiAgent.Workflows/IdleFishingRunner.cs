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
    bool HookVisible = false, double? Gauge = null, bool Compass = false, bool Mounted = false, bool PreparationComplete = true, bool BackpackFull = false, bool LeisureConfirm = false);
public sealed record FishingAction(string Kind, string? PointKey = null, string? Direction = null, double? DurationSeconds = null);
public interface IFishingPort : IForegroundWorkflowPort
{
    FishingFrame Observe();
    FishingFrame ObserveOnly() => Observe();
    bool NavigationIssued=>false;
    void AbandonUnsentNavigation(){}
    void InvalidatePreparation(){}
    void ReleaseBackpack(){}
    bool CanResumeAtPoint=>false;
    bool ModePreparationRequired=>false;
    /// <summary>Actions share system input boundaries; movement is only needed to activate or recover casting.</summary>
    void Perform(FishingAction action);
    void MoveWhileObserving(FishingAction action,Func<FishingFrame,bool> observe)
    {Perform(action);for(var i=0;i<2;i++)if(observe(Observe()))break;}
}
public sealed class IdleFishingRunner : PersistentWorkflow
{
    private readonly FishingPlan plan; private readonly IFishingPort port;
    private readonly double navigationTimeout, actionTimeout, castObservationTimeout, reboundThreshold, lowWatermark;
    private double? lastAt; private string? lastCapture;
    public bool NeedsBackpackCheck => B("backpack_check_requested");
    public bool NeedsRapidObservation => S("status")=="running" && (B("round_seen") || Pending?["kind"]?.GetValue<string>() is "start" or "reel");
    public bool PeriodicBackpackCheckAllowed => I("completed") - I("inspection_round") >= 20;
    public bool SafeToSwitch => S("status") is "complete" or "yielded" || S("status") == "paused" && Pending is null && !B("round_seen") && S("phase") == "refresh";
    public bool CanIsolateFault => Pending is null && !B("round_seen") && !B("navigation_seen") && !port.NavigationIssued && port.CanReleaseForeground();
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
            ("cast_preflight_rejections", 0), ("last_cast_elapsed_ms", null), ("unobserved_cast_attempts", 0), ("last_unobserved_cast", null),
            ("clock_started", false), ("inspection_round", 0), ("backpack_check_requested", false));
        if(plan.InPlace)State["phase"]="refresh";
        if (Load() is { } saved)
        {
            if (saved["schema_version"]?.GetValue<int>() != 1 || !JsonNode.DeepEquals(saved["plan"], serialized) || saved["target_s"]?.GetFiniteDouble() != plan.DurationSeconds || saved["owned_slots"] is not JsonArray { Count: 0 } || saved["elapsed_s"]?.GetFiniteDouble() is not { } elapsed || !double.IsFinite(elapsed) || elapsed < 0) throw new InvalidDataException("钓鱼记录不匹配，保留待核对");
            State = saved; if (I("completed") < 0 || I("completed") > I("submitted")) throw new InvalidDataException("钓鱼计数无效");
            State["clock_started"] ??= I("submitted") > 0 || D("elapsed_s") > 0 || Pending?["kind"]?.GetValue<string>() == "start";
            if (S("next_direction") is "left" or "a") State["next_direction"] = "w";
            if (S("next_direction") is "right" or "d") State["next_direction"] = "s";
            if (S("next_direction") is not ("w" or "s")) throw new InvalidDataException("钓鱼移动方向无效");
            if (S("status") is not ("complete" or "yielded")) Set(("status", "paused"), ("pause_reason", "restart"));
        }
        Save();
    }
    private void ResetClock() => lastAt = null;
    private void UpdateClock(FishingFrame f)
    {
        if (S("status") != "running" || !B("clock_started") || S("phase")=="navigate"
            || S("phase")=="backpack_exit"&&f.Page is not ("scene" or "fishing" or "fishing_ui"))
        { ResetClock(); return; }
        if (lastAt is {} previous) State["elapsed_s"] = Math.Min(D("target_s"), D("elapsed_s") + Math.Max(0, f.At - previous));
        lastAt = f.Page is "scene" or "fishing" or "fishing_ui" or "unknown" ? f.At : null;
    }
    public void Pause(string reason = "manual")
    {
        port.Pause(reason); lock (Gate) { if (S("status") == "complete" || S("pause_reason") == "manual" && reason == "focus_lost") return; Set(("status", "paused"), ("pause_reason", reason)); ResetClock(); Save(); }
    }
    public void Resume(bool explicitRequest = true)
    {
        lock (Gate)
        {
            if (S("status") == "complete" || !explicitRequest && S("pause_reason") != "focus_lost") return;
            if (S("status") == "yielded") {port.InvalidatePreparation();ResetRoundSamples();Set(("phase", plan.InPlace||port.CanResumeAtPoint?"refresh":"navigate"), ("navigation_seen", false), ("navigation_deadline", null), ("round_seen", false), ("yield_requested", false), ("inspection_round", I("completed")), ("backpack_check_requested", false));}
            // Recreate only reversible menu navigation after explicit resume/restart.
            // A witnessed native trip or unresolved fishing action remains observation-only.
            if(explicitRequest&&S("phase")=="navigate"&&!B("navigation_seen")&&!port.NavigationIssued&&Pending is null)State["navigation_deadline"]=null;
            if(explicitRequest&&Pending is null)State["cast_preflight_rejections"]=0;
            Set(("status", "running"), ("pause_reason", null), ("reason", null)); ResetClock(); ResetPageWait(); ResetProgressWait(); port.Resume(); Save();
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
        if (pending)
        {
            State["pending"] = new JsonObject { ["kind"] = action.Kind, ["at"] = f.At, ["capture_id"] = f.CaptureId, ["deadline"] = f.At + (action.Kind == "start" ? castObservationTimeout : actionTimeout) };
            if(action.Kind=="reel"){Pending!["gauge"]=f.Gauge;Pending["gauge_min"]=State["gauge_min"]?.DeepClone();}
        }
        Save();
        try { port.Perform(action); return true; }
        catch (InputRejectedBeforeDispatchException) { Set(("pending", null), ("status", "running"), ("reason", action.Kind=="start"&&I("cast_preflight_rejections")>=2?"cast_preflight_retry_limit":"target_changed_before_input")); if(action.Kind=="start"){State["phase"]="refresh";State["cast_preflight_rejections"]=I("cast_preflight_rejections")+1;}if(action.Kind=="navigate")State["navigation_deadline"]=null;Save();return false; }
        catch (Exception error) { RememberFault(error,action.Kind); Unknown(pending ? "action_result_not_observed" : "input_interrupted"); throw; }
    }
    private void ResetRoundSamples()=>Set(("gauge_min",null),("sample_at",null),("hook_seen",false));
    private void FinishRound()
    {
            ResetRoundSamples();
            Set(("completed", I("completed") + 1), ("pending", null), ("phase", "refresh"), ("round_seen", false), ("cast_preflight_rejections", 0),
            ("status", "running"), ("reason", null));
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
                    ResetRoundSamples();
                    Set(("submitted", I("submitted") + 1), ("round_seen", true), ("phase", "round"), ("unobserved_cast_attempts",0)); confirmed = true;
                }
                break;
            case "switch_mode": confirmed = f.ModeConfirm || Focused(f); break;
            case "confirm_mode": confirmed = !f.ModeConfirm && (Focused(f) || f.Compass || f.FishingButton); break;
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
                if(I("unobserved_cast_attempts")>=2)State["backpack_check_requested"]=true;
                Save();return true;
            }
            if(kind!="start" && f.Page is "scene" or "fishing" or "fishing_ui")
            {
                // Fishing observations do not own material balances. Recover the
                // current round without holding independent work on a missing receipt.
                Set(("pending",null),("phase",f.RoundActive?"round":"refresh"),("gauge_min",null),("sample_at",null),
                    ("round_seen",f.RoundActive),("reason",kind+"_observation_timeout"),("backpack_check_requested",true));
                Save();return true;
            }
        }
        return false;
    }
    private double PendingDeadline(JsonObject pending) => pending["kind"]?.GetValue<string>() == "start"
        ? pending["at"]!.GetFiniteDouble() + castObservationTimeout : pending["deadline"]!.GetFiniteDouble();
    private bool Boundary(FishingFrame f)
    {
        var status = D("elapsed_s") >= D("target_s") || B("stop_requested") ? "complete" : B("yield_requested") ? "yielded" : null;
        if(status is null)return false;
        if(f.ModeConfirm||f.LeisureConfirm){Perform(new("cancel_mode"),f);return true;}
        if((FishingUi(f)||f.Page=="scene")&&(FishingUi(f)||B("round_seen")||Pending is not null||B("navigation_seen")))
            if(!Perform(new("leave"),f))return false;
        Set(("status",status),("phase",status),("pause_reason",null),("pending",null),("round_seen",false),
            ("navigation_seen",false),("navigation_deadline",null));
        ResetRoundSamples();
        ResetClock();Save();return true;
    }
    public JsonObject Tick(bool higherPriority = false)
    { lock (Gate) { if (higherPriority) State["yield_requested"] = true; Step(); return Snapshot(); } }
    public void ObserveOnly()
    {
        lock (Gate)
        {
            if (S("status") is "complete" or "yielded") return;
            if (Pending is null && !B("round_seen") && !(S("phase") == "navigate" && (B("navigation_seen") || port.NavigationIssued || State["navigation_deadline"] is not null || State["unexpected_navigation_deadline"] is not null))) return;
            var f = port.ObserveOnly();
            if (!double.IsFinite(f.At) || string.IsNullOrWhiteSpace(f.CaptureId) || !Ready(f.Page, f.Foreground, f.Stable)) return;
            var reason = S("pause_reason") ?? "fishing_action_confirmed";
            if (Pending is not null) ReconcilePending(f, true);
            if (Pending is null && B("round_seen") && RoundEnded(f)) FinishRound();
            else if (S("phase") == "navigate")
            {
                if (f.NavigationActive == true) State["navigation_seen"] = true;
                else if (f.NavigationActive == false) Set(("phase", "refresh"), ("navigation_seen", false), ("navigation_deadline", null), ("reason", null));
            }
            if (Pending is null && S("phase") is "round" or "refresh")
                Set(("status", B("stop_requested") && !B("round_seen") ? "complete" : "paused"), ("pause_reason", reason));
            ResetClock(); Save();
        }
    }
    private void Step()
    {
        if (S("status") is "complete" or "yielded") { Save(); return; }
        if(S("status")=="running"&&S("phase")=="navigate")
        {
            try{CheckProgress("navigate",navigationTimeout);}
            catch(TimeoutException)
            {
                if(Pending is null&&!B("round_seen")&&!B("navigation_seen")&&!port.NavigationIssued)port.AbandonUnsentNavigation();
                throw;
            }
        }
        else if(S("status")=="running"&&S("phase")=="backpack_exit")CheckProgress("backpack_exit",actionTimeout);
        else ResetProgressWait();
        var f = port.Observe(); if (!double.IsFinite(f.At) || string.IsNullOrWhiteSpace(f.CaptureId)) throw new ArgumentException("需要新截图与时间");
        if (!f.Foreground) { if (S("status") != "paused" || S("pause_reason") == "focus_lost") Pause("focus_lost"); return; }
        UpdateClock(f);
        if(S("status")=="running"&&State["unexpected_navigation_deadline"] is{} externalDeadline
            &&f.At>=externalDeadline.GetFiniteDouble()&&f.NavigationActive!=false)
            throw new TimeoutException("钓鱼等待其他导航结束超时");
        if (!PageReady(f.Page, f.Foreground, f.Stable, Pause, pauseOnUnrecognized: false))
        {
            if(S("status") == "running" && S("phase") == "navigate" && State["navigation_deadline"] is not null && f.At >= D("navigation_deadline"))Unknown("navigation_not_observed");
            Save(); return;
        }
        if (S("status") == "paused") { ObserveOnly(); return; }
        if (S("status") == "unknown") { ObserveOnly(); return; }
        if (lastCapture == f.CaptureId) return; lastCapture = f.CaptureId;
        if(f.BackpackFull)State["backpack_check_requested"]=true;
        if(Boundary(f))return;
        if(f.Page is "backpack" or "bag" or "repair_tools")
        {
            Set(("pending",null),("round_seen",false),("phase","backpack_exit"));ResetRoundSamples();
            port.InvalidatePreparation();Save();port.ReleaseBackpack();return;
        }
        if(S("phase")=="backpack_exit"){State["phase"]="refresh";ResetProgressWait();}
        // 模式弹窗可以由初始悠闲状态或首次抛竿打开，先处理它再等待抛竿回执。
        if(f.ModeConfirm && Pending?["kind"]?.GetValue<string>()!="confirm_mode")
        {Set(("pending",null),("round_seen",false));ResetRoundSamples();Perform(new("confirm_mode"),f,true);return;}
        if(f.LeisureConfirm){Perform(new("cancel_mode"),f);return;}
        if (!ReconcilePending(f)) { Save(); return; }
        if(f.NavigationActive==true&&!B("round_seen")&&!f.RoundActive&&Pending is null
            &&(S("phase")!="navigate"||State["navigation_deadline"] is null))
        {
            State["unexpected_navigation_deadline"]??=f.At+navigationTimeout;
            if(f.At>=D("unexpected_navigation_deadline"))throw new TimeoutException("钓鱼等待其他导航结束超时");
            ResetClock();Save();return;
        }
        State.Remove("unexpected_navigation_deadline");
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
        if (B("round_seen") && RoundEnded(f)) FinishRound();
        if(!B("round_seen")&&!f.RoundActive&&Pending is null&&!f.Mounted)
        {
            if(port.ModePreparationRequired)
            {if(Perform(new("prepare_mode"),f))State["mode_probe_at"]=f.At;Save();return;}
            if(State["mode_probe_at"] is not null&&f.At-D("mode_probe_at")<.5){Save();return;}
            State["mode_probe_at"]=null;
        }
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
            if (!B("round_seen"))
            {
                ResetRoundSamples();
                Set(("round_seen",true),("submitted",I("submitted")+1),("clock_started",true),("unobserved_cast_attempts",0));
                lastAt??=f.At;
            }
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
        var elapsed=Stopwatch.StartNew();State["phase"]="confirm_start";
        if(Perform(new("start"),f,true)){ResetRoundSamples();State["clock_started"]=true;lastAt??=f.At;}
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

