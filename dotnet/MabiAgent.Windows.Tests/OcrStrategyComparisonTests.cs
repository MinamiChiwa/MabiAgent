using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using MabiAgent.Windows;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;
namespace MabiAgent.Windows.Tests;

public sealed class OcrStrategyComparisonTests(ITestOutputHelper output)
{
    private sealed record Sample(string Name,RgbImage Image,string Expected,int[] Thresholds,bool Negative=false);
    [Fact] public void CompareExistingNativeCropsWithoutChangingRuntimeDefaults()
    {
        if(Environment.GetEnvironmentVariable("MABI_OCR_STRATEGY_COMPARE")!="1")return;
        var root=RecognitionTests.Root();using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        var samples=new List<Sample>();var labels=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"config/bag_material_labels.1280x960.tw.json")))!.AsObject();
        foreach(var (_,label) in labels)samples.Add(new(Path.GetFileName(label!["label_template"]!.GetValue<string>()),Load(Path.Combine(root,label["label_template"]!.GetValue<string>())),label["name"]!.GetValue<string>(),[90,140]));
        var iron=Load(Path.Combine(root,"assets/templates/gathering_ui/bag_search_iron_full_name.png"));samples.Add(new("iron-search",iron,"鐵礦石",[90,140]));
        samples.Add(new("bag-quantity",Load(Path.Combine(root,"tests/fixtures/screen_ui/ocr/bag-iron-quantity.png")),"38",[165,210]));
        var config=ScreenRoutes.Load(root).Configuration["purchase"]!;
        foreach(var (file,quantity,price) in new[]{("shop-bottle.jpg","1","50"),("shop-bottle-two.jpg","2","100")})
        {
            var image=Load(Path.Combine(root,"assets/templates/purchase",file));samples.Add(new(file+":name",image.Crop(NavigationProfile.ReadBox(config["item_name_region"]!)),"空瓶",[90,140]));
            foreach(var (key,expected) in new[]{("quantity",quantity),("total_price",price),("bottles","140"),("gold","1,410,756")})
            {var spec=config[key]!;samples.Add(new(file+":"+key,image.Crop(NavigationProfile.ReadBox(spec["region"]!)),expected,spec["thresholds"]!.AsArray().Select(n=>n!.GetValue<int>()).ToArray()));}
        }
        for(var kind=1;kind<=4;kind++)
        {
            var pixels=iron.CopyPixels();var source=iron.CopyPixels();for(var y=0;y<iron.Height;y++)for(var x=0;x<iron.Width;x++)
            {var i=(y*iron.Width+x)*3;if(kind==1&&x<27||kind==2&&x>=47||kind==4)pixels.AsSpan(i,3).Clear();if(kind==3&&x>=80&&x<102)Array.Copy(source,(y*iron.Width+x-76)*3,pixels,i,3);}
            samples.Add(new("negative-"+kind,new(iron.Width,iron.Height,pixels),"鐵礦石",[90,140],true));
        }
        var report=new JsonObject{["recorded_utc"]=DateTimeOffset.UtcNow.ToString("O"),["model_sha256"]=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(engine.ModelPath))),["scope"]="offline existing crops; one warmup + three timed pairs; not game validation",["samples"]=new JsonArray(),["strategies"]=new JsonArray()};
        foreach(var s in samples)((JsonArray)report["samples"]!).Add(new JsonObject{["name"]=s.Name,["sha256"]=s.Image.Sha256,["width"]=s.Image.Width,["height"]=s.Image.Height,["expected"]=s.Expected,["negative"]=s.Negative});
        foreach(var mode in new[]{"current_binary","binary_crop_pad","gray_invert_crop_pad","rgb_crop_pad"})
        {
            var rows=new JsonArray();var times=new List<double>();var correct=0;var falseAccept=0;
            foreach(var s in samples)
            {
                OcrLine?[] Run()=>s.Thresholds.Select(t=>Recognize(engine,s.Image,t,mode)).ToArray();
                Run();OcrLine?[] readings=[];
                for(var repeat=0;repeat<3;repeat++){var timer=Stopwatch.StartNew();readings=Run();timer.Stop();times.Add(timer.Elapsed.TotalMilliseconds);}
                var accepted=readings.All(r=>r is {Confidence:>=.75 and <=1}&&double.IsFinite(r.Confidence))&&readings.Select(r=>r?.Text.Trim()).Distinct().Count()==1;
                var full=accepted&&readings[0]!.Text.Trim()==s.Expected;if(s.Negative){if(full)falseAccept++;}else if(full)correct++;
                if(mode=="current_binary")Assert.Equal(full,new ScreenQuantityReader(engine).Exact(s.Image,new(0,0,s.Image.Width,s.Image.Height),s.Expected,s.Thresholds));
                rows.Add(new JsonObject{["name"]=s.Name,["texts"]=new JsonArray(readings.Select(r=>(JsonNode?)JsonValue.Create(r?.Text)).ToArray()),["confidence"]=new JsonArray(readings.Select(r=>(JsonNode?)JsonValue.Create(r?.Confidence)).ToArray()),["consensus"]=accepted,["strict_expected"]=full});
            }
            times.Sort();var result=new JsonObject{["mode"]=mode,["positive_correct"]=correct,["positive_total"]=samples.Count(s=>!s.Negative),["negative_false_accepts"]=falseAccept,["negative_total"]=samples.Count(s=>s.Negative),["mean_pair_ms"]=times.Average(),["p95_pair_ms"]=times[(int)Math.Ceiling(times.Count*.95)-1],["rows"]=rows};
            ((JsonArray)report["strategies"]!).Add(result);output.WriteLine($"{mode}: {correct}/{samples.Count(s=>!s.Negative)}, false accepts {falseAccept}, mean {times.Average():F2} ms, P95 {result["p95_pair_ms"]}");
        }
        var path=Path.Combine(root,"build/ocr-strategy-comparison.json");File.WriteAllText(path,report.ToJsonString(new(){WriteIndented=true}));
        Assert.Equal(0,report["strategies"]![0]!["negative_false_accepts"]!.GetValue<int>());
    }
    private static OcrLine? Recognize(OnnxTextRecognizer engine,RgbImage image,int threshold,string mode)
    {
        var pixels=image.CopyPixels();var gray=new byte[image.Width*image.Height];int left=image.Width,right=-1,top=image.Height,bottom=-1;
        for(var i=0;i<gray.Length;i++){var value=pixels[i*3]*.299+pixels[i*3+1]*.587+pixels[i*3+2]*.114;gray[i]=(byte)(255-Math.Clamp((int)Math.Round(value),0,255));if(value>threshold){left=Math.Min(left,i%image.Width);right=Math.Max(right,i%image.Width);top=Math.Min(top,i/image.Width);bottom=Math.Max(bottom,i/image.Width);}}
        if(mode=="current_binary"){for(var i=0;i<gray.Length;i++)gray[i]=pixels[i*3]*.299+pixels[i*3+1]*.587+pixels[i*3+2]*.114>threshold?(byte)0:(byte)255;return engine.Recognize(image.Width,image.Height,gray);}
        if(bottom<0)return null;left=Math.Max(0,left-2);top=Math.Max(0,top-2);right=Math.Min(image.Width,right+3);bottom=Math.Min(image.Height,bottom+3);
        var width=right-left+8;var height=bottom-top+8;
        if(mode=="rgb_crop_pad")
        {
            var rgb=new byte[width*height*3];for(var y=top;y<bottom;y++)Array.Copy(pixels,(y*image.Width+left)*3,rgb,((y-top+4)*width+4)*3,(right-left)*3);return engine.RecognizeRgb(new(width,height,rgb));
        }
        var result=Enumerable.Repeat((byte)255,width*height).ToArray();for(var y=top;y<bottom;y++)for(var x=left;x<right;x++){var i=y*image.Width+x;result[(y-top+4)*width+x-left+4]=mode=="binary_crop_pad"?(pixels[i*3]*.299+pixels[i*3+1]*.587+pixels[i*3+2]*.114>threshold?(byte)0:(byte)255):gray[i];}
        return engine.Recognize(width,height,result);
    }
    private static RgbImage Load(string path)
    {using var bgr=Cv2.ImRead(path);Assert.False(bgr.Empty());using var rgb=new Mat();Cv2.CvtColor(bgr,rgb,ColorConversionCodes.BGR2RGB);var pixels=new byte[rgb.Width*rgb.Height*3];Marshal.Copy(rgb.Data,pixels,0,pixels.Length);return new(rgb.Width,rgb.Height,pixels);}
}
