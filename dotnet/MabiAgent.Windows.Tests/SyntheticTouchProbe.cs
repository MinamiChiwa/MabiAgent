using System.Text.Json.Nodes;
using MabiAgent.Windows;
namespace MabiAgent.Windows.Tests;

internal sealed class GuardedTouchProbeOps(NativeWindowState expected,TouchInputWindow anchor,TouchInputWindow cover,bool forwardTouches=false,TouchInputWindow? mousePad=null,TouchGesturePlan? gesture=null):INativeWindowsOps,ICurrentClientFrameSource,IGuardedWindowTransport,IGuardedClickLifetime,IDisposable
{
    private readonly NativeWindowsOps passive=new();private ForegroundInputGuard? guard;private PixelPoint point;private PhysicalInputSnapshot initial;
    private TouchCursorState cursorBefore;
    internal SyntheticTouchDevice? Probe{get;private set;}public bool RequiresForeground=>false;public int MoveSampleMilliseconds=>20;public int ClickSampleMilliseconds=>70;
    internal JsonArray Forwarded{get;}=new();private bool forwardedDown;
    internal JsonArray CursorTrace{get;}=new();
    private List<TouchEnvelope>? gestureMessages;private int gestureStep;private PixelPoint gestureStart;
    private int forwardingMoves,forwardingDowns,forwardingUps,forwardingDragMoves;
    internal JsonArray PhysicalIntervals{get;}=new();internal TouchMouseMonitor? MouseMonitor{get;set;}
    public void BindGuard(ForegroundInputGuard g)=>guard=g;
    public NativeWindowState ReadWindow(nint hwnd){if(Probe is not null&&(!Probe.Released||forwardedDown))CheckUser();return passive.ReadWindow(hwnd);}
    public RgbImage CaptureClient(NativeWindowState state)=>passive.CaptureClient(state);
    public CurrentClientFrame CaptureCurrentClientFrame(NativeWindowState state)=>passive.CaptureCurrentClientFrame(state);
    public bool AnyPhysicalKeyDown()=>passive.AnyPhysicalKeyDown();
    private ForegroundInputGuard Guard=>guard??throw new InputBlockedException("触控必须经共享守卫");
    private void CheckUser()
    {
        var now=new PhysicalInputReader().Read();var mouseInvalid=mousePad is null?now.Position!=initial.Position||now.ButtonsDown:TouchNative.At(now.Position)!=mousePad.Hwnd;
        if(mouseInvalid||now.Foreground!=initial.Foreground||now.KeyboardDown||now.Clip!=initial.Clip)throw new InputBlockedException("触控期间检测到授权区域外用户活动或前台变化");
    }
    public IDisposable? BeginClick(nint hwnd,PixelPoint p)
    {
        Guard.AuthorizeNativeTransport();var state=passive.ReadWindow(hwnd);if(hwnd!=expected.WindowId||state.ForegroundId==hwnd)throw new InputBlockedException("触控诊断要求游戏保持后台");
        point=new(checked(state.Geometry.X+p.X),checked(state.Geometry.Y+p.Y));if(TouchNative.At(point)!=cover.Hwnd||TouchNative.At(anchor.Point)!=anchor.Hwnd)throw new InputBlockedException("目标遮挡或锚定窗口位置已变化");
        initial=new PhysicalInputReader().Read();cursorBefore=TouchCursorPresentation.Read();CursorTrace.Add(new JsonObject{["phase"]="before",["cursor"]=cursorBefore.ToJson()});if((mousePad is null&&initial.ButtonsDown)||initial.KeyboardDown)throw new InputBlockedException("触控诊断需暂时无物理按键");
        Thread.Sleep(100);CheckUser();Guard.AuthorizeNativeTransport();Probe=new(anchor.Point,anchor.Hwnd);
        try{Probe.Begin();CheckUser();return Probe;}catch{Probe.Dispose();throw;}
    }
    public void MoveClient(nint hwnd,PixelPoint p)
    {
        Guard.AuthorizeNativeTransport();
        if(gesture is not null)
        {
            if(Probe is null){gestureStart=p;_=BeginClick(hwnd,p);return;}
            if(!forwardedDown||gestureMessages is null)throw new InputBlockedException("手势尚未按下");
            gestureStep++;if(gestureStep>gesture.Steps)throw new InputBlockedException("手势步数越界");
            var expectedPoint=gesture.Kind=="drag"?gesture.Points[gestureStep][0]:gestureStart;
            if(p!=expectedPoint)throw new InputBlockedException("共享拖动与触点轨迹不符");
            ForwardGesture(gesture.ContactCount+gestureStep-1,false);return;
        }
        if(Probe is null||Probe.Released)throw new InputBlockedException("未开始触控诊断");
    }
    public void MouseButton(MouseButton button,bool down)
    {
        if(button!=Windows.MouseButton.Left)throw new InputBlockedException("触控诊断仅允许一次轻点");
        if(gesture is not null)
        {
            if(down)
            {
                Guard.AuthorizeNativeTransport();var physicalBefore=MouseMonitor?.PhysicalMoves??0;var downBefore=MouseMonitor?.PhysicalDowns??0;
                gesture.Screen(expected.Geometry).Generate(Probe!,expected.WindowId,()=>{CheckUser();Guard.AuthorizeNativeTransport();},begin:false);
                PhysicalIntervals.Add(new JsonObject{["phase"]="local_generation",["physical_moves"]=(MouseMonitor?.PhysicalMoves??0)-physicalBefore,["physical_downs"]=(MouseMonitor?.PhysicalDowns??0)-downBefore});
                gestureMessages=gesture.Screen(expected.Geometry).Collect(cover);PrepareCursor();
                for(var i=0;i<gesture.ContactCount;i++)ForwardGesture(i,false);
            }
            else
            {
                Guard.AuthorizeMouseRelease(button);Probe?.Dispose();
                if(forwardedDown&&gestureMessages is not null)
                {for(var i=gestureMessages.Count-gesture.ContactCount;i<gestureMessages.Count;i++)ForwardGesture(i,true);forwardedDown=false;}
            }
            return;
        }
        if(down)
        {
            Guard.AuthorizeNativeTransport();Probe!.Down(point,expected.WindowId);
            if(forwardTouches)
            {
                // Finish the local gesture first: legacy touch translation can wait for the anchor release.
                // Only the retained system handles are forwarded; the physical gesture hits our own windows.
                Thread.Sleep(70);Probe.Up();Probe.Dispose();PrepareCursor();
                CheckUser();Forward(true,button);
            }
        }
        else{Guard.AuthorizeMouseRelease(button);Probe?.Up();if(forwardTouches&&forwardedDown)Forward(false,button);}
    }
    private void PrepareCursor()
    {
        CheckUser();CursorTrace.Add(new JsonObject{["phase"]="local_released",["cursor"]=TouchCursorPresentation.Read().ToJson()});
        if(mousePad is not null)
        {
            var timer=System.Diagnostics.Stopwatch.StartNew();while(!TouchCursorPresentation.Read().Showing&&timer.ElapsedMilliseconds<100){CheckUser();Thread.Sleep(2);}
            CursorTrace.Add(new JsonObject{["phase"]="physical_mouse_restore",["cursor"]=TouchCursorPresentation.Read().ToJson(),["absolute_position_written"]=false});
            if(!TouchCursorPresentation.Read().Showing)throw new InputBlockedException("并发诊断等待真实鼠标恢复显示超时，未转交游戏触控");
        }
        else if(Environment.GetEnvironmentVariable("MABI_TOUCH_CURSOR_RESTORE")=="absolute_same_pixel")
        {
            var restored=TouchCursorPresentation.Restore(cursorBefore.Showing||cursorBefore.Suppressed,()=>{CheckUser();Guard.AuthorizeNativeTransport();CheckUser();});CursorTrace.Add(new JsonObject{["phase"]="restore",["result"]=restored});
            if(TouchCursorPresentation.Read().Suppressed)throw new InputBlockedException("触控后光标抑制仍未解除，停止转交");
        }
    }
    private void ForwardGesture(int index,bool release)
    {
        if(release)Guard.AuthorizeMouseRelease(Windows.MouseButton.Left);else{CheckUser();Guard.AuthorizeNativeTransport();}
        var actual=passive.ReadWindow(expected.WindowId);
        if(actual.ProcessId!=expected.ProcessId||actual.ProcessStartUtcTicks!=expected.ProcessStartUtcTicks||actual.Geometry!=expected.Geometry)throw new InputOutcomeUnknownException("手势转交前目标身份或几何变化");
        if(index==0){forwardingMoves=MouseMonitor?.PhysicalMoves??0;forwardingDowns=MouseMonitor?.PhysicalDowns??0;forwardingUps=MouseMonitor?.PhysicalUps??0;forwardingDragMoves=MouseMonitor?.PhysicalDragMoves??0;}
        var trace=gestureMessages![index].Forward(expected.WindowId);trace["tick"]=System.Diagnostics.Stopwatch.GetTimestamp();Forwarded.Add(trace);if(!release)forwardedDown=true;
        if(index==gestureMessages.Count-1)PhysicalIntervals.Add(new JsonObject{["phase"]="game_forwarding",["physical_moves"]=(MouseMonitor?.PhysicalMoves??0)-forwardingMoves,["physical_downs"]=(MouseMonitor?.PhysicalDowns??0)-forwardingDowns,["physical_ups"]=(MouseMonitor?.PhysicalUps??0)-forwardingUps,["physical_drag_moves"]=(MouseMonitor?.PhysicalDragMoves??0)-forwardingDragMoves});
    }
    private void Forward(bool down,MouseButton button)
    {
        var began=System.Diagnostics.Stopwatch.StartNew();
        do
        {
            while(cover.Touches.TryDequeue(out var envelope))using(envelope)
            {
                var contacts=envelope.Decoded["contacts"]!.AsArray();var flag=down?2u:4u;
                if(!contacts.Any(c=>(c!["flags"]!.GetValue<uint>()&flag)!=0))continue;
                if(contacts.Count!=1||contacts[0]!["screen_hundredths"]![0]!.GetValue<int>()!=point.X*100||contacts[0]!["screen_hundredths"]![1]!.GetValue<int>()!=point.Y*100)throw new InputBlockedException("触控转交来源或坐标不符");
                if(down){CheckUser();Guard.AuthorizeNativeTransport();}else Guard.AuthorizeMouseRelease(button);
                var actual=passive.ReadWindow(expected.WindowId);if(actual.ProcessId!=expected.ProcessId||actual.ProcessStartUtcTicks!=expected.ProcessStartUtcTicks||actual.Geometry!=expected.Geometry)throw new InputOutcomeUnknownException("转交触控前目标身份或几何变化");
                Forwarded.Add(envelope.Forward(expected.WindowId));if(down)forwardedDown=true;return;
            }
            Thread.Sleep(5);
        }while(began.ElapsedMilliseconds<250);
        throw new InputOutcomeUnknownException("等待有效触控消息超时，禁止重放");
    }
    public void Wheel(int delta)=>throw new InputBlockedException("本次不验证滚轮");
    public void Key(ushort key,bool down,bool unicode=false)=>throw new InputBlockedException("本次不验证按键或文字");
    public void Dispose(){try{Probe?.Dispose();}finally{if(gestureMessages is not null)foreach(var envelope in gestureMessages)envelope.Dispose();}}
}
