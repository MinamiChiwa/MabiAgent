using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MabiAgent.Windows;

namespace MabiAgent.Desktop.Editors;

public interface ILiveTargetCaptureService:IEditorCaptureService
{
    Task<EditorTargetSession> PrepareSelectionAsync(CancellationToken cancellationToken=default);
}

public sealed record EditorTargetSession(ClientGeometry Geometry,Action Validate,Func<Bitmap> Capture,Action<nint>? AcceptOverlay=null);
public enum TargetSelectionKind { Point,Region,Ellipse,Circle }
public sealed record TargetSelection(PixelRect Region,Bitmap Capture,string Shape="rectangle"):IDisposable
{
    public void Dispose()=>Capture.Dispose();
}

/// <summary>A transparent input shield on the current physical client area; no pointer event reaches the game.</summary>
public sealed class LiveTargetPicker:Window
{
    readonly EditorTargetSession target;
    readonly TargetSelectionKind kind;
    readonly Canvas surface;
    readonly Shape selection;
    readonly TaskCompletionSource<TargetSelection?> completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    Avalonia.PixelPoint start;bool dragging;bool completed;

    LiveTargetPicker(EditorTargetSession target,TargetSelectionKind kind,PixelRect? initial,string? hintText)
    {
        this.target=target;this.kind=kind;
        Title=hintText??(kind==TargetSelectionKind.Point?"点击定位 · Esc 取消":"拖动框选 · Esc 取消");
        WindowDecorations=Avalonia.Controls.WindowDecorations.None;CanResize=false;ShowInTaskbar=false;Topmost=true;
        TransparencyLevelHint=[WindowTransparencyLevel.Transparent];Background=Brushes.Transparent;
        ExtendClientAreaToDecorationsHint=true;WindowStartupLocation=WindowStartupLocation.Manual;
        surface=new Canvas{Background=new SolidColorBrush(Color.FromArgb(2,0,0,0)),Cursor=new Cursor(StandardCursorType.Cross)};
        selection=kind is TargetSelectionKind.Circle or TargetSelectionKind.Ellipse?new Ellipse():new Rectangle();
        selection.Stroke=Brushes.DeepSkyBlue;selection.StrokeThickness=2;selection.Fill=new SolidColorBrush(Color.FromArgb(35,0,160,255));selection.IsHitTestVisible=false;
        surface.Children.Add(selection);
        var hint=new Border{Background=new SolidColorBrush(Color.FromArgb(230,25,34,45)),CornerRadius=new CornerRadius(10),Padding=new Thickness(12,8),Margin=new Thickness(12),Child=new TextBlock{Text=Title,Foreground=Brushes.White,FontSize=14},IsHitTestVisible=false};
        surface.Children.Add(hint);Content=surface;
        var geometry=target.Geometry;Position=new Avalonia.PixelPoint(geometry.X,geometry.Y);
        var scaling=Screens.ScreenFromPoint(Position)?.Scaling??1;
        Width=geometry.Width/scaling;Height=geometry.Height/scaling;
        Opened+=(_,_)=>{target.AcceptOverlay?.Invoke(TryGetPlatformHandle()?.Handle??0);Position=new Avalonia.PixelPoint(geometry.X,geometry.Y);Width=geometry.Width/RenderScaling;Height=geometry.Height/RenderScaling;if(initial is {} box)Draw(box);Focus();};
        surface.PointerPressed+=(_,e)=>
        {
            if(e.GetCurrentPoint(surface).Properties.IsRightButtonPressed){Finish(null);e.Handled=true;return;}
            if(!e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed)return;
            start=PhysicalPoint(e.GetPosition(this));dragging=true;e.Pointer.Capture(surface);e.Handled=true;
        };
        surface.PointerMoved+=(_,e)=>{if(dragging&&kind!=TargetSelectionKind.Point){Draw(NativeRegion(start,Constrain(PhysicalPoint(e.GetPosition(this))),new(geometry.Width,geometry.Height)));e.Handled=true;}};
        surface.PointerReleased+=async (_,e)=>
        {
            if(!dragging)return;dragging=false;e.Pointer.Capture(null);e.Handled=true;
            var end=Constrain(PhysicalPoint(e.GetPosition(this)));var box=kind==TargetSelectionKind.Point?new PixelRect(end.X,end.Y,1,1):NativeRegion(start,end,new(geometry.Width,geometry.Height));
            if(box is not {} valid)return;
            try
            {
                target.Validate();Hide();await Task.Delay(60);
                if(completed)return;
                target.Validate();Finish(new(valid,target.Capture(),kind switch{TargetSelectionKind.Circle=>"circle",TargetSelectionKind.Ellipse=>"ellipse",_=>"rectangle"}));
            }
            catch(Exception error){completion.TrySetException(error);completed=true;Close();}
        };
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape){Finish(null);e.Handled=true;}};
        Closed+=(_,_)=>{if(!completed)completion.TrySetResult(null);completed=true;};
    }

    Avalonia.PixelPoint Constrain(Avalonia.PixelPoint end)
    {
        if(kind!=TargetSelectionKind.Circle)return end;
        var side=Math.Min(Math.Abs(end.X-start.X),Math.Abs(end.Y-start.Y));
        return new(start.X+Math.Sign(end.X-start.X)*side,start.Y+Math.Sign(end.Y-start.Y)*side);
    }

    Avalonia.PixelPoint PhysicalPoint(Point position)
    {
        var screen=this.PointToScreen(position);var g=target.Geometry;
        var point=EditorSelectionGeometry.ClientPoint(screen.X,screen.Y,g.X,g.Y,g.Width,g.Height);return new(point.X,point.Y);
    }
    void Draw(PixelRect? native)
    {
        if(native is not {} box)return;var g=target.Geometry;
        var p=this.PointToClient(new Avalonia.PixelPoint(g.X+box.X,g.Y+box.Y));
        Canvas.SetLeft(selection,p.X);Canvas.SetTop(selection,p.Y);selection.Width=box.Width/RenderScaling;selection.Height=box.Height/RenderScaling;
    }
    void Finish(TargetSelection? result){if(completed){result?.Dispose();return;}completed=true;if(!completion.TrySetResult(result))result?.Dispose();Close();}
    public static PixelRect? NativeRegion(Avalonia.PixelPoint start,Avalonia.PixelPoint end,PixelSize size)
    {
        var box=EditorSelectionGeometry.FromDrag(start.X,start.Y,end.X,end.Y,size.Width,size.Height);
        return new(box.Left,box.Top,box.Width,box.Height);
    }
    public static async Task<TargetSelection?> PickAsync(Window owner,IEditorCaptureService? capture,TargetSelectionKind kind,PixelRect? initial=null,CancellationToken cancellationToken=default,string? hint=null)
    {
        if(capture is not ILiveTargetCaptureService live)throw new InvalidOperationException("请先连接游戏窗口，再使用实时定位");
        var session=await live.PrepareSelectionAsync(cancellationToken);
        var overlay=new LiveTargetPicker(session,kind,initial,hint);
        using var registration=cancellationToken.Register(()=>Avalonia.Threading.Dispatcher.UIThread.Post(()=>overlay.Finish(null)));
        overlay.Show();try{return await overlay.completion.Task;}finally{if(overlay.IsVisible)overlay.Close();owner.Activate();}
    }
}
