using System.Diagnostics;
using System.Text.Json.Nodes;
using MabiAgent.Capture;
using MabiAgent.Windows;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;
namespace MabiAgent.Windows.Tests;

public sealed class BackgroundGameProbeTests(ITestOutputHelper output)
{
    // Explicit single fixed action through the same production guard and transport.
    [Fact] public async Task ProbeFixedScriptStep()
    {
        var action=Environment.GetEnvironmentVariable("MABI_FIXED_PROBE");if(action is null)return;
        Assert.Contains(action,new[]{"observe","click","key","theater_enter","theater_full"});
        var hwnd=(nint)long.Parse(Environment.GetEnvironmentVariable("MABI_BACKGROUND_HWND")??throw new InvalidOperationException("需要已观察的窗口"));
        var state=Assert.Single(GameWindowDiscovery.List(),w=>w.State.WindowId==hwnd).State;
        Assert.False(state.Minimized);Assert.NotEqual(hwnd,state.ForegroundId);
        var ops=InputTransportFactory.Create(state);
        using var guard=new ForegroundInputGuard(state,ScreenWorkflowSession.ProcessScope(state),ops,_=>throw new InvalidOperationException("固定步骤不识别页面"));
        CaptureCoordinator? capture=null;
        try
        {
        if(Environment.GetEnvironmentVariable("MABI_FIXED_CAPTURE")=="1")
        {
            var decoder=ClientInventoryDecoder.Load(Path.Combine(RecognitionTests.Root(),"config/client-inventory-schema.disk.json"));
            var metadata=new AccountItemService(Path.Combine(RecognitionTests.Root(),"data/agent-run/desktop/accounts")).KnownCaptureIdentities();
            capture=new CaptureCoordinator(decoder.Decode,identityMetadata:metadata);capture.SelectProcess(state.ProcessId);_ = capture.RunAsync();
            var listening=Stopwatch.StartNew();
            while(capture.Snapshot().State!=CaptureState.Listening&&listening.Elapsed<TimeSpan.FromSeconds(12))await Task.Delay(100);
            Assert.True(capture.Snapshot().State==CaptureState.Listening,"被动抓包未就绪，固定输入未发送："+capture.Snapshot().Message);
        }
        guard.Start();var began=Stopwatch.GetTimestamp();
        if(action=="key")guard.PressKey(ushort.Parse(Environment.GetEnvironmentVariable("MABI_FIXED_KEY")!),TimeSpan.FromMilliseconds(80));
        if(action=="click"){var p=Environment.GetEnvironmentVariable("MABI_FIXED_POINT")!.Split(',').Select(int.Parse).ToArray();Assert.Equal(2,p.Length);guard.ClickAt(new(p[0],p[1]));}
        if(action is "theater_enter" or "theater_full")
        {
            var routes=ScreenRoutes.Load(RecognitionTests.Root());
            using var session=new ScreenWorkflowSession(routes,state,InputTransportFactory.Create(state),new ProbeNoOcr(),new Store());session.Start();
            var initial=session.Observe();Assert.Equal("scene",initial.View.Page);
            var script=new ScreenPrivateTheaterRefresh(session);script.Start(DateTimeOffset.UtcNow);var timeout=Stopwatch.StartNew();
            using var contact=new Mat(720,1280,MatType.CV_8UC3,Scalar.Black);var frameIndex=0;string? lastStep=null;
            while(script.Status==MabiAgent.Workflows.PrivateTheaterRefreshStatus.Running&&timeout.Elapsed<TimeSpan.FromSeconds(100))
            {
                if(Environment.GetEnvironmentVariable("MABI_FIXED_TRACE")=="1"&&script.CurrentStep!=lastStep&&frameIndex<12)
                {
                    var frame=session.Guard.Capture().Image;using var rgb=frame.ToMat();using var bgr=new Mat();Cv2.CvtColor(rgb,bgr,ColorConversionCodes.RGB2BGR);using var tile=new Mat(contact,new Rect(frameIndex%4*320,frameIndex/4*240,320,240));Cv2.Resize(bgr,tile,new Size(320,240));
                    Cv2.PutText(contact,script.CurrentStep??"done",new Point(frameIndex%4*320+5,frameIndex/4*240+20),HersheyFonts.HersheySimplex,.45,Scalar.Red,1);frameIndex++;lastStep=script.CurrentStep;
                    Cv2.ImWrite(Path.Combine(RecognitionTests.Root(),"build/theater-steps-current.jpg"),contact,new ImageEncodingParam(ImwriteFlags.JpegQuality,75));
                }
                if(action=="theater_enter"&&script.CurrentStep=="map_loaded") {await Task.Delay(8200);break;}
                script.Tick(DateTimeOffset.UtcNow);await Task.Delay(150);
            }
            SaveImage(ops.CaptureClient(ops.ReadWindow(hwnd)),Path.Combine(RecognitionTests.Root(),"build/background-input-current.jpg"));
            File.WriteAllText(Path.Combine(RecognitionTests.Root(),"build/theater-script-current.json"),new JsonObject{["at"]=DateTimeOffset.UtcNow.ToString("O"),["status"]=script.Status.ToString(),["step"]=script.CurrentStep,["failure"]=script.Failure}.ToJsonString());
            if(action=="theater_enter")Assert.True(script.Status==MabiAgent.Workflows.PrivateTheaterRefreshStatus.Running&&script.CurrentStep=="map_loaded",script.Failure??script.CurrentStep);
            if(action=="theater_full")Assert.True(script.Status==MabiAgent.Workflows.PrivateTheaterRefreshStatus.Complete,script.Failure??script.CurrentStep);
        }
        var dispatchMs=Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        Thread.Sleep(1500);var final=ops.ReadWindow(hwnd);var image=ops.CaptureClient(final);
        SaveImage(image,Path.Combine(RecognitionTests.Root(),"build/background-input-current.jpg"));
        var report=new JsonObject{["at"]=DateTimeOffset.UtcNow.ToString("O"),["action"]=action,["hwnd"]=(long)hwnd,["width"]=final.Geometry.Width,["height"]=final.Geometry.Height,["dpi"]=final.Geometry.Dpi,["dispatch_ms"]=dispatchMs,["foreground_unchanged"]=final.ForegroundId==state.ForegroundId,["cursor_unchanged"]=final.Cursor==state.Cursor,["delivery_only"]=action!="observe"};
        File.WriteAllText(Path.Combine(RecognitionTests.Root(),"build/fixed-script-current.json"),report.ToJsonString());
        if(capture is not null)await Task.Delay(6500);
        Assert.Equal(state.ForegroundId,final.ForegroundId);output.WriteLine(report.ToJsonString());
        }
        finally
        {
            if(capture is not null)
            {
                try{SavePrivateTheaterCapture(capture.Snapshot(),action);}finally{await capture.DisposeAsync();}
            }
        }
    }
    private sealed class ProbeNoOcr:IOcrEngine { public OcrLine? Recognize(int width,int height,byte[] grayPixels)=>null; }
    private static void SavePrivateTheaterCapture(CaptureStatus status,string action)
    {
        var inventory=status.Inventory;
        var healthy=inventory is {VerifiedThrough:not null,Error:null,Scope:not null,Identity:not null,FullSyncRevision:>0};
        var containers=new JsonObject();
        foreach(var container in Enum.GetValues<InventoryContainer>())
        {
            var counts=inventory?.Counts.GetValueOrDefault(container);
            containers[container.ToString()]=new JsonObject{["item_types"]=counts?.Count,["complete_coverage"]=healthy&&counts is not null};
        }
        var metrics=status.Metrics;
        var report=new JsonObject
        {
            ["at"]=DateTimeOffset.UtcNow.ToString("O"),["action"]=action,["state"]=status.State.ToString(),["message"]=status.Message,
            ["source"]="packet_capture",["account_id"]=inventory?.Identity?.AccountId,["character_id"]=inventory?.Identity?.CharacterId,
            ["session_id"]=inventory?.Scope?.SessionId,["full_sync_revision"]=inventory?.FullSyncRevision,["revision"]=inventory?.Revision,
            ["full_sync_at"]=inventory?.FullSyncAt,["verified_through"]=inventory?.VerifiedThrough,["error"]=inventory?.Error,
            ["containers"]=containers,["packets"]=metrics.Packets,["payload_bytes"]=metrics.PayloadBytes,["decoded_frames"]=metrics.DecodedFrames,
            ["iron_ore_backpack"]=inventory?.Quantity(InventoryContainer.Backpack,198875762),["iron_ingot_backpack"]=inventory?.Quantity(InventoryContainer.Backpack,1156816717),["gold"]=inventory?.Gold,
            ["connections"]=metrics.Connections,["buffered_bytes"]=metrics.BufferedBytes,["invalidations"]=metrics.Invalidations
        };
        File.WriteAllText(Path.Combine(RecognitionTests.Root(),"build/private-theater-capture-current.json"),report.ToJsonString());
    }
    // Explicit single-action validation through the project guard, never a queue runner.
    [Fact] public void ProbeExplicitlySelectedCurrentGame()
    {
        var action=Environment.GetEnvironmentVariable("MABI_BACKGROUND_PROBE");if(action is null)return;
        Assert.Contains(action,new[]{"observe","click_open_bag","click_open_skills","click_close_skills","click_close_bag","click_open_search","click_focus_search","click_open_settings","click_close_settings","click_open_map","click_close_map","repair_map_navigation","touch_cycles","map_drag","map_pinch_out","map_pinch","text","backspace","shortcut_x","shortcut_i","shortcut_cycles"});
        var hwnd=(nint)long.Parse(Environment.GetEnvironmentVariable("MABI_BACKGROUND_HWND")??throw new InvalidOperationException("需要指定已观察的目标窗口"));
        var selected=Assert.Single(GameWindowDiscovery.List(),w=>w.State.WindowId==hwnd);var state=selected.State;
        var foregroundSetup=Environment.GetEnvironmentVariable("MABI_PROBE_FOREGROUND")=="1";
        if(foregroundSetup)Assert.Equal(hwnd,state.ForegroundId);else Assert.NotEqual(hwnd,state.ForegroundId);Assert.False(state.Minimized);
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        var variant=Environment.GetEnvironmentVariable("MABI_MESSAGE_VARIANT")??"plain";Assert.Contains(variant,new[]{"plain","activate","send","focus","burst","touch","touch_native","touch_forward"});
        var messages=new JsonArray();
        INativeWindowsOps ops=variant=="touch_forward"?new TouchInputOps(state,new NativeWindowsOps(),new WindowMessageInputOps(state,notifyActivation:true),new NativeTouchGestureRunner(nativeRouting:false)):variant=="touch_native"?new TouchInputOps(state,new NativeWindowsOps(),new WindowMessageInputOps(state,notifyActivation:true),new NativeTouchGestureRunner(nativeRouting:true)):variant=="touch"?InputTransportFactory.Create(state):foregroundSetup?new NativeWindowsOps():new WindowMessageInputOps(state,notifyActivation:variant!="plain",synchronous:variant=="send",notifyApplication:variant=="focus",allowMouseProbe:action.StartsWith("click_",StringComparison.Ordinal),noDelayMouseProbe:variant=="burst",trace:t=>{if(messages.Count<32)messages.Add(new JsonObject{["at"]=t.At.ToString("O"),["hwnd"]=(long)t.Hwnd,["message"]=t.Message,["wparam"]=t.WParam,["lparam"]=t.LParam,["submitted"]=t.Submitted,["error"]=t.Error,["elapsed_ms"]=t.ElapsedMilliseconds,["cursor_before"]=new JsonArray(t.CursorBefore.X,t.CursorBefore.Y),["cursor_after"]=new JsonArray(t.CursorAfter.X,t.CursorAfter.Y),["foreground_before"]=(long)t.ForegroundBefore,["foreground_after"]=(long)t.ForegroundAfter});});using var session=new ScreenWorkflowSession(routes,state,ops,ocr,new Store());session.Start();
        UiObservation Stable()
        {
            var timer=Stopwatch.StartNew();UiObservation? o=null;do{o=session.Observe();if(o.Stable)return o;Thread.Sleep(100);}while(timer.Elapsed<TimeSpan.FromSeconds(10));
            throw new InputBlockedException("当前页无法稳定识别："+o?.View.Page);
        }
        var reportPath=Path.Combine(root,"build/reference-optimization-validation.json");var report=File.Exists(reportPath)?JsonNode.Parse(File.ReadAllText(reportPath))!.AsObject():new JsonObject();
        report["background_game"]??=new JsonObject();var entryKey=(foregroundSetup?(variant=="touch"?"foreground_touch_":"foreground_"):variant=="plain"?"":variant+"_")+action;
        var history=report["background_game"]![entryKey]?["previous_samples"]?.DeepClone() as JsonArray??new JsonArray();
        if(report["background_game"]![entryKey] is JsonObject prior){var copy=prior.DeepClone().AsObject();copy.Remove("previous_samples");history.Add(copy);while(history.Count>5)history.RemoveAt(0);}
        var entry=new JsonObject{["recorded_utc"]=DateTimeOffset.UtcNow.ToString("O"),["hwnd"]=(long)hwnd,["pid"]=state.ProcessId,["dpi"]=state.Geometry.Dpi,["width"]=state.Geometry.Width,["height"]=state.Geometry.Height,["transport"]=ops is TouchInputOps?"TouchInputOps":foregroundSetup?"SendInput":variant=="send"?"SendMessageTimeoutW":"PostMessageW",["activation_hint"]=!foregroundSetup&&variant!="plain",["capture"]="PrintWindow",["input_attempted"]=false,["foreground_before"]=(long)state.ForegroundId,["move_sample_ms"]=(ops as IGuardedWindowTransport)?.MoveSampleMilliseconds??16,["click_sample_ms"]=(ops as IGuardedWindowTransport)?.ClickSampleMilliseconds??32,["sequence_mode"]=variant=="burst"?"guarded_no_extra_wait_not_atomic":"sampled"};if(history.Count>0)entry["previous_samples"]=history;report["background_game"]![entryKey]=entry;
        try
        {
            var before=action=="observe"?session.Observe():Stable();entry["before_page"]=before.View.Page;entry["before_modal"]=before.View.Modal;entry["before_hash"]=before.ImageSha256;
            if(action=="observe")
            {
                if(Environment.GetEnvironmentVariable("MABI_PROBE_FISHING_POINT") is {} point)
                {
                    var maintenance=session.CreateMaintenancePort("diagnostic",new Store());var fishing=session.CreateFishingPort(point,"diagnostic",new Store());
                    var samples=new JsonArray();entry["fishing_observations"]=samples;
                    var count=int.TryParse(Environment.GetEnvironmentVariable("MABI_PROBE_SAMPLES"),out var requested)?requested:4;Assert.InRange(count,1,16);
                    for(var i=0;i<count;i++)
                    {
                        var m=maintenance.Observe();var f=fishing.ObserveOnly();
                        var view=session.LastObservation!.View;
                        samples.Add(new JsonObject{["page"]=f.Page,["modal"]=view.Modal,["mounted"]=f.Mounted,["cast"]=f.FishingButton,["compass"]=f.Compass,["stable"]=f.Stable,["reason"]=session.StabilityReason,["at"]=session.LastCapture!.CapturedAt,["maintenance"]=System.Text.Json.JsonSerializer.SerializeToNode(m),["hits"]=System.Text.Json.JsonSerializer.SerializeToNode(view.Hits.Select(h=>new{h.Name,h.Score,h.Box})),["targets"]=System.Text.Json.JsonSerializer.SerializeToNode(view.Targets),["boxes"]=System.Text.Json.JsonSerializer.SerializeToNode(view.StableBoxes)});
                        if(f.Mounted||f.Page=="unknown")SaveImage(session.LastCapture.Image,Path.Combine(root,"build/fishing-recognition-current.jpg"));
                    }
                }
                using var matcher=new TemplateMatcher(routes.Profile);var candidates=new JsonArray();
                var names=Environment.GetEnvironmentVariable("MABI_PROBE_ANCHORS")?.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
                    ??routes.Profile.Templates.Keys.Where(n=>n.Contains("ferghus_face")||n=="maint_ferghus_portrait"||n.StartsWith("purchase.map_portrait")).ToArray();
                Assert.InRange(names.Length,1,32);
                foreach(var name in names)
                {
                    var hit=matcher.Match(session.LastCapture!.Image,name,unique:false,thresholdOverride:.1);
                    candidates.Add(new JsonObject{["name"]=name,["score"]=hit?.Score,["box"]=hit is null?null:new JsonArray(hit.Box.Left,hit.Box.Top,hit.Box.Right,hit.Box.Bottom),["accepted"]=matcher.Match(session.LastCapture.Image,name) is not null});
                }
                entry["portrait_candidates"]=candidates;
                var pages=routes.Profile.Definition["pages"]!.AsObject().Select(p=>(p.Key,p.Value!)).Concat(routes.Profile.Definition["page_variants"]?.AsObject().Select(p=>(p.Value!["page"]!.GetValue<string>(),p.Value!))??[]);
                entry["page_candidates"]=new JsonArray(pages.Where(p=>NavigationProfile.Names(p.Item2["anchors"]) is {Length:>0} required&&required.All(n=>matcher.Anchor(session.LastCapture!.Image,n) is not null)).Select(p=>(JsonNode?)new JsonObject{["page"]=p.Item1,["anchors"]=p.Item2["anchors"]!.DeepClone(),["covers_pages"]=p.Item2["covers_pages"]?.DeepClone()}).ToArray());
                if(Environment.GetEnvironmentVariable("MABI_PROBE_CROP") is {} cropText)
                {
                    var edges=cropText.Split(',').Select(int.Parse).ToArray();Assert.Equal(4,edges.Length);
                    var crop=new PixelBox(edges[0],edges[1],edges[2],edges[3]);
                    Assert.True(crop.IsInside(state.Geometry.Width,state.Geometry.Height));
                    Assert.InRange(crop.Width,1,512);Assert.InRange(crop.Height,1,128);
                    using var rgb=session.LastCapture!.Image.Crop(crop).ToMat();using var bgr=new Mat();
                    Cv2.CvtColor(rgb,bgr,ColorConversionCodes.RGB2BGR);Cv2.ImWrite(Path.Combine(root,"build/background-input-detail.png"),bgr);
                    entry["detail_crop"]=new JsonArray(edges.Select(v=>(JsonNode?)JsonValue.Create(v)).ToArray());
                    if(Environment.GetEnvironmentVariable("MABI_PROBE_OCR")=="1")
                    {
                        var thresholds=Environment.GetEnvironmentVariable("MABI_PROBE_THRESHOLDS")?.Split(',').Select(int.Parse).ToArray()??[165,185];
                        Assert.InRange(thresholds.Length,1,4);Assert.All(thresholds,t=>Assert.InRange(t,0,255));
                        entry["detail_reading"]=System.Text.Json.JsonSerializer.SerializeToNode(session.QuantityReader.Read(session.LastCapture.Image,
                            new QuantitySpec(1,"diagnostic",crop,Environment.GetEnvironmentVariable("MABI_PROBE_FORMAT")??"grouped_integer",Thresholds:thresholds)));
                    }
                }
            }
            var current=ops.ReadWindow(hwnd);if(!foregroundSetup)Assert.NotEqual(hwnd,current.ForegroundId);var cursor=current.Cursor;var foreground=current.ForegroundId;
            entry["cursor_before"]=new JsonArray(cursor.X,cursor.Y);entry["foreground_before"]=(long)foreground;
            entry["window_diagnostics"]=WindowMessageDiagnostics.Read(current);
            if(action=="repair_map_navigation")
            {
                Assert.Equal("touch",variant);Assert.Equal("region_map",before.View.Page);
                var route=routes.Configuration["maintenance"]!["navigation"]!["repair_quote"]![before.View.Page]!.AsObject();
                var port=session.CreateMaintenancePort("diagnostic",new Store());var timer=Stopwatch.StartNew();var traces=new JsonArray();entry["navigation_trace"]=traces;entry["input_attempted"]=true;
                NavigationTestProgress progress;
                do
                {
                    progress=port.TestNavigationRoute(route);
                    if(port.LastTestNavigationTrace is {} trace&&(traces.Count==0||traces[^1]!["target"]!.GetValue<string>()!=trace.Target||traces[^1]!["phase"]!.GetValue<string>()!=trace.Phase))
                        traces.Add(new JsonObject{["target"]=trace.Target,["phase"]=trace.Phase});
                    if(progress.Completed||progress.Error is not null)break;
                    Thread.Sleep(200);
                }while(timer.ElapsedMilliseconds<90000);
                entry["route_completed"]=progress.Completed;entry["route_error"]=progress.Error;
                Assert.True(progress.Completed,progress.Error??"维修地图路线超过90秒");
            }
            if(action=="touch_cycles")
            {
                Assert.Equal("touch",variant);var samples=new JsonArray();entry["cycles"]=samples;
                for(var i=0;i<6;i++)
                {
                    var initial=Stable();Assert.Contains(initial.View.Page,new[]{"scene","skills"});
                    var expected=initial.View.Page=="scene"?"skills":"scene";
                    var proof=session.Guard.ObserveTarget(initial.View.Page=="scene"?"scene.open_skills":"skills.close");
                    var began=Stopwatch.GetTimestamp();entry["input_attempted"]=true;session.Guard.ClickReviewedNavigation(proof,proof.Page);
                    var dispatchMs=Stopwatch.GetElapsedTime(began).TotalMilliseconds;Thread.Sleep(500);var seen=Stable();var actual=ops.ReadWindow(hwnd);
                    var verified=seen.View.Page==expected&&actual.ForegroundId==foreground;
                    samples.Add(new JsonObject{["index"]=i+1,["before"]=initial.View.Page,["after"]=seen.View.Page,["verified"]=verified,["foreground_unchanged"]=actual.ForegroundId==foreground,["cursor_unchanged"]=actual.Cursor==cursor,["dispatch_ms"]=dispatchMs});
                    Assert.True(verified,"触控菜单切换未通过，不重发");
                }
            }
            if(action is "map_drag" or "map_pinch" or "map_pinch_out")
            {
                Assert.Equal("touch",variant);Assert.Equal("region_map",before.View.Page);
                using var beforeImage=session.LastCapture!.Image.ToMat();var beforeSample=ScreenMaintenancePort.MapSample(session.LastCapture.Image);
                var proof=session.Guard.ObserveTarget("map.canvas");entry["input_attempted"]=true;
                if(action=="map_drag")session.Guard.Drag(proof,new(proof.Point.X+120,proof.Point.Y+40),6);
                else
                {
                    var steps=int.TryParse(Environment.GetEnvironmentVariable("MABI_MAP_ZOOM_STEPS"),out var configured)?configured:4;
                    Assert.InRange(steps,1,12);entry["zoom_steps"]=steps;
                    session.Guard.Scroll(proof,action=="map_pinch_out"?-steps:steps,zoom:true);
                }
                Thread.Sleep(600);Assert.Equal("region_map",Stable().View.Page);
                using var afterImage=session.LastCapture!.Image.ToMat();using var first=new Mat(beforeImage,new Rect(200,150,850,630));using var last=new Mat(afterImage,new Rect(200,150,850,630));
                var transform=MapTouchGestureProbeTests.Measure(first,last);entry["map_transform"]=transform;
                entry["map_sample_changed"]=ScreenMaintenancePort.MapChanged(beforeSample,ScreenMaintenancePort.MapSample(session.LastCapture.Image));
                var scale=transform["scale"]?.GetValue<double>()??0;
                var verified=transform["verified"]?.GetValue<bool>()==true&&(action=="map_drag"?Math.Abs(scale-1)<.02&&Math.Abs(transform["dx"]!.GetValue<double>()-120)<10&&Math.Abs(transform["dy"]!.GetValue<double>()-40)<10:action=="map_pinch_out"?scale is >0 and <.95:scale>1.05);
                Assert.True(verified,"地图地标未确认预期平移或缩放，不重发");
            }
            if(action.StartsWith("click_",StringComparison.Ordinal))
            {
                if(variant=="burst"&&new PixelBox(0,0,current.Geometry.Width,current.Geometry.Height).Contains(new(current.Cursor.X-current.Geometry.X,current.Cursor.Y-current.Geometry.Y)))throw new InputBlockedException("无等待诊断需真实鼠标停在游戏客户区外；本次未发送点击");
                var target=action switch{"click_open_bag"=>"scene.open_bag","click_open_skills"=>"scene.open_skills","click_close_skills"=>"skills.close","click_close_bag"=>"bag.close","click_open_search"=>"bag.search_open","click_open_settings"=>"settings.open","click_close_settings"=>"settings.close","click_open_map"=>"scene.open_map","click_close_map"=>"map.close",_=>"bag.search_input"};
                var proof=session.Guard.ObserveTarget(target);entry["target"]=target;entry["client_point"]=new JsonArray(proof.Point.X,proof.Point.Y);entry["client_origin"]=new JsonArray(current.Geometry.X,current.Geometry.Y);entry["window_diagnostics"]=WindowMessageDiagnostics.Read(current,proof.Point);entry["mouse_prelude"]="pressed_move";entry["input_attempted"]=true;session.Guard.ClickReviewedNavigation(proof,proof.Page);
            }
            if(action is "text" or "backspace")
            {
                Assert.Equal("bag_search",before.View.Page);
                if(action=="text")Assert.Contains("bag.search_empty",before.View.Targets.Keys);
                if(action=="backspace")Assert.True(session.QuantityReader.Exact(session.LastCapture!.Image,session.Guard.ObserveTarget("bag.search_input").ClickRegion??throw new InputBlockedException("缺少搜索框UI区域"),"鐵礦石"),"只清除本次已核实的测试文字");
                var proof=session.Guard.ObserveTarget("bag.search_input");entry["input_attempted"]=true;session.Guard.TypeText(proof,action=="text"?"鐵礦石":"",backspaces:action=="backspace"?3:0,focus:false);
            }
            if(action=="shortcut_x")
            {
                Assert.Equal("scene",before.View.Page);var proof=session.Guard.ObserveTarget("scene.open_storage");entry["input_attempted"]=true;session.Guard.PressKey(proof,88,TimeSpan.FromMilliseconds(80));
            }
            if(action=="shortcut_i")
            {
                Assert.Contains(before.View.Page,new[]{"scene","bag","repair_tools"});var proof=session.Guard.ObserveTarget(before.View.Page=="scene"?"scene.open_bag":"bag.close");entry["input_attempted"]=true;session.Guard.PressKey(proof,73,TimeSpan.FromMilliseconds(80));
            }
            if(action=="shortcut_cycles")
            {
                var samples=new JsonArray();entry["cycles"]=samples;
                for(var i=0;i<6;i++)
                {
                    var initial=Stable();Assert.Contains(initial.View.Page,new[]{"scene","bag"});Assert.Equal(foreground,ops.ReadWindow(hwnd).ForegroundId);
                    var expected=initial.View.Page=="scene"?"bag":"scene";var proof=session.Guard.ObserveTarget(initial.View.Page=="scene"?"scene.open_bag":"bag.close");entry["input_attempted"]=true;session.Guard.PressKey(proof,73,TimeSpan.FromMilliseconds(80));Thread.Sleep(1200);
                    var seen=Stable();var unchanged=ops.ReadWindow(hwnd).ForegroundId==foreground;samples.Add(new JsonObject{["index"]=i+1,["before"]=initial.View.Page,["expected"]=expected,["after"]=seen.View.Page,["foreground_unchanged"]=unchanged,["verified"]=seen.View.Page==expected&&unchanged,["hash"]=seen.ImageSha256});
                    Assert.True(seen.View.Page==expected&&unchanged,"快捷键对照未通过，不重发");
                }
            }
            Thread.Sleep(action=="observe"?100:1200);var after=action=="observe"?session.Observe():Stable();var final=ops.ReadWindow(hwnd);
            entry["after_page"]=after.View.Page;entry["after_hash"]=after.ImageSha256;entry["foreground_unchanged"]=final.ForegroundId==foreground;entry["cursor_unchanged"]=final.Cursor==cursor;
            entry["cursor_before"]=new JsonArray(cursor.X,cursor.Y);entry["cursor_after"]=new JsonArray(final.Cursor.X,final.Cursor.Y);
            var box=action is "text" or "backspace"?session.Guard.ObserveTarget("bag.search_input").ClickRegion??throw new InputBlockedException("缺少搜索框UI区域"):default;
            var accepted=action switch
            {
                "click_open_bag"=>after.View.Page=="bag",
                "click_open_skills"=>after.View.Page=="skills",
                "click_close_skills"=>after.View.Page=="scene",
                "click_close_bag"=>after.View.Page=="scene",
                "click_open_settings"=>after.View.Page is "settings_root" or "settings_game",
                "click_close_settings"=>after.View.Page=="scene",
                "click_open_map"=>after.View.Page=="region_map",
                "click_close_map"=>after.View.Page=="scene",
                "click_open_search"=>after.View.Page=="bag_search",
                "text"=>after.View.Page=="bag_search"&&session.QuantityReader.Exact(session.LastCapture!.Image,box,"鐵礦石"),
                "backspace"=>after.View.Page=="bag_search"&&after.View.Targets.ContainsKey("bag.search_empty"),
                "shortcut_x"=>after.View.Page is "storage_dialog" or "npc_dialogue",
                "shortcut_i"=>after.View.Page==(before.View.Page=="scene"?"bag":"scene"),
                _=>true
            };
            entry["response_verified"]=accepted;entry["status"]=accepted?"response_verified":"not_observed";
            SaveImage(session.LastCapture!.Image,Path.Combine(root,"build/background-input-current.jpg"));
            Assert.True(final.ForegroundId==foreground,"验证期间前台发生变化");if(variant=="burst")Assert.True(final.Cursor==cursor,"无等待诊断期间真实光标变化，不能作为受控通过证据");Assert.True(accepted,"输入后未出现预期画面，不重发，详见验证报告");entry["validation_passed"]=true;
        }
        catch(Exception error){entry["error"]=error.GetType().Name+": "+error.Message[..Math.Min(error.Message.Length,512)];entry["status"]="failed";entry["validation_passed"]=false;if(session.LastCapture is {} c){entry["after_page"]=session.CurrentPage;entry["after_hash"]=c.Image.Sha256;SaveImage(c.Image,Path.Combine(root,"build/background-input-current.jpg"));}throw;}
        finally
        {
            // Keep input diagnostics even when the destination cannot be recognized.
            try{var final=ops.ReadWindow(hwnd);entry["foreground_after"]=(long)final.ForegroundId;entry["cursor_after"]=new JsonArray(final.Cursor.X,final.Cursor.Y);entry["foreground_unchanged"]=(long)final.ForegroundId==entry["foreground_before"]!.GetValue<long>();if(entry["cursor_before"] is JsonArray start)entry["cursor_unchanged"]=start[0]!.GetValue<int>()==final.Cursor.X&&start[1]!.GetValue<int>()==final.Cursor.Y;}
            catch(Exception error){entry["final_window_error"]=error.GetType().Name;}
            if(entry["input_attempted"]!.GetValue<bool>())entry["response_verified"]??=false;
            if(messages.Count>0)entry["messages"]=messages;
            File.WriteAllText(reportPath,report.ToJsonString(new(){WriteIndented=true}));output.WriteLine(entry.ToJsonString());
        }
    }
    private static void SaveImage(RgbImage image,string path)
    {using var rgb=image.ToMat();using var bgr=new Mat();Cv2.CvtColor(rgb,bgr,ColorConversionCodes.RGB2BGR);Cv2.ImWrite(path,bgr,new ImageEncodingParam(ImwriteFlags.JpegQuality,75));}
    private sealed class Store:MabiAgent.Workflows.IWorkflowStore
    {public JsonObject? Read()=>null;public void Flush(JsonObject state)=>throw new InvalidOperationException("探测禁止写入业务意图");}
}
