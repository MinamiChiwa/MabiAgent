using MabiAgent.Core;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MabiAgent.Workflows;
namespace MabiAgent.Windows;

/// <summary>One selected process, observer and input guard shared by every workflow.
/// Process-only scope is deliberately not an account identity or a stock scope.</summary>
public sealed class ScreenWorkflowSession : IDisposable
{
    private readonly NativeWindowState window;
    private readonly INativeWindowsOps inputOps;
    private readonly ScreenPageRecognizer recognizer;
    private ScreenObserver observer;
    private System.Runtime.CompilerServices.ConditionalWeakTable<object,ObservationContext> observationContexts=new();
    private sealed record ObservationContext(ScreenObserver Observer,System.Reflection.MethodInfo Method,long Epoch);
    private readonly Action? activateOnce;
    private readonly IWorkflowStore journal;
    private readonly NavigationConsumptionPolicy navigationConsumption;
    public Func<bool>? LifeVipEnabled {get;set;}
    public double ProductionDuration(double standardSeconds)=>ProductionTiming.Duration(standardSeconds,LifeVipEnabled?.Invoke()==true);
    private bool started;
    private Func<ClientCapture,PageView,PageView>? decorate;
    private Func<ClientCapture,PageView>? focusedRecognition;
    private Func<PacketGoldObservation?>? packetGold;
    private string? spentGoldVersion;
    public void SetPacketGoldProvider(Func<PacketGoldObservation?> provider)=>packetGold=provider;
    public long? CurrentPacketGold()
    {
        var packet=packetGold?.Invoke();var now=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
        return packet is not null&&packet.Balance>=0&&packet.Version!=spentGoldVersion&&double.IsFinite(packet.At)&&now-packet.At is >=0 and <=30?packet.Balance:null;
    }
    public long? ReadGold(Func<long?> screen)=>CurrentPacketGold()??screen();
    public long? ResolveGold(long? screen)
    {
        var packet=CurrentPacketGold();if(packet is null)return screen is >=0?screen:null;
        // A lower visible balance supersedes the login ceiling after any spend.
        return screen is>=0?Math.Min(packet.Value,screen.Value):packet;
    }
    public void InvalidatePacketGold()=>spentGoldVersion=packetGold?.Invoke()?.Version;
    public ScreenRoutes Routes {get;}
    public TemplateMatcher Matcher {get;}
    public ScreenQuantityReader QuantityReader {get;}
    public ScreenTravelWings? Travel {get;private set;}
    public ScreenRelogin? Relogin {get;private set;}
    private RuntimeLimitRecognition? runtimeLimit;
    public void ConfigureRuntimeLimitRecognition(string root){runtimeLimit?.Dispose();runtimeLimit=new(root);}
    public ForegroundInputGuard Guard {get;}
    public SessionScope Scope {get;}
    private Func<SessionScope?>? ownerResolver;
    public SessionScope? VerifiedOwner=>ownerResolver is not null?ownerResolver.Invoke() is {HasIdentity:true} owner?owner:null:Scope.HasIdentity?Scope:null;
    public void SetVerifiedOwnerResolver(Func<SessionScope?> resolver)=>ownerResolver=resolver;
    private Func<CharacterSlotIdentity?>? packetIdentity;
    private Func<string,string,int?>? resolveCharacterSlot;
    private Action<CharacterSlotIdentity,int>? recordCharacterSlot;
    public void ConfigureCharacterSelection(Func<CharacterSlotIdentity?> identity,Func<string,string,int?> resolve,Action<CharacterSlotIdentity,int> record)
    {packetIdentity=identity;resolveCharacterSlot=resolve;recordCharacterSlot=record;}
    internal CharacterSlotIdentity? CurrentPacketCharacter=>packetIdentity?.Invoke();
    internal int? CharacterSlot(CharacterSlotIdentity who)=>resolveCharacterSlot?.Invoke(who.AccountId,who.CharacterId);
    internal void RecordCharacterSlot(CharacterSlotIdentity who,int slot)=>recordCharacterSlot?.Invoke(who,slot);
    public long InputEpoch {get;private set;}
    public ClientCapture? LastCapture {get;private set;}
    public UiObservation? LastObservation {get;private set;}
    private ScreenObserver? lastObservationContext;
    public string CurrentPage=>LastObservation?.View.Page??"unknown";
    internal string? StabilityReason=>observer.StabilityReason;
    public string? CurrentDevice=>Routes.Devices.Values.FirstOrDefault(d=>d.QueuePage==CurrentPage)?.Key;
    public string? OnsiteDevice=>LastCapture is{} capture&&LastObservation is{Stable:true} observation&&!observation.View.NeedsDeviceArrival
        ?Routes.Devices.Values.FirstOrDefault(d=>d.QueuePage==CurrentPage&&(HasDeviceArrival(d.Key)||All(capture,d.ArrivalAnchors)))?.Key:null;
    private string? arrivedDevice;private long arrivedDeviceRevision=-1;
    internal bool HasDeviceArrival(string device)=>arrivedDevice==device&&arrivedDeviceRevision==LocationRevision;
    internal void ConfirmDeviceArrival(string device){arrivedDevice=device;arrivedDeviceRevision=LocationRevision;}
    private string? navigationId;private double navigationDeadline;
    internal long LocationRevision {get;private set;}
    public string? CurrentNavigationId=>navigationId is not null&&DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d<=navigationDeadline?navigationId:null;
    internal void BeginNavigation(string id,double seconds=120){LocationRevision++;navigationId=id;navigationDeadline=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d+seconds;}
    internal void EndNavigation(string id){if(navigationId==id)navigationId=null;}
    public bool TryTravel()=>CurrentNavigationId is {} id&&Travel?.TryUse(id)==true;
    public event Action? Changed;
    internal void TraceNavigation(string batch,string target,string phase)
    {diagnostics?.Write(diagnostics.Create(Guid.NewGuid().ToString("N"),"info","navigation",phase,"module",Scope.SessionId,job:null,action:batch,page:CurrentPage,recovery:"observe",message:target,trigger:"screen_navigation > "+target));}
    private readonly RuntimeDiagnostics? diagnostics;private readonly Dictionary<string,string> lastDiagnostic=[];
    public void ReportFault(Exception error,string module,string? action=null)
    {var key=module+":"+action;var signature=error.GetType().Name+":"+error.Message;if(lastDiagnostic.GetValueOrDefault(key)==signature)return;lastDiagnostic[key]=signature;diagnostics?.Write(diagnostics.Create(Guid.NewGuid().ToString("N"),"error",module,CurrentPage,"foreground",Scope.SessionId,null,action,CurrentPage,"observe_then_explicit_continue",error.Message,error,trigger:module+" > "+action));}
    public void ReportWarning(string module,string reason,string? action=null,bool pending=false)
    {var key=module+":"+action;if(lastDiagnostic.GetValueOrDefault(key)==reason)return;lastDiagnostic[key]=reason;diagnostics?.Write(diagnostics.Create(Guid.NewGuid().ToString("N"),"warning",module,CurrentPage,pending?"foreground":"module",Scope.SessionId,null,action,CurrentPage,pending?"observe_only":"review",reason,pending:pending,trigger:module+" > "+action));}
    public ScreenWorkflowSession(ScreenRoutes routes,NativeWindowState window,INativeWindowsOps ops,IOcrEngine ocr,IWorkflowStore intentJournal,Action? activateOnce=null,SessionScope? scope=null,RuntimeDiagnostics? diagnostics=null,InputOptions? inputOptions=null)
    {
        inputOps=ops;routes=routes.Snapshot();
        this.diagnostics=diagnostics;
        if(!GameWindowSelection.IsGame(window))throw new InputBlockedException("请选择当前游戏窗口");
        Routes=routes;navigationConsumption=new(routes);this.window=window;journal=intentJournal;this.activateOnce=activateOnce;
        Scope=scope??ProcessScope(window);Matcher=new(routes.Profile);QuantityReader=new(ocr);recognizer=new(routes.Profile,Matcher);
        observer=CreateObserver();
        Guard=new(window,Scope,ops,Review,maxFrameAgeSeconds:5,resourceRevision:()=>Routes.Revision,inputOptions:inputOptions);
    }
    public static SessionScope ProcessScope(NativeWindowState w)=>new($"process:{w.ProcessId}:{w.ProcessStartUtcTicks}:{w.WindowId}",null,null);
    private ScreenObserver CreateObserver()=>new(c=>SameProcess(c.Native)?Scope:null,c=>{var page=RecognizeCommon(c);if(Relogin?.Active!=true&&!page.Modal&&page.Page!="production_complete"&&!page.Page.StartsWith("notification_",StringComparison.Ordinal)&&!page.Page.StartsWith("relogin_",StringComparison.Ordinal))page=decorate?.Invoke(c,page)??page;page=Relogin?.Decorate(c,page)??page;return Relogin?.Active==true?page:Travel?.Decorate(c,page)??page;});
    private bool SameProcess(NativeWindowState w)=>w.WindowId==window.WindowId&&w.ProcessId==window.ProcessId&&w.ProcessStartUtcTicks==window.ProcessStartUtcTicks&&string.Equals(w.Executable,window.Executable,StringComparison.OrdinalIgnoreCase);
    private UiObservation Review(ClientCapture c){LastCapture=c;lastObservationContext=observer;LastObservation=observer.Observe(c);return LastObservation;}
    private PageView RecognizeCommon(ClientCapture c)
    {
        if(runtimeLimit?.Recognize(c.Image) is {} interrupted)return interrupted;
        if(Relogin?.Recognize(c) is {} recovery)return recovery;
        if(Routes.Configuration["notifications"]?["flows"] is JsonObject flows)foreach(var (name,node) in flows)
        {
            var anchors=Names(node?["anchors"]);var closeName=S(node,"close");
            if(closeName is null||!Routes.Profile.Templates.TryGetValue("notification."+closeName,out var close))continue;
            var identity=anchors.Where(a=>a!=closeName).ToArray();if(identity.Length==0)identity=anchors;
            var hits=identity.Select(a=>Matcher.Anchor(c.Image,"notification."+a)).ToArray();
            if(hits.Length==0||hits.Any(h=>h is null))continue;
            return new("notification_"+name,false,hits.Select(h=>h!).ToArray(),new Dictionary<string,PixelPoint>{{"notification.close",Box(close.ControlBox??close.Box).Center}},hits.Select(h=>h!.Box).ToArray());
        }
        var page=focusedRecognition?.Invoke(c)??recognizer.Recognize(c);
        if(!page.Targets.ContainsKey("page.close")&&(page.Page.EndsWith("_panel",StringComparison.Ordinal)||page.Page is "category_page" or "skills" or "recipe_detail"))
            foreach(var anchor in Names(Routes.Profile.Definition["close_anchors"]))
            {var hit=Matcher.Anchor(c.Image,anchor);if(hit is not null){page=AddTarget(page,"page.close",hit);break;}}
        // A remote device preview has a back arrow; the same device onsite has
        // an X at the opposite corner. Share the result across every port.
        if(Routes.Devices.Values.Any(d=>d.QueuePage==page.Page)&&page.Targets.ContainsKey("page.close")
            &&Matcher.Anchor(c.Image,"screen_overlay_back") is{} back)
        {page=RemoveTarget(page,"page.close");page=AddTarget(page,"page.close",back);}
        return page;
    }
    public bool DismissNotice(bool notificationsOnly=false)
    {
        var o=Observe();if(!o.Stable||!InputAvailable||Guard.Paused)return false;
        var target=o.View.Page.StartsWith("notification_",StringComparison.Ordinal)?"notification.close":!notificationsOnly&&o.View.Page=="production_complete"?"completion.confirm":null;if(target is null)return false;
        try{NavigateTarget(target);TraceNavigation("notification",target,"input_dispatched");return true;}
        catch(InputBlockedException)when(!Guard.Paused&&LastObservation is{IdentityVerified:true} fresh&&(fresh.View.Page!=o.View.Page||!fresh.Stable))
        {
            // The shared guard rejected a changing page before native dispatch.
            // Let the next tick recognize the new notice; never replay a sent click.
            ReportWarning("notification_transition","通知页面正在切换，等待新画面",target);return true;
        }
    }
    public ScreenTravelWings ConfigureTravel(IWorkflowStore store,Func<JsonObject> settings)=>Travel=new(this,store,settings);
    internal PageView RecognizeDeclaredPages(ClientCapture capture,IReadOnlySet<string> pages)=>recognizer.Recognize(capture.Image,pages);
    public ScreenRelogin ConfigureRelogin(IWorkflowStore store,TimeProvider? time=null)=>Relogin=new(this,store,time);
    internal void BeginRecoveryRecognition(){LocationRevision++;decorate=null;focusedRecognition=null;navigationId=null;observationContexts=new();observer=CreateObserver();}
    public void Start(){if(started)throw new InputBlockedException("已开始，请点击继续");if(Guard.RequiresForeground)activateOnce?.Invoke();observer.Reset();Guard.Start();started=true;}
    public void ResumeExplicit(){if(Guard.RequiresForeground)activateOnce?.Invoke();Resume();}
    public void Resume(){InputEpoch++;observer.Reset();if(started)Guard.Resume();else{Guard.Start();started=true;}}
    internal void ResumePort(){if(Guard.Paused)throw new InputBlockedException("请点击继续当前暂停："+Guard.PauseReason);}
    internal void PausePort(string reason)
    {
        // These are branch decisions, not permission to stop or resume the shared guard.
        if(reason is "craft_unavailable" or "materials_unknown" or "device_full" or "device_batch_not_ready" or "module_fault"
            or "consumables_unknown" or "empty_bottles_required" or "bottles_unknown")return;
        Pause(reason);
    }
    internal bool CanReleaseForeground()
    {
        if(Guard.Paused||CurrentNavigationId is not null)return false;
        try
        {
            var observation=Observe();var page=observation.View.Page;
            return !Guard.Paused&&InputAvailable&&observation.Stable&&!observation.View.Modal&&
                (page is "scene" or "bag" or "repair_tools" or "skills" or "category_page" or "recipe_detail"||Routes.Devices.Values.Any(d=>d.QueuePage==page));
        }catch(Exception error)when(error is not OutOfMemoryException){return false;}
    }
    public void ResumeWorkflow(){Guard.ResumeWorkflow();InputEpoch++;observer.Reset();}
    public void Pause(string reason="manual"){if(reason=="focus_lost"&&Guard.PauseReason=="window_minimized")reason="window_minimized";InputEpoch++;Guard.Pause(reason);observer.Reset();}
    internal void Use(Func<ClientCapture,PageView,PageView> enricher,Func<ClientCapture,PageView>? focused=null)
    {
        decorate=enricher;focusedRecognition=focused;var owner=enricher.Target??(object)enricher.Method;
        if(!observationContexts.TryGetValue(owner,out var context)||context.Method!=enricher.Method||context.Epoch!=InputEpoch)
        {observationContexts.Remove(owner);context=new(CreateObserver(),enricher.Method,InputEpoch);observationContexts.Add(owner,context);}
        observer=context.Observer;
    }
    public UiObservation Observe()
    {
        try
        {
            var view=Guard.ObserveFrame();
            if(view.View.Page=="restart_required")
            {
                Guard.Pause("restart_required");var prior=PortState("runtime-interruption");
                if(S(prior,"session")!=Scope.SessionId||S(prior,"capture")!=view.ImageSha256)SavePortState("runtime-interruption",new JsonObject{["session"]=Scope.SessionId,["capture"]=view.ImageSha256,["reason"]="game_runtime_limit",["input_issued"]=false});
            }
            NotifyChanged();return view;
        }
        catch(InputBlockedException error){ReportFault(error,"screen_observation");LastObservation=new("",0,Scope,PageView.Unknown(),false,false);NotifyChanged();return LastObservation;}
    }
    private void NotifyChanged(){foreach(var callback in Changed?.GetInvocationList()??[])try{((Action)callback)();}catch(Exception error){ReportFault(error,"screen_subscriber");}}
    internal bool InputAvailable=>LastCapture is {} c&&SameProcess(c.Native)&&c.Native.Visible&&!c.Native.Minimized&&c.Native.Enabled&&(!Guard.RequiresForeground||c.Native.ForegroundId==window.WindowId)&&LastObservation?.IdentityVerified==true;
    private ClientGeometry CurrentGeometry=>LastCapture?.Native.Geometry??window.Geometry;
    internal PixelBox Box(JsonNode node)=>Box(NavigationProfile.ReadBox(node));
    internal PixelBox Box(PixelBox box)=>ReviewedLayout.For(CurrentGeometry.Width,CurrentGeometry.Height,CurrentGeometry.Dpi).SearchBox(box);
    internal PixelBox? AnchoredBox(string template,TemplateHit icon,PixelBox reference)
    {
        var geometry=CurrentGeometry;var layout=ReviewedLayout.For(geometry.Width,geometry.Height,geometry.Dpi);
        if(!Routes.Profile.Templates.TryGetValue(template,out var spec)||!reference.IsInside(layout.ReferenceWidth,layout.ReferenceHeight))return null;
        var box=layout.SearchBox(reference);var expected=layout.SearchBox(spec.Box);var hit=icon.MatchBox??icon.Box;
        var dx=hit.Left-expected.Left;var dy=hit.Top-expected.Top;
        var shifted=new PixelBox(box.Left+dx,box.Top+dy,box.Right+dx,box.Bottom+dy);
        return shifted.IsInside(geometry.Width,geometry.Height)?shifted:null;
    }
    internal bool Hit(ClientCapture c,string? anchor)=>anchor is not null&&Matcher.Anchor(c.Image,anchor) is not null;
    internal bool All(ClientCapture c,IEnumerable<string> names){var a=names.ToArray();return a.Length>0&&a.All(n=>Hit(c,n));}
    internal static string? S(JsonNode? node,string key)=>node?[key]?.GetValue<string>();
    internal static string[] Names(JsonNode? node)=>NavigationProfile.Names(node);
    internal PageView AddTarget(PageView view,string target,TemplateHit hit,PixelPoint? point=null)
    {
        var targets=view.Targets.ToDictionary();targets[target]=point??hit.Box.Center;
        var regions=view.TargetRegions?.ToDictionary()??new Dictionary<string,PixelBox>();var prior=regions.GetValueOrDefault(target);regions.Remove(target);
        if(hit.Box.Contains(targets[target]))
        {
            var region=hit.Box;
            if(view.TargetShapes?.GetValueOrDefault(target)==ClickRegionShape.Ellipse&&prior.Width>0&&prior.Width==prior.Height)
            {var size=Math.Min(region.Width,region.Height);var left=region.Left+(region.Width-size)/2;var top=region.Top+(region.Height-size)/2;region=new(left,top,left+size,top+size);}
            if(region.Contains(targets[target]))regions[target]=region;
        }
        return view with{Targets=targets,TargetRegions=regions,Hits=view.Hits.Append(hit).Distinct().ToArray()};
    }
    internal PageView AddSurface(PageView view,string target,PixelBox box)
    {var targets=view.Targets.ToDictionary();targets[target]=box.Center;var regions=view.TargetRegions?.ToDictionary()??new Dictionary<string,PixelBox>();regions[target]=box;return view with{Targets=targets,TargetRegions=regions};}
    internal PageView RemoveTarget(PageView view,string target){var t=view.Targets.ToDictionary();t.Remove(target);var regions=view.TargetRegions?.ToDictionary()??new Dictionary<string,PixelBox>();regions.Remove(target);var shapes=view.TargetShapes?.ToDictionary()??new Dictionary<string,ClickRegionShape>();shapes.Remove(target);return view with{Targets=t,TargetRegions=regions,TargetShapes=shapes};}
    internal void NavigateTarget(string target,MouseButton button=MouseButton.Left)
    {var proof=Guard.ObserveCurrentTarget(target,lastObservationContext==observer?LastObservation:null);if(button==MouseButton.Left&&TryNavigationShortcut(proof))return;Guard.ClickReviewedNavigation(proof,proof.Page,button);}
    public void RunNavigationTarget(string target)=>NavigateTarget(target);
    internal const int ListSearchPassLength=12, ListSearchLimit=ListSearchPassLength*3;
    internal void ScrollListSearch(UiObservation observation,int attempt,string target="list.scroll",int passLength=ListSearchPassLength)
    {
        if(passLength is <1 or >33||attempt<0||attempt>=passLength*3)throw new InputBlockedException("列表搜索已到上限");
        // Lists retain their previous offset. Search down first, then twice as
        // far upward to cover both sides of that starting position.
        Guard.Scroll(Guard.ObserveCurrentTarget(target,observation),attempt<passLength?-3:3);
    }
    private bool TryNavigationShortcut(TargetProof proof)
    {
        ushort key=(proof.Page,proof.Name) switch{("scene","scene.open_bag") or ("bag" or "repair_tools","bag.close")=>73,("scene","scene.open_skills") or ("skills","skills.close")=>75,("repair_dialog" or "shop_dialog","dialog.end") or ("notification_news","notification.close")=>27,_=>0};
        if(key==0)return false;Guard.PressKey(proof,key,TimeSpan.FromMilliseconds(80));return true;
    }
    internal void Step(JsonNode step)
    {
        var target=S(step,"target")??throw new InputBlockedException("缺少导航锚点配置");
        var kind=S(step,"kind")??"click";
        var proof=Guard.ObserveCurrentTarget(target,lastObservationContext==observer?LastObservation:null);
        bool Validate(UiObservation fresh){navigationConsumption.Verify(target,fresh,Box);return true;}
        if(kind is "click" or "right"){if(kind=="click"&&TryNavigationShortcut(proof))return;Guard.ClickReviewedNavigation(proof,proof.Page,kind=="right"?MouseButton.Right:MouseButton.Left,Validate);return;}
        if(kind is "scroll" or "zoom_max")Guard.Scroll(proof,kind=="zoom_max"?step["steps"]?.GetValue<int>()??12:step["steps"]!.GetValue<int>(),zoom:kind=="zoom_max"||step["zoom"]?.GetValue<bool>()==true);
        else if(kind=="drag"){var a=step["destination"]!.AsArray();var p=Box(new PixelBox(a[0]!.GetValue<int>(),a[1]!.GetValue<int>(),a[0]!.GetValue<int>()+1,a[1]!.GetValue<int>()+1)).Center;Guard.Drag(proof,p,validate:Validate);}
        else throw new InputBlockedException("未审核的导航操作");
    }
    internal void Consume(string target,string batch,IWorkflowStore state,string kind,Func<UiObservation,bool> validate,Func<ClientCapture,bool>? beforeDelivery=null,ushort? movementKey=null)
    {
        var snapshot=state.Read()??throw new InputBlockedException("缺少已落盘流程");
        var pending=snapshot["pending"] as JsonObject??throw new InputBlockedException("缺少已落盘意图");
        if((S(pending,"kind")??S(pending,"action"))!=kind)throw new InputBlockedException("消费意图不匹配");
        var hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pending.ToJsonString())));
        var id=batch+":"+hash;TargetProof proof;
        try{proof=Guard.ObserveCurrentTarget(target,lastObservationContext==observer?LastObservation:null);}
        catch(InputBlockedException e){ReportWarning("consume_not_dispatched",e.Message,batch);throw new InputRejectedBeforeDispatchException(e.Message);}
        if(movementKey is not null&&target!="fishing.start")throw new InputBlockedException("移动抛竿仅用于已声明钓鱼步骤");
        Guard.Consume(proof,id,new DurableAuthority(journal,state,Scope,batch,hash,kind,validate,VerifiedOwner),beforeDelivery:beforeDelivery,key:target is "production.craft" or "fishing.start" or "fishing.reel"?(ushort)32:null,movementKey:movementKey);
    }
    internal sealed class DurableAuthority(IWorkflowStore journal,IWorkflowStore workflow,SessionScope scope,string batch,string pendingHash,string kind,Func<UiObservation,bool> validate,SessionScope? verifiedOwner=null):IProcessScopedConsumptionAuthorization
    {
        public bool AllowsProcessScope(SessionScope current)=>current==scope&&current.SessionId.StartsWith("process:",StringComparison.Ordinal);
        public bool VerifyPersistedIntent(string id,TargetProof target,UiObservation fresh)
        {
            if(fresh.Scope!=scope||!validate(fresh))return false;
            var pending=workflow.Read()?["pending"] as JsonObject;
            if(pending is null||(S(pending,"kind")??S(pending,"action"))!=kind||Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pending.ToJsonString())))!=pendingHash)return false;
            var saved=journal.Read()??new JsonObject{["schema_version"]=1,["intents"]=new JsonObject()};
            if(saved["intents"] is not JsonObject intents)throw new InvalidDataException("上次操作记录损坏");
            if(intents.ContainsKey(id))return false;
            intents[id]=new JsonObject{["batch_id"]=batch,["session_id"]=scope.SessionId,["account_id"]=(verifiedOwner??scope).AccountId,["character_id"]=(verifiedOwner??scope).CharacterId,["target"]=target.Name,["page"]=fresh.View.Page,["capture"]=fresh.ImageSha256,["kind"]=kind,["status"]="dispatch_attempted"};
            journal.Flush(saved);return true;
        }
    }
    internal JsonObject PortState(string key)=>journal.Read()?["ports"]?[key] is JsonObject s?(JsonObject)s.DeepClone():new JsonObject();
    internal void SavePortState(string key,JsonObject state)
    {var saved=journal.Read()??new JsonObject{["schema_version"]=1,["intents"]=new JsonObject()};saved["ports"]??=new JsonObject();saved["ports"]![key]=state.DeepClone();journal.Flush(saved);}
    public QuantityReading? ReadQuantity(JsonNode? spec,long? itemId=null,string? scope=null)
    {
        if(spec is null||LastCapture is not {} c||LastObservation is not {Stable:true,IdentityVerified:true})return null;
        var box=Box(spec["region"]!);
        if(S(spec,"after_anchor") is {} anchor){var hit=Matcher.Anchor(c.Image,anchor);if(hit is null||hit.Box.Right>=box.Right)return null;box=box with{Left=Math.Max(box.Left,hit.Box.Right+1)};}
        if(spec["between_anchors"] is {} between){var names=Names(between);var left=Matcher.Anchor(c.Image,names[0]);var right=Matcher.Anchor(c.Image,names[1]);if(left is null||right is null||left.Box.Right>=right.Box.Left)return null;box=box with{Left=left.Box.Right,Right=right.Box.Left};}
        return QuantityReader.Read(c.Image,new(itemId??spec["item_id"]?.GetValue<long>()??1,scope??S(spec,"scope")??"screen",box,S(spec,"format")??"integer",spec["expected_required"]?.GetValue<long>(),spec["min_confidence"]?.GetFiniteDouble()??.75,spec["thresholds"]?.AsArray().Select(n=>n!.GetValue<int>()).ToArray(),spec["include_red"]?.GetValue<bool>()??false,S(spec,"prefix")??"",S(spec,"suffix")??""));
    }
    internal QuantityValue? ConfirmedQuantity(string key,JsonNode? spec,long? item=null,string? scope=null)
    {
        var reading=ReadQuantity(spec,item,scope);if(reading?.Value is not {} value)
        {
            if(reading is not null)ReportWarning("quantity_read",string.Join("; ",reading.Readings.Select(r=>$"阈值{r.Threshold}: {r.Text?[..Math.Min(r.Text.Length,64)]??r.Error??"无读数"}")),key);
            return null;
        }
        return value;
    }
    internal PixelPoint? FindBagItem(string name)
    {
        var grid=Routes.Configuration["surplus_sale"]?["grid"];if(grid is null||LastCapture is null)return null;
        var label=QuantityReader.FindGridLabel(LastCapture.Image,Box(grid["region"]!),grid["columns"]!.GetValue<int>(),name);
        var offset=grid["icon_offset_y"]!.GetValue<int>();
        return label is{} found?new(found.Center.X,found.Center.Y-Box(new PixelBox(0,0,offset,offset)).Height):null;
    }
    internal bool BagItemNameMatches(string name)=>LastCapture is{} capture&&Routes.Configuration["maintenance"]?["surplus"]?["detail_title"] is{} region
        &&QuantityReader.FindGridLabel(capture.Image,Box(region),1,name) is not null;
    public ScreenProductionPort CreateProductionPort(string recipeKey,string batchId,IWorkflowStore workflowStore)=>new(this,recipeKey,batchId,workflowStore);
    public ScreenGatheringPort CreateGatheringPort(string routeKey,string batchId,IWorkflowStore workflowStore)=>new(this,routeKey,batchId,workflowStore);
    public ScreenStorageSupplyPort CreateStoragePort(WithdrawalPlan plan,string batchId,IWorkflowStore workflowStore,Func<InventorySnapshot>? packetInventory=null)=>new(this,plan,batchId,workflowStore,packetInventory);
    public ScreenFishingPort CreateFishingPort(string pointKey,string batchId,IWorkflowStore workflowStore)=>new(this,pointKey,batchId,workflowStore);
    public ScreenMaintenancePort CreateMaintenancePort(string batchId,IWorkflowStore workflowStore)=>new(this,batchId,workflowStore);
    public ScreenSurplusPort CreateSurplusPort(string batchId,IWorkflowStore workflowStore,Func<long,long?> requiredReserveFunc,Func<MabiAgent.Core.InventorySnapshot>? packetInventory=null,Func<long,string?>? itemName=null)=>new(this,batchId,workflowStore,requiredReserveFunc,packetInventory,itemName);
    public ScreenPurchasePort CreatePurchasePort(long itemId,string verifiedName,string batchId,Func<MabiAgent.Core.PurchaseState?> persistedPurchase,Func<MabiAgent.Core.MaintenancePreferences> preferences,Func<InventorySnapshot>? packetInventory=null)=>new(this,itemId,verifiedName,batchId,persistedPurchase,preferences,packetInventory);
    public void Dispose(){try{Guard.Dispose();}finally{try{(inputOps as IDisposable)?.Dispose();}finally{Matcher.Dispose();recognizer.Dispose();runtimeLimit?.Dispose();observer.Reset();}}}
}

