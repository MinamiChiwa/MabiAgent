using System.Runtime.InteropServices;
using System.Text;
namespace MabiAgent.Windows;
public sealed record GameWindow(NativeWindowState State,string Title)
{public string Label=>$"{Title} · {State.Geometry.Width}×{State.Geometry.Height} · 进程 {State.ProcessId}";}

// All UI modules share this selection. Discovery never activates or sends input.
public sealed class GameWindowSelection(Func<IReadOnlyList<GameWindow>> discover)
{
    public IReadOnlyList<GameWindow> Available {get;private set;}=[];
    public GameWindow? Selected {get;private set;}
    public event Action<GameWindow?>? Changed;
    public void Refresh()
    {
        Available=discover().Where(w=>IsGame(w.State)).ToArray();
        var next=Available.FirstOrDefault(w=>Selected is not null&&Same(w,Selected));
        if(next is null&&Available.Count==1)next=Available[0];
        Set(next);
    }
    public void Select(GameWindow? value)
    {
        if(value is not null&&!Available.Any(w=>Same(w,value)))throw new InputBlockedException("游戏窗口已变化，请刷新");
        Set(value);
    }
    private void Set(GameWindow? next)
    {var changed=Selected!=next;Selected=next;if(changed)Changed?.Invoke(next);}
    private static bool Same(GameWindow a,GameWindow b)=>a.State.WindowId==b.State.WindowId&&a.State.ProcessId==b.State.ProcessId
        &&a.State.ProcessStartUtcTicks==b.State.ProcessStartUtcTicks&&string.Equals(a.State.Executable,b.State.Executable,StringComparison.OrdinalIgnoreCase);
    public static bool IsGame(NativeWindowState s)=>s.Visible&&s.ClassName=="UnityWndClass"
        &&string.Equals(Path.GetFileName(s.Executable),"mabinogimobile.exe",StringComparison.OrdinalIgnoreCase);
}

public static class GameWindowDiscovery
{
    public static IReadOnlyList<GameWindow> List()
    {
        if(!OperatingSystem.IsWindows())return [];
        List<GameWindow> result=[];var ops=new NativeWindowsOps();
        EnumCallback callback=(hwnd,_)=>
        {
            try
            {
                if(!IsWindowVisible(hwnd))return true;
                var cls=new StringBuilder(256);GetClassName(hwnd,cls,cls.Capacity);
                if(cls.ToString()!="UnityWndClass")return true;
                var state=ops.ReadWindow(hwnd);if(!GameWindowSelection.IsGame(state))return true;
                var title=new StringBuilder(512);GetWindowText(hwnd,title,title.Capacity);
                result.Add(new(state,title.Length>0?title.ToString():"Mabinogi Mobile"));
            }
            catch(Exception e) when(e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or InputBlockedException) { }
            return true;
        };
        if(!EnumWindows(callback,0))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(callback);return result;
    }
    private delegate bool EnumCallback(nint hwnd,nint parameter);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool EnumWindows(EnumCallback callback,nint parameter);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(nint hwnd,StringBuilder value,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(nint hwnd,StringBuilder value,int size);
}
