using MabiAgent.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using MabiAgent.Workflows;
namespace MabiAgent.Windows;

public sealed record NavigationTestProgress(bool Completed,string? Error);
public sealed record NavigationTestTrace(string Target,string Phase);

public sealed class ScreenMaintenancePort:ScreenWorkflowPort,IMaintenancePort
{
    private readonly JsonNode config;private bool? bold,materials;private string? lastAction;
    private string? routeKey,stage;private int searches,zoomPasses;private double nextAt,dispatchedAt;private string? lastPage;private readonly HashSet<string> dispatched=[];
    private JsonNode? awaiting;private string? awaitingSignature;private byte[]? beforeMap;private RgbImage? beforeTerrain;private string? beforeTargets;private bool dispatchedSearch;
    private int dialogueSteps,releaseRetries;
    private int releaseDialogueSteps;private double lastReleaseDialogueAt=-1;
    private long? previewQuote;private long previewEpoch;
    private string? rejectedKey;private double rejectedAt;
    public NavigationTestTrace? LastTestNavigationTrace{get;private set;}
    // Whether the last ReleaseNavigation call actually dispatched a reversible
    // step. The caller budgets exit failures; a dispatched step is progress.
    internal bool LastReleaseDispatched{get;private set;}
    internal bool ReleaseWaiting
    {
        get
        {
            if(awaiting is null||routeKey is null||!routeKey.StartsWith("release",StringComparison.Ordinal))return false;
            var now=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
            return now<nextAt||now-dispatchedAt<(awaiting["timeout_s"]?.GetFiniteDouble()??15);
        }
    }
    void TraceNavigation(string target,string phase){LastTestNavigationTrace=new(target,phase);Session.TraceNavigation(BatchId,target,phase);}
    public ScreenMaintenancePort(ScreenWorkflowSession session,string batchId,IWorkflowStore store):base(session,batchId,store)
    {config=session.Routes.Configuration["maintenance"]??new JsonObject();}
    private int Pixels(RgbImage image,JsonNode? box,Func<int,int,int,bool> predicate)
    {if(box is null)return 0;var p=image.Crop(Session.Box(box)).Pixels;int count=0;for(var i=0;i<p.Length;i+=3)if(predicate(p[i],p[i+1],p[i+2]))count++;return count;}
    protected override PageView Decorate(ClientCapture c,PageView view)
    {
        bold=materials=null;if(view.Page!="tidy"||view.Modal)return view;var tidy=config["tidy"];
        if(tidy?["bold_region"] is {} b)
        {var orange=Pixels(c.Image,b,(r,g,blue)=>r>180&&r-g>30&&g>45&&blue<150);var gray=Pixels(c.Image,b,(r,g,blue)=>Math.Abs(r-g)<20&&Math.Abs(g-blue)<20&&r>55&&r<150);bold=orange>100?true:gray>100?false:null;}
        if(bold==false&&tidy?["materials_region"] is {} m)
        {var green=Pixels(c.Image,m,(r,g,b)=>g-r>45&&g-b>10&&g>90);var dark=Pixels(c.Image,m,(r,g,b)=>r<55&&g<65&&b<85);materials=green>150?true:dark>500?false:null;}
        if(bold!=false||materials!=false)view=Session.RemoveTarget(view,"tidy.prepare");return view;
    }
    public MaintenanceFrame Observe()
    {
        var o=Read();var page=o.View.Page=="bag"?"backpack":o.View.Page;var id=Session.LastCapture?.CapturedAt.ToString("R",CultureInfo.InvariantCulture)??"";
        if(page is "repair_dialog" or "repair_quote" or "repair_equipment")Session.EndNavigation("maintenance:"+BatchId);
        if(!o.Stable||!Session.InputAvailable)return new(id,page,Session.InputAvailable,false);
        var weight=page is "backpack" or "repair_tools"?ReadWeight():null;
        var payment=Store.Read()?["pending"]?["action"]?.GetValue<string>()=="confirm_repair";
        var gold=(page is "repair_equipment" or "repair_quote" or "repair_result")||(payment&&(page is "backpack" or "repair_tools"))
            ?Session.ReadGold(()=>Session.ConfirmedQuantity("maintenance.gold:"+page,config["gold_by_page"]?[page]??config["gold"])?.Quantity):Session.CurrentPacketGold();
        if(previewEpoch!=Session.InputEpoch||page is not ("repair_equipment" or "repair_quote"))previewQuote=null;
        previewEpoch=Session.InputEpoch;
        if(page=="repair_equipment")previewQuote=Session.ReadQuantity(config["repair"]?["preview_quote"])?.Value?.Quantity;
        var quote=page=="repair_quote"?ResolveRepairBudget(Session.ReadQuantity(config["repair"]?["quote"]),previewQuote):(null,false);
        var count=page=="tidy"&&bold==false&&materials==false?ReadTidyCount(Session.QuantityReader,Session.LastCapture!.Image,Session.Box(config["tidy"]!["count"]!["region"]!),config["tidy"]!["count"]!):null;
        var equipped=page=="repair_tools"&&config["repair"]?["equipped_tool_slots"] is JsonArray slots&&slots.Count(s=>Pixels(Session.LastCapture!.Image,s,(r,g,b)=>Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))>45&&Math.Max(r,Math.Max(g,b))>100)>=150)>=(config["repair"]?["minimum_equipped_tools"]?.GetValue<int>()??1);
        var notNeeded=page=="repair_dialog"&&o.View.Targets.ContainsKey("repair.not_needed")||page=="repair_equipment"&&o.View.Targets.ContainsKey("repair.tools_status")&&Pixels(Session.LastCapture!.Image,config["repair"]?["tools_label_region"],(r,g,b)=>r>190&&g>190&&b>190)<10;
        var resultVisible=page=="repair_result"||page=="npc_dialogue"&&Store.Read()?["pending"]?["action"]?.GetValue<string>()=="confirm_repair";
        var backpackFull=Session.Matcher.Anchor(Session.LastCapture!.Image,"overlay.backpack_full_toast") is not null;
        return new(id,page,true,true,weight?.Quantity,weight?.Capacity,gold,quote.Item1,o.View.Targets.ContainsKey("repair.gold")?"gold":null,o.View.Targets.ContainsKey("tidy.confirm")||o.View.Targets.ContainsKey("repair.confirm"),bold,materials,count?.Quantity,resultVisible,page=="tidy_result",equipped,notNeeded,BackpackFull:backpackFull,QuoteIsUpperBound:quote.Item2);
    }
    internal static QuantityValue? ReadTidyCount(ScreenQuantityReader reader,RgbImage image,PixelBox region,JsonNode spec)
    {
        // Disabled zero-count text is gray; a white-only mask loses it.
        // This is the cleanup selection count, never item inventory.
        var text=reader.ColorText(image,region,spec["min_confidence"]?.GetFiniteDouble()??.75);
        return text is null?null:QuantityParser.Parse(text,"labelled_integer",prefix:spec["prefix"]!.GetValue<string>(),suffix:spec["suffix"]!.GetValue<string>());
    }
    private QuantityValue? ReadWeight()
    {
        var spec=config["weight"];if(spec is null||Session.LastCapture is not{} capture)return null;
        // The warning badge bobs vertically. Locate its numeric line once
        // inside a padded search envelope, then run exactly one color OCR read.
        var region=FindWeightTextRegion(capture.Image,Session.Box(spec["region"]!));if(region is null)return null;
        var text=Session.QuantityReader.ColorText(capture.Image,region.Value,spec["min_confidence"]?.GetFiniteDouble()??.75);
        return text is null?null:QuantityParser.Parse(text,"fixed_ratio");
    }
    internal static PixelBox? FindWeightTextRegion(RgbImage image,PixelBox search)
    {
        if(!search.IsInside(image.Width,image.Height))return null;
        var support=new int[search.Height];var pixels=image.Pixels;
        static bool Ink(int r,int g,int b)=>r>=140&&r-g>=40&&b<=100||Math.Min(r,Math.Min(g,b))>=140&&Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))<40;
        for(var y=0;y<search.Height;y++)for(var x=0;x<search.Width;x++)
        {
            var i=((search.Top+y)*image.Width+search.Left+x)*3;int r=pixels[i],g=pixels[i+1],b=pixels[i+2];
            if(Ink(r,g,b))support[y]++;
        }
        var minimum=Math.Max(4,search.Width/32);var groups=new List<(int Top,int Bottom,int Ink)>();
        for(var y=0;y<support.Length;)
        {
            if(support[y]<minimum){y++;continue;}var top=y;var ink=0;
            while(y<support.Length&&support[y]>=minimum){ink+=support[y];y++;}
            if(y-top is >=10 and <=40)groups.Add((top,y-1,ink));
        }
        foreach(var group in groups.OrderByDescending(g=>g.Ink))
        {
            var left=search.Right;var right=search.Left;var ink=0;
            for(var y=search.Top+group.Top;y<=search.Top+group.Bottom;y++)
                for(var x=search.Left;x<search.Right;x++)
                {
                    var i=(y*image.Width+x)*3;int r=pixels[i],g=pixels[i+1],b=pixels[i+2];
                    if(!Ink(r,g,b))continue;left=Math.Min(left,x);right=Math.Max(right,x);ink++;
                }
            var height=group.Bottom-group.Top+1;var width=right-left+1;
            if(left>right||width<32||width<height*2||ink<width*2)continue;
            const int margin=4;
            return new(Math.Max(search.Left,left-margin),Math.Max(search.Top,search.Top+group.Top-margin),
                Math.Min(search.Right,right+margin+1),Math.Min(search.Bottom,search.Top+group.Bottom+margin+1));
        }
        return null;
    }
    public void ResetNavigation(){previewQuote=null;routeKey=stage=lastPage=rejectedKey=awaitingSignature=null;awaiting=null;beforeMap=null;beforeTerrain=null;searches=zoomPasses=dialogueSteps=releaseDialogueSteps=releaseRetries=0;lastReleaseDialogueAt=-1;nextAt=0;dispatchedSearch=false;LastTestNavigationTrace=null;dispatched.Clear();Session.EndNavigation("maintenance:"+BatchId);}
    private RgbImage Terrain()=>Session.LastCapture!.Image.Crop(Session.Box(new PixelBox(200,150,1050,780)));
    private bool ZoomStillChangesScale()
    {
        if(beforeMap is null||!MapChanged(beforeMap,MapSample(Session.LastCapture!.Image)))return false;
        if(beforeTerrain is null)return true;
        using var before=beforeTerrain.ToMat();using var after=Terrain().ToMat();
        // At the zoom limit, a pinch can still pan the map. Translation alone
        // must not block the stable NPC portrait stage.
        return !MapMotionEvidence.Measure(before,after).SameScale;
    }
    internal static byte[] MapSample(RgbImage image)
    {
        var sample=new byte[40*24*3];var pixels=image.Pixels;
        for(var y=0;y<24;y++)for(var x=0;x<40;x++)
        {var source=(((y+3)*image.Height/32)*image.Width+(x+3)*image.Width/48)*3;var dest=(y*40+x)*3;for(var c=0;c<3;c++)sample[dest+c]=pixels[source+c];}
        return sample;
    }
    internal static bool MapChanged(byte[] before,byte[] after)
    {var changed=0;for(var i=0;i<before.Length;i+=3)if(Math.Max(Math.Abs(before[i]-after[i]),Math.Max(Math.Abs(before[i+1]-after[i+1]),Math.Abs(before[i+2]-after[i+2])))>25)changed++;return changed>before.Length/3*.10;}
    public bool ReleaseNavigation()
    {
        LastReleaseDispatched=false;
        var o=Session.LastObservation;if(o is null||!o.Stable||!Session.InputAvailable)return false;
        if(o.View.Page=="npc_dialogue")
        {
            // Closing a storage panel can return to the NPC's dialogue before
            // the exit menu. Advance it without invoking the inn room X key.
            awaiting=null;awaitingSignature=null;dispatched.Clear();Session.EndNavigation("maintenance:"+BatchId);
            var now=Session.LastCapture!.CapturedAt;if(now-lastReleaseDialogueAt<1)return false;
            if(releaseDialogueSteps>=16)throw new InputBlockedException("仓库退出对话超过步骤上限");
            var target=o.View.Targets.ContainsKey("dialog.skip")?"dialog.skip":"dialog.advance";
            Session.NavigateTarget(target);lastReleaseDialogueAt=now;releaseDialogueSteps++;LastReleaseDispatched=true;
            TraceNavigation(target,"release_dialogue");return false;
        }
        if(o.View.Page=="scene")
        {
            if(o.View.Targets.ContainsKey("storage.room_exit"))
            {
                var exit=new JsonObject{["target"]="storage.room_exit",["wait_for_pages"]=new JsonArray("storage_room_exit"),["timeout_s"]=15};
                var prior=LastTestNavigationTrace;
                var error=NavigateCore("release_room",exit,false,false,o);if(error is not null)throw new InputBlockedException(error);
                LastReleaseDispatched=!ReferenceEquals(prior,LastTestNavigationTrace)&&LastTestNavigationTrace?.Phase=="dispatch";return false;
            }
            ResetNavigation();return true;
        }
        var step=config["exit_navigation"]?[o.View.Page]??(o.View.Targets.ContainsKey("page.close")?new JsonObject{["target"]="page.close"}:null)
            ??(o.View.Page=="recipe_detail"&&o.View.Targets.ContainsKey("recipe_detail.close")?new JsonObject{["target"]="recipe_detail.close"}:null);
        if(step is null)return false;
        var previous=LastTestNavigationTrace;
        var reason=NavigateCore("release",step as JsonObject,false,false,o);if(reason is not null)throw new InputBlockedException("维护页面退出未确认："+reason);
        LastReleaseDispatched=!ReferenceEquals(previous,LastTestNavigationTrace)&&LastTestNavigationTrace?.Phase=="dispatch";return false;
    }
    internal bool ReleaseFieldExit(string target)
    {
        LastReleaseDispatched=false;
        var o=Session.LastObservation;if(o is null||!o.Stable||!Session.InputAvailable||o.View.Page!="scene"||!o.View.Targets.ContainsKey(target))return false;
        // A field exit may load straight into town; do not wait for the storage confirmation page here.
        LastReleaseDispatched=true;Session.NavigateTarget(target);return true;
    }
    public string? Navigate(string destination)=>NavigateCore(destination,null,false,false,Session.LastObservation??throw new InputRejectedBeforeDispatchException("缺少维护等待观察"));
    internal string? NavigateObserved(string destination,UiObservation observation)=>NavigateCore(destination,null,false,false,observation);
    /// <summary>Tests an explicit non-consuming navigation step through the existing navigation confirmation state.</summary>
    public NavigationTestProgress TestNavigationStep(JsonObject step)
    {
        var result=NavigateCore("workbench_step",step,true,false);
        return new(result=="workbench_navigation_complete",result=="workbench_navigation_complete"?null:result);
    }
    /// <summary>Tests one imported route, preserving its stage alternatives and bounded search plan.</summary>
    public NavigationTestProgress TestNavigationRoute(JsonObject route)
    {
        var result=NavigateCore("workbench_route",route,false,true);
        return new(result=="workbench_navigation_complete",result=="workbench_navigation_complete"?null:result);
    }
    private string? NavigateCore(string destination,JsonObject? testRoute,bool singleStep,bool singleRoute,UiObservation? observation=null)
    {
        var o=observation??Read();if(!o.Stable||!Session.InputAvailable)return null;var page=o.View.Page;var key=destination+":"+page;
        var now=Session.LastCapture!.CapturedAt;if(now<nextAt)return null;
        if(awaiting is {} sent)
        {
            if(S(sent,"kind")=="zoom_max")
            {
                if(page!=lastPage)return "map_changed_during_zoom";
                if(ZoomStillChangesScale())
                {
                    if(zoomPasses>=3)return "map_maximum_zoom_not_observed";
                    var map=MapSample(Session.LastCapture.Image);var terrain=Terrain();
                    try{Session.Step(sent);}
                    catch(Exception error) when(error is InputRejectedBeforeDispatchException or InputBlockedException{TargetChangedBeforeDispatch:true})
                    {return ReobserveRejectedStep(key,S(sent,"target")!,now,error);}
                    zoomPasses++;beforeMap=map;beforeTerrain=terrain;WaitAfterDispatch(sent);
                    rejectedKey=null;TraceNavigation(S(sent,"target")!,"zoom_max");return null;
                }
                // Saturation is observed after a full positive wheel sweep. An already maximal map is valid.
                awaiting=null;beforeMap=null;beforeTerrain=null;stage=S(sent,"next")??stage;searches=0;
                TraceNavigation(S(sent,"target")!,"maximum_observed");
                if(singleStep||singleRoute&&!dispatchedSearch&&S(sent,"next") is null)return "workbench_navigation_complete";
            }
            else
            {
            var expected=Names(sent["wait_for_pages"]);if(expected.Length==0&&S(sent,"expected_page") is{} expectedPage)expected=[expectedPage];
            // Dialogue text is mutable content and cannot be a completion anchor.
            // Progress is driven by page/target transitions or declared map changes.
            var changed=expected.Length>0?expected.Contains(page):true;
            if(testRoute is not null&&expected.Length>0&&page==lastPage)
                changed=changed&&(beforeTargets!=TargetSignature(o.View)||beforeMap is not null&&MapChanged(beforeMap,MapSample(Session.LastCapture.Image)));
            // A bounded map search may hit an edge or an already open world view.
            // It advances the search plan without declaring the destination reached.
            var searchComplete=expected.Length==0&&page==lastPage&&sent["search_only"]?.GetValue<bool>()==true;
            if(!changed&&!searchComplete)
            {
                if(now-dispatchedAt<(sent["timeout_s"]?.GetFiniteDouble()??15))return null;
                if(destination=="release"&&releaseRetries<3&&o.View.Targets.ContainsKey(S(sent,"target")!))
                {
                    // Closing a still-visible panel is reversible. Retry only
                    // after its declared wait, never a consuming confirmation.
                    releaseRetries++;if(awaitingSignature is{} sentSignature)dispatched.Remove(sentSignature);
                    awaiting=null;beforeMap=null;Session.EndNavigation("maintenance:"+BatchId);nextAt=now+(sent["retry_delay_s"]?.GetFiniteDouble()??1.5);
                    TraceNavigation(S(sent,"target")!,"release_retry");return null;
                }
                Session.EndNavigation("maintenance:"+BatchId);return "navigation_result_not_observed:"+S(sent,"target");
            }
            awaiting=null;beforeMap=null;releaseRetries=0;
            if(page=="npc_dialogue"&&lastPage!=page){if(++dialogueSteps>16)return "dialogue_step_limit";dispatched.Clear();}
            if(expected.Length>0)Session.EndNavigation("maintenance:"+BatchId);
            TraceNavigation(S(sent,"target")!,searchComplete?"search_step_complete":"confirmed");
            if(singleStep||singleRoute&&!dispatchedSearch&&S(sent,"next") is null)return "workbench_navigation_complete";
            if(key==routeKey&&S(sent,"next") is {} next){stage=next;searches=0;}
            }
        }
        if(key!=routeKey){routeKey=key;stage=null;searches=dialogueSteps=0;dispatched.Clear();}
        var route=testRoute??config["navigation"]?[destination]?[page]??config["exit_navigation"]?[page]??(o.View.Targets.ContainsKey("page.close")?new JsonObject{["target"]="page.close"}:null);if(route is null)return "navigation_calibration_missing:"+destination+":"+page;
        if(testRoute is null&&destination=="backpack_tidy"&&page=="bag"&&!o.View.Targets.ContainsKey("bag.items_selected"))
            route=new JsonObject{["target"]="bag.open_items"};
        JsonNode? step=route;
        if(route["stages"] is {} stages)
        {
            stage??=S(route,"start");var current=stages[stage!]??throw new InputBlockedException("导航阶段配置无效");
            step=current["choices"]?.AsArray().FirstOrDefault(c=>o.View.Targets.ContainsKey(S(c,"target")!)&&Names(c?["requires"]).All(o.View.Targets.ContainsKey));
            if(step is null){var search=current["search"]?.AsArray();if(search is null||searches>=search.Count)return "navigation_search_exhausted:"+destination;step=search[searches];}
        }
        if(step is null)return "navigation_calibration_missing:"+destination;
        var target=S(step,"target")!;var signature=stage+":"+target+":"+searches;
        if(dispatched.Contains(signature)&&lastPage==page)return now-dispatchedAt>30?"navigation_result_not_observed":null;
        if(!o.View.Targets.ContainsKey(target))return null;
        dispatchedSearch=route["stages"]?[stage!]?["search"]?.AsArray().Any(s=>ReferenceEquals(s,step))==true;
        if(dispatchedSearch)searches++;
        dispatched.Add(signature);lastPage=page;dispatchedAt=now;nextAt=now+(step["settle_s"]?.GetFiniteDouble()??.3);
        awaiting=step;awaitingSignature=signature;beforeMap=null;beforeTargets=TargetSignature(o.View);if(S(step,"kind")=="zoom_max"){zoomPasses=1;beforeMap=MapSample(Session.LastCapture.Image);beforeTerrain=Terrain();}else beforeTerrain=null;
        if(step["wait_for_pages"] is not null)Session.BeginNavigation("maintenance:"+BatchId,step["timeout_s"]?.GetFiniteDouble()??120);
        try{Session.Step(step);}
        catch(Exception error) when(error is InputRejectedBeforeDispatchException or InputBlockedException{TargetChangedBeforeDispatch:true})
        {
            return ReobserveRejectedStep(key,target,now,error);
        }
        WaitAfterDispatch(step);rejectedKey=null;TraceNavigation(target,"dispatch");return null;
    }
    private void WaitAfterDispatch(JsonNode step)
    {
        // A touch sweep can take longer than the declared settle interval.
        // Give the game that interval after delivery, before reading its result.
        dispatchedAt=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
        var settle=step["settle_s"]?.GetFiniteDouble()??.3;
        nextAt=settle>0?dispatchedAt+settle:0;
    }
    private string? ReobserveRejectedStep(string key,string target,double now,Exception error)
    {
        // Only a proven pre-dispatch rejection can clear the wait and reacquire
        // the target. A partially delivered gesture must retain its protection.
        var rejected=key+":"+awaitingSignature;
        awaiting=null;if(awaitingSignature is{} signature)dispatched.Remove(signature);awaitingSignature=null;
        if(dispatchedSearch)searches--;dispatchedSearch=false;
        beforeMap=null;beforeTerrain=null;Session.EndNavigation("maintenance:"+BatchId);TraceNavigation(target,"reobserve:"+error.Message);
        if(rejectedKey!=rejected){rejectedKey=rejected;rejectedAt=now;}
        return now-rejectedAt>=15?"navigation_target_unstable:"+target+":"+error.Message:null;
    }
    private static string TargetSignature(PageView view)=>string.Join("|",view.Targets.OrderBy(t=>t.Key).Select(t=>t.Key+":"+t.Value));
    internal static (long? Budget,bool UpperBound) ResolveRepairBudget(QuantityReading? reading,long? preview)
    {
        var secondary=ReadRepairBudget(reading);
        if(preview is not >=0)return secondary;
        if(secondary.Budget is null)return (preview,false);
        if(!secondary.UpperBound)return secondary;
        var interval=reading!.Readings.Any(r=>(r.Text??"").Contains('.'))?1000:10000;
        return preview>=secondary.Budget-interval&&preview<secondary.Budget?(preview,false):secondary;
    }
    internal static (long? Budget,bool UpperBound) ReadRepairBudget(QuantityReading? reading)
    {
        if(reading?.Value is {} exact)return (exact.Quantity,false);
        // The compact display truncates to tenths of ten thousand. Budget the
        // whole next interval; this is never recorded as an actual payment.
        if(reading is null||reading.Readings.Count==0)return (null,false);
        long? budget=null;
        foreach(var line in reading.Readings)
        {
            if(line.Error is not null||line.Confidence is null or <.90 or >1)return (null,false);
            var match=System.Text.RegularExpressions.Regex.Match((line.Text??"").Trim(),@"^([0-9]{1,9})(?:[.]([0-9]))?[萬万]$");
            if(!match.Success)return (null,false);
            var whole=long.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);
            var value=match.Groups[2].Success?(whole*10+long.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture)+1)*1000:(whole+1)*10000;
            if(budget.HasValue&&budget!=value)return (null,false);
            budget=value;
        }
        return (budget,true);
    }
    public void Act(string action)
    {
        try{if(Session.LastObservation is not{Stable:true}||!Session.InputAvailable)throw new InputBlockedException("维护画面未确认");}
        catch(InputBlockedException e){throw new InputRejectedBeforeDispatchException(e.Message);}
        var target=action switch{"disable_bold"=>"tidy.bold","deselect_materials"=>"tidy.materials","prepare_tidy"=>"tidy.prepare","confirm_tidy"=>"tidy.confirm","confirm_repair"=>"repair.confirm","auto_equip"=>"bag.auto_equip",_=>throw new InputBlockedException("维护操作未配置")};
        if(action=="disable_bold"&&bold!=true||action=="deselect_materials"&&materials!=true||action=="prepare_tidy"&&(bold!=false||materials!=false))throw new InputBlockedException("整理保护选项未确认");
        if(action is "confirm_tidy" or "confirm_repair" or "auto_equip")Consume(target,action,o=>action switch{"confirm_tidy"=>o.View.Page=="tidy_confirm","confirm_repair"=>ReviewRepairPayment(o),_=>o.View.Page=="repair_tools"});
        else Session.NavigateTarget(target);lastAction=action;
    }
    public string? LastAction=>lastAction;
    private bool ReviewRepairPayment(UiObservation o)
    {
        if(o.View.Page!="repair_quote"||!o.View.Targets.ContainsKey("repair.gold"))return false;
        var saved=Store.Read()?["repair"];var expected=saved?["quote"]?.GetValue<long>();var before=saved?["before_gold"]?.GetValue<long>();
        var reserve=Math.Max(40000,saved?["gold_reserve"]?.GetValue<long>()??40000);
        if(expected is null or <0||before is null||before<expected||before-expected<reserve)return false;
        Session.InvalidatePacketGold();return true;
    }
    public string ShopCalibrationStatus=>Session.Routes.Configuration["purchase"]?["confirm_target"] is not null?"calibrated":"shop_calibration_missing";
}
