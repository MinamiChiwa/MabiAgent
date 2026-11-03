using OpenCvSharp;
namespace MabiAgent.Windows;
/// <summary>Positive pixels only. Null means unknown, never an empty-device claim or ownership permission.</summary>
public static class SlotRecognition
{
    public static IReadOnlyList<int>? ReadyIndices(RgbImage image,IReadOnlyList<PixelBox> boxes)
    {
        var ready=new List<int>();for(var i=0;i<boxes.Count;i++){var box=boxes[i];if(!box.IsInside(image.Width,image.Height)||box.Width!=box.Height||box.Width<20)return null;var crop=image.Crop(box);var pixels=crop.Pixels;var side=box.Width;var total=0;var cyan=0;for(var y=0;y<side;y++)for(var x=0;x<side;x++){var radius=Radius(x,y,side);if(radius<=.41||radius>=.47)continue;total++;var p=(y*side+x)*3;if(pixels[p+2]-pixels[p]>40&&pixels[p+1]-pixels[p]>30&&pixels[p+2]>100)cyan++;}if(total>0&&cyan/(double)total>=.9)ready.Add(i);}return ready;
    }
    public static IReadOnlyList<int>? EmptyIndices(RgbImage image,IReadOnlyList<PixelBox> boxes)
    {
        var empty=new List<int>();for(var i=0;i<boxes.Count;i++){var box=boxes[i];if(!box.IsInside(image.Width,image.Height)||box.Width!=box.Height||box.Width<20)return null;using var rgb=image.Crop(box).ToMat();using var gray=new Mat();Cv2.CvtColor(rgb,gray,ColorConversionCodes.RGB2GRAY);using var edges=new Mat();Cv2.Canny(gray,edges,20,50);var side=box.Width;var inside=new List<double>();var edgeCount=0;var rims=Enumerable.Range(0,8).Select(_=>new List<byte>()).ToArray();var inners=Enumerable.Range(0,8).Select(_=>new List<byte>()).ToArray();for(var y=0;y<side;y++)for(var x=0;x<side;x++){var radius=Radius(x,y,side);var value=gray.At<byte>(y,x);if(radius<.35){inside.Add(value);if(edges.At<byte>(y,x)>0)edgeCount++;}var angle=(Math.Atan2(y-(side-1)/2d,x-(side-1)/2d)+Math.PI)/(2*Math.PI);var sector=Math.Min(7,(int)(angle*8));if(radius>.41&&radius<.47)rims[sector].Add(value);if(radius>.27&&radius<.34)inners[sector].Add(value);}
            var mean=inside.Average();var std=Math.Sqrt(inside.Average(v=>(v-mean)*(v-mean)));var edgeFraction=edgeCount/(double)inside.Count;if(edgeFraction>.005&&std>18)continue;for(var sector=0;sector<8;sector++)if(rims[sector].Count==0||inners[sector].Count==0||Median(rims[sector])-Median(inners[sector])<8)return null;if(edgeFraction<=.005&&std<=18)empty.Add(i);
        }return empty;
    }
    private static double Radius(int x,int y,int side)=>Math.Sqrt(Math.Pow(x-(side-1)/2d,2)+Math.Pow(y-(side-1)/2d,2))/side;
    private static double Median(List<byte> values){values.Sort();var n=values.Count;return n%2==0?(values[n/2-1]+values[n/2])/2d:values[n/2];}
}
