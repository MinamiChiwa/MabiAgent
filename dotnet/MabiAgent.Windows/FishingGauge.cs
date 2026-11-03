using System.Text.Json.Nodes;
namespace MabiAgent.Windows;

/// <summary>Reads only an explicitly calibrated horizontal fill track, never scene colours.</summary>
internal static class FishingGauge
{
    internal static double? Read(RgbImage image,PixelBox box,JsonNode? calibration)
    {
        if(calibration?["calibrated"]?.GetValue<bool>()!=true||!box.IsInside(image.Width,image.Height)||box.Width<2)return null;
        if(calibration["fill_rgb"] is not JsonArray{Count:3} fill||calibration["empty_rgb"] is not JsonArray{Count:3} empty)return null;
        var a=fill.Select(v=>v!.GetValue<int>()).ToArray();var b=empty.Select(v=>v!.GetValue<int>()).ToArray();
        var tolerance=calibration["tolerance"]?.GetValue<int>()??20;
        if(tolerance is <0 or >100||a.Concat(b).Any(v=>v is <0 or >255)||a.Zip(b).All(p=>Math.Abs(p.First-p.Second)<=2*tolerance))return null;
        var reverse=calibration["direction"]?.GetValue<string>()=="right_to_left";
        if(calibration["scan_rows"]?.GetValue<bool>()==true)
        {
            var readings=new List<double>();var pixelsByRow=image.Pixels;
            for(var y=box.Top;y<box.Bottom;y++)
            {
                var count=0;var emptySeen=false;var valid=true;
                for(var column=0;column<box.Width;column++)
                {
                    var x=reverse?box.Right-1-column:box.Left+column;var offset=(y*image.Width+x)*3;
                    if(Match(pixelsByRow,offset,a,tolerance)){if(emptySeen){valid=false;break;}count++;}
                    else if(Match(pixelsByRow,offset,b,15))emptySeen=true;
                    else{valid=false;break;}
                }
                if(valid)readings.Add(count/(double)box.Width);
            }
            // The calibrated floating track moves vertically with the hook icon.
            // Require several agreeing interior rows, excluding its dark outline.
            var group=readings.GroupBy(v=>v).OrderByDescending(g=>g.Count()).FirstOrDefault();
            return group is not null&&group.Count()>=3?group.Key:null;
        }
        var pixels=image.Pixels;var filled=0;var reachedEmpty=false;
        for(var column=0;column<box.Width;column++)
        {
            var x=reverse?box.Right-1-column:box.Left+column;var fills=0;var empties=0;
            for(var y=box.Top;y<box.Bottom;y++)
            {
                var offset=(y*image.Width+x)*3;
                if(Match(pixels,offset,a,tolerance))fills++;else if(Match(pixels,offset,b,tolerance))empties++;
            }
            // Reject obscured or ambiguous tracks instead of reporting a false low.
            if(fills>=Math.Ceiling(box.Height*.7)){if(reachedEmpty)return null;filled++;}
            else if(empties>=Math.Ceiling(box.Height*.7))reachedEmpty=true;
            else return null;
        }
        return filled/(double)box.Width;
    }
    private static bool Match(ReadOnlySpan<byte> pixels,int offset,int[] rgb,int tolerance)
        =>Math.Abs(pixels[offset]-rgb[0])<=tolerance&&Math.Abs(pixels[offset+1]-rgb[1])<=tolerance&&Math.Abs(pixels[offset+2]-rgb[2])<=tolerance;
}
