using OpenCvSharp;
using System.Text.Json.Nodes;
namespace MabiAgent.Windows;

internal sealed record MapMotionEvidence(bool Verified,int Matches=0,int Inliers=0,double Scale=0,double Dx=0,double Dy=0,double Rotation=0)
{
    internal bool SameScale=>Verified&&Math.Abs(Scale-1)<.01&&Math.Abs(Rotation)<.5;
    internal JsonObject ToJson()=>new(){["verified"]=Verified,["matches"]=Matches,["inliers"]=Inliers,["scale"]=Scale,["dx"]=Dx,["dy"]=Dy,["rotation_degrees"]=Rotation};
    internal static MapMotionEvidence Measure(Mat before,Mat after)
    {
        using var first=new Mat();using var second=new Mat();Cv2.CvtColor(before,first,ColorConversionCodes.RGB2GRAY);Cv2.CvtColor(after,second,ColorConversionCodes.RGB2GRAY);
        using var orb=ORB.Create(2000);using var d1=new Mat();using var d2=new Mat();orb.DetectAndCompute(first,null,out var k1,d1);orb.DetectAndCompute(second,null,out var k2,d2);
        if(d1.Empty()||d2.Empty())return new(false);
        using var matcher=new BFMatcher(NormTypes.Hamming);var pairs=matcher.KnnMatch(d1,d2,2).Where(p=>p.Length==2&&p[0].Distance<.7*p[1].Distance).Select(p=>p[0]).ToArray();
        if(pairs.Length<12)return new(false,pairs.Length);
        using var source=Mat.FromArray(pairs.Select(p=>k1[p.QueryIdx].Pt).ToArray());using var dest=Mat.FromArray(pairs.Select(p=>k2[p.TrainIdx].Pt).ToArray());using var inliers=new Mat();using var matrix=Cv2.EstimateAffinePartial2D(source,dest,inliers);
        if(matrix is null||matrix.Empty())return new(false,pairs.Length);
        var a=matrix.At<double>(0,0);var b=matrix.At<double>(1,0);var count=Cv2.CountNonZero(inliers);
        return new(count>=20&&count>=pairs.Length*.5,pairs.Length,count,Math.Sqrt(a*a+b*b),matrix.At<double>(0,2),matrix.At<double>(1,2),Math.Atan2(b,a)*180/Math.PI);
    }
}
