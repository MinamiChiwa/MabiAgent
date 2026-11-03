using System.Diagnostics;
namespace MabiAgent.Windows;

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
    public TouchInputOps(NativeWindowState target):this(target,new NativeWindowsOps(),new WindowMessageInputOps(target,notifyActivation:true),new NativeTouchGestureRunner()){}
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
        ObjectDisposedException.ThrowIf(disposed,this);Guard.AuthorizeNativeTransport();if(keyboard is WindowMessageInputOps messages)messages.PreparePointerInput();var before=VerifyIdentity();
        foreach(var point in client.Points.SelectMany(p=>p))if(!new PixelBox(0,0,before.Geometry.Width,before.Geometry.Height).Contains(point))throw new InputBlockedException("触控轨迹超出客户区");
        void Check(){Guard.AuthorizeNativeTransport();if(VerifyIdentity().Geometry!=before.Geometry)throw new InputBlockedException("触控期间窗口位置、尺寸或DPI变化");}
        runner.Execute(client.Screen(before.Geometry),target.WindowId,holdMilliseconds,Check,()=>Guard.AuthorizeMouseRelease(holdMilliseconds==2000?Windows.MouseButton.Right:Windows.MouseButton.Left),VerifyIdentity);
    }
    public void Click(PixelPoint point,MouseButton button)
    {if(button is not(Windows.MouseButton.Left or Windows.MouseButton.Right))throw new ArgumentOutOfRangeException(nameof(button));Execute(new("click",[[point]]),button==Windows.MouseButton.Right?2000:40);}
    public void Drag(PixelPoint from,PixelPoint to,int steps)
    {var count=Math.Clamp(steps,2,12);Execute(new("drag",Enumerable.Range(0,count+1).Select(i=>new[]{new PixelPoint(from.X+(to.X-from.X)*i/count,from.Y+(to.Y-from.Y)*i/count)}).ToArray()));}
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

