using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace MabiAgent.Windows;

internal readonly record struct PrintWindowCaptureRequest(nint WindowId,int ProcessId,long ProcessStartUtcTicks,ClientGeometry Geometry);

/// <summary>Runs PrintWindow in a disposable process so a hung target cannot retain the workflow gate.</summary>
internal sealed class PrintWindowCaptureClient:IDisposable
{
    internal const int MaxPixels=16_777_216;
    internal const int MaxFrameBytes=MaxPixels*3;
    internal static readonly TimeSpan DefaultStartupTimeout=TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultFrameTimeout=TimeSpan.FromSeconds(3);
    private const uint RequestMagic=0x4D424341;
    private const uint ResponseMagic=0x4D424352;
    private const uint ProtocolVersion=1;
    private const int RequestBytes=48;
    private const int ResponseBytes=24;
    private readonly Func<string,int,long,ProcessStartInfo> launcher;
    private readonly Action<Process>? helperStarted;
    private readonly Action<Process>? helperReady;
    private readonly TimeSpan startupTimeout,frameTimeout;
    private readonly SemaphoreSlim gate=new(1,1);
    private NamedPipeServerStream? pipe;
    private Process? helper;
    private volatile bool disposed;
    private static Func<string,int,long,ProcessStartInfo>? testLauncher;

    internal static void ConfigureTestLauncher(Func<string,int,long,ProcessStartInfo> value)
    {ArgumentNullException.ThrowIfNull(value);if(Interlocked.CompareExchange(ref testLauncher,value,null) is not null)throw new InvalidOperationException("截图测试启动器已设置");}

    internal PrintWindowCaptureClient(Func<string,int,long,ProcessStartInfo>? launcher=null,TimeSpan? startupTimeout=null,TimeSpan? frameTimeout=null,Action<Process>? helperStarted=null,Action<Process>? helperReady=null)
    {
        this.launcher=launcher??Volatile.Read(ref testLauncher)??DefaultLauncher;
        this.helperStarted=helperStarted;
        this.helperReady=helperReady;
        this.startupTimeout=startupTimeout??DefaultStartupTimeout;
        this.frameTimeout=frameTimeout??DefaultFrameTimeout;
        if(this.startupTimeout<=TimeSpan.Zero||this.startupTimeout>TimeSpan.FromSeconds(30)||this.frameTimeout<=TimeSpan.Zero||this.frameTimeout>TimeSpan.FromSeconds(30))throw new ArgumentOutOfRangeException(nameof(frameTimeout));
    }

    internal byte[] Capture(PrintWindowCaptureRequest request,CancellationToken cancellationToken=default)
    {
        gate.Wait(cancellationToken);
        try{return CaptureAsync(request,cancellationToken).GetAwaiter().GetResult();}
        finally{gate.Release();}
    }

