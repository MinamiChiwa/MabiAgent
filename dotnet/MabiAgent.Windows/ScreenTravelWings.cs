using MabiAgent.Core;
using System.Text.Json.Nodes;
using MabiAgent.Workflows;
namespace MabiAgent.Windows;

/// <summary>Optional, one-attempt navigation consumable. Unknown quote never blocks ordinary walking.</summary>
public sealed class ScreenTravelWings
{
    private readonly object gate=new();
    private readonly ScreenWorkflowSession session;private readonly IWorkflowStore store;private readonly Func<JsonObject> settings;
    public ScreenTravelWings(ScreenWorkflowSession session,IWorkflowStore store,Func<JsonObject> settings){this.session=session;this.store=store;this.settings=settings;}
    internal PageView Decorate(ClientCapture c,PageView view)
    {
        if(view.Page!="scene"||view.Modal||session.CurrentNavigationId is null)return view;
        var icon=session.Matcher.Anchor(c.Image,"travel.wing_quote_icon");
        if(icon is null||session.Routes.Configuration["travel_wings"]?["use_box"] is not{} box)return view;
        // The panel travels with its icon; a fixed screen box drifts off the
        // button when the HUD shifts and would click outside it.
        var use=session.AnchoredBox("travel.wing_quote_icon",icon,NavigationProfile.ReadBox(box));
        return use is{} clickable?session.AddSurface(view,"travel.use_wings",clickable):view;
    }
    private QuantityValue? Quote()
    {
        var c=session.LastCapture;var cfg=session.Routes.Configuration["travel_wings"]?["quote"];if(c is null||cfg is null)return null;var icon=session.Matcher.Anchor(c.Image,"travel.wing_quote_icon");if(icon is null)return null;
        var a=cfg["offset"]!.AsArray();var scale=(double)c.Image.Width/session.Routes.Profile.Width;int X(int i)=>(int)Math.Round(a[i]!.GetValue<int>()*scale);
        var box=new PixelBox(icon.Box.Right+X(0),icon.Box.Top+X(1),Math.Min(c.Image.Width,icon.Box.Right+X(2)),icon.Box.Top+X(3));if(!box.IsInside(c.Image.Width,c.Image.Height))return null;
        return session.QuantityReader.ReadNavigationQuoteOnce(c.Image,box,cfg["threshold"]?.GetValue<int>()??100,cfg["min_confidence"]?.GetFiniteDouble()??.85);
    }
    public JsonObject Snapshot()=>store.Read()??new JsonObject{["status"]="idle"};
    public bool TryUse(string navigationId,bool arrived=false){lock(gate)return TryUseCore(navigationId,arrived);}
    private bool TryUseCore(string navigationId,bool arrived)
    {
        if(string.IsNullOrWhiteSpace(navigationId)||session.Guard.Paused)return false;var state=Snapshot();state["attempts"]??=new JsonObject();
        var revision=session.LocationRevision;var instance=InstanceKey(navigationId,revision);
        if(state["pending"] is JsonObject pending)
        {
            var previous=pending["navigation_id"]!.GetValue<string>();
            var previousInstance=pending["instance"]?.GetValue<string>();
            // Legacy intent has no instance proof; retain the original fail-closed
            // check rather than replaying an unknown dispatch from the same id.
            if(previousInstance is null&&previous==navigationId||previousInstance==instance)
            {if(arrived){state["status"]="arrived_cost_unverified";store.Flush(state);}return false;}
            // The dispatched result remains unknown. A different navigation
            // instance is independent even when its logical workflow id repeats.
            var unresolved=previousInstance??previous;
            state["unresolved"]??=new JsonObject();state["unresolved"]![unresolved]=pending.DeepClone();
            state["attempts"]![unresolved]??=pending["at"]?.DeepClone();state.Remove("pending");store.Flush(state);
        }
        var options=settings()["travel"];if(options?["auto_use_wings"]?.GetValue<bool>()==false||state["attempts"]![instance] is not null)return false;
        if(!TryReserve(options,out var reserve))return false;
        // This is the first usable frame carrying the quote. The fare changes
        // while walking, so do not wait for the number to settle or read again.
        var o=session.Observe();if(!session.InputAvailable||!o.View.Targets.ContainsKey("travel.use_wings"))return false;
        var value=Quote();var at=session.LastCapture!.CapturedAt;
        state["attempts"]![instance]=at;state["decisions"]??=new JsonObject();
        var decision=new JsonObject{["at"]=at,["cost"]=value?.Quantity,["holding"]=value?.Capacity,["reserve"]=reserve,["use"]=value is{Quantity:>0,Capacity:not null}&&value.Capacity-value.Quantity>=reserve,["instance"]=instance};
        var reading=session.QuantityReader.LastNavigationQuoteReading;
        decision["quote_text"]=reading?.Text;decision["quote_confidence"]=reading?.Confidence;
        state["decisions"]![navigationId]=decision;state["status"]="walking";store.Flush(state);
        if(value is null||value.Quantity<=0||value.Capacity is null)
        {session.ReportWarning("travel_wings",$"首次精灵之翼用量未识别，本次继续步行；text={reading?.Text}; confidence={reading?.Confidence}; error={reading?.Error}",navigationId);return false;}
        if(value.Capacity-value.Quantity<reserve)
        {session.ReportWarning("travel_wings","精灵之翼使用后不足保留量，继续步行",navigationId);return false;}
        state["pending"]=new JsonObject{["kind"]="use_wings",["navigation_id"]=navigationId,["instance"]=instance,["cost"]=value.Quantity,["holding"]=value.Capacity,["exact"]=value.CapacityExact,["at"]=at};state["attempts"]![instance]=at;state["status"]="unknown";store.Flush(state);
        try{session.Consume("travel.use_wings","travel:"+instance,store,"use_wings",_=>
            session.CurrentNavigationId==navigationId&&session.LocationRevision==revision);}
        catch(InputRejectedBeforeDispatchException)
        {state.Remove("pending");state["status"]="not_sent";store.Flush(state);return false;}
        session.TraceNavigation(navigationId,"travel.use_wings","input_dispatched");return true;
    }
    private string InstanceKey(string navigationId,long revision)=>session.Scope.SessionId+":"+navigationId+"@"+revision;
    private static bool TryReserve(JsonNode? travel,out long reserve)
    {
        reserve=0;
        var node=travel?["wing_reserve_min"];
        return node is null||node.TryGetFiniteLong(out reserve)&&reserve>=0;
    }
}
