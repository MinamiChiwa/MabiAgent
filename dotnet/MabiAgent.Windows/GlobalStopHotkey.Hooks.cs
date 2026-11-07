using System.ComponentModel;
using System.Runtime.InteropServices;
namespace MabiAgent.Windows;

public sealed partial class GlobalStopHotkey
{
    private readonly HookCallback keyboardCallback,mouseCallback;
    private nint keyboardHook,mouseHook;
    private readonly HashSet<uint> held=[];
    private uint modifierCandidate;
    private void InstallHooks()
    {
        keyboardHook=SetWindowsHookEx(13,keyboardCallback,GetModuleHandle(null),0);
        if(keyboardHook==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"无法监听全局键盘停止键");
        mouseHook=SetWindowsHookEx(14,mouseCallback,GetModuleHandle(null),0);
        if(mouseHook==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"无法监听全局鼠标停止键");
    }
    private void RemoveHooks(){if(keyboardHook!=0)UnhookWindowsHookEx(keyboardHook);if(mouseHook!=0)UnhookWindowsHookEx(mouseHook);}
    private nint KeyboardHook(int code,nuint message,nint data)
    {
        if(code>=0)try
        {
            var entry=Marshal.PtrToStructure<KeyboardEntry>(data);
            ObserveKeyboard((uint)message,entry.Key,entry.Flags,CurrentModifiers(),StopHotkeyEditing.IsSuppressed());
        }catch(Exception error){report(error);}
        return CallNextHookEx(keyboardHook,code,message,data);
    }
    private nint MouseHook(int code,nuint message,nint data)
    {
        if(code>=0)try
        {
            var entry=Marshal.PtrToStructure<MouseEntry>(data);
            ObserveMouse((uint)message,entry.Data,entry.Flags,entry.Extra,CurrentModifiers(),StopHotkeyEditing.IsSuppressed(true));
        }catch(Exception error){report(error);}
        return CallNextHookEx(mouseHook,code,message,data);
    }
    internal static uint MouseKey(uint message,uint data)=>message switch
    {
        0x201=>1,0x204=>2,0x207=>4,0x20b=>(data>>16)==1?5u:6u,
        0x20a=>unchecked((short)(data>>16))>0?0x100u:0x101u,
        0x20e=>unchecked((short)(data>>16))>0?0x103u:0x102u,_=>0
    };
    internal void ObserveKeyboard(uint message,uint key,uint flags,uint modifiers,bool editing)
    {
        if((flags&0x10)==0&&message is 0x100 or 0x104 or 0x101 or 0x105)
            HandlePhysicalKey(key,message is 0x101 or 0x105,modifiers,editing);
    }
    internal void ObserveMouse(uint message,uint data,uint flags,nuint extra,uint modifiers,bool editing)
    {
        if((flags&1)!=0||(extra&0xffffff00u)==0xff515700u)return;
        var key=MouseKey(message,data);
        if(key!=0){modifierCandidate=0;HandlePhysicalMouse(key,modifiers,editing);}
    }
    internal void HandlePhysicalKey(uint key,bool released,uint modifiers,bool editing)
    {
        var binding=active;
        if(released)
        {
            held.Remove(key);
            if(editing)StopHotkeyEditing.PublishKey(key,true,modifiers);
            if(modifierCandidate==key)
            {
                modifierCandidate=0;
                if(binding is not null&&binding.Key==key&&binding.Modifiers==(modifiers&~StopHotkey.Modifier(key))&&!editing)FireStop();
            }
            return;
        }
        if(!held.Add(key))return;
        modifierCandidate=0;
        if(editing)StopHotkeyEditing.PublishKey(key,false,modifiers);
        if(binding is null||editing)return;
        if(StopHotkey.Modifier(key)!=0){if(binding.Key==key&&binding.Modifiers==(modifiers&~StopHotkey.Modifier(key)))modifierCandidate=key;return;}
        if(!binding.IsMouse&&binding.Key==key&&binding.Modifiers==modifiers)FireStop();
    }
    internal void HandlePhysicalMouse(uint key,uint modifiers,bool editing)
    {
        if(active is {IsMouse:true} binding&&binding.Key==key&&binding.Modifiers==modifiers&&!editing)FireStop();
    }
    private void FireStop(){try{stop();}catch(Exception error){report(error);}}
    private static uint CurrentModifiers()=>((GetAsyncKeyState(0x11)&0x8000)!=0?2u:0)|((GetAsyncKeyState(0x12)&0x8000)!=0?1u:0)|((GetAsyncKeyState(0x10)&0x8000)!=0?4u:0)|(((GetAsyncKeyState(0x5b)|GetAsyncKeyState(0x5c))&0x8000)!=0?8u:0);
    private delegate nint HookCallback(int code,nuint message,nint data);
    [StructLayout(LayoutKind.Sequential)]private struct KeyboardEntry{public uint Key,Scan,Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)]private struct MouseEntry{public int X,Y;public uint Data,Flags,Time;public nuint Extra;}
    [DllImport("user32.dll",SetLastError=true)]private static extern nint SetWindowsHookEx(int id,HookCallback callback,nint module,uint thread);
    [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]private static extern nint CallNextHookEx(nint hook,int code,nuint message,nint data);
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll",EntryPoint="GetModuleHandleW",CharSet=CharSet.Unicode)]private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")]private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll",EntryPoint="DispatchMessageW")]private static extern nint DispatchMessage(ref Message message);
}

// Snapshot the editor's physical bounds on the UI thread. Hooks consult only
// immutable snapshots, including before the first click reaches the control.
public static class StopHotkeyEditing
{
    public static event Action<uint,bool,uint>? KeyCaptured;
    internal static void PublishKey(uint key,bool released,uint modifiers)=>KeyCaptured?.Invoke(key,released,modifiers);
    private sealed record Region(nint Window,int Left,int Top,int Right,int Bottom,bool KeyboardFocus);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<object,Region> regions=new();
    public static void Update(object owner,nint window,int left,int top,int right,int bottom,bool keyboardFocus)=>regions[owner]=new(window,left,top,right,bottom,keyboardFocus);
    public static void Remove(object owner)=>regions.TryRemove(owner,out _);
    internal static bool IsSuppressed(bool pointerInput=false)
    {
        if(regions.IsEmpty)return false;
        GetCursorPos(out var point);
        if(pointerInput)
        {
            var target=GetAncestor(WindowFromPoint(point),2);
            if(regions.Values.Any(r=>r.Window==target&&point.X>=r.Left&&point.X<r.Right&&point.Y>=r.Top&&point.Y<r.Bottom))return true;
        }
        return IsSuppressed(GetForegroundWindow(),point.X,point.Y);
    }
    internal static bool IsSuppressed(nint foreground,int x,int y)=>regions.Values.Any(r=>r.Window==foreground&&(r.KeyboardFocus||x>=r.Left&&x<r.Right&&y>=r.Top&&y<r.Bottom));
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")]private static extern nint GetAncestor(nint window,uint flags);
}
