using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
namespace MabiAgent.Windows;

public sealed record QuantityValue(long Quantity,long? Capacity=null,bool CapacityExact=true);
public static class QuantityParser
{
    public static QuantityValue? Parse(string raw,string format,long? expectedRequired=null,string prefix="",string suffix="")
    {
        if(raw.Length>128)return null;var text=raw.Normalize(NormalizationForm.FormKC).Trim();
        const string number="(?:[0-9]{1,3}(?:,[0-9]{3})+|[0-9]+)";
        var pattern=format switch {
            "integer"=>"([0-9]+)",
            "grouped_integer"=>"("+number+")",
            "available_required"=>"([0-9]+)/([0-9]+)",
            "fixed_ratio"=>"([0-9]+(?:[.][0-9]{1,2})?)/([0-9]+(?:[.][0-9]{1,2})?)",
            "labelled_integer"=>Regex.Escape(prefix)+"([0-9]+)"+Regex.Escape(suffix),
            "cost_available"=>"("+number+")[ ]*/[ ]*("+number+")([+])?",
            _=>throw new ArgumentException("未知数量格式")};
        var m=Regex.Match(text,pattern,RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        if(!m.Success||m.Index!=0||m.Length!=text.Length)return null;
        try
        {
            long ParseLong(string s)=>long.Parse(s.Replace(",",""),NumberStyles.None,CultureInfo.InvariantCulture);
            if(format=="fixed_ratio"){var q=checked((long)(decimal.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)*100));var cap=checked((long)(decimal.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture)*100));return cap>0?new(q,cap):null;}
            var value=ParseLong(m.Groups[1].Value);
            if(format=="available_required"&&(expectedRequired is null or <=0||ParseLong(m.Groups[2].Value)!=expectedRequired))return null;
            if(format=="cost_available")return new(value,checked(ParseLong(m.Groups[2].Value)+(m.Groups[3].Success?1:0)),!m.Groups[3].Success);
            return new(value);
        }catch(Exception e)when(e is FormatException or OverflowException){return null;}
    }
}
public sealed record OcrLine(string Text,double Confidence);
public interface IOcrEngine { OcrLine? Recognize(int width,int height,byte[] grayPixels); }
public interface IColorOcrEngine : IOcrEngine { OcrLine? RecognizeRgb(RgbImage image); }
public sealed record QuantitySpec(long ItemId,string Scope,PixelBox Region,string Format="integer",long? ExpectedRequired=null,double MinimumConfidence=.75,int[]? Thresholds=null,bool IncludeRed=false,string Prefix="",string Suffix="",int? ClientWidth=null,int? ClientHeight=null);
public sealed record ThresholdReading(int Threshold,string? Text,double? Confidence,QuantityValue? Value,string? Error=null);
public sealed record QuantityReading(long ItemId,string Scope,QuantityValue? Value,double? Confidence,PixelBox NativeRegion,string RegionSha256,IReadOnlyList<ThresholdReading> Readings);
public sealed class ScreenQuantityReader(IOcrEngine engine)
{
    private readonly object cacheGate=new();
    private RgbImage? cachedFrame;
    private readonly Dictionary<(int Width,int Height,string Hash),OcrLine?> frameReadings=[];
    private OcrLine? Recognize(RgbImage frame,int width,int height,byte[] pixels)
    {
        var key=(width,height,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)));
        lock(cacheGate)
        {
            if(!ReferenceEquals(cachedFrame,frame)){cachedFrame=frame;frameReadings.Clear();}
            if(frameReadings.TryGetValue(key,out var cached))return cached;
            var line=engine.Recognize(width,height,pixels);
            if(frameReadings.Count>=256)frameReadings.Clear();
            frameReadings[key]=line;return line;
        }
    }
    public string? ColorText(RgbImage image,PixelBox box,double minimum=.95)
    {
        if(engine is not IColorOcrEngine color)return null;
        try
        {
            var line=color.RecognizeRgb(image.Crop(box));
            return line is not null&&double.IsFinite(line.Confidence)&&line.Confidence>=minimum&&line.Confidence<=1
                ?line.Text.Normalize(NormalizationForm.FormKC).Trim():null;
        }
        catch(Exception e)when(e is IOException or InvalidOperationException or ArgumentException or TimeoutException){return null;}
    }
    public bool ExactColor(RgbImage image,PixelBox box,string expected,double minimum=.95)
    {
        if(engine is not IColorOcrEngine color||string.IsNullOrWhiteSpace(expected))return false;
        try
        {
            var line=color.RecognizeRgb(image.Crop(box));
            return line is not null&&double.IsFinite(line.Confidence)&&line.Confidence>=minimum&&line.Confidence<=1
                &&string.Equals(line.Text.Normalize(NormalizationForm.FormKC).Trim(),expected.Normalize(NormalizationForm.FormKC).Trim(),StringComparison.OrdinalIgnoreCase);
        }
        catch(Exception e)when(e is IOException or InvalidOperationException or ArgumentException or TimeoutException){return false;}
    }
    public PixelBox? FindGridLabel(RgbImage image,PixelBox surface,int columns,string expected,double minimum=.75)
    {
        if(columns is <1 or >8||!surface.IsInside(image.Width,image.Height)||surface.Width/columns<16)throw new ArgumentException("物品列表区域无效");
        if(engine is not IColorOcrEngine)return null;
        var pixels=image.Pixels;var step=surface.Width/columns;var margin=columns==1?0:5;var support=new int[surface.Height];
        static bool IsInk(int r,int g,int b){var max=Math.Max(r,Math.Max(g,b));var min=Math.Min(r,Math.Min(g,b));return max>90&&max<210&&(max-min<32||g-r>12&&g-b>0&&g-r<65);}
        // Unity keeps a partial row after scrolling. Locate the shared muted
        // label baselines instead of assuming the first row stayed at its origin.
        for(var col=0;col<columns;col++)for(var y=surface.Top;y<surface.Bottom;y++)
        {
            var ink=0;
            for(var x=surface.Left+col*step+margin;x<surface.Left+(col+1)*step-margin;x++)
            {var i=(y*image.Width+x)*3;if(IsInk(pixels[i],pixels[i+1],pixels[i+2]))ink++;}
            if(ink>=5)support[y-surface.Top]++;
        }
        var groups=new List<(int Top,int Bottom)>();
        for(var y=0;y<support.Length;y++)if(support[y]>=2)
        {if(groups.Count==0||y-groups[^1].Bottom>3)groups.Add((y,y));else groups[^1]=(groups[^1].Top,y);}
        // The last row may contain only one item.
        var tail=groups.Count==0?0:groups[^1].Bottom+45;
        for(var y=tail;y<support.Length;y++)if(support[y]>0)
        {if(groups.Count==0||y-groups[^1].Bottom>3)groups.Add((y,y));else groups[^1]=(groups[^1].Top,y);}
        foreach(var row in groups.Where(g=>g.Bottom-g.Top is >=7 and <=28))for(var col=0;col<columns;col++)
        {
            var box=new PixelBox(surface.Left+col*step+margin,Math.Max(surface.Top,surface.Top+row.Top-3),surface.Left+(col+1)*step-margin,Math.Min(surface.Bottom,surface.Top+row.Bottom+4));
            var left=box.Right;var right=box.Left;
            for(var x=box.Left;x<box.Right;x++)for(var y=surface.Top+row.Top;y<=surface.Top+row.Bottom;y++){var i=(y*image.Width+x)*3;if(IsInk(pixels[i],pixels[i+1],pixels[i+2])){left=Math.Min(left,x);right=Math.Max(right,x);break;}}
            if(left>right)continue;
            box=box with{Left=Math.Max(box.Left,left-4),Right=Math.Min(box.Right,right+5)};
            if(ExactColor(image,box,expected,minimum))return box;
        }
        return null;
    }
    public string? Text(RgbImage image,PixelBox box,double minimum=.90)
    {
        string? accepted=null;var crop=image.Crop(box);
        foreach(var threshold in new[]{90,140})
        {
            OcrLine? line;try{var bitmap=CreateNeutralTextBitmap(crop,threshold,out var w,out var h);line=Recognize(image,w,h,bitmap);}catch(TimeoutException){return null;}
            if(line is null||!double.IsFinite(line.Confidence)||line.Confidence<minimum||line.Confidence>1)return null;
            var value=line.Text.Normalize(NormalizationForm.FormKC).Trim();
            if(value.Length is <1 or >64||value.Any(char.IsControl)||accepted is not null&&accepted!=value)return null;accepted=value;
        }
        return accepted;
    }
    private static byte[] CreateNeutralTextBitmap(RgbImage crop,int threshold,out int width,out int height)
    {
        var p=crop.Pixels;var mask=Enumerable.Repeat((byte)255,crop.Width*crop.Height).ToArray();var columns=new int[crop.Width];
        for(var y=0;y<crop.Height;y++)for(var x=0;x<crop.Width;x++)
        {var i=y*crop.Width+x;int r=p[i*3],g=p[i*3+1],b=p[i*3+2];if(Math.Min(r,Math.Min(g,b))>=threshold&&Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))<45){mask[i]=0;columns[x]++;}}
        var groups=new List<(int Left,int Right,int Ink)>();var left=-1;var right=-1;var ink=0;var gap=Math.Max(4,crop.Height/2);
        for(var x=0;x<columns.Length;x++)if(columns[x]>=2)
        {if(left>=0&&x-right>gap){groups.Add((left,right,ink));left=-1;ink=0;}if(left<0)left=x;right=x;ink+=columns[x];}
        if(left>=0)groups.Add((left,right,ink));var ordered=groups.OrderByDescending(g=>g.Ink).ToArray();
        // Discard only small isolated glints. Ambiguous multiple text groups retain
        // the entire region instead of silently truncating a name.
        if(ordered.Length>0&&(ordered.Length==1||ordered[0].Ink>ordered.Skip(1).Sum(g=>g.Ink)*4))
        {left=Math.Max(0,ordered[0].Left-3);right=Math.Min(crop.Width,ordered[0].Right+4);}
        else {left=0;right=crop.Width;}
        width=right-left;height=crop.Height;var result=new byte[width*height];for(var y=0;y<height;y++)mask.AsSpan(y*crop.Width+left,width).CopyTo(result.AsSpan(y*width));return result;
    }
    public QuantityReading Read(RgbImage image,QuantitySpec spec)
    {
        var thresholds=spec.Thresholds??[165,210];
        if(spec.ItemId<=0||string.IsNullOrWhiteSpace(spec.Scope)||!spec.Region.IsInside(image.Width,image.Height)||!double.IsFinite(spec.MinimumConfidence)||spec.MinimumConfidence is <=0 or >1||thresholds.Length<2||thresholds.Distinct().Count()!=thresholds.Length||thresholds.Any(t=>t is <=0 or >=255)||spec.ClientWidth is {} w&&w!=image.Width||spec.ClientHeight is {} h&&h!=image.Height)throw new ArgumentException("数量读取需要校准区域、物品、范围和独立阈值");
        var crop=image.Crop(spec.Region);var readings=new List<ThresholdReading>();
        foreach(var threshold in thresholds)
        {
            var bitmap=CreateBitmap(crop,threshold,spec.IncludeRed,out var width,out var height);
            try{var line=Recognize(image,width,height,bitmap);var valid=line is not null&&double.IsFinite(line.Confidence)&&line.Confidence>=spec.MinimumConfidence&&line.Confidence<=1;var value=valid?QuantityParser.Parse(line!.Text,spec.Format,spec.ExpectedRequired,spec.Prefix,spec.Suffix):null;readings.Add(new(threshold,line?.Text,line?.Confidence,value));}
            catch(Exception e)when(e is IOException or InvalidOperationException or ArgumentException or TimeoutException){readings.Add(new(threshold,null,null,null,e.GetType().Name));}
        }
        var accepted=readings.All(r=>r.Value is not null)&&readings.Select(r=>r.Value).Distinct().Count()==1;
        return new(spec.ItemId,spec.Scope,accepted?readings[0].Value:null,accepted?readings.Min(r=>r.Confidence):null,spec.Region,crop.Sha256,readings);
    }
    // A navigation quote changes while walking. Its first valid reading is a
    // budget decision, never an inventory quantity or a cross-frame stability test.
    public QuantityValue? ReadNavigationQuoteOnce(RgbImage image,PixelBox region,int threshold,double minimum)
    {
        if(!region.IsInside(image.Width,image.Height)||threshold is <=0 or >=255||!double.IsFinite(minimum)||minimum is <=0 or >1)throw new ArgumentException("导航费用读取参数无效");
        try
        {
            var bitmap=CreateBitmap(image.Crop(region),threshold,false,out var width,out var height);
            var line=Recognize(image,width,height,bitmap);
            return line is not null&&double.IsFinite(line.Confidence)&&line.Confidence>=minimum&&line.Confidence<=1?QuantityParser.Parse(line.Text,"cost_available"):null;
        }
        catch(Exception e)when(e is IOException or InvalidOperationException or ArgumentException or TimeoutException){return null;}
    }
    public bool Exact(RgbImage image,PixelBox box,string expected,int[]? thresholds=null,double minimum=.75)
    {
        if(string.IsNullOrWhiteSpace(expected))return false;thresholds??=[90,140];
        if(thresholds.Length<2||thresholds.Distinct().Count()!=thresholds.Length||thresholds.Any(t=>t is <=0 or >=255)||!double.IsFinite(minimum)||minimum is <=0 or >1)throw new ArgumentException("需要独立文字阈值");
        var crop=image.Crop(box);
        foreach(var threshold in thresholds){try{var bitmap=CreateBitmap(crop,threshold,false,out var w,out var h);var line=Recognize(image,w,h,bitmap);if(line is null||!double.IsFinite(line.Confidence)||line.Confidence<minimum||line.Confidence>1||!string.Equals(line.Text.Normalize(NormalizationForm.FormKC).Trim(),expected.Normalize(NormalizationForm.FormKC).Trim(),StringComparison.OrdinalIgnoreCase))return false;}catch(Exception e)when(e is IOException or InvalidOperationException or ArgumentException or TimeoutException){return false;}}return true;
    }
    private static byte[] CreateBitmap(RgbImage crop,int threshold,bool includeRed,out int width,out int height)
    {
        var pixels=crop.Pixels;var mask=new byte[crop.Width*crop.Height];int left=crop.Width,top=crop.Height,bottom=-1;
        for(var i=0;i<mask.Length;i++){var r=pixels[i*3];var g=pixels[i*3+1];var b=pixels[i*3+2];var red=includeRed&&r>threshold*.65&&r>2*g&&r>2*b;mask[i]=r*.299+g*.587+b*.114>threshold||red?(byte)0:(byte)255;if(red){left=Math.Min(left,i%crop.Width);top=Math.Min(top,i/crop.Width);bottom=Math.Max(bottom,i/crop.Width);}}
        if(bottom<0){width=crop.Width;height=crop.Height;return mask;}left=Math.Max(0,left-2);top=Math.Max(0,top-2);bottom=Math.Min(crop.Height,bottom+3);width=crop.Width-left;height=bottom-top;var result=new byte[width*height];for(var y=0;y<height;y++)mask.AsSpan((top+y)*crop.Width+left,width).CopyTo(result.AsSpan(y*width));return result;
    }
}
