using System.Diagnostics;
using System.Runtime.InteropServices;
namespace MabiAgent.Windows;
internal readonly record struct PhysicalInputSnapshot(PixelPoint Position,nint Foreground,PixelBox Clip,bool ButtonsDown,bool KeyboardDown,uint InputTick);
internal sealed class PhysicalInputReader
{
    private readonly NativeWindowsOps native=new();
    public double Milliseconds=>Stopwatch.GetTimestamp()*1000d/Stopwatch.Frequency;
    public void Sleep(int milliseconds)=>Thread.Sleep(milliseconds);
    public PhysicalInputSnapshot Read()
    {
        var previous=SetThreadDpiAwarenessContext(-4);if(previous==0)throw new InputBlockedException("无法核验物理鼠标坐标");
        try
        {
            var input=new LastInput{Size=(uint)Marshal.SizeOf<LastInput>()};
            if(!GetCursorPos(out var cursor)||!GetClipCursor(out var clip)||!GetLastInputInfo(ref input))throw new InputBlockedException("无法核验物理输入状态");
            var buttons=new[]{1,2,4,5,6}.Any(k=>(GetAsyncKeyState(k)&0x8000)!=0);
            return new(new(cursor.X,cursor.Y),GetForegroundWindow(),new(clip.Left,clip.Top,clip.Right,clip.Bottom),buttons,native.AnyPhysicalKeyDown(),input.Tick);
        }
        finally{SetThreadDpiAwarenessContext(previous);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct LastInput{public uint Size,Tick;}
    [DllImport("user32.dll")]private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]private static extern bool GetClipCursor(out Rect rect);
    [DllImport("user32.dll")]private static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
}
