using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using MabiAgent.Windows;
namespace MabiAgent.Windows;

// Shared public-API touch platform. Never activates or changes the target game window.
internal static class TouchNative
{
    internal const uint Down=0x14006,Update=0x24006,Up=0x40000;
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X,Y;public Point(PixelPoint p){X=p.X;Y=p.Y;} }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct PointerInfo
    {
        public uint Type,Id,Frame,Flags;public nint Source,Target;public Point Pixel,Himetric,Raw,HimetricRaw;
        public uint Time,History;public int Input;public uint Keys;public ulong Performance;public uint Change;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct TouchInfo
    {public PointerInfo Pointer;public uint Flags,Mask;public Rect Contact,RawContact;public uint Orientation,Pressure;}
    [StructLayout(LayoutKind.Explicit,Size=152)] internal struct TypeInfo
    { [FieldOffset(0)] public uint Type;[FieldOffset(8)] public TouchInfo Touch; }
    [StructLayout(LayoutKind.Sequential)] internal struct LegacyTouchInput
    {public int X,Y;public nint Source;public uint Id,Flags,Mask,Time;public nuint Extra;public uint Width,Height;}
    internal static TypeInfo Contact(uint id,PixelPoint p,uint flags,nint suggestedTarget=0)=>new(){Type=2,Touch=new(){Pointer=new(){Type=2,Id=id,Flags=flags,Pixel=new(p),Raw=new(p),Target=suggestedTarget}}};
    internal static nint Foreground=>GetForegroundWindow();
    internal static nint At(PixelPoint p){var old=SetThreadDpiAwarenessContext(-4);try{return WindowFromPoint(new(p));}finally{SetThreadDpiAwarenessContext(old);}}
    internal static IDisposable PhysicalDpi()=>new Dpi();
    private sealed class Dpi:IDisposable { private readonly nint old=SetThreadDpiAwarenessContext(-4);public Dpi(){if(old==0)throw new Win32Exception();}public void Dispose()=>SetThreadDpiAwarenessContext(old); }
    [DllImport("user32.dll",SetLastError=true)] internal static extern nint CreateSyntheticPointerDevice(uint type,uint count,uint feedback);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool InjectSyntheticPointerInput(nint device,[In] TypeInfo[] pointers,uint count);
    [DllImport("user32.dll")] internal static extern void DestroySyntheticPointerDevice(nint device);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool GetPointerInfo(uint id,out PointerInfo info);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool RegisterTouchWindow(nint hwnd,uint flags);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool GetTouchInputInfo(nint input,uint count,[Out] LegacyTouchInput[] contacts,int size);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool CloseTouchInputHandle(nint input);
    [DllImport("user32.dll",EntryPoint="PostMessageW",SetLastError=true)] internal static extern bool PostTouch(nint hwnd,uint message,nuint w,nint l);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}

internal sealed class TouchEnvelope(nuint w,nint handle,JsonObject decoded):IDisposable
{
    private nint owned=handle;
    internal JsonObject Decoded{get;}=decoded;
    internal JsonObject Forward(nint hwnd)
    {
        if(owned==0)throw new InputBlockedException("触控句柄已转交，禁止重放");
        // WM_TOUCH forwarding transfers the system handle to the destination. Do not close or reuse it afterward.
        var h=owned;owned=0;var ok=TouchNative.PostTouch(hwnd,0x0240,w,h);var error=ok?0:Marshal.GetLastWin32Error();
        var trace=new JsonObject{["message"]=0x0240,["target"]=(long)hwnd,["submitted"]=ok,["error"]=error,["decoded_before_transfer"]=Decoded.DeepClone()};
        if(!ok)throw new InputOutcomeUnknownException("WM_TOUCH转交失败，禁止重放："+error);return trace;
    }
    public void Dispose(){var h=Interlocked.Exchange(ref owned,0);if(h!=0)TouchNative.CloseTouchInputHandle(h);}
}

