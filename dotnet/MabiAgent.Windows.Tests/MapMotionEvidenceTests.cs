using OpenCvSharp;
using Xunit;
namespace MabiAgent.Windows.Tests;

public sealed class MapMotionEvidenceTests
{
    [Theory][InlineData(1,true)][InlineData(1.2,false)]
    public void TranslationAtZoomLimitIsDistinctFromFurtherScaling(double scale,bool saturated)
    {
        using var image=new Mat(480,640,MatType.CV_8UC3,Scalar.Black);var random=new Random(81);
        for(var i=0;i<350;i++)Cv2.Circle(image,new Point(random.Next(30,610),random.Next(30,450)),random.Next(2,9),new Scalar(random.Next(256),random.Next(256),random.Next(256)),-1);
        using var transform=Cv2.GetRotationMatrix2D(new Point2f(320,240),0,scale);transform.Set(0,2,transform.At<double>(0,2)+35);transform.Set(1,2,transform.At<double>(1,2)+33);
        using var moved=new Mat();Cv2.WarpAffine(image,moved,transform,image.Size());
        var evidence=MapMotionEvidence.Measure(image,moved);Assert.True(evidence.Verified);Assert.Equal(saturated,evidence.SameScale);Assert.InRange(evidence.Scale,scale-.01,scale+.01);
    }
    [Fact] public void FeaturelessMapCannotProveSaturationAfterChange()
    {using var blank=new Mat(300,400,MatType.CV_8UC3,Scalar.Black);Assert.False(MapMotionEvidence.Measure(blank,blank).SameScale);}
}
