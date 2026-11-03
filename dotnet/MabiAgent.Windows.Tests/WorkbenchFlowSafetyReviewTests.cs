using System.Text.Json.Nodes;
using MabiAgent.Workflows;
using Xunit;

namespace MabiAgent.Windows.Tests;

public sealed class WorkbenchFlowSafetyReviewTests
{
    [Fact]
    public void FixedClickKeysTextAndDelayNeedNoScreenshots()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var ops=new ReviewOps(new(1280,960,new byte[1280*960*3]));
        using var session=new ScreenWorkflowSession(routes,ops.State,ops,new ReviewOcr(),new ReviewStore());session.Start();
        var flow=new ScreenWorkbenchFlow(session,JsonNode.Parse("""{"cards":[{"id":"click","action":"click","fixed_box":[10,10,30,30],"fixed_geometry":[1280,960,96]},{"id":"key","action":"key","key_code":32,"key_duration_ms":1},{"id":"text","action":"text","input_text":"測試"},{"id":"delay","action":"delay","delay_ms":0}]}""")!.AsObject());
        for(var i=0;i<4;i++)Assert.NotEqual(WorkbenchFlowState.Failed,flow.Tick().State);
        Assert.Equal(WorkbenchFlowState.Completed,flow.Progress.State);Assert.Equal(0,ops.Captures);Assert.True(flow.HasDispatchedInput);
    }
    [Fact]
    public void DispatchedClickConfirmsDestinationAfterItsOriginalPageConditionDisappears()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"scene.open_bag":"scene_bag"}}}""");routes.Profile.Definition.Remove("page_variants");
        var ops=new ReviewOps(Stamp(routes.Profile,"scene_bag"));using var session=new ScreenWorkflowSession(routes,ops.State,ops,new ReviewOcr(),new ReviewStore());session.Start();session.Observe();var observation=session.Observe();Assert.True(observation.Stable);
        var definition=JsonNode.Parse("""{"cards":[{"id":"click","name":"打开背包","action":"click","page_id":"scene","condition_operation":"page","target":"scene.open_bag","expected_page_id":"bag","timeout_seconds":5}]}""")!.AsObject();
        var time=new ReviewTime();var flow=new ScreenWorkbenchFlow(session,definition,timeProvider:time);Assert.Equal(WorkbenchFlowState.Running,flow.Tick(observation).State);Assert.True(flow.HasDispatchedInput);time.Seconds=1;
        var destination=observation with{View=observation.View with{Page="bag"}};Assert.Equal(WorkbenchFlowState.Completed,flow.Tick(destination).State);
    }
    [Fact]
    public void FalseBranchWithoutAnExitEndsItsPathBeforeFollowingCards()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var ops=new ReviewOps(Stamp(routes.Profile,"scene_bag"));
        using var session=new ScreenWorkflowSession(routes,ops.State,ops,new ReviewOcr(),new ReviewStore());session.Start();
        var flow=new ScreenWorkbenchFlow(session,JsonNode.Parse("""{"cards":[{"id":"branch","name":"判断页面","action":"branch","page_id":"skills","condition_operation":"page"},{"id":"next","name":"后续动作","action":"delay"}]}""")!.AsObject());
        var observation=new UiObservation("frame",0,session.Scope,PageView.Unknown(),true,true);
        var progress=flow.Tick(observation);Assert.Equal(WorkbenchFlowState.Completed,progress.State);Assert.Contains("条件未满足",progress.Message);Assert.Equal(1,progress.Completed);Assert.False(flow.HasDispatchedInput);
    }
    [Fact]
    public void AnUnrecognisedPageCannotWaitForeverBeforeTheStepTimerStarts()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var ops=new ReviewOps(Stamp(routes.Profile,"scene_bag"));
        using var session=new ScreenWorkflowSession(routes,ops.State,ops,new ReviewOcr(),new ReviewStore());session.Start();
        var time=new ReviewTime();var flow=new ScreenWorkbenchFlow(session,JsonNode.Parse("""{"cards":[{"id":"wait","name":"等待场景","action":"wait","page_id":"scene","timeout_seconds":2}]}""")!.AsObject(),timeProvider:time);
        var unknown=new UiObservation("frame",0,session.Scope,PageView.Unknown(),false,true);
        Assert.Equal(WorkbenchFlowState.Running,flow.Tick(unknown).State);time.Seconds=3;
        Assert.Equal(WorkbenchFlowState.Failed,flow.Tick(unknown).State);
    }

    [Theory]
    [InlineData("scene")]
    public void AnimatedPixelsDoNotConfirmAClickWithoutAnExplicitResultProof(string expectedPage)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"scene.open_bag":"scene_bag"}}}""");routes.Profile.Definition.Remove("page_variants");
        var ops=new ReviewOps(Stamp(routes.Profile,"scene_bag"));using var session=new ScreenWorkflowSession(routes,ops.State,ops,new ReviewOcr(),new ReviewStore());session.Start();
        session.Observe();var observation=session.Observe();Assert.True(observation.Stable);
        var definition=JsonNode.Parse("""{"cards":[{"id":"click","name":"打开背包","action":"click","page_id":"scene","target":"scene.open_bag","timeout_seconds":5}]}""")!.AsObject();definition["cards"]![0]!["expected_page_id"]=expectedPage;
        var time=new ReviewTime();var flow=new ScreenWorkbenchFlow(session,definition,timeProvider:time);
        Assert.Equal(WorkbenchFlowState.Running,flow.Tick(observation).State);Assert.True(flow.HasDispatchedInput);time.Seconds=1;
        var onlyAnimation=observation with{ImageSha256="unrelated-scene-animation",CapturedAt=observation.CapturedAt+.2};
        Assert.NotEqual(WorkbenchFlowState.Completed,flow.Tick(onlyAnimation).State);
    }
    [Fact]
    public void AnchorClickWithoutDeclaredResultAdvancesWithoutAnotherObservation()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"scene.open_bag":"scene_bag"}}}""");routes.Profile.Definition.Remove("page_variants");
        var ops=new ReviewOps(Stamp(routes.Profile,"scene_bag"));using var session=new ScreenWorkflowSession(routes,ops.State,ops,new ReviewOcr(),new ReviewStore());session.Start();
        var observation=session.Observe();Assert.True(observation.Stable);var captures=ops.Captures;
        var flow=new ScreenWorkbenchFlow(session,JsonNode.Parse("""{"cards":[{"id":"click","action":"click","page_id":"scene","target":"scene.open_bag"}]}""")!.AsObject());
        Assert.Equal(WorkbenchFlowState.Completed,flow.Tick(observation).State);Assert.True(flow.HasDispatchedInput);Assert.Equal(captures,ops.Captures);
        Assert.Equal(WorkbenchFlowState.Completed,flow.Tick().State);Assert.Equal(captures,ops.Captures);
    }

    static RgbImage Stamp(NavigationProfile profile,string name)
    {
        var template=profile.Templates[name];var pixels=new byte[1280*960*3];var source=template.Image.CopyPixels();
        for(var y=0;y<template.Image.Height;y++)Array.Copy(source,y*template.Image.Width*3,pixels,((template.Box.Top+y)*1280+template.Box.Left)*3,template.Image.Width*3);
        return new(1280,960,pixels);
    }
    sealed class ReviewTime:TimeProvider{public double Seconds;public override DateTimeOffset GetUtcNow()=>DateTimeOffset.UnixEpoch.AddSeconds(Seconds);}
    sealed class ReviewStore:IWorkflowStore{JsonObject? value;public JsonObject? Read()=>value;public void Flush(JsonObject state)=>value=(JsonObject)state.DeepClone();}
    sealed class ReviewOcr:IOcrEngine{public OcrLine? Recognize(int width,int height,byte[] grayPixels)=>null;}
    sealed class ReviewOps(RgbImage image):INativeWindowsOps
    {
        public int Captures;
        public NativeWindowState State=new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,1280,960,96),new(0,0,1280,960),1,true,false,true,new(0,0));
        public NativeWindowState ReadWindow(nint hwnd)=>State;
        public RgbImage CaptureClient(NativeWindowState state){Captures++;return image;}
        public void MoveClient(nint hwnd,PixelPoint point){}
        public void MouseButton(MouseButton button,bool down){}
        public void Wheel(int delta){}
        public void Key(ushort key,bool down,bool unicode=false){}
        public bool AnyPhysicalKeyDown()=>false;
    }
}
