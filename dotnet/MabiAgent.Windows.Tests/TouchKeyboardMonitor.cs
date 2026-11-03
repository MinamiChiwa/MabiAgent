using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace MabiAgent.Windows.Tests;

// Test-only observer: never hides keyboards or changes Windows settings.
internal sealed class TouchKeyboardMonitor:IDisposable
{
    private readonly HashSet<int> processes=Process.GetProcesses().Where(p=>p.ProcessName is "TabTip" or "TextInputHost" or "osk").Select(p=>p.Id).ToHashSet();
    private readonly ConcurrentDictionary<nint,byte> seen=new();private readonly Timer timer;
    public nint[] VisibleWindows=>seen.Keys.ToArray();
    public TouchKeyboardMonitor(){Sample();timer=new(_=>Sample(),null,0,25);}
    private void Sample()
    {
        EnumWindows((h,_)=>{GetWindowThreadProcessId(h,out var pid);if(!processes.Contains((int)pid)||!IsWindowVisible(h))return true;
            DwmGetWindowAttribute(h,14,out var cloaked,4);if(cloaked!=0||!GetWindowRect(h,out var r)||r.Right-r.Left<100||r.Bottom-r.Top<100)return true;
            seen.TryAdd(h,0);return true;},0);
    }
    public void Dispose()=>timer.Dispose();
    private delegate bool Callback(nint hwnd,nint value);
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool EnumWindows(Callback callback,nint value);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint process);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint hwnd,out Rect rect);
    [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(nint hwnd,uint attribute,out int value,int size);
}
