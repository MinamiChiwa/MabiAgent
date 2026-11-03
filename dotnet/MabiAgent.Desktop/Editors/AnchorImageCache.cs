using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
namespace MabiAgent.Desktop.Editors;

// Anchor image thumbnails for the workbench. Decoded at a bounded width and cached by resolved
// path, write time and width, so lists never load full images and a re-captured file refreshes.
public static class AnchorImageCache
{
    const int Capacity=256;
    static readonly Dictionary<(string Path,long Stamp,int Width),Bitmap> cache=[];
    static Func<string,string>? resolve;
    public static void Attach(Func<string,string> resolver)=>resolve=resolver;
    public static Bitmap? Load(string? path,int width)
    {
        if(string.IsNullOrWhiteSpace(path)||resolve is null||width<=0)return null;
        try
        {
            var full=resolve(path);if(!File.Exists(full))return null;
            var key=(full,File.GetLastWriteTimeUtc(full).Ticks,width);
            if(cache.TryGetValue(key,out var hit))return hit;
            // Evicted bitmaps may still be on screen, so they are left to the GC rather than disposed.
            if(cache.Count>=Capacity)cache.Clear();
            Bitmap image;
            using(var stream=File.OpenRead(full))image=new Bitmap(stream);
            if(image.PixelSize.Width>width){image.Dispose();using var stream=File.OpenRead(full);image=Bitmap.DecodeToWidth(stream,width,BitmapInterpolationMode.HighQuality);}
            return cache[key]=image;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException){return null;}
    }
    public static string? FirstImagePath(WorkbenchAnchor? anchor)=>anchor?.States.FirstOrDefault(s=>s.IsImage&&s.Path.Length>0)?.Path;
}

public static class WorkbenchConverters
{
    static int Width(object? parameter)=>int.TryParse(parameter?.ToString(),NumberStyles.Integer,CultureInfo.InvariantCulture,out var w)?w:96;
    /// <summary>Image path → thumbnail.</summary>
    public static readonly IValueConverter PathImage=new FuncValueConverter<string?,object?,Bitmap?>((path,width)=>AnchorImageCache.Load(path,Width(width)));
    /// <summary>Anchor → thumbnail of its first image state.</summary>
    public static readonly IValueConverter AnchorImage=new FuncValueConverter<WorkbenchAnchor?,object?,Bitmap?>((anchor,width)=>AnchorImageCache.Load(AnchorImageCache.FirstImagePath(anchor),Width(width)));
    /// <summary>(anchor id, state id, document) → thumbnail of the step's target state.</summary>
    public static readonly IMultiValueConverter CardTarget=new FuncMultiValueConverter<object?,Bitmap?>(values=>
    {
        var list=values.ToArray();
        if(list.Length<3||list[0] is not string anchorId||list[2] is not WorkbenchDocument document||document.FindAnchor(anchorId) is not {} anchor)return null;
        var state=anchor.States.FirstOrDefault(s=>s.Id==list[1] as string)??anchor.States.FirstOrDefault();
        return AnchorImageCache.Load(state is {IsImage:true}?state.Path:AnchorImageCache.FirstImagePath(anchor),160);
    });
    /// <summary>Card action key → icon kind.</summary>
    public static readonly IValueConverter ActionIcon=new FuncValueConverter<string?,string>(action=>action switch
    {
        "observe"=>"search","click" or "right"=>"touch","scroll"=>"down","zoom_max"=>"add","drag"=>"grip",
        "key"=>"keyboard","text"=>"edit","wait" or "delay"=>"clock","branch"=>"chain","semantic"=>"play",_=>"queue"
    });
}
