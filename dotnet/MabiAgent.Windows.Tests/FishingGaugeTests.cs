using System.Text.Json.Nodes;
using Xunit;
namespace MabiAgent.Windows.Tests;
public sealed class FishingGaugeTests
{
    [Theory][InlineData(0,101)][InlineData(1,72)][InlineData(2,42)][InlineData(3,14)]
    public void LiveFloatingGaugeReadsCalibratedInterior(int sample,int fill)
    {
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(RecognitionTests.Root(),$"tests/fixtures/fishing-gauge/live-{sample}.png"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var config=JsonNode.Parse("""{"calibrated":true,"fill_rgb":[40,220,88],"empty_rgb":[39,42,50],"tolerance":35,"scan_rows":true}""");
        Assert.Equal(fill/104d,FishingGauge.Read(new(rgb.Width,rgb.Height,pixels),new(38,99,142,125),config));
    }
    static JsonObject Calibration()=>new(){["calibrated"]=true,["fill_rgb"]=new JsonArray(10,200,30),["empty_rgb"]=new JsonArray(40,40,40),["tolerance"]=5};
    static RgbImage Track(int fill)
    {var pixels=new byte[10*3*3];for(var y=0;y<3;y++)for(var x=0;x<10;x++){var rgb=x<fill?new byte[]{10,200,30}:new byte[]{40,40,40};rgb.CopyTo(pixels,(y*10+x)*3);}return new(10,3,pixels);}
    [Theory][InlineData(0,0)][InlineData(2,.2)][InlineData(10,1)]
    public void CalibratedTrackReportsFraction(int fill,double expected)=>Assert.Equal(expected,FishingGauge.Read(Track(fill),new(0,0,10,3),Calibration()));
    [Fact] public void MissingCalibrationAndOcclusionRemainUnknown()
    {Assert.Null(FishingGauge.Read(Track(2),new(0,0,10,3),null));var config=Calibration();config["calibrated"]=false;Assert.Null(FishingGauge.Read(Track(2),new(0,0,10,3),config));Assert.Null(FishingGauge.Read(new(10,3,new byte[90]),new(0,0,10,3),Calibration()));}
    [Fact] public void RightToLeftTrackUsesDeclaredDirection()
    {var config=Calibration();config["direction"]="right_to_left";var pixels=Track(2).CopyPixels();for(var y=0;y<3;y++)for(var x=0;x<5;x++)for(var c=0;c<3;c++)(pixels[(y*10+x)*3+c],pixels[(y*10+9-x)*3+c])=(pixels[(y*10+9-x)*3+c],pixels[(y*10+x)*3+c]);Assert.Equal(.2,FishingGauge.Read(new(10,3,pixels),new(0,0,10,3),config));}
}
