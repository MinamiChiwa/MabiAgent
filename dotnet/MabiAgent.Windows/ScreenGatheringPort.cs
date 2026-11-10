using MabiAgent.Core;
using System.Text.Json.Nodes;
using MabiAgent.Workflows;
namespace MabiAgent.Windows;

public sealed class ScreenGatheringPort:ScreenWorkflowPort,IGatheringPort
{
    private readonly string key;private readonly JsonNode route;private JsonNode nav;private JsonNode? observer;private readonly JsonObject state;private readonly ScreenWorkbenchFlow workbench;
    private int idleCount;private double? idleSince;private string? pendingPage,pendingExpectedPage;private double pendingAt,pendingSince;private JsonNode? pendingStep;private int navigationRetries;private readonly Dictionary<string,int> scrollCounts=[];private string? listSearchPage;private int listSearchAttempt;private ScreenMaintenancePort? releaseNavigation;
    public bool HasExtensionPending=>workbench.Available&&workbench.Progress.State!=WorkbenchFlowState.Completed;
    public bool TickExtension()
    {
        if(!HasExtensionPending)return false;
        if(workbench.Progress.State==WorkbenchFlowState.AwaitingSemantic&&workbench.CurrentCard is{} semantic)
        {
            var saved=Store.Read();var role=S(semantic,"role");
            var confirmed=saved?["pending"] is null&&(role=="gathering_start"?(saved?["submitted"]?.GetValue<int>()??0)>0&&state["bound"]?.GetValue<bool>()==true:role=="observation"&&(saved?["completed"]?.GetValue<int>()??0)>0);
            if(!confirmed){var waiting=workbench.Tick(Session.LastObservation);if(waiting.State==WorkbenchFlowState.Failed)throw new InputBlockedException(waiting.Message);return waiting.State!=WorkbenchFlowState.AwaitingSemantic;}workbench.CompleteSemantic(true);
        }
        var progress=workbench.Tick(Session.LastObservation);
        if(progress.State==WorkbenchFlowState.Failed)throw new InputBlockedException(progress.Message);
        return progress.State is WorkbenchFlowState.Running or WorkbenchFlowState.Idle;
    }
    public override bool CanReleaseForeground()=>pendingPage is null&&state["started"]?.GetValue<bool>()!=true&&base.CanReleaseForeground();
    public ScreenGatheringPort(ScreenWorkflowSession session,string routeKey,string batchId,IWorkflowStore store):base(session,batchId,store)
    {key=routeKey;route=session.Routes.Configuration["gathering_routes"]?[key]??throw new ArgumentException("采集点未配置");nav=route["navigation"]!;observer=session.Routes.Configuration["gathering_observer"]?["routes"]?[key];state=session.PortState("gathering:"+batchId);workbench=new(session,routeKey);if(state["source_fallback_index"]?.GetValue<int>() is{} fallback&&route["fallback_sources"] is JsonArray choices&&fallback>=0&&fallback<choices.Count){nav=choices[fallback]!["navigation"]!;observer=choices[fallback]!["observer"];}}
    private bool TryNextSource()
    {
        if(state["started"]?.GetValue<bool>()==true||Store.Read()?["pending"] is not null)return false;
        var next=(state["source_fallback_index"]?.GetValue<int>()??-1)+1;
        if(route["fallback_sources"] is not JsonArray choices||next>=choices.Count)return false;
        var source=choices[next]!;nav=source["navigation"]!;observer=source["observer"];state["source_fallback_index"]=next;state["source_node"]=source["node"]!.DeepClone();Save();
        scrollCounts.Clear();listSearchPage=null;listSearchAttempt=0;navigationRetries=0;pendingPage=pendingExpectedPage=null;pendingStep=null;Session.TraceNavigation(BatchId,S(source,"node")??"fallback","unavailable_source_fallback");return true;
    }
    protected override PageView Decorate(ClientCapture capture,PageView view)
    {
        if(state["started"]?.GetValue<bool>()==true&&state["bound"]?.GetValue<bool>()!=true&&Session.Matcher.Anchor(capture.Image,"missing_consumables") is{} hit)
            return new("gathering_missing_consumables",false,[hit],new Dictionary<string,PixelPoint>(),[hit.Box]);
        return view;
    }
    public bool DismissMissingConsumables()
    {
        if(Session.LastObservation is not {Stable:true,View.Page:"gathering_missing_consumables"}||!Session.InputAvailable)return false;
        Session.Guard.PressKey(0x1B,TimeSpan.FromMilliseconds(80));
        state.Clear();Save();Session.EndNavigation("gathering:"+BatchId);idleSince=null;idleCount=0;
        pendingPage=pendingExpectedPage=null;pendingStep=null;return true;
    }
    private bool Match(ClientCapture c,string? name)=>name is not null&&Session.Matcher.Anchor(c.Image,"gathering_observer."+name,minimum:.8) is not null;
    private void Save()=>Session.SavePortState("gathering:"+BatchId,state);
    private void ObserveListSearchPage(string page)
    {
        // Reopened lists start a fresh downward pass; the total page budget survives.
        // A transient unrecognized frame does not establish a new list entry.
        if(page!="unknown"&&listSearchPage!=page){listSearchPage=page;listSearchAttempt=0;}
    }
    private void Verify(GatheringRoute requested){if(requested.Key!=key||requested.TargetItemId!=route["target_item_id"]!.GetValue<long>())throw new InputBlockedException("采集点与端口不匹配");}
    public GatheringFrame Observe(GatheringRoute requested,string actionId)
        =>ObserveFrame(requested,actionId,true);
    public GatheringFrame ObserveOnly(GatheringRoute requested,string actionId)
        =>ObserveFrame(requested,actionId,false);
    private GatheringFrame ObserveFrame(GatheringRoute requested,string actionId,bool allowInspection)
    {
        Verify(requested);var o=Read();if(!o.Stable||!Session.InputAvailable){idleCount=0;idleSince=null;return new(o.View.Page,Session.InputAvailable,false);}
        ObserveListSearchPage(o.View.Page);
        if(o.View.Page=="gathering_missing_consumables")return new(o.View.Page,true,true,key,MissingConsumables:true);
        var c=Session.LastCapture!;var cfg=Session.Routes.Configuration["gathering_observer"];var started=state["started"]?.GetValue<bool>()==true&&S(state,"session")==Session.Scope.SessionId&&S(state,"action_id")==actionId;
        var taskTitle=Match(c,S(cfg,"task_title_template"));
        var runningControl=Match(c,S(cfg,"running_template"));
        // The exact selected detail and durable start intent already bind this native task.
        // During that start's bounded arrival window, its shared stable title is enough;
        // old unknown actions cannot claim a later unrelated task through this path.
        var awaitingOwnTask=started&&state["awaiting_task_until"]?.GetValue<double>()>=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d;
        var expected=awaitingOwnTask&&taskTitle;
        if(!expected)
        {
            expected=observer is not null&&Names(observer["task_anchors"]) is {Length:>0} names&&names.All(n=>Match(c,n));
            if(observer is not null&&S(observer,"task_name") is {} name&&observer["task_name_region"] is {} region)expected=Session.QuantityReader.Exact(c.Image,Session.Box(region),name)&&(Names(observer["task_anchors"]).Length==0||expected);
        }
        var status="unknown";var evidence=new JsonObject{["source"]="screen",["capture"]=c.Image.Sha256,["navigation_idle_confirmed"]=false};
        if(started&&expected)
        {
            // The native task title appears while still travelling. Keep the
            // navigation instance alive so a later first wing quote is handled.
            state["bound"]=true;state["task_id"]="screen-gathering:"+key+":"+actionId;status="active";idleCount=0;idleSince=null;
            // The title also appears during arrival. Witness the running control
            // before accepting this native batch's later idle boundary.
            if(runningControl)state["progressed"]=true;
            Save();
        }
        else if(started&&state["bound"]?.GetValue<bool>()==true&&(taskTitle||runningControl))
        {
            idleCount=0;idleSince=null;status="active";
            if(runningControl&&state["progressed"]?.GetValue<bool>()!=true){state["progressed"]=true;Save();}
        }
        else if(o.View.Page=="scene"&&!taskTitle&&!expected&&!runningControl&&!(started&&state["bound"]?.GetValue<bool>()!=true&&Session.CurrentNavigationId=="gathering:"+BatchId))
        {
            idleSince??=c.CapturedAt;idleCount++;var settled=idleCount>=(cfg?["completion_idle_observations"]?.GetValue<int>()??3)&&c.CapturedAt-idleSince>=(cfg?["completion_idle_seconds"]?.GetFiniteDouble()??2);
            evidence["task_labels_absent"]=true;
            evidence["navigation_idle_confirmed"]=settled&&(!started||state["progressed"]?.GetValue<bool>()==true);status=!started?"idle":settled&&state["bound"]?.GetValue<bool>()==true&&state["progressed"]?.GetValue<bool>()==true?"complete":"unknown";
        }
        else{idleCount=0;idleSince=null;}
        evidence["running_control_visible"]=runningControl;
        // Native attempts are never treated as produced quantity. Unknown yield frees navigation for independent work.
        if(status=="complete")evidence["navigation_idle_confirmed"]=true;
        if(status=="complete"){state["complete"]=true;Session.EndNavigation("gathering:"+BatchId);Save();}
        if(started&&state["complete"]?.GetValue<bool>()==true){status="complete";evidence["navigation_idle_confirmed"]=true;}
        long? actual=null;
        var owned=started&&state["bound"]?.GetValue<bool>()==true;
        return new(o.View.Page,true,true,owned||o.View.Page==S(nav,"detail_page")?key:null,o.View.Page==S(nav,"detail_page")&&o.View.Targets.ContainsKey(S(nav,"start_target")!),owned?state["task_id"]?.DeepClone():null,status,actual,evidence,false);
    }
    public void Navigate(GatheringRoute requested)
    {
        Verify(requested);var o=Session.LastObservation??throw new InputRejectedBeforeDispatchException("缺少采集等待观察");if(!o.Stable||!Session.InputAvailable)return;
        ObserveListSearchPage(o.View.Page);
        if(state["started"]?.GetValue<bool>()==true&&state["progressed"]?.GetValue<bool>()!=true)
        {Session.Travel?.TryUse("gathering:"+BatchId);return;}
        if(pendingPage is not null)
        {
            if((pendingExpectedPage is{} expected?o.View.Page==expected:o.View.Page!=pendingPage)||o.View.Page!=S(nav,"detail_page")&&o.View.Page.StartsWith("point_detail_",StringComparison.Ordinal))
            {pendingPage=pendingExpectedPage=null;pendingStep=null;navigationRetries=0;}
            else
            {
                var now=Session.LastCapture!.CapturedAt;var retry=Session.Routes.Configuration["navigation_retry"];
                if(now-pendingSince>(retry?["timeout_s"]?.GetFiniteDouble()??90))
                {
                    // A locked list row can remain visible without opening its detail.
                    // Only this reversible point-selection timeout may change source.
                    if(pendingStep is not null&&S(pendingStep,"page")=="logging_nodes"&&o.View.Page=="logging_nodes"&&TryNextSource())return;
                    throw new InputBlockedException("采集导航结果未确认");
                }
                // Retry only reversible navigation while the original page and target
                // remain visible. Never repeat Start or a dispatched native batch.
                if(pendingStep is not null&&o.View.Page==pendingPage&&now-pendingAt>=(retry?["interval_s"]?.GetFiniteDouble()??8)
                    &&navigationRetries<(retry?["max_retries"]?.GetValue<int>()??4)
                    &&(S(pendingStep,"target") is not{} retryTarget||o.View.Targets.ContainsKey(retryTarget)))
                {Session.Step(pendingStep);navigationRetries++;pendingAt=now;Session.TraceNavigation(BatchId,S(pendingStep,"target")??"navigation","retry_dispatched");}
                return;
            }
        }
        if(o.View.Page!=S(nav,"detail_page")&&o.View.Page.StartsWith("point_detail_",StringComparison.Ordinal)&&o.View.Targets.ContainsKey("gathering.close"))
        {Session.NavigateTarget("gathering.close");WaitForNavigation(o,new JsonObject{["target"]="gathering.close"});return;}
        if(releaseNavigation is not null){if(!releaseNavigation.ReleaseNavigation())return;releaseNavigation=null;}
        if(workbench.TryNavigate(o))return;
        var step=nav["steps"]!.AsArray().FirstOrDefault(s=>S(s,"page")==o.View.Page);
        if(step is null)
        {
            if(o.View.Page==S(nav,"detail_page"))return;
            // Production preview may leave its device/menu open before the next
            // material turn. Reuse the declared shared close target.
            if(o.View.Targets.ContainsKey("page.close")||o.View.Targets.ContainsKey("recipe_detail.close"))
            {var close=o.View.Targets.ContainsKey("page.close")?"page.close":"recipe_detail.close";Session.NavigateTarget(close);WaitForNavigation(o,new JsonObject{["target"]=close});return;}
            if(o.View.Targets.ContainsKey("bag.close"))
            {Session.NavigateTarget("bag.close");WaitForNavigation(o,new JsonObject{["target"]="bag.close"});return;}
            if(Session.Routes.Configuration["maintenance"]?["exit_navigation"]?[o.View.Page] is not null||o.View.Page.StartsWith("storage_",StringComparison.Ordinal)||o.View.Page=="npc_dialogue")
            {releaseNavigation=Session.CreateMaintenancePort(BatchId+":release",Store);releaseNavigation.ReleaseNavigation();return;}
            throw new InputBlockedException("采集路线缺少当前页导航");
        }
        var target=S(step,"target");
        if(target is not null&&!o.View.Targets.ContainsKey(target)&&o.View.Targets.ContainsKey("list.scroll"))
        {
            // Keep each list's search budget when recognition or navigation returns to it.
            var scrollCount=scrollCounts.GetValueOrDefault(o.View.Page);
            if(scrollCount>=ScreenWorkflowSession.ListSearchLimit){if(TryNextSource())return;throw new InputBlockedException("采集点列表搜索已到上限");}
            Session.ScrollListSearch(o,listSearchAttempt);listSearchAttempt++;scrollCounts[o.View.Page]=scrollCount+1;
            return;
        }
        Session.Step(step);WaitForNavigation(o,step);
    }
    private void WaitForNavigation(UiObservation observation,JsonNode step)
    {pendingPage=observation.View.Page;pendingExpectedPage=S(step,"expected_page");pendingStep=step;pendingAt=pendingSince=Session.LastCapture!.CapturedAt;navigationRetries=0;}
    public void Start(GatheringRoute requested,string actionId)
    {
        Verify(requested);var target=S(nav,"start_target")!;
        var beforeState=(JsonObject)state.DeepClone();
        try{Consume(target,"gathering",o=>
        {
            if(o.View.Page!=S(nav,"detail_page")||Store.Read()?["pending"]?["action_id"]?.GetValue<string>()!=actionId)return false;
            state["started"]=true;state["action_id"]=actionId;state["session"]=Session.Scope.SessionId;
            state["awaiting_task_until"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d+300;
            Save();return true;
        });}
        catch(MabiAgent.Core.InputNotDispatchedException)
        {state.Clear();foreach(var (name,value) in beforeState)state[name]=value?.DeepClone();Save();throw;}
        idleSince=null;idleCount=0;
        Session.BeginNavigation("gathering:"+BatchId,300);
    }
}
