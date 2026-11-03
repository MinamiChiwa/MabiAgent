using System.Globalization;
using System.Text.Json.Nodes;
using MabiAgent.Core;
using MabiAgent.Workflows;
namespace MabiAgent.Windows;

public sealed class ScreenFishingPort:ScreenWorkflowPort,IFishingPort
{
    private readonly string pointKey;private readonly JsonNode config,nav;private readonly JsonObject state;
    private bool movingForCast;private bool cast,compass,active,confirm,hook,mounted,reelIssued;private bool? leisure;private double? gauge;private bool navigationSeen;
    private string? pendingPage;private double pendingAt;private bool navigating;
    private JsonNode? pendingStep;private int navigationRetries,scrollCount;private readonly TimeProvider navigationTime;
    private double? travelIssuedAt;private bool navigationSettled;
    private PixelBox? lastGaugeBox;private double? clearSceneSince;
    public FishingFrame? LastFrame {get;private set;}
    public override bool CanReleaseForeground()=>pendingPage is null&&!navigating&&!navigationSeen&&!active&&base.CanReleaseForeground();
    public ScreenFishingPort(ScreenWorkflowSession session,string pointKey,string batchId,IWorkflowStore store,TimeProvider? navigationTime=null):base(session,batchId,store)
    {this.navigationTime=navigationTime??TimeProvider.System;this.pointKey=pointKey;nav=pointKey==FishingPlan.CurrentLocation?new JsonObject():session.Routes.Configuration["gathering_routes"]?[pointKey]?["navigation"]??throw new ArgumentException("钓鱼点未配置");config=session.Routes.Profile.Definition["idle_fishing"]??new JsonObject();state=session.PortState("fishing:"+batchId);navigationSeen=state["navigation_seen"]?.GetValue<bool>()==true;}
    private string? Anchor(string role)=>S(config["anchors"],role);
    protected override PageView Decorate(ClientCapture c,PageView view)
    {
        cast=compass=active=confirm=hook=false;mounted=view.Targets.ContainsKey("scene.dismount");leisure=null;gauge=null;
        if(view.Modal||mounted){clearSceneSince=null;return view;}
        // Cast availability comes from the fixed movement script, never a dynamic button.
        var roundObserved=state["round_observed"]?.GetValue<bool>()==true;
        reelIssued=state["reel_issued"]?.GetValue<bool>()==true;
        var hookHit=Anchor("hook") is {} h?Session.Matcher.Anchor(c.Image,h):null;hook=hookHit is not null;
        confirm=Anchor("confirm") is {} cf&&Session.Matcher.Anchor(c.Image,cf) is not null;
        if(config["gauge"] is {} calibration&&calibration["region"] is {} region&&(hook||roundObserved||reelIssued))
        {
            var box=hook?Session.Box(region):lastGaugeBox;
            if(hook&&calibration["hook_track_offset"] is JsonObject offsets)
            {
                var center=hookHit!.Box.Center;var sx=c.Image.Width/(double)Session.Routes.Profile.Width;var sy=c.Image.Height/(double)Session.Routes.Profile.Height;
                box=new(center.X+(int)Math.Round(offsets["left"]!.GetValue<int>()*sx),center.Y+(int)Math.Round(offsets["top"]!.GetValue<int>()*sy),center.X+(int)Math.Round(offsets["right"]!.GetValue<int>()*sx),center.Y+(int)Math.Round(offsets["bottom"]!.GetValue<int>()*sy));
            }
            if(box is {} track){gauge=FishingGauge.Read(c.Image,track,calibration);if(hook)lastGaugeBox=track;}
        }
        // The ordinary HUD is also visible during fishing. A missed hook is not
        // round completion: keep reading its last calibrated gauge region and
        // wait one second of clear scene at the existing observation cadence.
        var signal=hook||gauge is not null||confirm;
        var now=navigationTime.GetUtcNow().ToUnixTimeMilliseconds()/1000d;
        if(signal||view.Page!="scene")clearSceneSince=null;
        else if(roundObserved||reelIssued)clearSceneSince??=now;
        active=signal||(roundObserved||reelIssued)&&(clearSceneSince is null||now-clearSceneSince.Value<1);
        leisure=hook||gauge is not null?false:null;
        compass=view.Page=="scene"&&!active;cast=movingForCast&&view.Page=="scene"&&!hook;
        if(active&&view.Page=="scene")view=view with{Page="fishing"};
        if(view.Page=="scene"||hook||active)
        {
            if(cast||hook)view=view with{Page="fishing"};
            if(config["controls"]?["start"] is {} start)
            {
                // A fixed surface is script context, not a recognized cast button.
                if(!active)view=Session.AddSurface(view,"fishing.start",Session.Box(start));
                if(hook||gauge is not null)view=Session.AddSurface(view,"fishing.reel",Session.Box(start));
            }
            if(config["controls"]?["move"] is {} move)view=Session.AddSurface(view,"fishing.move",Session.Box(move));
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
        if(Anchor("hook") is {} name&&Session.Matcher.Anchor(capture.Image,name) is {} hit)
            return new("fishing",false,[hit],new Dictionary<string,PixelPoint>(),[hit.Box]);
        var bag=Session.Matcher.Anchor(capture.Image,"scene_bag");var map=Session.Matcher.Anchor(capture.Image,"maint_scene_map");
        return bag is not null&&map is not null?new("scene",false,[bag,map],new Dictionary<string,PixelPoint>(),[bag.Box,map.Box]):PageView.Unknown();
    }
    private FishingFrame ObserveFrame(bool allowInput)
    {
        var workflow=Store.Read();var waiting=S(workflow?["pending"],"kind") is "start" or "reel"||S(workflow,"phase") is "round" or "confirm_reel";
        var o=waiting?ReadFocused(RecognizeFishingWait):Read();var c=Session.LastCapture;var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d;
        var navigationActive=c is not null&&Session.Hit(c,Anchor("nav"));
        if(c is not null&&o.Stable&&Session.InputAvailable)
        {
            if(hook&&!reelIssued&&state["round_observed"]?.GetValue<bool>()!=true){state["round_observed"]=true;Session.SavePortState("fishing:"+BatchId,state);}
            if(navigating&&travelIssuedAt is {} issued&&c.CapturedAt>issued&&navigationActive){navigationSeen=true;state["navigation_seen"]=true;Session.SavePortState("fishing:"+BatchId,state);}
            else if(navigating&&navigationSeen&&travelIssuedAt is {} sent&&c.CapturedAt>sent&&o.View.Page is "scene" or "fishing")
            {
                // Closing the destination menu is not arrival. Require the
                // dispatched travel task to have appeared before its disappearance.
                navigationSettled=true;navigating=false;pendingPage=null;Session.EndNavigation("fishing:"+BatchId);
            }
            if(allowInput&&navigating&&!navigationSeen&&!Session.Guard.Paused&&S(Store.Read(),"status")=="running")
            {
                try{NavigationStep(o);}
                catch(InputBlockedException e)when(!Session.Guard.Paused){Session.ReportWarning("fishing_navigation",e.Message,BatchId);}
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
        if(cast||!o.Stable)Session.ReportWarning("fishing_cast_observation",$"page={o.View.Page}; cast={cast}; stable={o.Stable}; reason={Session.StabilityReason}; active={active}; mounted={mounted}",BatchId);
        return LastFrame=new(now,c?.CapturedAt.ToString("R",CultureInfo.InvariantCulture)??"",o.View.Page,Session.InputAvailable,o.Stable,navigationActive?true:navigationSettled?false:null,cast,leisure,confirm,active,hook,gauge,compass,mounted);
    }
    private void NavigationStep(UiObservation o)
    {
        // Once travel has been dispatched, only observe its result. In particular,
        // a short trip must not reopen the route from the scene before its task is seen.
        if(state["travel_issued"]?.GetValue<bool>()==true)return;
        var now=navigationTime.GetUtcNow().ToUnixTimeMilliseconds()/1000d;
        if(pendingPage is not null)
        {
            var expected=S(pendingStep,"expected_page");
            if(expected is not null?o.View.Page==expected:o.View.Page!=pendingPage)
            {Session.TraceNavigation(BatchId,NavigationDescription(pendingStep!,o),"confirmed");pendingPage=null;pendingStep=null;navigationRetries=scrollCount=0;}
            else if(now-pendingAt<(Session.Routes.Configuration["navigation_retry"]?["interval_s"]?.GetFiniteDouble()??8))return;
            else if(o.View.Page==pendingPage&&navigationRetries<(Session.Routes.Configuration["navigation_retry"]?["max_retries"]?.GetValue<int>()??4))
            {navigationRetries++;Session.Step(pendingStep!);pendingAt=now;Session.TraceNavigation(BatchId,NavigationDescription(pendingStep!,o),"retry_dispatched");return;}
            else
            {
                var reason="钓鱼导航等待超时："+NavigationDescription(pendingStep!,o);
                navigating=false;Session.EndNavigation("fishing:"+BatchId);Session.ReportWarning("fishing_navigation",reason,BatchId);throw new TimeoutException(reason);
            }
        }
        if(o.View.Page==S(nav,"detail_page"))
        {
            Session.NavigateTarget(S(nav,"start_target")!);travelIssuedAt=Session.LastCapture!.CapturedAt;state["travel_issued"]=true;Session.SavePortState("fishing:"+BatchId,state);Session.BeginNavigation("fishing:"+BatchId);Session.TraceNavigation(BatchId,S(nav,"start_target")!,"travel_dispatched");return;
        }
        var step=nav["steps"]!.AsArray().FirstOrDefault(s=>S(s,"page")==o.View.Page)??Session.Routes.Configuration["maintenance"]?["exit_navigation"]?[o.View.Page];if(step is null)return;
        var target=S(step,"target");
        if(target is not null&&!o.View.Targets.ContainsKey(target)&&o.View.Targets.ContainsKey("list.scroll"))
        {
            if(scrollCount>=12){navigating=false;throw new TimeoutException("钓鱼列表搜索已到上限："+NavigationDescription(step,o));}
            Session.ScrollListDown(o);scrollCount++;
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
        try{PerformCore(action);if(action.Kind is "move" or "start")Session.TraceNavigation(BatchId,"fishing."+action.Kind,"input_dispatched");}
        catch(InputRejectedBeforeDispatchException e){Session.ReportWarning("fishing_preflight",e.Message,action.Kind);throw;}
        catch(InputBlockedException e)when(!Session.Guard.Paused){Session.ReportWarning("fishing_preflight",e.Message,action.Kind);throw new InputRejectedBeforeDispatchException(e.Message);}
    }
    private void PerformCore(FishingAction action)
    {
        if(action.Kind is "start" or "reel" or "switch_mode" or "confirm_mode")
        {
            // ObserveFrame already selected this port's decoration context. Consume
            // reuses that review for its proof; dispatch checks system boundaries only.
            Consume("fishing."+action.Kind,action.Kind,_=>action.Kind switch{"start"=>movingForCast&&!active,"reel"=>(hook||gauge is not null)&&leisure==false,"switch_mode"=>leisure==true,_=>confirm},null);
            if(action.Kind=="start"){lastGaugeBox=null;clearSceneSince=null;state.Remove("round_started");state["round_observed"]=false;state["reel_issued"]=false;Session.SavePortState("fishing:"+BatchId,state);}
            else if(action.Kind=="reel"){clearSceneSince=null;state["reel_issued"]=true;Session.SavePortState("fishing:"+BatchId,state);}
            return;
        }
        UiObservation o;
        try{o=Session.LastObservation??throw new InputBlockedException("缺少钓鱼等待观察");if(!o.Stable||!Session.InputAvailable)throw new InputBlockedException("钓鱼画面未确认");}
        catch(InputBlockedException e){throw new InputRejectedBeforeDispatchException(e.Message);}
        if(action.Kind=="dismount"){Session.NavigateTarget("scene.dismount");return;}
        if(action.Kind=="navigate"){if(action.PointKey!=pointKey)throw new InputBlockedException("钓鱼点不匹配");navigationSeen=navigationSettled=false;travelIssuedAt=null;state["navigation_seen"]=false;state["travel_issued"]=false;Session.SavePortState("fishing:"+BatchId,state);pendingPage=null;pendingStep=null;navigationRetries=scrollCount=0;navigating=true;NavigationStep(o);return;}
        if(action.Kind=="move"){if(action.Direction is not ("w" or "s")||action.DurationSeconds is null or <=0 or >.3)throw new InputBlockedException("钓鱼移动参数无效");Session.Guard.PressKey(Session.Guard.ObserveCurrentTarget("fishing.move",o),action.Direction=="w"?(ushort)87:(ushort)83,TimeSpan.FromSeconds(action.DurationSeconds.Value));return;}
        if(action.Kind=="leave"){Session.Guard.PressKey(Session.Guard.ObserveCurrentTarget(o.View.Page=="scene"?"fishing.move":"fishing.leave",o),87,TimeSpan.FromMilliseconds(120));navigating=false;Session.EndNavigation("fishing:"+BatchId);return;}
        throw new InputBlockedException("钓鱼操作未配置");
    }
    public void InvalidatePreparation(){}
    public void MoveWhileObserving(FishingAction action,Func<FishingFrame,bool> observe)
    {
        if(action.Kind!="move"||action.Direction is not ("w" or "s")||action.DurationSeconds is not (>0 and <=.12))throw new InputBlockedException("移动轻按参数无效");
        var o=Session.LastObservation??throw new InputRejectedBeforeDispatchException("缺少移动等待观察");if(!o.Stable||o.View.Page is not ("scene" or "fishing"))throw new InputRejectedBeforeDispatchException("移动页面未确认");
        Session.Guard.PressKey(Session.Guard.ObserveCurrentTarget("fishing.move",o),action.Direction=="w"?(ushort)87:(ushort)83,TimeSpan.FromSeconds(action.DurationSeconds.Value));
        movingForCast=true;
        try
        {
            // Activation is the confirmed fixed W/S script. Do not spend its
            // short cast window capturing or reviewing a dynamic control.
            var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d;
            observe(new(now,"movement:"+now.ToString("R",CultureInfo.InvariantCulture),"fishing",
                Session.InputAvailable,true,FishingButton:true));
        }
        finally{movingForCast=false;}
    }
}
