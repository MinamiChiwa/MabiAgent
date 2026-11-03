using OpenCvSharp;
namespace MabiAgent.Windows;

/// <summary>Port of recognition._profile_hit: native ROI, calibrated resize, glyph modes, contrast/luminance veto and ambiguity suppression.</summary>
public sealed class TemplateMatcher(NavigationProfile configuration):IDisposable
{
    private readonly NavigationProfile profile=configuration.Snapshot();
    static TemplateMatcher()=>Cv2.SetNumThreads(1);
    private readonly object gate=new();
    private readonly PreparedCache references=new(),regions=new();
    private readonly Dictionary<MatchKey,TemplateHit?> results=[];
    private RgbImage? frame;private bool disposed;private long queries,nativeMatches,resultHits;
    public RecognitionCacheStatistics CacheStatistics{get{lock(gate)return new(queries,nativeMatches,resultHits,references.Bytes,regions.Bytes,references.Count,regions.Count,results.Count);}}
    private readonly record struct TransformKey(string? Mode,int Light,bool Relative,int? Contrast);
    private readonly record struct ReferenceKey(string Sha,int Width,int Height,TransformKey Transform);
    private readonly record struct RegionKey(PixelBox Box,TransformKey Transform);
    private readonly record struct MatchKey(string Revision,TemplateDefinition Definition,PixelBox Box,double Minimum,bool Unique,double? Threshold);
    public TemplateHit? Match(RgbImage image,string name,double minimum=.97,bool unique=true,double? thresholdOverride=null,PixelBox? searchOverride=null)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);queries++;
            if(!ReferenceEquals(frame,image)){regions.Clear();results.Clear();frame=image;}
            if(!profile.Templates.TryGetValue(name,out var spec))return null;ReviewedLayout layout;try{layout=ReviewedLayout.For(image.Width,image.Height,profile.Dpi);}catch(ArgumentException){return null;}
            if(layout.ReferenceWidth!=profile.Width||layout.ReferenceHeight!=profile.Height)return null;var roi=searchOverride??layout.SearchBox(spec.Search);if(!roi.IsInside(image.Width,image.Height))return null;
            var key=new MatchKey(profile.Revision,spec,roi,minimum,unique,thresholdOverride);if(results.TryGetValue(key,out var cached)){resultHits++;return cached;}
            var result=MatchCore(image,spec,roi,layout,minimum,unique,thresholdOverride);if(results.Count<2048)results[key]=result;return result;
        }
    }
    private TemplateHit? MatchCore(RgbImage image,TemplateDefinition spec,PixelBox roi,ReviewedLayout layout,double minimum,bool unique,double? thresholdOverride)
    {
        var transform=new TransformKey(spec.Mode,spec.LightThreshold,spec.RelativeContrast,spec.RelativeContrastThreshold);var size=layout.TemplateSize(spec.Image.Width,spec.Image.Height);
        using var referenceLease=references.Get(new ReferenceKey(spec.Image.Sha256,size.Width,size.Height,transform),()=>
        {
            using var original=spec.Image.ToMat();using var resized=new Mat();Cv2.Resize(original,resized,size,0,0,size.Width<original.Width?InterpolationFlags.Area:InterpolationFlags.Cubic);return new Prepared(Transform(resized,spec));
        });
        var reference=referenceLease.Value.Image;if(roi.Width<reference.Width||roi.Height<reference.Height||referenceLease.Value.Std<2)return null;
        using var regionLease=regions.Get(new RegionKey(roi,transform),()=>{using var source=image.ToMat(roi);return new Prepared(Transform(source,spec));});var gray=regionLease.Value.Image;if(regionLease.Value.Std<2)return null;nativeMatches++;
        using var scores=new Mat();Cv2.MatchTemplate(gray,reference,scores,TemplateMatchModes.CCoeffNormed);Cv2.MinMaxLoc(scores,out _,out var score,out _,out var peak);using var selected=new Mat(gray,new Rect(peak.X,peak.Y,reference.Width,reference.Height));
        var ratio=Cv2.Mean(selected).Val0/Math.Max(1,referenceLease.Value.Mean);var threshold=thresholdOverride??Math.Max(minimum,spec.Threshold);if(spec.Mode is "light_text" or "bright_text" or "color_glyph" or "yellow_text" or "red_glyph")threshold=spec.GlyphThreshold??Math.Min(threshold,.93);
        if(!double.IsFinite(score)||score<threshold||ratio is <.65 or >1.5)return null;
        if(unique){int l=Math.Max(0,peak.X-reference.Width/2),t=Math.Max(0,peak.Y-reference.Height/2),r=Math.Min(scores.Width,peak.X+reference.Width/2+1),b=Math.Min(scores.Height,peak.Y+reference.Height/2+1);using(var exclusion=new Mat(scores,new Rect(l,t,r-l,b-t)))exclusion.SetTo(-1);Cv2.MinMaxLoc(scores,out double _,out double runnerUp);if(runnerUp>=threshold)return null;}
        var box=new PixelBox(roi.Left+peak.X,roi.Top+peak.Y,roi.Left+peak.X+reference.Width,roi.Top+peak.Y+reference.Height);return new(spec.Name,score,box,spec.Sha256);
    }
    public TemplateHit? Anchor(RgbImage image,string name,double minimum=.97,bool unique=true,double? thresholdOverride=null)
    {
        var names=new[]{name}.Concat(NavigationProfile.Names(profile.Definition["anchor_variants"]?[name]));
        foreach(var variant in names){var hit=Match(image,variant,minimum,unique,thresholdOverride);if(hit is null)continue;var spec=profile.Templates[variant];if(spec.ControlBox is {} control){var layout=ReviewedLayout.For(image.Width,image.Height,profile.Dpi);var expected=layout.SearchBox(spec.Box);var box=layout.SearchBox(control);var dx=hit.Box.Left-expected.Left;var dy=hit.Box.Top-expected.Top;var shifted=new PixelBox(box.Left+dx,box.Top+dy,box.Right+dx,box.Bottom+dy);if(!shifted.IsInside(image.Width,image.Height))continue;hit=hit with{MatchBox=hit.Box,Box=shifted};}return hit with{Name=name,Variant=variant==name?null:variant};}return null;
    }
    private static unsafe Mat Transform(Mat rgb,TemplateDefinition spec)
    {
        var gray=new Mat();if(spec.Mode is not ("light_text" or "bright_text" or "yellow_text" or "color_glyph" or "red_glyph"))
        {
            Cv2.CvtColor(rgb,gray,ColorConversionCodes.RGB2GRAY);
            // Map portraits retain a fixed screen size while panning changes subpixel rasterization.
            // Smooth only the calibrated inner face; keep the normal threshold and ambiguity veto.
            if(spec.Mode=="portrait_core")Cv2.GaussianBlur(gray,gray,new Size(5,5),1.2);
            return gray;
        }
        var height=rgb.Height;var width=rgb.Width;gray.Create(height,width,MatType.CV_8UC1);for(var y=0;y<height;y++){var row=(byte*)rgb.Ptr(y);var dest=(byte*)gray.Ptr(y);for(var x=0;x<width;x++){int r=row[x*3],g=row[x*3+1],b=row[x*3+2];dest[x]=spec.Mode switch{"red_glyph"=>(byte)(r>130&&r-g>50&&r-b>40?255:0),"yellow_text"=>(byte)(r>130&&g>130&&g-b>40&&r*5>=g*4?255:0),"color_glyph"=>(byte)(Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))>80?255:0),"bright_text"=>(byte)Math.Max(r,Math.Max(g,b)),_=>(byte)Math.Min(r,Math.Min(g,b))};}}
        if(spec.Mode is "light_text" or "bright_text"){if(spec.RelativeContrast){using var kernel=Cv2.GetStructuringElement(MorphShapes.Rect,new Size(15,15));Cv2.MorphologyEx(gray,gray,MorphTypes.TopHat,kernel);Cv2.MinMaxLoc(gray,out double _,out double max);if(max<16)gray.SetTo(0);else{Cv2.Normalize(gray,gray,0,255,NormTypes.MinMax);if(spec.RelativeContrastThreshold is {} t)Cv2.Threshold(gray,gray,t,255,ThresholdTypes.Binary);}}else Cv2.Threshold(gray,gray,spec.LightThreshold,255,ThresholdTypes.Binary);}
        Cv2.GaussianBlur(gray,gray,new Size(5,5),1.2);return gray;
    }
    private sealed class Prepared:IDisposable
    {
        public Mat Image{get;}public double Mean{get;}public double Std{get;}public long Bytes=>Image.Total()*Image.ElemSize();
        public Prepared(Mat image){Image=image;Cv2.MeanStdDev(image,out var mean,out var std);Mean=mean.Val0;Std=std.Val0;}
        public void Dispose()=>Image.Dispose();
    }
    private sealed class Lease(Prepared value,bool owned):IDisposable{public Prepared Value=>value;public void Dispose(){if(owned)value.Dispose();}}
    private sealed class PreparedCache
    {
        private const long Limit=8*1024*1024;private readonly Dictionary<object,Prepared> items=[];private readonly Queue<object> order=[];
        public long Bytes{get;private set;}public int Count=>items.Count;
        public Lease Get(object key,Func<Prepared> create)
        {
            if(items.TryGetValue(key,out var cached))return new(cached,false);var value=create();if(value.Bytes>Limit)return new(value,true);
            while(Bytes+value.Bytes>Limit||items.Count>=1024){var oldest=order.Dequeue();var removed=items[oldest];Bytes-=removed.Bytes;items.Remove(oldest);removed.Dispose();}
            items.Add(key,value);order.Enqueue(key);Bytes+=value.Bytes;return new(value,false);
        }
        public void Clear(){foreach(var value in items.Values)value.Dispose();items.Clear();order.Clear();Bytes=0;}
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;references.Clear();regions.Clear();results.Clear();frame=null;}}
}
public sealed record RecognitionCacheStatistics(long Queries,long NativeMatches,long ResultHits,long ReferenceBytes,long FrameBytes,int ReferenceEntries,int FrameEntries,int Results);
public sealed class ScreenPageRecognizer:IDisposable
{
    private readonly NavigationProfile profile;private readonly TemplateMatcher matcher;private readonly bool ownsMatcher;
    private readonly Dictionary<string,long> anchorCosts;
    public ScreenPageRecognizer(NavigationProfile profile,TemplateMatcher? matcher=null)
    {
        this.profile=profile;this.matcher=matcher??new(profile);ownsMatcher=matcher is null;
        anchorCosts=profile.Templates.Keys.ToDictionary(name=>name,name=>new[]{name}.Concat(NavigationProfile.Names(profile.Definition["anchor_variants"]?[name]))
            .Where(profile.Templates.ContainsKey).Sum(variant=>{var box=profile.Templates[variant].Search;return (long)box.Width*box.Height;}));
    }
    public PageView Recognize(ClientCapture capture)=>Recognize(capture.Image);
    public PageView Recognize(RgbImage image,IReadOnlySet<string>? declaredPages=null)
    {
        ReviewedLayout layout;try{layout=ReviewedLayout.For(image.Width,image.Height,profile.Dpi);}catch(ArgumentException){return PageView.Unknown();}
        var matches=new List<PageView>();var candidates=profile.Definition["pages"]!.AsObject().Select(p=>(p.Key,p.Value!)).Concat(profile.Definition["page_variants"]?.AsObject().Select(p=>(p.Value!["page"]!.GetValue<string>(),p.Value!))??[]).ToArray();
        if(declaredPages is not null)candidates=candidates.Where(c=>declaredPages.Contains(c.Item1)).ToArray();
        var cache=new Dictionary<string,TemplateHit?>();
        TemplateHit? Anchor(string name){if(!cache.TryGetValue(name,out var hit)){hit=matcher.Anchor(image,name);cache[name]=hit;}return hit;}
        TemplateHit[]? Required(string[] names,bool relaxed=false)
        {
            var hits=new TemplateHit[names.Length];
            // Fail a nonmatching page on its cheapest identity evidence before
            // searching large moving-label regions. Preserve configured hit order.
            foreach(var index in Enumerable.Range(0,names.Length).OrderBy(i=>anchorCosts.GetValueOrDefault(names[i],long.MaxValue)))
            {var name=names[index];var hit=relaxed?matcher.Anchor(image,name,.85,true,.85):Anchor(name);if(hit is null)return null;hits[index]=hit;}
            return hits;
        }
        foreach(var (page,spec) in candidates)
        {
            var names=NavigationProfile.Names(spec["anchors"]);if(names.Length==0)continue;var hits=Required(names);
            if(hits is null&&profile.Width==1280&&page=="scene")hits=Required(names,true);if(hits is null)continue;
            var targets=new Dictionary<string,PixelPoint>();var regions=new Dictionary<string,PixelBox>();var shapes=new Dictionary<string,ClickRegionShape>();var randomTargets=new HashSet<string>(StringComparer.Ordinal);var accepted=hits.Select(h=>h!).ToList();
            if(spec["targets"] is System.Text.Json.Nodes.JsonObject ts)foreach(var (target,anchor) in ts){var name=anchor!.GetValue<string>();var hit=accepted.FirstOrDefault(h=>h.Name==name)??Anchor(name);if(hit is not null){targets[target]=hit.Box.Center;regions[target]=hit.Box;if(!accepted.Contains(hit))accepted.Add(hit);}}
            if(spec["fixed_targets"] is System.Text.Json.Nodes.JsonObject fixedTargets)foreach(var (target,box) in fixedTargets)
                if(targets.ContainsKey(target)){var region=layout.SearchBox(NavigationProfile.ReadBox(box!));targets[target]=region.Center;regions[target]=region;}
            if(spec["coordinate_targets"] is System.Text.Json.Nodes.JsonObject coordinates)foreach(var (target,box) in coordinates)
            {
                var position=layout.SearchBox(NavigationProfile.ReadBox(box!));
                if(!position.IsInside(image.Width,image.Height))throw new InvalidDataException("固定点击区域越界："+target);
                targets[target]=position.Center;
                regions[target]=position;
                shapes[target]=NavigationProfile.ReadClickShape(box!);
                if(box is System.Text.Json.Nodes.JsonObject&&box["random"]?.GetValue<bool>()==true)randomTargets.Add(target);
            }
            if(spec["surfaces"] is System.Text.Json.Nodes.JsonObject surfaces)foreach(var (target,box) in surfaces)
                if(targets.TryAdd(target,layout.SearchBox(NavigationProfile.ReadBox(box!)).Center))regions[target]=layout.SearchBox(NavigationProfile.ReadBox(box!));
            if(spec["random_targets"] is System.Text.Json.Nodes.JsonObject randoms)foreach(var (target,configuration) in randoms)
            {
                if(configuration is null||!regions.TryGetValue(target,out var region))continue;
                shapes[target]=NavigationProfile.ReadClickShape(configuration);
                if(configuration["shape"]?.GetValue<string>()=="circle")
                {
                    var size=Math.Min(region.Width,region.Height);var left=region.Left+(region.Width-size)/2;var top=region.Top+(region.Height-size)/2;
                    regions[target]=new(left,top,left+size,top+size);
                }
                randomTargets.Add(target);
            }
            // Page stability belongs to its required identity anchors. Optional
            // action targets are checked independently by the input guard.
            matches.Add(new(page,false,accepted,targets,hits.Select(h=>h.Box).ToArray(),TargetRegions:regions,TargetShapes:shapes,RandomTargets:randomTargets));
        }
        var covered=candidates.Where(c=>matches.Any(m=>m.Page==c.Item1)).SelectMany(c=>NavigationProfile.Names(c.Item2["covers_pages"])).ToHashSet();
        matches.RemoveAll(m=>covered.Contains(m.Page));
        if(matches.Count==0)return NpcDialogueRecognition.Recognize(image)??PageView.Unknown();var first=matches[0];if(matches.Skip(1).Any(m=>m.Page!=first.Page))return PageView.Unknown();
        var mergedTargets=first.Targets.ToDictionary();foreach(var match in matches.Skip(1))foreach(var (name,point) in match.Targets)
        {if(mergedTargets.TryGetValue(name,out var old)&&old!=point)return PageView.Unknown();mergedTargets[name]=point;}
        var mergedRegions=new Dictionary<string,PixelBox>();foreach(var match in matches)foreach(var (name,box) in match.TargetRegions??new Dictionary<string,PixelBox>()){if(mergedRegions.TryGetValue(name,out var old)&&old!=box)return PageView.Unknown();mergedRegions[name]=box;}
        var mergedShapes=new Dictionary<string,ClickRegionShape>();foreach(var match in matches)foreach(var name in match.Targets.Keys){var shape=match.TargetShapes?.GetValueOrDefault(name)??ClickRegionShape.Rectangle;if(mergedShapes.TryGetValue(name,out var old)&&old!=shape)return PageView.Unknown();mergedShapes[name]=shape;}
        first=first with{Targets=mergedTargets,TargetRegions=mergedRegions,TargetShapes=mergedShapes,RandomTargets=matches.SelectMany(m=>m.RandomTargets??new HashSet<string>()).ToHashSet(StringComparer.Ordinal)};
        var allHits=matches.SelectMany(m=>m.Hits).Distinct().ToArray();var warning=NavigationProfile.Names(profile.Definition["remote_device_warnings"]).Any(n=>matcher.Anchor(image,n,unique:false) is not null);
        return first with{Hits=allHits,StableBoxes=matches.SelectMany(m=>m.StableBoxes).Distinct().ToArray(),NeedsDeviceArrival=warning};
    }
    public void Dispose(){if(ownsMatcher)matcher.Dispose();}
}
