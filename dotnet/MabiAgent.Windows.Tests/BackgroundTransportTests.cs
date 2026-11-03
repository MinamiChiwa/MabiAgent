using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;

public sealed class BackgroundTransportTests
{
    private static readonly SessionScope Scope=new("background-test","account","character");
    private static ForegroundInputGuard Make(Ops ops)=>new(ops.State,Scope,ops,c=>new(c.Image.Sha256,c.CapturedAt,Scope,new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(10,10)}},[]),true,true),()=>1);
    [Fact] public void BackgroundClickDoesNotRequireFocusAndOtherApplicationTypingDoesNotPause()
    {
        var ops=new Ops{PhysicalKey=true};using var guard=Make(ops);guard.Start();guard.Click(guard.ObserveTarget("scene.open_bag"));
        Assert.False(guard.RequiresForeground);Assert.False(guard.Paused);Assert.Equal(["move","Left:True","Left:False"],ops.Events);Assert.Equal((nint)99,ops.State.ForegroundId);
        Assert.Equal(MouseButton.Left,ops.PreparedButton);
    }
    [Fact] public void RightClickUsesTheMatchingPreludeAndSingleRelease()
    {
        var ops=new Ops();using var guard=Make(ops);guard.Start();guard.Click(guard.ObserveTarget("scene.open_bag"),MouseButton.Right);
        Assert.Equal(MouseButton.Right,ops.PreparedButton);Assert.Equal(["move","Right:True","Right:False"],ops.Events);
    }
    [Fact] public void PhysicalInputInTheGameStillPausesBackgroundTransport()
    {
        var ops=new Ops();using var guard=Make(ops);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");ops.PhysicalKey=true;ops.State=ops.State with{ForegroundId=1};
        Assert.Throws<InputBlockedException>(()=>guard.Click(proof));Assert.Empty(ops.Events);Assert.Equal("user_input",guard.PauseReason);
    }
    [Fact] public void TraditionalTextAndShortcutUseSameGuardAndManualPauseStillRequiresResume()
    {
        var ops=new Ops();using var guard=Make(ops);guard.Start();guard.TypeText(guard.ObserveTarget("scene.open_bag"),"鐵礦石",focus:false);
        Assert.Equal(6,ops.Events.Count);Assert.Contains("u:37941:True",ops.Events);guard.PressKey(guard.ObserveTarget("scene.open_bag"),87,TimeSpan.FromMilliseconds(20));
        Assert.Equal("k:87:False",ops.Events.Last());guard.Pause();Assert.Throws<InputBlockedException>(()=>guard.TypeText(guard.ObserveTarget("scene.open_bag"),"a"));
        guard.Resume();guard.TypeText(guard.ObserveTarget("scene.open_bag"),"a",focus:false);Assert.False(guard.Paused);
    }
    [Fact] public void FailedDownIsReleasedOnlyOnceAndNeverAutomaticallyRetried()
    {
        var ops=new Ops{FailDown=true};using var guard=Make(ops);guard.Start();Assert.Throws<InputOutcomeUnknownException>(()=>guard.Click(guard.ObserveTarget("scene.open_bag")));
        Assert.Equal(["move","Left:True","Left:False"],ops.Events);Assert.True(guard.Paused);Assert.Throws<InputBlockedException>(()=>guard.ObserveTarget("scene.open_bag"));
    }
    [Fact] public void BackgroundStillRejectsProcessReplacementAndDpiChangesBeforeDispatch()
    {
        var ops=new Ops();using var guard=Make(ops);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");ops.State=ops.State with{ProcessStartUtcTicks=999};
        Assert.Throws<InputBlockedException>(()=>guard.Click(proof));Assert.Empty(ops.Events);
        ops.State=ops.State with{ProcessStartUtcTicks=42};proof=guard.ObserveTarget("scene.open_bag");ops.State=ops.State with{Geometry=ops.State.Geometry with{Dpi=144}};
        Assert.Throws<InputBlockedException>(()=>guard.Click(proof));Assert.Empty(ops.Events);
    }
    [Fact] public void PauseDuringHeldKeyStillReleasesAndDoesNotDeliverAnotherDown()
    {
        var ops=new Ops();using var guard=Make(ops);guard.Start();ops.OnKeyDown=guard.RequestPause;
        Assert.Throws<InputBlockedException>(()=>guard.PressKey(guard.ObserveTarget("scene.open_bag"),87,TimeSpan.FromMilliseconds(50)));
        Assert.Equal(["k:87:True","k:87:False"],ops.Events);
    }
    [Fact] public void MessageEncodingPreservesSignedCoordinatesAndExtendedKeyRelease()
    {
        Assert.Equal(unchecked((nint)(int)0xfffeffff),WindowMessageInputOps.EncodePoint(new(-1,-2)));
        Assert.Equal(unchecked((nint)(int)0xc11d0001),WindowMessageInputOps.EncodeKey(0xe01d,true));
        Assert.Throws<ArgumentOutOfRangeException>(()=>WindowMessageInputOps.EncodePoint(new(32768,0)));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroDelayRequiresTransportOptInAndStillUsesTheGuard(bool enabled)
    {
        var ops=new Ops{Delay=0,AllowsZeroClickDelay=enabled};using var guard=Make(ops);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");
        if(enabled){guard.Click(proof);Assert.Equal(["move","Left:True","Left:False"],ops.Events);}
        else{Assert.Throws<InputBlockedException>(()=>guard.Click(proof));Assert.Empty(ops.Events);}
    }
    [Fact] public void ZeroDelayStillHonorsPauseBeforeMouseDown()
    {
        var ops=new Ops{Delay=0,AllowsZeroClickDelay=true};using var guard=Make(ops);guard.Start();ops.OnMove=guard.RequestPause;
        Assert.Throws<InputBlockedException>(()=>guard.Click(guard.ObserveTarget("scene.open_bag")));Assert.Equal(["move"],ops.Events);Assert.True(guard.Paused);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClickLifetimeAlwaysReleasesButtonBeforeEndingGesture(bool fail)
    {
        var ops=new Ops{UseLifetime=true,FailDown=fail};using var guard=Make(ops);guard.Start();var proof=guard.ObserveTarget("scene.open_bag");
        if(fail)Assert.Throws<InputOutcomeUnknownException>(()=>guard.Click(proof));else guard.Click(proof);
        Assert.Equal(["begin","move","Left:True","Left:False","end"],ops.Events);
    }
    sealed class Ops:INativeWindowsOps,IGuardedWindowTransport,IGuardedClickLifetime
    {
        public bool RequiresForeground=>false;public void BindGuard(ForegroundInputGuard guard){}
        public bool AllowsZeroClickDelay{get;init;}public int Delay{get;init;}=16;public int MoveSampleMilliseconds=>Delay;public int ClickSampleMilliseconds=>Delay;
        public bool UseLifetime;public IDisposable? BeginClick(nint hwnd,PixelPoint point){if(!UseLifetime)return null;Events.Add("begin");return new EndClick(()=>Events.Add("end"));}
        public NativeWindowState State=new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,100,100,96),new(0,0,100,100),99,true,false,true,new(0,0));
        public bool PhysicalKey,FailDown;public Action? OnKeyDown,OnMove;public List<string> Events=[];public MouseButton? PreparedButton;
        public NativeWindowState ReadWindow(nint hwnd)=>State;
        public RgbImage CaptureClient(NativeWindowState state)=>new(100,100,new byte[30000]);
        public void MoveClient(nint hwnd,PixelPoint point){Events.Add("move");OnMove?.Invoke();}
        public void MoveForClick(nint hwnd,PixelPoint point,MouseButton button){PreparedButton=button;MoveClient(hwnd,point);}
        public void MouseButton(MouseButton button,bool down){Events.Add($"{button}:{down}");if(down&&FailDown)throw new InputOutcomeUnknownException("test");}
        public void Wheel(int delta)=>Events.Add("wheel");
        public void Key(ushort key,bool down,bool unicode=false){Events.Add($"{(unicode?"u":"k")}:{key}:{down}");if(down)OnKeyDown?.Invoke();}
        public bool AnyPhysicalKeyDown()=>PhysicalKey;
        private sealed class EndClick(Action end):IDisposable{public void Dispose()=>end();}
    }
}