internal sealed class NativeTouchGestureRunner(bool nativeRouting=false):ITouchGestureRunner
{
    private TouchMouseMonitor? activeMonitor;private Action? beforeDelivery;
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
        if(!nativeRouting&&verifyIdentity().ForegroundId==target)
        {ExecuteForeground(screen,target,holdMilliseconds,authorize,authorizeRelease,verifyIdentity);return;}
        if(nativeRouting)
        {
            var foreground=verifyIdentity().ForegroundId;
            void Check(){authorize();if(foreground!=target&&verifyIdentity().ForegroundId==target)throw new InputBlockedException("直接触控激活了游戏窗口");}
            if(screen.Points[0].Any(p=>TouchNative.At(p)!=target))throw new InputBlockedException("直接触控目标被遮挡");
            using var directMonitor=new TouchMouseMonitor();var directCursor=TouchCursorPresentation.Read();
            using var directAnchor=new TouchInputWindow(AnchorAwayFrom(screen.Bounds,directCursor.Position),inputSurface:true);
            using var directDevice=new SyntheticTouchDevice(directAnchor.Point,directAnchor.Hwnd);
            try
            {
                activeMonitor=directMonitor;Check();if(TouchNative.At(directAnchor.Point)!=directAnchor.Hwnd)throw new InputBlockedException("触控锚定窗口未就绪");directDevice.Begin();Check();if(screen.Points[0].Any(p=>TouchNative.At(p)!=target))throw new InputBlockedException("发送触控前目标被遮挡");beforeDelivery?.Invoke();directDevice.Down(screen.Points[0],target);
                if(screen.Steps==0)Hold(holdMilliseconds,()=>directDevice.Move(screen.Points[0]),Check);
                else foreach(var frame in screen.Points.Skip(1)){Wait(20,Check);directDevice.Move(frame);}
                Wait(screen.ReleaseDelayMilliseconds,Check);authorizeRelease();verifyIdentity();directDevice.Up();
            }
            finally
            {
                try{authorizeRelease();verifyIdentity();directDevice.Dispose();}finally{activeMonitor=null;}
                if(TouchCursorPresentation.Read().Suppressed&&directMonitor.PhysicalMoves+directMonitor.PhysicalDowns+directMonitor.PhysicalUps==0)
                    TouchCursorPresentation.Restore(true,()=>{verifyIdentity();authorizeRelease();},"absolute_same_pixel");
            }
            return;
        }
        using var monitor=new TouchMouseMonitor();
        var cursor=TouchCursorPresentation.Read();
        using var anchor=new TouchInputWindow(AnchorAwayFrom(screen.Bounds,cursor.Position),inputSurface:true);
        using var cover=new TouchInputWindow(screen.Bounds,legacyTouch:true,retainTouchHandles:true,inputSurface:true);
        cover.LimitContactRegion(screen.Points[0]);
        if(!SpinWait.SpinUntil(()=>TouchNative.At(anchor.Point)==anchor.Hwnd&&screen.Points[0].All(point=>TouchNative.At(point)==cover.Hwnd),150))throw new MabiAgent.Workflows.InputRejectedBeforeDispatchException($"触控辅助窗口未覆盖起始触点，未发送输入；锚定 {anchor.Hwnd}/{TouchNative.At(anchor.Point)}，覆盖 {cover.Hwnd}/{TouchNative.At(screen.Points[0][0])}，前台 {TouchNative.Foreground}");
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
            activeMonitor=monitor;
            authorize();screen.Generate(device,target,authorize);
            try{messages=screen.Collect(cover);}
            catch(InputBlockedException error){throw new InputBlockedException(error.Message+"，锚定窗消息 "+new System.Text.Json.Nodes.JsonArray(anchor.Received.Take(10).Select(row=>(System.Text.Json.Nodes.JsonNode)row.DeepClone()).ToArray()).ToJsonString());}
            releaseIndex=messages.Count-screen.ContactCount;
            // Restore cursor presentation after releasing local touch resources.
            beforeDelivery?.Invoke();
            for(var i=0;i<releaseIndex;i++)
            {authorize();verifyIdentity();messages[i].Forward(target);down=true;if(i>=screen.ContactCount-1)Wait(i==0&&screen.Steps==0?holdMilliseconds:16,authorize);}
            Wait(screen.ReleaseDelayMilliseconds,()=>{authorize();verifyIdentity();});authorizeRelease();Release(verifyIdentity);
        }
        finally
        {
            try{if(down){authorizeRelease();Release(verifyIdentity);}}
            finally{try{device.Dispose();if(messages is not null){foreach(var message in messages)message.Dispose();messages=null;}cover.Dispose();anchor.Dispose();RestoreCursor();}finally{activeMonitor=null;}}
        }
    }
    private void ExecuteForeground(TouchGesturePlan screen,nint target,int holdMilliseconds,Action authorize,Action authorizeRelease,Func<NativeWindowState> verifyIdentity)
    {
        // Unity uses native pointer input while foreground. Select this route before
        // issuing input; never switch routes to replay an unconfirmed gesture.
        void Check(){authorize();if(verifyIdentity().ForegroundId!=target)throw new InputBlockedException("前台触控期间窗口失焦");}
        Check();if(screen.Points[0].Any(p=>TouchNative.At(p)!=target))throw new InputBlockedException("前台触控目标被其他窗口遮挡");
        using var monitor=new TouchMouseMonitor();var cursor=TouchCursorPresentation.Read();
        using var device=new SyntheticTouchDevice(default,0,anchored:false);
        try
        {
            activeMonitor=monitor;
            Check();beforeDelivery?.Invoke();device.Down(screen.Points[0],target);
            if(screen.Steps==0)Hold(holdMilliseconds,()=>device.Move(screen.Points[0]),Check);
            else foreach(var frame in screen.Points.Skip(1)){Wait(20,Check);device.Move(frame);}
            Wait(screen.ReleaseDelayMilliseconds,Check);authorizeRelease();verifyIdentity();device.Up();
        }
        finally
        {
            activeMonitor=null;
            device.Dispose();
            var activity=monitor.PhysicalMoves+monitor.PhysicalDowns+monitor.PhysicalUps;
            if(TouchCursorPresentation.Read().Suppressed&&!new PhysicalInputReader().Read().ButtonsDown)
                TouchCursorPresentation.Restore(true,()=>{verifyIdentity();authorizeRelease();if(monitor.PhysicalMoves+monitor.PhysicalDowns+monitor.PhysicalUps!=activity||new PhysicalInputReader().Read().ButtonsDown)throw new InputBlockedException("光标呈现前检测到用户活动");},"absolute_same_pixel");
        }
    }
    private static void Wait(int milliseconds,Action check)
    {var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<milliseconds){check();Thread.Sleep(Math.Min(10,Math.Max(1,milliseconds-(int)timer.ElapsedMilliseconds)));}check();}
    private static void Hold(int milliseconds,Action refresh,Action check)
    {var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<milliseconds){Wait(Math.Min(40,Math.Max(1,milliseconds-(int)timer.ElapsedMilliseconds)),check);check();refresh();}}
    private static PixelBox AnchorAwayFrom(PixelBox bounds,PixelPoint cursor)
    {
        var desktop=PhysicalDesktop.Bounds;
        foreach(var p in new[]{new PixelPoint(desktop.Left+40,desktop.Top+40),new PixelPoint(desktop.Right-40,desktop.Top+40),new PixelPoint(desktop.Left+40,desktop.Bottom-40),new PixelPoint(desktop.Right-40,desktop.Bottom-40)}.OrderByDescending(p=>Math.Pow(p.X-bounds.Center.X,2)+Math.Pow(p.Y-bounds.Center.Y,2)))
        {var box=new PixelBox(p.X-12,p.Y-12,p.X+12,p.Y+12);if(!bounds.Contains(p)&&!box.Contains(cursor))return box;}
        throw new InputBlockedException("没有可用的本地触控锚定区域");
    }
    public void Release(Func<NativeWindowState> verifyIdentity)
    {
        if(!down||messages is null)return;verifyIdentity();
        for(var i=releaseIndex;i<messages.Count;i++){var message=messages[i];releaseIndex=i+1;message.Forward(target);}down=false;
    }
    public void Dispose(){if(messages is not null){foreach(var message in messages)message.Dispose();messages=null;}}
}
