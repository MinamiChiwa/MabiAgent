using System.Security.Cryptography;
using OpenCvSharp;
namespace MabiAgent.Windows;

/// <summary>识别固定运行时限提示并交给共享守卫暂停，保留当前消费意图。</summary>
public sealed class RuntimeLimitRecognition:IDisposable
{
    private readonly Mat message;
    private const string Hash="2a3199b7f7babdbb913c129fb39c50cb8390ad1d9d987cf02790c7087763dcc7";
    public RuntimeLimitRecognition(string root)
    {
        var path=Path.Combine(root,"assets/templates/session_interruptions/session_expired_message.png");
        if(new FileInfo(path).Length>65536)throw new InvalidDataException("运行时限模板超过大小上限");
        var bytes=File.ReadAllBytes(path);
        if(bytes.Length>65536||Convert.ToHexStringLower(SHA256.HashData(bytes))!=Hash)throw new InvalidDataException("运行时限模板变化");
        message=Cv2.ImDecode(bytes,ImreadModes.Grayscale);
        if(message.Empty())throw new InvalidDataException("运行时限模板损坏");
    }
    public PageView? Recognize(RgbImage image)
    {
        // 两行完整正文取自1280×960无损帧；按钮颜色、尺寸和文字均不参与识别。
        var scale=image.Width/1280d;var size=new Size((int)Math.Round(message.Width*scale),(int)Math.Round(message.Height*scale));
        var height=Math.Min(image.Height,(int)Math.Round(image.Height*.4));
        if(size.Width<1||size.Height<1||image.Width<size.Width||height<size.Height)return null;
        using var reference=new Mat();Cv2.Resize(message,reference,size,0,0,InterpolationFlags.Linear);
        using var rgb=image.ToMat();using var gray=new Mat();Cv2.CvtColor(rgb,gray,ColorConversionCodes.RGB2GRAY);
        using var source=new Mat(gray,new Rect(0,0,image.Width,height));using var scores=new Mat();
        Cv2.MatchTemplate(source,reference,scores,TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores,out _,out var score,out _,out var point);
        if(!double.IsFinite(score)||score<.95)return null;
        var hit=new TemplateHit("runtime_limit_message",score,new(point.X,point.Y,point.X+size.Width,point.Y+size.Height),Hash);
        return new("restart_required",true,[hit],new Dictionary<string,PixelPoint>(),[hit.Box]);
    }
    public void Dispose()=>message.Dispose();
}
