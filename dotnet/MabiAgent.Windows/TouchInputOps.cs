using System.Diagnostics;
namespace MabiAgent.Windows;

public sealed record TouchDiagnosticWriteFailure(string GestureId,string Stage,string Error);
internal interface IKeyboardPointerPreparation{void PreparePointerInput();}
/// <summary>Shared background pointer gestures and window-message keyboard. No foreground fallback.</summary>
public sealed class TouchInputOps:INativeWindowsOps,ICurrentClientFrameSource,IGuardedWindowTransport,IGuardedPointerGestures,IDisposable
{
    private readonly NativeWindowState target;
    private readonly INativeWindowsOps passive;
    private readonly INativeWindowsOps keyboard;
    private readonly ITouchGestureRunner runner;
    private ForegroundInputGuard? guard;
    private bool disposed;
    public bool RequiresForeground=>false;
    internal bool SupportsPreparedReview=>runner.SupportsPreparedReview;
    public bool? PhysicalPointerDown=>runner.PhysicalPointerDown;
    /// <summary>Raised after a diagnostic write fails; subscribers must report on their own UI/main-thread path.</summary>
    public event Action<TouchDiagnosticWriteFailure>? DiagnosticWriteFailed;
    public TouchInputOps(NativeWindowState target,MabiAgent.Core.RuntimeDiagnostics? diagnostics=null):this(target,new NativeWindowsOps(),new WindowMessageInputOps(target,notifyActivation:true),new ForwardedTouchGestureRunner(diagnostics:diagnostics))
    {if(this.runner is ForwardedTouchGestureRunner native)native.DiagnosticWriteFailed+=failure=>DiagnosticWriteFailed?.Invoke(failure);}
    internal TouchInputOps(NativeWindowState target,INativeWindowsOps passive,INativeWindowsOps keyboard,ITouchGestureRunner runner)
    {this.target=target;this.passive=passive;this.keyboard=keyboard;this.runner=runner;}
    private ForegroundInputGuard Guard=>guard??throw new InputBlockedException("触控输入必须经共享守卫");
    public void BindGuard(ForegroundInputGuard owner)
    {if(guard is not null&&!ReferenceEquals(guard,owner))throw new InputBlockedException("触控通道已绑定其他守卫");guard=owner;runner.SetBeforeDelivery(owner.ReviewPreparedPointer);(keyboard as IGuardedWindowTransport)?.BindGuard(owner);}
    public NativeWindowState ReadWindow(nint hwnd)=>passive.ReadWindow(hwnd);
    public RgbImage CaptureClient(NativeWindowState state)=>passive.CaptureClient(state);
    public CurrentClientFrame CaptureCurrentClientFrame(NativeWindowState state)=>passive is ICurrentClientFrameSource source?source.CaptureCurrentClientFrame(state):throw new InputBlockedException("缺少当前帧来源");
    public bool AnyPhysicalKeyDown()=>passive.AnyPhysicalKeyDown();
    public void Key(ushort key,bool down,bool unicode=false)=>keyboard.Key(key,down,unicode);
    public void MoveClient(nint hwnd,PixelPoint point)=>throw new InputBlockedException("触控不使用系统鼠标定位");
    public void Wheel(int delta)=>throw new InputBlockedException("触控滚动需提供已核验区域");
    public void MouseButton(MouseButton button,bool down)
    {if(down)throw new InputBlockedException("请使用完整触控手势");Guard.AuthorizeMouseRelease(button);runner.Release(VerifyIdentity);}
    private NativeWindowState VerifyIdentity()
    {
        var current=passive.ReadWindow(target.WindowId);
        if(current.WindowId!=target.WindowId||current.ProcessId!=target.ProcessId||current.ProcessStartUtcTicks!=target.ProcessStartUtcTicks||!string.Equals(current.Executable,target.Executable,StringComparison.OrdinalIgnoreCase)||current.ClassName!=target.ClassName)
            throw new InputOutcomeUnknownException("触控目标身份变化，禁止转交或重放");
        return current;
    }
    private void Execute(TouchGesturePlan client,int holdMilliseconds=0)
    {
        ObjectDisposedException.ThrowIf(disposed,this);Guard.AuthorizeNativeTransport();if(keyboard is IKeyboardPointerPreparation preparation)preparation.PreparePointerInput();var before=VerifyIdentity();
        foreach(var point in client.Points.SelectMany(p=>p))if(!new PixelBox(0,0,before.Geometry.Width,before.Geometry.Height).Contains(point))throw new InputBlockedException("触控轨迹超出客户区");
        void Check(){Guard.AuthorizeNativeTransport();if(VerifyIdentity().Geometry!=before.Geometry)throw new InputBlockedException("触控期间窗口位置、尺寸或DPI变化");}
        runner.Execute(client.Screen(before.Geometry),target.WindowId,holdMilliseconds,Check,()=>Guard.AuthorizeMouseRelease(holdMilliseconds==2000?Windows.MouseButton.Right:Windows.MouseButton.Left),VerifyIdentity);
    }
    public void Click(PixelPoint point,MouseButton button)
    {if(button is not(Windows.MouseButton.Left or Windows.MouseButton.Right))throw new ArgumentOutOfRangeException(nameof(button));Execute(new("click",[[point]]),button==Windows.MouseButton.Right?2000:40);}
    public void Drag(PixelPoint from,PixelPoint to,int steps)
    {
        var count=Math.Clamp(steps*2,4,24);
        // 地图定位需要停在建筑上；末段减速并停留，避免松手惯性把建筑甩出缩放中心。
        Execute(new("drag",Enumerable.Range(0,count+1).Select(i=>
        {
            var progress=1-Math.Pow(1-i/(double)count,3);
            return new[]{new PixelPoint(from.X+(int)Math.Round((to.X-from.X)*progress),from.Y+(int)Math.Round((to.Y-from.Y)*progress))};
        }).ToArray(),ReleaseDelayMilliseconds:160));
    }
    public void Scroll(PixelPoint point,PixelBox surface,int steps,bool zoom)
    {
        if(!surface.Contains(point)||surface.Width<8||surface.Height<8)throw new InputBlockedException("触控滚动区域过小或目标越界");
        if(zoom)
        {
            var radius=Math.Min(surface.Width,surface.Height)/3;if(radius<12)throw new InputBlockedException("双指缩放区域过小");
            var center=surface.Center;var small=Math.Max(4,radius/3);var direction=Math.Sign(steps);
            for(var n=0;n<(Math.Abs(steps)+3)/4;n++)
                Execute(new("pinch",Enumerable.Range(0,7).Select(i=>{var span=direction>0?small+(radius-small)*i/6:radius-(radius-small)*i/6;return new[]{new PixelPoint(center.X-span,center.Y),new PixelPoint(center.X+span,center.Y)};}).ToArray()));
        }
        else
        {
            var center=surface.Center;
            for(var remaining=Math.Abs(steps);remaining>0;remaining-=3)
            {var distance=Math.Min(surface.Height*3/5,Math.Max(12,Math.Min(remaining,3)*40));var half=distance/2;
            // Wheel up reveals earlier rows: finger moves down. Wheel down moves the finger up.
            // A fast swipe flings Unity lists past the requested rows. Ease out
            // and keep the contact still before releasing to stop inertia.
            var from=new PixelPoint(center.X,center.Y-Math.Sign(steps)*half);
            var to=new PixelPoint(center.X,center.Y+Math.Sign(steps)*half);
            var frames=Enumerable.Range(0,25).Select(i=>{
                var progress=1-Math.Pow(1-i/24d,3);
                return new[]{new PixelPoint(from.X,from.Y+(int)Math.Round((to.Y-from.Y)*progress))};
            }).ToArray();
            Execute(new("drag",frames,ReleaseDelayMilliseconds:160));
            }
        }
    }
    public void Dispose(){if(disposed)return;disposed=true;runner.Dispose();}
}

