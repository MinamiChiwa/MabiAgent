using System.Diagnostics;
using System.Text.Json.Nodes;
using MabiAgent.Core;
using MabiAgent.Workflows;
using OpenCvSharp;
using Xunit;
namespace MabiAgent.Windows.Tests;

public sealed class FishingAcceptanceTests
{
    [Fact] public async Task ExplicitCurrentGameFishingAcceptance()
    {
        if(Environment.GetEnvironmentVariable("MABI_FISHING_ACCEPTANCE")!="1")return;
        var hwnd=(nint)long.Parse(Environment.GetEnvironmentVariable("MABI_BACKGROUND_HWND")??throw new InvalidOperationException("需要明确窗口"));
        var window=Assert.Single(GameWindowDiscovery.List(),w=>w.State.WindowId==hwnd).State;
        Assert.False(window.Minimized);Assert.NotEqual(hwnd,window.ForegroundId);
        var root=RecognitionTests.Root();var directory=Path.Combine(root,"build");Directory.CreateDirectory(directory);
        var routes=ScreenRoutes.Load(root);var config=routes.Profile.Definition["idle_fishing"]!;
        var calibrated=config["gauge"]?["calibrated"]?.GetValue<bool>()==true;
        var point=Environment.GetEnvironmentVariable("MABI_FISHING_POINT")??"point_fishing_00";
        var navigationOnly=Environment.GetEnvironmentVariable("MABI_FISHING_NAVIGATION_ONLY")=="1";
        var runKey=Environment.GetEnvironmentVariable("MABI_FISHING_ACCEPTANCE_ID")??"current";
        if(runKey.Any(c=>!char.IsLetterOrDigit(c)&&c!='-'&&c!='_'))throw new ArgumentException("验收运行标识无效");
        var prefix="fishing-acceptance-"+runKey;
        var store=new JsonWorkflowStore(Path.Combine(directory,prefix+"-runner.json"));
        var journal=new JsonWorkflowStore(Path.Combine(directory,prefix+"-intents.json"));
        using var lease=new FileStream(Path.Combine(directory,prefix+".lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        var diagnostics=new RuntimeDiagnostics(directory);
        using var session=new ScreenWorkflowSession(routes,window,InputTransportFactory.Create(window),ocr,journal,diagnostics:diagnostics);
        session.Start();var port=session.CreateFishingPort(point,prefix,store);
        var saved=store.Read();
        var runner=new IdleFishingRunner(new(point,600),port,store);
        if(saved is null&&Environment.GetEnvironmentVariable("MABI_FISHING_AT_POINT")=="1")
        {
            // Explicit operator-selected checkpoint after visually verified arrival.
            // A new isolated attempt only; never reset a saved or unresolved action.
            var checkpoint=runner.Snapshot();checkpoint["phase"]="refresh";store.Flush(checkpoint);
            runner=new IdleFishingRunner(new(point,600),port,store);runner.Resume(true);
        }
        // The same durable store survives repeated invocations. Resume observes
        // unresolved actions; it never replaces or clears their pending intent.
        if(saved is not null&&saved["status"]?.GetValue<string>()!="unknown")runner.Resume(true);
        var trace=new JsonArray();var samples=new JsonArray();var clock=Stopwatch.StartNew();
        var reportPath=Path.Combine(directory,prefix+"-current.json");
        var sampling=false;var lastSample=double.NegativeInfinity;var outcome="bounded_timeout";var reelSeen=false;
        try
        {
            while(clock.Elapsed<TimeSpan.FromSeconds(180))
            {
                if(navigationOnly&&runner.Snapshot()["phase"]?.GetValue<string>()!="navigate"){outcome="navigation_only_finished";break;}
                if(sampling&&!calibrated)runner.ObserveOnly();else runner.Tick();
                var frame=port.LastFrame??port.ObserveOnly();var state=runner.Snapshot();
                reelSeen|=state["pending"]?["kind"]?.GetValue<string>()=="reel";
                if((state["submitted"]?.GetValue<int>()??0)>0)runner.StopSubmitting();
                if(trace.Count<180)trace.Add(new JsonObject{["at"]=frame.At,["page"]=frame.Page,["stable"]=frame.Stable,["phase"]=state["phase"]?.DeepClone(),["status"]=state["status"]?.DeepClone(),["pending"]=state["pending"]?["kind"]?.DeepClone(),["hook"]=frame.HookVisible,["gauge"]=frame.Gauge,["mounted"]=frame.Mounted,["compass"]=frame.Compass,["round_active"]=frame.RoundActive,["fishing_button"]=frame.FishingButton,["navigation_active"]=frame.NavigationActive,["leisure_mode"]=frame.LeisureMode,["current_page"]=session.CurrentPage,["stability_reason"]=session.StabilityReason});
                sampling|=frame.HookVisible;
                if(navigationOnly&&frame.NavigationActive==false){outcome="navigation_arrival_observed";break;}
                if(frame.HookVisible&&samples.Count<6&&clock.Elapsed.TotalSeconds-lastSample>=.25&&session.LastCapture is {} capture)
                {
                    var region=config["gauge"]?["region"]??throw new InvalidDataException("缺少候选Gauge区域");
                    var box=session.Box(region);
                    box=session.Box(new PixelBox(500,250,800,550));
                    var path=Path.Combine(directory,$"{prefix}-gauge-{samples.Count}.png");
                    using var rgb=capture.Image.Crop(box).ToMat();using var bgr=new Mat();Cv2.CvtColor(rgb,bgr,ColorConversionCodes.RGB2BGR);Cv2.ImWrite(path,bgr);
                    samples.Add(new JsonObject{["path"]=path,["at"]=capture.CapturedAt,["gauge"]=frame.Gauge,["native_box"]=new JsonArray(box.Left,box.Top,box.Right,box.Bottom),["client_width"]=capture.Image.Width,["client_height"]=capture.Image.Height});lastSample=clock.Elapsed.TotalSeconds;
                }
                if(!calibrated&&samples.Count==6){outcome="calibration_samples_only_native_round_preserved";break;}
                if((state["completed"]?.GetValue<int>()??0)>0){outcome=reelSeen?"native_round_completed":"native_round_ended_without_reel";break;}
                if(state["status"]?.GetValue<string>() is "unknown" or "complete"){outcome=state["status"]!.GetValue<string>();break;}
                await Task.Delay(sampling?100:150);
            }
        }
        finally
        {
            // Pausing persists the checkpoint and releases input; it sends no leave/reel.
            runner.Pause("acceptance_end");
            if(session.LastCapture is {} finalCapture)
            {
                using var rgb=finalCapture.Image.ToMat();using var bgr=new Mat();Cv2.CvtColor(rgb,bgr,ColorConversionCodes.RGB2BGR);
                byte[] encoded=[];
                for(var quality=85;quality>=15;quality-=10)
                {Cv2.ImEncode(".jpg",bgr,out encoded,new ImageEncodingParam(ImwriteFlags.JpegQuality,quality));if(encoded.Length<=1024*1024)break;}
                if(encoded.Length>1024*1024)
                {using var reduced=new Mat();Cv2.Resize(bgr,reduced,new Size(1280,Math.Max(1,bgr.Height*1280/bgr.Width)));Cv2.ImEncode(".jpg",reduced,out encoded,new ImageEncodingParam(ImwriteFlags.JpegQuality,60));}
                if(encoded.Length<=1024*1024)File.WriteAllBytes(Path.Combine(directory,prefix+"-screen.jpg"),encoded);
            }
            File.WriteAllText(reportPath,new JsonObject{["at"]=DateTimeOffset.UtcNow.ToString("O"),["outcome"]=outcome,["gauge_calibrated"]=calibrated,["samples"]=samples,["trace"]=trace,["runner"]=runner.Snapshot(),["diagnostics_log"]=Path.Combine(directory,"runtime-events.jsonl"),["diagnostics_write_error"]=diagnostics.WriteError}.ToJsonString());
        }
    }
}
