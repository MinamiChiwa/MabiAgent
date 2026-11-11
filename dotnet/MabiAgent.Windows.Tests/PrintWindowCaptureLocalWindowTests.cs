using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MabiAgent.Windows.CaptureFixture;
using MabiAgent.Windows;
using Xunit;

namespace MabiAgent.Windows.Tests;

public sealed class PrintWindowCaptureLocalWindowTests
{
    [Fact]
    public void CapturesLocalWindowWithPrintFlagsThree()
    {
        if(!OperatingSystem.IsWindows())return;
        using var window=new LocalWindow();using var native=new NativeWindowsOps();var state=native.ReadWindow(window.Hwnd);
        var image=native.CaptureClient(state);Assert.Equal(100,image.Width);Assert.Equal(100,image.Height);var pixels=image.CopyPixels();Assert.Equal(17,pixels[0]);Assert.Equal(34,pixels[1]);Assert.Equal(51,pixels[2]);
    }

    [Fact]
    public void HelperExitsWhenParentIdentityIsAlreadyGone()
    {
        if(!OperatingSystem.IsWindows())return;
        var desktop=FindDesktopDll();Assert.True(File.Exists(desktop));var pipe="mabiagent-parent-test-"+Guid.NewGuid().ToString("N");
        using var process=Process.Start(DesktopLauncher(desktop,pipe,999999,1))!;Assert.True(process.WaitForExit(3000));
    }

    [Fact]
    public void HelperIsReapedWhenParentDiesDuringBlockedPrintWindow()
    {
        if(!OperatingSystem.IsWindows())return;
        using var window=new LocalWindow{BlockPrint=true};var native=new NativeWindowsOps();var state=native.ReadWindow(window.Hwnd);native.Dispose();
        var report=Path.Combine(Path.GetTempPath(),"mabi-capture-helper-"+Guid.NewGuid().ToString("N")+".pid");
        var info=new ProcessStartInfo{FileName=DotnetHost(),UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};info.ArgumentList.Add(typeof(Program).Assembly.Location);info.ArgumentList.Add("--capture-owner");info.ArgumentList.Add(report);info.ArgumentList.Add(((long)state.WindowId).ToString());info.ArgumentList.Add(state.ProcessId.ToString());info.ArgumentList.Add(state.ProcessStartUtcTicks.ToString());info.ArgumentList.Add(state.Geometry.X.ToString());info.ArgumentList.Add(state.Geometry.Y.ToString());info.ArgumentList.Add(state.Geometry.Width.ToString());info.ArgumentList.Add(state.Geometry.Height.ToString());info.ArgumentList.Add(state.Geometry.Dpi.ToString());
        using var owner=Process.Start(info)!;var ownerErrors=new StringBuilder();owner.ErrorDataReceived+=(_,eventArgs)=>{if(eventArgs.Data is{} line)lock(ownerErrors)ownerErrors.AppendLine(line);};owner.BeginErrorReadLine();try
        {
            var reported=SpinWait.SpinUntil(()=>File.Exists(report),TimeSpan.FromSeconds(7));if(!reported)owner.WaitForExit(1000);Assert.True(reported,ownerErrors.ToString());var helperPid=int.Parse(File.ReadAllText(report));owner.Kill();Assert.True(owner.WaitForExit(3000));
            Assert.True(SpinWait.SpinUntil(()=>!IsAlive(helperPid),TimeSpan.FromSeconds(3)));
        }
        finally{window.BlockPrint=false;try{File.Delete(report);}catch{}}
    }

