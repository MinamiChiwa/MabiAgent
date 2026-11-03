using OpenCvSharp;
namespace MabiAgent.Windows;

/// <summary>Recognizes the dialogue shell, independent of the actor's changing lines.</summary>
internal static class NpcDialogueRecognition
{
    public static PageView? Recognize(RgbImage image)
    {
        var sx=image.Width/1280d;var sy=image.Height/960d;
        var search=new PixelBox((int)(300*sx),(int)(50*sy),(int)(1000*sx),(int)(250*sy));
        var crop=image.Crop(search);var pixels=crop.Pixels;
        using var white=new Mat(crop.Height,crop.Width,MatType.CV_8UC1);
        using var green=new Mat(crop.Height,crop.Width,MatType.CV_8UC1);
        unsafe
        {
            for(var y=0;y<crop.Height;y++)
            {
                var w=(byte*)white.Ptr(y);var g=(byte*)green.Ptr(y);
                for(var x=0;x<crop.Width;x++)
                {
                    var i=(y*crop.Width+x)*3;int r=pixels[i],gr=pixels[i+1],b=pixels[i+2];
                    w[x]=(byte)(r>=242&&gr>=242&&b>=242?1:0);
                    g[x]=(byte)(gr>150&&r>80&&gr>=r&&b<100&&gr-b>80?1:0);
                }
            }
        }
        using var labels=new Mat();using var stats=new Mat();using var centers=new Mat();
        var count=Cv2.ConnectedComponentsWithStats(white,labels,stats,centers);
        using var greenLabels=new Mat();using var greenStats=new Mat();using var greenCenters=new Mat();
        var greens=Cv2.ConnectedComponentsWithStats(green,greenLabels,greenStats,greenCenters);
        PageView? result=null;
        for(var i=1;i<count;i++)
        {
            int x=stats.At<int>(i,0),y=stats.At<int>(i,1),w=stats.At<int>(i,2),h=stats.At<int>(i,3),area=stats.At<int>(i,4);
            if(w<220*sx||w>650*sx||h<80*sy||h>180*sy||area<w*h*.45)continue;
            int bodyLeft=x+w,bodyRight=x,tail=0;
            for(var col=x;col<x+w;col++)
            {
                if(labels.At<int>(y+h/2,col)==i){bodyLeft=Math.Min(bodyLeft,col);bodyRight=col;}
                if(labels.At<int>(y+h-Math.Max(2,(int)(10*sy)),col)==i)tail++;
            }
            if(bodyRight-bodyLeft<w*.6||tail<=0||tail>w*.3)continue;
            for(var j=1;j<greens;j++)
            {
                int bx=greenStats.At<int>(j,0),by=greenStats.At<int>(j,1),bw=greenStats.At<int>(j,2),bh=greenStats.At<int>(j,3),ba=greenStats.At<int>(j,4);
                if(bw<40*sx||bw>180*sx||bh<18*sy||bh>40*sy||ba<bw*bh*.45||bx<x||bx+bw>x+w||by<y-30*sy||by>y+15*sy)continue;
                if(result is not null)return null;
                var badge=new PixelBox(search.Left+bx,search.Top+by,search.Left+bx+bw,search.Top+by+bh);
                var target=new PixelBox(search.Left+x+w/2-(int)(25*sx),search.Top+y+(int)(40*sy),search.Left+x+w/2+(int)(25*sx),search.Top+y+(int)(60*sy));
                result=new("npc_dialogue",false,[],new Dictionary<string,PixelPoint>{{"dialog.advance",target.Center}},[badge],TargetRegions:new Dictionary<string,PixelBox>{{"dialog.advance",target}});
            }
        }
        return result;
    }
}
