using System.Diagnostics;
namespace MabiAgent.Windows;

public enum MouseButton { Left,Right }
public interface INativeWindowsOps
{
    NativeWindowState ReadWindow(nint hwnd);
    RgbImage CaptureClient(NativeWindowState state);
    void MoveClient(nint hwnd,PixelPoint point);
    void MoveForClick(nint hwnd,PixelPoint point,MouseButton button)=>MoveClient(hwnd,point);
    void MouseButton(MouseButton button,bool down);
    void Wheel(int delta);
    void Key(ushort key,bool down,bool unicode=false);
    bool AnyPhysicalKeyDown();
}
public interface IGuardedWindowTransport
{
    bool RequiresForeground {get;}
    bool? PhysicalPointerDown=>null;
    bool AllowsZeroClickDelay=>false;
    int MoveSampleMilliseconds=>16;
    int ClickSampleMilliseconds=>32;
    void BindGuard(ForegroundInputGuard guard);
}
internal interface IGuardedClickLifetime
{ IDisposable? BeginClick(nint hwnd,PixelPoint point); }
/// <summary>The host must durably record intent and validate recipe/free-slot/owner before returning true. Unknown intents must always return false.</summary>
public interface IConsumptionAuthorization
{
    bool VerifyPersistedIntent(string intentId,TargetProof target,UiObservation fresh);
}
public interface IProcessScopedConsumptionAuthorization : IConsumptionAuthorization
{ bool AllowsProcessScope(SessionScope scope); }
/// <summary>Only reversible navigation is accepted by Click. Consumption requires a host-owned durable intent authority.</summary>
public sealed class ForegroundInputGuard:IDisposable
{
    private readonly object gate=new();private readonly NativeWindowState expected;private readonly SessionScope scope;private readonly INativeWindowsOps ops;private readonly Func<ClientCapture,UiObservation> observer;private readonly Func<double> clock;private readonly double maxAge;
    private readonly HashSet<string> dispatchedIntents=[];private readonly HashSet<MouseButton> heldButtons=[];private readonly HashSet<(ushort Code,bool Unicode)> heldKeys=[];
    private readonly bool globalTouch,centerOffset;
    private bool started,paused=true,manualPause,disposed;private int dispatching;private volatile bool pauseRequested;private TargetProof? issuedProof;private double transportAt;
    private ClientGeometry? capturedGeometry,issuedGeometry,dispatchGeometry;
    private readonly Func<string> resourceRevision;private long captureSequence,geometryGeneration;private ClientFrameEvidence? lastSourceFrame;private string transportRevision="";
    private Func<ClientCapture,bool>? pointerCheck;
    private Action? consumptionAuthorization,nativeActionStarted;
    private bool fixedDispatch;
    private ClientGeometry? scriptGeometry;
    private UiObservation? lastReview;private ClientCapture? lastReviewedCapture;
    public bool Paused{get{lock(gate)return paused;}}
    public bool RequiresForeground=>ops is not IGuardedWindowTransport transport||transport.RequiresForeground;
    public ClientGeometry ReadGeometry(){lock(gate){CheckRunning();var state=ops.ReadWindow(expected.WindowId);CheckNative(state,true);return state.Geometry;}}
    public string? PauseReason{get;private set;}
    public ForegroundInputGuard(NativeWindowState expected,SessionScope scope,INativeWindowsOps ops,Func<ClientCapture,UiObservation> observer,Func<double>? clock=null,double maxFrameAgeSeconds=.5,Func<string>? resourceRevision=null,InputOptions? inputOptions=null)
    {
        globalTouch=ops is TouchInputOps||inputOptions?.IsTouch==true;centerOffset=inputOptions?.CenterOffset??true;
        if(expected.WindowId==0||expected.ProcessId<=0||expected.ProcessStartUtcTicks<=0||string.IsNullOrWhiteSpace(expected.Executable)||string.IsNullOrWhiteSpace(scope.SessionId)||!double.IsFinite(maxFrameAgeSeconds)||maxFrameAgeSeconds is <=0 or >30)throw new ArgumentException("需要固定进程、会话及有界有效期");
        this.resourceRevision=resourceRevision??(()=>"");this.expected=expected;this.scope=scope;this.ops=ops;this.observer=observer;this.clock=clock??(()=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency);maxAge=maxFrameAgeSeconds;if(ops is NativeWindowsOps native)native.Bind(this);if(ops is IGuardedWindowTransport transport)transport.BindGuard(this);
    }
    public void Start(){lock(gate){ThrowDisposed();if(manualPause)throw new InputBlockedException("手动暂停需要点击继续");if(started)throw new InputBlockedException("运行已开始；暂停后需要点击继续");var actual=ops.ReadWindow(expected.WindowId);CheckNative(actual,true);scriptGeometry=actual.Geometry;lastReview=null;started=true;paused=false;PauseReason=null;}}
    public void Resume(){lock(gate){ThrowDisposed();if(!started)throw new InputBlockedException("尚未开始");var actual=ops.ReadWindow(expected.WindowId);CheckNative(actual,true);scriptGeometry=actual.Geometry;ReleaseOwned();manualPause=false;paused=false;pauseRequested=false;issuedProof=null;lastReview=null;PauseReason=null;}}
    internal void ResumeWorkflow(){lock(gate){if(manualPause||PauseReason is not (null or "inventory_sync"))throw new InputBlockedException("请点击继续当前暂停");Resume();}}
    public void RequestPause()=>pauseRequested=true;
    public void Pause(string reason="manual"){RequestPause();lock(gate){issuedProof=null;lastReview=null;paused=true;manualPause|=reason=="manual";PauseReason=reason;ReleaseOwned();}}
    public ClientCapture Capture()
    {
        lock(gate)
        {
            ThrowDisposed();var began=clock();var revision=resourceRevision();var before=ops.ReadWindow(expected.WindowId);CheckNative(before,true);
            var source=ops is ICurrentClientFrameSource current?current.CaptureCurrentClientFrame(before):
                new CurrentClientFrame(ops.CaptureClient(before),new("client-capture",1,++captureSequence,began));
            var after=ops.ReadWindow(expected.WindowId);CheckNative(after,true);var completed=clock();
            if(!double.IsFinite(began)||!double.IsFinite(completed)||began<0||completed<began)throw new InputBlockedException("截图时钟无效");
            if(completed-began>maxAge)throw new InputBlockedException("截图耗时超出画面有效期"){CaptureTimedOut=true};
            if(before.Geometry!=after.Geometry||before.WindowRect!=after.WindowRect)throw new InputBlockedException("截图期间窗口变化");
            if(source.Image.Width!=after.Geometry.Width||source.Image.Height!=after.Geometry.Height)throw new InputBlockedException("截图必须为原生客户区");
            var frame=source.Evidence;
            if(!frame.IsCurrent||!Fresh(frame.SourceTime))throw new InputBlockedException("预览、缓存或过期帧不能作为输入证据");
            if(lastSourceFrame is {} prior&&frame.Backend==prior.Backend&&frame.Generation==prior.Generation&&
                (frame.Sequence<=prior.Sequence||ops is ICurrentClientFrameSource&&frame.SourceTime<=prior.SourceTime))throw new InputBlockedException("截图源帧序列或源时间未前进");
            if(revision!=resourceRevision())throw new InputBlockedException("截图期间识别资源变化");
            if(capturedGeometry is null||capturedGeometry.Width!=after.Geometry.Width||capturedGeometry.Height!=after.Geometry.Height||capturedGeometry.Dpi!=after.Geometry.Dpi)geometryGeneration++;
            frame=frame with{GeometryGeneration=geometryGeneration};
            if(lastSourceFrame is {} previous&&(frame.Backend!=previous.Backend||frame.Generation!=previous.Generation||frame.GeometryGeneration!=previous.GeometryGeneration))issuedProof=null;
            lastSourceFrame=frame;capturedGeometry=after.Geometry;return new(after,source.Image,frame.SourceTime,frame,revision);
        }
    }
    public UiObservation Observe(){lock(gate){CheckRunning();return Review();}}
    internal UiObservation ObserveFrame()
    {lock(gate){var capture=Capture();lastReviewedCapture=capture;return lastReview=observer(capture);}}
    internal TargetProof ObserveCurrentTarget(string name,UiObservation? expectedReview)
    {
        lock(gate)
        {
            CheckRunning();
            // Only a declared observation supplies business evidence. Expiry asks
            // the caller to observe again rather than silently adding a capture.
            if(lastReviewedCapture is not {} source||expectedReview is null||expectedReview.ImageSha256!=source.Image.Sha256||expectedReview.CapturedAt!=source.CapturedAt||!ReferenceEquals(lastReview,expectedReview)||!expectedReview.IdentityVerified
                ||expectedReview.Scope!=scope||expectedReview.View.Modal||expectedReview.View.Page is "unknown" or "restart_required"
                ||expectedReview.Frame!=lastSourceFrame||expectedReview.ResourceRevision!=resourceRevision()||!Fresh(expectedReview.CapturedAt))throw new InputBlockedException("当前观察已失效，请在等待节点重新识别",targetChangedBeforeDispatch:true);
            var native=ops.ReadWindow(expected.WindowId);CheckNative(native,true);
            if(native.Geometry!=capturedGeometry)throw new InputBlockedException("客户区已变化，请在等待节点重新识别",targetChangedBeforeDispatch:true);
            return IssueTarget(name,expectedReview,false);
        }
    }
    public TargetProof ObserveTarget(string name,bool randomize=false)
    {
        lock(gate)
        {
            CheckRunning();return IssueTarget(name,Review(requireStable:false),randomize);
        }
    }
    private TargetProof IssueTarget(string name,UiObservation review,bool randomize)
    {
            if(!review.View.Targets.TryGetValue(name,out var point))throw new InputBlockedException("当前画面未找到目标",targetChangedBeforeDispatch:true);
            PixelBox? region=null;var random=globalTouch?centerOffset:randomize||review.View.RandomTargets?.Contains(name)==true;
            if(random)
            {
                if(review.View.TargetRegions?.TryGetValue(name,out var found)==true&&capturedGeometry is {} geometry&&found.IsInside(geometry.Width,geometry.Height)&&found.Contains(point))region=found;
                else if(!globalTouch)throw new InputBlockedException("目标没有已核实的点击区域");
            }
            var shape=review.View.TargetShapes?.GetValueOrDefault(name)??ClickRegionShape.Rectangle;
            if(region is {} clickable&&!clickable.Contains(point,shape))throw new InputBlockedException("目标不在点击形状内");
            issuedGeometry=capturedGeometry;issuedProof=new(name,review.View.Page,point,review.ImageSha256,review.CapturedAt,review.Scope,region,shape,review.Frame,review.ResourceRevision);return issuedProof;
    }
    public void Click(TargetProof target,MouseButton button=MouseButton.Left)
    {
        RejectBackgroundTextTouch(target);
        if(!NavigationTransitions.All.TryGetValue(target.Name,out var route)||route.From!=target.Page)throw new InputBlockedException("未审核的可逆导航");
        Dispatch(target,()=>ClickNative(ClickPoint(target),button));
    }
    /// <summary>Fixed script actions use system boundaries only; they do not capture or recognize a page.</summary>
    public void ClickAt(PixelPoint point,PixelBox? region=null,MouseButton button=MouseButton.Left)
        =>DispatchFixed(g=>{CheckPoint(point,g);if(region is {} box){if(!box.IsInside(g.Width,g.Height)||!box.Contains(point))throw new ArgumentException("固定点击区域无效");if(globalTouch&&centerOffset)point=box.CenterOffsetPoint(Math.Max(1,(int)Math.Round(3d*g.Width/1280)));}ClickNative(point,button);});
    public void PressKey(ushort key,TimeSpan duration)
    {
        if(!InputKeyPolicy.IsShortcut(key)||duration<=TimeSpan.Zero||duration>TimeSpan.FromSeconds(2))throw new ArgumentException("按键或按住时长无效");
        DispatchFixed(_=>{KeyDown(key,false);WaitForInputSample((int)Math.Ceiling(duration.TotalMilliseconds));KeyUp(key,false);});
    }
    public void TypeText(string text,int backspaces=0)
    {
        if(text.Length>128||text.Any(char.IsControl)||backspaces is <0 or >128)throw new ArgumentException("只允许有界字面文字");
        DispatchFixed(_=>{for(var i=0;i<backspaces;i++)KeyPair(8,false);WriteLiteralText(text);});
    }
    public void ScrollAt(PixelPoint point,PixelBox surface,int steps,bool zoom=false)
    {
        if(steps==0||Math.Abs((long)steps)>12)throw new ArgumentOutOfRangeException(nameof(steps));
        DispatchFixed(g=>{CheckPoint(point,g);if(!surface.IsInside(g.Width,g.Height)||!surface.Contains(point))throw new ArgumentException("滚动区域无效");if(ops is IGuardedPointerGestures pointer){RunPointer(MouseButton.Left,()=>pointer.Scroll(point,surface,steps,zoom));return;}ops.MoveClient(expected.WindowId,point);for(var i=0;i<Math.Abs(steps);i++){CheckTransport();ops.Wheel(Math.Sign(steps)*120);WaitForInputSample(40);}});
    }
    public void DragAt(PixelPoint from,PixelPoint to,int steps=12)
    {
        if(steps is <1 or >120)throw new ArgumentOutOfRangeException(nameof(steps));
        DispatchFixed(g=>{CheckPoint(from,g);CheckPoint(to,g);if(ops is IGuardedPointerGestures pointer){RunPointer(MouseButton.Left,()=>pointer.Drag(from,to,steps));return;}ops.MoveClient(expected.WindowId,from);Down(MouseButton.Left);for(var i=1;i<=steps;i++){CheckTransport();ops.MoveClient(expected.WindowId,new(from.X+(to.X-from.X)*i/steps,from.Y+(to.Y-from.Y)*i/steps));WaitForInputSample(16);}Up(MouseButton.Left);});
    }
    private static void CheckPoint(PixelPoint point,ClientGeometry geometry)
    {if(!new PixelBox(0,0,geometry.Width,geometry.Height).Contains(point))throw new ArgumentOutOfRangeException(nameof(point));}
    private void DispatchFixed(Action<ClientGeometry> action)
    {
        lock(gate)
        {
            CheckRunning();if(dispatching!=0||heldButtons.Count>0||heldKeys.Count>0)throw new InputBlockedException("前次释放未完成");
            var state=ops.ReadWindow(expected.WindowId);CheckNative(state,true);
            var baseline=scriptGeometry??expected.Geometry;
            if(state.Geometry.Width!=baseline.Width||state.Geometry.Height!=baseline.Height||state.Geometry.Dpi!=baseline.Dpi){paused=true;PauseReason="geometry_changed";throw new InputBlockedException("游戏窗口大小或缩放比例已变化，请点击继续");}
            scriptGeometry=state.Geometry;dispatchGeometry=state.Geometry;transportRevision=resourceRevision();transportAt=clock();fixedDispatch=true;issuedProof=null;dispatching=Environment.CurrentManagedThreadId;
            try{CheckTransport();action(state.Geometry);}
            catch(InputBlockedException){if(pauseRequested){paused=true;PauseReason??="pause_requested";}throw;}
            catch(MabiAgent.Core.InputNotDispatchedException){throw;}
            catch{paused=true;PauseReason??="input_outcome_unknown";throw;}
            finally{try{ReleaseOwned();}finally{fixedDispatch=false;dispatching=0;dispatchGeometry=null;lastReview=null;lastDispatchView=null;}}
        }
    }
    public void Consume(TargetProof target,string intentId,IConsumptionAuthorization authority,MouseButton button=MouseButton.Left,Func<ClientCapture,bool>? beforeDelivery=null,ushort? key=null,ushort? movementKey=null)
    {
        lock(gate)
        {
        var enteredNativeAction=false;pointerCheck=beforeDelivery;
        try
        {
            if(beforeDelivery is not null&&ops is IGuardedPointerGestures&&ops is not TouchInputOps{SupportsPreparedReview:true})throw new MabiAgent.Workflows.InputRejectedBeforeDispatchException("当前通道不支持转交前复核，未发送");
            if(string.IsNullOrWhiteSpace(intentId)||!scope.HasIdentity && !(authority is IProcessScopedConsumptionAuthorization process && process.AllowsProcessScope(scope)))throw new InputBlockedException("消费需要当前会话与落盘意图");
            if(dispatchedIntents.Contains(intentId))throw new InputBlockedException("消费意图不可重放或未落盘");
            if(key is not null&&key!=32)throw new InputBlockedException("消费快捷键未审核");
            if(movementKey is not null&&(target.Name!="fishing.start"||movementKey is not(87 or 83)||key!=32))throw new InputBlockedException("抛竿移动仅支持轻按 W/S 后空格");
            consumptionAuthorization=()=>{var review=lastReview??throw new MabiAgent.Workflows.InputRejectedBeforeDispatchException("消费观察已失效");if(dispatchedIntents.Contains(intentId)||!authority.VerifyPersistedIntent(intentId,target,review))throw new MabiAgent.Workflows.InputRejectedBeforeDispatchException("消费意图不可重放或未落盘");dispatchedIntents.Add(intentId);};
            nativeActionStarted=()=>enteredNativeAction=true;
            Dispatch(target,()=>{if(key is{} code){if(movementKey is{} move)RunFishingMovement(move);KeyDown(code,false);try{WaitForInputSample(80);}finally{KeyUp(code,false);}}else ClickNative(ClickPoint(target),button);},preparedReview:beforeDelivery is not null&&ops is TouchInputOps);
        }
        catch(InputBlockedException e) when(!enteredNativeAction){throw new MabiAgent.Workflows.InputRejectedBeforeDispatchException(e.Message);}
        catch(InputBlockedException e) when(movementKey is not null&&enteredNativeAction)
        {paused=true;PauseReason??="input_outcome_unknown";throw new InputOutcomeUnknownException("抛竿移动已尝试发送，后续输入结果未确认："+e.Message);}
        finally{pointerCheck=null;consumptionAuthorization=null;nativeActionStarted=null;}
        }
    }
    internal void ClickReviewedNavigation(TargetProof target,string from,MouseButton button=MouseButton.Left,Func<UiObservation,bool>? validate=null)
    {
        RejectBackgroundTextTouch(target);
        if(target.Page!=from)throw new InputBlockedException("导航页面变化");
        Dispatch(target,()=>ClickNative(ClickPoint(target),button),review=>{if(validate is not null&&!validate(review))throw new InputBlockedException("导航前文字未核实");});
    }
    public void Scroll(TargetProof target,int steps,bool zoom=false)
    {
        if(steps==0||Math.Abs((long)steps)>12)throw new ArgumentOutOfRangeException(nameof(steps));
        Dispatch(target,()=>{CheckTransport();if(ops is IGuardedPointerGestures pointer){var surface=CurrentSurface(target);RunPointer(MouseButton.Left,()=>pointer.Scroll(target.Point,surface,steps,zoom||target.Page is "region_map" or "world_map"));return;}ops.MoveClient(expected.WindowId,target.Point);for(var i=0;i<Math.Abs(steps);i++){CheckTransport();ops.Wheel(Math.Sign(steps)*120);WaitForInputSample(40);}},fixedSequence:true);
    }
    public void Drag(TargetProof from,PixelPoint to,int steps=12,Func<UiObservation,bool>? validate=null)
    {
        if(steps is <1 or >120)throw new ArgumentOutOfRangeException(nameof(steps));
        Dispatch(from,()=>{CheckTransport();if(dispatchGeometry is not {} geometry||!new PixelBox(0,0,geometry.Width,geometry.Height).Contains(to))throw new ArgumentOutOfRangeException(nameof(to));var start=globalTouch?from.Point:ClickPoint(from);if(ops is IGuardedPointerGestures pointer){RunPointer(MouseButton.Left,()=>pointer.Drag(start,to,steps));return;}ops.MoveClient(expected.WindowId,start);Down(MouseButton.Left);WaitForInputSample(50);for(var i=1;i<=steps;i++){CheckTransport();ops.MoveClient(expected.WindowId,new(start.X+(to.X-start.X)*i/steps,start.Y+(to.Y-start.Y)*i/steps));WaitForInputSample(16);}Up(MouseButton.Left);},review=>{if(validate is not null&&!validate(review))throw new InputBlockedException("拖拽目标未核实");});
    }
    private void WaitForInputSample(int milliseconds)
    {var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<milliseconds){Thread.Sleep(Math.Min(10,milliseconds-(int)timer.ElapsedMilliseconds));CheckTransport();}}
    public void TypeText(TargetProof field,string text,int backspaces=0,bool focus=true)
    {
        if(focus&&!RequiresForeground)throw new MabiAgent.Workflows.InputRejectedBeforeDispatchException("后台文字输入不能触摸聚焦；请先用已校准键盘路径进入编辑");
        if(text.Length>128||text.Any(char.IsControl)||backspaces is <0 or >128)throw new ArgumentException("只允许有界字面文字");
        Dispatch(field,()=>{if(focus)ClickNative(ClickPoint(field),MouseButton.Left);for(var i=0;i<backspaces;i++)KeyPair(8,false);WriteLiteralText(text);});
    }
    internal static bool IsTextEntryTarget(string target)=>target is "bag.search_open" or "bag.search_input" or "storage.search_open" or "storage.search_input" or "storage.quantity" or "bag.sale_quantity" or "bag.decompose_quantity";
    private void RejectBackgroundTextTouch(TargetProof target)
    {
        if(RequiresForeground)return;
        var view=lastReview?.View;
        if(IsTextEntryTarget(target.Name)&&!(target.Name=="bag.search_open"&&target.Page=="bag")||view is not null&&view.Targets.Any(p=>IsTextEntryTarget(p.Key)&&p.Key!="bag.search_open"&&(view.TargetRegions is{} regions&&regions.TryGetValue(p.Key,out var box)&&box.Contains(target.Point)||p.Value==target.Point)))
            throw new MabiAgent.Workflows.InputRejectedBeforeDispatchException("后台文本框不允许触摸，避免弹出触控键盘；请使用键盘编辑或列表查找");
    }
    public void EditBagSearch(TargetProof field,string text)
    {
        if(field.Name!="bag.search_input"||field.Page!="bag_search"||text.Length>128||text.Any(char.IsControl))throw new ArgumentException("背包搜索只接受道具名称");
        Dispatch(field,()=>
        {
            SampleEnter();WaitForInputSample(250);
            for(var i=0;i<128;i++)KeyPair(8,false);
            WaitForInputSample(150);
            WriteLiteralText(text);
            WaitForInputSample(150);SampleEnter();
        });
    }
    private void SampleEnter()
    {KeyDown(13,false);try{WaitForInputSample(80);}finally{KeyUp(13,false);}WaitForInputSample(80);}
    private void WriteLiteralText(string text)
    {
        for(var i=0;i<text.Length;i++)
        {
            KeyPair(text[i],true);
            if(char.IsHighSurrogate(text[i])&&i+1<text.Length&&char.IsLowSurrogate(text[i+1]))KeyPair(text[++i],true);
            WaitForInputSample(100);
        }
    }
    public void EditQuantity(TargetProof field,string text)
    {
        if(field.Name is not("storage.quantity" or "bag.sale_quantity" or "bag.decompose_quantity")||text.Length is <1 or >16||text.Any(c=>c<'0'||c>'9'))throw new ArgumentException("数量键盘路径只接受已校准数量框及数字");
        Dispatch(field,()=>
        {
            void SampleKey(ushort key,int milliseconds){KeyDown(key,false);try{WaitForInputSample(milliseconds);}finally{KeyUp(key,false);}WaitForInputSample(30);}
            SampleKey(13,80);WaitForInputSample(250);
            for(var i=0;i<16;i++)SampleKey(8,25);
            foreach(var c in text)KeyPair(c,true);
            WaitForInputSample(80);SampleKey(13,80);
        });
    }
    public void PressKey(TargetProof context,ushort key,TimeSpan duration)
    {
        if(!InputKeyPolicy.IsShortcut(key)||duration<=TimeSpan.Zero||duration>TimeSpan.FromSeconds(2))throw new ArgumentException("只允许有界 WASD、I、K、空格、X 及 ESC");
        Dispatch(context,()=>{KeyDown(key,false);var timer=Stopwatch.StartNew();while(timer.Elapsed<duration){Thread.Sleep(10);CheckTransport();}KeyUp(key,false);});
    }
    private void Dispatch(TargetProof target,Action action,Action<UiObservation>? authorize=null,bool preparedReview=false,bool fixedSequence=false)
    {
        lock(gate)
        {
            CheckRunning();
            if(dispatching!=0||heldButtons.Count>0||heldKeys.Count>0)throw new InputBlockedException("前次释放未完成");
            if(target!=issuedProof||target.Scope!=scope||target.ResourceRevision!=resourceRevision()||!Fresh(target.CapturedAt)||string.IsNullOrWhiteSpace(target.ImageSha256))throw new InputBlockedException("目标证据失效");
            // The target proof is the single business observation for this
            // action. Dispatch performs only transport/window checks; it does
            // not rescan the page or require a second stable frame.
            var review=lastReview;
            if(review is null||review.ImageSha256!=target.ImageSha256||review.CapturedAt!=target.CapturedAt||review.Scope!=scope||review.Frame!=target.Frame||review.ResourceRevision!=target.ResourceRevision||!review.IdentityVerified)
                throw new InputBlockedException("目标观察已失效，请重新识别目标",targetChangedBeforeDispatch:true);
            if(preparedReview&&review.Frame!=lastSourceFrame)throw new InputBlockedException("触控转交前目标源帧已变化，请重新识别目标",targetChangedBeforeDispatch:true);
            if(capturedGeometry is not {} current||issuedGeometry is not {} issued||current.Width!=issued.Width||current.Height!=issued.Height||current.Dpi!=issued.Dpi)throw new InputBlockedException("客户区尺寸或 DPI 已变化，请重新识别目标");
            if(target.ClickRegion is {} region&&(!region.IsInside(current.Width,current.Height)||(review.View.TargetShapes?.GetValueOrDefault(target.Name)??ClickRegionShape.Rectangle)!=target.ClickShape))throw new InputBlockedException("点击区域已变化");
            authorize?.Invoke(review);lastDispatchView=review.View;issuedProof=null;transportAt=review.CapturedAt;transportRevision=review.ResourceRevision;dispatchGeometry=current;dispatching=Environment.CurrentManagedThreadId;
            // A bounded scroll validates its source once, then completes the
            // fixed gesture with system boundaries. Its own duration must not
            // expire the source frame between pinch contacts.
            fixedDispatch=fixedSequence;
            try{CheckTransport();action();}
            catch(InputBlockedException) when(heldButtons.Count==0&&heldKeys.Count==0&&!pauseRequested){throw;}
            catch(InputBlockedException) when(heldButtons.Count==0&&heldKeys.Count==0&&pauseRequested){paused=true;PauseReason??="pause_requested";throw;}
            catch(MabiAgent.Core.InputNotDispatchedException){throw;}
            catch{paused=true;PauseReason??="input_outcome_unknown";throw;}
            finally{try{ReleaseOwned();}finally{dispatching=0;dispatchGeometry=null;lastDispatchView=null;fixedDispatch=false;}}
        }
    }
    private UiObservation Review(bool requireStable=true)
    {
        var capture=Capture();var review=lastReview=observer(capture);
        if(review.Frame is not null&&review.Frame!=capture.Frame||review.ResourceRevision.Length>0&&review.ResourceRevision!=capture.ResourceRevision)throw new InputBlockedException("识别结果的源帧或资源版本不符");
        review=review with{Frame=capture.Frame,ResourceRevision=capture.ResourceRevision};
        lastReview=review;
        var sourceVerified=review.ImageSha256==capture.Image.Sha256&&review.CapturedAt==capture.CapturedAt&&Fresh(review.CapturedAt)&&review.Scope==scope&&review.IdentityVerified;
        if(!sourceVerified||review.View.Modal||review.View.Page is "unknown" or "restart_required"||(requireStable&&!review.Stable))throw new InputBlockedException($"当前画面或身份尚未独立核实（页面 {review.View.Page}，稳定 {review.Stable}，身份 {review.IdentityVerified}，帧龄 {clock()-review.CapturedAt:F2}秒）",targetChangedBeforeDispatch:sourceVerified&&!review.Stable&&!review.View.Modal&&review.View.Page is not ("unknown" or "restart_required"));
        var actual=ops.ReadWindow(expected.WindowId);CheckNative(actual,true);if(actual.Geometry!=capturedGeometry)throw new InputBlockedException("识别期间窗口变化，请重新识别");return review;
    }
    private bool Fresh(double at){var now=clock();return double.IsFinite(at)&&double.IsFinite(now)&&now>=at&&now-at<=maxAge;}
    private void CheckRunning(){ThrowDisposed();if(!started||paused)throw new InputBlockedException("输入暂停");}
    private void CheckNative(NativeWindowState actual,bool foreground)
    {
        if(actual.WindowId==expected.WindowId&&actual.ProcessId==expected.ProcessId&&actual.ProcessStartUtcTicks==expected.ProcessStartUtcTicks&&actual.Minimized)
        {paused=true;if(!manualPause)PauseReason="window_minimized";throw new InputBlockedException("游戏已最小化，请还原窗口后继续");}
        if(actual.WindowId!=expected.WindowId||actual.ProcessId!=expected.ProcessId||actual.ProcessStartUtcTicks!=expected.ProcessStartUtcTicks||!string.Equals(Path.GetFullPath(actual.Executable),Path.GetFullPath(expected.Executable),StringComparison.OrdinalIgnoreCase)||actual.ClassName!="UnityWndClass"||actual.Geometry.Width<=0||actual.Geometry.Height<=0||(long)actual.Geometry.Width*actual.Geometry.Height>16_777_216||actual.Geometry.Dpi is <48 or >768||!actual.Visible||actual.Minimized||!actual.Enabled)throw new InputBlockedException("窗口、进程身份或客户区发生变化");
        if(foreground&&RequiresForeground&&actual.ForegroundId!=expected.WindowId){paused=true;PauseReason="focus_lost";throw new InputBlockedException("运行失焦，已暂停");}
    }
    private void CheckTransport()
    {
        CheckRunning();if(pauseRequested||transportRevision!=resourceRevision()||!fixedDispatch&&!Fresh(transportAt))throw new InputBlockedException("暂停请求或当前画面证据失效");if(dispatching!=Environment.CurrentManagedThreadId)throw new InputBlockedException("共享守卫上下文缺失");var state=ops.ReadWindow(expected.WindowId);CheckNative(state,true);
        if(state.Geometry!=dispatchGeometry)throw new InputBlockedException("输入期间窗口位置、尺寸或 DPI 变化，请重新识别");
        var physicalPointer=(ops as IGuardedWindowTransport)?.PhysicalPointerDown??(state.LeftDown||state.RightDown);
        if((RequiresForeground||state.ForegroundId==expected.WindowId)&&((globalTouch||heldButtons.Count==0)&&physicalPointer||(globalTouch||heldKeys.Count==0)&&ops.AnyPhysicalKeyDown())){paused=true;PauseReason="user_input";throw new InputBlockedException("检测到玩家输入");}
    }
    internal void ReviewPreparedPointer()
    {
        lock(gate)
        {
            CheckTransport();
            if(pointerCheck is not null)
            {
                var current=Capture();
                if(!pointerCheck(current)||clock()-current.CapturedAt>.2)
                    throw new MabiAgent.Workflows.InputRejectedBeforeDispatchException("触控转交前目标已变化或复核超过200毫秒，未发送");
                transportAt=current.CapturedAt;CheckTransport();
            }
            // The source authorizes this bounded gesture once. Persistence and
            // native delivery can take time; its own age must not abort the
            // remaining contact/release. Pause, identity and geometry still apply.
            fixedDispatch=true;
            if(ops is TouchInputOps){CommitConsumption();nativeActionStarted?.Invoke();}
        }
    }
    private void CommitConsumption(){var commit=consumptionAuthorization;if(commit is null)return;consumptionAuthorization=null;commit();}
    private void RunFishingMovement(ushort code)
    {
        // Persist before movement, then use the game's displayed Space action
        // through the existing background keyboard without touch preparation.
        KeyDown(code,false);try{WaitForInputSample(60);}finally{KeyUp(code,false);}
        WaitForInputSample(300);
    }
    internal void AuthorizeNativeTransport(){lock(gate)CheckTransport();}
    internal void AuthorizeMouseRelease(MouseButton button){lock(gate){if(!heldButtons.Contains(button))throw new InputBlockedException("只能释放本守卫尝试按下的按钮");}}
    internal void AuthorizeKeyRelease(ushort code,bool unicode){lock(gate){if(!heldKeys.Contains((code,unicode)))throw new InputBlockedException("只能释放本守卫尝试按下的按键");}}
    private PixelBox CurrentSurface(TargetProof proof)
    {
        var fresh=lastDispatchView?.TargetRegions;
        if(fresh is not null&&fresh.TryGetValue(proof.Name,out var surface))return surface;
        throw new InputBlockedException("触控滚动缺少已核验区域");
    }
    private PageView? lastDispatchView;
    private PixelPoint ClickPoint(TargetProof proof)
    {
        if(proof.ClickRegion is not {} region)return proof.Point;
        return globalTouch?region.CenterOffsetPoint(Math.Max(1,(int)Math.Round(3d*(dispatchGeometry?.Width??1280)/1280)),proof.ClickShape):region.RandomPoint(shape:proof.ClickShape);
    }
    private void RunPointer(MouseButton button,Action action)
    {CheckTransport();if(ops is not TouchInputOps){fixedDispatch=true;CommitConsumption();nativeActionStarted?.Invoke();}heldButtons.Add(button);try{action();}finally{Up(button);}}

    private void ClickNative(PixelPoint point,MouseButton button)
    {
        if(ops is IGuardedPointerGestures pointer){RunPointer(button,()=>pointer.Click(point,button));return;}
        CheckTransport();var moveDelay=ops is IGuardedWindowTransport transport?transport.MoveSampleMilliseconds:16;var clickDelay=ops is IGuardedWindowTransport clickTransport?clickTransport.ClickSampleMilliseconds:32;var minimum=ops is IGuardedWindowTransport{AllowsZeroClickDelay:true}?0:1;
        if(moveDelay<minimum||moveDelay>250||clickDelay<minimum||clickDelay>250)throw new InputBlockedException("输入采样间隔无效");
        IDisposable? lease=null;
        try{lease=(ops as IGuardedClickLifetime)?.BeginClick(expected.WindowId,point);nativeActionStarted?.Invoke();ops.MoveForClick(expected.WindowId,point,button);WaitForInputSample(moveDelay);ReviewPreparedPointer();Down(button);WaitForInputSample(clickDelay);}
        finally{try{Up(button);}finally{lease?.Dispose();}}
    }
    private void Down(MouseButton button){CheckTransport();fixedDispatch=true;CommitConsumption();heldButtons.Add(button);nativeActionStarted?.Invoke();ops.MouseButton(button,true);}
    private void Up(MouseButton button){if(heldButtons.Contains(button)){ops.MouseButton(button,false);heldButtons.Remove(button);}}
    private void KeyDown(ushort code,bool unicode){CheckTransport();fixedDispatch=true;CommitConsumption();heldKeys.Add((code,unicode));nativeActionStarted?.Invoke();ops.Key(code,true,unicode);}
    private void KeyUp(ushort code,bool unicode){if(heldKeys.Contains((code,unicode))){ops.Key(code,false,unicode);heldKeys.Remove((code,unicode));}}
    private void KeyPair(ushort code,bool unicode){KeyDown(code,unicode);KeyUp(code,unicode);}
    private void ReleaseOwned(){Exception? error=null;foreach(var button in heldButtons.ToArray())try{Up(button);}catch(Exception e){error??=e;}foreach(var key in heldKeys.ToArray())try{KeyUp(key.Code,key.Unicode);}catch(Exception e){error??=e;}if(error is not null)throw new InputOutcomeUnknownException("输入释放未确认："+error.Message);}
    private void ThrowDisposed()=>ObjectDisposedException.ThrowIf(disposed,this);
    public void Dispose(){lock(gate){if(disposed)return;paused=true;try{ReleaseOwned();}finally{disposed=true;}}}
}
public sealed record NavigationTransition(string From,string To);
public static class NavigationTransitions
{
    public static IReadOnlyDictionary<string,NavigationTransition> All{get;}=new Dictionary<string,NavigationTransition>
    {
        ["scene.open_bag"]=new("scene","bag"),["bag.close"]=new("bag","scene"),["scene.open_skills"]=new("scene","skills"),["skills.close"]=new("skills","scene"),
        ["skills.open_processing"]=new("skills","category_page"),["category.open_metal"]=new("category_page","metal_panel"),["metal.open_iron_recipe"]=new("metal_panel","recipe_detail"),["recipe_detail.close"]=new("recipe_detail","metal_panel"),
        ["storage_account.close"]=new("storage_account","storage_dialog"),["storage_character.close"]=new("storage_character","storage_dialog"),["storage.open_character"]=new("storage_account","storage_character"),["storage.open_account"]=new("storage_character","storage_account"),["storage_dialog.leave"]=new("storage_dialog","storage_farewell"),["storage_farewell.leave"]=new("storage_farewell","scene")
    };
}
