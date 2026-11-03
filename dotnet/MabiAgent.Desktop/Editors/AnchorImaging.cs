using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
namespace MabiAgent.Desktop.Editors;
public interface IEditorCaptureService
{
    // Must use the same GameWindowSelection and shared input guard as the host.
    // An explicit screenshot authorizes at most one foreground activation.
    Task<Bitmap> CaptureAsync(CancellationToken cancellationToken=default);
}
public sealed record AnchorText(string Text,PixelRect Bounds);
public interface IAnchorTextDetector { IReadOnlyList<AnchorText> Detect(Bitmap image); }
public static class StableAnchorCrop
{
    static readonly Regex Level=new(@"(?:\b[lL][vV]\s*[.．:]?\s*|等[級级极]\s*)\d+(?:\s*以上)?",RegexOptions.CultureInvariant);
    public static (Bitmap Image,PixelRect Offset) RemoveLevels(Bitmap source,IAnchorTextDetector detector)
    {
        var roi=new PixelRect(0,0,source.PixelSize.Width,source.PixelSize.Height);
        foreach(var row in detector.Detect(source))
        {
            var text=row.Text.Normalize(NormalizationForm.FormKC);var level=Level.Match(text);if(!level.Success)continue;
            int x=Math.Max(0,row.Bounds.X),y=Math.Max(0,row.Bounds.Y-2),r=Math.Min(source.PixelSize.Width,row.Bounds.X+row.Bounds.Width+2),b=Math.Min(source.PixelSize.Height,row.Bounds.Y+row.Bounds.Height+2);
            static double Weight(string s)=>s.Sum(c=>c>=0x2e80?1d:.5d);var total=Math.Max(.5,Weight(text));var h=b-y;
            var left=Math.Max(x,(int)(x+(r-x)*Weight(text[..level.Index])/total)-h);var right=Math.Min(r,(int)(x+(r-x)*Weight(text[..(level.Index+level.Length)])/total)+h);
            int a=roi.X,t=roi.Y,c=roi.X+roi.Width,d=roi.Y+roi.Height;if(right<=a||left>=c||b<=t||y>=d)continue;
            var boxes=new[]{(a,t,Math.Min(c,left),d),(Math.Max(a,right),t,c,d),(a,t,c,Math.Min(d,y)),(a,Math.Max(t,b),c,d)}.Where(v=>v.Item3-v.Item1>=12&&v.Item4-v.Item2>=8).OrderByDescending(v=>(v.Item3-v.Item1)*(v.Item4-v.Item2)).ToArray();
            if(boxes.Length==0)throw new InvalidDataException("截图只有可变等级，请框选名称或固定图案");var best=boxes[0];roi=new(best.Item1,best.Item2,best.Item3-best.Item1,best.Item4-best.Item2);
        }
        return(ImagePixels.Crop(source,roi),roi);
    }
    public static byte[] Png(Bitmap image){using var output=new MemoryStream();image.Save(output,PngBitmapEncoderOptions.Default);return output.ToArray();}
    public static Bitmap Load(string path)
    {var info=new FileInfo(path);if(info.Length>8*1024*1024)throw new InvalidDataException("图片须小于8 MiB");using var stream=File.OpenRead(path);var image=new Bitmap(stream);if((long)image.PixelSize.Width*image.PixelSize.Height>16_000_000){image.Dispose();throw new InvalidDataException("图片尺寸过大");}return image;}
}
// Selection is local; letterboxing is removed before native pixel conversion.
public sealed class CropSelectionWindow:Ursa.Controls.UrsaWindow
{
    readonly Bitmap source;readonly Canvas canvas;readonly Shape rectangle;readonly Button confirm;readonly string selectionShape;Point start;bool dragging;
    public PixelRect? Selection{get;private set;}
    public CropSelectionWindow(Bitmap image,PixelRect? initial=null,PixelRect? matched=null,string selectionShape="rectangle")
    {
        if(selectionShape is not("rectangle" or "circle" or "ellipse"))throw new ArgumentException("选区形状无效",nameof(selectionShape));
        this.selectionShape=selectionShape;
        source=image;Title="图片框选";Width=1000;Height=800;MinWidth=520;MinHeight=360;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Opened+=(_,_)=>MabiAgent.Desktop.Controls.WindowSizing.FitToWorkingArea(this,Owner as Window);
        var root=new DockPanel{Margin=new Thickness(16)};var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Spacing=12,Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(actions,Dock.Bottom);
        confirm=new Button{Content="使用区域",IsEnabled=false};confirm.Classes.Add("Primary");confirm.Theme=(Avalonia.Styling.ControlTheme)Avalonia.Application.Current!.FindResource("SolidButton")!;confirm.Click+=(_,_)=>{if(Selection is not null)Close(true);};var cancel=new Button{Content="取消"};cancel.Click+=(_,_)=>Close(false);actions.Children.Add(new MabiAgent.Desktop.Controls.HelpButton{Message="拖动鼠标框选固定名称或图案，避开等级、数量和动效。"});actions.Children.Add(cancel);actions.Children.Add(confirm);root.Children.Add(actions);
        var picture=new Image{Source=image,Stretch=Stretch.Uniform};canvas=new Canvas{Background=Brushes.Transparent};rectangle=selectionShape is "circle" or "ellipse"?new Ellipse():new Rectangle();rectangle.Stroke=Brushes.DeepSkyBlue;rectangle.StrokeThickness=2;rectangle.Fill=new SolidColorBrush(Color.FromArgb(35,0,160,255));rectangle.IsHitTestVisible=false;canvas.Children.Add(rectangle);var stage=new Grid();stage.Children.Add(picture);stage.Children.Add(canvas);root.Children.Add(stage);Content=root;
        canvas.PointerPressed+=(_,e)=>{if(!e.GetCurrentPoint(canvas).Properties.IsLeftButtonPressed)return;start=Clamp(e.GetPosition(canvas));dragging=true;e.Pointer.Capture(canvas);Draw(start);};canvas.PointerMoved+=(_,e)=>{if(dragging)Draw(Clamp(e.GetPosition(canvas)));};canvas.PointerReleased+=(_,e)=>{if(dragging){Draw(Clamp(e.GetPosition(canvas)));dragging=false;e.Pointer.Capture(null);}};canvas.PointerCaptureLost+=(_,_)=>dragging=false;
        if(initial is {} selected){Selection=ConstrainNative(selected);confirm.IsEnabled=true;}
        Rectangle? match=null;if(matched is not null){match=new Rectangle{Stroke=Brushes.LimeGreen,StrokeThickness=3,IsHitTestVisible=false};canvas.Children.Add(match);}
        canvas.SizeChanged+=(_,_)=>{if(Selection is {} selected)DrawNative(rectangle,selected);if(matched is {} hit&&match is not null)DrawNative(match,hit);};
        KeyDown+=(_,e)=>{if(e.Key==Avalonia.Input.Key.Escape){Close(false);e.Handled=true;}};
    }
    PixelRect ConstrainNative(PixelRect box)
    {if(selectionShape!="circle")return box;var side=Math.Min(box.Width,box.Height);return new(box.X,box.Y,side,side);}
    void DrawNative(Shape shape,PixelRect box)
    {var(s,x,y)=Transform();Canvas.SetLeft(shape,x+box.X*s);Canvas.SetTop(shape,y+box.Y*s);shape.Width=box.Width*s;shape.Height=box.Height*s;}
    (double scale,double left,double top) Transform(){var scale=Math.Min(canvas.Bounds.Width/source.PixelSize.Width,canvas.Bounds.Height/source.PixelSize.Height);return(scale,(canvas.Bounds.Width-source.PixelSize.Width*scale)/2,(canvas.Bounds.Height-source.PixelSize.Height*scale)/2);}
    Point Clamp(Point p){var(s,x,y)=Transform();return new(Math.Clamp(p.X,x,x+source.PixelSize.Width*s),Math.Clamp(p.Y,y,y+source.PixelSize.Height*s));}
    void Draw(Point end)
    {
        var(s,_,_)=Transform();if(s<=0)return;
        if(selectionShape=="circle"){var side=Math.Min(Math.Abs(end.X-start.X),Math.Abs(end.Y-start.Y));end=new(start.X+Math.Sign(end.X-start.X)*side,start.Y+Math.Sign(end.Y-start.Y)*side);}
        Selection=NativeSelection(start,end,canvas.Bounds.Size,source.PixelSize);
        if(Selection is {} native){Selection=ConstrainNative(native);DrawNative(rectangle,Selection.Value);}
        confirm.IsEnabled=Selection is not null;
    }
    public static PixelRect? NativeSelection(Point start,Point end,Size display,PixelSize pixels)
    {var scale=Math.Min(display.Width/pixels.Width,display.Height/pixels.Height);if(scale<=0)return null;var x=(display.Width-pixels.Width*scale)/2;var y=(display.Height-pixels.Height*scale)/2;int l=Math.Clamp((int)Math.Floor((Math.Min(start.X,end.X)-x)/scale),0,pixels.Width),t=Math.Clamp((int)Math.Floor((Math.Min(start.Y,end.Y)-y)/scale),0,pixels.Height),r=Math.Clamp((int)Math.Ceiling((Math.Max(start.X,end.X)-x)/scale),0,pixels.Width),b=Math.Clamp((int)Math.Ceiling((Math.Max(start.Y,end.Y)-y)/scale),0,pixels.Height);return r>l&&b>t?new(l,t,r-l,b-t):null;}
}
