using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using OpenCvSharp;
namespace MabiAgent.Desktop.Editors;
internal static class ImagePixels
{
    private static Mat Decode(Bitmap image)=>Cv2.ImDecode(StableAnchorCrop.Png(image),ImreadModes.Color);
    private static Bitmap Encode(Mat image){Cv2.ImEncode(".png",image,out var bytes);using var stream=new MemoryStream(bytes);return new Bitmap(stream);}
    public static Bitmap Crop(Bitmap source,PixelRect box){using var image=Decode(source);using var region=new Mat(image,new OpenCvSharp.Rect(box.X,box.Y,box.Width,box.Height));return Encode(region);}
    public static Bitmap Resize(Bitmap source,int width,int height){using var image=Decode(source);using var resized=new Mat();Cv2.Resize(image,resized,new OpenCvSharp.Size(width,height),interpolation:InterpolationFlags.Area);return Encode(resized);}
    public static Bitmap FromRgb(int width,int height,byte[] pixels){using var rgb=new Mat(height,width,MatType.CV_8UC3);Marshal.Copy(pixels,0,rgb.Data,pixels.Length);using var bgr=new Mat();Cv2.CvtColor(rgb,bgr,ColorConversionCodes.RGB2BGR);return Encode(bgr);}
    private static byte[] Convert(Bitmap image,ColorConversionCodes mode,int channels){using var bgr=Decode(image);using var converted=new Mat();Cv2.CvtColor(bgr,converted,mode);var bytes=new byte[converted.Width*converted.Height*channels];Marshal.Copy(converted.Data,bytes,0,bytes.Length);return bytes;}
    public static byte[] Rgb(Bitmap image)=>Convert(image,ColorConversionCodes.BGR2RGB,3);
    public static byte[] Gray(Bitmap image)=>Convert(image,ColorConversionCodes.BGR2GRAY,1);
    public static byte[] Preview(Bitmap image){using var pixels=Decode(image);foreach(var quality in new[]{88,75,60,45}){Cv2.ImEncode(".jpg",pixels,out var bytes,new ImageEncodingParam(ImwriteFlags.JpegQuality,quality));if(bytes.Length<=1048576)return bytes;}throw new InvalidDataException("预览超过1 MiB，请缩小框选区域");}
}