public abstract class ScreenWorkflowPort(ScreenWorkflowSession session,string batchId,IWorkflowStore store):IForegroundWorkflowPort
{
    protected ScreenWorkflowSession Session {get;}=session;
    protected string BatchId {get;}=batchId;
    protected IWorkflowStore Store {get;}=store;
    protected virtual PageView Decorate(ClientCapture capture,PageView view)=>view;
    protected UiObservation Read(){Session.Use(Decorate);return Session.Observe();}
    protected UiObservation ReadFocused(Func<ClientCapture,PageView> recognize){Session.Use(Decorate,recognize);return Session.Observe();}
    public void Pause(string reason)=>Session.PausePort(reason);
    public void PauseLocal(string reason){}
    public virtual bool CanReleaseForeground()=>Session.CanReleaseForeground();
    public void Resume()=>Session.ResumePort();
    protected void Consume(string target,string kind,Func<UiObservation,bool> check,Func<ClientCapture,bool>? beforeDelivery=null,ushort? movementKey=null)=>Session.Consume(target,BatchId,Store,kind,check,beforeDelivery,movementKey);
    protected static string? S(JsonNode? node,string key)=>ScreenWorkflowSession.S(node,key);
    protected static string[] Names(JsonNode? node)=>ScreenWorkflowSession.Names(node);
}

