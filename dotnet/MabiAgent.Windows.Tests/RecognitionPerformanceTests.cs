using System.Diagnostics;
using System.Text.Json.Nodes;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;
namespace MabiAgent.Windows.Tests;

public sealed class RecognitionPerformanceTests(ITestOutputHelper output)
{
    // Opt-in measurement: no game connection or input; reuses existing fixtures.
    [Fact] public void MeasureCurrentRecognitionAndGuardReviews()
    {
        var stage=Environment.GetEnvironmentVariable("MABI_RECOGNITION_BENCHMARK");if(stage is not ("baseline" or "optimized"))return;
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        var paths=Directory.GetFiles(Path.Combine(root,"tests/fixtures"),"*",SearchOption.AllDirectories).Where(p=>new[]{".png",".webp"}.Contains(Path.GetExtension(p))).Order().ToArray();
        var frames=new List<(string Path,RgbImage Image)>();
        foreach(var path in paths)
        {
            using var bgr=Cv2.ImRead(path);if(bgr.Empty())continue;
            try{if(ReviewedLayout.For(bgr.Width,bgr.Height,96).ReferenceWidth!=routes.Profile.Width)continue;}catch(ArgumentException){continue;}
            using var rgb=new Mat();Cv2.CvtColor(bgr,rgb,ColorConversionCodes.BGR2RGB);var bytes=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,bytes,0,bytes.Length);frames.Add((Path.GetRelativePath(root,path),new(rgb.Width,rgb.Height,bytes)));
        }
        Assert.NotEmpty(frames);var samples=new JsonArray();
        foreach(var (path,image) in frames)
        {
            using var recognizer=new ScreenPageRecognizer(routes.Profile);var ops=new ReplayOps(image);using var session=new ScreenWorkflowSession(routes,ops.Window,ops,new EmptyOcr(),new MemoryStore());session.Start();
            recognizer.Recognize(image);session.Observe();session.Observe();
            for(var repeat=0;repeat<4;repeat++)
            {
                var fresh=new RgbImage(image.Width,image.Height,image.CopyPixels());var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;var allocated=GC.GetTotalAllocatedBytes();var sw=Stopwatch.StartNew();var page=recognizer.Recognize(fresh);sw.Stop();
                var sample=new JsonObject{["path"]=path,["sha256"]=image.Sha256,["repeat"]=repeat,["page_ms"]=sw.Elapsed.TotalMilliseconds,["cpu_ms"]= (process.TotalProcessorTime-cpu).TotalMilliseconds,["allocated_bytes"]=GC.GetTotalAllocatedBytes()-allocated,["decision"]=Decision(page)};
                var captures=ops.Captures;cpu=process.TotalProcessorTime;sw.Restart();var view=session.Observe();
                var target=view.View.Targets.Keys.Order().FirstOrDefault();
                if(target is not null&&view.Stable){var proof=session.Guard.ObserveTarget(target);session.Guard.TypeText(proof,"",focus:false);}
                sw.Stop();sample["guard_ms"]=sw.Elapsed.TotalMilliseconds;sample["guard_cpu_ms"]=(process.TotalProcessorTime-cpu).TotalMilliseconds;sample["captures"]=ops.Captures-captures;sample["guard_decision"]=Decision(view.View);samples.Add(sample);
            }
        }
        var reportPath=Path.Combine(root,"build/runtime-recognition-performance.json");var report=File.Exists(reportPath)?JsonNode.Parse(File.ReadAllText(reportPath))!.AsObject():new JsonObject();
        report[stage]=new JsonObject{["recorded_utc"]=DateTimeOffset.UtcNow.ToString("O"),["frames"]=frames.Count,["processor_count"]=Environment.ProcessorCount,["opencv_threads"]=Cv2.GetNumThreads(),["samples"]=samples};
        if(stage=="optimized"&&report["baseline"]?["samples"] is JsonArray baseline)
        {
            Assert.Equal(baseline.Count,samples.Count);
            for(var i=0;i<samples.Count;i++)foreach(var key in new[]{"path","sha256","repeat","decision","guard_decision","captures"})Assert.True(JsonNode.DeepEquals(baseline[i]![key],samples[i]![key]),$"回放变化：{i} {key}");
        }
        File.WriteAllText(reportPath,report.ToJsonString(new(){WriteIndented=true}));output.WriteLine($"{stage}: {frames.Count} frames, {samples.Count} samples -> {reportPath}");
    }
    private static string Decision(PageView view)=>System.Text.Json.JsonSerializer.Serialize(new{view.Page,view.Modal,view.NeedsDeviceArrival,Targets=view.Targets.OrderBy(p=>p.Key).Select(p=>new{p.Key,p.Value}),Hits=view.Hits.OrderBy(h=>h.Name).Select(h=>new{h.Name,h.Box,h.MatchBox,h.Variant})});
    private sealed class EmptyOcr:IOcrEngine{public OcrLine? Recognize(int width,int height,byte[] grayPixels)=>null;}
    private sealed class MemoryStore:MabiAgent.Workflows.IWorkflowStore{public JsonObject? Read()=>null;public void Flush(JsonObject value)=>throw new InvalidOperationException("benchmark cannot persist consumption");}
    private sealed class ReplayOps(RgbImage image):INativeWindowsOps
    {
        public NativeWindowState Window=>new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,image.Width,image.Height,96),new(0,0,image.Width,image.Height),1,true,false,true,new(0,0));
        public int Captures;public NativeWindowState ReadWindow(nint hwnd)=>Window;
        public RgbImage CaptureClient(NativeWindowState state){Captures++;return new(image.Width,image.Height,image.CopyPixels());}
        public void MoveClient(nint hwnd,PixelPoint p)=>throw new InvalidOperationException("no input");public void MouseButton(MouseButton b,bool down)=>throw new InvalidOperationException("no input");public void Wheel(int delta)=>throw new InvalidOperationException("no input");public void Key(ushort key,bool down,bool unicode=false)=>throw new InvalidOperationException("no input");public bool AnyPhysicalKeyDown()=>false;
    }
}
