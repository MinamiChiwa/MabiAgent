using System.Runtime.InteropServices;
namespace MabiAgent.Windows;

/// <summary>Shared background keyboard transport. Mouse messages are available only to explicit diagnostic probes.</summary>
public sealed class WindowMessageInputOps(NativeWindowState target,bool notifyActivation=false,bool synchronous=false,Action<WindowMessageTrace>? trace=null,bool notifyApplication=false,bool allowMouseProbe=false,bool noDelayMouseProbe=false):INativeWindowsOps,ICurrentClientFrameSource,IGuardedWindowTransport
{
    private readonly NativeWindowsOps passive=new();
    private ForegroundInputGuard? guard;
    private PixelPoint position;
    private bool positioned;
    private readonly HashSet<MouseButton> heldButtons=[];
    public bool RequiresForeground=>false;
    public bool AllowsZeroClickDelay=>allowMouseProbe&&noDelayMouseProbe;
    public int MoveSampleMilliseconds=>AllowsZeroClickDelay?0:50;
    public int ClickSampleMilliseconds=>AllowsZeroClickDelay?0:50;
    public void BindGuard(ForegroundInputGuard owner)
    {if(guard is not null&&!ReferenceEquals(guard,owner))throw new InputBlockedException("消息通道已绑定其他守卫");guard=owner;}
    private ForegroundInputGuard Guard=>guard??throw new InputBlockedException("消息输入必须经共享守卫");
    public NativeWindowState ReadWindow(nint hwnd)=>passive.ReadWindow(hwnd);
    public RgbImage CaptureClient(NativeWindowState state)=>passive.CaptureClient(state);
    public CurrentClientFrame CaptureCurrentClientFrame(NativeWindowState state)=>passive.CaptureCurrentClientFrame(state);
    public bool AnyPhysicalKeyDown()=>passive.AnyPhysicalKeyDown();
    private NativeWindowState VerifyTarget()
    {
        var current=passive.ReadWindow(target.WindowId);
        if(current.WindowId!=target.WindowId||current.ProcessId!=target.ProcessId||current.ProcessStartUtcTicks!=target.ProcessStartUtcTicks||!string.Equals(current.Executable,target.Executable,StringComparison.OrdinalIgnoreCase)||current.ClassName!=target.ClassName)
            throw new InputOutcomeUnknownException("消息目标身份已变化，停止输入及释放");
        return current;
    }
    public void MoveClient(nint hwnd,PixelPoint point)=>MoveMessage(hwnd,point,ButtonFlags());
    public void MoveForClick(nint hwnd,PixelPoint point,MouseButton button)=>MoveMessage(hwnd,point,ButtonFlags()|MouseFlag(button));
    private void MoveMessage(nint hwnd,PixelPoint point,nuint flags)
    {
        Guard.AuthorizeNativeTransport();var state=VerifyTarget();
        RequireMouseProbe(state);
        if(hwnd!=target.WindowId||!new PixelBox(0,0,state.Geometry.Width,state.Geometry.Height).Contains(point))throw new InputBlockedException("消息坐标越出目标客户区");
        NotifyActivation();position=point;positioned=true;Post(0x0200,flags,EncodePoint(point));
    }
    public void MouseButton(MouseButton button,bool down)
    {
        if(down)Guard.AuthorizeNativeTransport();else Guard.AuthorizeMouseRelease(button);
        var state=VerifyTarget();if(down)RequireMouseProbe(state);
        if(!positioned)throw new InputBlockedException("消息点击缺少客户区位置");
        if(down)heldButtons.Add(button);else heldButtons.Remove(button);
        var message=button switch{Windows.MouseButton.Left=>down?0x0201u:0x0202u,Windows.MouseButton.Right=>down?0x0204u:0x0205u,_=>throw new ArgumentOutOfRangeException(nameof(button))};
        Post(message,ButtonFlags(),EncodePoint(position));
    }
    private nuint ButtonFlags()=>(heldButtons.Contains(Windows.MouseButton.Left)?1u:0u)|(heldButtons.Contains(Windows.MouseButton.Right)?2u:0u);
    private static nuint MouseFlag(MouseButton button)=>button switch{Windows.MouseButton.Left=>1u,Windows.MouseButton.Right=>2u,_=>throw new ArgumentOutOfRangeException(nameof(button))};
    public void Wheel(int delta)
    {
        Guard.AuthorizeNativeTransport();var state=VerifyTarget();
        RequireMouseProbe(state);
        if(!positioned||delta==0||delta%120!=0||Math.Abs((long)delta)>1440)throw new InputBlockedException("消息滚轮参数无效");
        Post(0x020A,unchecked((nuint)((uint)(ushort)(short)delta<<16))|ButtonFlags(),EncodePoint(new(checked(state.Geometry.X+position.X),checked(state.Geometry.Y+position.Y))));
    }
    private void RequireMouseProbe(NativeWindowState state)
    {
        if(!allowMouseProbe)throw new InputBlockedException("窗口消息鼠标未通过坐标验收，仅允许显式诊断；快捷键和文字独立验证");
        if(noDelayMouseProbe&&new PixelBox(0,0,state.Geometry.Width,state.Geometry.Height).Contains(new(state.Cursor.X-state.Geometry.X,state.Cursor.Y-state.Geometry.Y)))throw new InputBlockedException("无等待点击诊断需真实鼠标停在游戏客户区外，避免已知错位误点");
    }
    public void Key(ushort key,bool down,bool unicode=false)
    {
        if(down)Guard.AuthorizeNativeTransport();else Guard.AuthorizeKeyRelease(key,unicode);
        VerifyTarget();if(down)NotifyActivation();
        if(unicode){if(down)Post(0x0102,key,1);return;}
        if(!InputKeyPolicy.IsSupported(key))throw new InputBlockedException("消息键未获支持");
        var thread=GetWindowThreadProcessId(target.WindowId,out var pid);if(thread==0||pid!=target.ProcessId)throw new InputOutcomeUnknownException("消息目标线程已变化");
        var scan=MapVirtualKeyEx(key,4,GetKeyboardLayout(thread));if(scan==0)throw new InputBlockedException("目标键盘布局不支持此键");
        Post(down?0x0100u:0x0101u,key,EncodeKey(scan,!down));
    }
    internal void PreparePointerInput()
    {
        // Allow the game to consume queued keys before restoring the actual
        // background state at the next guarded pointer action.
        // A previous session or process may have sent the keyboard activation
        // hint. Reconcile against the actual foreground before every pointer
        // gesture instead of trusting this instance's bookkeeping.
        Guard.AuthorizeNativeTransport();var current=VerifyTarget();
        if(current.ForegroundId!=target.WindowId)Post(0x0006,0,current.ForegroundId);
    }
    private void NotifyActivation()
    {
        if(!notifyActivation)return;
        if(notifyApplication){var current=VerifyTarget();var otherThread=GetWindowThreadProcessId(current.ForegroundId,out _);Post(0x001c,1,(nint)otherThread);Post(0x0086,1,0);}
        Post(0x0006,1,0);if(notifyApplication)Post(0x0007,0,0);Thread.Sleep(10);Guard.AuthorizeNativeTransport();
    }
    private void Post(uint message,nuint value,nint flags)
    {
        var before=trace is null?null:passive.ReadWindow(target.WindowId);var began=System.Diagnostics.Stopwatch.GetTimestamp();
        var sent=synchronous?SendMessageTimeout(target.WindowId,message,value,flags,3,250,out _)!=0:PostMessage(target.WindowId,message,value,flags);
        var error=sent?0:Marshal.GetLastWin32Error();
        if(trace is not null&&before is not null)
        {
            // Diagnostics are optional and never authorize or retry an input.
            try{var after=passive.ReadWindow(target.WindowId);trace(new(DateTimeOffset.UtcNow,target.WindowId,message,(ulong)value,(long)flags,sent,error,System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds,before.Geometry,before.Cursor,after.Cursor,before.ForegroundId,after.ForegroundId));}catch{ }
        }
        if(!sent)throw new InputOutcomeUnknownException("窗口消息投递未确认，禁止重放："+error);
    }
    public static nint EncodePoint(PixelPoint point)
    {if(point.X is <short.MinValue or >short.MaxValue||point.Y is <short.MinValue or >short.MaxValue)throw new ArgumentOutOfRangeException(nameof(point));return unchecked((nint)(int)((uint)(ushort)point.X|((uint)(ushort)point.Y<<16)));}
    public static nint EncodeKey(uint scan,bool up)
    {uint bits=1|((scan&0xff)<<16);if((scan&0xff00)==0xe000)bits|=1u<<24;if(up)bits|=(1u<<30)|(1u<<31);return unchecked((nint)(int)bits);}
    [DllImport("user32.dll",EntryPoint="PostMessageW",ExactSpelling=true,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint hwnd,uint message,nuint value,nint flags);
    [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW",ExactSpelling=true,SetLastError=true)]private static extern nint SendMessageTimeout(nint hwnd,uint message,nuint value,nint flags,uint options,uint timeout,out nuint result);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll")]private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")]private static extern uint MapVirtualKeyEx(uint code,uint type,nint layout);
}
public sealed record WindowMessageTrace(DateTimeOffset At,nint Hwnd,uint Message,ulong WParam,long LParam,bool Submitted,int Error,double ElapsedMilliseconds,ClientGeometry Geometry,PixelPoint CursorBefore,PixelPoint CursorAfter,nint ForegroundBefore,nint ForegroundAfter);
