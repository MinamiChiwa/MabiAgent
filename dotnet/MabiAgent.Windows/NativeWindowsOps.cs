using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace MabiAgent.Windows;

/// <summary>Foreground SendInput and client-area PrintWindow capture. No activation, process memory, injection or background input.</summary>
public sealed class NativeWindowsOps:INativeWindowsOps,ICurrentClientFrameSource,IDisposable
{
    private static long captureGeneration;
    private readonly long generation=Interlocked.Increment(ref captureGeneration);
    private long captureSequence;
    private readonly PrintWindowCaptureClient captureClient;
    private int disposed;
    private ForegroundInputGuard? guard;
    public NativeWindowsOps():this(null){}
    internal NativeWindowsOps(Func<string,int,long,ProcessStartInfo>? helperLauncher,TimeSpan? startupTimeout=null,TimeSpan? frameTimeout=null,Action<Process>? helperStarted=null,Action<Process>? helperReady=null)
    {captureClient=new(helperLauncher,startupTimeout,frameTimeout,helperStarted,helperReady);}
    internal void Bind(ForegroundInputGuard owner){if(guard is not null&&!ReferenceEquals(owner,guard))throw new InputBlockedException("原生输入适配器已绑定其他守卫");guard=owner;}
    private ForegroundInputGuard Guard=>guard??throw new InputBlockedException("原生输入必须经共享守卫");
    public NativeWindowState ReadWindow(nint hwnd)
    {
        ThrowDisposed();EnsureWindows();using var dpi=new DpiContext();
        if(hwnd==0||!IsWindow(hwnd))throw new InputBlockedException("窗口不存在");
        GetWindowThreadProcessId(hwnd,out var pid);var identity=ReadProcessIdentity(pid);
        var cls=new StringBuilder(256);if(GetClassName(hwnd,cls,cls.Capacity)==0)throw Error();
        if(!GetClientRect(hwnd,out var client)||!GetWindowRect(hwnd,out var window)||!GetCursorPos(out var cursor))throw Error();
        var origin=new Point();if(!ClientToScreen(hwnd,ref origin))throw Error();
        var width=client.Right-client.Left;var height=client.Bottom-client.Top;var resolution=GetDpiForWindow(hwnd);
        if(width<=0||height<=0||(long)width*height>16_777_216||resolution==0)throw new InputBlockedException("客户区无效");
        if(GetWindowThreadProcessId(hwnd,out var currentPid)==0||currentPid!=pid)throw new InputBlockedException("窗口进程在核验期间变化");
        return new(hwnd,checked((int)pid),identity.CreatedUtcTicks,identity.Executable,cls.ToString(),new(origin.X,origin.Y,width,height,(int)resolution),new(window.Left,window.Top,window.Right,window.Bottom),GetForegroundWindow(),IsWindowVisible(hwnd),IsIconic(hwnd),IsWindowEnabled(hwnd),new(cursor.X,cursor.Y),(GetAsyncKeyState(1)&0x8000)!=0,(GetAsyncKeyState(2)&0x8000)!=0);
    }
    // Query current metadata through one short-lived handle; never cache an identity or enumerate modules.
    public static (long CreatedUtcTicks,string Executable) ReadProcessIdentity(uint pid)
    {
        EnsureWindows();if(pid==0)throw new InputBlockedException("窗口进程无效");
        using var process=OpenProcess(0x1000,false,pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if(process.IsInvalid)throw Error();
        if(!GetProcessTimes(process,out var created,out _,out _,out _))throw Error();
        var path=new StringBuilder(1024);var size=path.Capacity;
        if(!QueryFullProcessImageName(process,0,path,ref size))
        {
            if(Marshal.GetLastWin32Error()!=122)throw Error();
            path=new StringBuilder(32768);size=path.Capacity;
            if(!QueryFullProcessImageName(process,0,path,ref size))throw Error();
        }
        if(size<=0||path.Length!=size)throw new InputBlockedException("无法核验完整进程路径");
        return (DateTime.FromFileTimeUtc(created).Ticks,path.ToString());
    }
    public CurrentClientFrame CaptureCurrentClientFrame(NativeWindowState state)
    {
        var at=System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency;
        var sequence=Interlocked.Increment(ref captureSequence);
        return new(CaptureClient(state),new("PrintWindow",generation,sequence,at));
    }
    public RgbImage CaptureClient(NativeWindowState state)
        =>CaptureClient(state,CancellationToken.None);
    public RgbImage CaptureClient(NativeWindowState state,CancellationToken cancellationToken)
    {ThrowDisposed();cancellationToken.ThrowIfCancellationRequested();EnsureWindows();var frame=captureClient.Capture(new(state.WindowId,state.ProcessId,state.ProcessStartUtcTicks,state.Geometry),cancellationToken);cancellationToken.ThrowIfCancellationRequested();return new(state.Geometry.Width,state.Geometry.Height,frame);}
    public void MoveClient(nint hwnd,PixelPoint point)
    {
        ThrowDisposed();Guard.AuthorizeNativeTransport();EnsureWindows();using var dpi=new DpiContext();if(!GetClientRect(hwnd,out var client))throw Error();if(!new PixelBox(0,0,client.Right-client.Left,client.Bottom-client.Top).Contains(point))throw new InputBlockedException("鼠标目标越出客户区");var p=new Point{X=point.X,Y=point.Y};if(!ClientToScreen(hwnd,ref p))throw Error();
        int left=GetSystemMetrics(76),top=GetSystemMetrics(77),width=GetSystemMetrics(78),height=GetSystemMetrics(79);
        if(width<=1||height<=1||p.X<left||p.X>=left+width||p.Y<top||p.Y>=top+height)throw new InputBlockedException("鼠标目标越出虚拟桌面");
        Send(new Input{Type=0,Data=new InputUnion{Mouse=new MouseInput{X=(int)Math.Round((p.X-left)*65535d/(width-1)),Y=(int)Math.Round((p.Y-top)*65535d/(height-1)),Flags=0x0001|0x8000|0x4000}}});
    }
    public void MouseButton(MouseButton button,bool down){ThrowDisposed();if(down)Guard.AuthorizeNativeTransport();else Guard.AuthorizeMouseRelease(button);Send(new Input{Type=0,Data=new InputUnion{Mouse=new MouseInput{Flags=button switch{Windows.MouseButton.Left=>down?0x0002u:0x0004u,Windows.MouseButton.Right=>down?0x0008u:0x0010u,_=>throw new ArgumentException("未知鼠标按钮")}}}});}
    public void Wheel(int delta){ThrowDisposed();Guard.AuthorizeNativeTransport();if(delta==0||delta%120!=0||Math.Abs((long)delta)>1440)throw new ArgumentOutOfRangeException(nameof(delta));Send(new Input{Type=0,Data=new InputUnion{Mouse=new MouseInput{Data=unchecked((uint)delta),Flags=0x0800}}});}
    public void Key(ushort key,bool down,bool unicode=false)
    {
        ThrowDisposed();
        if(down)Guard.AuthorizeNativeTransport();else Guard.AuthorizeKeyRelease(key,unicode);
        if(!unicode&&!InputKeyPolicy.IsSupported(key))throw new InputBlockedException("键未获支持");
        Send(new Input{Type=1,Data=new InputUnion{Keyboard=new KeyInput{VirtualKey=unicode?(ushort)0:key,Scan=unicode?key:(ushort)0,Flags=(unicode?0x0004u:0)|(down?0:0x0002u)}}});
    }
    public bool AnyPhysicalKeyDown(){ThrowDisposed();EnsureWindows();for(var i=8;i<255;i++)if(IsPhysicalKeyDown(i,GetAsyncKeyState,key=>MapVirtualKey((uint)key,4)))return true;return false;}
    internal static bool IsPhysicalKeyDown(int key,Func<int,short> state,Func<int,uint> scanCode)
    {
        if((state(key)&0x8000)==0)return false;
        // Legacy OEM/DBE states may stay set without a physical key mapping.
        // Keep ordinary keys and mapped OEM keys fail-closed, including numpad keys.
        return key is not (>=0xF0 and <=0xF5)||scanCode(key)!=0;
    }
    private static void Send(Input input){EnsureWindows();if(SendInput(1,[input],Marshal.SizeOf<Input>())!=1)throw new InputOutcomeUnknownException("SendInput 未确认；禁止自动重放");}
    private static void EnsureWindows(){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("仅支持 Windows");}
    private static Win32Exception Error()=>new(Marshal.GetLastWin32Error());
    private void ThrowDisposed()=>ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)captureClient.Dispose();}
    private sealed class DpiContext:IDisposable
    {
        readonly nint old;
        public DpiContext(){old=SetThreadDpiAwarenessContext(-4);if(old==0)throw new InputBlockedException("无法进入物理像素坐标上下文");}
        public void Dispose(){SetThreadDpiAwarenessContext(old);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct Input{public uint Type;public InputUnion Data;}
    [StructLayout(LayoutKind.Explicit)]private struct InputUnion{[FieldOffset(0)]public MouseInput Mouse;[FieldOffset(0)]public KeyInput Keyboard;}
    [StructLayout(LayoutKind.Sequential)]private struct MouseInput{public int X,Y;public uint Data,Flags,Time;public nuint ExtraInfo;}
    [StructLayout(LayoutKind.Sequential)]private struct KeyInput{public ushort VirtualKey,Scan;public uint Flags,Time;public nuint ExtraInfo;}
    [DllImport("user32.dll")]private static extern bool IsWindow(nint hwnd);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exited,out long kernel,out long user);
    [DllImport("kernel32.dll",EntryPoint="QueryFullProcessImageNameW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder path,ref int size);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern int GetClassName(nint hwnd,StringBuilder name,int max);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool GetClientRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool GetWindowRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool ClientToScreen(nint hwnd,ref Point point);
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")]private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",EntryPoint="MapVirtualKeyW")]private static extern uint MapVirtualKey(uint key,uint mapType);
    [DllImport("user32.dll")]private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",SetLastError=true)]private static extern uint SendInput(uint count,Input[] inputs,int size);
}
