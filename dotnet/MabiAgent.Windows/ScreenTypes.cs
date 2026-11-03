using System.Security.Cryptography;
using OpenCvSharp;
namespace MabiAgent.Windows;

public readonly record struct PixelPoint(int X,int Y);
public enum ClickRegionShape { Rectangle,Ellipse }
public readonly record struct PixelBox(int Left,int Top,int Right,int Bottom)
{
    public int Width=>Right-Left; public int Height=>Bottom-Top;
    public PixelPoint Center=>new((Left+Right)/2,(Top+Bottom)/2);
    public bool Contains(PixelPoint p)=>p.X>=Left&&p.X<Right&&p.Y>=Top&&p.Y<Bottom;
    public bool Contains(PixelPoint p,ClickRegionShape shape)
    {
        if(!Contains(p))return false;if(shape==ClickRegionShape.Rectangle)return true;
        if(shape!=ClickRegionShape.Ellipse)throw new ArgumentOutOfRangeException(nameof(shape));
        var x=(2d*(p.X-Left)+1-Width)/Width;var y=(2d*(p.Y-Top)+1-Height)/Height;return x*x+y*y<=1;
    }
    public bool IsInside(int width,int height)=>Left>=0&&Top>=0&&Right<=width&&Bottom<=height&&Width>0&&Height>0;
    public PixelPoint RandomPoint(Random? random=null,ClickRegionShape shape=ClickRegionShape.Rectangle)
    {
        if(Width<=0||Height<=0)throw new ArgumentException("点击区域不能为空");
        // Stay in the central 60%: animation, outlines and neighbouring controls sit at the edge.
        var innerWidth=Math.Max(1,(int)Math.Floor(Width*.6));var innerHeight=Math.Max(1,(int)Math.Floor(Height*.6));
        var innerLeft=Left+(Width-innerWidth+1)/2;var innerTop=Top+(Height-innerHeight+1)/2;
        var inner=new PixelBox(innerLeft,innerTop,innerLeft+innerWidth,innerTop+innerHeight);
        random??=Random.Shared;if(shape==ClickRegionShape.Rectangle)return new(random.Next(inner.Left,inner.Right),random.Next(inner.Top,inner.Bottom));
        if(shape!=ClickRegionShape.Ellipse)throw new ArgumentOutOfRangeException(nameof(shape));
        for(var attempt=0;attempt<128;attempt++)
        {
            var radius=Math.Sqrt(random.NextDouble());var angle=random.NextDouble()*Math.Tau;
            var point=new PixelPoint(inner.Left+(int)Math.Floor(inner.Width*(.5+.5*radius*Math.Cos(angle))),inner.Top+(int)Math.Floor(inner.Height*(.5+.5*radius*Math.Sin(angle))));
            if(inner.Contains(point,shape)&&Contains(point,shape))return point;
        }
        return Center;
    }
    public PixelPoint CenterOffsetPoint(int maximumPixels=3,ClickRegionShape shape=ClickRegionShape.Rectangle,Random? random=null)
    {
        if(maximumPixels is <1 or >32||Width<=0||Height<=0)throw new ArgumentException("中心偏移范围无效");
        var rx=Math.Min(maximumPixels,Math.Max(0,(int)Math.Floor(Width*.3)-1));var ry=Math.Min(maximumPixels,Math.Max(0,(int)Math.Floor(Height*.3)-1));
        random??=Random.Shared;
        for(var i=0;i<64;i++){var point=new PixelPoint(Center.X+random.Next(-rx,rx+1),Center.Y+random.Next(-ry,ry+1));if(Contains(point,shape))return point;}
        return Center;
    }
    internal Rect Rect=>new(Left,Top,Width,Height);
}
public sealed record ReviewedLayout(int ReferenceWidth,int ReferenceHeight,int Width,int Height,int Dpi)
{
    public static ReviewedLayout For(int width,int height,int dpi)
    {
        if(dpi is <48 or >768)throw new ArgumentException("DPI 无效");
        if(width==1920&&height==1009)return new(width,height,width,height,dpi);
        if(height is >=720 and <=2880&&width is >=960 and <=3840&&Math.Abs(width-Math.Round(height*4d/3))<=1)return new(1280,960,width,height,dpi);
        throw new ArgumentException("客户区比例或尺寸未经校准");
    }
    public PixelBox SearchBox(PixelBox box)
    {
        if(!box.IsInside(ReferenceWidth,ReferenceHeight))throw new ArgumentException("搜索区域越界");
        double sx=(double)Width/ReferenceWidth,sy=(double)Height/ReferenceHeight;
        return new((int)Math.Floor(box.Left*sx),(int)Math.Floor(box.Top*sy),Math.Min(Width,(int)Math.Ceiling(box.Right*sx)),Math.Min(Height,(int)Math.Ceiling(box.Bottom*sy)));
    }
    public Size TemplateSize(int width,int height)=>new(Math.Max(1,(int)Math.Round(width*(double)Width/ReferenceWidth)),Math.Max(1,(int)Math.Round(height*(double)Height/ReferenceHeight)));
}
public sealed class RgbImage
{
    private readonly byte[] pixels;
    public int Width{get;} public int Height{get;} public string Sha256{get;}
    public RgbImage(int width,int height,ReadOnlySpan<byte> rgb)
    {
        if(width<=0||height<=0||(long)width*height>16_777_216||rgb.Length!=(long)width*height*3)throw new ArgumentException("需要有界原生 RGB 图像");
        Width=width;Height=height;pixels=rgb.ToArray();Sha256=Convert.ToHexStringLower(SHA256.HashData(pixels));
    }
    public byte[] CopyPixels()=>pixels.ToArray();
    internal ReadOnlySpan<byte> Pixels=>pixels;
    internal Mat ToMat(){var m=new Mat(Height,Width,MatType.CV_8UC3);System.Runtime.InteropServices.Marshal.Copy(pixels,0,m.Data,pixels.Length);return m;}
    internal Mat ToMat(PixelBox box)
    {
        if(!box.IsInside(Width,Height))throw new ArgumentException("裁图越界");
        var m=new Mat(box.Height,box.Width,MatType.CV_8UC3);
        for(var row=0;row<box.Height;row++)System.Runtime.InteropServices.Marshal.Copy(pixels,((box.Top+row)*Width+box.Left)*3,m.Ptr(row),box.Width*3);
        return m;
    }
    public RgbImage Crop(PixelBox box)
    {
        if(!box.IsInside(Width,Height))throw new ArgumentException("裁图越界");
        var rgb=new byte[box.Width*box.Height*3];
        for(var y=0;y<box.Height;y++)pixels.AsSpan(((y+box.Top)*Width+box.Left)*3,box.Width*3).CopyTo(rgb.AsSpan(y*box.Width*3));
        return new(box.Width,box.Height,rgb);
    }
}
public sealed record ClientGeometry(int X,int Y,int Width,int Height,int Dpi);
/// <summary>Reference pixels map to physical client pixels. DPI is metadata, never an extra multiplier.</summary>
public sealed class ClientCoordinateTransform
{
    public int ReferenceWidth{get;}public int ReferenceHeight{get;}public ClientGeometry Geometry{get;}
    public ClientCoordinateTransform(int referenceWidth,int referenceHeight,ClientGeometry geometry)
    {
        if(referenceWidth<=0||referenceHeight<=0||geometry.Width<=0||geometry.Height<=0||geometry.Dpi is <48 or >768)throw new ArgumentException("坐标参考尺寸或 DPI 无效");
        ReferenceWidth=referenceWidth;ReferenceHeight=referenceHeight;Geometry=geometry;
    }
    public PixelPoint ToClient(PixelPoint reference)
    {
        if(!new PixelBox(0,0,ReferenceWidth,ReferenceHeight).Contains(reference))throw new ArgumentException("参考坐标越界");
        return new((int)((long)reference.X*Geometry.Width/ReferenceWidth),(int)((long)reference.Y*Geometry.Height/ReferenceHeight));
    }
    public PixelBox ToClient(PixelBox reference)
    {
        if(!reference.IsInside(ReferenceWidth,ReferenceHeight))throw new ArgumentException("参考区域越界");
        return new((int)((long)reference.Left*Geometry.Width/ReferenceWidth),(int)((long)reference.Top*Geometry.Height/ReferenceHeight),
            (int)(((long)reference.Right*Geometry.Width+ReferenceWidth-1)/ReferenceWidth),(int)(((long)reference.Bottom*Geometry.Height+ReferenceHeight-1)/ReferenceHeight));
    }
    public PixelPoint ToReference(PixelPoint client)
    {
        if(!new PixelBox(0,0,Geometry.Width,Geometry.Height).Contains(client))throw new ArgumentException("客户区坐标越界");
        return new((int)((long)client.X*ReferenceWidth/Geometry.Width),(int)((long)client.Y*ReferenceHeight/Geometry.Height));
    }
    public PixelPoint ToScreen(PixelPoint reference)=>ClientToScreen(ToClient(reference));
    public PixelPoint ClientToScreen(PixelPoint client)
    {
        if(!new PixelBox(0,0,Geometry.Width,Geometry.Height).Contains(client))throw new ArgumentException("客户区坐标越界");
        return new(checked(Geometry.X+client.X),checked(Geometry.Y+client.Y));
    }
    public PixelPoint ScreenToClient(PixelPoint screen)
    {
        var result=new PixelPoint(checked(screen.X-Geometry.X),checked(screen.Y-Geometry.Y));
        if(!new PixelBox(0,0,Geometry.Width,Geometry.Height).Contains(result))throw new ArgumentException("屏幕坐标不在目标客户区");return result;
    }
}
public sealed record NativeWindowState(nint WindowId,int ProcessId,long ProcessStartUtcTicks,string Executable,string ClassName,ClientGeometry Geometry,PixelBox WindowRect,nint ForegroundId,bool Visible,bool Minimized,bool Enabled,PixelPoint Cursor,bool LeftDown=false,bool RightDown=false);
public enum ClientFramePurpose { Current, Cached, Preview }
public sealed record ClientFrameEvidence(string Backend,long Generation,long Sequence,double SourceTime,ClientFramePurpose Purpose=ClientFramePurpose.Current,long GeometryGeneration=0)
{
    public bool IsCurrent=>Purpose==ClientFramePurpose.Current&&!string.IsNullOrWhiteSpace(Backend)&&Generation>0&&Sequence>0&&double.IsFinite(SourceTime)&&SourceTime>=0;
    public bool Follows(ClientFrameEvidence prior)=>IsCurrent&&prior.IsCurrent&&Backend==prior.Backend&&Generation==prior.Generation&&GeometryGeneration==prior.GeometryGeneration&&Sequence>prior.Sequence&&SourceTime>prior.SourceTime;
}
public sealed record CurrentClientFrame(RgbImage Image,ClientFrameEvidence Evidence);
/// <summary>Describes the actual source frame; cached images and previews cannot authorize input.</summary>
public interface ICurrentClientFrameSource
{ CurrentClientFrame CaptureCurrentClientFrame(NativeWindowState state); }
public sealed record ClientCapture(NativeWindowState Native,RgbImage Image,double CapturedAt,ClientFrameEvidence? Frame=null,string ResourceRevision="");
public sealed record SessionScope(string SessionId,string? AccountId,string? CharacterId)
{
    public bool HasIdentity=>!string.IsNullOrWhiteSpace(SessionId)&&!string.IsNullOrWhiteSpace(AccountId)&&!string.IsNullOrWhiteSpace(CharacterId);
}
public sealed record TemplateHit(string Name,double Score,PixelBox Box,string TemplateSha256,PixelBox? MatchBox=null,string? Variant=null);
public sealed record PageView(string Page,bool Modal,IReadOnlyList<TemplateHit> Hits,IReadOnlyDictionary<string,PixelPoint> Targets,IReadOnlyList<PixelBox> StableBoxes,bool NeedsDeviceArrival=false,IReadOnlyDictionary<string,PixelBox>? TargetRegions=null,IReadOnlyDictionary<string,ClickRegionShape>? TargetShapes=null,IReadOnlySet<string>? RandomTargets=null,bool SceneHudIdentity=false)
{
    public static PageView Unknown(bool modal=false)=>new("unknown",modal,[],new Dictionary<string,PixelPoint>(),[]);
}
public sealed record UiObservation(string ImageSha256,double CapturedAt,SessionScope Scope,PageView View,bool Stable,bool IdentityVerified,ClientFrameEvidence? Frame=null,string ResourceRevision="");
public sealed record TargetProof(string Name,string Page,PixelPoint Point,string ImageSha256,double CapturedAt,SessionScope Scope,PixelBox? ClickRegion=null,ClickRegionShape ClickShape=ClickRegionShape.Rectangle,ClientFrameEvidence? Frame=null,string ResourceRevision="");
public sealed class InputBlockedException(string message,bool targetChangedBeforeDispatch=false):InvalidOperationException(message)
{ public bool TargetChangedBeforeDispatch {get;}=targetChangedBeforeDispatch; }
public sealed class InputOutcomeUnknownException(string message):IOException(message);
