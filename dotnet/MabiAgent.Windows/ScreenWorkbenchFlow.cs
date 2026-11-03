using System.Text.Json.Nodes;

namespace MabiAgent.Windows;

public enum WorkbenchFlowState { Idle,Running,AwaitingSemantic,Completed,Failed }
public sealed record WorkbenchFlowProgress(WorkbenchFlowState State,string? CardId,string Message,int Completed,int Total);

/// <summary>Card scheduling over the session's existing observation, target proof and input guard.</summary>
public sealed class ScreenWorkbenchFlow
{
    readonly ScreenWorkflowSession session;readonly JsonObject? definition;readonly JsonObject[] cards;readonly HashSet<string>? selection;
    readonly Dictionary<string,int> visits=[];readonly Dictionary<string,int> retries=[];readonly TimeProvider time;
    int index,completed;double enteredAt;bool entered,dispatched,initialized,expectedBefore;string? fromPage;double dispatchedAt;
    public bool Available=>definition is not null;
    public bool HasDispatchedInput {get;private set;}
    public JsonObject? CurrentCard=>index<cards.Length?(JsonObject)cards[index].DeepClone():null;
    public WorkbenchFlowProgress Progress {get;private set;}
    public ScreenWorkbenchFlow(ScreenWorkflowSession session,string runtimeKey,IEnumerable<string>? selectedCardIds=null,TimeProvider? timeProvider=null)
        :this(session,session.Routes.Configuration["workbench_chains"]?[runtimeKey] as JsonObject,selectedCardIds,timeProvider){}
    public ScreenWorkbenchFlow(ScreenWorkflowSession session,JsonObject? flow,IEnumerable<string>? selectedCardIds=null,TimeProvider? timeProvider=null)
    {
        this.session=session;definition=flow is null?null:(JsonObject)flow.DeepClone();time=timeProvider??TimeProvider.System;
        selection=selectedCardIds?.ToHashSet(StringComparer.Ordinal);
        cards=(definition?["cards"] as JsonArray??[]).OfType<JsonObject>().Where(c=>c["enabled"]?.GetValue<bool>()!=false).Where(c=>selection is null||selection.Contains(S(c,"id"))).ToArray();
        Progress=new(Available?WorkbenchFlowState.Idle:WorkbenchFlowState.Completed,null,Available?"准备测试":"使用原任务流程",0,cards.Length);
    }
    public WorkbenchFlowProgress Tick(UiObservation? observation=null)
    {
        if(!Available||Progress.State is WorkbenchFlowState.Completed or WorkbenchFlowState.Failed)return Progress;
        try
        {
            if(index<cards.Length&&new[]{"fishing.settings_","settings.nickname","settings.hide_all","fishing.prepare_menu"}.Any(retired=>cards[index].ToJsonString().Contains(retired,StringComparison.Ordinal)))
                return Set(WorkbenchFlowState.Failed,"此用户步骤引用已退休的钓鱼昵称设置，请修改该步骤；原内容保留");
            var now=time.GetUtcNow().ToUnixTimeMilliseconds()/1000d;
            if(index>=cards.Length)return Set(WorkbenchFlowState.Completed,"流程完成");
            if(!entered){entered=true;enteredAt=now;var entering=cards[index];var enteringId=S(entering,"id");visits[enteringId]=visits.GetValueOrDefault(enteringId)+1;if(visits[enteringId]>32||visits.Values.Sum()>1024)throw new InputBlockedException("任务链循环达到上限："+S(entering,"name",enteringId));session.TraceNavigation("workbench",enteringId,"enter");}
            if(now-enteredAt>(cards[index]["timeout_seconds"]?.GetValue<int>()??15))return FailOrBranch(cards[index],"步骤超时："+S(cards[index],"name",S(cards[index],"id")));
            var fixedCard=cards[index];var fixedAction=S(fixedCard,"action","observe");
            if(fixedAction=="delay")
            {session.Guard.ReadGeometry();if((now-enteredAt)*1000>=(fixedCard["delay_ms"]?.GetValue<int>()??200))Advance(fixedCard,true);return Progress;}
            if(!dispatched&&(fixedAction is "key" or "text"||fixedCard["fixed_box"] is JsonArray)&&
                (fixedCard["condition_anchors"] as JsonArray??[]).Count==0&&S(fixedCard,"condition_operation","all")=="all")
            {
                if(fixedCard["consumes_resources"]?.GetValue<bool>()==true)throw new InputBlockedException("消费动作必须落盘意图");
                RunFixed(fixedCard,fixedAction);dispatched=true;HasDispatchedInput=true;dispatchedAt=now;
                session.TraceNavigation("workbench",S(fixedCard,"id"),"dispatch");
                if(S(fixedCard,"expected_page_id").Length==0&&(fixedCard["expected_anchors"] as JsonArray??[]).Count==0)Advance(fixedCard,true);
                return Progress;
            }
            var o=observation??session.Observe();if(!o.Stable||!o.IdentityVerified||o.View.Modal||session.Guard.Paused)return Set(WorkbenchFlowState.Running,"等待稳定页面");
            if(!initialized)
            {
                initialized=true;
                if(selection is null&&definition?["resume_at_current_page"]?.GetValue<bool>()==true)
                {var found=Array.FindIndex(cards,c=>S(c,"page_id")==o.View.Page);if(found>0&&cards.Take(found).All(c=>c["source"] is not null&&S(c,"role","navigation")=="navigation")){index=found;enteredAt=now;}}
            }
            if(index>=cards.Length)return Set(WorkbenchFlowState.Completed,"流程完成");
            var card=cards[index];var id=S(card,"id");var action=S(card,"action","observe");
            if(action=="semantic")
            {
                if(Progress.State!=WorkbenchFlowState.AwaitingSemantic&&!EvaluateCondition(card,o))return Set(WorkbenchFlowState.Running,"等待任务动作条件");
                return Set(WorkbenchFlowState.AwaitingSemantic,"交由原任务执行："+S(card,"role","任务动作"));
            }
            if(action=="delay")
            {if((now-enteredAt)*1000>=(card["delay_ms"]?.GetValue<int>()??200))Advance(card,true);return Progress;}
            var condition=EvaluateCondition(card,o);
            if(action=="branch"){Advance(card,condition);return Progress;}
            if(!dispatched&&!condition)return Set(WorkbenchFlowState.Running,"等待步骤条件");
            var page=S(card,"page_id");if(page.Length>0&&o.View.Page!=page&&!dispatched)return Set(WorkbenchFlowState.Running,"等待页面："+page);
            if(action is "observe" or "wait"){Advance(card,true);return Progress;}
            if(!dispatched&&(action is "key" or "text"||card["fixed_box"] is JsonArray))
            {
                if(card["consumes_resources"]?.GetValue<bool>()==true)throw new InputBlockedException("消费动作必须落盘意图");
                fromPage=o.View.Page;RunFixed(card,action);dispatched=true;HasDispatchedInput=true;dispatchedAt=now;
                if(S(card,"expected_page_id").Length==0&&(card["expected_anchors"] as JsonArray??[]).Count==0)Advance(card,true);
                return Progress;
            }
            if(dispatched)
            {
                var expected=S(card,"expected_page_id");var expectedAnchors=(card["expected_anchors"] as JsonArray??[]).Select(n=>n!.GetValue<string>()).ToArray();
                var anchorsConfirmed=expectedAnchors.Length>0&&session.LastCapture is{} fresh&&expectedAnchors.All(name=>session.Matcher.Anchor(fresh.Image,name) is not null);
                var pageConfirmed=expected.Length>0&&expected!=fromPage&&o.View.Page==expected;
                var confirmed=pageConfirmed||!expectedBefore&&anchorsConfirmed&&(expected.Length==0||o.View.Page==expected);
                if(now-dispatchedAt>=.12&&confirmed)Advance(card,true);else Set(WorkbenchFlowState.Running,"确认操作结果");
                return Progress;
            }
            if(card["consumes_resources"]?.GetValue<bool>()==true)throw new InputBlockedException("消费动作必须由原任务核验并落盘意图");
            if(action is not("click" or "right" or "scroll" or "zoom_max" or "drag"))throw new InputBlockedException("任务步骤类型未审核："+action);
            var target=S(card,"target");if(target.Length==0||!o.View.Targets.ContainsKey(target))return Set(WorkbenchFlowState.Running,"等待目标："+target);
            var step=card["source"]?.DeepClone()?.AsObject()??new JsonObject();step["kind"]=action;step["target"]=target;
            var expectedNames=(card["expected_anchors"] as JsonArray??[]).Select(n=>n!.GetValue<string>()).ToArray();expectedBefore=expectedNames.Length>0&&session.LastCapture is{} before&&expectedNames.All(name=>session.Matcher.Anchor(before.Image,name) is not null);
            fromPage=o.View.Page;session.Step(step);dispatched=true;HasDispatchedInput=true;dispatchedAt=now;
            session.TraceNavigation("workbench",id,"dispatch");
            if(S(card,"expected_page_id").Length==0&&expectedNames.Length==0){Advance(card,true);return Progress;}
            return Set(WorkbenchFlowState.Running,"等待声明的操作结果");
        }
        catch(Exception error)when(error is not OutOfMemoryException)
        {session.ReportFault(error,"workbench",index<cards.Length?S(cards[index],"id"):null);return Set(WorkbenchFlowState.Failed,error.Message);}
    }
    public bool TryNavigate(UiObservation observation)
    {
        if(!Available)return false;var progress=Tick(observation);
        if(progress.State==WorkbenchFlowState.Failed)throw new InputBlockedException(progress.Message);
        return progress.State is WorkbenchFlowState.Running or WorkbenchFlowState.Idle;
    }
    void RunFixed(JsonObject card,string action)
    {
        var guard=session.Guard;
        if(action=="key"){guard.PressKey(checked((ushort)(card["key_code"]?.GetValue<int>()??32)),TimeSpan.FromMilliseconds(card["key_duration_ms"]?.GetValue<int>()??80));return;}
        if(action=="text"){guard.TypeText(S(card,"input_text"),card["backspaces"]?.GetValue<int>()??0);return;}
        var geometry=guard.ReadGeometry();var source=card["fixed_geometry"] as JsonArray;
        var width=source?[0]?.GetValue<int>()??1280;var height=source?[1]?.GetValue<int>()??960;
        if(width<=0||height<=0)throw new InputBlockedException("固定坐标尺寸无效");
        int X(int x)=>(int)Math.Round((double)x*geometry.Width/width);int Y(int y)=>(int)Math.Round((double)y*geometry.Height/height);
        var b=card["fixed_box"]!.AsArray();var box=new PixelBox(X(b[0]!.GetValue<int>()),Y(b[1]!.GetValue<int>()),X(b[2]!.GetValue<int>()),Y(b[3]!.GetValue<int>()));
        if(action is "click" or "right"){guard.ClickAt(box.Center,box,action=="right"?MouseButton.Right:MouseButton.Left);return;}
        if(action is "scroll" or "zoom_max"){guard.ScrollAt(box.Center,box,action=="zoom_max"?12:card["source"]?["steps"]?.GetValue<int>()??3,action=="zoom_max");return;}
        if(action=="drag"){var p=card["source"]!["destination"]!.AsArray();guard.DragAt(box.Center,new(X(p[0]!.GetValue<int>()),Y(p[1]!.GetValue<int>())));return;}
        throw new InputBlockedException("固定动作类型无效："+action);
    }
    public WorkbenchFlowProgress CompleteSemantic(bool succeeded,string? error=null)
    {
        if(Progress.State!=WorkbenchFlowState.AwaitingSemantic||index>=cards.Length)throw new InvalidOperationException("当前步骤不等待原任务结果");
        if(!succeeded)return FailOrBranch(cards[index],error??"原任务结果尚未确认");
        Advance(cards[index],true);return Progress;
    }
    public bool EvaluateCondition(JsonObject card,UiObservation observation)
    {
        var names=(card["condition_anchors"] as JsonArray??[]).Select(n=>n!.GetValue<string>()).ToArray();
        var values=names.Select(name=>session.LastCapture is{} capture&&session.Matcher.Anchor(capture.Image,name) is not null).ToArray();
        return S(card,"condition_operation","all") switch
        {
            "all"=>values.All(x=>x),"any"=>values.Length>0&&values.Any(x=>x),"not" or "none"=>values.All(x=>!x),"exists"=>values.Length>0&&values.All(x=>x),
            "page"=>S(card,"page_id").Length>0&&observation.View.Page==S(card,"page_id"),_=>throw new InputBlockedException("条件关系未审核")
        };
    }
    WorkbenchFlowProgress FailOrBranch(JsonObject card,string error)
    {
        if(S(card,"failure_id").Length>0){Advance(card,false);return Progress;}
        var id=S(card,"id");var retryLimit=card["retry_limit"]?.GetValue<int>()??0;
        if(!dispatched&&card["consumes_resources"]?.GetValue<bool>()!=true&&retries.GetValueOrDefault(id)<retryLimit)
        {retries[id]=retries.GetValueOrDefault(id)+1;entered=false;return Set(WorkbenchFlowState.Running,"重新识别："+error);}
        return Set(WorkbenchFlowState.Failed,error);
    }
    void Advance(JsonObject card,bool success)
    {
        session.TraceNavigation("workbench",S(card,"id"),success?"confirmed":"condition_false");completed++;
        var next=S(card,success?"success_id":"failure_id");
        var endConditionPath=!success&&S(card,"action")=="branch"&&next.Length==0;
        if(next.Length>0){var destination=Array.FindIndex(cards,c=>S(c,"id")==next);index=destination<0?cards.Length:destination;}else if(endConditionPath)index=cards.Length;else index++;
        entered=dispatched=expectedBefore=false;fromPage=null;
        Set(index>=cards.Length?WorkbenchFlowState.Completed:WorkbenchFlowState.Running,endConditionPath?"条件未满足，当前路径结束":index>=cards.Length?"流程完成":"准备下一步");
    }
    WorkbenchFlowProgress Set(WorkbenchFlowState state,string message)=>Progress=new(state,index<cards.Length?S(cards[index],"id"):null,message,completed,cards.Length);
    static string S(JsonNode node,string key,string fallback="")=>node[key]?.GetValue<string>()??fallback;
}
