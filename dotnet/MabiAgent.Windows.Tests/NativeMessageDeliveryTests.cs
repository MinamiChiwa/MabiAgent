using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Diagnostics;
using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;

public sealed class NativeMessageDeliveryTests
{
    // Diagnostic only: real cross-process WM_TOUCH, with the receiver deliberately
    // waiting until the sending test process has exited before reading either handle.
    [Fact] public void ExplicitTouchSenderLifetimeProbe()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_LIFETIME_HWND") is not {} value)return;
        var hwnd=(nint)long.Parse(value);var native=new NativeWindowsOps();var state=native.ReadWindow(hwnd);
        Assert.Equal("UnityWndClass",state.ClassName);Assert.Equal(int.Parse(Environment.GetEnvironmentVariable("MABI_TOUCH_LIFETIME_PID")!),state.ProcessId);Assert.NotEqual(hwnd,state.ForegroundId);
        using var ops=(TouchInputOps)InputTransportFactory.Create(state);
        using var guard=new ForegroundInputGuard(state,new("touch-lifetime",null,null),ops,_=>throw new InvalidOperationException("诊断不识别游戏页面"));
        guard.Start();guard.ClickAt(new(50,50));
    }
    [Fact] public async Task ExplicitTouchHandlesSurviveSenderExitAndDelayedReceiver()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_LIFETIME")!="1")return;
        using var receiver=new Receiver(touchLifetime:true);var before=new NativeWindowsOps().ReadWindow(receiver.Hwnd);var beforeDiagnostics=WindowMessageDiagnostics.Read(before);
        var start=new ProcessStartInfo("dotnet"){WorkingDirectory=RecognitionTests.Root(),UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"test","dotnet/MabiAgent.Windows.Tests","--no-build","--no-restore","--filter","FullyQualifiedName~ExplicitTouchSenderLifetimeProbe","--logger","console;verbosity=normal"})start.ArgumentList.Add(arg);
        start.Environment["DOTNET_CLI_UI_LANGUAGE"]="en";
        start.Environment.Remove("MABI_TOUCH_LIFETIME");start.Environment["MABI_TOUCH_LIFETIME_HWND"]=receiver.Hwnd.ToString();
        start.Environment["MABI_TOUCH_LIFETIME_PID"]=Environment.ProcessId.ToString();
        using var sender=Process.Start(start)!;var stdout=sender.StandardOutput.ReadToEndAsync();var stderr=sender.StandardError.ReadToEndAsync();
        try
        {
            Assert.True(sender.WaitForExit(25000),"发送方未按时退出");
            Assert.True(sender.ExitCode==0,await stdout+await stderr);
            receiver.ReadTouch.Set();
            Assert.True(SpinWait.SpinUntil(()=>receiver.TouchMessages.Count>=2,3000),"未收到完整按下/抬起");
            var after=new NativeWindowsOps().ReadWindow(receiver.Hwnd);
            var report=new JsonObject{["at"]=DateTimeOffset.UtcNow.ToString("O"),["sender_exited_before_read"]=true,["foreground_unchanged"]=before.ForegroundId==after.ForegroundId,["cursor_unchanged"]=before.Cursor==after.Cursor,["game_input"]=false,["before_input"]=beforeDiagnostics,["after_input"]=WindowMessageDiagnostics.Read(after),["messages"]=new JsonArray(receiver.TouchMessages.Select(m=>(JsonNode)m.DeepClone()).ToArray())};
            File.WriteAllText(Path.Combine(RecognitionTests.Root(),"build/touch-delivery-lifetime-current.json"),report.ToJsonString());
            Assert.All(receiver.TouchMessages,m=>{Assert.True(m["readable"]!.GetValue<bool>(),m.ToJsonString());Assert.True(m["after_sender_exit"]!.GetValue<bool>());});
            var flags=receiver.TouchMessages.SelectMany(m=>m["contacts"]!.AsArray()).Select(c=>c!["flags"]!.GetValue<uint>()&7).ToArray();
            Assert.Contains(2u,flags);Assert.Contains(4u,flags);Assert.Equal(before.ForegroundId,after.ForegroundId);Assert.Equal(before.Cursor,after.Cursor);
        }
        finally{receiver.ReadTouch.Set();if(!sender.HasExited)sender.Kill(entireProcessTree:true);}
    }
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
        using var receiver=new Receiver();var state=new NativeWindowsOps().ReadWindow(receiver.Hwnd);var traces=new List<WindowMessageTrace>();var ops=new WindowMessageInputOps(state,trace:traces.Add);var scope=new SessionScope("native-capability-gate",null,null);
        using var guard=new ForegroundInputGuard(state,scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,scope,new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(20,30)}},[]),true,true),maxFrameAgeSeconds:5);
        guard.Start();Assert.Throws<InputBlockedException>(()=>guard.Click(guard.ObserveTarget("scene.open_bag")));Assert.Empty(traces);Assert.Empty(receiver.Messages);
        guard.Resume();guard.PressKey(guard.ObserveTarget("scene.open_bag"),73,TimeSpan.FromMilliseconds(20));Assert.Contains(traces,t=>t.Message==0x0100);Assert.Contains(traces,t=>t.Message==0x0101);Assert.DoesNotContain(traces,t=>t.Message is >=0x0200 and <=0x020a);
    }
    private sealed record Received(uint Message,nuint WParam,nint LParam,PixelPoint Cursor,PixelPoint QueuePoint);
    private sealed class Receiver:IDisposable
    {
        private readonly Thread thread;private readonly TaskCompletionSource<nint> ready=new(TaskCreationOptions.RunContinuationsAsynchronously);private readonly WndProc proc;private nint hwnd;
        private readonly bool touchLifetime;private const string ClassName="UnityWndClass";
        public ManualResetEventSlim ReadTouch{get;}=new(false);
        public ConcurrentQueue<JsonObject> TouchMessages{get;}=new();
        public nint Hwnd=>ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        public ConcurrentQueue<Received> Messages{get;}=new();
        public Receiver(bool touchLifetime=false){this.touchLifetime=touchLifetime;proc=Handle;thread=new Thread(Run){IsBackground=true,Name="native-message-oracle"};thread.Start();_=Hwnd;}
        private void Run()
        {
            var module=GetModuleHandle(null);
            try
            {
                using var dpi=TouchNative.PhysicalDpi();
                var c=new WindowClass{Size=(uint)Marshal.SizeOf<WindowClass>(),Instance=module,Name=ClassName,Procedure=Marshal.GetFunctionPointerForDelegate(proc)};
                if(RegisterClassEx(ref c)==0)throw new InvalidOperationException("无法创建本机消息测试窗口："+Marshal.GetLastWin32Error());
                // Keyboard oracle stays off-screen; the explicit touch oracle uses a small
                // nonactivating window on screen so the production transport can reach it.
                hwnd=CreateWindowEx(0x08000080,ClassName,"message-oracle",0x90000000,touchLifetime?300:-30000,touchLifetime?200:-30000,100,100,0,0,module,0);if(hwnd==0)throw new InvalidOperationException("无法创建消息接收器");
                if(touchLifetime&&!TouchNative.RegisterTouchWindow(hwnd,2))throw new InvalidOperationException("无法注册诊断触控接收器");ready.SetResult(hwnd);
                while(GetMessage(out var message,0,0,0)>0){TranslateMessage(ref message);DispatchMessage(ref message);}
            }
            catch(Exception error){ready.TrySetException(error);}
            finally{if(hwnd!=0)DestroyWindow(hwnd);UnregisterClass(ClassName,module);}
        }
        private nint Handle(nint h,uint message,nuint w,nint l)
        {
            if(message==0x0240&&touchLifetime)
            {
                var began=Stopwatch.GetTimestamp();var released=ReadTouch.Wait(30000);var count=(uint)(w&0xffff);
                var contacts=new TouchNative.LegacyTouchInput[Math.Min(count,10)];
                var ok=count is >0 and <=10&&TouchNative.GetTouchInputInfo(l,count,contacts,Marshal.SizeOf<TouchNative.LegacyTouchInput>());var error=ok?0:Marshal.GetLastWin32Error();
                TouchMessages.Enqueue(new(){["readable"]=ok,["error"]=error,["after_sender_exit"]=released,["wait_ms"]=Stopwatch.GetElapsedTime(began).TotalMilliseconds,["contacts"]=new JsonArray(contacts.Select(c=>(JsonNode)new JsonObject{["id"]=c.Id,["flags"]=c.Flags,["source"]=(long)c.Source,["time"]=c.Time,["screen_hundredths"]=new JsonArray(c.X,c.Y)}).ToArray())});
                TouchNative.CloseTouchInputHandle(l);return 0;
            }
            if(message is 0x0200 or 0x0201 or 0x0202){GetCursorPos(out var cursor);var p=GetMessagePos();Messages.Enqueue(new(message,w,l,new(cursor.X,cursor.Y),new(unchecked((short)p),unchecked((short)(p>>16)))));return 0;}
            if(message==0x0010){DestroyWindow(h);return 0;}if(message==0x0002){PostQuitMessage(0);return 0;}return DefWindowProc(h,message,w,l);
        }
        public void Dispose(){ReadTouch.Set();if(hwnd!=0)PostMessage(hwnd,0x0010,0,0);if(!thread.Join(3000))throw new TimeoutException("本机消息测试窗口未退出");ReadTouch.Dispose();GC.KeepAlive(proc);}
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
