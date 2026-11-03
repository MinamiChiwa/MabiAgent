using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;
public class GeometryQuantityTests
{
    [Fact] public void SameFrameOcrReusesInferenceButKeepsConfidenceParsingAndNewFramesIndependent()
    {
        var pixels=new byte[8*8*3];for(var i=0;i<32*3;i++)pixels[i]=200;
        var frame=new RgbImage(8,8,pixels);var engine=new CountingOcr();var reader=new ScreenQuantityReader(engine);var spec=new QuantitySpec(1,"bag",new(0,0,8,8));
        Assert.Equal(7,reader.Read(frame,spec).Value?.Quantity);Assert.Equal(7,reader.Read(frame,spec).Value?.Quantity);
        Assert.Equal(2,engine.Calls);Assert.Null(reader.Read(frame,spec with{MinimumConfidence=.99}).Value);Assert.Equal(2,engine.Calls);
        Assert.False(reader.Exact(frame,spec.Region,"8",[165,210]));Assert.True(reader.Exact(frame,spec.Region,"7",[165,210]));Assert.Equal(2,engine.Calls);
        Assert.Equal(7,reader.Read(new(8,8,pixels),spec).Value?.Quantity);Assert.Equal(4,engine.Calls);
    }
    [Fact] public void OcrTimeoutIsNotCachedAsASuccessOrPermanentFailure()
    {
        var engine=new CountingOcr{Timeout=true};var reader=new ScreenQuantityReader(engine);var frame=new RgbImage(8,8,new byte[192]);var spec=new QuantitySpec(1,"bag",new(0,0,8,8));
        Assert.Null(reader.Read(frame,spec).Value);Assert.Equal(2,engine.Calls);engine.Timeout=false;
        Assert.Equal(7,reader.Read(frame,spec).Value?.Quantity);Assert.Equal(3,engine.Calls);
    }
    private sealed class CountingOcr:IOcrEngine
    {public int Calls;public bool Timeout;public OcrLine? Recognize(int width,int height,byte[] pixels){Calls++;if(Timeout)throw new TimeoutException();return new("7",.95);}}
    [Fact] public void LayoutPreservesNativeGeometryAcrossDpiAndRejectsUnreviewedAspect()
    {
        var layout=ReviewedLayout.For(1600,1200,96);
        Assert.Equal(new PixelBox(12,25,38,50),layout.SearchBox(new PixelBox(10,20,30,40)));
        Assert.Equal(layout.SearchBox(new PixelBox(10,20,30,40)),ReviewedLayout.For(1600,1200,120).SearchBox(new PixelBox(10,20,30,40)));
        Assert.Throws<ArgumentException>(()=>ReviewedLayout.For(1920,1080,96));
    }
    [Theory]
    [InlineData("１２３","integer",null,123)]
    [InlineData("1,234","grouped_integer",null,1234)]
    [InlineData("8/10","available_required",10L,8)]
    public void StrictQuantityFormats(string text,string format,long? required,long expected)
        =>Assert.Equal(expected,QuantityParser.Parse(text,format,required)?.Quantity);
    [Theory]
    [InlineData("1O","integer",null)]
    [InlineData("1,23","grouped_integer",null)]
    [InlineData("1.9萬","grouped_integer",null)]
    [InlineData("8/11","available_required",10L)]
    [InlineData("2/0","fixed_ratio",null)]
    public void AmbiguityNeverBecomesZero(string text,string format,long? required)
        =>Assert.Null(QuantityParser.Parse(text,format,required));
    [Fact] public void FixedRatiosAndCappedHoldingsPreserveScope()
    {
        Assert.Equal(new QuantityValue(123,450,true),QuantityParser.Parse("1.23/4.50","fixed_ratio"));
        Assert.Equal(new QuantityValue(3,10000,false),QuantityParser.Parse("3/9,999+","cost_available"));
    }
}
