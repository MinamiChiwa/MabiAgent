using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using MabiAgent.Windows;
namespace MabiAgent.Windows;

// Read-only, bounded diagnostics: always passes input to the next hook, never blocks hardware input.
internal sealed class TouchMouseMonitor:IDisposable
{
    private readonly Thread thread;private readonly HookProc callback;private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);private uint threadId;private nint hook;private int cleanupComplete;
    internal ConcurrentQueue<MouseEvent> Events{get;}=new();
    private int physicalMoves,physicalDowns,physicalUps,physicalDragMoves,injected;private bool leftHeld;
    internal int PhysicalDragMoves=>Volatile.Read(ref physicalDragMoves);
    internal int PhysicalMoves=>Volatile.Read(ref physicalMoves);internal int PhysicalDowns=>Volatile.Read(ref physicalDowns);internal int PhysicalUps=>Volatile.Read(ref physicalUps);internal int Injected=>Volatile.Read(ref injected);
    internal sealed record MouseEvent(long Tick,uint Message,PixelPoint Point,uint Flags,nuint Extra,uint CursorFlags)
    {
        internal bool Physical=>(Flags&1)==0&&(Extra&0xffffff00u)!=0xff515700u;
        internal JsonObject ToJson()=>new(){["tick"]=Tick,["message"]=Message,["point"]=new JsonArray(Point.X,Point.Y),["injected"]=(Flags&1)!=0,["physical"]=Physical,["extra"]=(ulong)Extra,["cursor_flags"]=CursorFlags};
    }
    internal TouchMouseMonitor(){callback=Handle;thread=new Thread(Run){IsBackground=true,Name="touch-mouse-observer"};thread.Start();ready.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();}
    private void Run()
    {
        using var dpi=TouchNative.PhysicalDpi();threadId=GetCurrentThreadId();PeekMessage(out _,0,0,0,0);
        try{hook=SetWindowsHookEx(14,callback,GetModuleHandle(null),0);if(hook==0)throw new Win32Exception();ready.SetResult();while(GetMessage(out var m,0,0,0)>0){TranslateMessage(ref m);DispatchMessage(ref m);}}
        catch(Exception e){ready.TrySetException(e);}finally{if(hook!=0)UnhookWindowsHookEx(hook);}
    }
    private nint Handle(int code,nuint message,nint data)
    {
        if(code>=0)try
        {
            var value=Marshal.PtrToStructure<MouseHook>(data);var entry=new MouseEvent(Stopwatch.GetTimestamp(),(uint)message,new(value.Point.X,value.Point.Y),value.Flags,value.Extra,0);
            if(entry.Physical){if(message==0x200){Interlocked.Increment(ref physicalMoves);if(leftHeld)Interlocked.Increment(ref physicalDragMoves);}if(message==0x201){leftHeld=true;}if(message==0x202){leftHeld=false;}if(message is 0x201 or 0x204 or 0x207 or 0x20b)Interlocked.Increment(ref physicalDowns);if(message is 0x202 or 0x205 or 0x208 or 0x20c)Interlocked.Increment(ref physicalUps);}else Interlocked.Increment(ref injected);
            Events.Enqueue(entry);while(Events.Count>1024)Events.TryDequeue(out _);
        }catch{/* Observation failure must never consume hardware input. */}
        return CallNextHookEx(hook,code,message,data);
    }
    public void Dispose()
    {
        if(Volatile.Read(ref cleanupComplete)!=0)return;
        TouchThreadCleanup.Run(thread,()=>PostThreadMessage(threadId,0x12,0,0),()=>{GC.KeepAlive(callback);Volatile.Write(ref cleanupComplete,1);},"鼠标只读观察线程未退出");
    }
    private delegate nint HookProc(int code,nuint message,nint data);
    [StructLayout(LayoutKind.Sequential)]private struct MouseHook{public TouchNative.Point Point;public uint Data,Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)]private struct Message{public nint Hwnd;public uint Id;public nuint W;public nint L;public uint Time;public TouchNative.Point Point;public uint Private;}
    [DllImport("user32.dll",SetLastError=true)]private static extern nint SetWindowsHookEx(int id,HookProc callback,nint module,uint thread);
    [DllImport("user32.dll")]private static extern nint CallNextHookEx(nint hook,int code,nuint w,nint l);
    [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",EntryPoint="GetModuleHandleW",CharSet=CharSet.Unicode)]private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll",EntryPoint="PeekMessageW")]private static extern bool PeekMessage(out Message m,nint h,uint low,uint high,uint remove);
    [DllImport("user32.dll",EntryPoint="GetMessageW")]private static extern int GetMessage(out Message m,nint h,uint low,uint high);
    [DllImport("user32.dll")]private static extern bool TranslateMessage(ref Message m);
    [DllImport("user32.dll",EntryPoint="DispatchMessageW")]private static extern nint DispatchMessage(ref Message m);
    [DllImport("user32.dll",EntryPoint="PostThreadMessageW")]private static extern bool PostThreadMessage(uint thread,uint m,nuint w,nint l);
}
