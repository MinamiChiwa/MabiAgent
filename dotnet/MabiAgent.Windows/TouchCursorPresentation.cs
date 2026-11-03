using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using MabiAgent.Windows;
namespace MabiAgent.Windows;

internal readonly record struct TouchCursorState(uint Flags,PixelPoint Position,nint Foreground)
{
    internal bool Showing=>(Flags&1)!=0;
    internal bool Suppressed=>(Flags&2)!=0;
    internal JsonObject ToJson()=>new(){["flags"]=Flags,["showing"]=Showing,["suppressed"]=Suppressed,["point"]=new JsonArray(Position.X,Position.Y),["foreground"]=(long)Foreground};
}

// No ShowCursor or button writes. Current-pixel absolute mode is idle-only;
// active physical mouse movement must never be overwritten by a stale position.
internal static class TouchCursorPresentation
{
    internal static readonly nuint Marker=0x4d414254;
    internal static TouchCursorState Read()
    {
        using var dpi=TouchNative.PhysicalDpi();var info=new CursorInfo{Size=(uint)Marshal.SizeOf<CursorInfo>()};
        if(!GetCursorInfo(ref info))throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(info.Flags,new(info.Position.X,info.Position.Y),TouchNative.Foreground);
    }
    internal static JsonObject Restore(bool originalMousePresentation,Action authorize,string? explicitMode=null)
    {
        using var dpi=TouchNative.PhysicalDpi();
        var before=Read();var mode=explicitMode??Environment.GetEnvironmentVariable("MABI_TOUCH_CURSOR_RESTORE")??"relative_zero";var result=new JsonObject{["before"]=before.ToJson(),["mode"]=mode,["submitted"]=false};
        if(!originalMousePresentation||!before.Suppressed){result["reason"]="no_touch_suppression_to_restore";result["after"]=before.ToJson();return result;}
        authorize();var latest=Read();if(latest.Position!=before.Position||latest.Foreground!=before.Foreground){result["reason"]="user_activity_before_notification";result["after"]=latest.ToJson();return result;}var timer=Stopwatch.StartNew();
        var data=new MouseData{Flags=mode=="relative_no_coalesce"?0x2001u:1u,Extra=Marker};
        if(mode=="absolute_same_pixel")
        {
            var left=GetSystemMetrics(76);var top=GetSystemMetrics(77);var width=GetSystemMetrics(78);var height=GetSystemMetrics(79);
            if(width<=0||height<=0)throw new InputBlockedException("虚拟桌面范围无效");
            data.X=(int)Math.Floor((before.Position.X-left+.5)*65536d/width);data.Y=(int)Math.Floor((before.Position.Y-top+.5)*65536d/height);data.Flags=0xe001;
        }
        else if(mode is not ("relative_zero" or "relative_no_coalesce"))throw new ArgumentException("未知光标恢复候选");
        var packet=new MousePacket{Type=0,Mouse=data};
        var sent=SendInput(1,[packet],Marshal.SizeOf<MousePacket>());result["submitted"]=sent==1;result["error"]=sent==1?0:Marshal.GetLastWin32Error();
        if(sent!=1)throw new InputOutcomeUnknownException("零位移光标恢复未确认，禁止重发");
        TouchCursorState after;do{after=Read();if(!after.Suppressed)break;Thread.Sleep(2);}while(timer.ElapsedMilliseconds<100);
        if(after.Position!=before.Position||after.Foreground!=before.Foreground)throw new InputOutcomeUnknownException("光标呈现通知后位置或前台变化，停止诊断，不重发");
        result["after"]=after.ToJson();result["elapsed_ms"]=timer.Elapsed.TotalMilliseconds;result["suppression_cleared"]=!after.Suppressed;result["visible_restored"]=after.Showing&&!after.Suppressed;
        return result;
    }
    [StructLayout(LayoutKind.Sequential)]private struct CursorInfo{public uint Size,Flags;public nint Cursor;public TouchNative.Point Position;}
    [StructLayout(LayoutKind.Sequential)]private struct MouseData{public int X,Y;public uint Data,Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Explicit,Size=40)]private struct MousePacket{[FieldOffset(0)]public uint Type;[FieldOffset(8)]public MouseData Mouse;}
    [DllImport("user32.dll",SetLastError=true)]private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll",SetLastError=true)]private static extern uint SendInput(uint count,MousePacket[] inputs,int size);
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
}
