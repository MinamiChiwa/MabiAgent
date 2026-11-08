using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;
public class ForegroundGuardTests
{
    [Fact] public void BagSearchTextSurvivesFrameSampledReceiver()
    {
        var ops=new FakeOps();var scope=new SessionScope("search",null,null);
        using var guard=new ForegroundInputGuard(ops.State,scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,scope,new("bag_search",false,[],new Dictionary<string,PixelPoint>{{"bag.search_input",new(5,5)}},[]),true,true),()=>1);
        var received=new System.Text.StringBuilder();var timer=System.Diagnostics.Stopwatch.StartNew();double last=-100;double entered=0;var enterHolds=new List<double>();
        ops.OnKey=(key,down,unicode)=>
        {
            if(unicode&&down&&timer.Elapsed.TotalMilliseconds-last>=75){received.Append((char)key);last=timer.Elapsed.TotalMilliseconds;}
            if(!unicode&&key==13){if(down)entered=timer.Elapsed.TotalMilliseconds;else enterHolds.Add(timer.Elapsed.TotalMilliseconds-entered);}
        };
        guard.Start();guard.EditBagSearch(guard.ObserveTarget("bag.search_input"),"高級原木樹液銀礦石");
        Assert.Equal("高級原木樹液銀礦石",received.ToString());Assert.Equal(2,enterHolds.Count);Assert.All(enterHolds,value=>Assert.True(value>=60));
    }
    [Theory][InlineData(1,true)][InlineData(double.NaN,false)][InlineData(-1,false)]
    public void CaptureTimeoutIsDistinctFromInvalidClockAndSendsNoInput(double elapsed,bool timeout)
    {
        var ops=new FakeOps();var reads=0;var reviews=0;
        using var guard=new ForegroundInputGuard(ops.State,new("capture",null,null),ops,_=>{reviews++;throw new InvalidOperationException();},()=>reads++==0?1:1+elapsed);
        guard.Start();var error=Assert.Throws<InputBlockedException>(()=>guard.Capture());
        Assert.Equal(timeout,error.CaptureTimedOut);Assert.Equal(0,reviews);Assert.Empty(ops.Events);Assert.False(guard.Paused);
    }
    [Fact] public void ReviewedCraftKeyUsesSameDurableAuthorityAndCannotReplay()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();
        guard.Consume(guard.ObserveTarget("scene.open_bag"),"craft-key",new Authority(),key:32);
        Assert.Equal(new[]{"key","key"},ops.Events);Assert.False(ops.KeyHeld);
        Assert.Throws<MabiAgent.Workflows.InputRejectedBeforeDispatchException>(()=>guard.Consume(guard.ObserveTarget("scene.open_bag"),"craft-key",new Authority(),key:32));
        Assert.Equal(2,ops.Events.Count);
    }
    [Fact] public void FixedScriptOnlyChecksWindowAndAlwaysReleasesUnknownInput()
    {
        var ops=new FakeOps();var scope=new SessionScope("script",null,null);
        using var guard=new ForegroundInputGuard(ops.State,scope,ops,_=>throw new InvalidOperationException("fixed script must not observe"));
        guard.Start();guard.ClickAt(new(5,5));guard.PressKey(32,TimeSpan.FromMilliseconds(1));guard.TypeText("字");
        Assert.False(guard.Paused);ops.FailDown=true;Assert.Throws<InputOutcomeUnknownException>(()=>guard.ClickAt(new(5,5)));
        Assert.True(guard.Paused);Assert.Equal("left:False",ops.Events.Last());
    }
    [Fact] public void FixedScriptMinimizeRequiresExplicitResumeAndNoInputIsSent()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();ops.State=ops.State with{Minimized=true};
        Assert.Throws<InputBlockedException>(()=>guard.ClickAt(new(5,5)));Assert.Empty(ops.Events);
        ops.State=ops.State with{Minimized=false};Assert.Throws<InputBlockedException>(()=>guard.ClickAt(new(5,5)));
        guard.Resume();guard.ClickAt(new(5,5));Assert.Equal(3,ops.Events.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void MovementTapReleasesBeforeReviewedConsumptionEvenIfClickFails(bool fail)
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();
        guard.PressKey(guard.ObserveTarget("scene.open_bag"),87,TimeSpan.FromMilliseconds(60));
        Assert.False(ops.KeyHeld);
        void Run()
        {
            ops.AfterDown=()=>{Assert.False(ops.KeyHeld);if(fail)throw new InputOutcomeUnknownException("unknown");};
            guard.Consume(guard.ObserveTarget("scene.open_bag"),"moving-cast",new Authority());
        }
        if(fail)Assert.Throws<InputOutcomeUnknownException>(Run);else Run();
        Assert.Equal(new[]{"key","key","move","left:True","left:False"},ops.Events);Assert.Equal(fail,guard.Paused);
    }
    [Fact]
    public void CurrentReviewReusesOneFixedFrameForDispatch()
    {
        var ops=new FakeOps();var scope=new SessionScope("session","account","character");var visible=true;var reviews=0;
        using var guard=new ForegroundInputGuard(ops.State,scope,ops,c=>{reviews++;return new(c.Image.Sha256,c.CapturedAt,scope,new("scene",false,[],visible?new Dictionary<string,PixelPoint>{{"scene.open_bag",new(5,5)}}:[],[]),true,true,Frame:c.Frame,ResourceRevision:c.ResourceRevision);},()=>1);
        guard.Start();var review=guard.ObserveFrame();var proof=guard.ObserveCurrentTarget("scene.open_bag",review);Assert.Equal(1,reviews);
        visible=false;guard.Click(proof);Assert.Equal(1,reviews);Assert.Equal(3,ops.Events.Count);
    }
    [Theory][InlineData("copy")][InlineData("resume")][InlineData("stale")][InlineData("source")]
    public void CurrentReviewMustBeOwnedFreshAndFromCurrentSession(string change)
    {
        var ops=new FakeOps();var scope=new SessionScope("session","account","character");double now=1;var reviews=0;
        using var guard=new ForegroundInputGuard(ops.State,scope,ops,c=>{reviews++;return new(c.Image.Sha256,c.CapturedAt,scope,new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(5,5)}},[]),true,true,Frame:c.Frame,ResourceRevision:c.ResourceRevision);},()=>now);
        guard.Start();var review=guard.ObserveFrame();
        if(change=="copy")review=review with{};
        if(change=="resume"){guard.Pause();guard.Resume();}
        if(change=="stale")now=2;
        if(change=="source")guard.Capture();
        Assert.Throws<InputBlockedException>(()=>guard.ObserveCurrentTarget("scene.open_bag",review));Assert.Equal(1,reviews);Assert.Empty(ops.Events);
        var current=guard.ObserveFrame();guard.Click(guard.ObserveCurrentTarget("scene.open_bag",current));Assert.Equal(2,reviews);Assert.Equal(3,ops.Events.Count);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void UnstableCurrentPageCanReobserveButUnverifiedIdentityCannot(bool identity)
    {
        var ops=new FakeOps();var scope=new SessionScope("session","account","character");var stable=true;var verified=true;
        using var guard=new ForegroundInputGuard(ops.State,scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,scope,new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(5,5)}},[]),stable,verified),()=>1);
        guard.Start();var proof=guard.ObserveTarget("scene.open_bag");stable=false;verified=identity;
        stable=verified=identity;guard.Click(proof);Assert.Equal(3,ops.Events.Count);Assert.False(guard.Paused);
    }
    [Fact]
    public void UnrelatedMovingTargetDoesNotBlockInputButSelectedTargetStillMustMatch()
    {
        var ops=new FakeOps();var scope=new SessionScope("session","account","character");double now=1;var moving=0;var selected=new PixelPoint(5,5);
        var observer=new ScreenObserver(_=>scope,_=>new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",selected},{"repair.talk",new(20+moving++,20)}},[]));
        using var guard=new ForegroundInputGuard(ops.State,scope,ops,observer.Observe,()=>now+=.01);
        guard.Start();guard.Click(guard.ObserveTarget("scene.open_bag"));Assert.Equal(3,ops.Events.Count);
        var proof=guard.ObserveTarget("scene.open_bag");selected=new(6,5);
        guard.Click(proof);Assert.Equal(6,ops.Events.Count);Assert.False(guard.Paused);
    }
    [Theory][InlineData(27)][InlineData(75)]
    public void MenuAndSkillsShortcutsShareNativeAndBackgroundKeyPolicy(ushort key)
    {
        Assert.True(InputKeyPolicy.IsSupported(key));var ops=new FakeOps();using var guard=Make(ops);guard.Start();guard.PressKey(guard.ObserveTarget("scene.open_bag"),key,TimeSpan.FromMilliseconds(10));
        Assert.Equal(new[]{"key","key"},ops.Events);
    }
    [Fact]
    public void SingleFrameFixedTargetCanDispatchWithoutStablePair()
    {
        var ops=new FakeOps();var scope=new SessionScope("session","account","character");
        using var guard=new ForegroundInputGuard(ops.State,scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,scope,new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(5,5)}},[]),false,true,Frame:c.Frame,ResourceRevision:c.ResourceRevision),()=>1);
        guard.Start();guard.Click(guard.ObserveTarget("scene.open_bag"));Assert.Equal(3,ops.Events.Count);
    }
    [Fact] public void ConsumptionPreflightIsDistinctFromFailureAfterEnteringNativeClick()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");
        Assert.Throws<MabiAgent.Workflows.InputRejectedBeforeDispatchException>(()=>guard.Consume(proof with{Point=new(80,80)},"x",new Authority()));Assert.Empty(ops.Events);
        ops.AfterMove=()=>ops.State=ops.State with{Geometry=ops.State.Geometry with{X=200}};
        Assert.Throws<InputBlockedException>(()=>guard.Consume(guard.ObserveTarget("scene.open_bag"),"y",new Authority()));Assert.Equal(["move"],ops.Events);
    }
    [Fact] public void ConsumptionTransportFailureDoesNotPersistIntent()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");var authority=new Authority();
        ops.State=ops.State with{ForegroundId=2};
        Assert.Throws<MabiAgent.Workflows.InputRejectedBeforeDispatchException>(()=>guard.Consume(proof,"not-sent",authority));
        Assert.Equal(0,authority.Calls);Assert.Empty(ops.Events);
    }
    [Fact] public void KnownFailureAfterNativeMoveDoesNotBurnConsumptionIntent()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();var authority=new Authority();
        ops.AfterMove=()=>ops.State=ops.State with{Geometry=ops.State.Geometry with{X=200}};
        Assert.Throws<InputBlockedException>(()=>guard.Consume(guard.ObserveTarget("scene.open_bag"),"after-move",authority));
        Assert.Equal(0,authority.Calls);Assert.Equal(["move"],ops.Events);
        ops.AfterMove=null;guard.Resume();guard.Consume(guard.ObserveTarget("scene.open_bag"),"after-move",authority);
        Assert.Equal(1,authority.Calls);Assert.Equal(4,ops.Events.Count);
    }
    [Fact] public void WindowCanMoveBeforeFreshReviewAndClientPointUsesNewOrigin()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");
        ops.State=ops.State with{Geometry=ops.State.Geometry with{X=-1200,Y=300},WindowRect=new(-1200,300,-1100,400)};
        Assert.Throws<InputBlockedException>(()=>guard.Click(proof));Assert.Empty(ops.Events);Assert.False(guard.Paused);
        guard.Click(guard.ObserveTarget("scene.open_bag"));Assert.Equal(new PixelPoint(-1195,305),ops.ScreenPoints.Single());Assert.False(guard.Paused);
    }
    [Fact] public void MoveAfterCursorPlacementBlocksMouseDown()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();ops.AfterMove=()=>ops.State=ops.State with{Geometry=ops.State.Geometry with{X=200}};
        Assert.Throws<InputBlockedException>(()=>guard.Click(guard.ObserveTarget("scene.open_bag")));Assert.Equal(["move"],ops.Events);
        ops.AfterMove=null;guard.Resume();guard.Click(guard.ObserveTarget("scene.open_bag"));Assert.Equal(new PixelPoint(205,5),ops.ScreenPoints.Last());
    }
    [Fact] public void DpiOrResizeRequiresNewTargetProofThenCanResumeWithFreshGeometry()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();var old=guard.ObserveTarget("scene.open_bag");
        ops.State=ops.State with{Geometry=ops.State.Geometry with{Width=200,Height=200,Dpi=192}};
        Assert.Throws<InputBlockedException>(()=>guard.Click(old));Assert.Empty(ops.Events);
        guard.Click(guard.ObserveTarget("scene.open_bag"));Assert.Equal(3,ops.Events.Count);
    }
    [Theory] [InlineData(ClickRegionShape.Rectangle)] [InlineData(ClickRegionShape.Ellipse)]
    public void RandomClickUsesOnlyRevalidatedRegionAndShape(ClickRegionShape shape)
    {
        var ops=new FakeOps();using var guard=Make(ops,regions:true,shape:shape);guard.Start();
        for(var i=0;i<30;i++)guard.Click(guard.ObserveTarget("scene.open_bag",randomize:true));
        Assert.All(ops.ClientPoints,p=>Assert.True(new PixelBox(0,0,10,10).Contains(p,shape)));
        var proof=guard.ObserveTarget("scene.open_bag",randomize:true);Assert.Throws<InputBlockedException>(()=>guard.Click(proof with{ClickRegion=new(0,0,100,100)}));
        using var plain=Make(new());plain.Start();Assert.Throws<InputBlockedException>(()=>plain.ObserveTarget("scene.open_bag",randomize:true));
    }
    [Fact] public void DragSamplesCentralStartAndKeepsExactDestination()
    {
        var ops=new FakeOps();using var guard=Make(ops,regions:true,region:new(0,0,50,50));guard.Start();
        guard.Drag(guard.ObserveTarget("scene.open_bag",randomize:true),new(70,70),steps:3);
        var start=ops.ClientPoints[0];Assert.True(new PixelBox(10,10,40,40).Contains(start));
        Assert.Equal(new PixelPoint(70,70),ops.ClientPoints.Last());
        Assert.Equal(new PixelPoint(start.X+(70-start.X)/3,start.Y+(70-start.Y)/3),ops.ClientPoints[1]);
        Assert.Equal("left:False",ops.Events.Last());
    }
    [Fact] public void WheelUsesSeparateNotchesAndStopsOnPause()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();guard.Scroll(guard.ObserveTarget("scene.open_bag"),-3);
        Assert.Equal(new[]{-120,-120,-120},ops.Wheels);
        ops.Wheels.Clear();ops.AfterWheel=guard.RequestPause;
        Assert.Throws<InputBlockedException>(()=>guard.Scroll(guard.ObserveTarget("scene.open_bag"),-3));Assert.Single(ops.Wheels);
    }
    [Fact] public void DragSpansGameFramesAndReleasesOnFocusLoss()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();guard.Drag(guard.ObserveTarget("scene.open_bag"),new(70,70));
        Assert.True(ops.HeldFor.TotalMilliseconds>=180);Assert.Equal("left:False",ops.Events.Last());
        ops.AfterDown=()=>ops.State=ops.State with{ForegroundId=2};
        Assert.Throws<InputBlockedException>(()=>guard.Drag(guard.ObserveTarget("scene.open_bag"),new(70,70)));
        Assert.Equal("left:False",ops.Events.Last());Assert.True(guard.Paused);
    }
    [Fact] public void FocusLostAfterMovePreventsMouseDown()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();
        ops.AfterMove=()=>ops.State=ops.State with{ForegroundId=2};
        Assert.Throws<InputBlockedException>(()=>guard.Click(guard.ObserveTarget("scene.open_bag")));
        Assert.Equal(["move"],ops.Events);Assert.Equal("focus_lost",guard.PauseReason);
    }
    [Fact] public void PauseAfterMovePreventsMouseDown()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();ops.AfterMove=guard.RequestPause;
        Assert.Throws<InputBlockedException>(()=>guard.Click(guard.ObserveTarget("scene.open_bag")));
        Assert.Equal(["move"],ops.Events);Assert.True(guard.Paused);
    }
    [Fact] public void ExpiredEvidenceAfterMovePreventsMouseDown()
    {
        var ops=new FakeOps();double now=1;using var guard=Make(ops,()=>now);guard.Start();ops.AfterMove=()=>now=2;
        Assert.Throws<InputBlockedException>(()=>guard.Click(guard.ObserveTarget("scene.open_bag")));
        Assert.Equal(["move"],ops.Events);
    }
    [Fact] public void PhysicalMouseAfterMovePreventsMouseDown()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();ops.AfterMove=()=>ops.State=ops.State with{LeftDown=true};
        Assert.Throws<InputBlockedException>(()=>guard.Click(guard.ObserveTarget("scene.open_bag")));
        Assert.Equal(["move"],ops.Events);Assert.Equal("user_input",guard.PauseReason);
    }
    [Fact] public void WindowReplacedAfterMovePreventsMouseDown()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();ops.AfterMove=()=>ops.State=ops.State with{ProcessStartUtcTicks=99};
        Assert.Throws<InputBlockedException>(()=>guard.Click(guard.ObserveTarget("scene.open_bag")));
        Assert.Equal(["move"],ops.Events);
    }
    [Fact] public void FocusLossAfterDownStillReleasesOnlyOnce()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();ops.AfterDown=()=>ops.State=ops.State with{ForegroundId=2};
        var proof=guard.ObserveTarget("scene.open_bag");Assert.Throws<InputBlockedException>(()=>guard.Click(proof));
        Assert.Equal(["move","left:True","left:False"],ops.Events);
        Assert.Throws<InputBlockedException>(()=>guard.Click(proof));Assert.Equal(3,ops.Events.Count);
    }
    [Fact] public void WorkflowResumeCannotClearManualOrFocusPause()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();guard.Pause("inventory_sync");guard.ResumeWorkflow();Assert.False(guard.Paused);
        guard.Pause("manual");guard.Pause("inventory_sync");Assert.Throws<InputBlockedException>(guard.ResumeWorkflow);
        guard.Resume();guard.Pause("focus_lost");Assert.Throws<InputBlockedException>(guard.ResumeWorkflow);Assert.True(guard.Paused);
    }
    [Fact] public void PlayerKeyBeforeDispatchPausesWithoutLosingReason()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");ops.KeyHeld=true;
        Assert.Throws<InputBlockedException>(()=>guard.Click(proof));
        Assert.Equal("user_input",guard.PauseReason);Assert.Empty(ops.Events);
        ops.KeyHeld=false;Assert.Throws<InputBlockedException>(()=>guard.Click(proof));
        guard.Resume();guard.Click(guard.ObserveTarget("scene.open_bag"));Assert.Equal(3,ops.Events.Count);
    }
    [Fact] public void LostFocusPausesAndNeverActivates()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");ops.State=ops.State with{ForegroundId=2};
        Assert.Throws<InputBlockedException>(()=>guard.Click(proof));Assert.True(guard.Paused);Assert.Empty(ops.Events);
        ops.State=ops.State with{ForegroundId=1};Assert.Throws<InputBlockedException>(()=>guard.Click(proof));
        guard.Resume();guard.Click(guard.ObserveTarget("scene.open_bag"));Assert.Equal(["move","left:True","left:False"],ops.Events);
    }
    [Fact] public void ManualPauseAndStaleProofCannotBeBypassed()
    {
        var ops=new FakeOps();double now=1;using var guard=Make(ops,()=>now);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");now=2;Assert.Throws<InputBlockedException>(()=>guard.Click(proof));
        guard.Pause();Assert.Throws<InputBlockedException>(()=>guard.Start());Assert.Empty(ops.Events);
    }
    [Fact] public void ProcessReuseAndUnstablePagePreventInput()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");ops.State=ops.State with{ProcessStartUtcTicks=99};Assert.Throws<InputBlockedException>(()=>guard.Click(proof));Assert.Empty(ops.Events);
    }
    [Fact] public void FailedDownStillReleasesAndNeverRetries()
    {
        var ops=new FakeOps(){FailDown=true};using var guard=Make(ops);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");Assert.Throws<InputOutcomeUnknownException>(()=>guard.Click(proof));Assert.Equal(["move","left:True","left:False"],ops.Events);Assert.True(guard.Paused);
    }
    [Fact] public void MutatedScopeAndTargetPreventDispatch()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");Assert.Throws<InputBlockedException>(()=>guard.Click(proof with{Point=new(80,80)}));Assert.Empty(ops.Events);
    }
    [Fact] public void ForgedHashAndPreviousResumeProofAreRejected()
    {
        var ops=new FakeOps();using var guard=Make(ops);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");Assert.Throws<InputBlockedException>(()=>guard.Click(proof with{ImageSha256="invented"}));guard.Pause();guard.Resume();Assert.Throws<InputBlockedException>(()=>guard.Click(proof));Assert.Empty(ops.Events);
    }
    [Fact] public void PersistedConsumptionIsOneShotEvenWhenInputOutcomeIsUnknown()
    {
        var ops=new FakeOps(){FailDown=true};using var guard=Make(ops);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");var authority=new Authority();Assert.Throws<InputOutcomeUnknownException>(()=>guard.Consume(proof,"intent-1",authority));guard.Resume();ops.FailDown=false;Assert.Throws<MabiAgent.Workflows.InputRejectedBeforeDispatchException>(()=>guard.Consume(guard.ObserveTarget("scene.open_bag"),"intent-1",authority));Assert.Equal(1,authority.Calls);
    }
    sealed class Authority:IConsumptionAuthorization{public int Calls;public bool VerifyPersistedIntent(string id,TargetProof target,UiObservation review){Calls++;return true;}}
    static ForegroundInputGuard Make(FakeOps ops,Func<double>? clock=null,bool regions=false,ClickRegionShape shape=ClickRegionShape.Rectangle,PixelBox? region=null)=>new(ops.State,new("session","account","character"),ops,c=>new(c.Image.Sha256,c.CapturedAt,new("session","account","character"),new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(5,5)}},[],TargetRegions:regions?new Dictionary<string,PixelBox>{{"scene.open_bag",region??new(0,0,10,10)}}:null,TargetShapes:new Dictionary<string,ClickRegionShape>{{"scene.open_bag",shape}}),true,true),clock??(()=>1));
    sealed class FakeOps:INativeWindowsOps
    {
        public NativeWindowState State=new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,100,100,96),new(0,0,100,100),1,true,false,true,new(0,0));
        public List<string> Events=[];public List<int> Wheels=[];public List<PixelPoint> ClientPoints=[],ScreenPoints=[];public bool FailDown,KeyHeld;public Action? AfterMove,AfterDown,AfterWheel;
        private System.Diagnostics.Stopwatch? held;public TimeSpan HeldFor;public Action<ushort,bool,bool>? OnKey;
        public NativeWindowState ReadWindow(nint hwnd)=>State;
        public RgbImage CaptureClient(NativeWindowState state)=>new(state.Geometry.Width,state.Geometry.Height,new byte[state.Geometry.Width*state.Geometry.Height*3]);
        public void MoveClient(nint hwnd,PixelPoint point){Events.Add("move");ClientPoints.Add(point);ScreenPoints.Add(new(State.Geometry.X+point.X,State.Geometry.Y+point.Y));AfterMove?.Invoke();}
        public void MouseButton(MouseButton button,bool down){Events.Add($"{button.ToString().ToLowerInvariant()}:{down}");if(down){held=System.Diagnostics.Stopwatch.StartNew();AfterDown?.Invoke();}else HeldFor=held?.Elapsed??TimeSpan.Zero;if(down&&FailDown)throw new InputOutcomeUnknownException("unknown");}
        public void Wheel(int delta){Events.Add("wheel");Wheels.Add(delta);AfterWheel?.Invoke();}
        public void Key(ushort key,bool down,bool unicode=false){Events.Add("key");if(!unicode&&key is 87 or 83)KeyHeld=down;OnKey?.Invoke(key,down,unicode);}
        public bool AnyPhysicalKeyDown()=>KeyHeld;
    }
}