    private static ProcessStartInfo DesktopLauncher(string desktop,string pipe,int pid,long ticks)
    {
        var info=new ProcessStartInfo{FileName=DotnetHost(),UseShellExecute=false,CreateNoWindow=true};info.ArgumentList.Add(desktop);info.ArgumentList.Add("--capture-helper");info.ArgumentList.Add(pipe);info.ArgumentList.Add(pid.ToString());info.ArgumentList.Add(ticks.ToString());return info;
    }
    private static ProcessStartInfo FixtureLauncher(string fixture,string pipe,int pid,long ticks)
    {
        var info=new ProcessStartInfo{FileName=DotnetHost(),UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};info.ArgumentList.Add(fixture);info.ArgumentList.Add("--capture-helper");info.ArgumentList.Add(pipe);info.ArgumentList.Add(pid.ToString());info.ArgumentList.Add(ticks.ToString());return info;
    }
    private static string DotnetHost(){var configured=Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");if(!string.IsNullOrWhiteSpace(configured)&&File.Exists(configured))return configured;var standard=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet","dotnet.exe");return File.Exists(standard)?standard:"dotnet";}
    private static bool IsAlive(int pid){try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch{return false;}}
    private static string FindDesktopDll()
    {
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
        {
            var path=Path.Combine(directory.FullName,"dotnet","MabiAgent.Desktop","bin","Debug","net10.0-windows","MabiAgent.Desktop.dll");if(File.Exists(path))return path;
            path=Path.Combine(directory.FullName,"dotnet","MabiAgent.Desktop","bin","Debug","net10.0","MabiAgent.Desktop.dll");if(File.Exists(path))return path;
        }
        return Path.Combine(AppContext.BaseDirectory,"MabiAgent.Desktop.dll");
    }

    private sealed class LocalWindow:IDisposable
    {
        private const string ClassName="MabiPrintWindowIsolation";private readonly Thread thread;private readonly TaskCompletionSource<nint> ready=new(TaskCreationOptions.RunContinuationsAsynchronously);private readonly WndProc callback;private nint hwnd;private volatile bool block;
        public ManualResetEventSlim PrintEntered{get;}=new(false);public int LastPrintFlags{get;private set;}public bool BlockPrint{get=>block;set=>block=value;}public nint Hwnd=>ready.Task.GetAwaiter().GetResult();public ClientGeometry Geometry{get;private set;}=new(0,0,10,10,96);public PixelBox WindowRect{get;private set;}=new(0,0,10,10);
        public LocalWindow(){callback=WndProcImpl;thread=new Thread(Run){IsBackground=true};thread.Start();_=Hwnd;}
        private void Run()
        {
            var module=GetModuleHandle(null);try{var c=new WindowClass{Size=(uint)Marshal.SizeOf<WindowClass>(),Instance=module,Name=ClassName,Procedure=Marshal.GetFunctionPointerForDelegate(callback)};if(RegisterClassEx(ref c)==0)throw new InvalidOperationException();hwnd=CreateWindowEx(0x00000080|0x08000000,ClassName,"print-window-test",0x80000000|0x10000000,300,200,100,100,0,0,module,0);if(hwnd==0)throw new InvalidOperationException();InvalidateRect(hwnd,0,true);UpdateWindow(hwnd);GetWindowRect(hwnd,out var rect);var origin=new Point();ClientToScreen(hwnd,ref origin);Geometry=new(origin.X,origin.Y,100,100,(int)GetDpiForWindow(hwnd));WindowRect=new(rect.Left,rect.Top,rect.Right,rect.Bottom);ready.SetResult(hwnd);while(GetMessage(out var message,0,0,0)>0){TranslateMessage(ref message);DispatchMessage(ref message);}}
            catch(Exception error){ready.TrySetException(error);}finally{if(hwnd!=0)DestroyWindow(hwnd);UnregisterClass(ClassName,module);}
        }
        private nint WndProcImpl(nint window,uint message,nuint wParam,nint lParam)
        {if(message==0x000F){var ps=new PaintStruct();var dc=BeginPaint(window,out ps);var brush=CreateSolidBrush(0x00332211);var rect=new Rect{Right=100,Bottom=100};FillRect(dc,ref rect,brush);DeleteObject(brush);EndPaint(window,ref ps);return 0;}if(message is 0x0317 or 0x0318){LastPrintFlags=unchecked((int)lParam);PrintEntered.Set();while(block)Thread.Sleep(5);var dc=unchecked((nint)(long)wParam);var brush=CreateSolidBrush(0x00332211);var rect=new Rect{Right=100,Bottom=100};FillRect(dc,ref rect,brush);DeleteObject(brush);return 1;}if(message==0x0010){DestroyWindow(window);return 0;}if(message==0x0002){PostQuitMessage(0);return 0;}return DefWindowProc(window,message,wParam,lParam);}
        public void Dispose(){block=false;PrintEntered.Set();if(hwnd!=0)PostMessage(hwnd,0x0010,0,0);if(!thread.Join(3000))throw new TimeoutException();PrintEntered.Dispose();GC.KeepAlive(callback);}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct WindowClass{public uint Size,Style;public nint Procedure;public int ClassExtra,WindowExtra;public nint Instance,Icon,Cursor,Background;public string? Menu,Name;public nint SmallIcon;}
        [StructLayout(LayoutKind.Sequential)]private struct PaintStruct{public nint Dc;public int Erase;public Rect Paint;public int Restore,IncUpdate;[MarshalAs(UnmanagedType.ByValArray,SizeConst=32)]public byte[] Reserved;}
        [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}[StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
        private delegate nint WndProc(nint h,uint m,nuint w,nint l);
        [DllImport("kernel32.dll",EntryPoint="GetModuleHandleW",CharSet=CharSet.Unicode)]private static extern nint GetModuleHandle(string? name);[DllImport("user32.dll",EntryPoint="RegisterClassExW",CharSet=CharSet.Unicode)]private static extern ushort RegisterClassEx(ref WindowClass c);[DllImport("user32.dll",EntryPoint="CreateWindowExW",CharSet=CharSet.Unicode)]private static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint param);[DllImport("user32.dll")]private static extern bool GetWindowRect(nint hwnd,out Rect rect);[DllImport("user32.dll")]private static extern bool ClientToScreen(nint hwnd,ref Point p);[DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint hwnd);[DllImport("user32.dll",EntryPoint="GetMessageW")]private static extern int GetMessage(out Message m,nint h,uint min,uint max);[DllImport("user32.dll")]private static extern bool TranslateMessage(ref Message m);[DllImport("user32.dll",EntryPoint="DispatchMessageW")]private static extern nint DispatchMessage(ref Message m);[DllImport("user32.dll",EntryPoint="DefWindowProcW")]private static extern nint DefWindowProc(nint h,uint m,nuint w,nint l);[DllImport("user32.dll")]private static extern bool DestroyWindow(nint h);[DllImport("user32.dll",EntryPoint="UnregisterClassW",CharSet=CharSet.Unicode)]private static extern bool UnregisterClass(string cls,nint instance);[DllImport("user32.dll")]private static extern bool PostMessage(nint h,uint m,nuint w,nint l);[DllImport("user32.dll")]private static extern void PostQuitMessage(int code);[DllImport("user32.dll")]private static extern bool InvalidateRect(nint h,nint rect,bool erase);[DllImport("user32.dll")]private static extern bool UpdateWindow(nint h);[DllImport("user32.dll")]private static extern nint BeginPaint(nint h,out PaintStruct ps);[DllImport("user32.dll")]private static extern bool EndPaint(nint h,ref PaintStruct ps);[DllImport("gdi32.dll")]private static extern nint CreateSolidBrush(uint color);[DllImport("user32.dll")]private static extern int FillRect(nint dc,ref Rect rect,nint brush);[DllImport("gdi32.dll")]private static extern bool DeleteObject(nint obj);
        [StructLayout(LayoutKind.Sequential)]private struct Message{public nint Window;public uint Id;public nuint WParam;public nint LParam;public uint Time;public Point Point;public uint Private;}
    }
}
