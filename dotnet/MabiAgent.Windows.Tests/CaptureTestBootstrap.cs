using System.Runtime.CompilerServices;
using System.Diagnostics;
using MabiAgent.Windows;

namespace MabiAgent.Windows.Tests;

internal static class CaptureTestBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var fixture=typeof(MabiAgent.Windows.CaptureFixture.Program).Assembly.Location;
        PrintWindowCaptureClient.ConfigureTestLauncher((pipe,pid,ticks)=>
        {
            var info=new ProcessStartInfo{FileName=DotnetHost(),UseShellExecute=false,CreateNoWindow=true};info.ArgumentList.Add(fixture);info.ArgumentList.Add("--capture-helper");info.ArgumentList.Add(pipe);info.ArgumentList.Add(pid.ToString());info.ArgumentList.Add(ticks.ToString());return info;
        });
    }
    private static string DotnetHost(){var configured=Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");if(!string.IsNullOrWhiteSpace(configured)&&File.Exists(configured))return configured;var standard=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet","dotnet.exe");return File.Exists(standard)?standard:"dotnet";}
}
