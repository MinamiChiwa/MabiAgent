using System.Text.Json.Nodes;
using Xunit;
namespace MabiAgent.Windows.Tests;

[Collection("Native process metadata")]
public sealed class TouchTransportTests
{
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
    [Fact] public void ExplicitCrossProcessTouchReceiver()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_RECEIVER_FILE") is not {} path)return;
        using var receiver=new TouchInputWindow(new(500,300,660,420),legacyTouch:true);
        File.WriteAllText(path,new JsonObject{["hwnd"]=(long)receiver.Hwnd,["pid"]=Environment.ProcessId}.ToJsonString());
        Assert.True(SpinWait.SpinUntil(()=>receiver.Received.Any(m=>m["contacts"] is JsonArray contacts&&contacts.Any(c=>(c!["flags"]!.GetValue<uint>()&4)!=0)),15000),"跨进程接收窗未解码到释放帧");
        var contacts=receiver.Received.Where(m=>m["contacts"] is JsonArray).SelectMany(m=>m["contacts"]!.AsArray()).ToArray();
        Assert.Contains(contacts,c=>(c!["flags"]!.GetValue<uint>()&2)!=0);
        File.WriteAllText(path,new JsonObject{["decoded"]=true,["contacts"]=new JsonArray(contacts.Select(c=>c!.DeepClone()).ToArray())}.ToJsonString());
    }
    [Fact] public void ExplicitCrossProcessTouchDelivery()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_CROSS_PROCESS")!="1")return;
        var path=Path.GetTempFileName();
        var start=new System.Diagnostics.ProcessStartInfo("dotnet"){WorkingDirectory=RecognitionTests.Root(),UseShellExecute=false,CreateNoWindow=true};
        foreach(var arg in new[]{"test","dotnet/MabiAgent.Windows.Tests","--no-build","--no-restore","--filter","FullyQualifiedName~ExplicitCrossProcessTouchReceiver","--verbosity","quiet"})start.ArgumentList.Add(arg);
        start.Environment["MABI_TOUCH_RECEIVER_FILE"]=path;
        using var child=System.Diagnostics.Process.Start(start)!;
        try
        {
            JsonObject? ready=null;Assert.True(SpinWait.SpinUntil(()=>{try{ready=JsonNode.Parse(File.ReadAllText(path))?.AsObject();return ready?["hwnd"] is not null;}catch{return false;}},25000));
            var hwnd=(nint)ready!["hwnd"]!.GetValue<long>();var native=new NativeWindowsOps();var state=native.ReadWindow(hwnd);
            Assert.NotEqual(Environment.ProcessId,state.ProcessId);Assert.Equal(ready["pid"]!.GetValue<int>(),state.ProcessId);
            using var runner=new NativeTouchGestureRunner(nativeRouting:false);var before=TouchCursorPresentation.Read();
            runner.Execute(new("click",[[new(state.Geometry.X+80,state.Geometry.Y+60)]]),hwnd,40,()=>Assert.Equal(state.ProcessId,native.ReadWindow(hwnd).ProcessId),()=>{},()=>native.ReadWindow(hwnd));
            Assert.True(child.WaitForExit(20000));Assert.Equal(0,child.ExitCode);
            Assert.True(JsonNode.Parse(File.ReadAllText(path))!["decoded"]!.GetValue<bool>());
            Assert.Equal(before.Foreground,TouchNative.Foreground);Assert.Equal(before.Position,TouchCursorPresentation.Read().Position);
        }
        finally{if(!child.HasExited)child.Kill(entireProcessTree:true);File.Delete(path);}
    }
    [Theory][InlineData(true,false)][InlineData(false,false)][InlineData(true,true)]
    public void PreparedTouchRechecksNewFrameBeforeAnyGameDelivery(bool stillVisible,bool slow)
    {
        var runner=new Runner();var passive=new Passive();using var ops=new TouchInputOps(State,passive,passive,runner);var scope=new SessionScope("test","account","character");
        double delay=0;var prepared=false;var reviews=0;
        using var guard=new ForegroundInputGuard(State,scope,ops,c=>{reviews++;return new(c.Image.Sha256,c.CapturedAt,scope,new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(50,50)}},[]),true,true,Frame:c.Frame,ResourceRevision:c.ResourceRevision);},()=>System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency+delay,maxFrameAgeSeconds:5,inputOptions:new("touch"));
        guard.Start();runner.OnPrepared=()=>prepared=true;var proof=guard.ObserveCurrentTarget("scene.open_bag",guard.ObserveFrame());var checks=0;
        void Send()=>guard.Consume(proof,"intent",new TestAuthority(),beforeDelivery:c=>{checks++;Assert.True(prepared);if(slow)delay+=.3;return stillVisible;});
        if(stillVisible&&!slow){Send();Assert.Equal(1,runner.Deliveries);}
        else{Assert.Throws<MabiAgent.Workflows.InputRejectedBeforeDispatchException>(Send);Assert.Equal(0,runner.Deliveries);Assert.False(guard.Paused);}
        Assert.Equal(1,checks);Assert.Equal(1,reviews);Assert.Equal(0,passive.MouseWrites);Assert.Equal(1,runner.Releases);
    }
    private sealed class TestAuthority:IConsumptionAuthorization
    {public bool VerifyPersistedIntent(string id,TargetProof target,UiObservation review)=>true;}
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
    [Theory][InlineData(ClickRegionShape.Rectangle)][InlineData(ClickRegionShape.Ellipse)]
    public void CenterOffsetStaysAwayFromEdgesIncludingTinyTargets(ClickRegionShape shape)
    {foreach(var box in new[]{new PixelBox(0,0,1,1),new PixelBox(100,20,110,28),new PixelBox(0,0,200,200)})for(var i=0;i<100;i++){var p=box.CenterOffsetPoint(shape:shape);Assert.True(box.Contains(p,shape));Assert.InRange(Math.Abs(p.X-box.Center.X),0,3);Assert.InRange(Math.Abs(p.Y-box.Center.Y),0,3);}}
    [Theory][InlineData("click",450,250)][InlineData("drag",450,250)][InlineData("pinch",450,250)][InlineData("click",650,450)][InlineData("drag",650,450)][InlineData("pinch",650,450)]
    public void TransparentNativeHelperProducesRealTransferableTouchWithoutActivation(string kind,int x,int y)
    {
        if(!OperatingSystem.IsWindows())return;
        var box=new PixelBox(x-250,y-150,x+250,y+150);using var receiver=new TouchInputWindow(box,legacyTouch:true);using var runner=new NativeTouchGestureRunner();var before=TouchCursorPresentation.Read();
        var state=new NativeWindowsOps().ReadWindow(receiver.Hwnd);var plan=kind=="click"?new TouchGesturePlan("click",[[box.Center]]):TouchGesturePlan.Create(kind,box.Center);
        try{runner.Execute(plan,receiver.Hwnd,kind=="click"?40:0,()=>{},()=>{},()=>state);}
        catch(Exception error){throw new Exception(error.Message+"，目标消息 "+new JsonArray(receiver.Received.Take(12).Select(row=>(JsonNode)row.DeepClone()).ToArray()).ToJsonString(),error);}
        Assert.True(SpinWait.SpinUntil(()=>receiver.Received.Any(j=>j["contacts"] is JsonArray),1000));
        var contacts=receiver.Received.Where(j=>j["contacts"] is JsonArray).SelectMany(j=>j["contacts"]!.AsArray()).ToArray();Assert.Contains(contacts,c=>(c!["flags"]!.GetValue<uint>()&2)!=0);Assert.Contains(contacts,c=>(c!["flags"]!.GetValue<uint>()&4)!=0);
        Assert.Equal(before.Foreground,TouchCursorPresentation.Read().Foreground);
    }
    [Fact] public void PauseDuringGestureReleasesOnceAndManualPauseNeedsExplicitResume()
    {
        var runner=new Runner();var passive=new Passive();using var ops=new TouchInputOps(State,passive,passive,runner);using var guard=Guard(ops);guard.Start();runner.OnStart=guard.RequestPause;
        Assert.Throws<InputBlockedException>(()=>guard.Click(guard.ObserveTarget("scene.open_bag")));Assert.True(guard.Paused);Assert.Equal(1,runner.Releases);Assert.Equal(1,runner.Calls);
        guard.Pause();Assert.Throws<InputBlockedException>(()=>guard.ObserveTarget("scene.open_bag"));guard.Resume();runner.OnStart=null;guard.Click(guard.ObserveTarget("scene.open_bag"));Assert.Equal(2,runner.Calls);
    }
    private static NativeWindowState State=>new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,100,100,96),new(0,0,100,100),99,true,false,true,new(0,0));
    private static ForegroundInputGuard Guard(INativeWindowsOps ops){var scope=new SessionScope("test",null,null);return new(State,scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,scope,new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(50,50)}},[],TargetRegions:new Dictionary<string,PixelBox>{{"scene.open_bag",new(10,10,90,90)}}),true,true),maxFrameAgeSeconds:5,inputOptions:new("touch"));}
    private sealed class Runner:ITouchGestureRunner
    {public TouchGesturePlan? Plan;public int Hold,Calls,Releases;public bool Fail;public int Deliveries;public Action? OnStart,OnPrepared;private Action? beforeDelivery;public bool SupportsPreparedReview=>true;public void SetBeforeDelivery(Action check)=>beforeDelivery=check;public void Execute(TouchGesturePlan p,nint target,int hold,Action check,Action release,Func<NativeWindowState> verify){Calls++;Plan=p;Hold=hold;OnStart?.Invoke();check();OnPrepared?.Invoke();beforeDelivery?.Invoke();Deliveries++;if(Fail)throw new InputOutcomeUnknownException("test");}public void Release(Func<NativeWindowState> verify){verify();Releases++;}public void Dispose(){}}
    private sealed class Passive:INativeWindowsOps,ICurrentClientFrameSource
    {private long sequence;public int Keys,MouseWrites;public NativeWindowState ReadWindow(nint hwnd)=>State;public RgbImage CaptureClient(NativeWindowState state)=>new(100,100,new byte[30000]);public CurrentClientFrame CaptureCurrentClientFrame(NativeWindowState state)=>new(CaptureClient(state),new("test",1,++sequence,System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency));public void MoveClient(nint hwnd,PixelPoint p)=>MouseWrites++;public void MouseButton(MouseButton b,bool down)=>MouseWrites++;public void Wheel(int delta)=>MouseWrites++;public void Key(ushort key,bool down,bool unicode=false)=>Keys++;public bool AnyPhysicalKeyDown()=>false;}
}