internal interface ITouchGestureRunner:IDisposable
{
    bool? PhysicalPointerDown=>null;
    bool SupportsPreparedReview=>false;
    void SetBeforeDelivery(Action check){}
    void Execute(TouchGesturePlan screen,nint target,int holdMilliseconds,Action authorize,Action authorizeRelease,Func<NativeWindowState> verifyIdentity);
    void Release(Func<NativeWindowState> verifyIdentity);
}

internal sealed class ForwardedTouchGestureRunner(MabiAgent.Core.RuntimeDiagnostics? diagnostics=null):ITouchGestureRunner
{
    private TouchMouseMonitor? activeMonitor;private Action? beforeDelivery;
    private string gestureId="",stage="start";private int forwardAttempts;
    private const int MaxPendingTraces=256;
    private readonly List<(string GestureId,string Stage,System.Text.Json.Nodes.JsonObject Details,DateTimeOffset At)> pendingTraces=[];
    internal event Action<TouchDiagnosticWriteFailure>? DiagnosticWriteFailed;
    public bool SupportsPreparedReview=>true;
    public void SetBeforeDelivery(Action check)=>beforeDelivery=check;
    public bool? PhysicalPointerDown=>activeMonitor is{} monitor?monitor.PhysicalDowns>0:null;
    private List<TouchEnvelope>? messages;
    private int releaseIndex;
    private nint target;
    private bool down;
    public void Execute(TouchGesturePlan screen,nint target,int holdMilliseconds,Action authorize,Action authorizeRelease,Func<NativeWindowState> verifyIdentity)
    {
        if(messages is not null)throw new InputBlockedException("前次触控尚未释放");this.target=target;
        var desktop=PhysicalDesktop.Bounds;if(screen.Points.SelectMany(frame=>frame).Any(point=>!desktop.Contains(point)))throw new InputBlockedException("目标触点位于屏幕之外");
        gestureId=Guid.NewGuid().ToString("N");forwardAttempts=0;releaseIndex=0;stage="prepare";
        var initial=verifyIdentity();
        Trace("started",new(){["kind"]=screen.Kind,["target"]=(long)target,["process_id"]=initial.ProcessId,["geometry"]=new System.Text.Json.Nodes.JsonArray(initial.Geometry.X,initial.Geometry.Y,initial.Geometry.Width,initial.Geometry.Height,initial.Geometry.Dpi),["planned_messages"]=screen.MessageCount,["contacts"]=screen.ContactCount});FlushTraces();
        Exception? tracedFailure=null;
        try
        {
        using var monitor=new TouchMouseMonitor();
        var cursor=TouchCursorPresentation.Read();
        using var anchor=new TouchInputWindow(AnchorAwayFrom(screen.Bounds,cursor.Position),inputSurface:true);
        using var cover=new TouchInputWindow(screen.Bounds,legacyTouch:true,retainTouchHandles:true,inputSurface:true);
        cover.LimitContactRegion(screen.Points[0]);
        if(!SpinWait.SpinUntil(()=>TouchNative.At(anchor.Point)==anchor.Hwnd&&screen.Points[0].All(point=>TouchNative.At(point)==cover.Hwnd),150))
        {
            var message=$"触控辅助窗口未覆盖起始触点，未发送输入；锚定 {anchor.Hwnd}/{TouchNative.At(anchor.Point)}，覆盖 {cover.Hwnd}/{TouchNative.At(screen.Points[0][0])}，前台 {TouchNative.Foreground}";
            throw new MabiAgent.Workflows.InputRejectedBeforeDispatchException(message);
        }
        Trace("prepared",new(){["anchor"]=(long)anchor.Hwnd,["cover"]=(long)cover.Hwnd,["foreground"]=(long)TouchNative.Foreground});FlushTraces();
        using var device=new SyntheticTouchDevice(anchor.Point,anchor.Hwnd);
        System.Text.Json.Nodes.JsonObject? restoreTrace=null;
        void RestoreCursor()
        {
            // Clear suppression caused by this touch even if the foreground app
            // had hidden its cursor. The current-pixel notification never changes
            // that app's ShowCursor count or forces a cursor image.
            if(!TouchCursorPresentation.Read().Suppressed)return;
            var activity=monitor.PhysicalMoves+monitor.PhysicalDowns+monitor.PhysicalUps;var wait=Stopwatch.StartNew();var idle=Stopwatch.StartNew();
            while(TouchCursorPresentation.Read().Suppressed&&wait.ElapsedMilliseconds<180)
            {
                var next=monitor.PhysicalMoves+monitor.PhysicalDowns+monitor.PhysicalUps;
                if(next!=activity){activity=next;idle.Restart();}
                if(idle.ElapsedMilliseconds>=40&&!new PhysicalInputReader().Read().ButtonsDown)
                {
                    try{restoreTrace=TouchCursorPresentation.Restore(true,()=>{verifyIdentity();authorizeRelease();if(monitor.PhysicalMoves+monitor.PhysicalDowns+monitor.PhysicalUps!=activity||new PhysicalInputReader().Read().ButtonsDown)throw new InputBlockedException("恢复光标前检测到用户活动");},"absolute_same_pixel");}
                    catch(InputBlockedException)when(monitor.PhysicalMoves+monitor.PhysicalDowns+monitor.PhysicalUps!=activity||new PhysicalInputReader().Read().ButtonsDown){return;}
                    return;
                }
                Thread.Sleep(2);
            }
        }
        try
        {
            activeMonitor=monitor;stage="generate";
            authorize();screen.Generate(device,cover.Hwnd,authorize);
            stage="collect";
            try{messages=screen.Collect(cover);}
            // Collection only reads our helper windows. No envelope has been
            // forwarded to the game yet, so a missing local frame is unsent.
            catch(InputBlockedException error){throw new MabiAgent.Workflows.InputRejectedBeforeDispatchException(error.Message+"，覆盖窗消息 "+new System.Text.Json.Nodes.JsonArray(cover.Received.TakeLast(16).Select(row=>(System.Text.Json.Nodes.JsonNode)row.DeepClone()).ToArray()).ToJsonString()+"，锚定窗消息 "+new System.Text.Json.Nodes.JsonArray(anchor.Received.Take(10).Select(row=>(System.Text.Json.Nodes.JsonNode)row.DeepClone()).ToArray()).ToJsonString());}
            Trace("collected",new(){["messages"]=messages.Count,["expected_max"]=screen.MessageCount});
            releaseIndex=messages.Count-screen.ContactCount;stage="forward";
            // Restore cursor presentation after releasing local touch resources.
            for(var i=0;i<releaseIndex;i++)
            {authorize();verifyIdentity();if(i==0)beforeDelivery?.Invoke();Forward(messages[i]);down=true;if(i>=screen.ContactCount-1)Wait(i==0&&screen.Steps==0?holdMilliseconds:16,authorize);}
            stage="release";Wait(screen.ReleaseDelayMilliseconds,()=>{authorize();verifyIdentity();});authorizeRelease();Release(verifyIdentity);
        }
        catch(Exception error){TraceFailure(error,stage);tracedFailure=error;throw;}
        finally
        {
            stage="cleanup";
            try
            {
                try{if(down){authorizeRelease();Release(verifyIdentity);}}
                finally{try{device.Dispose();if(messages is not null){foreach(var message in messages)message.Dispose();messages=null;}cover.Dispose();anchor.Dispose();RestoreCursor();}finally{activeMonitor=null;}}
            }
            catch(Exception error){TraceFailure(error,"cleanup");tracedFailure=error;throw;}
            finally{Trace("resources",new(){["messages_open"]=messages is not null,["down"]=down,["forward_attempts"]=forwardAttempts,["cursor_restore"]=restoreTrace});}
        }
        }
        catch(Exception error)
        {
            if(!ReferenceEquals(tracedFailure,error))TraceFailure(error,stage);
            if(error is InputBlockedException&&forwardAttempts==0)
                throw new MabiAgent.Workflows.InputRejectedBeforeDispatchException(error.Message+"；本次触控未向游戏转交");
            throw;
        }
        finally
        {
            if(pendingTraces.All(row=>row.Stage!="resources"))Trace("resources",new(){["messages_open"]=messages is not null,["down"]=down,["forward_attempts"]=forwardAttempts});
            FlushTraces();
        }
    }
    private static void Wait(int milliseconds,Action check)
    {var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<milliseconds){check();Thread.Sleep(Math.Min(10,Math.Max(1,milliseconds-(int)timer.ElapsedMilliseconds)));}check();}
    private static PixelBox AnchorAwayFrom(PixelBox bounds,PixelPoint cursor)
    {
        var desktop=PhysicalDesktop.Bounds;
        foreach(var p in new[]{new PixelPoint(desktop.Left+40,desktop.Top+40),new PixelPoint(desktop.Right-40,desktop.Top+40),new PixelPoint(desktop.Left+40,desktop.Bottom-40),new PixelPoint(desktop.Right-40,desktop.Bottom-40)}.OrderByDescending(p=>Math.Pow(p.X-bounds.Center.X,2)+Math.Pow(p.Y-bounds.Center.Y,2)))
        {var box=new PixelBox(p.X-12,p.Y-12,p.X+12,p.Y+12);if(!bounds.Contains(p)&&!box.Contains(cursor))return box;}
        throw new InputBlockedException("没有可用的本地触控锚定区域");
    }
    // Keep frame traces in memory; never insert diagnostic file I/O between touch delivery and cleanup.
    private void Trace(string name,System.Text.Json.Nodes.JsonObject? details=null)
    {
        if(pendingTraces.Count>=MaxPendingTraces)pendingTraces.RemoveAt(0);
        pendingTraces.Add((gestureId,name,details is null?new():(System.Text.Json.Nodes.JsonObject)details.DeepClone(),DateTimeOffset.UtcNow));
    }
    private void FlushTraces()
    {
        var written=0;
        while(written<pendingTraces.Count)
        {
            var (id,name,details,at)=pendingTraces[written];
            if(TouchDiagnostics.Write(diagnostics,id,name,details,at)){written++;continue;}
            try{DiagnosticWriteFailed?.Invoke(new(id,name,diagnostics?.WriteError??"触控诊断写入失败"));}catch{/* Reporting diagnostics must not alter input. */}
            break;
        }
        if(written>0)pendingTraces.RemoveRange(0,written);
    }
    private void TraceFailure(Exception error,string failedStage)
    {
        var details=new System.Text.Json.Nodes.JsonObject{["failed_stage"]=failedStage,["exception"]=error.GetType().FullName,["message"]=error.Message,["forward_attempts"]=forwardAttempts,["release_index"]=releaseIndex,["down"]=down};
        Trace("failed",details);
    }
    private void Forward(TouchEnvelope message)
    {
        forwardAttempts++;message.Forward(target,row=>Trace("forwarded",row));
    }
    public void Release(Func<NativeWindowState> verifyIdentity)
    {
        if(!down||messages is null)return;verifyIdentity();
        for(var i=releaseIndex;i<messages.Count;i++){var message=messages[i];releaseIndex=i+1;Forward(message);}down=false;
        Trace("released",new(){["messages"]=messages.Count,["release_index"]=releaseIndex,["forward_attempts"]=forwardAttempts});
    }
    public void Dispose(){if(messages is not null){foreach(var message in messages)message.Dispose();messages=null;}}
}