internal sealed class TouchInputWindow:IDisposable
{
    private readonly Thread thread;private readonly TaskCompletionSource<nint> ready=new(TaskCreationOptions.RunContinuationsAsynchronously);private readonly WndProc proc;
    private readonly string name="MabiTouchInput_"+Guid.NewGuid().ToString("N");private nint hwnd;private readonly PixelBox box;private int disposed;private readonly bool legacyTouch,retainTouchHandles;private readonly string? label;private readonly bool inputSurface;
    private int mouseMoves,mouseDowns,mouseUps,mouseDragMoves;
    internal int MouseDragMoves=>Volatile.Read(ref mouseDragMoves);
    internal int MouseMoves=>Volatile.Read(ref mouseMoves);internal int MouseDowns=>Volatile.Read(ref mouseDowns);internal int MouseUps=>Volatile.Read(ref mouseUps);
    internal ConcurrentQueue<JsonObject> Received{get;}=new();
    internal ConcurrentQueue<TouchEnvelope> Touches{get;}=new();
    internal nint Hwnd=>ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    internal PixelPoint Point=>box.Center;
    internal void LimitContactRegion(IEnumerable<PixelPoint> points)
    {
        var region=CreateRectRgn(0,0,0,0);if(region==0)throw new Win32Exception();
        try{foreach(var p in points){var part=CreateRectRgn(p.X-box.Left-1,p.Y-box.Top-1,p.X-box.Left+2,p.Y-box.Top+2);if(part==0)throw new Win32Exception();try{if(CombineRgn(region,region,part,2)==0)throw new Win32Exception();}finally{DeleteObject(part);}}
            if(SetWindowRgn(Hwnd,region,true)==0)throw new Win32Exception();region=0;}
        finally{if(region!=0)DeleteObject(region);}
    }
    [DllImport("gdi32.dll")]private static extern nint CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("gdi32.dll")]private static extern int CombineRgn(nint result,nint first,nint second,int mode);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll",SetLastError=true)]private static extern int SetWindowRgn(nint hwnd,nint region,bool redraw);
    internal bool Closed=>!thread.IsAlive;
    internal TouchInputWindow(PixelBox box,bool legacyTouch=false,bool retainTouchHandles=false,string? label=null,bool inputSurface=false){this.inputSurface=inputSurface;this.box=box;this.legacyTouch=legacyTouch;this.retainTouchHandles=retainTouchHandles;this.label=label;proc=Handle;thread=new Thread(Run){IsBackground=true,Name="touch-probe-window"};thread.Start();_=Hwnd;}
    private void Run()
    {
        var module=GetModuleHandle(null);using var dpi=TouchNative.PhysicalDpi();
        try
        {
            var c=new WindowClass{Size=(uint)Marshal.SizeOf<WindowClass>(),Instance=module,Name=name,Procedure=Marshal.GetFunctionPointerForDelegate(proc),Background=6,Cursor=LoadCursor(0,32512)};
            if(RegisterClassEx(ref c)==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            hwnd=CreateWindowEx(inputSurface?0x08080088u:0x08000088u,name,"输入触点",0x90000000,box.Left,box.Top,box.Width,box.Height,0,0,module,0);
            if(hwnd==0)throw new Win32Exception(Marshal.GetLastWin32Error());if(inputSurface&&!SetLayeredWindowAttributes(hwnd,0,1,2))throw new Win32Exception(Marshal.GetLastWin32Error());if(legacyTouch&&!TouchNative.RegisterTouchWindow(hwnd,2))throw new Win32Exception(Marshal.GetLastWin32Error());ready.SetResult(hwnd);
            while(GetMessage(out var message,0,0,0)>0){TranslateMessage(ref message);DispatchMessage(ref message);}
        }
        catch(Exception error){ready.TrySetException(error);}
        finally{if(hwnd!=0)DestroyWindow(hwnd);UnregisterClass(name,module);}
    }
    private nint Handle(nint h,uint m,nuint w,nint l)
    {
        if(m==0x000f&&label is not null)
        {
            var dc=BeginPaint(h,out var paint);try{GetClientRect(h,out var rect);rect.Left+=16;rect.Right-=16;rect.Top+=40;DrawText(dc,label,-1,ref rect,0x11);}finally{EndPaint(h,ref paint);}return 0;
        }
        if(m==0x024b)return 3; // PA_NOACTIVATE
        if(m==0x0021)return 3; // MA_NOACTIVATE
        if(m==0x0240)
        {
            var count=(uint)(w&0xffff);var contacts=new TouchNative.LegacyTouchInput[Math.Min(count,10)];
            var ok=count is >0 and <=10&&TouchNative.GetTouchInputInfo(l,count,contacts,Marshal.SizeOf<TouchNative.LegacyTouchInput>());
            var decoded=new JsonObject{["message"]=m,["touch_info_available"]=ok,["count"]=count,["receiver"]=(long)h,["foreground"]=(long)TouchNative.Foreground,["contacts"]=new JsonArray(contacts.Select(t=>(JsonNode)new JsonObject{["id"]=t.Id,["flags"]=t.Flags,["screen_hundredths"]=new JsonArray(t.X,t.Y)}).ToArray())};
            if(Received.Count<40||contacts.Any(t=>(t.Flags&4)!=0)){Received.Enqueue(decoded);while(Received.Count>80)Received.TryDequeue(out _);}
            if(ok&&retainTouchHandles&&Touches.Count<64)Touches.Enqueue(new(w,l,decoded));else TouchNative.CloseTouchInputHandle(l);
            return 0;
        }
        if(m is >=0x0245 and <=0x024f)
        {
            if(Received.Count<40||m==0x247){var id=(uint)(w&0xffff);var known=TouchNative.GetPointerInfo(id,out var p);Received.Enqueue(new(){["message"]=m,["id"]=id,["pointer_info_available"]=known,["flags"]=p.Flags,["target"]=(long)p.Target,["point"]=new JsonArray(p.Pixel.X,p.Pixel.Y),["foreground"]=(long)TouchNative.Foreground});while(Received.Count>80)Received.TryDequeue(out _);}
            // Registered legacy receivers need default pointer translation to obtain a real WM_TOUCH handle.
            return legacyTouch?DefWindowProc(h,m,w,l):0;
        }
        if(m is 0x0200 or 0x0201 or 0x0202)
        {
            if(m==0x0200){Interlocked.Increment(ref mouseMoves);if((w&1)!=0)Interlocked.Increment(ref mouseDragMoves);}else if(m==0x0201)Interlocked.Increment(ref mouseDowns);else Interlocked.Increment(ref mouseUps);
            if(Received.Count<40||m!=0x0200){Received.Enqueue(new(){["mouse_message"]=m,["extra"]=(long)GetMessageExtraInfo(),["tick"]=System.Diagnostics.Stopwatch.GetTimestamp()});while(Received.Count>80)Received.TryDequeue(out _);}return 0;
        }
        if(m==0x0010){DestroyWindow(h);return 0;}if(m==0x0002){PostQuitMessage(0);return 0;}return DefWindowProc(h,m,w,l);
    }
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;if(hwnd!=0)PostMessage(hwnd,0x0010,0,0);if(!thread.Join(3000))throw new TimeoutException("触控辅助窗口未退出");while(Touches.TryDequeue(out var touch))touch.Dispose();GC.KeepAlive(proc);}
    private delegate nint WndProc(nint h,uint m,nuint w,nint l);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct WindowClass{public uint Size,Style;public nint Procedure;public int ClassExtra,WindowExtra;public nint Instance,Icon,Cursor,Background;public string? Menu,Name;public nint SmallIcon;}
    [StructLayout(LayoutKind.Sequential)]private struct Message{public nint Window;public uint Id;public nuint WParam;public nint LParam;public uint Time;public TouchNative.Point Point;public uint Private;}
    [StructLayout(LayoutKind.Sequential)]private struct PaintInfo{public nint Dc;public int Erase;public TouchNative.Rect Rect;public int Restore,Update;[MarshalAs(UnmanagedType.ByValArray,SizeConst=32)]public byte[] Reserved;}
    [DllImport("kernel32.dll",EntryPoint="GetModuleHandleW",CharSet=CharSet.Unicode)]private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll",EntryPoint="RegisterClassExW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern ushort RegisterClassEx(ref WindowClass c);
    [DllImport("user32.dll",EntryPoint="CreateWindowExW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint CreateWindowEx(uint extended,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll",EntryPoint="GetMessageW")]private static extern int GetMessage(out Message m,nint h,uint min,uint max);
    [DllImport("user32.dll")]private static extern bool TranslateMessage(ref Message m);
    [DllImport("user32.dll",EntryPoint="DispatchMessageW")]private static extern nint DispatchMessage(ref Message m);
    [DllImport("user32.dll",EntryPoint="DefWindowProcW")]private static extern nint DefWindowProc(nint h,uint m,nuint w,nint l);
    [DllImport("user32.dll",EntryPoint="LoadCursorW")]private static extern nint LoadCursor(nint instance,nint resource);
    [DllImport("user32.dll")]private static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll")]private static extern nint BeginPaint(nint h,out PaintInfo paint);
    [DllImport("user32.dll")]private static extern bool EndPaint(nint h,ref PaintInfo paint);
    [DllImport("user32.dll")]private static extern bool GetClientRect(nint h,out TouchNative.Rect rect);
    [DllImport("user32.dll",EntryPoint="DrawTextW",CharSet=CharSet.Unicode)]private static extern int DrawText(nint dc,string text,int count,ref TouchNative.Rect rect,uint flags);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetLayeredWindowAttributes(nint hwnd,uint key,byte alpha,uint flags);
    [DllImport("user32.dll",EntryPoint="PostMessageW")]private static extern bool PostMessage(nint h,uint m,nuint w,nint l);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")]private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll",EntryPoint="UnregisterClassW",CharSet=CharSet.Unicode)]private static extern bool UnregisterClass(string cls,nint instance);
}

