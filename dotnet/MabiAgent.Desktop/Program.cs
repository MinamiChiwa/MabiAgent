using Avalonia;
using MabiAgent.Windows;
namespace MabiAgent.Desktop;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)=>PrintWindowCaptureEntry.TryRun(args,out var exitCode)?exitCode:AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}
