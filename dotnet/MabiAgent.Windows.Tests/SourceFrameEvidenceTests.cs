using MabiAgent.Windows;
using System.Text.Json.Nodes;
using Xunit;

namespace MabiAgent.Windows.Tests;

public class SourceFrameEvidenceTests
{
    static readonly SessionScope Scope=new("frames","account","character");
    static PageView View=>new("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(5,5)}},[]);
    static UiObservation Review(ClientCapture c)=>new(c.Image.Sha256,c.CapturedAt,Scope,View,true,true,c.Frame,c.ResourceRevision);

    [Fact] public void SlowLegacyCaptureIsNotMadeFreshAtCompletion()
    {
        double now=1;var ops=new LegacyOps{DuringCapture=()=>now=2};
        using var guard=new ForegroundInputGuard(ops.State,Scope,ops,Review,()=>now);
        guard.Start();Assert.Throws<InputBlockedException>(()=>guard.ObserveTarget("scene.open_bag"));Assert.Equal(0,ops.Inputs);
        now=3;ops.DuringCapture=()=>now=3.2;var capture=guard.Capture();Assert.Equal(3,capture.CapturedAt);Assert.Equal(3,capture.Frame!.SourceTime);
    }

    [Theory] [InlineData(ClientFramePurpose.Cached)] [InlineData(ClientFramePurpose.Preview)]
    public void CachedAndPreviewFramesCannotAuthorizeInput(ClientFramePurpose purpose)
    {
        var ops=new FrameOps{Purpose=purpose};using var guard=new ForegroundInputGuard(ops.State,Scope,ops,Review,()=>ops.Now);
        guard.Start();Assert.Throws<InputBlockedException>(()=>guard.ObserveTarget("scene.open_bag"));Assert.Equal(0,ops.Inputs);
        var observer=new ScreenObserver(_=>Scope,_=>View);
        Assert.Throws<InputBlockedException>(()=>observer.Observe(new(ops.State,ops.Image,1,new("PrintWindow",1,1,1,purpose))));
    }

    [Fact] public void DuplicateSourceSequenceAndSameSourceTimeAreRejected()
    {
        var ops=new FrameOps();using var guard=new ForegroundInputGuard(ops.State,Scope,ops,Review,()=>ops.Now);
        guard.Start();guard.Capture();ops.Now=1.1;
        Assert.Throws<InputBlockedException>(()=>guard.Capture());
        ops.Sequence=2;ops.Now=1;Assert.Throws<InputBlockedException>(()=>guard.Capture());Assert.Equal(0,ops.Inputs);
    }

    [Fact] public void BackendGenerationAndResourceChangeInvalidateIssuedTarget()
    {
        var ops=new FrameOps();var revision="r1";
        using var guard=new ForegroundInputGuard(ops.State,Scope,ops,Review,()=>ops.Now,resourceRevision:()=>revision);
        guard.Start();var proof=guard.ObserveTarget("scene.open_bag");ops.Generation++;ops.Sequence+=2;ops.Now+=1;guard.Capture();
        Assert.Throws<InputBlockedException>(()=>guard.Click(proof));Assert.Equal(0,ops.Inputs);
        ops.Sequence+=2;ops.Now+=1;proof=guard.ObserveTarget("scene.open_bag");revision="r2";ops.Sequence+=2;ops.Now+=1;guard.Capture();
        Assert.Throws<InputBlockedException>(()=>guard.Click(proof));Assert.Equal(0,ops.Inputs);
    }

    [Fact] public void GeometryGenerationInvalidatesTargetEvenAfterDpiReturnsToOriginal()
    {
        var ops=new FrameOps();using var guard=new ForegroundInputGuard(ops.State,Scope,ops,Review,()=>ops.Now);
        guard.Start();var proof=guard.ObserveTarget("scene.open_bag");
        ops.State=ops.State with{Geometry=ops.State.Geometry with{Dpi=192}};ops.Sequence++;ops.Now+=.1;guard.Capture();
        ops.State=ops.State with{Geometry=ops.State.Geometry with{Dpi=96}};ops.Sequence++;ops.Now+=.1;
        Assert.Throws<InputBlockedException>(()=>guard.Click(proof));Assert.Equal(0,ops.Inputs);
        var fresh=guard.ObserveTarget("scene.open_bag");Assert.True(fresh.Frame!.GeometryGeneration>proof.Frame!.GeometryGeneration);
    }

    [Fact] public void FirstCurrentFrameAndBackgroundFocusChangesDoNotRequireHistoricalStability()
    {
        var ops=new FrameOps();var observer=new ScreenObserver(_=>Scope,_=>View);
        ClientCapture Frame(long sequence,double time,long generation=1,string revision="r1",NativeWindowState? state=null)=>
            new(state??ops.State,ops.Image,time,new("PrintWindow",generation,sequence,time),revision);
        Assert.True(observer.Observe(Frame(1,1)).Stable);
        Assert.True(observer.Observe(Frame(1,1.1)).Stable);
        Assert.True(observer.Observe(Frame(2,1.1)).Stable);
        Assert.True(observer.Observe(Frame(3,1.2)).Stable);
        Assert.True(observer.Observe(Frame(4,1.3,revision:"r2")).Stable);
        Assert.True(observer.Observe(Frame(5,1.4,revision:"r2")).Stable);
        Assert.True(observer.Observe(Frame(1,1.5,generation:2,revision:"r2")).Stable);
        Assert.True(observer.Observe(Frame(2,1.55,generation:2,revision:"r2",state:ops.State with{ForegroundId=999})).Stable);
        var moved=ops.State with{Geometry=ops.State.Geometry with{X=300}};
        Assert.True(observer.Observe(Frame(2,1.6,generation:2,revision:"r2",state:moved)).Stable);
        Assert.True(observer.Observe(Frame(3,1.7,generation:2,revision:"r2",state:moved)).Stable);
    }

    [Fact] public void CurrentUnknownModalAndUnavailableWindowRemainUnusable()
    {
        var ops=new FrameOps();var page=View;var observer=new ScreenObserver(_=>Scope,_=>page);
        page=PageView.Unknown();Assert.False(observer.Observe(new(ops.State,ops.Image,1)).Stable);
        page=View with{Modal=true};Assert.False(observer.Observe(new(ops.State,ops.Image,2)).Stable);
        page=View;Assert.False(observer.Observe(new(ops.State with{Minimized=true},ops.Image,3)).Stable);
    }
    [Fact] public void IdentityChangeDuringRecognitionRejectsCurrentFrame()
    {
        var ops=new FrameOps();SessionScope? current=Scope;
        var observer=new ScreenObserver(_=>current,_=>{current=null;return View;});
        Assert.Throws<InputBlockedException>(()=>observer.Observe(new(ops.State,ops.Image,1)));
    }
    [Fact] public void ProfileRevisionChangesOncePerLoadedSnapshotAndMatcherCacheIsIsolated()
    {
        var root=RecognitionTests.Root();var first=NavigationProfile.Load(root,"config/navigation_ui.1280x960.tw.json");
        var definition=(JsonObject)first.Definition.DeepClone();definition["revision_test"]=2;
        var second=NavigationProfile.FromDefinition(root,definition);Assert.NotEqual(first.Revision,second.Revision);
        var image=new RgbImage(1280,960,new byte[1280*960*3]);using var before=new TemplateMatcher(first);using var after=new TemplateMatcher(second);
        before.Match(image,"scene_bag");before.Match(image,"scene_bag");Assert.Equal(1,before.CacheStatistics.ResultHits);
        Assert.Equal(0,after.CacheStatistics.ResultHits);after.Match(image,"scene_bag");Assert.Equal(0,after.CacheStatistics.ResultHits);
        definition["revision_test"]=3;Assert.Equal(2,second.Definition["revision_test"]!.GetValue<int>());
    }

    [Fact] public void RouteSnapshotRevisionIncludesRecognitionConfigurationAndKeepsPreviousSnapshot()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var prior=routes.Snapshot();
        routes.Configuration["revision_test"]=2;var changed=routes.Snapshot();
        Assert.NotEqual(prior.Revision,changed.Revision);Assert.Null(prior.Configuration["revision_test"]);
        routes.Profile.Definition["revision_test"]=3;var newAnchors=routes.Snapshot();
        Assert.NotEqual(changed.Revision,newAnchors.Revision);Assert.Null(changed.Profile.Definition["revision_test"]);
    }

    class LegacyOps:INativeWindowsOps
    {
        public NativeWindowState State=new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,100,100,96),new(0,0,100,100),1,true,false,true,new(0,0));
        public RgbImage Image=new(100,100,new byte[30000]);public Action? DuringCapture;public int Inputs;
        public NativeWindowState ReadWindow(nint hwnd)=>State;
        public RgbImage CaptureClient(NativeWindowState state){DuringCapture?.Invoke();return Image;}
        public void MoveClient(nint hwnd,PixelPoint point)=>Inputs++;
        public void MouseButton(MouseButton button,bool down)=>Inputs++;
        public void Wheel(int delta)=>Inputs++;
        public void Key(ushort key,bool down,bool unicode=false)=>Inputs++;
        public bool AnyPhysicalKeyDown()=>false;
    }
    sealed class FrameOps:LegacyOps,ICurrentClientFrameSource
    {
        public double Now=1;public long Sequence=1,Generation=1;public ClientFramePurpose Purpose=ClientFramePurpose.Current;
        public CurrentClientFrame CaptureCurrentClientFrame(NativeWindowState state)=>new(CaptureClient(state),new("PrintWindow",Generation,Sequence,Now,Purpose));
    }
}
