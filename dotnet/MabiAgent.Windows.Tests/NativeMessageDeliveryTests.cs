using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;

public sealed class NativeMessageDeliveryTests
{
    [Fact] public void BackgroundKeyboardRestoresActivationBeforeNextTouch()
    {
        if(!OperatingSystem.IsWindows())return;
        using var receiver=new Receiver();var native=new NativeWindowsOps();var state=native.ReadWindow(receiver.Hwnd);var traces=new List<WindowMessageTrace>();
        var ops=new WindowMessageInputOps(state,notifyActivation:true,trace:traces.Add);var scope=new SessionScope("keyboard-touch-sequence",null,null);
        using var touch=new TouchInputOps(state,native,ops,new NoTouchRunner());
        using var guard=new ForegroundInputGuard(state,scope,touch,c=>new(c.Image.Sha256,c.CapturedAt,scope,new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_skills",new(20,30)}},[]),true,true),maxFrameAgeSeconds:5);
        guard.Start();guard.PressKey(guard.ObserveTarget("scene.open_skills"),75,TimeSpan.FromMilliseconds(20));
        Assert.Equal(new uint[]{0x0006,0x0100,0x0101},traces.Select(t=>t.Message));guard.Click(guard.ObserveTarget("scene.open_skills"));
        Assert.Equal(new uint[]{0x0006,0x0100,0x0101,0x0006},traces.Select(t=>t.Message));Assert.Equal((ulong)0,traces[^1].WParam);
        traces.Clear();guard.TypeText(guard.ObserveTarget("scene.open_skills"),"鐵",focus:false);
        Assert.Equal(new uint[]{0x0006,0x0102},traces.Select(t=>t.Message));guard.Click(guard.ObserveTarget("scene.open_skills"));
        Assert.Equal(new uint[]{0x0006,0x0102,0x0006},traces.Select(t=>t.Message));Assert.Equal((ulong)0,traces[^1].WParam);
        Assert.All(traces,t=>Assert.True(t.Submitted));Assert.Equal(state.ForegroundId,native.ReadWindow(receiver.Hwnd).ForegroundId);
    }
    private sealed class NoTouchRunner:ITouchGestureRunner
    {
        public void Execute(TouchGesturePlan screen,nint target,int holdMilliseconds,Action authorize,Action authorizeRelease,Func<NativeWindowState> verifyIdentity){authorize();authorizeRelease();}
        public void Release(Func<NativeWindowState> verifyIdentity){}
        public void Dispose(){}
    }
    [Fact] public void UnvalidatedMouseIsBlockedBeforeAnyMessageButKeyboardRemainsAvailable()
    {
        if(!OperatingSystem.IsWindows())return;
        using var receiver=new Receiver();var state=new NativeWindowsOps().ReadWindow(receiver.Hwnd);var traces=new List<WindowMessageTrace>();var ops=new WindowMessageInputOps(state,trace:traces.Add,noDelayMouseProbe:true);var scope=new SessionScope("native-capability-gate",null,null);
        Assert.False(ops.AllowsZeroClickDelay);Assert.Equal(50,ops.MoveSampleMilliseconds);
        using var guard=new ForegroundInputGuard(state,scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,scope,new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(20,30)}},[]),true,true),maxFrameAgeSeconds:5);
        guard.Start();Assert.Throws<InputBlockedException>(()=>guard.Click(guard.ObserveTarget("scene.open_bag")));Assert.Empty(traces);Assert.Empty(receiver.Messages);
        guard.Resume();guard.PressKey(guard.ObserveTarget("scene.open_bag"),73,TimeSpan.FromMilliseconds(20));Assert.Contains(traces,t=>t.Message==0x0100);Assert.Contains(traces,t=>t.Message==0x0101);Assert.DoesNotContain(traces,t=>t.Message is >=0x0200 and <=0x020a);
    }
    [Theory]
    [InlineData(false,false,false,false)]
    [InlineData(true,false,false,false)]
    [InlineData(true,true,false,false)]
    [InlineData(true,false,true,false)]
    [InlineData(true,false,false,true)]
    public void RealReceiverGetsExactClientCoordinatesButQueuePositionRemainsIndependent(bool activate,bool sync,bool application,bool immediate)
    {
        if(!OperatingSystem.IsWindows())return;
        using var receiver=new Receiver();var native=new NativeWindowsOps();var before=native.ReadWindow(receiver.Hwnd);
        var traces=new List<WindowMessageTrace>();var ops=new WindowMessageInputOps(before,activate,sync,traces.Add,application,allowMouseProbe:true,noDelayMouseProbe:immediate);var scope=new SessionScope("native-message-receiver",null,null);
        Assert.Equal(immediate?0:50,ops.MoveSampleMilliseconds);Assert.Equal(immediate?0:50,ops.ClickSampleMilliseconds);
        using var guard=new ForegroundInputGuard(before,scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,scope,new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(20,30)}},[]),true,true),maxFrameAgeSeconds:5);
        guard.Start();guard.Click(guard.ObserveTarget("scene.open_bag"));
        Assert.True(SpinWait.SpinUntil(()=>receiver.Messages.Count(m=>m.Message==0x0202)==1,2000));
        var messages=receiver.Messages.Where(m=>m.Message is 0x0200 or 0x0201 or 0x0202).ToArray();Assert.Equal(new uint[]{0x0200,0x0201,0x0202},messages.Select(m=>m.Message));
        Assert.All(messages,m=>Assert.Equal(WindowMessageInputOps.EncodePoint(new(20,30)),m.LParam));Assert.Equal((nuint)1,messages[1].WParam);Assert.Equal((nuint)0,messages[2].WParam);
        Assert.All(traces,t=>Assert.True(t.Submitted));Assert.Equal(before.ForegroundId,native.ReadWindow(receiver.Hwnd).ForegroundId);
        // The message argument is not the system cursor or MSG.pt/GetMessagePos.
        var screenTarget=new PixelPoint(before.Geometry.X+20,before.Geometry.Y+30);
        Assert.All(messages,m=>Assert.NotEqual(screenTarget,m.Cursor));
        if(!sync)Assert.All(messages,m=>Assert.NotEqual(screenTarget,m.QueuePoint));
        if(Environment.GetEnvironmentVariable("MABI_MESSAGE_DELIVERY_REPORT")=="1")
        {
            var path=Path.Combine(RecognitionTests.Root(),"build/reference-optimization-validation.json");var report=File.Exists(path)?JsonNode.Parse(File.ReadAllText(path))!.AsObject():new JsonObject();report["native_receiver"]??=new JsonObject();
            report["native_receiver"]![immediate?"post_activate_no_delay":application?"post_focus":activate?sync?"send_activate":"post_activate":"post_plain"]=new JsonObject{["at"]=DateTimeOffset.UtcNow.ToString("O"),["scope"]="self-owned native window; not game verification",["move_delay_ms"]=ops.MoveSampleMilliseconds,["click_delay_ms"]=ops.ClickSampleMilliseconds,["target_screen"]=new JsonArray(screenTarget.X,screenTarget.Y),["foreground_unchanged"]=before.ForegroundId==native.ReadWindow(receiver.Hwnd).ForegroundId,["messages"]=new JsonArray(messages.Select(m=>(JsonNode)new JsonObject{["message"]=m.Message,["wparam"]=(ulong)m.WParam,["lparam"]=(long)m.LParam,["cursor"]=new JsonArray(m.Cursor.X,m.Cursor.Y),["queue_point"]=new JsonArray(m.QueuePoint.X,m.QueuePoint.Y)}).ToArray())};
            File.WriteAllText(path,report.ToJsonString(new(){WriteIndented=true}));
        }
    }
    private sealed record Received(uint Message,nuint WParam,nint LParam,PixelPoint Cursor,PixelPoint QueuePoint);
    private sealed class Receiver:IDisposable
    {
        private readonly Thread thread;private readonly TaskCompletionSource<nint> ready=new(TaskCreationOptions.RunContinuationsAsynchronously);private readonly WndProc proc;private nint hwnd;
        public nint Hwnd=>ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        public ConcurrentQueue<Received> Messages{get;}=new();
        public Receiver(){proc=Handle;thread=new Thread(Run){IsBackground=true,Name="native-message-oracle"};thread.Start();_=Hwnd;}
        private void Run()
        {
            var module=GetModuleHandle(null);
            try
            {
                var c=new WindowClass{Size=(uint)Marshal.SizeOf<WindowClass>(),Instance=module,Name="UnityWndClass",Procedure=Marshal.GetFunctionPointerForDelegate(proc)};
                if(RegisterClassEx(ref c)==0)throw new InvalidOperationException("无法创建本机消息测试窗口："+Marshal.GetLastWin32Error());
                // Off-screen, no activation, no user input capture; never moves the game window.
                hwnd=CreateWindowEx(0x08000080,"UnityWndClass","message-oracle",0x90000000,-30000,-30000,100,100,0,0,module,0);if(hwnd==0)throw new InvalidOperationException("无法创建消息接收器");ready.SetResult(hwnd);
                while(GetMessage(out var message,0,0,0)>0){TranslateMessage(ref message);DispatchMessage(ref message);}
            }
            catch(Exception error){ready.TrySetException(error);}
            finally{if(hwnd!=0)DestroyWindow(hwnd);UnregisterClass("UnityWndClass",module);}
        }
        private nint Handle(nint h,uint message,nuint w,nint l)
        {
            if(message is 0x0200 or 0x0201 or 0x0202){GetCursorPos(out var cursor);var p=GetMessagePos();Messages.Enqueue(new(message,w,l,new(cursor.X,cursor.Y),new(unchecked((short)p),unchecked((short)(p>>16)))));return 0;}
            if(message==0x0010){DestroyWindow(h);return 0;}if(message==0x0002){PostQuitMessage(0);return 0;}return DefWindowProc(h,message,w,l);
        }
        public void Dispose(){if(hwnd!=0)PostMessage(hwnd,0x0010,0,0);if(!thread.Join(3000))throw new TimeoutException("本机消息测试窗口未退出");GC.KeepAlive(proc);}
    }
    private delegate nint WndProc(nint h,uint m,nuint w,nint l);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct WindowClass{public uint Size,Style;public nint Procedure;public int ClassExtra,WindowExtra;public nint Instance,Icon,Cursor,Background;public string? Menu,Name;public nint SmallIcon;}
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct Message{public nint Window;public uint Id;public nuint WParam;public nint LParam;public uint Time;public Point Point;public uint Private;}
    [DllImport("kernel32.dll",EntryPoint="GetModuleHandleW",CharSet=CharSet.Unicode)]private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll",EntryPoint="RegisterClassExW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern ushort RegisterClassEx(ref WindowClass c);
    [DllImport("user32.dll",EntryPoint="CreateWindowExW",CharSet=CharSet.Unicode)]private static extern nint CreateWindowEx(uint extended,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll",EntryPoint="GetMessageW")]private static extern int GetMessage(out Message m,nint h,uint min,uint max);
    [DllImport("user32.dll")]private static extern bool TranslateMessage(ref Message m);
    [DllImport("user32.dll",EntryPoint="DispatchMessageW")]private static extern nint DispatchMessage(ref Message m);
    [DllImport("user32.dll",EntryPoint="DefWindowProcW")]private static extern nint DefWindowProc(nint h,uint m,nuint w,nint l);
    [DllImport("user32.dll",EntryPoint="PostMessageW")]private static extern bool PostMessage(nint h,uint m,nuint w,nint l);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")]private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll",EntryPoint="UnregisterClassW",CharSet=CharSet.Unicode)]private static extern bool UnregisterClass(string cls,nint instance);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")]private static extern uint GetMessagePos();
}
