using System.Text.Json.Nodes;
using MabiAgent.Windows;
using MabiAgent.Workflows;
using Xunit;

namespace MabiAgent.Windows.Tests;

public sealed class WorkbenchNavigationTests
{
    [Fact] public void ZoomSettleIntervalStartsAfterTheSweepFinishes()
    {
        var (session,ops)=Create();using(session)
        {
            var port=session.CreateMaintenancePort("test",new Store());port.Observe();
            var step=JsonNode.Parse("""{"target":"map.canvas","kind":"zoom_max","settle_s":0.2}""")!.AsObject();
            Assert.False(port.TestNavigationStep(step).Completed);Assert.Equal(12,ops.Wheels);
            Assert.False(port.TestNavigationStep(step).Completed);
            Assert.Equal("dispatch",port.LastTestNavigationTrace?.Phase);
            Thread.Sleep(250);
            Assert.True(port.TestNavigationStep(step).Completed);Assert.Equal(12,ops.Wheels);
        }
    }
    [Fact] public void InterruptedZoomSweepKeepsPauseAndDoesNotRetryUnknownInput()
    {
        var (session,ops)=Create();using(session)
        {
            var port=session.CreateMaintenancePort("test",new Store());port.Observe();port.Observe();
            var step=JsonNode.Parse("""{"target":"map.canvas","kind":"zoom_max","settle_s":0}""")!.AsObject();
            Assert.False(port.TestNavigationStep(step).Completed);Assert.Equal(12,ops.Wheels);
            var pixels=ops.Image.CopyPixels();
            for(var y=150;y<780;y++)for(var x=200;x<1050;x++)for(var c=0;c<3;c++)pixels[(y*1280+x)*3+c]=100;
            ops.Image=new(1280,960,pixels);ops.FailAtWheel=13;
            Assert.Throws<IOException>(()=>port.TestNavigationStep(step));Assert.Equal(13,ops.Wheels);
            Assert.True(session.Guard.Paused);Assert.Equal("input_outcome_unknown",session.Guard.PauseReason);
            Assert.Throws<InputBlockedException>(()=>port.TestNavigationStep(step));Assert.Equal(13,ops.Wheels);
        }
    }
    [Fact] public void ZoomSweepUsesFixedInputBatchesWithoutBusinessRevalidation()
    {
        var (session,ops)=Create(dynamicCanvas:true);using(session)
        {
            var port=session.CreateMaintenancePort("test",new Store());port.Observe();port.Observe();
            var step=JsonNode.Parse("""{"target":"map.canvas","kind":"zoom_max","settle_s":0,"next":"portrait"}""")!.AsObject();
            Assert.False(port.TestNavigationStep(step).Completed);Assert.Equal(12,ops.Wheels);
            var pixels=ops.Image.CopyPixels();
            for(var y=150;y<780;y++)for(var x=200;x<1050;x++)for(var c=0;c<3;c++)pixels[(y*1280+x)*3+c]=100;
            var target=session.Routes.Profile.Templates["maint_ferghus_scene_name"];var glyph=target.Image.CopyPixels();
            for(var y=0;y<target.Image.Height;y++)Array.Copy(glyph,y*target.Image.Width*3,pixels,((target.Box.Top+y)*1280+target.Box.Left)*3,target.Image.Width*3);
            var changed=new RgbImage(1280,960,pixels);ops.Image=changed;var shifted=changed.CopyPixels();
            for(var y=0;y<target.Image.Height;y++)
            {
                Array.Clear(shifted,((target.Box.Top+y)*1280+target.Box.Left)*3,target.Image.Width*3);
                Array.Copy(glyph,y*target.Image.Width*3,shifted,((target.Box.Top+y)*1280+target.Box.Left+2)*3,target.Image.Width*3);
            }
            var moved=new RgbImage(1280,960,shifted);
            var rejectAt=ops.Captures+3;
            ops.CaptureFrame=n=>n>=rejectAt?moved:changed;
            var rejected=port.TestNavigationStep(step);
            Assert.Null(rejected.Error);Assert.False(rejected.Completed);Assert.Equal(24,ops.Wheels);
            Assert.NotEqual("reobserve",port.LastTestNavigationTrace?.Phase);Assert.False(session.Guard.Paused);
            ops.CaptureFrame=null;port.Observe();port.Observe();
            Assert.True(port.TestNavigationStep(step).Completed);Assert.Equal(24,ops.Wheels);Assert.Empty(ops.Clicks);
        }
    }
    [Fact] public void SelectedZoomStepUsesSaturationConfirmationWithoutFollowingNextStage()
    {
        var (session,ops)=Create();using(session)
        {
            var port=session.CreateMaintenancePort("test",new Store());port.Observe();port.Observe();
            var step=JsonNode.Parse("""{"target":"map.canvas","kind":"zoom_max","settle_s":0,"next":"portrait"}""")!.AsObject();
            Assert.False(port.TestNavigationStep(step).Completed);Assert.Equal(12,ops.Wheels);
            Assert.True(port.TestNavigationStep(step).Completed);Assert.Empty(ops.Clicks);
            Assert.Equal("maximum_observed",port.LastTestNavigationTrace?.Phase);
        }
    }
    [Fact] public void BoundedSearchCompletionDoesNotDeclareWholeRouteComplete()
    {
        var (session,ops)=Create();using(session)
        {
            var port=session.CreateMaintenancePort("test",new Store());port.Observe();port.Observe();
            var route=JsonNode.Parse("""{"start":"search","stages":{"search":{"search":[{"target":"map.canvas","kind":"scroll","steps":-1,"settle_s":0,"search_only":true},{"target":"map.canvas","kind":"scroll","steps":-1,"settle_s":0,"search_only":true}]}}}""")!.AsObject();
            Assert.False(port.TestNavigationRoute(route).Completed);
            var result=port.TestNavigationRoute(route);Assert.False(result.Completed);Assert.Null(result.Error);Assert.Equal(2,ops.Wheels);
            result=port.TestNavigationRoute(route);Assert.False(result.Completed);Assert.Contains("search_exhausted",result.Error);
        }
    }
    [Fact] public void SelectedSearchIsReportedSeparatelyFromArrival()
    {
        var (session,_)=Create();using(session)
        {
            var port=session.CreateMaintenancePort("test",new Store());port.Observe();port.Observe();
            var step=JsonNode.Parse("""{"target":"map.canvas","kind":"scroll","steps":-1,"settle_s":0,"search_only":true}""")!.AsObject();
            Assert.False(port.TestNavigationStep(step).Completed);Assert.True(port.TestNavigationStep(step).Completed);
            Assert.Equal("search_step_complete",port.LastTestNavigationTrace?.Phase);
        }
    }
    static (ScreenWorkflowSession,Ops) Create(bool dynamicCanvas=false)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"region_map":{"anchors":["scene_bag"],"coordinate_targets":{"map.canvas":[400,300,800,700],"map.npc":[600,400,620,420]}}}""");routes.Profile.Definition.Remove("page_variants");
        var t=routes.Profile.Templates["scene_bag"];var source=t.Image.CopyPixels();var pixels=new byte[1280*960*3];
        for(int y=0;y<t.Image.Height;y++)Array.Copy(source,y*t.Image.Width*3,pixels,((t.Box.Top+y)*1280+t.Box.Left)*3,t.Image.Width*3);
        if(dynamicCanvas)
        {
            routes.Profile.Definition["pages"]!["region_map"]!["coordinate_targets"]!.AsObject().Remove("map.canvas");
            routes.Profile.Definition["pages"]!["region_map"]!["targets"]=new JsonObject{["map.canvas"]="maint_ferghus_scene_name"};
            var canvas=routes.Profile.Templates["maint_ferghus_scene_name"];var glyph=canvas.Image.CopyPixels();
            for(var y=0;y<canvas.Image.Height;y++)Array.Copy(glyph,y*canvas.Image.Width*3,pixels,((canvas.Box.Top+y)*1280+canvas.Box.Left)*3,canvas.Image.Width*3);
        }
        var ops=new Ops(new(1280,960,pixels));var session=new ScreenWorkflowSession(routes,Window(),ops,new Ocr(),new Store());session.Start();return(session,ops);
    }
    static NativeWindowState Window()=>new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,1280,960,96),new(0,0,1280,960),1,true,false,true,new(0,0));
    sealed class Ocr:IOcrEngine{public OcrLine? Recognize(int width,int height,byte[] grayPixels)=>null;}
    sealed class Store:IWorkflowStore{public JsonObject? Read()=>null;public void Flush(JsonObject state){}}
    sealed class Ops(RgbImage image):INativeWindowsOps
    {
        public RgbImage Image=image;public int Captures;public Func<int,RgbImage>? CaptureFrame;
        PixelPoint point;public int Wheels;public int? FailAtWheel;public List<PixelPoint> Clicks{get;}=[];
        public NativeWindowState ReadWindow(nint hwnd)=>Window();public RgbImage CaptureClient(NativeWindowState state){Captures++;return CaptureFrame?.Invoke(Captures)??Image;}
        public void MoveClient(nint hwnd,PixelPoint position)=>point=position;public void MouseButton(MouseButton button,bool down){if(down)Clicks.Add(point);}
        public void Wheel(int delta){Wheels++;if(Wheels==FailAtWheel)throw new IOException("gesture delivery failed");}public void Key(ushort key,bool down,bool unicode=false)=>throw new InvalidOperationException();public bool AnyPhysicalKeyDown()=>false;
    }
}
