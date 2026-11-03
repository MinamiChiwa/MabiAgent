using MabiAgent.Core;
using System.Text.Json.Nodes;
using MabiAgent.Workflows;
namespace MabiAgent.Windows;

/// <summary>Optional, one-attempt navigation consumable. Unknown quote never blocks ordinary walking.</summary>
public sealed class ScreenTravelWings
{
    private readonly ScreenWorkflowSession session;private readonly IWorkflowStore store;private readonly Func<JsonObject> settings;
    public ScreenTravelWings(ScreenWorkflowSession session,IWorkflowStore store,Func<JsonObject> settings){this.session=session;this.store=store;this.settings=settings;}
    internal PageView Decorate(ClientCapture c,PageView view)
    {
        if(view.Page!="scene"||view.Modal||session.CurrentNavigationId is null)return view;
        var icon=session.Matcher.Anchor(c.Image,"travel.wing_quote_icon");
        if(icon is null||session.Routes.Configuration["travel_wings"]?["use_box"] is not{} box)return view;
        return session.AddSurface(view,"travel.use_wings",session.Box(box));
    }
    private QuantityValue? Quote()
    {
        var c=session.LastCapture;var cfg=session.Routes.Configuration["travel_wings"]?["quote"];if(c is null||cfg is null)return null;var icon=session.Matcher.Anchor(c.Image,"travel.wing_quote_icon");if(icon is null)return null;
        var a=cfg["offset"]!.AsArray();var scale=(double)c.Image.Width/session.Routes.Profile.Width;int X(int i)=>(int)Math.Round(a[i]!.GetValue<int>()*scale);
        var box=new PixelBox(icon.Box.Right+X(0),icon.Box.Top+X(1),icon.Box.Right+X(2),icon.Box.Top+X(3));if(!box.IsInside(c.Image.Width,c.Image.Height))return null;
        return session.QuantityReader.ReadNavigationQuoteOnce(c.Image,box,cfg["threshold"]?.GetValue<int>()??100,cfg["min_confidence"]?.GetFiniteDouble()??.85);
    }
    public JsonObject Snapshot()=>store.Read()??new JsonObject{["status"]="idle"};
    public bool TryUse(string navigationId,bool arrived=false)
    {
        if(string.IsNullOrWhiteSpace(navigationId)||session.Guard.Paused)return false;var state=Snapshot();state["attempts"]??=new JsonObject();
        if(state["pending"] is JsonObject pending)
        {
            var previous=pending["navigation_id"]!.GetValue<string>();
            if(previous==navigationId)
            {if(arrived){state["status"]="arrived_cost_unverified";store.Flush(state);}return false;}
            // Keep the old unknown use without letting it block another trip.
            state["unresolved"]??=new JsonObject();state["unresolved"]![previous]=pending.DeepClone();
            state["attempts"]![previous]??=pending["at"]?.DeepClone();state.Remove("pending");store.Flush(state);
        }
        var options=settings()["travel"];if(options?["auto_use_wings"]?.GetValue<bool>()==false||state["attempts"]![navigationId] is not null)return false;
        var reserve=options?["wing_reserve_min"]?.GetValue<long>()??0;if(reserve<0)return false;
        var o=session.Observe();if(!o.Stable||!session.InputAvailable||!o.View.Targets.ContainsKey("travel.use_wings"))return false;
        var value=Quote();var at=session.LastCapture!.CapturedAt;
        state["attempts"]![navigationId]=at;state["decisions"]??=new JsonObject();
        var decision=new JsonObject{["at"]=at,["cost"]=value?.Quantity,["holding"]=value?.Capacity,["reserve"]=reserve,["use"]=value is{Quantity:>0,Capacity:not null}&&value.Capacity-value.Quantity>=reserve};
        state["decisions"]![navigationId]=decision;state["status"]="walking";store.Flush(state);
        if(value is null||value.Quantity<=0||value.Capacity is null)
        {session.ReportWarning("travel_wings","首次精灵之翼用量未识别，本次继续步行",navigationId);return false;}
        if(value.Capacity-value.Quantity<reserve)
        {session.ReportWarning("travel_wings","精灵之翼使用后不足保留量，继续步行",navigationId);return false;}
        state["pending"]=new JsonObject{["kind"]="use_wings",["navigation_id"]=navigationId,["cost"]=value.Quantity,["holding"]=value.Capacity,["exact"]=value.CapacityExact,["at"]=at};state["attempts"]![navigationId]=at;state["status"]="unknown";store.Flush(state);
        try{session.Consume("travel.use_wings","travel:"+navigationId,store,"use_wings",_=>
        {var p=settings()["travel"];return session.CurrentNavigationId==navigationId&&p?["auto_use_wings"]?.GetValue<bool>()!=false&&value.Capacity-value.Quantity>=(p?["wing_reserve_min"]?.GetValue<long>()??0);});}
        catch(InputRejectedBeforeDispatchException)
        {state.Remove("pending");state["status"]="not_sent";store.Flush(state);return false;}
        session.TraceNavigation(navigationId,"travel.use_wings","input_dispatched");return true;
    }
}
