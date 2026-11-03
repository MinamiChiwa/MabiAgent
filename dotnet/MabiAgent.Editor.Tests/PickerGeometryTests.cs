using MabiAgent.Desktop.Editors;
using Xunit;

public sealed class PickerGeometryTests
{
    [Theory]
    [InlineData(1280,960,200,150,400,300)]
    [InlineData(1600,1200,250,187,500,375)]
    [InlineData(1920,1440,300,225,600,450)]
    [InlineData(2560,1920,400,300,800,600)]
    public void PhysicalSelectionNormalizesAcrossDisplayScales(int width,int height,int x,int y,int w,int h)
    {
        var result=EditorSelectionGeometry.ToReference(new(x,y,x+w,y+h),width,height,1280,960);
        Assert.InRange(result.Left,199,200);Assert.InRange(result.Top,149,150);
        Assert.InRange(result.Width,400,401);Assert.InRange(result.Height,300,301);
    }
    [Fact] public void NegativeDesktopOriginIsRemovedExactlyOnce()
    {Assert.Equal((200,150),EditorSelectionGeometry.ClientPoint(-1400,250,-1600,100,1600,1200));}
    [Fact] public void MovingWindowDoesNotChangeClientPoint()
    {Assert.Equal(EditorSelectionGeometry.ClientPoint(300,250,100,100,1280,960),EditorSelectionGeometry.ClientPoint(-1000,550,-1200,400,1280,960));}
    [Fact] public void ReverseDragProducesOrderedRectangle()
    {Assert.Equal(new SelectionBounds(199,99,400,300),EditorSelectionGeometry.FromDrag(399,299,199,99,800,600));}
    [Fact] public void CircleUsesShorterDragSide()
    {var box=EditorSelectionGeometry.FromDrag(300,300,100,250,800,600,true);Assert.Equal(51,box.Width);Assert.Equal(box.Width,box.Height);}
    [Fact] public void SinglePixelSurvivesDownscaling()
    {var box=EditorSelectionGeometry.ToReference(new(2559,1919,2560,1920),2560,1920,1280,960);Assert.Equal(new SelectionBounds(1279,959,1280,960),box);}
    [Fact] public void OutsideSelectionIsRejected()
    {Assert.Throws<ArgumentException>(()=>EditorSelectionGeometry.ToReference(new(-1,0,10,10),1280,960,1280,960));}
}
