using System.Diagnostics;
using System.Text.Json.Nodes;
using MabiAgent.Windows;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;
namespace MabiAgent.Windows.Tests;

public sealed class MapTouchGestureProbeTests(ITestOutputHelper output)
{
    [Fact] public void ExplicitGameMapCompetitionSequence()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_MAP_SEQUENCE")!="1")return;
        var kind=Environment.GetEnvironmentVariable("MABI_TOUCH_MAP_GAME");var mouse=Environment.GetEnvironmentVariable("MABI_TOUCH_MAP_MOUSE");var direction=Environment.GetEnvironmentVariable("MABI_TOUCH_MAP_DIRECTION");
        try
        {
            Environment.SetEnvironmentVariable("MABI_TOUCH_MAP_MOUSE","1");
            foreach(var step in new[]{("drag",1),("pinch",-1),("pinch",1)})
            {Environment.SetEnvironmentVariable("MABI_TOUCH_MAP_GAME",step.Item1);Environment.SetEnvironmentVariable("MABI_TOUCH_MAP_DIRECTION",step.Item2.ToString());ExplicitGameMapGestureProbe();}
        }
        finally{Environment.SetEnvironmentVariable("MABI_TOUCH_MAP_GAME",kind);Environment.SetEnvironmentVariable("MABI_TOUCH_MAP_MOUSE",mouse);Environment.SetEnvironmentVariable("MABI_TOUCH_MAP_DIRECTION",direction);}
    }
    [Fact] public void ExplicitGameMapGestureProbe()
    {
        var kind=Environment.GetEnvironmentVariable("MABI_TOUCH_MAP_GAME");if(kind is not ("observe" or "drag" or "pinch"))return;
        var hwnd=(nint)long.Parse(Environment.GetEnvironmentVariable("MABI_BACKGROUND_HWND")??throw new InvalidOperationException("需要指定当前游戏窗口"));
        var state=Assert.Single(GameWindowDiscovery.List(),w=>w.State.WindowId==hwnd).State;Assert.False(state.Minimized);Assert.NotEqual(hwnd,state.ForegroundId);
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var observation=new ScreenWorkflowSession(routes,state,new WindowMessageInputOps(state),ocr,new NoWrites());observation.Start();var seen=observation.Observe();if(kind!="observe")seen=Stable(observation);
        Save(observation.LastCapture!.Image,Path.Combine(root,"build/background-input-current.jpg"));
        var result=new JsonObject{["at"]=DateTimeOffset.Now.ToString("O"),["kind"]=kind,["before_page"]=seen.View.Page,["hwnd"]=(long)hwnd,["geometry"]=new JsonArray(state.Geometry.X,state.Geometry.Y,state.Geometry.Width,state.Geometry.Height,state.Geometry.Dpi),["targets"]=new JsonArray(seen.View.Targets.Keys.Select(k=>(JsonNode)JsonValue.Create(k)!).ToArray())};
        if(kind=="observe"){result["stable"]=seen.Stable;Write("touch_map_observation",result);return;}
        Assert.Equal("region_map",seen.View.Page);var proof=observation.Guard.ObserveTarget("map.canvas");
        var direction=Environment.GetEnvironmentVariable("MABI_TOUCH_MAP_DIRECTION")=="-1"?-1:1;
        var plan=TouchGesturePlan.Create(kind,proof.Point,direction);Assert.True(plan.Bounds.IsInside(state.Geometry.Width,state.Geometry.Height));
        Assert.True(seen.View.TargetRegions!.TryGetValue("map.canvas",out var canvas));Assert.True(canvas.Contains(new(plan.Bounds.Left,plan.Bounds.Top))&&canvas.Contains(new(plan.Bounds.Right-1,plan.Bounds.Bottom-1)));
        var competition=Environment.GetEnvironmentVariable("MABI_TOUCH_MAP_MOUSE")=="1";
        using var monitor=competition?new TouchMouseMonitor():null;using var pad=competition?new TouchInputWindow(new(100,100,440,340),label:"地图手势并发测试区\n持续移动，点击并按住左键拖动\n请保持在白色区域内"):null;
        if(pad is not null)
        {
            Assert.False(new PixelBox(state.Geometry.X,state.Geometry.Y,state.Geometry.X+state.Geometry.Width,state.Geometry.Y+state.Geometry.Height).Contains(pad.Point));
            var wait=Stopwatch.StartNew();while(TouchNative.At(TouchCursorPresentation.Read().Position)!=pad.Hwnd&&wait.ElapsedMilliseconds<8000)Thread.Sleep(20);
            Assert.Equal(pad.Hwnd,TouchNative.At(TouchCursorPresentation.Read().Position));Thread.Sleep(1500);
        }
        var screen=plan.Screen(state.Geometry);using var anchor=new TouchInputWindow(new(20,20,44,44));using var cover=new TouchInputWindow(screen.Bounds,legacyTouch:true,retainTouchHandles:true);
        using var ops=new GuardedTouchProbeOps(state,anchor,cover,true,pad,plan){MouseMonitor=monitor};using var session=new ScreenWorkflowSession(routes,state,ops,ocr,new NoWrites());session.Start();
        var before=TouchCursorPresentation.Read();using var beforeImage=observation.LastCapture.Image.ToMat();
        try
        {
            Assert.Equal("region_map",Stable(session).View.Page);var current=session.Guard.ObserveTarget("map.canvas");Assert.Equal(proof.Point,current.Point);
            result["direction"]=direction;result["before"]=before.ToJson();result["input_requested"]=true;
            var physicalMoves=monitor?.PhysicalMoves??0;var physicalDowns=monitor?.PhysicalDowns??0;var physicalUps=monitor?.PhysicalUps??0;
            session.Guard.Drag(current,kind=="drag"?plan.Points[^1][0]:current.Point,plan.Steps,o=>o.View.Page=="region_map");
            result["physical_during_guarded_action"]=new JsonObject{["moves"]=(monitor?.PhysicalMoves??0)-physicalMoves,["downs"]=(monitor?.PhysicalDowns??0)-physicalDowns,["ups"]=(monitor?.PhysicalUps??0)-physicalUps};
            Thread.Sleep(700);var after=Stable(session);result["after_page"]=after.View.Page;Assert.Equal("region_map",after.View.Page);
            Save(session.LastCapture!.Image,Path.Combine(root,"build/background-input-current.jpg"));
            using var afterImage=session.LastCapture.Image.ToMat();using var left=new Mat(beforeImage,new Rect(200,150,850,630));using var right=new Mat(afterImage,new Rect(200,150,850,630));using var pair=new Mat();using var bgr=new Mat();
            var transform=Measure(left,right);result["map_transform"]=transform;var scale=transform["scale"]?.GetValue<double>()??0;
            var verified=transform["verified"]?.GetValue<bool>()==true&&(kind=="drag"?Math.Abs(scale-1)<.02&&Math.Abs(transform["dx"]!.GetValue<double>()-direction*120)<10&&Math.Abs(transform["dy"]!.GetValue<double>()-direction*40)<10:direction==1?scale>1.05:scale is >0 and <.95);
            result["game_response_verified"]=verified;
            Cv2.HConcat([left,right],pair);Cv2.CvtColor(pair,bgr,ColorConversionCodes.RGB2BGR);Cv2.ImWrite(Path.Combine(root,"build/map-touch-current.jpg"),bgr,new ImageEncodingParam(ImwriteFlags.JpegQuality,75));
            result["image_evidence"]=new JsonObject{["path"]="build/map-touch-current.jpg",["left"]="before",["right"]="after",["client_roi"]=new JsonArray(200,150,850,630),["scale"]=1};
            Assert.True(verified,"地图地标未确认预期的平移/缩放，结果不重发");
            var final=TouchCursorPresentation.Read();result["after"]=final.ToJson();Assert.Equal(before.Foreground,final.Foreground);if(!competition)Assert.Equal(before.Position,final.Position);
            Assert.True(final.Showing);result["diagnostic_completed"]=true;
            if(pad is not null){Thread.Sleep(1800);var wait=Stopwatch.StartNew();while(new PhysicalInputReader().Read().ButtonsDown&&wait.ElapsedMilliseconds<2000)Thread.Sleep(10);}
        }
        catch(Exception e){result["error"]=e.GetType().Name+": "+e.Message;throw;}
        finally
        {
            ops.Dispose();result["forwarded"]=ops.Forwarded.DeepClone();result["frames"]=ops.Probe?.Frames.DeepClone();result["local_physical_intervals"]=ops.PhysicalIntervals.DeepClone();result["cursor_trace"]=ops.CursorTrace.DeepClone();
            if(pad is not null&&monitor is not null)
            {
                result["physical_moves"]=monitor.PhysicalMoves;result["physical_downs"]=monitor.PhysicalDowns;result["physical_ups"]=monitor.PhysicalUps;result["physical_drag_moves"]=monitor.PhysicalDragMoves;result["pad_moves"]=pad.MouseMoves;result["pad_downs"]=pad.MouseDowns;result["pad_ups"]=pad.MouseUps;result["pad_drag_moves"]=pad.MouseDragMoves;result["absolute_mouse_position_written"]=false;
                result["mouse_tail"]=new JsonArray(monitor.Events.ToArray().TakeLast(20).Select(e=>(JsonNode)e.ToJson()).ToArray());
            }
            cover.Dispose();anchor.Dispose();pad?.Dispose();monitor?.Dispose();result["device_destroyed"]=ops.Probe?.Released??true;result["auxiliary_windows_closed"]=cover.Closed&&anchor.Closed&&(pad?.Closed??true);Write("touch_map_"+kind+(direction<0?"_out":"")+(competition?"_mouse":""),result);
        }
    }
    private static UiObservation Stable(ScreenWorkflowSession session)
    {var timer=Stopwatch.StartNew();do{var o=session.Observe();if(o.Stable)return o;Thread.Sleep(100);}while(timer.ElapsedMilliseconds<10000);throw new InputBlockedException("页面无法稳定识别");}
    private static void Save(RgbImage image,string path)
    {using var rgb=image.ToMat();using var bgr=new Mat();Cv2.CvtColor(rgb,bgr,ColorConversionCodes.RGB2BGR);Cv2.ImWrite(path,bgr,new ImageEncodingParam(ImwriteFlags.JpegQuality,75));}
    internal static JsonObject Measure(Mat before,Mat after)=>MapMotionEvidence.Measure(before,after).ToJson();
    private sealed class NoWrites:MabiAgent.Workflows.IWorkflowStore
    {public JsonObject? Read()=>null;public void Flush(JsonObject state)=>throw new InvalidOperationException("手势诊断禁止写入业务意图");}
    [Fact] public void ExplicitNativeMapGestureProbe()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_MAP_NATIVE")!="1")return;
        var original=TouchCursorPresentation.Read();var result=new JsonObject{["at"]=DateTimeOffset.Now.ToString("O")};var samples=new JsonArray();
        try
        {
            foreach(var kind in new[]{"drag","pinch"})
            {
                var plan=TouchGesturePlan.Create(kind,new(650,450));using var anchor=new TouchInputWindow(new(20,20,44,44));using var target=new TouchInputWindow(plan.Bounds,legacyTouch:true);using var cover=new TouchInputWindow(plan.Bounds,legacyTouch:true,retainTouchHandles:true);
                using var probe=new SyntheticTouchDevice(anchor.Point,anchor.Hwnd);plan.Generate(probe,target.Hwnd,()=>{});var envelopes=plan.Collect(cover);var before=target.Received.Count;
                try{foreach(var envelope in envelopes){envelope.Forward(target.Hwnd);Thread.Sleep(20);}}finally{foreach(var envelope in envelopes)envelope.Dispose();}
                Thread.Sleep(80);var received=target.Received.Skip(before).Where(t=>t["message"]?.GetValue<uint>()==0x240).ToArray();
                Assert.Equal(plan.MessageCount,received.Length);Assert.All(received,t=>Assert.True(t["touch_info_available"]!.GetValue<bool>()));
                samples.Add(new JsonObject{["kind"]=kind,["frames"]=received.Length,["contacts_per_frame"]=plan.Points[0].Length,["received"]=new JsonArray(received.Select(t=>(JsonNode)t.DeepClone()).ToArray()),["device_destroyed"]=probe.Released});
            }
            result["diagnostic_completed"]=true;
        }
        catch(Exception e){result["error"]=e.GetType().Name+": "+e.Message;throw;}
        finally
        {
            result["samples"]=samples;result["restore"]=TouchCursorPresentation.Restore(original.Showing||original.Suppressed,()=>{var current=TouchCursorPresentation.Read();if(current.Position!=original.Position||current.Foreground!=original.Foreground)throw new InputBlockedException("用户已活动，不恢复旧位置");});
            result["after"]=TouchCursorPresentation.Read().ToJson();Write("touch_map_native",result);
        }
    }
    private void Write(string key,JsonObject result)
    {var path=Path.Combine(RecognitionTests.Root(),"build/reference-optimization-validation.json");var report=JsonNode.Parse(File.ReadAllText(path))!.AsObject();report[key]=result;File.WriteAllText(path,report.ToJsonString(new(){WriteIndented=true}));output.WriteLine(result.ToJsonString());}
}
