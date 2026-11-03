using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;

public class ClientCoordinateTransformTests
{
    [Theory]
    [InlineData(96,1280,960,640,480)]
    [InlineData(120,1600,1200,800,600)]
    [InlineData(144,1920,1440,960,720)]
    [InlineData(192,2560,1920,1280,960)]
    public void PhysicalClientDimensionsDetermineScaleExactlyOnce(int dpi,int width,int height,int x,int y)
    {
        var transform=new ClientCoordinateTransform(1280,960,new(-1800,120,width,height,dpi));
        Assert.Equal(new PixelPoint(x,y),transform.ToClient(new PixelPoint(640,480)));
        Assert.Equal(new PixelPoint(x-1800,y+120),transform.ToScreen(new(640,480)));
        Assert.Equal(new PixelPoint(640,480),transform.ToReference(new(x,y)));
        Assert.Equal(new PixelPoint(x,y),transform.ScreenToClient(new(x-1800,y+120)));
        var layout=ReviewedLayout.For(width,height,dpi);
        Assert.Equal(transform.ToClient(new PixelBox(100,200,200,300)),layout.SearchBox(new(100,200,200,300)));
    }
    [Theory]
    [InlineData(96)] [InlineData(120)] [InlineData(144)] [InlineData(192)]
    public void SamePhysicalClientNeverChangesCoordinatesBecauseOfDpi(int dpi)
    {
        var transform=new ClientCoordinateTransform(1280,960,new(400,-300,1280,960,dpi));
        Assert.Equal(new PixelPoint(600,400),transform.ToClient(new PixelPoint(600,400)));
        Assert.Equal(new PixelPoint(1000,100),transform.ToScreen(new(600,400)));
    }
    [Fact] public void RegionsRoundOutwardsAndRejectOutOfBounds()
    {
        var transform=new ClientCoordinateTransform(1280,960,new(-500,-600,1600,1200,120));
        Assert.Equal(new PixelBox(12,25,38,50),transform.ToClient(new PixelBox(10,20,30,40)));
        Assert.Equal(new PixelBox(0,0,1600,1200),transform.ToClient(new PixelBox(0,0,1280,960)));
        Assert.Throws<ArgumentException>(()=>transform.ToClient(new PixelPoint(1280,10)));
        Assert.Throws<ArgumentException>(()=>transform.ToClient(new PixelBox(-1,0,10,10)));
        Assert.Throws<ArgumentException>(()=>transform.ClientToScreen(new(1600,0)));
        Assert.Throws<ArgumentException>(()=>transform.ScreenToClient(new(-501,-600)));
        Assert.Throws<ArgumentException>(()=>new ClientCoordinateTransform(0,960,transform.Geometry));
    }
    [Theory]
    [InlineData(ClickRegionShape.Rectangle)]
    [InlineData(ClickRegionShape.Ellipse)]
    public void RandomPointsStayInsideShapeAndCoverItsInterior(ClickRegionShape shape)
    {
        var box=new PixelBox(10,20,110,80);var random=new Random(314159);var quadrants=new int[4];var points=new HashSet<PixelPoint>();
        for(var i=0;i<10000;i++)
        {
            var point=box.RandomPoint(random,shape);Assert.True(box.Contains(point,shape));Assert.True(new PixelBox(30,32,90,68).Contains(point,shape));points.Add(point);
            quadrants[(point.X<60?0:1)+(point.Y<50?0:2)]++;
        }
        Assert.All(quadrants,n=>Assert.InRange(n,2100,2900));Assert.True(points.Count>1300);
    }
    [Theory] [InlineData(1,1)] [InlineData(1,10)] [InlineData(10,1)]
    public void SmallEllipsesRemainBounded(int width,int height)
    {
        var box=new PixelBox(0,0,width,height);for(var i=0;i<100;i++)Assert.True(box.Contains(box.RandomPoint(shape:ClickRegionShape.Ellipse),ClickRegionShape.Ellipse));
        Assert.Throws<ArgumentException>(()=>new PixelBox(0,0,0,0).RandomPoint());
    }
}
