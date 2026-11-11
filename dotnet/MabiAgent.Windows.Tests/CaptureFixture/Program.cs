using System.IO.Pipes;
using System.Diagnostics;
using MabiAgent.Windows;

namespace MabiAgent.Windows.CaptureFixture;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if(args.Length>0&&args[0]=="--capture-helper")
        {
            PrintWindowCaptureEntry.TryRun(args,out var exitCode);Environment.ExitCode=exitCode;return;
        }
        if(args.Length>0&&args[0]=="--capture-owner")
        {
            RunCaptureOwner(args);return;
        }
        if(args.Length<2) return;
        using var pipe=new NamedPipeClientStream(".",args[0],PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        var mode=args[1];
        if(mode=="exit")return;
        var request=new byte[48];if(!await ReadExact(pipe,request))return;
        if(mode=="hang"){await Task.Delay(Timeout.InfiniteTimeSpan);return;}
        if(mode=="invalid")
        {
            var response=new byte[24];Write(response,0,0x4D424352u);Write(response,4,1u);Write(response,8,0u);Write(response,12,10);Write(response,16,10);Write(response,20,int.MaxValue);await pipe.WriteAsync(response);await pipe.FlushAsync();await Task.Delay(5000);return;
        }
        var width=BitConverter.ToInt32(request,36);var height=BitConverter.ToInt32(request,40);var length=checked(width*height*3);var ok=new byte[24];Write(ok,0,0x4D424352u);Write(ok,4,1u);Write(ok,8,0u);Write(ok,12,width);Write(ok,16,height);Write(ok,20,length);await pipe.WriteAsync(ok);await pipe.WriteAsync(new byte[length]);await pipe.FlushAsync();await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    private static void RunCaptureOwner(string[] args)
    {
        var report=args[1];var hwnd=(nint)long.Parse(args[2]);var pid=int.Parse(args[3]);var created=long.Parse(args[4]);var geometry=new ClientGeometry(int.Parse(args[5]),int.Parse(args[6]),int.Parse(args[7]),int.Parse(args[8]),int.Parse(args[9]));
        var state=new NativeWindowState(hwnd,pid,created,"fixture.exe","MabiPrintWindowIsolation",geometry,new(0,0,geometry.Width,geometry.Height),0,true,false,true,new(0,0));
        ProcessStartInfo Launcher(string pipe,int parentPid,long parentStart)
        {var info=new ProcessStartInfo{FileName=Environment.ProcessPath??throw new InvalidOperationException(),UseShellExecute=false,CreateNoWindow=true};info.ArgumentList.Add(typeof(Program).Assembly.Location);info.ArgumentList.Add("--capture-helper");info.ArgumentList.Add(pipe);info.ArgumentList.Add(parentPid.ToString());info.ArgumentList.Add(parentStart.ToString());return info;}
        using var native=new NativeWindowsOps(Launcher,frameTimeout:TimeSpan.FromSeconds(20),startupTimeout:TimeSpan.FromSeconds(5),helperReady:process=>File.WriteAllText(report,process.Id.ToString()));native.CaptureClient(state);Thread.Sleep(Timeout.Infinite);
    }
    private static async Task<bool> ReadExact(Stream stream,byte[] buffer){var offset=0;while(offset<buffer.Length){var count=await stream.ReadAsync(buffer.AsMemory(offset));if(count==0)return false;offset+=count;}return true;}
    private static void Write(byte[] bytes,int offset,uint value)=>BitConverter.TryWriteBytes(bytes.AsSpan(offset,4),value);
    private static void Write(byte[] bytes,int offset,int value)=>BitConverter.TryWriteBytes(bytes.AsSpan(offset,4),value);
}