    private async Task<byte[]> CaptureAsync(PrintWindowCaptureRequest request,CancellationToken cancellationToken)
    {
        ThrowDisposed();Validate(request);
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(frameTimeout);
            var bytes=new byte[RequestBytes];var offset=0;
            WriteUInt32(bytes,ref offset,RequestMagic);WriteUInt32(bytes,ref offset,ProtocolVersion);
            WriteInt64(bytes,ref offset,request.WindowId.ToInt64());WriteInt32(bytes,ref offset,request.ProcessId);WriteInt64(bytes,ref offset,request.ProcessStartUtcTicks);
            WriteInt32(bytes,ref offset,request.Geometry.X);WriteInt32(bytes,ref offset,request.Geometry.Y);WriteInt32(bytes,ref offset,request.Geometry.Width);WriteInt32(bytes,ref offset,request.Geometry.Height);WriteInt32(bytes,ref offset,request.Geometry.Dpi);
            await WriteAsync(pipe!,bytes,deadline.Token).ConfigureAwait(false);
            var response=new byte[ResponseBytes];if(!await ReadExactAsync(pipe!,response,deadline.Token).ConfigureAwait(false))throw new EndOfStreamException("截图 helper 已断开");
            offset=0;if(ReadUInt32(response,ref offset)!=ResponseMagic||ReadUInt32(response,ref offset)!=ProtocolVersion)throw new InvalidDataException("截图 helper 协议版本不匹配");
            var status=ReadUInt32(response,ref offset);var width=ReadInt32(response,ref offset);var height=ReadInt32(response,ref offset);var length=ReadInt32(response,ref offset);
            if(status!=0){Abort();throw new InputBlockedException("隔离截图失败");}
            if(width!=request.Geometry.Width||height!=request.Geometry.Height||length<0||length>MaxFrameBytes||length!=(long)width*height*3)throw new InvalidDataException("截图 helper 返回帧长度无效");
            var frame=new byte[length];if(!await ReadExactAsync(pipe!,frame,deadline.Token).ConfigureAwait(false))throw new EndOfStreamException("截图 helper 帧不完整");
            return frame;
        }
        catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested){Abort();throw;}
        catch(OperationCanceledException){Abort();throw new InputBlockedException("隔离截图超时"){CaptureTimedOut=true};}
        catch(TimeoutException){Abort();throw new InputBlockedException("隔离截图超时"){CaptureTimedOut=true};}
        catch(Exception error)when(error is EndOfStreamException or InvalidDataException or IOException or InvalidOperationException)
        {Abort();throw new InputBlockedException("隔离截图失败");}
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if(pipe is {IsConnected:true}&&helper is {HasExited:false})return;
        Abort();
        var pipeName="mabiagent-capture-"+Guid.NewGuid().ToString("N");
        var candidate=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,64*1024,64*1024);
        var current=Process.GetCurrentProcess();var start=current.StartTime.ToUniversalTime().Ticks;
        pipe=candidate;
        try
        {
            var process=Process.Start(launcher(pipeName,current.Id,start))??throw new InvalidOperationException("无法启动截图 helper");helper=process;helperStarted?.Invoke(process);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(startupTimeout);
            var connected=candidate.WaitForConnectionAsync(deadline.Token);var exited=process.WaitForExitAsync();
            if(await Task.WhenAny(connected,exited).ConfigureAwait(false)!=connected)throw new InvalidOperationException("截图 helper 启动失败");
            await connected.ConfigureAwait(false);
            helperReady?.Invoke(process);
        }
        catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested){Abort();throw;}
        catch(OperationCanceledException){Abort();throw new TimeoutException("截图 helper 启动超时");}
        catch{Abort();throw;}
    }

    private static ProcessStartInfo DefaultLauncher(string pipeName,int parentPid,long parentStartTicks)
    {
        var assembly=System.Reflection.Assembly.GetEntryAssembly()??throw new InputBlockedException("无法定位桌面截图 helper 入口");
        if(assembly.GetName().Name!="MabiAgent.Desktop")throw new InputBlockedException("当前宿主未配置截图 helper 启动器");
        var processPath=Environment.ProcessPath??throw new InputBlockedException("无法定位桌面启动器");
        var info=new ProcessStartInfo{UseShellExecute=false,CreateNoWindow=true};
        if(Path.GetFileNameWithoutExtension(processPath).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
        {info.FileName=processPath;var assemblyPath=assembly.Location;if(!Path.GetExtension(assemblyPath).Equals(".dll",StringComparison.OrdinalIgnoreCase))throw new InputBlockedException("无法定位桌面程序集");info.ArgumentList.Add(assemblyPath);}
        else info.FileName=processPath;
        info.ArgumentList.Add("--capture-helper");info.ArgumentList.Add(pipeName);info.ArgumentList.Add(parentPid.ToString(System.Globalization.CultureInfo.InvariantCulture));info.ArgumentList.Add(parentStartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return info;
    }

    private void Abort()
    {
        var stream=Interlocked.Exchange(ref pipe,null);try{stream?.Dispose();}catch{}
        var process=Interlocked.Exchange(ref helper,null);if(process is null)return;
        try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch{}
        try{process.WaitForExit(1000);}catch{}process.Dispose();
    }
    public void Dispose(){if(disposed)return;disposed=true;Abort();}
    private void ThrowDisposed()=>ObjectDisposedException.ThrowIf(disposed,this);
    private static void Validate(PrintWindowCaptureRequest request){var geometry=request.Geometry;if(request.WindowId==0||request.ProcessId<=0||request.ProcessStartUtcTicks<=0||geometry.Width<=0||geometry.Height<=0||geometry.Dpi is <48 or >768||(long)geometry.Width*geometry.Height>MaxPixels)throw new InputBlockedException("截图请求无效");}
    private static async Task WriteAsync(Stream stream,byte[] data,CancellationToken token){await stream.WriteAsync(data,token).ConfigureAwait(false);await stream.FlushAsync(token).ConfigureAwait(false);}
    private static async Task<bool> ReadExactAsync(Stream stream,byte[] data,CancellationToken token){var offset=0;while(offset<data.Length){var count=await stream.ReadAsync(data.AsMemory(offset),token).ConfigureAwait(false);if(count==0)return false;offset+=count;}return true;}
    private static void WriteUInt32(byte[] data,ref int offset,uint value){BitConverter.TryWriteBytes(data.AsSpan(offset,4),value);offset+=4;}
    private static void WriteInt32(byte[] data,ref int offset,int value){BitConverter.TryWriteBytes(data.AsSpan(offset,4),value);offset+=4;}
    private static void WriteInt64(byte[] data,ref int offset,long value){BitConverter.TryWriteBytes(data.AsSpan(offset,8),value);offset+=8;}
    private static uint ReadUInt32(byte[] data,ref int offset){var value=BitConverter.ToUInt32(data,offset);offset+=4;return value;}
    private static int ReadInt32(byte[] data,ref int offset){var value=BitConverter.ToInt32(data,offset);offset+=4;return value;}
    private static long ReadInt64(byte[] data,ref int offset){var value=BitConverter.ToInt64(data,offset);offset+=8;return value;}
}

public static class PrintWindowCaptureEntry
{
    private const uint RequestMagic=0x4D424341;
    private const uint ResponseMagic=0x4D424352;
    private const uint ProtocolVersion=1;
    private const int RequestBytes=48;
    private const int ResponseBytes=24;
    public static bool TryRun(string[] args,out int exitCode)
    {
        var index=Array.IndexOf(args,"--capture-helper");if(index<0){exitCode=0;return false;}
        try
        {
            if(index+3>=args.Length)throw new ArgumentException("截图 helper 参数不完整");
            exitCode=RunAsync(args[index+1],int.Parse(args[index+2],System.Globalization.CultureInfo.InvariantCulture),long.Parse(args[index+3],System.Globalization.CultureInfo.InvariantCulture)).GetAwaiter().GetResult();
        }
        catch{exitCode=2;}
        return true;
    }
    private static async Task<int> RunAsync(string pipeName,int parentPid,long parentStartTicks)
    {
        using var monitor=new ParentMonitor(parentPid,parentStartTicks);
        using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000).ConfigureAwait(false);
        var request=new byte[RequestBytes];
        while(await ReadExactAsync(pipe,request).ConfigureAwait(false))
        {
            var offset=0;if(ReadUInt32(request,ref offset)!=RequestMagic||ReadUInt32(request,ref offset)!=ProtocolVersion)break;
            var hwnd=new nint(ReadInt64(request,ref offset));var pid=ReadInt32(request,ref offset);var created=ReadInt64(request,ref offset);var geometry=new ClientGeometry(ReadInt32(request,ref offset),ReadInt32(request,ref offset),ReadInt32(request,ref offset),ReadInt32(request,ref offset),ReadInt32(request,ref offset));
            byte[]? frame=null;uint status=0;
            try{frame=Capture(hwnd,pid,created,geometry);}
            catch{status=1;}
            var response=new byte[ResponseBytes];offset=0;WriteUInt32(response,ref offset,ResponseMagic);WriteUInt32(response,ref offset,ProtocolVersion);WriteUInt32(response,ref offset,status);WriteInt32(response,ref offset,geometry.Width);WriteInt32(response,ref offset,geometry.Height);WriteInt32(response,ref offset,frame?.Length??0);
            await pipe.WriteAsync(response).ConfigureAwait(false);if(frame is not null)await pipe.WriteAsync(frame).ConfigureAwait(false);await pipe.FlushAsync().ConfigureAwait(false);
        }
        return 0;
    }
    private static byte[] Capture(nint hwnd,int expectedPid,long expectedCreated,ClientGeometry expectedGeometry)
    {
        using var dpi=new DpiContext();
        var width=expectedGeometry.Width;var height=expectedGeometry.Height;
        if(hwnd==0||expectedPid<=0||expectedCreated<=0||width<=0||height<=0||(long)width*height>PrintWindowCaptureClient.MaxPixels)throw new InvalidDataException();
        if(GetWindowThreadProcessId(hwnd,out var pid)==0||pid!=(uint)expectedPid)throw new InputBlockedException("截图窗口进程已变化");
        using var process=OpenProcess(0x1000,false,pid);if(process.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        if(!GetProcessTimes(process,out var created,out _,out _,out _)||DateTime.FromFileTimeUtc(created).Ticks!=expectedCreated)throw new InputBlockedException("截图窗口创建时间已变化");
        if(!GetClientRect(hwnd,out var client)||client.Right-client.Left!=width||client.Bottom-client.Top!=height)throw new InputBlockedException("截图客户区尺寸已变化");
        var origin=new Point();if(!ClientToScreen(hwnd,ref origin)||origin.X!=expectedGeometry.X||origin.Y!=expectedGeometry.Y||GetDpiForWindow(hwnd)!=(uint)expectedGeometry.Dpi)throw new InputBlockedException("截图客户区位置或 DPI 已变化");
        var dc=GetDC(hwnd);if(dc==0)throw new Win32Exception(Marshal.GetLastWin32Error());nint memory=0,bitmap=0,previous=0;
        try
        {
            memory=CreateCompatibleDC(dc);if(memory==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            var info=new BitmapInfo{Header=new BitmapInfoHeader{Size=(uint)Marshal.SizeOf<BitmapInfoHeader>(),Width=width,Height=-height,Planes=1,BitCount=32,SizeImage=(uint)(width*height*4)}};
            bitmap=CreateDIBSection(dc,ref info,0,out var data,0,0);if(bitmap==0||data==0)throw new Win32Exception(Marshal.GetLastWin32Error());previous=SelectObject(memory,bitmap);if(previous==0||previous==-1)throw new Win32Exception(Marshal.GetLastWin32Error());
            if(!PrintWindow(hwnd,memory,3))throw new InputBlockedException("PrintWindow 截图失败");
            var bgra=new byte[width*height*4];Marshal.Copy(data,bgra,0,bgra.Length);var rgb=new byte[width*height*3];for(var i=0;i<width*height;i++){rgb[i*3]=bgra[i*4+2];rgb[i*3+1]=bgra[i*4+1];rgb[i*3+2]=bgra[i*4];}return rgb;
        }
        finally{if(previous!=0&&previous!=-1&&memory!=0)SelectObject(memory,previous);if(memory!=0)DeleteDC(memory);if(bitmap!=0)DeleteObject(bitmap);ReleaseDC(hwnd,dc);}
    }
    private static async Task<bool> ReadExactAsync(Stream stream,byte[] data){var offset=0;while(offset<data.Length){var count=await stream.ReadAsync(data.AsMemory(offset)).ConfigureAwait(false);if(count==0)return false;offset+=count;}return true;}
    private static void WriteUInt32(byte[] data,ref int offset,uint value){BitConverter.TryWriteBytes(data.AsSpan(offset,4),value);offset+=4;}
    private static void WriteInt32(byte[] data,ref int offset,int value){BitConverter.TryWriteBytes(data.AsSpan(offset,4),value);offset+=4;}
    private static uint ReadUInt32(byte[] data,ref int offset){var value=BitConverter.ToUInt32(data,offset);offset+=4;return value;}
    private static int ReadInt32(byte[] data,ref int offset){var value=BitConverter.ToInt32(data,offset);offset+=4;return value;}
    private static long ReadInt64(byte[] data,ref int offset){var value=BitConverter.ToInt64(data,offset);offset+=8;return value;}
    private sealed class DpiContext:IDisposable{private readonly nint old=SetThreadDpiAwarenessContext(-4);public DpiContext(){if(old==0)throw new InputBlockedException("helper 无法进入物理像素上下文");}public void Dispose()=>SetThreadDpiAwarenessContext(old);}
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct BitmapInfoHeader{public uint Size;public int Width,Height;public ushort Planes,BitCount;public uint Compression,SizeImage;public int XPelsPerMeter,YPelsPerMeter;public uint ClrUsed,ClrImportant;}
    [StructLayout(LayoutKind.Sequential)]private struct BitmapInfo{public BitmapInfoHeader Header;public uint Colors;}
    [DllImport("user32.dll",SetLastError=true)]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool GetClientRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool ClientToScreen(nint hwnd,ref Point point);
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")]private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")]private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")]private static extern int ReleaseDC(nint hwnd,nint dc);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool PrintWindow(nint hwnd,nint dc,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exited,out long kernel,out long user);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern nint CreateDIBSection(nint dc,ref BitmapInfo info,uint usage,out nint bits,nint section,uint offset);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern nint SelectObject(nint dc,nint obj);
    [DllImport("gdi32.dll")]private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint obj);
    private sealed class ParentMonitor:IDisposable
    {
        private readonly CancellationTokenSource stop=new();private readonly Task task;
        public ParentMonitor(int pid,long ticks){task=Task.Run(async()=>{while(!stop.IsCancellationRequested){if(!Alive(pid,ticks)){try{Process.GetCurrentProcess().Kill();}catch{}return;}await Task.Delay(250,stop.Token).ConfigureAwait(false);}});}
        private static bool Alive(int pid,long ticks){try{using var process=Process.GetProcessById(pid);return !process.HasExited&&process.StartTime.ToUniversalTime().Ticks==ticks;}catch{return false;}}
        public void Dispose(){stop.Cancel();try{task.Wait(500);}catch{}stop.Dispose();}
    }
}
