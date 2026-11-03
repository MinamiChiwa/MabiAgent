using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
namespace MabiAgent.Desktop;
// Compact always-on-top card. It shares MainViewModel with the main window; closing it only returns there.
public partial class MiniWindow:Window
{
    private bool closingWithOwner;
    private PixelPoint? lastPosition;
    public MiniWindow(){InitializeComponent();}
    public MiniWindow(MainViewModel model):this(){DataContext=model;}
    public void ShowNear(Window owner)
    {
        if(lastPosition is {} at)Position=at;
        else if((owner.Screens.ScreenFromWindow(owner)??owner.Screens.Primary) is {} screen)
        {
            // Top-right corner of the working area, clear of the taskbar and window edges.
            var area=screen.WorkingArea;var margin=(int)(24*screen.Scaling);
            Position=new PixelPoint(area.Right-(int)(Width*screen.Scaling)-margin,area.Y+margin);
        }
        Show();Activate();
        Avalonia.Threading.Dispatcher.UIThread.Post(()=>Controls.Motion.PopIn(MiniShell),Avalonia.Threading.DispatcherPriority.Loaded);
    }
    public void HideToOwner(){lastPosition=Position;Hide();}
    public void CloseWithOwner(){closingWithOwner=true;Close();}
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if(closingWithOwner||e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)return;
        e.Cancel=true;
        if(DataContext is MainViewModel model)model.IsMiniMode=false;
    }
    private void DragWindow(object? sender,PointerPressedEventArgs e)
    {if(e.GetCurrentPoint(this).Properties.IsLeftButtonPressed&&e.ClickCount==1)BeginMoveDrag(e);}
    private void ReturnToMain(object? sender,TappedEventArgs e)
    {if(DataContext is MainViewModel model)model.IsMiniMode=false;}
    private void HandleKeys(object? sender,KeyEventArgs e)
    {
        if(DataContext is not MainViewModel model)return;
        if(e.Key==Key.M&&e.KeyModifiers.HasFlag(KeyModifiers.Control)){model.IsMiniMode=false;e.Handled=true;}
    }
}
