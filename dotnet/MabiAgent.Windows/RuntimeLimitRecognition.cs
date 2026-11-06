using System.Security.Cryptography;
using OpenCvSharp;
namespace MabiAgent.Windows;
/// <summary>Positive runtime-expiry recognition only; never kills or restarts a process.</summary>
public sealed class RuntimeLimitRecognition:IDisposable
{
    private readonly List<(string Name,Mat Image,int X,int Y,string Hash)> templates=[];
    public RuntimeLimitRecognition(string root)
    {
        foreach(var (name,file,hash,x,y) in new[]{("message","session_expired_message.png","0a5e72b346857e692b3ad0ec9a7fdb2ae5e97ee8cb60d0ff3c1cef8faf14d064",137,-90),("end","session_expired_end.png","7527142839436951605a494329a61c73cbbbe365e8ab15668b1e56153c49d8a1",204,12)})
        {
            var path=Path.Combine(root,"assets/templates/session_interruptions",file);if(new FileInfo(path).Length>65536)throw new InvalidDataException("运行时限模板超过大小上限");
            var bytes=File.ReadAllBytes(path);if(bytes.Length>65536||Convert.ToHexStringLower(SHA256.HashData(bytes))!=hash)throw new InvalidDataException("运行时限模板变化");
            var image=Cv2.ImDecode(bytes,ImreadModes.Grayscale);if(image.Empty())throw new InvalidDataException("运行时限模板损坏");templates.Add((name,image,x,y,hash));
        }
    }
    public PageView? Recognize(RgbImage image)
    {
        using var mask=new Mat(image.Height,image.Width,MatType.CV_8UC1);var p=image.Pixels;var bytes=new byte[image.Width*image.Height];for(var i=0;i<bytes.Length;i++)bytes[i]=(byte)(p[i*3+1]>120&&p[i*3+1]-p[i*3]>70&&p[i*3+1]-p[i*3+2]>15?1:0);System.Runtime.InteropServices.Marshal.Copy(bytes,0,mask.Data,bytes.Length);
        using var labels=new Mat();using var stats=new Mat();using var centers=new Mat();var count=Cv2.ConnectedComponentsWithStats(mask,labels,stats,centers);using var rgb=image.ToMat();using var gray=new Mat();Cv2.CvtColor(rgb,gray,ColorConversionCodes.RGB2GRAY);
        for(var i=1;i<count;i++)
        {
            int x=stats.At<int>(i,0),y=stats.At<int>(i,1),w=stats.At<int>(i,2),h=stats.At<int>(i,3),area=stats.At<int>(i,4);var scale=w/466d;
            if(area<1500||w<160||h<20||(double)w/h is <=4 or >=14||scale is <.6 or >2)continue;var hits=new List<TemplateHit>();
            foreach(var t in templates)
            {
                var size=new Size((int)Math.Round(t.Image.Width*scale),(int)Math.Round(t.Image.Height*scale));using var reference=new Mat();Cv2.Resize(t.Image,reference,size,0,0,InterpolationFlags.Linear);
                var px=(int)Math.Round(x+t.X*scale);var py=(int)Math.Round(y+t.Y*scale);var margin=Math.Max(4,(int)Math.Round(6*scale));var l=Math.Max(0,px-margin);var top=Math.Max(0,py-margin);var r=Math.Min(image.Width,px+size.Width+margin);var b=Math.Min(image.Height,py+size.Height+margin);if(r-l<size.Width||b-top<size.Height)break;
                using var source=new Mat(gray,new Rect(l,top,r-l,b-top));using var scores=new Mat();Cv2.MatchTemplate(source,reference,scores,TemplateMatchModes.CCoeffNormed);Cv2.MinMaxLoc(scores,out _,out var score,out _,out var point);if(!double.IsFinite(score)||score<.95)break;
                hits.Add(new("runtime_limit_"+t.Name,score,new(l+point.X,top+point.Y,l+point.X+size.Width,top+point.Y+size.Height),t.Hash));
            }
            if(hits.Count==2)return new("restart_required",true,hits,new Dictionary<string,PixelPoint>(),hits.Select(h=>h.Box).ToArray());
        }
        return null;
    }
    public void Dispose(){foreach(var template in templates)template.Image.Dispose();}
}
