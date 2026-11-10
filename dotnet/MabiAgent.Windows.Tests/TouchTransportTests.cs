using System.Text.Json.Nodes;
using Xunit;
namespace MabiAgent.Windows.Tests;

[Collection("Native process metadata")]
public sealed class TouchTransportTests
{
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)]
    public void EmptyLocalTouchFramesRebuildOnlyUntilTheThirdAttempt(int failures)
    {
        var calls=0;var reviews=0;var deliveries=0;
        void Execute(){calls++;if(calls<=failures)throw new LocalTouchFramesUnavailableException("0/2");deliveries++;}
        if(failures==3)Assert.Throws<MabiAgent.Workflows.InputRejectedBeforeDispatchException>(()=>TouchInputOps.ExecutePreparedGesture(Execute,()=>reviews++));
        else TouchInputOps.ExecutePreparedGesture(Execute,()=>reviews++);
        Assert.Equal(Math.Min(3,failures+1),calls);Assert.Equal(calls,reviews);Assert.Equal(failures==3?0:1,deliveries);
    }
    [Fact] public void LocalTouchRecoveryDoesNotRepeatUnknownDeliveryOrAChangedTarget()
    {
        var calls=0;
        Assert.Throws<InputOutcomeUnknownException>(()=>TouchInputOps.ExecutePreparedGesture(()=>{calls++;throw new InputOutcomeUnknownException("已发送");},()=>{}));
        Assert.Equal(1,calls);calls=0;var reviews=0;
        Assert.Throws<InputBlockedException>(()=>TouchInputOps.ExecutePreparedGesture(()=>{calls++;throw new LocalTouchFramesUnavailableException("0/2");},()=>{if(++reviews>1)throw new InputBlockedException("暂停或窗口变化");}));
        Assert.Equal(1,calls);
    }
    [Fact] public void TouchDiagnosticsWritesCorrelatedStructuredEvent()
    {
        var directory=Path.Combine(Path.GetTempPath(),"mabi-touch-trace-"+Guid.NewGuid().ToString("N"));
        try
        {
            var diagnostics=new MabiAgent.Core.RuntimeDiagnostics(directory);
            var occurredAt=new DateTimeOffset(2026,11,4,1,2,3,TimeSpan.Zero);TouchDiagnostics.Write(diagnostics,"gesture-1","forwarded",new(){["submitted"]=true},occurredAt);
            var value=System.Text.Json.JsonSerializer.Deserialize<MabiAgent.Core.RuntimeFaultEvent>(Assert.Single(File.ReadAllLines(Path.Combine(directory,"runtime-events.jsonl"))))!;
            Assert.Equal(occurredAt,value.At);Assert.Equal("gesture-1",value.TraceId);Assert.Equal("touch",value.Module);Assert.Equal("forwarded",value.Stage);Assert.Equal("info",value.Severity);
            Assert.Contains("\"submitted\":true",value.Message,StringComparison.Ordinal);
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact] public void TouchDiagnosticsWithoutSinkDoesNothing()=>TouchDiagnostics.Write(null,"gesture-1","started");
    [Fact] public void TouchDiagnosticsWriteFailureIsExposedThroughSharedStateWithoutThrowing()
    {
        var path=Path.GetTempFileName();
        try{var diagnostics=new MabiAgent.Core.RuntimeDiagnostics(path);Assert.False(TouchDiagnostics.Write(diagnostics,"gesture-1","forwarded"));Assert.NotNull(diagnostics.WriteError);}
        finally{File.Delete(path);}
    }
    [Fact] public void TouchWriteFailureIsVisibleToDesktopAndRetainsBoundedPendingTraces()
    {
        var path=Path.GetTempFileName();
        try
        {
            var diagnostics=new MabiAgent.Core.RuntimeDiagnostics(path);using var ops=new TouchInputOps(State,diagnostics);TouchDiagnosticWriteFailure? failure=null;ops.DiagnosticWriteFailed+=value=>failure=value;
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;var runner=(ForwardedTouchGestureRunner)typeof(TouchInputOps).GetField("runner",flags)!.GetValue(ops)!;
            typeof(ForwardedTouchGestureRunner).GetField("gestureId",flags)!.SetValue(runner,"visible-gesture");var trace=typeof(ForwardedTouchGestureRunner).GetMethod("Trace",flags)!;var flush=typeof(ForwardedTouchGestureRunner).GetMethod("FlushTraces",flags)!;
            trace.Invoke(runner,["forwarded",new JsonObject{["submitted"]=false}]);flush.Invoke(runner,null);
            Assert.Equal("visible-gesture",failure!.GestureId);Assert.Equal("forwarded",failure.Stage);Assert.Equal(diagnostics.WriteError,failure.Error);
            var pending=(System.Collections.IList)typeof(ForwardedTouchGestureRunner).GetField("pendingTraces",flags)!.GetValue(runner)!;Assert.Single(pending);
            for(var i=0;i<300;i++)trace.Invoke(runner,["forwarded",new JsonObject{["index"]=i}]);flush.Invoke(runner,null);
            Assert.InRange(pending.Count,1,256);Assert.NotNull(diagnostics.WriteError);
        }
        finally{File.Delete(path);}
    }
    [Fact] public void BufferedTouchTraceRestoresOccurrenceTimeInsteadOfFlushTime()
    {
        var directory=Path.Combine(Path.GetTempPath(),"mabi-touch-trace-"+Guid.NewGuid().ToString("N"));
        try
        {
            var flushTime=new DateTimeOffset(2035,1,1,0,0,0,TimeSpan.Zero);var diagnostics=new MabiAgent.Core.RuntimeDiagnostics(directory,new FixedTime(flushTime));using var runner=new ForwardedTouchGestureRunner(diagnostics:diagnostics);
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;typeof(ForwardedTouchGestureRunner).GetField("gestureId",flags)!.SetValue(runner,"buffered");
            var before=DateTimeOffset.UtcNow;typeof(ForwardedTouchGestureRunner).GetMethod("Trace",flags)!.Invoke(runner,["forwarded",new JsonObject{["submitted"]=true}]);var after=DateTimeOffset.UtcNow;
            typeof(ForwardedTouchGestureRunner).GetMethod("FlushTraces",flags)!.Invoke(runner,null);
            var value=System.Text.Json.JsonSerializer.Deserialize<MabiAgent.Core.RuntimeFaultEvent>(Assert.Single(File.ReadAllLines(Path.Combine(directory,"runtime-events.jsonl"))))!;
            Assert.InRange(value.At,before,after);Assert.NotEqual(flushTime,value.At);Assert.Equal("buffered",value.TraceId);
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact] public void FailureTracePrecedesResourcesAndKeepsOriginalFailureTimeAndStage()
    {
        var directory=Path.Combine(Path.GetTempPath(),"mabi-touch-trace-"+Guid.NewGuid().ToString("N"));
        try
        {
            var diagnostics=new MabiAgent.Core.RuntimeDiagnostics(directory,new FixedTime(new(2035,1,1,0,0,0,TimeSpan.Zero)));using var runner=new ForwardedTouchGestureRunner(diagnostics:diagnostics);
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;var failure=typeof(ForwardedTouchGestureRunner).GetMethod("TraceFailure",flags)!;var trace=typeof(ForwardedTouchGestureRunner).GetMethod("Trace",flags)!;
            var before=DateTimeOffset.UtcNow;failure.Invoke(runner,[new InvalidOperationException("boom"),"generate"]);var after=DateTimeOffset.UtcNow;trace.Invoke(runner,["resources",new JsonObject()]);typeof(ForwardedTouchGestureRunner).GetMethod("FlushTraces",flags)!.Invoke(runner,null);
            var rows=File.ReadAllLines(Path.Combine(directory,"runtime-events.jsonl")).Select(line=>System.Text.Json.JsonSerializer.Deserialize<MabiAgent.Core.RuntimeFaultEvent>(line)!).ToArray();
            Assert.Equal(["failed","resources"],rows.Select(row=>row.Stage).ToArray());Assert.InRange(rows[0].At,before,after);Assert.Contains("\"failed_stage\":\"generate\"",rows[0].Message,StringComparison.Ordinal);
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact] public void FailedTouchForwardClosesHandleAndCannotReplay()
    {
        var closed=new List<nint>();using var envelope=new TouchEnvelope(1,123,new(),(_,_,_,_)=>false,closed.Add);JsonObject? trace=null;
        Assert.Throws<InputOutcomeUnknownException>(()=>envelope.Forward(99,row=>trace=row));
        Assert.Equal([123],closed);Assert.False(trace!["submitted"]!.GetValue<bool>());
        Assert.Throws<InputBlockedException>(()=>envelope.Forward(99));envelope.Dispose();Assert.Equal([123],closed);
    }
    [Fact] public void SuccessfulTouchForwardTransfersOwnershipWithoutClosing()
    {
        var closed=new List<nint>();using var envelope=new TouchEnvelope(1,123,new(),(_,_,_,_)=>true,closed.Add);
        var trace=envelope.Forward(99);Assert.True(trace["submitted"]!.GetValue<bool>());Assert.Empty(closed);
        envelope.Dispose();Assert.Empty(closed);Assert.Throws<InputBlockedException>(()=>envelope.Forward(99));
    }
    [Fact] public void ThreadCleanupRetriesCloseAndCompletesOnce()
    {
        using var gate=new ManualResetEventSlim(false);var thread=new Thread(()=>gate.Wait()){IsBackground=true};thread.Start();var closes=0;var completions=0;
        try
        {
            TouchThreadCleanup.Run(thread,()=>{closes++;if(closes==2)gate.Set();},()=>completions++,"test",10);
            Assert.Equal(2,closes);Assert.Equal(1,completions);
        }
        finally{gate.Set();Assert.True(thread.Join(1000));}
    }
    [Fact] public void ThreadCleanupTimeoutIsBoundedAndDoesNotComplete()
    {
        using var gate=new ManualResetEventSlim(false);var thread=new Thread(()=>gate.Wait()){IsBackground=true};thread.Start();var closes=0;var completions=0;
        try
        {
            Assert.Throws<TimeoutException>(()=>TouchThreadCleanup.Run(thread,()=>closes++,()=>completions++,"test",1));
            Assert.Equal(2,closes);Assert.Equal(0,completions);
        }
        finally{gate.Set();Assert.True(thread.Join(1000));}
    }
    [Theory][InlineData("storage.quantity")][InlineData("bag.sale_quantity")]
    public void QuantityEditingUsesKeysOnlyAndSearchTouchIsRejected(string field)
    {
        var runner=new Runner();var passive=new Passive();using var ops=new TouchInputOps(State,passive,passive,runner);var scope=new SessionScope("test",null,null);
        using var guard=new ForegroundInputGuard(State,scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,scope,new("storage_transfer",false,[],new Dictionary<string,PixelPoint>{{field,new(50,50)},{"bag.search_open",new(20,20)}},[]),true,true),maxFrameAgeSeconds:5,inputOptions:new("touch"));guard.Start();
        guard.EditQuantity(guard.ObserveTarget(field),"107");Assert.Equal(0,runner.Calls);Assert.True(passive.Keys>0);Assert.Equal(0,passive.MouseWrites);
        Assert.Throws<MabiAgent.Workflows.InputRejectedBeforeDispatchException>(()=>guard.TypeText(guard.ObserveTarget(field),"107"));
        Assert.Throws<MabiAgent.Workflows.InputRejectedBeforeDispatchException>(()=>guard.ClickReviewedNavigation(guard.ObserveTarget("bag.search_open"),"storage_transfer"));Assert.False(guard.Paused);Assert.Equal(0,runner.Calls);
    }
    [Fact] public void RejectedTouchPreparationDoesNotPauseOrClaimDelivery()
    {
        var runner=new Runner{OnStart=()=>throw new MabiAgent.Workflows.InputRejectedBeforeDispatchException("not ready")};var passive=new Passive();using var ops=new TouchInputOps(State,passive,passive,runner);using var guard=Guard(ops);guard.Start();
        Assert.Throws<MabiAgent.Workflows.InputRejectedBeforeDispatchException>(()=>guard.Click(guard.ObserveTarget("scene.open_bag")));Assert.False(guard.Paused);Assert.Equal(0,runner.Deliveries);
    }
    [Fact] public void BoundedMapZoomFinishesAfterItsSourceAgesButRejectsAnExpiredEntry()
    {
        var runner=new Runner();var passive=new Passive();using var ops=new TouchInputOps(State,passive,passive,runner);var scope=new SessionScope("test",null,null);double delay=0;
        using var guard=new ForegroundInputGuard(State,scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,scope,new("region_map",false,[],new Dictionary<string,PixelPoint>{{"map.zoom",new(50,50)}},[],TargetRegions:new Dictionary<string,PixelBox>{{"map.zoom",new(10,10,90,90)}}),true,true),()=>System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency+delay,maxFrameAgeSeconds:5,inputOptions:new("touch"));
        guard.Start();var proof=guard.ObserveTarget("map.zoom");runner.OnStart=()=>delay+=6;
        guard.Scroll(proof,12,zoom:true);Assert.Equal(3,runner.Deliveries);Assert.False(guard.Paused);
        Assert.Throws<InputBlockedException>(()=>guard.Scroll(proof,12,zoom:true));Assert.Equal(3,runner.Deliveries);
    }
    [Theory][InlineData("before")][InlineData("after")][InlineData("pause")][InlineData("resources")]
    public void PreparedGestureChecksSourceBeforeDeliveryAndSystemBoundariesUntilRelease(string change)
    {
        var runner=new Runner();var passive=new Passive();using var ops=new TouchInputOps(State,passive,passive,runner);
        var scope=new SessionScope("test","account","character");double delay=0;var revision="a";
        using var guard=new ForegroundInputGuard(State,scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,scope,
            new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(50,50)}},[]),true,true),
            ()=>System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency+delay,
            maxFrameAgeSeconds:5,resourceRevision:()=>revision,inputOptions:new("touch"));
        guard.Start();var proof=guard.ObserveTarget("scene.open_bag");var authority=new TestAuthority();
        if(change=="before")runner.OnPrepared=()=>delay+=6;
        else runner.OnDelivered=()=>
        {
            delay+=6;
            if(change=="pause")guard.RequestPause();
            if(change=="resources")revision="b";
        };
        void Send()=>guard.Consume(proof,"bounded",authority);
        if(change=="after"){Send();Assert.False(guard.Paused);}
        else if(change=="before"){Assert.Throws<MabiAgent.Workflows.InputRejectedBeforeDispatchException>(Send);Assert.Equal(0,authority.Calls);Assert.Equal(0,runner.Deliveries);return;}
        else Assert.Throws<InputBlockedException>(Send);
        Assert.Equal(1,authority.Calls);Assert.Equal(1,runner.Deliveries);Assert.Equal(1,runner.Releases);
        Assert.Throws<InputBlockedException>(()=>guard.Click(proof));Assert.Equal(1,runner.Deliveries);
    }
    [Fact] public void ExplicitCrossProcessTouchReceiver()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_RECEIVER_FILE") is not {} path)return;
        var origin=Environment.GetEnvironmentVariable("MABI_TOUCH_RECEIVER_ORIGIN")?.Split(',').Select(int.Parse).ToArray()??[500,300];
        using var receiver=new TouchInputWindow(new(origin[0],origin[1],origin[0]+400,origin[1]+240),legacyTouch:true);
        File.WriteAllText(path,new JsonObject{["hwnd"]=(long)receiver.Hwnd,["pid"]=Environment.ProcessId}.ToJsonString());
        Assert.True(SpinWait.SpinUntil(()=>receiver.Received.Any(m=>m["contacts"] is JsonArray contacts&&contacts.Any(c=>(c!["flags"]!.GetValue<uint>()&4)!=0)),15000),"跨进程接收窗未解码到释放帧");
        Assert.True(SpinWait.SpinUntil(()=>File.Exists(path+".done"),10000),"发送方未完成触控资源释放");
        var contacts=receiver.Received.Where(m=>m["contacts"] is JsonArray).SelectMany(m=>m["contacts"]!.AsArray()).ToArray();
        Assert.Contains(contacts,c=>(c!["flags"]!.GetValue<uint>()&2)!=0);
        File.WriteAllText(path,new JsonObject{["decoded"]=true,["contacts"]=new JsonArray(contacts.Select(c=>c!.DeepClone()).ToArray())}.ToJsonString());
    }
    [Fact] public void ExplicitCrossProcessTouchDelivery()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_CROSS_PROCESS")!="1")return;
        var path=Path.GetTempFileName();
        var start=new System.Diagnostics.ProcessStartInfo("dotnet"){WorkingDirectory=RecognitionTests.Root(),UseShellExecute=false,CreateNoWindow=true};
        var configuration=typeof(TouchTransportTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute),false).Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
        foreach(var arg in new[]{"test","dotnet/MabiAgent.Windows.Tests","--configuration",configuration,"--no-build","--no-restore","--filter","FullyQualifiedName~ExplicitCrossProcessTouchReceiver","--verbosity","quiet"})start.ArgumentList.Add(arg);
        start.Environment["MABI_TOUCH_RECEIVER_FILE"]=path;
        using var child=System.Diagnostics.Process.Start(start)!;
        try
        {
            JsonObject? ready=null;Assert.True(SpinWait.SpinUntil(()=>{try{ready=JsonNode.Parse(File.ReadAllText(path))?.AsObject();return ready?["hwnd"] is not null;}catch{return false;}},25000));
            var hwnd=(nint)ready!["hwnd"]!.GetValue<long>();var native=new NativeWindowsOps();var state=native.ReadWindow(hwnd);
            Assert.NotEqual(Environment.ProcessId,state.ProcessId);Assert.Equal(ready["pid"]!.GetValue<int>(),state.ProcessId);
            using var mouse=new TouchMouseMonitor();
            var reportRoot=Environment.GetEnvironmentVariable("MABI_TOUCH_DIAGNOSTIC_ROOT");
            using var runner=new ForwardedTouchGestureRunner(diagnostics:reportRoot is null?null:new MabiAgent.Core.RuntimeDiagnostics(reportRoot));var before=TouchCursorPresentation.Read();
            var kind=Environment.GetEnvironmentVariable("MABI_TOUCH_TEST_GESTURE")??"click";
            var center=new PixelPoint(state.Geometry.X+200,state.Geometry.Y+120);
            var plan=kind=="click"?new TouchGesturePlan("click",[[center]]):TouchGesturePlan.Create(kind,center);
            runner.Execute(plan,hwnd,40,()=>Assert.Equal(state.ProcessId,native.ReadWindow(hwnd).ProcessId),()=>{},()=>native.ReadWindow(hwnd));
            File.WriteAllText(path+".done","released");
            Assert.True(child.WaitForExit(20000));Assert.Equal(0,child.ExitCode);
            Assert.True(JsonNode.Parse(File.ReadAllText(path))!["decoded"]!.GetValue<bool>());
            var received=JsonNode.Parse(File.ReadAllText(path))!["contacts"]!.AsArray();
            foreach(var point in plan.Points[0])Assert.Contains(received,c=>(c!["flags"]!.GetValue<uint>()&7)==2&&c["screen_hundredths"]![0]!.GetValue<int>()==point.X*100&&c["screen_hundredths"]![1]!.GetValue<int>()==point.Y*100);
            foreach(var point in plan.Points[^1])Assert.Contains(received,c=>(c!["flags"]!.GetValue<uint>()&7)==4&&c["screen_hundredths"]![0]!.GetValue<int>()==point.X*100&&c["screen_hundredths"]![1]!.GetValue<int>()==point.Y*100);
            var after=TouchCursorPresentation.Read();var physicalMoves=mouse.PhysicalMoves;
            var observation=new JsonObject{["before"]=before.ToJson(),["after"]=after.ToJson(),["physical_moves"]=physicalMoves,["physical_downs"]=mouse.PhysicalDowns,["events"]=new JsonArray(mouse.Events.Select(e=>(JsonNode)e.ToJson()).ToArray())};
            if(reportRoot is not null)File.WriteAllText(Path.Combine(reportRoot,"mouse-observation.json"),observation.ToJsonString());
            Assert.Equal(before.Foreground,after.Foreground);
            // Hardware movement is allowed throughout the test. Coordinate equality
            // is meaningful only if the physical mouse stayed still.
            if(physicalMoves==0)Assert.True(before.Position==after.Position,observation.ToJsonString());
        }
        finally{if(!child.HasExited)child.Kill(entireProcessTree:true);File.Delete(path);File.Delete(path+".done");}
    }
    [Theory][InlineData(true,false)][InlineData(false,false)][InlineData(true,true)]
    public void PreparedTouchRechecksNewFrameBeforeAnyGameDelivery(bool stillVisible,bool slow)
    {
        var runner=new Runner();var passive=new Passive();using var ops=new TouchInputOps(State,passive,passive,runner);var scope=new SessionScope("test","account","character");
        double delay=0;var prepared=false;var reviews=0;
        using var guard=new ForegroundInputGuard(State,scope,ops,c=>{reviews++;return new(c.Image.Sha256,c.CapturedAt,scope,new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(50,50)}},[]),true,true,Frame:c.Frame,ResourceRevision:c.ResourceRevision);},()=>System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency+delay,maxFrameAgeSeconds:5,inputOptions:new("touch"));
        guard.Start();runner.OnPrepared=()=>prepared=true;var proof=guard.ObserveCurrentTarget("scene.open_bag",guard.ObserveFrame());var checks=0;var authority=new TestAuthority();
        void Send()=>guard.Consume(proof,"intent",authority,beforeDelivery:c=>{checks++;Assert.True(prepared);if(slow)delay+=.3;return stillVisible;});
        if(stillVisible&&!slow){Send();Assert.Equal(1,runner.Deliveries);}
        else{Assert.Throws<MabiAgent.Workflows.InputRejectedBeforeDispatchException>(Send);Assert.Equal(0,runner.Deliveries);Assert.False(guard.Paused);}
        Assert.Equal(1,checks);Assert.Equal(1,reviews);Assert.Equal(0,passive.MouseWrites);Assert.Equal(1,runner.Releases);Assert.Equal(stillVisible&&!slow?1:0,authority.Calls);
    }
    [Theory][InlineData(true,false)][InlineData(false,false)][InlineData(true,true)]
    public void FishingShortcutPersistsBeforeMovementAndDoesNotPrepareTouch(bool authorized,bool pauseAfterMovement)
    {
        var events=new List<string>();var held=new HashSet<ushort>();var runner=new Runner();var passive=new Passive();
        using var ops=new TouchInputOps(State,passive,passive,runner);var scope=new SessionScope("test","account","character");
        using var guard=new ForegroundInputGuard(State,scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,scope,new("scene",false,[],new Dictionary<string,PixelPoint>{{"fishing.start",new(50,50)}},[]),true,true,Frame:c.Frame,ResourceRevision:c.ResourceRevision),maxFrameAgeSeconds:5,inputOptions:new("touch"));
        guard.Start();var proof=guard.ObserveCurrentTarget("fishing.start",guard.ObserveFrame());
        passive.OnKey=(key,down)=>
        {
            Assert.Contains(key,new ushort[]{87,32});
            if(key==32)Assert.DoesNotContain((ushort)87,held);
            events.Add($"{key}_{(down?"down":"up")}");if(down)held.Add(key);else held.Remove(key);
            if(key==87&&!down&&pauseAfterMovement)guard.RequestPause();
        };
        passive.OnPointerPreparation=()=>throw new InvalidOperationException("抛竿不准备触控");
        var authority=new TestAuthority(authorized,()=>events.Add("durable"));
        void Send()=>guard.Consume(proof,"cast-once",authority,key:32,movementKey:87);
        if(authorized&&pauseAfterMovement)
        {
            Assert.Throws<InputOutcomeUnknownException>(Send);
            Assert.Equal(new[]{"durable","87_down","87_up"},events);
            Assert.True(guard.Paused);Assert.Equal(0,runner.Deliveries);Assert.Equal(2,passive.Keys);
            guard.Resume();proof=guard.ObserveCurrentTarget("fishing.start",guard.ObserveFrame());
            Assert.Throws<MabiAgent.Workflows.InputRejectedBeforeDispatchException>(Send);
            Assert.Equal(0,runner.Calls);Assert.Equal(2,passive.Keys);Assert.Equal(1,authority.Calls);
        }
        else if(authorized)
        {
            Send();Assert.Equal(new[]{"durable","87_down","87_up","32_down","32_up"},events);
            Assert.Equal(0,runner.Deliveries);Assert.Equal(4,passive.Keys);
            Assert.Throws<MabiAgent.Workflows.InputRejectedBeforeDispatchException>(Send);
            Assert.Equal(0,runner.Calls);Assert.Equal(4,passive.Keys);Assert.Equal(1,authority.Calls);
        }
        else
        {
            Assert.Throws<MabiAgent.Workflows.InputRejectedBeforeDispatchException>(Send);
            Assert.Equal(new[]{"durable"},events);Assert.Equal(0,passive.Keys);Assert.Equal(0,runner.Deliveries);
        }
        Assert.False(guard.Paused);Assert.Empty(held);Assert.Equal(0,passive.MouseWrites);
    }
    private sealed class TestAuthority(bool authorized=true,Action? onVerify=null):IConsumptionAuthorization
    {public int Calls;public bool VerifyPersistedIntent(string id,TargetProof target,UiObservation review){Calls++;onVerify?.Invoke();return authorized;}}
    [Fact] public void CoalescedMotionKeepsCompleteBoundariesAndRejectsWrongCoordinatesOrMissingRelease()
    {
        var plan=new TouchGesturePlan("drag",[[new(10,10)],[new(20,20)],[new(30,30)]]);
        TouchEnvelope Frame(uint flags,int x,int y)=>new(1,0,new(){["contacts"]=new JsonArray(new JsonObject{["id"]=1u,["flags"]=flags,["screen_hundredths"]=new JsonArray(x*100,y*100)})});
        var down=Frame(2,10,10);var last=Frame(1,30,30);var up=Frame(4,30,30);
        plan.Validate([down,last,up]);plan.Validate([down,up]);
        Assert.Throws<InputBlockedException>(()=>plan.Validate([down,last]));
        Assert.Throws<InputBlockedException>(()=>plan.Validate([down,Frame(1,35,30),up]));
        Assert.Throws<InputBlockedException>(()=>plan.Validate([down,last,Frame(1,20,20),up]));
        Assert.Throws<InputBlockedException>(()=>plan.Validate([down,Frame(4,10,10)]));
        Assert.Throws<InputBlockedException>(()=>plan.Validate([down,Frame(1,20,20),last,last,up]));
        Assert.Throws<InputBlockedException>(()=>plan.Validate([down,last,Frame(1,35,30),last,up]));
    }
    [Fact] public void NativeTouchAbiMatchesWindowsX64()
    {
        Assert.Equal(8,IntPtr.Size);
        Assert.Equal(96,System.Runtime.InteropServices.Marshal.SizeOf<TouchNative.PointerInfo>());
        Assert.Equal(144,System.Runtime.InteropServices.Marshal.SizeOf<TouchNative.TouchInfo>());
        Assert.Equal(152,System.Runtime.InteropServices.Marshal.SizeOf<TouchNative.TypeInfo>());
    }
    [Fact] public void FactoryDefaultsToTouchAndRejectsUnknownSchemes()
    {Assert.IsType<TouchInputOps>(InputTransportFactory.Create(State));Assert.IsType<NativeWindowsOps>(InputTransportFactory.Create(State,new("mouse")));Assert.Throws<ArgumentException>(()=>InputTransportFactory.Create(State,new("unsupported")));Assert.Equal(new InputOptions(),InputOptions.FromJson(new JsonObject()));}
    [Theory][InlineData(false)][InlineData(true)]
    public void GlobalCenterSettingCoversEveryClickAndCannotBeDisabledByTask(bool enabled)
    {
        var runner=new Runner();var passive=new Passive();using var ops=new TouchInputOps(State,passive,passive,runner);var scope=new SessionScope("test",null,null);
        using var guard=new ForegroundInputGuard(State,scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,scope,new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(50,50)}},[],TargetRegions:new Dictionary<string,PixelBox>{{"scene.open_bag",new(30,30,70,70)}}),true,true),maxFrameAgeSeconds:5,inputOptions:new("touch",enabled));
        guard.Start();var points=new List<PixelPoint>();for(var i=0;i<16;i++){guard.Click(guard.ObserveTarget("scene.open_bag",randomize:false));points.Add(runner.Plan!.Points[0][0]);}
        Assert.All(points,p=>{Assert.InRange(p.X,47,53);Assert.InRange(p.Y,47,53);});if(!enabled)Assert.All(points,p=>Assert.Equal(new(50,50),p));else Assert.True(points.Distinct().Count()>1);
        Assert.False(guard.RequiresForeground);Assert.Equal(16,runner.Releases);
    }
    [Theory][InlineData(MouseButton.Left,40)][InlineData(MouseButton.Right,2000)]
    public void ClickAndRightLongPressUseTheSameGuard(MouseButton button,int hold)
    {var runner=new Runner();var passive=new Passive();using var ops=new TouchInputOps(State,passive,passive,runner);using var guard=Guard(ops);guard.Start();guard.Click(guard.ObserveTarget("scene.open_bag"),button);Assert.Equal(hold,runner.Hold);Assert.Equal("click",runner.Plan!.Kind);}
    [Fact] public void DragScrollPinchAndTextUseOneChannel()
    {
        var runner=new Runner();var passive=new Passive();using var ops=new TouchInputOps(State,passive,passive,runner);using var guard=Guard(ops);guard.Start();
        guard.Drag(guard.ObserveTarget("scene.open_bag"),new(70,60));Assert.Equal("drag",runner.Plan!.Kind);
        var drag=runner.Plan;Assert.Equal(new PixelPoint(70,60),drag.Points[^1][0]);Assert.Equal(160,drag.ReleaseDelayMilliseconds);
        Assert.True(drag.Points[1][0].X-drag.Points[0][0].X>drag.Points[^1][0].X-drag.Points[^2][0].X);
        guard.Scroll(guard.ObserveTarget("scene.open_bag"),-2);Assert.Equal("drag",runner.Plan!.Kind);Assert.True(runner.Plan.Points[0][0].Y>runner.Plan.Points[^1][0].Y);
        guard.Scroll(guard.ObserveTarget("scene.open_bag"),4,zoom:true);Assert.Equal(2,runner.Plan!.ContactCount);Assert.True(runner.Plan.Points[0][1].X-runner.Plan.Points[0][0].X<runner.Plan.Points[^1][1].X-runner.Plan.Points[^1][0].X);
        guard.TypeText(guard.ObserveTarget("scene.open_bag"),"鐵礦石",focus:false);guard.PressKey(guard.ObserveTarget("scene.open_bag"),87,TimeSpan.FromMilliseconds(10));Assert.Equal(8,passive.Keys);
    }
    [Fact] public void FailedGesturePausesAndDoesNotSwitchOrReplay()
    {var runner=new Runner{Fail=true};var passive=new Passive();using var ops=new TouchInputOps(State,passive,passive,runner);using var guard=Guard(ops);guard.Start();Assert.Throws<InputOutcomeUnknownException>(()=>guard.Click(guard.ObserveTarget("scene.open_bag")));Assert.True(guard.Paused);Assert.Equal(1,runner.Calls);Assert.Equal(1,runner.Releases);Assert.Equal(0,passive.MouseWrites);}
    [Theory][InlineData(-3)][InlineData(3)]
    public void ListScrollDeceleratesAndHoldsBeforeReleaseWithoutChangingMapGestures(int direction)
    {
        var runner=new Runner();var passive=new Passive();using var ops=new TouchInputOps(State,passive,passive,runner);using var guard=Guard(ops);guard.Start();
        guard.Scroll(guard.ObserveTarget("scene.open_bag"),direction);
        var plan=runner.Plan!;Assert.Equal(24,plan.Steps);Assert.Equal(160,plan.ReleaseDelayMilliseconds);
        Assert.Equal(Math.Sign(direction),Math.Sign(plan.Points[^1][0].Y-plan.Points[0][0].Y));
        Assert.True(Math.Abs(plan.Points[1][0].Y-plan.Points[0][0].Y)>Math.Abs(plan.Points[^1][0].Y-plan.Points[^2][0].Y));
        Assert.Equal(plan.ReleaseDelayMilliseconds,plan.Screen(new(100,100,100,100,96)).ReleaseDelayMilliseconds);
        Assert.All(plan.Points,f=>Assert.True(new PixelBox(10,10,90,90).Contains(f[0])));
        guard.Scroll(guard.ObserveTarget("scene.open_bag"),4,zoom:true);Assert.Equal(6,runner.Plan!.Steps);Assert.Equal(0,runner.Plan.ReleaseDelayMilliseconds);
    }
    [Theory][InlineData(-12,1280,960)][InlineData(-3,1280,960)][InlineData(3,1280,960)][InlineData(12,1280,960)][InlineData(-3,1920,1440)][InlineData(3,1920,1440)]
    public void LargeListScrollAdvancesSeveralRowsWithOverlap(int steps,int width,int height)
    {
        var state=new NativeWindowState(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,width,height,96),new(0,0,width,height),99,true,false,true,new(0,0));
        var surface=new PixelBox(800*width/1280,275*height/960,1190*width/1280,885*height/960);var rowHeight=96*height/960;
        var runner=new Runner();var passive=new Passive(state);using var ops=new TouchInputOps(state,passive,passive,runner);var scope=new SessionScope("list-scroll",null,null);
        using var guard=new ForegroundInputGuard(state,scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,scope,new("herb_nodes",false,[],new Dictionary<string,PixelPoint>{{"list.scroll",surface.Center}},[],TargetRegions:new Dictionary<string,PixelBox>{{"list.scroll",surface}}),true,true),maxFrameAgeSeconds:5,inputOptions:new("touch"));
        guard.Start();guard.Scroll(guard.ObserveTarget("list.scroll"),steps);
        var plan=runner.Plan!;var distance=Math.Abs(plan.Points[^1][0].Y-plan.Points[0][0].Y);
        Assert.Equal(1,runner.Deliveries);Assert.Equal(Math.Sign(steps),Math.Sign(plan.Points[^1][0].Y-plan.Points[0][0].Y));
        Assert.InRange(distance,rowHeight*3,surface.Height-rowHeight*2);Assert.All(plan.Points,frame=>Assert.True(surface.Contains(frame[0])));
        Assert.Equal(160,plan.ReleaseDelayMilliseconds);
    }
    [Theory][InlineData(ClickRegionShape.Rectangle)][InlineData(ClickRegionShape.Ellipse)]
    public void CenterOffsetStaysAwayFromEdgesIncludingTinyTargets(ClickRegionShape shape)
    {foreach(var box in new[]{new PixelBox(0,0,1,1),new PixelBox(100,20,110,28),new PixelBox(0,0,200,200)})for(var i=0;i<100;i++){var p=box.CenterOffsetPoint(shape:shape);Assert.True(box.Contains(p,shape));Assert.InRange(Math.Abs(p.X-box.Center.X),0,3);Assert.InRange(Math.Abs(p.Y-box.Center.Y),0,3);}}
    [Theory][InlineData("click",450,250,false)][InlineData("click",450,250,true)][InlineData("drag",450,250,false)][InlineData("drag",450,250,true)][InlineData("pinch",450,250,false)][InlineData("pinch",450,250,true)][InlineData("click",650,450,false)][InlineData("click",650,450,true)][InlineData("drag",650,450,false)][InlineData("drag",650,450,true)][InlineData("pinch",650,450,false)][InlineData("pinch",650,450,true)]
    public void ForwardingIsIdenticalForReportedForegroundAndBackground(string kind,int x,int y,bool foreground)
    {
        if(!OperatingSystem.IsWindows())return;
        var box=new PixelBox(x-250,y-150,x+250,y+150);using var receiver=new TouchInputWindow(box,legacyTouch:true);using var runner=new ForwardedTouchGestureRunner();var before=TouchCursorPresentation.Read();
        var state=new NativeWindowsOps().ReadWindow(receiver.Hwnd);if(foreground)state=state with{ForegroundId=receiver.Hwnd};var plan=kind=="click"?new TouchGesturePlan("click",[[box.Center]]):TouchGesturePlan.Create(kind,box.Center);
        try{runner.Execute(plan,receiver.Hwnd,kind=="click"?40:0,()=>{},()=>{},()=>state);}
        catch(Exception error){throw new Exception(error.Message+"，目标消息 "+new JsonArray(receiver.Received.Take(12).Select(row=>(JsonNode)row.DeepClone()).ToArray()).ToJsonString(),error);}
        Assert.True(SpinWait.SpinUntil(()=>receiver.Received.Any(j=>j["contacts"] is JsonArray),1000));
        var contacts=receiver.Received.Where(j=>j["contacts"] is JsonArray).SelectMany(j=>j["contacts"]!.AsArray()).ToArray();Assert.Contains(contacts,c=>(c!["flags"]!.GetValue<uint>()&2)!=0);Assert.Contains(contacts,c=>(c!["flags"]!.GetValue<uint>()&4)!=0);
        Assert.DoesNotContain(receiver.Received,j=>j["message"]?.GetValue<uint>() is >=0x245 and <=0x24f);
        Assert.Equal(before.Foreground,TouchCursorPresentation.Read().Foreground);
    }
    [Fact] public void NativeSourceRejectsForeignWindowBeforeContactAndKeepsDeviceReleasable()
    {
        if(!OperatingSystem.IsWindows())return;
        Assert.Throws<InputBlockedException>(()=>new SyntheticTouchDevice(default,0));
        using var helper=new TouchInputWindow(new(20,20,44,44));
        using var device=new SyntheticTouchDevice(helper.Point,helper.Hwnd);
        Assert.Throws<InputBlockedException>(()=>device.Down(new PixelPoint(650,450),0));
        Assert.Empty(device.Frames);device.Dispose();Assert.True(device.Released);
    }
    [Fact] public void PauseDuringGestureReleasesOnceAndManualPauseNeedsExplicitResume()
    {
        var runner=new Runner();var passive=new Passive();using var ops=new TouchInputOps(State,passive,passive,runner);using var guard=Guard(ops);guard.Start();runner.OnStart=guard.RequestPause;
        Assert.Throws<InputBlockedException>(()=>guard.Click(guard.ObserveTarget("scene.open_bag")));Assert.True(guard.Paused);Assert.Equal(1,runner.Releases);Assert.Equal(1,runner.Calls);
        guard.Pause();Assert.Throws<InputBlockedException>(()=>guard.ObserveTarget("scene.open_bag"));guard.Resume();runner.OnStart=null;guard.Click(guard.ObserveTarget("scene.open_bag"));Assert.Equal(2,runner.Calls);
    }
    private static NativeWindowState State=>new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,100,100,96),new(0,0,100,100),99,true,false,true,new(0,0));
    private static ForegroundInputGuard Guard(INativeWindowsOps ops){var scope=new SessionScope("test",null,null);return new(State,scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,scope,new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(50,50)}},[],TargetRegions:new Dictionary<string,PixelBox>{{"scene.open_bag",new(10,10,90,90)}}),true,true),maxFrameAgeSeconds:5,inputOptions:new("touch"));}
    private sealed class FixedTime(DateTimeOffset value):TimeProvider{public override DateTimeOffset GetUtcNow()=>value;}
    private sealed class Runner:ITouchGestureRunner
    {public TouchGesturePlan? Plan;public int Hold,Calls,Releases;public bool Fail;public int Deliveries;public Action? OnStart,OnPrepared,OnDelivered;private Action? beforeDelivery;public bool SupportsPreparedReview=>true;public void SetBeforeDelivery(Action check)=>beforeDelivery=check;public void Execute(TouchGesturePlan p,nint target,int hold,Action check,Action release,Func<NativeWindowState> verify){Calls++;Plan=p;Hold=hold;OnStart?.Invoke();check();OnPrepared?.Invoke();beforeDelivery?.Invoke();Deliveries++;if(Fail)throw new InputOutcomeUnknownException("test");if(OnDelivered is not null){OnDelivered();check();release();}}public void Release(Func<NativeWindowState> verify){verify();Releases++;}public void Dispose(){}}
    private sealed class Passive(NativeWindowState? window=null):INativeWindowsOps,ICurrentClientFrameSource,IKeyboardPointerPreparation
    {private long sequence;public int Keys,MouseWrites;public Action<ushort,bool>? OnKey;public Action? OnPointerPreparation;public void PreparePointerInput()=>OnPointerPreparation?.Invoke();public NativeWindowState ReadWindow(nint hwnd)=>window??State;public RgbImage CaptureClient(NativeWindowState state)=>new(state.Geometry.Width,state.Geometry.Height,new byte[state.Geometry.Width*state.Geometry.Height*3]);public CurrentClientFrame CaptureCurrentClientFrame(NativeWindowState state)=>new(CaptureClient(state),new("test",1,++sequence,System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency));public void MoveClient(nint hwnd,PixelPoint p)=>MouseWrites++;public void MouseButton(MouseButton b,bool down)=>MouseWrites++;public void Wheel(int delta)=>MouseWrites++;public void Key(ushort key,bool down,bool unicode=false){Keys++;OnKey?.Invoke(key,down);}public bool AnyPhysicalKeyDown()=>false;}
}
