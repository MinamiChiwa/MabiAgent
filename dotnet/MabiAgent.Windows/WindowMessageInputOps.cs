using System.Runtime.InteropServices;
namespace MabiAgent.Windows;

/// <summary>Shared background keyboard transport. Pointer actions use the shared touch transport.</summary>
public sealed class WindowMessageInputOps(NativeWindowState target,bool notifyActivation=false,Action<WindowMessageTrace>? trace=null):INativeWindowsOps,ICurrentClientFrameSource,IGuardedWindowTransport,IKeyboardPointerPreparation,IDisposable
{
    private readonly NativeWindowsOps passive=new();
    private ForegroundInputGuard? guard;
    private bool disposed;
    public bool RequiresForeground=>false;
    public void BindGuard(ForegroundInputGuard owner)
    {ThrowDisposed();if(guard is not null&&!ReferenceEquals(guard,owner))throw new InputBlockedException("消息通道已绑定其他守卫");guard=owner;}
    private ForegroundInputGuard Guard=>guard??throw new InputBlockedException("消息输入必须经共享守卫");
    public NativeWindowState ReadWindow(nint hwnd){ThrowDisposed();return passive.ReadWindow(hwnd);}
    public RgbImage CaptureClient(NativeWindowState state){ThrowDisposed();return passive.CaptureClient(state);}
    public CurrentClientFrame CaptureCurrentClientFrame(NativeWindowState state){ThrowDisposed();return passive.CaptureCurrentClientFrame(state);}
    public bool AnyPhysicalKeyDown(){ThrowDisposed();return passive.AnyPhysicalKeyDown();}
    private NativeWindowState VerifyTarget()
    {
        var current=passive.ReadWindow(target.WindowId);
        if(current.WindowId!=target.WindowId||current.ProcessId!=target.ProcessId||current.ProcessStartUtcTicks!=target.ProcessStartUtcTicks||!string.Equals(current.Executable,target.Executable,StringComparison.OrdinalIgnoreCase)||current.ClassName!=target.ClassName)
            throw new InputOutcomeUnknownException("消息目标身份已变化，停止输入及释放");
        return current;
    }
    public void MoveClient(nint hwnd,PixelPoint point){ThrowDisposed();throw new InputBlockedException("窗口消息通道仅支持键盘；指针操作须使用共享触控");}
    public void MouseButton(MouseButton button,bool down)
    {
        ThrowDisposed();
        if(down)throw new InputBlockedException("窗口消息通道不支持鼠标点击");
        Guard.AuthorizeMouseRelease(button);
    }
    public void Wheel(int delta){ThrowDisposed();throw new InputBlockedException("窗口消息通道不支持滚轮");}
    public void Key(ushort key,bool down,bool unicode=false)
    {
        ThrowDisposed();
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
        ThrowDisposed();
        // Allow the game to consume queued keys before restoring the actual
        // background state at the next guarded pointer action.
        // A previous session or process may have sent the keyboard activation
        // hint. Reconcile against the actual foreground before every pointer
        // gesture instead of trusting this instance's bookkeeping.
        Guard.AuthorizeNativeTransport();var current=VerifyTarget();
        if(current.ForegroundId!=target.WindowId)Post(0x0006,0,current.ForegroundId);
    }
    void IKeyboardPointerPreparation.PreparePointerInput()=>PreparePointerInput();
    public void Dispose(){if(disposed)return;disposed=true;passive.Dispose();}
    private void ThrowDisposed()=>ObjectDisposedException.ThrowIf(disposed,this);
    private void NotifyActivation()
    {
        if(!notifyActivation)return;
        Post(0x0006,1,0);Thread.Sleep(10);Guard.AuthorizeNativeTransport();
    }
    private void Post(uint message,nuint value,nint flags)
    {
        var before=trace is null?null:passive.ReadWindow(target.WindowId);var began=System.Diagnostics.Stopwatch.GetTimestamp();
        var sent=PostMessage(target.WindowId,message,value,flags);
        var error=sent?0:Marshal.GetLastWin32Error();
        if(trace is not null&&before is not null)
        {
            // Diagnostics are optional and never authorize or retry an input.
            try{var after=passive.ReadWindow(target.WindowId);trace(new(DateTimeOffset.UtcNow,target.WindowId,message,(ulong)value,(long)flags,sent,error,System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds,before.Geometry,before.Cursor,after.Cursor,before.ForegroundId,after.ForegroundId));}catch{ }
        }
        if(!sent)throw new InputOutcomeUnknownException("窗口消息投递未确认，禁止重放："+error);
    }
    public static nint EncodeKey(uint scan,bool up)
    {uint bits=1|((scan&0xff)<<16);if((scan&0xff00)==0xe000)bits|=1u<<24;if(up)bits|=(1u<<30)|(1u<<31);return unchecked((nint)(int)bits);}
    [DllImport("user32.dll",EntryPoint="PostMessageW",ExactSpelling=true,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint hwnd,uint message,nuint value,nint flags);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll")]private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")]private static extern uint MapVirtualKeyEx(uint code,uint type,nint layout);
}
public sealed record WindowMessageTrace(DateTimeOffset At,nint Hwnd,uint Message,ulong WParam,long LParam,bool Submitted,int Error,double ElapsedMilliseconds,ClientGeometry Geometry,PixelPoint CursorBefore,PixelPoint CursorAfter,nint ForegroundBefore,nint ForegroundAfter);
