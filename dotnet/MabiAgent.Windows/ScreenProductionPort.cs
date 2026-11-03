using System.Text.Json.Nodes;
using MabiAgent.Core;
using MabiAgent.Workflows;
namespace MabiAgent.Windows;

public sealed class ScreenProductionPort:ScreenWorkflowPort,IProductionPort
{
    private readonly RecipeRoute recipe;private readonly DeviceRoute device;private readonly ScreenWorkbenchFlow workbench;
    private string? observedRecipe;private int[]? empty,ready;private bool arrived;
    private int[]? queueEmpty;private double queueAt;private long queueEpoch;private bool queueArrived;
    private string? pendingNavigation;private string? pendingFrom;private double navigationAt;private int scrollCount,navigationRetries;
    private readonly JsonObject navigationState;
    private readonly TimeProvider navigationClock;
    private double NavigationNow=>navigationClock.GetUtcNow().ToUnixTimeMilliseconds()/1000d;
    private bool DeviceTravel=>navigationState["device_travel"]?.GetValue<bool>()==true;
    private static readonly IReadOnlySet<string> ProcessingWaitPages=new HashSet<string>{"skills","category_page"};
    public bool RemoteDevicePreview=>true;
    public bool HasExtensionPending=>workbench.Available&&workbench.Progress.State!=WorkbenchFlowState.Completed;
    public bool TickExtension()
    {
        if(!HasExtensionPending)return false;
        if(workbench.Progress.State==WorkbenchFlowState.AwaitingSemantic&&workbench.CurrentCard is{} semantic)
        {
            var state=Store.Read();var role=S(semantic,"role");var count=state?["count"]?.GetValue<int>()??1;
            var confirmed=state?["pending"] is null&&(role=="production"?(state?["submitted"]?.GetValue<int>()??0)>=count:role=="result"&&(state?["completed"]?.GetValue<int>()??0)>=count);
            if(!confirmed){var waiting=workbench.Tick(Session.LastObservation);if(waiting.State==WorkbenchFlowState.Failed)throw new InputBlockedException(waiting.Message);return waiting.State!=WorkbenchFlowState.AwaitingSemantic;}workbench.CompleteSemantic(true);
        }
        var progress=workbench.Tick(Session.LastObservation);
        if(progress.State==WorkbenchFlowState.Failed)throw new InputBlockedException(progress.Message);
        return progress.State is WorkbenchFlowState.Running or WorkbenchFlowState.Idle;
    }
    public override bool CanReleaseForeground()=>!DeviceTravel&&pendingNavigation is null&&base.CanReleaseForeground();
    public ScreenProductionPort(ScreenWorkflowSession session,string recipeKey,string batchId,IWorkflowStore store,TimeProvider? navigationClock=null):base(session,batchId,store)
    {recipe=session.Routes.Recipes.TryGetValue(recipeKey,out var found)?found:throw new ArgumentException("配方未配置");device=session.Routes.Devices[recipe.Device];workbench=new(session,recipeKey);navigationState=session.PortState("production:"+batchId);this.navigationClock=navigationClock??TimeProvider.System;}
    protected override PageView Decorate(ClientCapture c,PageView view)
    {
        empty=null;ready=null;observedRecipe=null;arrived=false;if(view.Modal)return view;
        var automaticArrival=navigationState["device_arrived"]?.GetValue<bool>()==true;
        if(automaticArrival&&(view.Page==device.QueuePage||view.Page==recipe.Page))view=view with{NeedsDeviceArrival=false};
        var boxes=device.Slots.Select(Session.Box).ToArray();
        if(view.Page==device.QueuePage||view.Page==recipe.Page&&Session.All(c,recipe.ConfirmAnchors))
        {
            empty=SlotRecognition.EmptyIndices(c.Image,boxes)?.ToArray();ready=SlotRecognition.ReadyIndices(c.Image,boxes)?.ToArray();
            arrived=automaticArrival||!view.NeedsDeviceArrival&&Session.All(c,device.ArrivalAnchors);
            if(view.Page==device.QueuePage){queueEmpty=empty;queueAt=c.CapturedAt;queueEpoch=Session.InputEpoch;queueArrived=arrived;}
            if(view.Page==recipe.Page)
            {
                // Detail overlays can cover the slot grid. Reuse only the immediately preceding
                // same-guard queue inspection; any pause/resume or age expiration invalidates it.
                if(empty is null&&queueEpoch==Session.InputEpoch&&queueArrived&&(automaticArrival||c.CapturedAt-queueAt<=5))empty=queueEmpty;
                observedRecipe=recipe.Key;var hit=Session.Matcher.Anchor(c.Image,recipe.CraftAnchor);var button=Session.Box(recipe.CraftButtonBox);
                if(hit is not null&&button.Contains(hit.Box.Center)&&Enabled(c.Image,button)&&empty is {Length:>0}&&!view.NeedsDeviceArrival)
                    view=Session.AddTarget(view,"production.craft",hit);
                // Detail equipment anchors also positively identify the device.
                arrived=!view.NeedsDeviceArrival&&queueArrived&&queueEpoch==Session.InputEpoch&&empty is not null
                    &&(automaticArrival||c.CapturedAt-queueAt<=5);
            }
            else if(ready is {Length:>0})
            {var hit=device.CollectAnchors.Select(a=>Session.Matcher.Anchor(c.Image,a)).FirstOrDefault(h=>h is not null);if(hit is not null&&!view.NeedsDeviceArrival)view=Session.AddTarget(view,"production.collect",hit);}
        }
        if(view.Page==device.QueuePage)
        {
            var gotoButton=Session.Routes.Profile.Templates["goto_device"];view=Session.AddSurface(view,device.GotoTarget,Session.Box(gotoButton.ControlBox??gotoButton.Box));
            if(view.Targets.TryGetValue(recipe.OpenTarget,out var point))
            {var layout=ReviewedLayout.For(c.Image.Width,c.Image.Height,c.Native.Geometry.Dpi);var targets=view.Targets.ToDictionary();targets[recipe.OpenTarget]=new(point.X+(int)Math.Round(recipe.OpenClickOffset.X*(double)layout.Width/layout.ReferenceWidth),point.Y+(int)Math.Round(recipe.OpenClickOffset.Y*(double)layout.Height/layout.ReferenceHeight));view=view with{Targets=targets};}
        }
        if(!view.Targets.ContainsKey("page.close")&&(view.Page.EndsWith("_panel",StringComparison.Ordinal)||view.Page is "category_page" or "skills" or "recipe_detail"))
            foreach(var anchor in Names(Session.Routes.Profile.Definition["close_anchors"])){var hit=Session.Matcher.Anchor(c.Image,anchor);if(hit is not null){view=Session.AddTarget(view,"page.close",hit);break;}}
        return view;
    }
    internal static bool Enabled(RgbImage image,PixelBox box)
    {var p=image.Crop(box).Pixels;var count=0;for(var i=0;i<p.Length;i+=3)if(p[i+1]-p[i]>20&&p[i+2]-p[i]>30&&p[i+2]>80)count++;return count>=300;}
    public ProductionFrame Observe()
    {
        var o=pendingNavigation=="skills.open_processing"?ReadFocused(c=>Session.RecognizeDeclaredPages(c,ProcessingWaitPages)):Read();
        if(DeviceTravel)
        {
            if(o.View.Page!="device_travel_confirm")
            {navigationState.Remove("travel_confirm_attempts");navigationState.Remove("travel_confirm_at");}
            // Closing the panel starts the native trip. Its automatic reopening
            // is the user-confirmed arrival signal; no timer or task icon.
            if(o.View.Page!=device.QueuePage&&navigationState["panel_closed"]?.GetValue<bool>()!=true)
            {navigationState["panel_closed"]=true;SaveNavigation();}
            else if(o.Stable&&o.View.Page==device.QueuePage&&navigationState["panel_closed"]?.GetValue<bool>()==true)
            {
                navigationState["device_travel"]=false;navigationState["device_arrived"]=true;SaveNavigation();
                arrived=queueArrived=true;pendingNavigation=pendingFrom=null;scrollCount=0;
                Session.EndNavigation("device:"+BatchId);Session.TraceNavigation(BatchId,"device page reopened: "+device.QueuePage,"arrival_observed");
            }
        }
        else if(o.Stable&&o.View.Page=="scene"&&navigationState["device_arrived"]?.GetValue<bool>()==true)
        {navigationState["device_arrived"]=false;SaveNavigation();}
        // Observe the reversible result when it occurs. A recipe overlay may
        // open and auto-close after craft before Navigate is called again.
        if(!DeviceTravel&&pendingNavigation is not null&&o.Stable&&o.View.Page!=pendingFrom)
        {pendingNavigation=pendingFrom=null;scrollCount=navigationRetries=0;}
        if(!o.Stable)Session.ReportWarning("production_observation",$"phase={S(Store.Read(),"phase")}; waiting={pendingNavigation}; from={pendingFrom}; page={o.View.Page}; reason={Session.StabilityReason}",BatchId);
        if(arrived&&!DeviceTravel)Session.EndNavigation("device:"+BatchId);return new(o.View.Page,Session.InputAvailable,o.Stable,empty,observedRecipe,o.View.Targets.ContainsKey("production.craft"),o.View.Targets.ContainsKey("production.collect"),Session.ProductionDuration(recipe.DurationSeconds),ready,false,arrived,device.Slots.Count,NavigationActive:DeviceTravel);
    }
    private void SaveNavigation()=>Session.SavePortState("production:"+BatchId,navigationState);
    private void VerifyRecipe(ProductionRecipe requested){if(requested.Key!=recipe.Key||requested.Device!=recipe.Device)throw new InputBlockedException("配方与端口不匹配");}
    public void Click(string kind,ProductionRecipe requested)
    {
        VerifyRecipe(requested);
        if(kind is not ("craft" or "collect"))throw new InputBlockedException("生产动作未配置");
        Consume("production."+kind,kind,_=>
        {
            var pending=Store.Read()?["pending"];var before=pending?["before_empty"]?.AsArray().Select(v=>v!.GetValue<int>()).ToHashSet();
            if(empty is null||before is null||!before.SetEquals(empty)||!arrived)return false;
            if(kind=="craft")return observedRecipe==recipe.Key&&empty.Length>0;
            var targets=pending?["target_slots"]?.AsArray().Select(v=>v!.GetValue<int>()).ToHashSet();
            var owned=Store.Read()?["owned_slots"]?.AsArray().Select(v=>v!.GetValue<int>()).ToHashSet()??[];
            if(pending?["collection_owners"] is JsonObject owners)foreach(var row in owners)foreach(var slot in row.Value!.AsArray())owned.Add(slot!.GetValue<int>());
            var approval=pending?["collect_all_approval"];
            var approved=approval?["source"]?.GetValue<string>()=="user_explicit"&&approval?["device"]?.GetValue<string>()==recipe.Device
                &&approval?["expires_at"]?.GetFiniteDouble()>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d
                &&targets is not null&&targets.Overlaps(owned);
            return targets is {Count:>0}&&(targets.IsSubsetOf(owned)||approved)&&ready is not null&&targets.SetEquals(ready);
        });
    }
    public void Navigate(string destination,ProductionRecipe requested)
    {
        VerifyRecipe(requested);
        var o=Session.LastObservation??Read();if(!o.Stable||!Session.InputAvailable)return;var page=o.View.Page;
        if(DeviceTravel)
        {
            // A cross-region trip first asks to travel to the device's town.
            // This reversible confirmation never completes the arrival wait.
            if(page!="device_travel_confirm"||!o.View.Targets.ContainsKey("device.confirm_travel"))return;
            var attempts=navigationState["travel_confirm_attempts"]?.GetValue<int>()??0;
            if(attempts>0&&NavigationNow-(navigationState["travel_confirm_at"]?.GetFiniteDouble()??NavigationNow)<5)return;
            if(attempts>=3)throw new InputBlockedException("设备跨区前往确认未关闭，请检查后继续");
            navigationState["travel_confirm_attempts"]=attempts+1;navigationState["travel_confirm_at"]=NavigationNow;SaveNavigation();
            Session.NavigateTarget("device.confirm_travel");Session.TraceNavigation(BatchId,"device.confirm_travel","input_dispatched");return;
        }
        if(pendingNavigation is not null)
        {
            if(page!=pendingFrom||pendingNavigation==device.GotoTarget&&arrived){pendingNavigation=null;scrollCount=navigationRetries=0;}
            else if(destination=="leave"&&pendingNavigation=="page.close")
            {
                if(NavigationNow-navigationAt<5)return;
                if(navigationRetries<2&&o.View.Targets.ContainsKey(pendingNavigation))
                {Session.NavigateTarget(pendingNavigation);navigationRetries++;navigationAt=NavigationNow;Session.TraceNavigation(BatchId,"page.close","exit_retry_dispatched");return;}
                pendingNavigation=pendingFrom=null;navigationRetries=0;throw new InputBlockedException("设备页关闭失败，已保留加工槽，请检查后继续");
            }
            else if(NavigationNow-navigationAt>45)throw new InputBlockedException("导航结果未确认，请复核");else return;
        }
        if(destination!="leave"&&!(page==device.QueuePage&&(destination=="queue"||!arrived))&&workbench.TryNavigate(o))return;
        string? target=null;
        if(page=="production_complete")target="completion.confirm";
        else if(destination=="leave")target=page=="recipe_detail"?device.RecipeCloseTarget:page=="scene"?null:o.View.Targets.ContainsKey("page.close")?"page.close":o.View.Targets.ContainsKey("bag.close")?"bag.close":null;
        else if(page=="scene")target="scene.open_skills";
        else if(page=="skills")target="skills.open_processing";
        else if(page=="category_page")target=device.CategoryTarget;
        else if(page==device.QueuePage)
        {
            if(destination is "queue" or "recipe"&&!arrived&&o.View.Targets.ContainsKey(device.GotoTarget))target=device.GotoTarget;
            else if(destination is "recipe" or "recipe_preview")
            {
                target=recipe.OpenTarget;
                if(!o.View.Targets.ContainsKey(target)&&o.View.Targets.ContainsKey("list.scroll")){if(scrollCount++>=12)throw new InputBlockedException("配方列表搜索已到上限");Session.ScrollListDown(o);return;}
            }
        }
        else if(page=="recipe_detail")target=device.RecipeCloseTarget;
        else if(o.View.Targets.ContainsKey("bag.close"))target="bag.close";
        else if(o.View.Targets.ContainsKey("page.close"))target="page.close";
        else if(Session.Routes.Profile.Definition["travel_confirmations"]?[page] is {} confirm)target=confirm.GetValue<string>();
        if(target is null)return;
        if(Session.Routes.Configuration["custom_navigation_nodes"]?[recipe.Key] is {} configuredNodes)
        {
            var required=target=="scene.open_skills"?"scene":target=="skills.open_processing"?"skills":target==device.CategoryTarget?"category":target==recipe.OpenTarget?"open_recipe":null;
            if(required is not null&&!Names(configuredNodes).Contains(required))throw new InputBlockedException("自定义路线已删除导航节点："+required);
        }
        if(!o.View.Targets.ContainsKey(target))throw new InputBlockedException("导航目标未识别："+target);
        Session.NavigateTarget(target);
        Session.TraceNavigation(BatchId,$"target={target}; from={page}; destination={destination}","input_dispatched");
        if(target==device.GotoTarget)
        {navigationState["device_travel"]=true;navigationState["panel_closed"]=false;navigationState["device_arrived"]=false;SaveNavigation();Session.BeginNavigation("device:"+BatchId);}
        else if(destination=="leave"&&page==device.QueuePage){navigationState["device_arrived"]=false;SaveNavigation();}
        pendingNavigation=target;pendingFrom=page;navigationAt=NavigationNow;
    }
}
