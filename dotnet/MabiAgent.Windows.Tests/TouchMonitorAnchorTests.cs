using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;
public sealed class TouchMonitorAnchorTests
{
    [Theory]
    [InlineData(0,0,3840,4320,1800,2400,1800,2400)]
    [InlineData(-1920,0,3840,4320,1800,2400,3720,2400)]
    [InlineData(0,-2160,3840,2160,500,-1800,500,360)]
    [InlineData(-1920,-2160,3840,4320,-1800,-2000,120,160)]
    public void NativeInjectionUsesVirtualDesktopOriginAndPreservesAbsoluteSource(int left,int top,int right,int bottom,int x,int y,int expectedX,int expectedY)
    {
        var original=TouchNative.Contact(2,new(x,y),TouchNative.Down,123);
        var frame=new[]{original};
        var injected=Assert.Single(SyntheticTouchDevice.ToInjectionFrame(frame,new(left,top,right,bottom)));
        Assert.Equal(expectedX,injected.Touch.Pointer.Pixel.X);Assert.Equal(expectedY,injected.Touch.Pointer.Pixel.Y);
        Assert.Equal(expectedX,injected.Touch.Pointer.Raw.X);Assert.Equal(expectedY,injected.Touch.Pointer.Raw.Y);
        Assert.Equal(x,frame[0].Touch.Pointer.Pixel.X);Assert.Equal(y,frame[0].Touch.Pointer.Pixel.Y);
        Assert.Equal(original.Touch.Pointer.Target,injected.Touch.Pointer.Target);Assert.Equal(original.Touch.Pointer.Id,injected.Touch.Pointer.Id);Assert.Equal(original.Touch.Pointer.Flags,injected.Touch.Pointer.Flags);
    }
    [Fact]public void OutsideVirtualDesktopRejectsBeforeInjection()
        =>Assert.Throws<InputBlockedException>(()=>SyntheticTouchDevice.ToInjectionFrame([TouchNative.Contact(1,new(100,100),TouchNative.Down)],new(-100,-100,100,100)));
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
