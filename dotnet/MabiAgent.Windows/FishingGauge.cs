using System.Text.Json.Nodes;
namespace MabiAgent.Windows;

/// <summary>Reads only an explicitly calibrated horizontal fill track, never scene colours.</summary>
internal static class FishingGauge
{
    internal readonly record struct Located(PixelBox Box,double Value);

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
            var readings=new List<(int Y,int Count)>();var pixelsByRow=image.Pixels;
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
                if(valid)readings.Add((y,count));
            }
            // The calibrated floating track moves vertically with the hook icon.
            // Prefer adjacent filled interior rows: the dark outline and surrounding
            // background can otherwise outvote the track with false zero readings.
            var groups=readings.GroupBy(r=>r.Count).Select(g=>new{Count=g.Key,Rows=ConsecutiveRows(g.Select(r=>r.Y))})
                .Where(g=>g.Rows>=3).OrderByDescending(g=>g.Count>0).ThenByDescending(g=>g.Rows).ToArray();
            return groups.FirstOrDefault() is{} group?group.Count/(double)box.Width:null;
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
    internal static Located? Find(RgbImage image,PixelBox search,JsonNode? calibration,int expectedWidth)
    {
        if(calibration?["calibrated"]?.GetValue<bool>()!=true||!search.IsInside(image.Width,image.Height)||expectedWidth<8)return null;
        if(calibration["fill_rgb"] is not JsonArray{Count:3} fill||calibration["empty_rgb"] is not JsonArray{Count:3} empty)return null;
        var a=fill.Select(v=>v!.GetValue<int>()).ToArray();var b=empty.Select(v=>v!.GetValue<int>()).ToArray();
        var tolerance=calibration["tolerance"]?.GetValue<int>()??20;
        if(tolerance is <0 or >100||a.Concat(b).Any(v=>v is <0 or >255)||a.Zip(b).All(p=>Math.Abs(p.First-p.Second)<=2*tolerance))return null;
        var minimum=Math.Max(8,(int)Math.Floor(expectedWidth*.75));var maximum=(int)Math.Ceiling(expectedWidth*1.3);
        var minimumSegment=Math.Max(3,(int)Math.Ceiling(expectedWidth*.05));
        var rows=new List<(int Y,int Left,int Right,double Value)>();var pixels=image.Pixels;
        for(var y=search.Top;y<search.Bottom;y++)
        {
            var x=search.Left;
            while(x<search.Right)
            {
                while(x<search.Right&&!Match(pixels,(y*image.Width+x)*3,a,tolerance))x++;
                var left=x;while(x<search.Right&&Match(pixels,(y*image.Width+x)*3,a,tolerance))x++;
                var filled=x-left;var emptyStart=x;
                while(x<search.Right&&Match(pixels,(y*image.Width+x)*3,b,15))x++;
                var emptyCount=x-emptyStart;var width=x-left;
                if(filled>=minimumSegment&&emptyCount>=minimumSegment&&width>=minimum&&width<=maximum)
                    rows.Add((y,left,x,filled/(double)width));
                if(x==left)x++;
            }
        }
        // The bar has several adjacent interior rows with almost identical bounds.
        // Requiring that shape rejects isolated scene colours without scanning the page.
        var group=rows.GroupBy(r=>((r.Left+2)/4,(r.Right+2)/4)).Select(g=>g.OrderBy(r=>r.Y).ToArray())
            .Where(g=>ConsecutiveRows(g)>=5)
            .OrderByDescending(g=>g.Length).ThenBy(g=>Math.Abs(g.Select(r=>r.Right-r.Left).Average()-expectedWidth)).FirstOrDefault();
        if(group is null)return null;
        var ordered=group.OrderBy(r=>r.Value).ToArray();var middle=ordered[ordered.Length/2];
        var leftBound=(int)Math.Round(group.Average(r=>r.Left));var rightBound=(int)Math.Round(group.Average(r=>r.Right));
        return new(new(leftBound,group.Min(r=>r.Y),rightBound,group.Max(r=>r.Y)+1),middle.Value);
    }
    private static int ConsecutiveRows((int Y,int Left,int Right,double Value)[] rows)
        =>ConsecutiveRows(rows.Select(r=>r.Y));
    private static int ConsecutiveRows(IEnumerable<int> rows)
    {
        var longest=0;var current=0;int? previous=null;
        foreach(var y in rows)
        {
            current=previous is not null&&y==previous+1?current+1:1;
            longest=Math.Max(longest,current);previous=y;
        }
        return longest;
    }
    private static bool Match(ReadOnlySpan<byte> pixels,int offset,int[] rgb,int tolerance)
        =>Math.Abs(pixels[offset]-rgb[0])<=tolerance&&Math.Abs(pixels[offset+1]-rgb[1])<=tolerance&&Math.Abs(pixels[offset+2]-rgb[2])<=tolerance;
}
