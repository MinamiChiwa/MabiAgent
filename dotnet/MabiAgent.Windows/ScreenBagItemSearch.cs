using MabiAgent.Core;
namespace MabiAgent.Windows;

// Reversible item selection shared by selling and decomposition. Quantity stays in packet inventory.
internal sealed class ScreenBagItemSearch(ScreenWorkflowSession session)
{
    private int step;
    private bool itemsIssued;
    private double started;
    private int clearStep;private double clearStarted;
    public bool Used {get;private set;}
    private static double Now=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d;
    public void RestoreFilter()=>Used=true;
    public void Reset(){step=0;itemsIssued=false;started=Now;clearStep=0;clearStarted=0;}
    public (string Status,PixelPoint? Item) Tick(UiObservation observation,string name)
    {
        if(started==0)Reset();
        if(Now-started>45)return ("missing",null);
        var page=observation.View.Page;
        if(step==0)
        {
            if(page=="scene"){session.NavigateTarget("scene.open_bag");return ("running",null);}
            if(page=="bag_search"){session.Guard.PressKey(27,TimeSpan.FromMilliseconds(80));return ("running",null);}
            if(page is not ("bag" or "repair_tools"))return ("running",null);
            if(!observation.View.Targets.ContainsKey("bag.items_selected"))
            {if(!itemsIssued){session.NavigateTarget("bag.open_items");itemsIssued=true;}return ("running",null);}
            session.Guard.DragAt(session.Box(new PixelBox(839,139,841,141)).Center,session.Box(new PixelBox(1194,139,1196,141)).Center,12);
            step=1;return ("running",null);
        }
        if((step is 1 or 2 or 6)&&page=="bag"&&!observation.View.Targets.ContainsKey("bag.items_selected"))
        {step=0;itemsIssued=false;return ("running",null);}
        if(step==1&&page=="bag")
        {session.Guard.ClickAt(session.Box(new PixelBox(810,122,868,157)).Center);step=2;}
        else if(step==2&&page=="bag")
        {Used=true;session.NavigateTarget("bag.search_open");step=3;}
        else if(step==3&&page=="bag_search")
        {session.NavigateTarget("bag.search_reset");step=4;}
        else if(step==4&&page=="bag_search")
        {session.Guard.EditBagSearch(session.Guard.ObserveCurrentTarget("bag.search_input",observation),name);step=5;}
        else if(step==5&&page=="bag_search")
        {session.NavigateTarget("bag.search_apply");step=6;}
        else if(step==6&&page=="bag")
        {var item=session.FindBagItem(name);return (item is null?"missing":"found",item);}
        return ("running",null);
    }
    public bool Clear(UiObservation observation)
    {
        if(!Used)return true;
        if(clearStarted==0)clearStarted=Now;
        if(Now-clearStarted>30){Used=false;return true;}
        var page=observation.View.Page;
        if(page=="bag"&&clearStep==2){Used=false;clearStep=0;clearStarted=0;return true;}
        if(page=="bag")
        {
            if(!observation.View.Targets.ContainsKey("bag.items_selected")){session.NavigateTarget("bag.open_items");return false;}
            session.NavigateTarget("bag.search_open");return false;
        }
        if(page!="bag_search")return true;
        if(clearStep==0){session.NavigateTarget("bag.search_reset");clearStep=1;}
        else if(clearStep==1){session.NavigateTarget("bag.search_apply");clearStep=2;}
        return false;
    }

}
