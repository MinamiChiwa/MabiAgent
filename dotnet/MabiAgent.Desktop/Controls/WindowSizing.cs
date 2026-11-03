using Avalonia.Controls;

namespace MabiAgent.Desktop.Controls;

internal static class WindowSizing
{
    public static void FitToWorkingArea(Window window,Window? owner=null)
    {
        var screen=window.Screens.ScreenFromWindow(owner??window);
        screen??=window.Screens.Primary;
        if(screen is null)return;
        var availableWidth=Math.Max(320,screen.WorkingArea.Width/screen.Scaling-24);
        var availableHeight=Math.Max(240,screen.WorkingArea.Height/screen.Scaling-56);
        window.MinWidth=Math.Min(window.MinWidth,availableWidth);
        window.MinHeight=Math.Min(window.MinHeight,availableHeight);
        window.Width=Math.Clamp(window.Width,window.MinWidth,availableWidth);
        window.Height=Math.Clamp(window.Height,window.MinHeight,availableHeight);
    }
}
