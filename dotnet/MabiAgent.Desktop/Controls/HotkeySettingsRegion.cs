using Avalonia;
using Avalonia.Controls;
using MabiAgent.Windows;
namespace MabiAgent.Desktop.Controls;

public sealed class HotkeySettingsRegion:Decorator
{
    protected override Type StyleKeyOverride=>typeof(Decorator);
    public HotkeySettingsRegion()
    {
        LayoutUpdated+=(_,_)=>UpdateRegion();
        GotFocus+=(_,_)=>UpdateRegion();
        LostFocus+=(_,_)=>Avalonia.Threading.Dispatcher.UIThread.Post(UpdateRegion);
        DetachedFromVisualTree+=(_,_)=>StopHotkeyEditing.Remove(this);
        PropertyChanged+=(_,e)=>{if(e.Property==IsVisibleProperty)UpdateRegion();};
    }
    private void UpdateRegion()
    {
        if(!IsEffectivelyVisible||TopLevel.GetTopLevel(this) is not Window window||window.TryGetPlatformHandle() is not{} handle||Bounds.Width<=0||Bounds.Height<=0)
        {StopHotkeyEditing.Remove(this);return;}
        var a=this.PointToScreen(default);var b=this.PointToScreen(new Point(Bounds.Width,Bounds.Height));
        StopHotkeyEditing.Update(this,handle.Handle,a.X,a.Y,b.X,b.Y,IsKeyboardFocusWithin);
    }
}
