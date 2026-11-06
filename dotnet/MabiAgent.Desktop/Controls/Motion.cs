using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Rendering.Composition;
using MabiAgent.Desktop;
namespace MabiAgent.Desktop.Controls;

// Entrance motion played on the compositor. Logical Opacity and layout bounds never change,
// so a stalled render clock, offscreen capture or layout check always sees the final state.
internal static class Motion
{
    public static void Rotate(Visual target)
    {
        if(UiMotion.Reduced)return;
        if(ElementComposition.GetElementVisual(target) is not {} visual)return;
        visual.CenterPoint=new Vector3D(target.Bounds.Width/2,target.Bounds.Height/2,0);
        var rotation=visual.Compositor.CreateScalarKeyFrameAnimation();
        rotation.InsertKeyFrame(0f,0f);rotation.InsertKeyFrame(1f,(float)(Math.PI*2),new CubicEaseOut());
        rotation.Duration=TimeSpan.FromMilliseconds(420);
        visual.StartAnimation("RotationAngle",rotation);
    }
    static readonly TimeSpan FadeTime=TimeSpan.FromMilliseconds(160);
    static readonly TimeSpan PopTime=TimeSpan.FromMilliseconds(200);
    public static void FadeIn(Visual target)
    {
        if(UiMotion.Reduced)return;
        if(ElementComposition.GetElementVisual(target) is not {} visual)return;
        var fade=visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f,0f);fade.InsertKeyFrame(1f,1f,new CubicEaseOut());fade.Duration=FadeTime;
        visual.StartAnimation("Opacity",fade);
    }
    public static void SlideIn(Visual target,double distance)
    {
        if(UiMotion.Reduced)return;
        if(ElementComposition.GetElementVisual(target) is not {} visual)return;
        var at=visual.Offset;
        var slide=visual.Compositor.CreateVector3DKeyFrameAnimation();
        slide.InsertKeyFrame(0f,new Vector3D(at.X+distance,at.Y,at.Z));slide.InsertKeyFrame(1f,at,new CubicEaseOut());slide.Duration=PopTime;
        visual.StartAnimation("Offset",slide);
        FadeIn(target);
    }
    public static void PopIn(Visual target)
    {
        if(UiMotion.Reduced)return;
        if(ElementComposition.GetElementVisual(target) is not {} visual)return;
        var size=target.Bounds.Size;
        visual.CenterPoint=new Vector3D(size.Width/2,size.Height/2,0);
        var scale=visual.Compositor.CreateVector3DKeyFrameAnimation();
        scale.InsertKeyFrame(0f,new Vector3D(.96,.96,1));scale.InsertKeyFrame(1f,new Vector3D(1,1,1),new CubicEaseOut());scale.Duration=PopTime;
        visual.StartAnimation("Scale",scale);
        FadeIn(target);
    }
}
