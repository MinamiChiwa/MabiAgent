using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using MabiAgent.Windows;
namespace MabiAgent.Windows.Tests;

// Read-only system boundaries; no activation, cursor movement, hooks or process memory.
internal static class WindowMessageDiagnostics
{
    internal static JsonObject Read(NativeWindowState state,PixelPoint? target=null)
    {
        var previous=SetThreadDpiAwarenessContext(-4);if(previous==0)throw new InvalidOperationException("无法核实物理坐标");
        try
        {
            var tid=GetWindowThreadProcessId(state.WindowId,out var pid);var gui=new GuiInfo{Size=(uint)Marshal.SizeOf<GuiInfo>()};
            var guiKnown=GetGUIThreadInfo(tid,ref gui);var cursorClient=new Point{X=state.Cursor.X,Y=state.Cursor.Y};if(!ScreenToClient(state.WindowId,ref cursorClient))throw new InvalidOperationException("光标坐标转换失败");
            var children=new JsonArray();EnumChildWindows(state.WindowId,(h,_)=>{if(children.Count<32)children.Add(Describe(h));return true;},0);
            var root=new JsonObject{["at"]=DateTimeOffset.UtcNow.ToString("O"),["hwnd"]=(long)state.WindowId,["pid"]=pid,["thread"]=tid,["class"]=state.ClassName,["geometry"]=new JsonArray(state.Geometry.X,state.Geometry.Y,state.Geometry.Width,state.Geometry.Height,state.Geometry.Dpi),["cursor_screen"]=new JsonArray(state.Cursor.X,state.Cursor.Y),["cursor_client"]=new JsonArray(cursorClient.X,cursorClient.Y),["foreground"]=(long)state.ForegroundId,["last_active_popup"]=(long)GetLastActivePopup(state.WindowId),["children"]=children,["gui_known"]=guiKnown,["hung"]=IsHungAppWindow(state.WindowId)};
            if(guiKnown){root["active"]=(long)gui.Active;root["focus"]=(long)gui.Focus;root["capture"]=(long)gui.Capture;root["gui_flags"]=gui.Flags;}
            root["touch_registered"]=IsTouchWindow(state.WindowId,out var touchFlags);root["touch_flags"]=touchFlags;
            root["digitizer_status"]=GetSystemMetrics(94);root["maximum_touches"]=GetSystemMetrics(95);
            root["pointer_devices"]=PointerDevices();root["process_start_utc"]=new DateTime(state.ProcessStartUtcTicks,DateTimeKind.Utc).ToString("O");
            root["sender_integrity"]=Integrity(Environment.ProcessId);root["target_integrity"]=Integrity(state.ProcessId);
            if(GetClipCursor(out var clip))root["cursor_clip"]=new JsonArray(clip.Left,clip.Top,clip.Right,clip.Bottom);
            if(target is {} point)
            {
                var screen=new Point{X=point.X,Y=point.Y};if(!ClientToScreen(state.WindowId,ref screen))throw new InvalidOperationException("目标坐标转换失败");var roundtrip=screen;if(!ScreenToClient(state.WindowId,ref roundtrip))throw new InvalidOperationException("目标坐标回读失败");
                root["target_client"]=new JsonArray(point.X,point.Y);root["target_screen"]=new JsonArray(screen.X,screen.Y);root["roundtrip"]=new JsonArray(roundtrip.X,roundtrip.Y);root["child_at_target"]=(long)RealChildWindowFromPoint(state.WindowId,new(){X=point.X,Y=point.Y});root["window_over_target"]=Describe(WindowFromPoint(screen));
            }
            return root;
        }finally{SetThreadDpiAwarenessContext(previous);}
    }
    private static JsonObject Describe(nint hwnd){var name=new StringBuilder(256);GetClassName(hwnd,name,name.Capacity);GetWindowThreadProcessId(hwnd,out var pid);return new(){["hwnd"]=(long)hwnd,["pid"]=pid,["class"]=name.ToString()};}
    private static JsonObject PointerDevices()
    {
        // Enumeration is a current snapshot, not proof that the game saw these devices at startup.
        for(var attempt=0;attempt<2;attempt++)
        {
            uint count=0;if(!GetPointerDevices(ref count,0))return new(){["known"]=false,["error"]=Marshal.GetLastWin32Error()};
            if(count>128)return new(){["known"]=false,["reason"]="device_count_out_of_bounds",["count"]=count};
            if(count==0)return new(){["known"]=true,["devices"]=new JsonArray()};
            var devices=new PointerDeviceInfo[count];
            if(!GetPointerDevices(ref count,devices))
            {var error=Marshal.GetLastWin32Error();if(error==122&&attempt==0)continue;return new(){["known"]=false,["error"]=error};}
            if(count>devices.Length)return new(){["known"]=false,["reason"]="device_count_changed"};
            return new(){["known"]=true,["devices"]=new JsonArray(devices.Take((int)count).Select(d=>(JsonNode)new JsonObject{["handle"]=(long)d.Device,["type"]=d.Type,["orientation"]=d.Orientation,["contacts"]=d.MaxContacts,["product"]=d.Product}).ToArray())};
        }
        return new(){["known"]=false,["reason"]="device_enumeration_changed"};
    }
    private static int? Integrity(int pid)
    {
        var process=OpenProcess(0x1000,false,pid);if(process==0)return null;
        nint token=0,data=0;
        try
        {
            if(!OpenProcessToken(process,8,out token))return null;
            GetTokenInformation(token,25,0,0,out var length);if(length<=0||length>65536)return null;
            data=Marshal.AllocHGlobal(length);if(!GetTokenInformation(token,25,data,length,out _))return null;
            var sid=Marshal.ReadIntPtr(data);var count=Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return count==0?null:Marshal.ReadInt32(GetSidSubAuthority(sid,(uint)count-1));
        }
        finally{if(data!=0)Marshal.FreeHGlobal(data);if(token!=0)CloseHandle(token);CloseHandle(process);}
    }
    [DllImport("kernel32.dll")]private static extern nint OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll")]private static extern bool OpenProcessToken(nint process,uint access,out nint token);
    [DllImport("advapi32.dll")]private static extern bool GetTokenInformation(nint token,int information,nint value,int length,out int required);
    [DllImport("advapi32.dll")]private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")]private static extern nint GetSidSubAuthority(nint sid,uint index);
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct GuiInfo{public uint Size,Flags;public nint Active,Focus,Capture,MenuOwner,MoveSize,Caret;public Rect CaretRect;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct PointerDeviceInfo
    {public uint Orientation;public nint Device;public uint Type;public nint Monitor;public uint StartingCursorId;public ushort MaxContacts;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=520)]public string? Product;}
    private delegate bool EnumProc(nint h,nint l);
    [DllImport("user32.dll")]private static extern bool GetGUIThreadInfo(uint tid,ref GuiInfo info);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(nint hwnd,EnumProc proc,nint data);
    [DllImport("user32.dll",EntryPoint="GetClassNameW",CharSet=CharSet.Unicode)]private static extern int GetClassName(nint hwnd,StringBuilder name,int size);
    [DllImport("user32.dll")]private static extern nint GetLastActivePopup(nint hwnd);
    [DllImport("user32.dll")]private static extern nint RealChildWindowFromPoint(nint hwnd,Point p);
    [DllImport("user32.dll")]private static extern nint WindowFromPoint(Point p);
    [DllImport("user32.dll")]private static extern bool ScreenToClient(nint hwnd,ref Point p);
    [DllImport("user32.dll")]private static extern bool ClientToScreen(nint hwnd,ref Point p);
    [DllImport("user32.dll")]private static extern bool IsHungAppWindow(nint hwnd);
    [DllImport("user32.dll")]private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")]private static extern bool GetClipCursor(out Rect rect);
    [DllImport("user32.dll")]private static extern bool IsTouchWindow(nint hwnd,out uint flags);
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool GetPointerDevices(ref uint count,nint devices);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool GetPointerDevices(ref uint count,[Out] PointerDeviceInfo[] devices);
}
