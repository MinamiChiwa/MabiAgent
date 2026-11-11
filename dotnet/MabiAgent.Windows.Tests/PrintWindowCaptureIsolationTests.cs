using System.Diagnostics;
using MabiAgent.Windows.CaptureFixture;
using Xunit;

namespace MabiAgent.Windows.Tests;

public sealed class PrintWindowCaptureIsolationTests
{
    [Fact]
    public void UnresponsiveHelperTimesOutAndTheNextRequestStartsANewProcess()
    {
        if(!OperatingSystem.IsWindows())return;
        var calls=0;var pids=new List<int>();
        using var client=new PrintWindowCaptureClient((pipe,pid,ticks)=>FixtureLauncher(pipe,++calls==1?"hang":"normal"),frameTimeout:TimeSpan.FromMilliseconds(250),startupTimeout:TimeSpan.FromSeconds(2),helperStarted:process=>pids.Add(process.Id));
        var request=Request();var error=Assert.Throws<InputBlockedException>(()=>client.Capture(request));
        Assert.True(error.CaptureTimedOut);Assert.NotEmpty(pids);Assert.True(WaitForExit(pids[0]));
        var frame=client.Capture(request);Assert.Equal(request.Geometry.Width*request.Geometry.Height*3,frame.Length);Assert.True(pids.Count>=2);Assert.NotEqual(pids[0],pids[1]);
    }

    [Fact]
    public void HelperExitAndMalformedLengthAreRejectedAndReaped()
    {
        if(!OperatingSystem.IsWindows())return;
        foreach(var mode in new[]{"exit","invalid"})
        {
            var pids=new List<int>();using var client=new PrintWindowCaptureClient((pipe,pid,ticks)=>FixtureLauncher(pipe,mode),frameTimeout:TimeSpan.FromMilliseconds(500),startupTimeout:TimeSpan.FromSeconds(2),helperStarted:process=>pids.Add(process.Id));
            Assert.Throws<InputBlockedException>(()=>client.Capture(Request()));Assert.NotEmpty(pids);Assert.True(WaitForExit(pids[0]));
        }
    }

    [Fact]
    public void DisposedNativeWindowsOpsRejectsCaptureAndClosesItsHelper()
    {
        if(!OperatingSystem.IsWindows())return;
        var pids=new List<int>();using var native=new NativeWindowsOps((pipe,pid,ticks)=>FixtureLauncher(pipe,"normal"),frameTimeout:TimeSpan.FromMilliseconds(500),startupTimeout:TimeSpan.FromSeconds(2),helperStarted:process=>pids.Add(process.Id));
        Assert.Equal(300,native.CaptureClient(RequestState()).CopyPixels().Length);Assert.NotEmpty(pids);native.Dispose();Assert.True(WaitForExit(pids[0]));Assert.Throws<ObjectDisposedException>(()=>native.ReadWindow(1));
    }

    private static PrintWindowCaptureRequest Request()=>new(1,1,DateTime.UtcNow.Ticks,new(0,0,10,10,96));
    private static NativeWindowState RequestState()=>new(1,1,DateTime.UtcNow.Ticks,"fixture.exe","UnityWndClass",new(0,0,10,10,96),new(0,0,10,10),0,true,false,true,new(0,0));
    private static ProcessStartInfo FixtureLauncher(string pipe,string mode)
    {
        var info=new ProcessStartInfo{FileName=DotnetHost(),UseShellExecute=false,CreateNoWindow=true};info.ArgumentList.Add(typeof(Program).Assembly.Location);info.ArgumentList.Add(pipe);info.ArgumentList.Add(mode);return info;
    }
    private static string DotnetHost(){var configured=Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");if(!string.IsNullOrWhiteSpace(configured)&&File.Exists(configured))return configured;var directory=new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());return Path.Combine(directory.Parent!.Parent!.Parent!.FullName,OperatingSystem.IsWindows()?"dotnet.exe":"dotnet");}
    private static bool WaitForExit(int pid)
    {
        if(pid<=0)return true;try{using var process=Process.GetProcessById(pid);return process.WaitForExit(3000);}catch{return true;}
    }
}
