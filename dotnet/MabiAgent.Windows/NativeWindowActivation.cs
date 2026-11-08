using System.Runtime.InteropServices;
namespace MabiAgent.Windows;
/// <summary>Host calls this only for explicit Start/Continue/Screenshot. Never called by ticks.</summary>
public static class NativeWindowActivation
{
    public static void Activate(NativeWindowState expected)
    {
        var actual=new NativeWindowsOps().ReadWindow(expected.WindowId);
        if(!GameWindowSelection.IsGame(actual)||actual.ProcessId!=expected.ProcessId||actual.ProcessStartUtcTicks!=expected.ProcessStartUtcTicks||actual.Executable!=expected.Executable)throw new InputBlockedException("游戏进程已变化");
        if(actual.ForegroundId==expected.WindowId)return;
        if(actual.Minimized)ShowWindow(expected.WindowId,9);
        if(!SetForegroundWindow(expected.WindowId))throw new InputBlockedException("请手动切回游戏");
        WaitForForeground(()=>new NativeWindowsOps().ReadWindow(expected.WindowId).ForegroundId==expected.WindowId,()=>Thread.Sleep(25));
    }
    internal static void WaitForForeground(Func<bool> isForeground,Action wait)
    {
        for(var attempt=0;attempt<20;attempt++){if(isForeground())return;wait();}
        if(!isForeground())throw new InputBlockedException("游戏尚未切到前台，请点击继续");
    }
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")]private static extern bool ShowWindow(nint hwnd,int command);
}
