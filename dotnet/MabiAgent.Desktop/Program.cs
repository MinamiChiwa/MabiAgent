using Avalonia;
namespace MabiAgent.Desktop;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)=>AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}
