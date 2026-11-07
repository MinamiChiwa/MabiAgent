using MabiAgent.Windows;
using OpenCvSharp;
using Xunit;

namespace MabiAgent.Windows.Tests;

public sealed class FishingFeedbackTests
{
    internal static RgbImage ReadImage(string path)
    {
        using var bgr=Cv2.ImRead(path);using var rgb=new Mat();Cv2.CvtColor(bgr,rgb,ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        return new(rgb.Width,rgb.Height,pixels);
    }
    [Fact] public void VisibleFishingRowsExposeAllSixCompleteNames()
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        var image=ReadImage(Path.Combine(root,"tests/fixtures/production/east-wall-fishing-list-current.jpg"));
        using var matcher=new TemplateMatcher(routes.Profile);using var recognizer=new ScreenPageRecognizer(routes.Profile,matcher);
        var view=recognizer.Recognize(image);Assert.Equal("fishing_nodes",view.Page);
        for(var i=0;i<6;i++)Assert.Contains($"gathering.open_point_fishing_{i:00}",view.Targets.Keys);
    }
    [Fact] public void ActualNavigationQuoteReadsFareAndAvailableWings()
    {
        var root=RecognitionTests.Root();var image=ReadImage(Path.Combine(root,"tests/fixtures/production/travel-wing-quote-current.jpg"));
        var routes=ScreenRoutes.Load(root);using var matcher=new TemplateMatcher(routes.Profile);
        var icon=matcher.Anchor(image,"travel.wing_quote_icon");Assert.NotNull(icon);
        var cfg=routes.Configuration["travel_wings"]!["quote"]!;var offset=cfg["offset"]!.AsArray();
        int X(int i)=>offset[i]!.GetValue<int>();var box=new PixelBox(icon.Box.Right+X(0),icon.Box.Top+X(1),icon.Box.Right+X(2),icon.Box.Top+X(3));
        using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));var trace=new TraceOcr(engine);
        var value=new ScreenQuantityReader(trace).ReadNavigationQuoteOnce(image,box,cfg["threshold"]!.GetValue<int>(),.85);
        Assert.True(value is{Quantity:19,Capacity:10000,CapacityExact:false},$"box={box}; text={trace.Line}; result={value}");
        Assert.Equal(1,trace.Calls);
    }
    [Fact] public void UserVideoSouthWallRowIsClickable()
    {
        var root=RecognitionTests.Root();using var recognizer=new ScreenPageRecognizer(ScreenRoutes.Load(root).Profile);
        var view=recognizer.Recognize(ReadImage(Path.Combine(root,"tests/fixtures/production/fishing-nodes-video-current.jpg")));
        Assert.Equal("fishing_nodes",view.Page);Assert.Contains("gathering.open_point_fishing_01",view.Targets.Keys);
        var point=view.Targets["gathering.open_point_fishing_01"];Assert.InRange(point.Y,700,750);
    }
    public static IEnumerable<object[]> FishingRows()
    {
        foreach(var video in new[]{false,true})
            for(var row=0;row<(video?4:6);row++)
                foreach(var brightness in new[]{.7,1d,1.2})yield return [video,row,brightness];
    }
    [Theory][MemberData(nameof(FishingRows))]
    public void RealNamesRemainDistinctAcrossListPositionAndBrightness(bool video,int row,double brightness)
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);using var matcher=new TemplateMatcher(routes.Profile);
        using var image=Cv2.ImRead(Path.Combine(root,"tests/fixtures/production",video?"fishing-nodes-video-current.jpg":"east-wall-fishing-list-current.jpg"));
        var name=$"preset_catalog_point_fishing_{row:00}_node";var width=routes.Profile.Templates[name].Image.Width;
        using var crop=new Mat(image,new Rect(video?802:803,(video?610:393)+96*row,width,34));
        using var adjusted=new Mat();crop.ConvertTo(adjusted,MatType.CV_8UC3,brightness,10);
        foreach(var top in new[]{310,552,790})
        {
            using var frame=new Mat(960,1280,MatType.CV_8UC3,Scalar.All(32));
            using(var region=new Mat(frame,new Rect(802,top,width,34)))adjusted.CopyTo(region);
            var rgb=FromBgr(frame);var hit=matcher.Anchor(rgb,name);Assert.NotNull(hit);
            Assert.InRange(hit.Box.Center.Y,top,top+34);
            for(var other=0;other<6;other++)
                if(other!=row)Assert.Null(matcher.Anchor(rgb,$"preset_catalog_point_fishing_{other:00}_node"));
        }
    }
    private static RgbImage FromBgr(Mat bgr)
    {
        using var rgb=new Mat();Cv2.CvtColor(bgr,rgb,ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        return new(rgb.Width,rgb.Height,pixels);
    }
    private sealed class TraceOcr(IOcrEngine engine):IOcrEngine
    {
        public int Calls;public OcrLine? Line;
        public OcrLine? Recognize(int w,int h,byte[] pixels){Calls++;return Line=engine.Recognize(w,h,pixels);}
    }
}
