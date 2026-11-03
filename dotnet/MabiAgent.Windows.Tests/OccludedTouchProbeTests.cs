using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using MabiAgent.Windows;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;
namespace MabiAgent.Windows.Tests;

public sealed class OccludedTouchProbeTests(ITestOutputHelper output)
{
    [Fact] public void ExplicitNativeTouchMouseCompetitionProbe()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_MOUSE_COMPETITION")!="1")return;
        var original=TouchCursorPresentation.Read();var result=new JsonObject{["at"]=DateTimeOffset.UtcNow.ToString("O"),["scope"]="real mouse on self-owned pad; no game input",["before"]=original.ToJson()};var phases=new JsonArray();var overlap=0;
        using var monitor=new TouchMouseMonitor();using var pad=new TouchInputWindow(new(100,100,440,340),label:"鼠标竞争测试区\n请在此区域持续移动鼠标，并点击数次\n约12秒后自动关闭");
        using var anchor=new TouchInputWindow(new(20,20,44,44));using var target=new TouchInputWindow(new(500,100,660,220),legacyTouch:true);using var cover=new TouchInputWindow(new(500,100,660,220),legacyTouch:true,retainTouchHandles:true);
        try
        {
            Thread.Sleep(1000);
            for(var i=0;i<12;i++)
            {
                var began=Stopwatch.GetTimestamp();using var probe=new SyntheticTouchDevice(anchor.Point,anchor.Hwnd);probe.Begin();probe.Down(cover.Point,cover.Hwnd);Thread.Sleep(70);probe.Up();probe.Dispose();var ended=Stopwatch.GetTimestamp();
                Thread.Sleep(20);var forwarded=0;while(cover.Touches.TryDequeue(out var envelope))using(envelope){envelope.Forward(target.Hwnd);forwarded++;}
                var events=monitor.Events.ToArray();var during=events.Count(e=>e.Physical&&e.Tick>=began&&e.Tick<=ended);overlap+=during;
                phases.Add(new JsonObject{["index"]=i+1,["physical_events_during_contacts"]=during,["forwarded"]=forwarded,["cursor"]=TouchCursorPresentation.Read().ToJson(),["device_destroyed"]=probe.Released});
                Thread.Sleep(750);
            }
            result["foreground_unchanged"]=TouchNative.Foreground==original.Foreground;
            result["physical_moves"]=monitor.PhysicalMoves;result["physical_downs"]=monitor.PhysicalDowns;result["physical_ups"]=monitor.PhysicalUps;result["physical_events_during_contacts"]=overlap;
            result["pad_moves"]=pad.MouseMoves;result["pad_downs"]=pad.MouseDowns;result["pad_ups"]=pad.MouseUps;
            result["underlying_target_mouse_downs"]=target.MouseDowns;result["underlying_target_mouse_ups"]=target.MouseUps;
            result["decoded_touch_downs"]=target.Received.Count(t=>t["message"]?.GetValue<uint>()==0x240&&t["touch_info_available"]!.GetValue<bool>()&&t["contacts"]!.AsArray().Any(c=>(c!["flags"]!.GetValue<uint>()&2)!=0));
            result["decoded_touch_ups"]=target.Received.Count(t=>t["message"]?.GetValue<uint>()==0x240&&t["touch_info_available"]!.GetValue<bool>()&&t["contacts"]!.AsArray().Any(c=>(c!["flags"]!.GetValue<uint>()&4)!=0));
            result["physical_coverage_sufficient"]=overlap>0&&monitor.PhysicalMoves>10&&pad.MouseDowns>=2&&pad.MouseUps>=2;
            result["diagnostic_completed"]=true;
        }
        catch(Exception e){result["error"]=e.GetType().Name+": "+e.Message;throw;}
        finally
        {
            result["phases"]=phases;result["mouse_tail"]=new JsonArray(monitor.Events.ToArray().TakeLast(24).Select(e=>(JsonNode)e.ToJson()).ToArray());
            cover.Dispose();target.Dispose();anchor.Dispose();pad.Dispose();monitor.Dispose();
            result["auxiliary_windows_closed"]=cover.Closed&&target.Closed&&anchor.Closed&&pad.Closed;
            var beforeRestore=TouchCursorPresentation.Read();Thread.Sleep(100);
            try{result["final_restore"]=TouchCursorPresentation.Restore(original.Showing||original.Suppressed,()=>{var now=TouchCursorPresentation.Read();if(now.Position!=beforeRestore.Position||now.Foreground!=beforeRestore.Foreground)throw new InputBlockedException("用户仍在移动，跳过光标呈现通知");});}
            catch(InputBlockedException e){result["restore_skipped"]=e.Message;}
            result["after"]=TouchCursorPresentation.Read().ToJson();Write("touch_mouse_competition",result);
        }
    }

    [Fact] public void ExplicitGameTouchStabilityProbe()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_STABILITY_PROBE")!="1")return;
        Assert.Equal("1",Environment.GetEnvironmentVariable("MABI_TOUCH_GAME_PROBE"));Assert.Equal("1",Environment.GetEnvironmentVariable("MABI_TOUCH_FORWARD_PROBE"));Assert.Equal("absolute_same_pixel",Environment.GetEnvironmentVariable("MABI_TOUCH_CURSOR_RESTORE"));
        var result=new JsonObject{["at"]=DateTimeOffset.UtcNow.ToString("O"),["requested_actions"]=8};var samples=new JsonArray();var timer=Stopwatch.StartNew();
        try
        {
            for(var i=0;i<8;i++)
            {
                var action=Stopwatch.StartNew();ExplicitOccludedGameMenuProbe();var latest=JsonNode.Parse(File.ReadAllText(Path.Combine(RecognitionTests.Root(),"build/reference-optimization-validation.json")))!["touch_forward_game"]!;
                var sample=new JsonObject{["index"]=i+1,["elapsed_ms"]=action.Elapsed.TotalMilliseconds};
                foreach(var field in new[]{"target","before_page","after_page","game_response_verified","foreground_unchanged","cursor_unchanged","cursor_visible_after","cursor_suppressed_after","device_destroyed","auxiliary_windows_closed"})sample[field]=latest[field]?.DeepClone();
                samples.Add(sample);Assert.True(latest["game_response_verified"]!.GetValue<bool>());Assert.False(latest["cursor_suppressed_after"]!.GetValue<bool>());Assert.True(latest["cursor_visible_after"]!.GetValue<bool>());
            }
            result["diagnostic_completed"]=true;
        }
        catch(Exception e){result["error"]=e.GetType().Name+": "+e.Message;throw;}
        finally{result["samples"]=samples;result["completed_actions"]=samples.Count;result["elapsed_ms"]=timer.Elapsed.TotalMilliseconds;Write("touch_stability",result);}
    }

    [Fact] public void ExplicitTouchCursorVisibilityProbe()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_CURSOR_PROBE")!="1")return;
        var original=TouchCursorPresentation.Read();var result=new JsonObject{["at"]=DateTimeOffset.UtcNow.ToString("O"),["before"]=original.ToJson(),["scope"]="self-owned windows; no game input"};
        using var anchor=new TouchInputWindow(AnchorAwayFrom(original.Position));using var cover=new TouchInputWindow(new(100,100,260,220),legacyTouch:true,retainTouchHandles:true);
        using var probe=new SyntheticTouchDevice(anchor.Point,anchor.Hwnd);var phases=new JsonArray();
        void Sample(string phase)=>phases.Add(new JsonObject{["phase"]=phase,["cursor"]=TouchCursorPresentation.Read().ToJson()});
        try
        {
            Assert.Equal(cover.Hwnd,TouchNative.At(cover.Point));probe.Begin();Sample("anchor_down");probe.Down(cover.Point,cover.Hwnd);Thread.Sleep(70);Sample("target_down");
            probe.Up();Sample("target_up");probe.Dispose();Thread.Sleep(30);Sample("device_destroyed");
            result["restore"]=TouchCursorPresentation.Restore(original.Showing||original.Suppressed,()=>Assert.Equal(original.Foreground,TouchNative.Foreground));
            var after=TouchCursorPresentation.Read();result["after"]=after.ToJson();result["cursor_unchanged"]=after.Position==original.Position;result["foreground_unchanged"]=after.Foreground==original.Foreground;
            Assert.False(after.Suppressed);Assert.True(after.Showing);Assert.Equal(original.Position,after.Position);Assert.Equal(original.Foreground,after.Foreground);result["diagnostic_completed"]=true;
        }
        catch(Exception e){result["error"]=e.GetType().Name+": "+e.Message;throw;}
        finally
        {
            probe.Dispose();result["phases"]=phases;cover.Dispose();anchor.Dispose();result["device_destroyed"]=probe.Released;result["auxiliary_windows_closed"]=cover.Closed&&anchor.Closed;Write("touch_cursor_visibility",result);
        }
    }

    [Fact] public void NativePointerLayoutMatchesWindowsX64Abi()
    {
        Assert.Equal(8,IntPtr.Size);Assert.Equal(96,Marshal.SizeOf<TouchNative.PointerInfo>());
        Assert.Equal(144,Marshal.SizeOf<TouchNative.TouchInfo>());Assert.Equal(152,Marshal.SizeOf<TouchNative.TypeInfo>());
        Assert.Equal(8,Marshal.OffsetOf<TouchNative.TypeInfo>(nameof(TouchNative.TypeInfo.Touch)).ToInt32());
        Assert.Equal(48,Marshal.SizeOf<TouchNative.LegacyTouchInput>());
    }

    [Fact] public void ExplicitNativeTouchForwardingProbe()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_FORWARD_NATIVE_PROBE")!="1")return;
        var initial=new PhysicalInputReader().Read();Assert.False(initial.ButtonsDown||initial.KeyboardDown);
        var result=new JsonObject{["at"]=DateTimeOffset.UtcNow.ToString("O"),["scope"]="self-owned windows across threads; game compatibility separate"};
        using var anchor=new TouchInputWindow(AnchorAwayFrom(initial.Position));using var target=new TouchInputWindow(new(100,100,260,220),legacyTouch:true);
        using var cover=new TouchInputWindow(new(100,100,260,220),legacyTouch:true,retainTouchHandles:true);using var probe=new SyntheticTouchDevice(anchor.Point,anchor.Hwnd);
        var forwarded=new JsonArray();
        try
        {
            void Forward(uint flag)
            {
                var timer=Stopwatch.StartNew();do
                {
                    while(cover.Touches.TryDequeue(out var touch))using(touch)
                    {if(!touch.Decoded["contacts"]!.AsArray().Any(c=>(c!["flags"]!.GetValue<uint>()&flag)!=0))continue;forwarded.Add(touch.Forward(target.Hwnd));return;}
                    Thread.Sleep(5);
                }while(timer.ElapsedMilliseconds<250);
                throw new TimeoutException("没有收到可转交的有效WM_TOUCH句柄");
            }
            Assert.Equal(cover.Hwnd,TouchNative.At(target.Point));probe.Begin();probe.Down(target.Point,target.Hwnd);Thread.Sleep(70);probe.Up();probe.Dispose();Assert.Equal(initial.Position,new PhysicalInputReader().Read().Position);Forward(2);Forward(4);Thread.Sleep(100);
            var touches=target.Received.Where(x=>x["message"]?.GetValue<uint>()==0x0240).ToArray();
            result["target_decoded_down"]=touches.Any(t=>t["touch_info_available"]!.GetValue<bool>()&&t["contacts"]!.AsArray().Any(c=>(c!["flags"]!.GetValue<uint>()&2)!=0));
            result["target_decoded_up"]=touches.Any(t=>t["touch_info_available"]!.GetValue<bool>()&&t["contacts"]!.AsArray().Any(c=>(c!["flags"]!.GetValue<uint>()&4)!=0));
            var final=new PhysicalInputReader().Read();result["foreground_unchanged"]=initial.Foreground==final.Foreground;result["cursor_unchanged"]=initial.Position==final.Position;
            Assert.True(result["target_decoded_down"]!.GetValue<bool>()&&result["target_decoded_up"]!.GetValue<bool>());Assert.Equal(initial.Foreground,final.Foreground);Assert.Equal(initial.Position,final.Position);result["diagnostic_completed"]=true;
        }
        catch(Exception e){result["error"]=e.GetType().Name+": "+e.Message;throw;}
        finally
        {
            try{probe.Dispose();}finally
            {result["forwarded"]=forwarded;result["frames"]=probe.Frames.DeepClone();result["target_received"]=new JsonArray(target.Received.Select(t=>(JsonNode)t.DeepClone()).ToArray());result["cover_received"]=new JsonArray(cover.Received.Select(t=>(JsonNode)t.DeepClone()).ToArray());cover.Dispose();target.Dispose();anchor.Dispose();result["device_destroyed"]=probe.Released;result["auxiliary_windows_closed"]=cover.Closed&&target.Closed&&anchor.Closed;Write("touch_forward_native",result);}
        }
    }

    [Fact] public void ExplicitNativeOcclusionRoutingProbe()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_NATIVE_PROBE")!="1")return;
        var initial=new PhysicalInputReader().Read();Assert.False(initial.ButtonsDown||initial.KeyboardDown);
        var result=new JsonObject{["at"]=DateTimeOffset.UtcNow.ToString("O"),["scope"]="self-owned native windows; not game compatibility",["foreground_before"]=(long)initial.Foreground};
        using var anchor=new TouchInputWindow(new(20,20,44,44));using var target=new TouchInputWindow(new(100,100,260,220));
        try
        {
            void Run(string name,TouchInputWindow? cover)
            {
                var before=target.Received.Count;using var probe=new SyntheticTouchDevice(anchor.Point,anchor.Hwnd);
                probe.Begin();probe.Down(target.Point,target.Hwnd);Thread.Sleep(70);probe.Up();Thread.Sleep(30);probe.Dispose();Thread.Sleep(80);
                result[name]=new JsonObject{["requested_target"]=(long)target.Hwnd,["window_over_target"]=(long)TouchNative.At(target.Point),["target_received"]=new JsonArray(target.Received.Skip(before).Select(x=>(JsonNode)x.DeepClone()).ToArray()),["cover_received"]=cover is null?new JsonArray():new JsonArray(cover.Received.Select(x=>(JsonNode)x.DeepClone()).ToArray()),["frames"]=probe.Frames.DeepClone(),["device_destroyed"]=probe.Released};
            }
            Assert.Equal(target.Hwnd,TouchNative.At(target.Point));Run("visible",null);
            using(var cover=new TouchInputWindow(new(100,100,260,220))){Assert.Equal(cover.Hwnd,TouchNative.At(target.Point));Run("covered",cover);}
            var final=new PhysicalInputReader().Read();result["foreground_unchanged"]=final.Foreground==initial.Foreground;result["cursor_unchanged"]=final.Position==initial.Position;result["cursor_before"]=new JsonArray(initial.Position.X,initial.Position.Y);result["cursor_after"]=new JsonArray(final.Position.X,final.Position.Y);
            result["visible_target_down"]=HasDown(result["visible"]!["target_received"]!);result["covered_target_down"]=HasDown(result["covered"]!["target_received"]!);result["covered_cover_down"]=HasDown(result["covered"]!["cover_received"]!);
            Assert.True(final.Foreground==initial.Foreground);Assert.Equal(initial.Position,final.Position);Assert.True(result["visible_target_down"]!.GetValue<bool>(),"原生触控正对照未收到按下");
            result["probe_completed"]=true;
        }
        catch(Exception e){result["error"]=e.GetType().Name+": "+e.Message;throw;}
        finally{anchor.Dispose();target.Dispose();result["auxiliary_windows_closed"]=anchor.Closed&&target.Closed;Write("touch_native_routing",result);}
    }

    [Fact] public void ExplicitOccludedGameMenuProbe()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_GAME_PROBE")!="1")return;
        var forward=Environment.GetEnvironmentVariable("MABI_TOUCH_FORWARD_PROBE")=="1";
        var competition=Environment.GetEnvironmentVariable("MABI_TOUCH_GAME_MOUSE_COMPETITION")=="1";
        var hwnd=(nint)long.Parse(Environment.GetEnvironmentVariable("MABI_BACKGROUND_HWND")??throw new InvalidOperationException("必须指定当前游戏窗口"));
        var state=Assert.Single(GameWindowDiscovery.List(),w=>w.State.WindowId==hwnd).State;Assert.NotEqual(hwnd,state.ForegroundId);Assert.False(state.Minimized);
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        var result=new JsonObject{["at"]=DateTimeOffset.UtcNow.ToString("O"),["hwnd"]=(long)hwnd,["pid"]=state.ProcessId,["geometry"]=new JsonArray(state.Geometry.X,state.Geometry.Y,state.Geometry.Width,state.Geometry.Height,state.Geometry.Dpi),["game_window_modified"]=false,["input_attempted"]=false,["forward_valid_wm_touch"]=forward};
        using var observation=new ScreenWorkflowSession(routes,state,new WindowMessageInputOps(state),ocr,new NoWrites());observation.Start();var seen=Stable(observation);Assert.Contains(seen.View.Page,new[]{"scene","skills","region_map","fishing_menu"});
        var target=Environment.GetEnvironmentVariable("MABI_TOUCH_MENU_TARGET")??(seen.View.Page=="scene"?"scene.open_skills":"skills.close");
        Assert.Contains(target,new[]{"scene.open_skills","skills.close","scene.open_map","map.close","settings.open"});
        var expected=target switch{"scene.open_map"=>"region_map","scene.open_skills"=>"skills","settings.open"=>"settings_root",_=>"scene"};var firstProof=observation.Guard.ObserveTarget(target);
        var point=new PixelPoint(state.Geometry.X+firstProof.Point.X,state.Geometry.Y+firstProof.Point.Y);
        using var mouseMonitor=competition?new TouchMouseMonitor():null;
        using var mousePad=competition?new TouchInputWindow(new(100,100,440,340),label:"游戏并发测试区\n请持续移动鼠标，并在此点击数次\n游戏仅测试一次菜单切换"):null;
        if(mousePad is not null)
        {
            Assert.False(new PixelBox(state.Geometry.X,state.Geometry.Y,state.Geometry.X+state.Geometry.Width,state.Geometry.Y+state.Geometry.Height).Contains(mousePad.Point));
            var wait=Stopwatch.StartNew();while(TouchNative.At(TouchCursorPresentation.Read().Position)!=mousePad.Hwnd&&wait.ElapsedMilliseconds<4000)Thread.Sleep(20);
            Assert.Equal(mousePad.Hwnd,TouchNative.At(TouchCursorPresentation.Read().Position));
        }
        using var anchor=new TouchInputWindow(AnchorAwayFrom(new PhysicalInputReader().Read().Position));using var cover=new TouchInputWindow(new(point.X-48,point.Y-48,point.X+48,point.Y+48),legacyTouch:forward,retainTouchHandles:forward);
        using var ops=new GuardedTouchProbeOps(state,anchor,cover,forward,mousePad);using var session=new ScreenWorkflowSession(routes,state,ops,ocr,new NoWrites());session.Start();
        var before=new PhysicalInputReader().Read();
        try
        {
            var current=Stable(session);result["before_page"]=current.View.Page;result["before_hash"]=current.ImageSha256;result["foreground_before"]=(long)before.Foreground;result["cursor_before"]=new JsonArray(before.Position.X,before.Position.Y);
            var proof=session.Guard.ObserveTarget(target);Assert.Equal(firstProof.Point,proof.Point);Assert.Equal(cover.Hwnd,TouchNative.At(point));
            result["target"]=target;result["target_screen"]=new JsonArray(point.X,point.Y);result["occluder"]=(long)cover.Hwnd;result["window_over_target"]=(long)TouchNative.At(point);result["guarded_action_requested"]=true;
            session.Guard.ClickReviewedNavigation(proof,proof.Page);Thread.Sleep(1200);var after=Stable(session);var final=new PhysicalInputReader().Read();
            result["after_page"]=after.View.Page;result["after_hash"]=after.ImageSha256;result["expected_page"]=expected;result["game_response_verified"]=after.View.Page==expected;
            result["foreground_after"]=(long)final.Foreground;result["cursor_after"]=new JsonArray(final.Position.X,final.Position.Y);result["foreground_unchanged"]=final.Foreground==before.Foreground;result["cursor_unchanged"]=final.Position==before.Position;
            var cursor=TouchCursorPresentation.Read();result["cursor_visible_after"]=cursor.Showing;result["cursor_suppressed_after"]=cursor.Suppressed;
            using var rgb=session.LastCapture!.Image.ToMat();using var bgr=new Mat();Cv2.CvtColor(rgb,bgr,ColorConversionCodes.RGB2BGR);Cv2.ImWrite(Path.Combine(root,"build/background-input-current.jpg"),bgr,new ImageEncodingParam(ImwriteFlags.JpegQuality,75));
            Assert.Equal(before.Foreground,final.Foreground);if(!competition)Assert.Equal(before.Position,final.Position);else Assert.Equal(mousePad!.Hwnd,TouchNative.At(final.Position));
            Assert.True(after.View.Page==expected || target=="settings.open"&&after.View.Page=="settings_game","触控后未出现预期页面；消息投递成功不代表游戏操作成功");result["diagnostic_completed"]=true;
        }
        catch(Exception e){result["error"]=e.GetType().Name+": "+e.Message;throw;}
        finally
        {
            try{ops.Dispose();result["device_destroyed"]=ops.Probe?.Released??true;}
            finally
            {
                result["frames"]=ops.Probe?.Frames.DeepClone();result["input_attempted"]=ops.Probe?.Frames.Any(f=>f?["submitted"]?.GetValue<bool>()==true)??false;
                result["anchor_received"]=new JsonArray(anchor.Received.Select(x=>(JsonNode)x.DeepClone()).ToArray());result["cover_received"]=new JsonArray(cover.Received.Select(x=>(JsonNode)x.DeepClone()).ToArray());
                result["forwarded"]=ops.Forwarded.DeepClone();result["cursor_trace"]=ops.CursorTrace.DeepClone();cover.Dispose();anchor.Dispose();
                if(mouseMonitor is not null&&mousePad is not null)
                {
                    var until=Stopwatch.StartNew();while(new PhysicalInputReader().Read().ButtonsDown&&until.ElapsedMilliseconds<1500)Thread.Sleep(10);
                    result["physical_moves"]=mouseMonitor.PhysicalMoves;result["physical_downs"]=mouseMonitor.PhysicalDowns;result["physical_ups"]=mouseMonitor.PhysicalUps;
                    var frameTicks=ops.Probe?.Frames.Select(f=>f!["tick"]!.GetValue<long>()).ToArray()??[];
                    var observedEvents=mouseMonitor.Events.ToArray();var intervalRetained=frameTicks.Length>0&&observedEvents.Length>0&&observedEvents[0].Tick<=frameTicks.Min();
                    result["contact_interval_retained"]=intervalRetained;
                    result["physical_events_during_contacts"]=intervalRetained?JsonValue.Create(observedEvents.Count(e=>e.Physical&&e.Tick>=frameTicks.Min()&&e.Tick<=frameTicks.Max())):null;
                    result["pad_moves"]=mousePad.MouseMoves;result["pad_downs"]=mousePad.MouseDowns;result["pad_ups"]=mousePad.MouseUps;result["absolute_mouse_position_written"]=false;
                    result["physical_events"]=new JsonArray(mouseMonitor.Events.ToArray().TakeLast(16).Select(e=>(JsonNode)e.ToJson()).ToArray());
                    mousePad.Dispose();mouseMonitor.Dispose();
                }
                result["auxiliary_windows_closed"]=cover.Closed&&anchor.Closed&&(mousePad?.Closed??true);Write(competition?"touch_mouse_game":forward?"touch_forward_game":"touch_occluded_game",result);
            }
        }
    }
    private static bool HasDown(JsonNode node)=>node.AsArray().Any(x=>x?["message"]?.GetValue<uint>()==0x0246);
    private static PixelBox AnchorAwayFrom(PixelPoint cursor)=>cursor==new PixelPoint(32,32)?new(70,20,94,44):new(20,20,44,44);
    private static UiObservation Stable(ScreenWorkflowSession session)
    {var t=Stopwatch.StartNew();do{var o=session.Observe();if(o.Stable)return o;Thread.Sleep(100);}while(t.Elapsed<TimeSpan.FromSeconds(10));throw new InputBlockedException("当前画面无法稳定识别");}
    private void Write(string key,JsonObject result)
    {
        var path=Path.Combine(RecognitionTests.Root(),"build/reference-optimization-validation.json");var report=JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if(key=="touch_forward_game"&&report[key] is JsonObject previous)
        {
            var samples=previous["previous_samples"]?.DeepClone().AsArray()??new JsonArray();var sample=new JsonObject();
            foreach(var field in new[]{"at","target","before_page","after_page","game_response_verified","foreground_unchanged","cursor_unchanged","error"})sample[field]=previous[field]?.DeepClone();
            samples.Add(sample);while(samples.Count>4)samples.RemoveAt(0);result["previous_samples"]=samples;
        }
        report[key]=result;File.WriteAllText(path,report.ToJsonString(new(){WriteIndented=true}));output.WriteLine(result.ToJsonString());
    }
    private sealed class NoWrites:MabiAgent.Workflows.IWorkflowStore
    {public JsonObject? Read()=>null;public void Flush(JsonObject state)=>throw new InvalidOperationException("触控诊断禁止写入业务意图");}
}
