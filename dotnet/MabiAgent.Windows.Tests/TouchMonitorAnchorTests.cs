using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;
public sealed class TouchMonitorAnchorTests
{
    [Theory]
    [InlineData(0,2160,2560,4320,740,2412)]
    [InlineData(-1920,0,0,1080,-1800,100)]
    [InlineData(1920,500,4480,1940,2196,685)]
    public void AnchorRemainsOnTargetMonitorWithNegativeAndOffsetCoordinates(int l,int t,int r,int b,int x,int y)
    {
        var monitor=new PixelBox(l,t,r,b);var gesture=new PixelBox(x,y,x+150,y+150);
        var first=ForwardedTouchGestureRunner.AnchorAwayFrom(gesture,new(10,10),monitor);
        var second=ForwardedTouchGestureRunner.AnchorAwayFrom(gesture,first.Center,monitor);
        foreach(var box in new[]{first,second})
        {
            Assert.True(monitor.Contains(new(box.Left,box.Top)));Assert.True(monitor.Contains(new(box.Right-1,box.Bottom-1)));
            Assert.True(box.Right<=gesture.Left||box.Left>=gesture.Right||box.Bottom<=gesture.Top||box.Top>=gesture.Bottom);
        }
        Assert.False(second.Contains(first.Center));
    }
    [Fact]public void NoUsableAnchorRejectsBeforeNativeInput()
        =>Assert.Throws<InputBlockedException>(()=>ForwardedTouchGestureRunner.AnchorAwayFrom(new(0,0,100,100),new(10,10),new(0,0,100,100)));
}
