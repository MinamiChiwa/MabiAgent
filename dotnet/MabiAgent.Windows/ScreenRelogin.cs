using MabiAgent.Core;
using System.Text.Json.Nodes;
using MabiAgent.Workflows;
namespace MabiAgent.Windows;

/// <summary>Account-local fixed character slots; packet identity owns inventory and storage.</summary>
public sealed class ScreenRelogin
{
    private readonly ScreenWorkflowSession session;private readonly IWorkflowStore store;private readonly TimeProvider time;private JsonObject state;
    public string? CharacterName=>state["character"]?.GetValue<string>();
    public int? CharacterSlot=>state["character_slot"]?.GetValue<int>();
    public bool Active=>state["status"]?.GetValue<string>()=="running";
    private CharacterSlotIdentity? Expected=>state["account_id"] is{} a&&state["character_id"] is{} c?new(a.GetValue<string>(),c.GetValue<string>(),CharacterName??""):null;
    public ScreenRelogin(ScreenWorkflowSession session,IWorkflowStore store,TimeProvider? time=null)
    {this.session=session;this.store=store;this.time=time??TimeProvider.System;state=store.Read()??new JsonObject{["status"]="idle"};if(state["pending"] is not null||Active){state["status"]="blocked";state["reason"]="前次重登结果待核实";store.Flush(state);}}
    public JsonObject Snapshot()=>(JsonObject)state.DeepClone();
    public void Pause(){if(Active)Stop("重登已暂停，请核实当前页面");}
    public void BeginCurrent(bool safeBoundary,bool explicitRequest)=>Begin(session.CurrentPacketCharacter??throw new InputBlockedException("重登需要当前完整抓包身份，不能从屏幕角色名猜测"),safeBoundary,explicitRequest);
    public void Begin(CharacterSlotIdentity who,bool safeBoundary,bool explicitRequest)
    {
        if(!explicitRequest||!safeBoundary||Active||string.IsNullOrWhiteSpace(who.AccountId)||string.IsNullOrWhiteSpace(who.CharacterId)||string.IsNullOrWhiteSpace(who.Name))throw new InputBlockedException("重登需要授权、安全边界及当前抓包身份");
        if(state["pending"] is not null&&(Expected is not{} saved||saved.AccountId!=who.AccountId||saved.CharacterId!=who.CharacterId))throw new InputBlockedException("前次重登缺少相同账户与角色ID，保留原意图待核对");
        var pending=state["pending"]?.DeepClone();var slot=session.CharacterSlot(who);
        session.BeginRecoveryRecognition();state=new JsonObject{["schema_version"]=2,["status"]="running",["phase"]="verify_owner",["session"]=session.Scope.SessionId,["account_id"]=who.AccountId,["character_id"]=who.CharacterId,["character"]=who.Name,["character_slot"]=slot,["deadline"]=Now+Timeout("timeout_s",300),["phase_deadline"]=Now+Timeout("phase_timeout_s",60)};
        if(pending is not null)state["pending"]=pending;store.Flush(state);
    }
    public bool MatchesPacketIdentity(CharacterSlotIdentity? who)=>who is not null&&Expected is{} expected&&who.AccountId==expected.AccountId&&who.CharacterId==expected.CharacterId;
    private double Now=>(time.GetUtcNow()-DateTimeOffset.UnixEpoch).TotalSeconds;
    private double Timeout(string key,double fallback)=>Math.Clamp(session.Routes.Configuration["relogin"]?[key]?.GetFiniteDouble()??fallback,1,900);
    private void Phase(string phase){state["phase"]=phase;state["phase_deadline"]=Now+Timeout(phase=="sync"?"sync_timeout_s":"phase_timeout_s",phase=="sync"?120:60);}
    public static string PhaseLabel(string? phase)=>phase switch{"verify_owner"=>"确认抓包角色","menu"=>"打开游戏菜单","exit"=>"打开退出选项","character_select"=>"返回角色选择","enter"=>"按角色顺序进入","sync"=>"等待完整库存同步",_=>"识别当前页面"};
    internal PageView? Recognize(ClientCapture capture)
    {
        if(!Active)return null;
        foreach(var (page,names,target) in new[]{("relogin_exit",new[]{"exit_title","select_character"},"character_select"),("relogin_characters",new[]{"character_title","start_game"},"enter"),("relogin_menu",new[]{"menu_settings","menu_exit"},"exit")})
        {
            if(names.Take(names.Length-1).Any(n=>session.Matcher.Anchor(capture.Image,"relogin."+n) is null))continue;
            var targets=new Dictionary<string,PixelPoint>();
            if(target!="enter"||CharacterSlot is{} slot&&SelectedSlot(capture)==slot)targets["relogin."+target]=session.Box(session.Routes.Profile.Templates["relogin."+names[^1]].Box).Center;
            if(page=="relogin_menu")targets["page.close"]=session.Box(new PixelBox(1210,28,1270,90)).Center;
            return new(page,false,[],targets,[]);
        }
        return null;
    }
    internal PageView Decorate(ClientCapture c,PageView view)
    {
        if(!Active||view.Modal)return view;
        if(view.Page=="scene")
        {
            if(session.Routes.Configuration["relogin"]?["menu_region"] is{} region)return session.AddSurface(view,"relogin.menu",session.Box(region));
            if(session.Matcher.Anchor(c.Image,"scene_menu") is{} menu)return session.AddSurface(view,"relogin.menu",menu.Box);
        }
        return view;
    }
    internal int? SelectedSlot(ClientCapture capture)
    {
        int? selected=null;
        for(var slot=1;slot<=6;slot++)
        {
            var i=slot-1;var x=new[]{87,368,649,930}[i%4];var y=i<4?117:565;
            var p=capture.Image.Crop(session.Box(new PixelBox(x,y,x+4,Math.Min(y+410,960)))).Pixels;var gold=0;
            for(var at=0;at<p.Length;at+=3)if(p[at]>145&&p[at+1]>100&&p[at+2]<145)gold++;
            if(gold/(p.Length/3d)<=.25)continue;if(selected is not null)return null;selected=slot;
        }
        return selected;
    }
    internal static PixelPoint SlotCenter(int slot)
    {if(slot is <1 or >6)throw new ArgumentOutOfRangeException(nameof(slot));var i=slot-1;return new(new[]{219,500,781,1062}[i%4],i<4?320:760);}
    public string Tick(bool sameCharacterFreshSync,bool noticeDismissed=false)
    {
        if(!Active)return state["status"]!.GetValue<string>();
        if(session.Guard.Paused)return Stop("重登已暂停："+session.Guard.PauseReason);
        if(Now>=state["deadline"]!.GetFiniteDouble()||state["phase_deadline"] is{} phaseDeadline&&Now>=phaseDeadline.GetFiniteDouble())return Stop(PhaseLabel(state["phase"]?.GetValue<string>())+"超时（页面："+session.CurrentPage+"）");
        if(state["session"]!.GetValue<string>()!=session.Scope.SessionId)return Stop("重登会话已变化");
        if(session.CurrentPacketCharacter is{} current&&!MatchesPacketIdentity(current))return Stop("抓包账户或角色ID与重登目标不符");
        if(noticeDismissed||state["retry_at"]?.GetFiniteDouble()>Now)return "running";
        var o=session.Observe();if(session.Guard.Paused)return Stop("重登已暂停："+session.Guard.PauseReason);
        if(!o.Stable||!session.InputAvailable)return "running";
        var phase=state["phase"]!.GetValue<string>();
        if(phase=="verify_owner")
        {
            if(state["pending"]?["kind"]?.GetValue<string>()=="relogin_enter")
            {if(!sameCharacterFreshSync||!MatchesPacketIdentity(session.CurrentPacketCharacter))return Stop("前次进入结果未知，等待同角色新抓包");Phase("sync");store.Flush(state);return "running";}
            var next=o.View.Page switch{"scene"=>"menu","relogin_menu"=>"exit","relogin_exit"=>"character_select","relogin_characters"=>"enter",_=>null};
            if(next is null)
            {var close=o.View.Page=="skills"&&o.View.Targets.ContainsKey("skills.close")?"skills.close":o.View.Targets.ContainsKey("bag.close")?"bag.close":o.View.Targets.ContainsKey("page.close")?"page.close":null;if(close is not null)session.NavigateTarget(close);return "running";}
            state.Remove("pending");Phase(next);store.Flush(state);return "running";
        }
        if(phase=="sync")
        {
            if(o.View.Page=="scene"&&sameCharacterFreshSync&&MatchesPacketIdentity(session.CurrentPacketCharacter))
            {state["character"]=session.CurrentPacketCharacter!.Name;if(CharacterSlot is{} slot)session.RecordCharacterSlot(session.CurrentPacketCharacter!,slot);state["status"]="complete";state.Remove("pending");store.Flush(state);}
            return state["status"]!.GetValue<string>();
        }
        if(phase=="enter"&&o.View.Page=="relogin_characters")
        {
            var selected=SelectedSlot(session.LastCapture!);
            if(state["source_slot_associated"]?.GetValue<bool>()!=true)
            {
                if(selected is null)return "running";
                if(CharacterSlot is{} known&&known!=selected)return Stop("当前选中角色顺序与该账户的抓包关联冲突");
                try{session.RecordCharacterSlot(Expected!,selected.Value);}catch(Exception e)when(e is ArgumentException or InvalidDataException or IOException){return Stop(e.Message);}
                state["character_slot"]=selected.Value;state["source_slot_associated"]=true;state["slot_source"]="selected_card_after_packet_identity";store.Flush(state);
                return "running";
            }
            if(selected!=CharacterSlot)
            {if(state["selected_at"]?.GetFiniteDouble()+1>Now)return "running";session.Guard.ClickAt(session.Box(new PixelBox(SlotCenter(CharacterSlot!.Value).X,SlotCenter(CharacterSlot.Value).Y,SlotCenter(CharacterSlot.Value).X+1,SlotCenter(CharacterSlot.Value).Y+1)).Center);state["selected_at"]=Now;store.Flush(state);return "running";}
            // The newly learned slot becomes a declared enter target next tick.
            if(!o.View.Targets.ContainsKey("relogin.enter"))return "running";
        }
        var expected=phase switch{"menu"=>("scene","menu","exit"),"exit"=>("relogin_menu","exit","character_select"),"character_select"=>("relogin_exit","character_select","enter"),"enter"=>("relogin_characters","enter","sync"),_=>("unknown","","")};
        if(o.View.Page!=expected.Item1)return "running";var target="relogin."+expected.Item2;if(!o.View.Targets.ContainsKey(target))return "running";
        var before=(JsonObject)state.DeepClone();state["pending"]=new JsonObject{["kind"]="relogin_"+phase,["at"]=Now,["account_id"]=state["account_id"]!.DeepClone(),["character_id"]=state["character_id"]!.DeepClone(),["character_slot"]=state["character_slot"]?.DeepClone()};Phase(expected.Item3);store.Flush(state);
        try{session.Consume(target,"relogin:"+session.Scope.SessionId,store,"relogin_"+phase,_=>expected.Item2!="enter"||SelectedSlot(session.LastCapture!)==CharacterSlot);state.Remove("last_rejection");state.Remove("rejected_attempts");state.Remove("retry_at");store.Flush(state);}
        catch(InputRejectedBeforeDispatchException error)
        {state=before;state["last_rejection"]=error.Message;state["rejected_attempts"]=(state["rejected_attempts"]?.GetValue<int>()??0)+1;state["retry_at"]=Now+3;store.Flush(state);if(state["rejected_attempts"]!.GetValue<int>()>=4)return Stop("重登输入尚未发送："+error.Message);}
        return "running";
    }
    private string Stop(string reason){state["status"]="blocked";state["reason"]=reason;store.Flush(state);session.ReportWarning("relogin",reason,state["phase"]?.GetValue<string>(),state["pending"] is not null);return "blocked";}
}
