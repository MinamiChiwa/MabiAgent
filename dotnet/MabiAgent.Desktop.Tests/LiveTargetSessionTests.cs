using MabiAgent.Desktop.Editors;
using MabiAgent.Windows;
using Xunit;

public sealed class LiveTargetSessionTests
{
    [Fact]
    public async Task OnlyTheBoundGameAndRegisteredOverlayMayOwnForeground()
    {
        var (selection,ops)=Create();
        var session=await new SelectedGameCaptureService(selection,ops).PrepareSelectionAsync();
        session.Validate();
        ops.State=ops.State with{ForegroundId=2};
        Assert.Throws<InputBlockedException>(session.Validate);
        session.AcceptOverlay!(2);
        session.Validate();
        ops.State=ops.State with{ForegroundId=3};
        Assert.Throws<InputBlockedException>(session.Validate);
        Assert.Equal(0,ops.Captures);
    }

    [Theory]
    [InlineData("process")]
    [InlineData("start")]
    [InlineData("executable")]
    [InlineData("geometry")]
    [InlineData("windowRect")]
    [InlineData("minimized")]
    [InlineData("disabled")]
    [InlineData("selection")]
    [InlineData("running")]
    public async Task ChangingTargetOrRunStateRejectsSelectionBeforeCapture(string change)
    {
        var (selection,ops)=Create();var running=false;
        var session=await new SelectedGameCaptureService(selection,ops,()=>running).PrepareSelectionAsync();
        session.AcceptOverlay!(2);ops.State=ops.State with{ForegroundId=2};
        ops.State=change switch
        {
            "process"=>ops.State with{ProcessId=9},
            "start"=>ops.State with{ProcessStartUtcTicks=99},
            "executable"=>ops.State with{Executable=@"C:\Other\mabinogimobile.exe"},
            "geometry"=>ops.State with{Geometry=ops.State.Geometry with{X=101}},
            "windowRect"=>ops.State with{WindowRect=new(101,100,140,140)},
            "minimized"=>ops.State with{Minimized=true},
            "disabled"=>ops.State with{Enabled=false},
            _=>ops.State
        };
        if(change=="selection")selection.Select(null);
        if(change=="running")running=true;
        Assert.Throws<InputBlockedException>(()=>session.Capture());
        Assert.Equal(0,ops.Captures);
    }

    [Fact]
    public async Task ForegroundLossBeforeCaptureRejectsWithoutReadingPixels()
    {
        var (selection,ops)=Create();
        var session=await new SelectedGameCaptureService(selection,ops).PrepareSelectionAsync();
        session.AcceptOverlay!(2);ops.State=ops.State with{ForegroundId=3};
        Assert.Throws<InputBlockedException>(()=>session.Capture());
        Assert.Equal(0,ops.Captures);
    }

    [Fact]
    public async Task ForegroundLossDuringCaptureRejectsTheReturnedFrame()
    {
        var (selection,ops)=Create();
        var session=await new SelectedGameCaptureService(selection,ops).PrepareSelectionAsync();
        session.AcceptOverlay!(2);ops.State=ops.State with{ForegroundId=2};
        ops.OnCapture=()=>{ops.State=ops.State with{ForegroundId=3};return new(40,40,new byte[40*40*3]);};
        Assert.Throws<InputBlockedException>(()=>session.Capture());
        Assert.Equal(1,ops.Captures);
    }

    [Fact]
    public async Task RegisteredOverlayCanRemainForegroundUntilTargetCapture()
    {
        var (selection,ops)=Create();
        var session=await new SelectedGameCaptureService(selection,ops).PrepareSelectionAsync();
        session.AcceptOverlay!(2);ops.State=ops.State with{ForegroundId=2};
        ops.OnCapture=()=>throw new CaptureReachedException();
        Assert.Throws<CaptureReachedException>(()=>session.Capture());
        Assert.Equal(1,ops.Captures);
        Assert.Equal(ops.State.WindowId,ops.CapturedWindow);
    }

    static (GameWindowSelection Selection,FakeOps Ops) Create()
    {
        var ops=new FakeOps();var selection=new GameWindowSelection(()=>[new(ops.State,"取点测试")]);
        selection.Refresh();return(selection,ops);
    }

    sealed class CaptureReachedException:Exception;
    sealed class FakeOps:INativeWindowsOps
    {
        public NativeWindowState State=new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(100,100,40,40,96),new(100,100,140,140),1,true,false,true,new(0,0));
        public int Captures;public nint CapturedWindow;public Func<RgbImage>? OnCapture;
        public NativeWindowState ReadWindow(nint hwnd){Assert.Equal((nint)1,hwnd);return State;}
        public RgbImage CaptureClient(NativeWindowState state){Captures++;CapturedWindow=state.WindowId;return OnCapture!();}
        public void MoveClient(nint hwnd,PixelPoint point)=>throw new InvalidOperationException("取点测试不允许输入");
        public void MouseButton(MouseButton button,bool down)=>throw new InvalidOperationException("取点测试不允许输入");
        public void Wheel(int delta)=>throw new InvalidOperationException("取点测试不允许输入");
        public void Key(ushort key,bool down,bool unicode=false)=>throw new InvalidOperationException("取点测试不允许输入");
        public bool AnyPhysicalKeyDown()=>throw new InvalidOperationException("取点测试不允许查询真实输入");
    }
}
