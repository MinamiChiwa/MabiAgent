using System.Text.Json.Nodes;
using MabiAgent.Workflows;

namespace MabiAgent.Windows;

/// <summary>Shared fixed theatre route; only declared load waits capture stable HUD glyphs.</summary>
public sealed class ScreenPrivateTheaterRefresh
{
    readonly ScreenWorkflowSession session;readonly JsonObject configuration;readonly PrivateTheaterRefreshScript script;
    readonly Dictionary<string,JsonObject> steps;
    ScreenMaintenancePort? preparation;DateTimeOffset preparationDeadline;bool prepared;
    public PrivateTheaterRefreshStatus Status=>script.Status;
    public string? Failure=>script.Failure;
    public string? CurrentStep=>script.Current?.Id;
    public ScreenPrivateTheaterRefresh(ScreenWorkflowSession session,Func<DateTimeOffset>? completedAt=null)
    {
        this.session=session;configuration=session.Routes.Configuration["private_theater_refresh"]?.DeepClone()?.AsObject()??throw new InvalidDataException("缺少私人剧场固定脚本配置");
        var rows=(configuration["steps"] as JsonArray??throw new InvalidDataException("缺少私人剧场步骤")).OfType<JsonObject>().ToArray();
        steps=rows.ToDictionary(s=>s["id"]!.GetValue<string>(),StringComparer.Ordinal);
        script=new(new(rows.Select(s=>new PrivateTheaterStep(s["id"]!.GetValue<string>(),s["kind"]!.GetValue<string>() switch{"key"=>PrivateTheaterStepKind.PressKey,"click"=>PrivateTheaterStepKind.Click,"wait"=>PrivateTheaterStepKind.WaitForStable,_=>throw new InvalidDataException("剧场步骤类型无效")},s["id"]!.GetValue<string>(),TimeSpan.FromMilliseconds(s["delay_ms"]?.GetValue<int>()??300)){Timeout=TimeSpan.FromSeconds(s["timeout_s"]?.GetValue<int>()??30)}).ToArray()),completedAt??(()=>DateTimeOffset.UtcNow));
    }
    public void Start(DateTimeOffset now){preparationDeadline=now.AddSeconds(120);script.Start(now);}
    public void Pause()=>script.Pause();
    public bool Tick(DateTimeOffset now)
    {
        if(!prepared&&Status==PrivateTheaterRefreshStatus.Running)
        {
            var observed=session.Observe();
            if(observed.View.Page.StartsWith("notification_",StringComparison.Ordinal))
            {session.DismissNotice();return false;}
            if(observed.Stable&&observed.View.Page is "bag" or "repair_tools")
            {prepared=true;script.Pause();script.Continue(now);}
            else if(now<preparationDeadline)
            {
                preparation??=session.CreateMaintenancePort("inventory-refresh-prepare",new PreparationStore());
                preparation.Observe();
                if(preparation.ReleaseNavigation())
                {session.BeginRecoveryRecognition();prepared=true;script.Pause();script.Continue(now.AddSeconds(2));}
                return false;
            }
            else return script.Tick(now,_=>false);
        }
        var before=CurrentStep;var prior=Status;
        // The bag can already be open from warehouse supply or an interrupted
        // reversible route; treat the current stable bag as the open_bag result.
        if(CurrentStep=="open_bag")
        {var current=session.Observe();if(current.Stable&&current.View.Page is "bag" or "repair_tools" or "bag_search")script.Tick(now,_=>true);}
        var advanced=script.Tick(now,step=>
    {
        var source=steps[step.Id];if(step.Kind==PrivateTheaterStepKind.WaitForStable)return true;
        if(step.Kind==PrivateTheaterStepKind.PressKey)
        {
            var key=checked((ushort)source["key"]!.GetValue<int>());
            if(step.Id=="tools_tab")
            {
                var frame=session.Guard.Capture();
                if(session.Matcher.Anchor(frame.Image,"maint_tools_selected") is not null)return true;
                key=session.Matcher.Anchor(frame.Image,"bag_items_selected") is not null?(ushort)87:(ushort)83;
            }
            session.Guard.PressKey(key,TimeSpan.FromMilliseconds(80));return true;
        }
        var geometry=session.Guard.ReadGeometry();var size=configuration["client_size"]!.AsArray();
        var point=source["point"]!.AsArray();
        session.Guard.ClickAt(new((int)Math.Round(point[0]!.GetValue<int>()*(double)geometry.Width/size[0]!.GetValue<int>()),(int)Math.Round(point[1]!.GetValue<int>()*(double)geometry.Height/size[1]!.GetValue<int>())));return true;
    },target=>
    {
        if(target=="bag_ready"){var observed=session.Observe();return observed.Stable&&observed.View.Page is "bag" or "repair_tools";}
        var capture=session.Guard.Capture();var anchors=steps[target]["anchors"]!.AsArray().Select(n=>n!.GetValue<string>()).ToArray();
        return anchors.Length>0&&anchors.All(name=>session.Matcher.Anchor(capture.Image,name) is not null);
        });
        if(advanced)session.TraceNavigation("private_theater_refresh",before??"complete","script_step_dispatched");
        if(before!=CurrentStep||prior!=Status)session.TraceNavigation("private_theater_refresh",$"step={CurrentStep}; status={Status}; failure={Failure}","script_progress");
        return advanced;
    }
    sealed class PreparationStore:IWorkflowStore
    {public JsonObject? Read()=>null;public void Flush(JsonObject state)=>throw new InvalidOperationException("刷新准备只允许可逆导航");}
}