internal sealed class SyntheticTouchDevice:IDisposable
{
    private nint device;private readonly PixelPoint anchor;private readonly nint anchorWindow;private readonly bool anchored;private PixelPoint target;private nint targetWindow;
    private PixelPoint[] active=[];
    private bool anchorAttempted,targetAttempted;internal JsonArray Frames{get;}=new();internal bool Released{get;private set;}
    internal SyntheticTouchDevice(PixelPoint anchor,nint anchorWindow,bool anchored=true)
    {this.anchor=anchor;this.anchorWindow=anchorWindow;this.anchored=anchored;device=TouchNative.CreateSyntheticPointerDevice(2,3,3);if(device==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"创建合成触点设备失败");}
    internal void Begin()
    {if(!anchored)return;anchorAttempted=true;Send(TouchNative.Contact(1,anchor,TouchNative.Down,anchorWindow));Thread.Sleep(20);}
    internal void Down(PixelPoint point,nint hwnd)
    {Down([point],hwnd);}
    internal void Down(PixelPoint[] points,nint hwnd)
    {
        if(points.Length is <1 or >2||targetAttempted)throw new ArgumentException("仅支持一到两个未按下目标触点");
        active=points.ToArray();target=active[0];targetWindow=hwnd;targetAttempted=true;SendTarget(TouchNative.Down);
    }
    internal void Move(PixelPoint[] points)
    {if(!targetAttempted||points.Length!=active.Length)throw new InvalidOperationException("目标触点数量变化");active=points.ToArray();target=active[0];SendTarget(TouchNative.Update);}
    private void SendTarget(uint flags)=>Send((anchored?new[]{TouchNative.Contact(1,anchor,TouchNative.Update,anchorWindow)}:Array.Empty<TouchNative.TypeInfo>()).Concat(active.Select((p,i)=>TouchNative.Contact((uint)i+2,p,flags,targetWindow))).ToArray());
    internal void Up()
    {if(!targetAttempted)return;SendTarget(TouchNative.Up);targetAttempted=false;}
    private void Send(params TouchNative.TypeInfo[] frame)
    {
        using var dpi=TouchNative.PhysicalDpi();var ok=TouchNative.InjectSyntheticPointerInput(device,frame,(uint)frame.Length);var error=ok?0:Marshal.GetLastWin32Error();
        Frames.Add(new JsonObject{["tick"]=System.Diagnostics.Stopwatch.GetTimestamp(),["submitted"]=ok,["error"]=error,["foreground"]=(long)TouchNative.Foreground,["contacts"]=new JsonArray(frame.Select(p=>(JsonNode)new JsonObject{["id"]=p.Touch.Pointer.Id,["flags"]=p.Touch.Pointer.Flags,["suggested_target"]=(long)p.Touch.Pointer.Target,["point"]=new JsonArray(p.Touch.Pointer.Pixel.X,p.Touch.Pointer.Pixel.Y)}).ToArray())});
        if(!ok)throw new Win32Exception(error,$"合成触点投递失败（系统错误{error}），禁止重放");
    }
    public void Dispose()
    {
        if(device==0)return;Exception? error=null;
        try{Up();}catch(Exception e){error=e;}
        try{if(anchorAttempted){Thread.Sleep(20);Send(TouchNative.Contact(1,anchor,TouchNative.Up,anchorWindow));}}catch(Exception e){error??=e;}
        finally{TouchNative.DestroySyntheticPointerDevice(device);device=0;Released=true;}
        if(error is not null)throw new InputOutcomeUnknownException("触点释放存在异常："+error.Message);
    }
}


internal static class PhysicalDesktop
{
    internal static PixelBox Bounds { get { using var dpi=TouchNative.PhysicalDpi();var x=GetSystemMetrics(76);var y=GetSystemMetrics(77);return new(x,y,x+GetSystemMetrics(78),y+GetSystemMetrics(79)); } }
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
}
