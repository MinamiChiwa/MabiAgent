using System.Text.Json.Nodes;
using MabiAgent.Windows;
using MabiAgent.Workflows;
using Xunit;

namespace MabiAgent.Windows.Tests;

public sealed class WorkbenchNavigationTests
{
    [Theory]
    [InlineData("storage","map.inn","map.storage_box")]
    [InlineData("repair_quote","map.blacksmith","map.repair_npc")]
    [InlineData("purchase_shop","map.general_store","map.shop_npc")]
    public void SameNamedShopInAnotherMapSwitchesToTirBeforeCentering(string destination,string shop,string npc)
    {
        var (session,ops)=CreateNamedMap();using(session)
        {
            var route=MapRoute(session,destination);var port=session.CreateMaintenancePort("named-map",new Store());
            Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal("map.world",port.LastTestNavigationTrace?.Target);
            Assert.Equal(0,ops.Wheels);
            ops.Image=MapImage(session,"maint_tir_town");
            Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal("map.tir_town",port.LastTestNavigationTrace?.Target);
            var sent=ops.Clicks.Count;
            Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal(sent,ops.Clicks.Count);Assert.Equal(0,ops.Wheels);
            ops.Image=MapImage(session,"maint_tir_region_header");
            Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal(shop,port.LastTestNavigationTrace?.Target);
            Assert.Equal(0,ops.Wheels);
            Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal("map.canvas",port.LastTestNavigationTrace?.Target);Assert.Equal(4,ops.Wheels);
            Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal(shop,port.LastTestNavigationTrace?.Target);
            Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal(npc,port.LastTestNavigationTrace?.Target);
        }
    }
    [Theory]
    [InlineData("storage","map.inn")]
    [InlineData("repair_quote","map.blacksmith")]
    [InlineData("purchase_shop","map.general_store")]
    public void ConfirmedTirMapCentersTheVisibleShopDirectly(string destination,string shop)
    {
        var (session,ops)=CreateNamedMap();using(session)
        {
            ops.Image=MapImage(session,"maint_tir_region_header");
            var port=session.CreateMaintenancePort("already-tir",new Store());
            Assert.Null(port.TestNavigationRoute(MapRoute(session,destination)).Error);
            Assert.Equal(shop,port.LastTestNavigationTrace?.Target);Assert.Equal(0,ops.Wheels);
        }
    }
    [Theory]
    [InlineData("storage","locate")][InlineData("repair_quote","locate")][InlineData("purchase_shop","locate")]
    [InlineData("storage","world")][InlineData("repair_quote","world")][InlineData("purchase_shop","world")]
    public void FailedTirSelectionEndsItsWaitWithoutUsingAnotherMapsShop(string destination,string stage)
    {
        var (session,ops)=CreateNamedMap();using(session)
        {
            ops.Image=MapImage(session,"maint_tir_town");var route=MapRoute(session,destination);route["start"]=stage;
            foreach(var definition in route["stages"]!.AsObject())
            foreach(var step in definition.Value?["choices"]?.AsArray()??new JsonArray())
                if(step?["target"]?.GetValue<string>()=="map.tir_town")step["timeout_s"]=0;
            var port=session.CreateMaintenancePort("tir-timeout",new Store());
            Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal("map.tir_town",port.LastTestNavigationTrace?.Target);
            var sent=ops.Clicks.Count;
            Assert.Equal("navigation_result_not_observed:map.tir_town",port.TestNavigationRoute(route).Error);
            Assert.Equal(sent,ops.Clicks.Count);Assert.Equal(0,ops.Wheels);Assert.Null(session.CurrentNavigationId);Assert.False(session.Guard.Paused);
        }
    }
    [Theory]
    [InlineData("locate")][InlineData("local")][InlineData("local_wide")][InlineData("label_search")][InlineData("label_search_final")]
    public void EveryShopSearchStageRequiresTheTirHeader(string stage)
    {
        var (session,ops)=CreateNamedMap();using(session)
        {
            var route=MapRoute(session,"storage");route["start"]=stage;
            var port=session.CreateMaintenancePort("wrong-map-stage",new Store());port.TestNavigationRoute(route);
            Assert.NotEqual("map.inn",port.LastTestNavigationTrace?.Target);
            Assert.DoesNotContain(new PixelPoint(501,401),ops.Clicks);
        }
    }
    [Theory][InlineData("storage","map.inn","map.storage_box")][InlineData("repair_quote","map.blacksmith","map.repair_npc")][InlineData("purchase_shop","map.general_store","map.shop_npc")]
    public void SharedMapZoomRecentersTheShopBeforeSelectingItsNpc(string destination,string shop,string npc)
    {
        var (session,ops)=CreateNamedMap();using(session)
        {
            ops.Image=MapImage(session,"maint_tir_region_header");var route=MapRoute(session,destination);route["start"]="zoom";
            var port=session.CreateMaintenancePort("zoom-center",new Store());
            Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal(4,ops.Wheels);
            Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal(shop,port.LastTestNavigationTrace?.Target);Assert.Equal("zoom_recenter",port.LastTestNavigationTrace?.Phase);
            var sent=ops.Clicks.Count;Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal(npc,port.LastTestNavigationTrace?.Target);
            Assert.Equal(4,ops.Wheels);Assert.Equal(sent+1,ops.Clicks.Count);
        }
    }
    [Fact]
    public void ActualFailedMapZoomsOutToReacquireTheInnAndBoundsMissingLabels()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        var ops=new Ops(LoadMap("tir-inn-lost-after-zoom-current.png"));using var session=new ScreenWorkflowSession(routes,Window(),ops,new Ocr(),new Store());session.Start();
        var route=MapRoute(session,"storage");route["start"]="portrait";var port=session.CreateMaintenancePort("lost-inn",new Store());
        Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal(4,ops.Wheels);Assert.Equal("map.canvas",port.LastTestNavigationTrace?.Target);
        // No label appears in this replay: the declared searches must finish.
        NavigationTestProgress result=new(false,null);
        for(var i=0;i<5&&result.Error is null;i++)result=port.TestNavigationRoute(route);
        Assert.Equal("navigation_search_exhausted:workbench_route",result.Error);Assert.Equal(16,ops.Wheels);Assert.Empty(ops.Clicks);Assert.False(session.Guard.Paused);
    }
    [Fact]
    public void MapRecoveryResumesOnTheVisibleInnAndLimitsRepeatedLostCenters()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        var lost=LoadMap("tir-inn-lost-after-zoom-current.png");var ops=new Ops(lost);using var session=new ScreenWorkflowSession(routes,Window(),ops,new Ocr(),new Store());session.Start();
        var route=MapRoute(session,"storage");route["start"]="portrait";var port=session.CreateMaintenancePort("recover-inn",new Store());
        Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal(4,ops.Wheels);
        for(var attempt=0;attempt<2;attempt++)
        {
            ops.Image=LoadMap("tir-inn-wide-current.png");Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal("map.inn",port.LastTestNavigationTrace?.Target);
            Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal("map.canvas",port.LastTestNavigationTrace?.Target);
            ops.Image=lost;
            var result=port.TestNavigationRoute(route);
            if(attempt==0)Assert.Null(result.Error);else Assert.Equal("map_center_recovery_exhausted",result.Error);
        }
        Assert.False(session.Guard.Paused);Assert.Null(session.CurrentNavigationId);
    }
    [Fact] public void VisibleStorageBoxContinuesWhenTheInnLabelIsCovered()
    {
        var (session,ops)=CreateNamedMap();using(session)
        {
            ops.Image=MapImage(session,"maint_tir_region_header");var route=MapRoute(session,"storage");route["start"]="zoom";
            session.Routes.Profile.Definition["pages"]!["region_map"]!["coordinate_targets"]!.AsObject().Remove("map.inn");
            var port=session.CreateMaintenancePort("visible-box",new Store());Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal(4,ops.Wheels);
            Assert.Null(port.TestNavigationRoute(route).Error);Assert.Equal("map.storage_box",port.LastTestNavigationTrace?.Target);Assert.Equal(4,ops.Wheels);Assert.Single(ops.Clicks);
        }
    }
    static RgbImage LoadMap(string fixture)
    {
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(RecognitionTests.Root(),"tests/fixtures/production",fixture));using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);return new(rgb.Width,rgb.Height,pixels);
    }
    static JsonObject MapRoute(ScreenWorkflowSession session,string destination)
    {
        var route=session.Routes.Configuration["maintenance"]!["navigation"]![destination]!["region_map"]!.DeepClone().AsObject();
        foreach(var stage in route["stages"]!.AsObject())
        foreach(var step in new[]{"choices","search"}.SelectMany(k=>stage.Value?[k]?.AsArray()??new JsonArray()))step!["settle_s"]=0;
        return route;
    }
    static (ScreenWorkflowSession,Ops) CreateNamedMap()
    {
        var (session,ops)=Create();
        session.Routes.Profile.Definition["pages"]=JsonNode.Parse("""{"region_map":{"anchors":["scene_bag"],"targets":{"map.current_is_tir":"maint_tir_region_header","map.tir_town":"maint_tir_town"},"coordinate_targets":{"map.canvas":[400,300,800,700],"map.world":[40,25,135,54],"map.current_region":[200,25,290,54],"map.inn":[500,400,502,402],"map.storage_box":[600,420,602,422],"map.blacksmith":[700,420,702,422],"map.repair_npc":[740,420,742,422],"map.general_store":[780,420,782,422],"map.shop_npc":[820,420,822,422]}}}""");
        return(session,ops);
    }
    static RgbImage MapImage(ScreenWorkflowSession session,string anchor)
    {
        var pixels=new byte[1280*960*3];
        foreach(var name in new[]{"scene_bag",anchor})
        {
            var template=session.Routes.Profile.Templates[name];var source=template.Image.CopyPixels();
            for(var y=0;y<template.Image.Height;y++)Array.Copy(source,y*template.Image.Width*3,pixels,((template.Box.Top+y)*1280+template.Box.Left)*3,template.Image.Width*3);
        }
        return new(1280,960,pixels);
    }
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
