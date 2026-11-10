using System.Globalization;
using System.Text.Json.Nodes;
using MabiAgent.Core;
using MabiAgent.Workflows;
namespace MabiAgent.Windows;

public sealed class ScreenFishingPort:ScreenWorkflowPort,IFishingPort
{
    private const double ReelAnimationSeconds=3,RecastWindowSeconds=2;
    private readonly string pointKey;private readonly JsonNode config,nav;private readonly JsonObject state;
    private bool movingForCast;private ushort? castMovementKey;private bool cast,compass,active,confirm,leisureConfirm,hook,mounted,reelIssued;private bool? leisure;private double? gauge;private bool navigationSeen,observedNavigationActive;
    private string? pendingPage;private double pendingAt;private bool navigating;
    private JsonNode? pendingStep;private int navigationRetries,scrollCount;private readonly TimeProvider navigationTime;
    private double? unsentNavigationSince,navigationStartedAt;private int pointReselections;private bool navigationFailed;
    private double? travelIssuedAt,navigationIdleSince;private bool navigationSettled;private long? arrivalRevision;
    public bool CanResumeAtPoint=>pointKey==FishingPlan.CurrentLocation||arrivalRevision==Session.LocationRevision;
    public bool NavigationIssued=>state["travel_issued"]?.GetValue<bool>()==true;
    public bool ModePreparationRequired=>config["controls"]?["switch_mode"] is not null&&state["mode_prepared"]?.GetValue<bool>()!=true;
    private PixelBox? lastGaugeBox;private double? clearSceneSince,recastUntil;
    private ScreenMaintenancePort? backpackExit;
    public FishingFrame? LastFrame {get;private set;}
    public override bool CanReleaseForeground()
    {
        if(pendingPage is not null||navigating||active||observedNavigationActive)return false;
        // 尚未发出旅行的选点失败后，由后续任务接手退出遗留菜单。
        if(navigationFailed&&!Session.Guard.Paused&&Session.InputAvailable&&Session.CurrentNavigationId is null
            &&state["travel_issued"]?.GetValue<bool>()!=true)return true;
        return base.CanReleaseForeground();
    }
    public ScreenFishingPort(ScreenWorkflowSession session,string pointKey,string batchId,IWorkflowStore store,TimeProvider? navigationTime=null):base(session,batchId,store)
    {
        this.navigationTime=navigationTime??TimeProvider.System;this.pointKey=pointKey;nav=pointKey==FishingPlan.CurrentLocation?new JsonObject():session.Routes.Configuration["gathering_routes"]?[pointKey]?["navigation"]??throw new ArgumentException("钓鱼点未配置");config=session.Routes.Profile.Definition["idle_fishing"]??new JsonObject();state=session.PortState("fishing:"+batchId);navigationSeen=state["navigation_seen"]?.GetValue<bool>()==true;
        var workflow=store.Read();observedNavigationActive=workflow?["unexpected_navigation_deadline"] is not null;
        if(NavigationIssued&&S(workflow,"phase")=="navigate"){navigating=true;travelIssuedAt=this.navigationTime.GetUtcNow().ToUnixTimeMilliseconds()/1000d;}
    }
    private string? Anchor(string role)=>S(config["anchors"],role);
    protected override PageView Decorate(ClientCapture c,PageView view)
    {
        cast=compass=active=confirm=leisureConfirm=hook=false;mounted=view.Targets.ContainsKey("scene.dismount");leisure=null;gauge=null;
        // 稳定弹窗标题可以在普通观察和轮内观察中共用，先于模态页过滤。
        var focusName=Anchor("confirm");var focusHit=focusName is{} focus?Session.Matcher.Anchor(c.Image,focus):null;
        var leisureHit=Session.Matcher.Anchor(c.Image,"fishing_leisure_dialog_title");
        confirm=focusHit is not null;leisureConfirm=leisureHit is not null;
        if(confirm||leisureConfirm)
        {
            state["mode_prepared"]=true;
            view=view with{Page="fishing",Modal=false};
            var titleName=confirm?focusName!:"fishing_leisure_dialog_title";var title=focusHit??leisureHit!;
            if(config["controls"]?["confirm_mode"] is{} change&&Session.AnchoredBox(titleName,title,NavigationProfile.ReadBox(change)) is{} changeBox)
                view=Session.AddSurface(view,"fishing.confirm_mode",changeBox);
            if(Session.Routes.Profile.Templates.TryGetValue("fishing_change_cancel",out var cancel)
                &&Session.AnchoredBox(titleName,title,cancel.Box) is{} cancelBox)view=Session.AddSurface(view,"fishing.cancel_mode",cancelBox);
            active=confirm;return view;
        }
        if(view.Modal||mounted||view.Page is not ("scene" or "fishing" or "unknown"))
        {InvalidatePreparation();return view;}
        // Cast availability comes from movement or the just-ended round's short window.
        var roundObserved=state["round_observed"]?.GetValue<bool>()==true;
        reelIssued=state["reel_issued"]?.GetValue<bool>()==true;
        var hookHit=Anchor("hook") is {} h?Session.Matcher.Anchor(c.Image,h):null;hook=hookHit is not null;
        confirm=Anchor("confirm") is {} cf&&Session.Matcher.Anchor(c.Image,cf) is not null;
        var trackEstablished=hook||roundObserved||reelIssued;
        var awaitingCast=S(Store.Read()?["pending"],"kind")=="start";
        // A pending cast alone cannot turn uniform scene colours into a zero
        // gauge. Its first hook may be missed, so a shaped two-colour track can
        // also establish the round; only an established track may read as zero.
        if(config["gauge"] is {} calibration&&calibration["region"] is {} region&&(trackEstablished||awaitingCast))
        {
            var configuredBox=Session.Box(region);var box=lastGaugeBox??configuredBox;
            if(hook&&calibration["hook_track_offset"] is JsonObject offsets)
            {
                var center=hookHit!.Box.Center;var sx=c.Image.Width/(double)Session.Routes.Profile.Width;var sy=c.Image.Height/(double)Session.Routes.Profile.Height;
                box=new(center.X+(int)Math.Round(offsets["left"]!.GetValue<int>()*sx),center.Y+(int)Math.Round(offsets["top"]!.GetValue<int>()*sy),center.X+(int)Math.Round(offsets["right"]!.GetValue<int>()*sx),center.Y+(int)Math.Round(offsets["bottom"]!.GetValue<int>()*sy));
            }
            gauge=trackEstablished?FishingGauge.Read(c.Image,box,calibration):null;
            if(gauge is null&&Anchor("hook") is {} hookName&&Session.Routes.Profile.Templates.TryGetValue(hookName,out var hookTemplate))
            {
                var search=Session.Box(hookTemplate.Search);var padding=Math.Max(8,configuredBox.Width/2);
                search=new(Math.Max(0,search.Left-padding),Math.Max(0,search.Top-padding),Math.Min(c.Image.Width,search.Right+padding),Math.Min(c.Image.Height,search.Bottom+padding));
                if(FishingGauge.Find(c.Image,search,calibration,configuredBox.Width) is{} located)
                {box=located.Box;gauge=located.Value;}
            }
            if(gauge is not null)lastGaugeBox=box;
        }
        // Keep tracking the gauge if the hook is missed. During the reel animation
        // the scene HUD disappears too: its return must not start another wait.
        var signal=hook||gauge is not null||confirm;
        var now=navigationTime.GetUtcNow().ToUnixTimeMilliseconds()/1000d;
        var sourceAt=now-Math.Max(0,System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency-c.CapturedAt);
        if(signal)clearSceneSince=null;
        else if(roundObserved||reelIssued)clearSceneSince??=sourceAt;
        active=signal||(roundObserved||reelIssued)&&(clearSceneSince is null||now-clearSceneSince.Value<ReelAnimationSeconds);
        if(signal)recastUntil=null;
        else if(!active&&(roundObserved||reelIssued)&&clearSceneSince is {} ended)recastUntil??=ended+ReelAnimationSeconds+RecastWindowSeconds;
        leisure=hook||gauge is not null?false:null;
        compass=view.Page=="scene"&&!active;cast=compass&&(movingForCast||recastUntil is {} until&&now<until);
        if(active&&view.Page is "scene" or "unknown")view=view with{Page="fishing"};
        if(view.Page=="scene"||hook||active)
        {
            if(!active&&config["controls"]?["switch_mode"] is{} mode)view=Session.AddSurface(view,"fishing.prepare_mode",Session.Box(mode));
            if(cast||hook)view=view with{Page="fishing"};
            if(config["controls"]?["start"] is {} start)
            {
                // A fixed surface is script context, not a recognized cast button.
                if(!active)view=Session.AddSurface(view,"fishing.start",Session.Box(start));
                if(hook||gauge is not null)view=Session.AddSurface(view,"fishing.reel",Session.Box(start));
            }
            if(config["controls"]?["move"] is {} move)view=Session.AddSurface(view,"fishing.move",Session.Box(move));
            if(view.Page=="fishing"&&config["controls"]?["move"] is {} leave)view=Session.AddSurface(view,"fishing.leave",Session.Box(leave));
        }
        // A positive navigation task is remembered only by Observe, not by recognition.
        return view;
    }
    public FishingFrame Observe()
        => ObserveFrame(true);
    public FishingFrame ObserveOnly()
        => ObserveFrame(false);
    private PageView RecognizeFishingWait(ClientCapture capture)
    {
        if(Anchor("confirm") is{} title&&Session.Matcher.Anchor(capture.Image,title) is{} mode)
            return new("fishing",false,[mode],new Dictionary<string,PixelPoint>(),[mode.Box]);
        if(Session.Matcher.Anchor(capture.Image,"fishing_leisure_dialog_title") is{} leisureMode)
            return new("fishing",false,[leisureMode],new Dictionary<string,PixelPoint>(),[leisureMode.Box]);
        if(Session.Matcher.Anchor(capture.Image,"bag_close") is {} close&&Session.Matcher.Anchor(capture.Image,"bag_tools_tab") is {} tools)
            return new("bag",false,[close,tools],new Dictionary<string,PixelPoint>{{"bag.close",close.Box.Center}},[close.Box,tools.Box]);
        if(Anchor("hook") is {} name&&Session.Matcher.Anchor(capture.Image,name) is {} hit)
            return new("fishing",false,[hit],new Dictionary<string,PixelPoint>(),[hit.Box]);
        // Reuse the common scene recognizer's calibrated HUD tolerance. Its
        // controls fade during the animation; unknown frames still send no cast.
        var bag=Session.Matcher.Anchor(capture.Image,"scene_bag",.85,thresholdOverride:.85);var map=Session.Matcher.Anchor(capture.Image,"maint_scene_map",.85,thresholdOverride:.85);
        return bag is not null&&map is not null?new("scene",false,[bag,map],new Dictionary<string,PixelPoint>(),[bag.Box,map.Box]):PageView.Unknown();
    }
    private FishingFrame ObserveFrame(bool allowInput)
    {
        var observedAt=System.Diagnostics.Stopwatch.GetTimestamp();
        var workflow=Store.Read();var waiting=S(workflow?["pending"],"kind") is "start" or "reel"||S(workflow,"phase") is "round" or "confirm_reel";
        var o=waiting?ReadFocused(RecognizeFishingWait):Read();var c=Session.LastCapture;var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d;
        if(o.View.Page is "scene" or "fishing"&&backpackExit is not null)
        {backpackExit.ResetNavigation();backpackExit=null;}
        var navNow=navigationTime.GetUtcNow().ToUnixTimeMilliseconds()/1000d;
        var navigationActive=c is not null&&(navigating||!waiting)
            &&(Session.Hit(c,Anchor("nav"))||Session.Hit(c,"navigation_task_prefix"));
        if(navigationActive&&!navigating)arrivalRevision=null;
        if(navigationActive)observedNavigationActive=true;
        else if(observedNavigationActive&&o.Stable&&o.View.Page is "scene" or "fishing")
        {observedNavigationActive=false;if(!navigating)navigationSettled=true;}
        if(allowInput&&!Session.Guard.Paused&&S(workflow,"status")=="running")CheckSelectionBudget();
        if(c is not null&&o.Stable&&Session.InputAvailable)
        {
            if((hook||gauge is not null)&&!reelIssued&&state["round_observed"]?.GetValue<bool>()!=true){state["round_observed"]=true;state["mode_prepared"]=true;Session.SavePortState("fishing:"+BatchId,state);}
            if(navigating&&travelIssuedAt is {} issued&&navNow>issued&&navigationActive){navigationIdleSince=null;navigationSeen=true;state["navigation_seen"]=true;Session.SavePortState("fishing:"+BatchId,state);}
            else if(navigating&&travelIssuedAt is {} sent&&navNow>sent&&o.View.Page is "scene" or "fishing")
            {
                // A nearby destination may never create a navigation task. Wait for its
                // fixed title to be absent on the scene for three seconds in both cases.
                navigationIdleSince??=navNow;
                if(navNow-navigationIdleSince>=3)
                {
                    navigationSettled=true;navigating=false;pendingPage=null;arrivalRevision=Session.LocationRevision;
                    navigationSeen=false;state["navigation_seen"]=false;state["travel_issued"]=false;
                    Session.SavePortState("fishing:"+BatchId,state);Session.EndNavigation("fishing:"+BatchId);
                }
            }
            if(navigating&&navigationActive!=true&&o.View.Page is not ("scene" or "fishing"))navigationIdleSince=null;
            if(allowInput&&navigating&&!navigationSeen&&!navigationActive&&!Session.Guard.Paused&&S(Store.Read(),"status")=="running")
            {
                try{NavigationStep(o);}
                catch(Exception e)when(!Session.Guard.Paused&&e is InputBlockedException or InputRejectedBeforeDispatchException){Session.ReportWarning("fishing_navigation",e.Message,BatchId);}
            }
        }
        if(reelIssued&&o.Stable&&o.View.Page=="scene"&&!hook)
        {
            state["reel_issued"]=false;
            Session.SavePortState("fishing:"+BatchId,state);
        }
        if(!active&&o.Stable&&o.View.Page=="scene"&&state["round_observed"]?.GetValue<bool>()==true)
        {
            state["round_observed"]=false;lastGaugeBox=null;clearSceneSince=null;
            Session.SavePortState("fishing:"+BatchId,state);
            Session.TraceNavigation(BatchId,"fishing.round_end: hook and gauge absent; scene wait elapsed","observed");
        }
        if(cast)Session.TraceNavigation(BatchId,$"fishing.recast_ready: absent_ms={(navNow-clearSceneSince)*1000:F0}; observe_ms={System.Diagnostics.Stopwatch.GetElapsedTime(observedAt).TotalMilliseconds:F1}","observed");
        else if(!o.Stable)Session.ReportWarning("fishing_cast_observation",$"page={o.View.Page}; cast={cast}; stable={o.Stable}; reason={Session.StabilityReason}; active={active}; mounted={mounted}",BatchId);
        var full=c is not null&&Session.Matcher.Anchor(c.Image,"overlay.backpack_full_toast") is not null;
        return LastFrame=new(now,c?.CapturedAt.ToString("R",CultureInfo.InvariantCulture)??"",o.View.Page,Session.InputAvailable,o.Stable,navigationActive?true:navigationSettled?false:null,cast,leisure,confirm,active,hook,gauge,compass,mounted,BackpackFull:full,LeisureConfirm:leisureConfirm);
    }
    private void NavigationStep(UiObservation o)
    {
        try{NavigationStepCore(o);unsentNavigationSince=null;}
        catch(InputRejectedBeforeDispatchException error)
        {
            var now=navigationTime.GetUtcNow().ToUnixTimeMilliseconds()/1000d;unsentNavigationSince??=now;
            if(now-unsentNavigationSince>=15)
            {
                EndFailedNavigation();
                throw new TimeoutException("钓鱼导航输入连续15秒未能发送："+error.Message,error);
            }
            throw;
        }
        catch(TimeoutException){EndFailedNavigation();throw;}
    }
    private void EndFailedNavigation()
    {
        navigating=false;navigationFailed=true;pendingPage=null;pendingStep=null;unsentNavigationSince=navigationStartedAt=null;
        navigationRetries=scrollCount=0;Session.EndNavigation("fishing:"+BatchId);
    }
    public void AbandonUnsentNavigation()
    {
        if(navigationSeen||active||state["travel_issued"]?.GetValue<bool>()==true
            ||state["round_observed"]?.GetValue<bool>()==true||state["reel_issued"]?.GetValue<bool>()==true
            ||Store.Read()?["pending"] is not null)return;
        EndFailedNavigation();
    }
    private void CheckSelectionBudget()
    {
        if(!navigating||state["travel_issued"]?.GetValue<bool>()==true||navigationStartedAt is not{} began)return;
        var now=navigationTime.GetUtcNow().ToUnixTimeMilliseconds()/1000d;
        if(now-began<(Session.Routes.Configuration["navigation_retry"]?["timeout_s"]?.GetFiniteDouble()??90))return;
        EndFailedNavigation();
        throw new TimeoutException("钓鱼选点导航已到总等待上限");
    }
    private void NavigationStepCore(UiObservation o)
    {
        // Once travel has been dispatched, only observe its result. In particular,
        // a short trip must not reopen the route from the scene before its task is seen.
        if(state["travel_issued"]?.GetValue<bool>()==true)return;
        CheckSelectionBudget();
        var now=navigationTime.GetUtcNow().ToUnixTimeMilliseconds()/1000d;
        var wrongDetail=o.View.Page!=S(nav,"detail_page")&&o.View.Page.StartsWith("point_detail_",StringComparison.Ordinal);
        if(pendingPage is not null)
        {
            var expected=S(pendingStep,"expected_page");
            if((expected is not null?o.View.Page==expected:o.View.Page!=pendingPage)
                ||wrongDetail&&S(pendingStep,"target")!="gathering.close")
            {Session.TraceNavigation(BatchId,NavigationDescription(pendingStep!,o),"confirmed");pendingPage=null;pendingStep=null;navigationRetries=scrollCount=0;}
            else if(now-pendingAt<(Session.Routes.Configuration["navigation_retry"]?["interval_s"]?.GetFiniteDouble()??8))return;
            else if(o.View.Page==pendingPage&&navigationRetries<(Session.Routes.Configuration["navigation_retry"]?["max_retries"]?.GetValue<int>()??4))
            {navigationRetries++;Session.Step(pendingStep!);pendingAt=now;Session.TraceNavigation(BatchId,NavigationDescription(pendingStep!,o),"retry_dispatched");return;}
            else
            {
                var reason="钓鱼导航等待超时："+NavigationDescription(pendingStep!,o);
                Session.ReportWarning("fishing_navigation",reason,BatchId);throw new TimeoutException(reason);
            }
        }
        if(wrongDetail&&o.View.Targets.ContainsKey("gathering.close"))
        {
            if(pointReselections>=3)throw new TimeoutException("钓鱼点连续误选，重选已到三次上限");
            var close=new JsonObject{["page"]=o.View.Page,["target"]="gathering.close",["expected_page"]="fishing_nodes"};
            Session.Step(close);pointReselections++;pendingPage=o.View.Page;pendingStep=close;pendingAt=now;navigationRetries=scrollCount=0;
            Session.TraceNavigation(BatchId,$"requested={pointKey}; observed={o.View.Page}; reselect={pointReselections}","wrong_point_closed");return;
        }
        if(o.View.Page==S(nav,"detail_page"))
        {
            Session.NavigateTarget(S(nav,"start_target")!);travelIssuedAt=navigationTime.GetUtcNow().ToUnixTimeMilliseconds()/1000d;state["travel_issued"]=true;Session.SavePortState("fishing:"+BatchId,state);Session.BeginNavigation("fishing:"+BatchId);Session.TraceNavigation(BatchId,S(nav,"start_target")!,"travel_dispatched");return;
        }
        var step=nav["steps"]!.AsArray().FirstOrDefault(s=>S(s,"page")==o.View.Page)??Session.Routes.Configuration["maintenance"]?["exit_navigation"]?[o.View.Page];if(step is null)return;
        var target=S(step,"target");
        if(target is not null&&!o.View.Targets.ContainsKey(target)&&o.View.Targets.ContainsKey("list.scroll"))
        {
            if(scrollCount>=ScreenWorkflowSession.ListSearchLimit)throw new TimeoutException("钓鱼列表搜索已到上限："+NavigationDescription(step,o));
            Session.ScrollListSearch(o,scrollCount++);
            Session.TraceNavigation(BatchId,NavigationDescription(step,o)+$"; scroll={scrollCount}","search_dispatched");return;
        }
        Session.Step(step);pendingPage=o.View.Page;pendingStep=step;pendingAt=now;
        Session.TraceNavigation(BatchId,NavigationDescription(step,o),"input_dispatched");
    }
    private string NavigationDescription(JsonNode step,UiObservation observation)
    {
        var target=S(step,"target")??"?";var point=observation.View.Targets.TryGetValue(target,out var p)?$"{p.X},{p.Y}":"unavailable";
        return $"target={target}; from={S(step,"page")??"?"}; expected={S(step,"expected_page")??"page_change"}; observed={observation.View.Page}; point={point}; retries={navigationRetries}";
    }
    public void Perform(FishingAction action)
    {
        try
        {
            PerformCore(action);
            if(action.Kind is "move" or "start")Session.TraceNavigation(BatchId,"fishing."+action.Kind,"input_dispatched");
            else if(action.Kind=="reel")
            {var pending=Store.Read()?["pending"];Session.TraceNavigation(BatchId,$"fishing.reel: gauge={pending?["gauge"]}; low={pending?["gauge_min"]}","input_dispatched");}
        }
        catch(InputRejectedBeforeDispatchException e){Session.ReportWarning("fishing_preflight",e.Message,action.Kind);throw;}
        catch(InputBlockedException e)when(!Session.Guard.Paused){Session.ReportWarning("fishing_preflight",e.Message,action.Kind);throw new InputRejectedBeforeDispatchException(e.Message);}
    }
    private void PerformCore(FishingAction action)
    {
        if(action.Kind is "prepare_mode" or "cancel_mode")
        {
            Session.NavigateTarget("fishing."+action.Kind);state["mode_prepared"]=true;
            Session.SavePortState("fishing:"+BatchId,state);return;
        }
        if(action.Kind is "start" or "reel" or "switch_mode" or "confirm_mode")
        {
            // ObserveFrame already selected this port's decoration context. Consume
            // reuses that review for its proof; dispatch checks system boundaries only.
            Consume("fishing."+action.Kind,action.Kind,_=>action.Kind switch{"start"=>!active&&(movingForCast||cast&&recastUntil is {} until&&navigationTime.GetUtcNow().ToUnixTimeMilliseconds()/1000d<until),"reel"=>(hook||gauge is not null)&&leisure==false,"switch_mode"=>leisure==true,_=>confirm},null,movementKey:action.Kind=="start"?castMovementKey:null);
            if(action.Kind=="start"){InvalidatePreparation();state.Remove("round_started");}
            else if(action.Kind=="reel"){clearSceneSince=null;state["reel_issued"]=true;Session.SavePortState("fishing:"+BatchId,state);}
            return;
        }
        UiObservation o;
        try{o=Session.LastObservation??throw new InputBlockedException("缺少钓鱼等待观察");if(!o.Stable||!Session.InputAvailable)throw new InputBlockedException("钓鱼画面未确认");}
        catch(InputBlockedException e){throw new InputRejectedBeforeDispatchException(e.Message);}
        if(action.Kind=="dismount"){Session.NavigateTarget("scene.dismount");return;}
        if(action.Kind=="navigate"){if(action.PointKey!=pointKey)throw new InputBlockedException("钓鱼点不匹配");navigationSeen=navigationSettled=navigationFailed=false;travelIssuedAt=navigationIdleSince=null;navigationStartedAt=navigationTime.GetUtcNow().ToUnixTimeMilliseconds()/1000d;pointReselections=0;arrivalRevision=null;state["navigation_seen"]=false;state["travel_issued"]=false;Session.SavePortState("fishing:"+BatchId,state);pendingPage=null;pendingStep=null;navigationRetries=scrollCount=0;navigating=true;NavigationStep(o);return;}
        if(action.Kind=="move"){if(action.Direction is not ("w" or "s")||action.DurationSeconds is null or <=0 or >.3)throw new InputBlockedException("钓鱼移动参数无效");Session.Guard.PressKey(Session.Guard.ObserveCurrentTarget("fishing.move",o),action.Direction=="w"?(ushort)87:(ushort)83,TimeSpan.FromSeconds(action.DurationSeconds.Value));return;}
        if(action.Kind=="leave"){Session.Guard.PressKey(Session.Guard.ObserveCurrentTarget(o.View.Page=="scene"?"fishing.move":"fishing.leave",o),87,TimeSpan.FromMilliseconds(120));InvalidatePreparation();navigating=false;Session.EndNavigation("fishing:"+BatchId);return;}
        throw new InputBlockedException("钓鱼操作未配置");
    }
    public void InvalidatePreparation()
    {
        lastGaugeBox=null;clearSceneSince=recastUntil=null;cast=active=reelIssued=false;
        if(state["round_observed"]?.GetValue<bool>()==true||state["reel_issued"]?.GetValue<bool>()==true)
        {state["round_observed"]=false;state["reel_issued"]=false;Session.SavePortState("fishing:"+BatchId,state);}
    }
    public void ReleaseBackpack()
    {
        backpackExit??=Session.CreateMaintenancePort(BatchId+":backpack_exit",Store);
        backpackExit.ReleaseNavigation();
    }
    public void MoveWhileObserving(FishingAction action,Func<FishingFrame,bool> observe)
    {
        if(action.Kind!="move"||action.Direction is not ("w" or "s")||action.DurationSeconds is not (>0 and <=.12))throw new InputBlockedException("移动轻按参数无效");
        if(Session.Guard.Paused||!Session.InputAvailable)throw new InputBlockedException("输入暂停");
        var o=Session.LastObservation??throw new InputRejectedBeforeDispatchException("缺少移动等待观察");if(!o.Stable||o.View.Page is not ("scene" or "fishing"))throw new InputRejectedBeforeDispatchException("移动页面未确认");
        // Prepare the durable cast before the shared guard lightly taps W/S,
        // then uses the game's Space action through the same keyboard channel.
        movingForCast=true;castMovementKey=action.Direction=="w"?(ushort)87:(ushort)83;
        try
        {
            // This frame is fixed-script action context, not a current-frame
            // detection result. Do not spend the short cast window capturing
            // or reviewing a dynamic control.
            var now=Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d,Math.BitIncrement(LastFrame?.At??0));
            observe(new(now,"movement:"+now.ToString("R",CultureInfo.InvariantCulture),"fishing",
                Session.InputAvailable,true,FishingButton:true));
        }
        finally{movingForCast=false;castMovementKey=null;}
    }
}
