using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Windows.Tests;
public sealed class ScreenWorkflowSessionTests
{
    private static readonly MabiAgent.Core.CharacterSlotIdentity TestWho=new("account","character","抓包名");
    [Fact] public void DelayedBagCloseRetriesAreBoundedAndSuccessClearsNavigation()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"repair_tools":{"anchors":["scene_bag"],"targets":{"bag.close":"scene_bag"}},"scene":{"anchors":["maint_scene_map"]}}""");
        routes.Configuration["maintenance"]!["exit_navigation"]!["repair_tools"]=JsonNode.Parse("""{"target":"bag.close","expected_page":"scene","timeout_s":0,"settle_s":0,"retry_delay_s":0}""");
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"));using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();var port=session.CreateMaintenancePort("close-retry",new MemoryStore());
        for(var i=0;i<7;i++){port.Observe();Assert.False(port.ReleaseNavigation());}
        Assert.Equal(4,native.Keys.Count);port.Observe();Assert.Throws<InputBlockedException>(()=>port.ReleaseNavigation());port.Observe();Assert.Throws<InputBlockedException>(()=>port.ReleaseNavigation());Assert.Equal(4,native.Keys.Count);
        native.CurrentImage=Stamp(routes.Profile,"maint_scene_map");port.Observe();Assert.True(port.ReleaseNavigation());Assert.Null(port.LastTestNavigationTrace);
    }
    [Fact] public void UnknownSlotAndLegacyReloginIntentDoNotBlockOrdinarySceneInput()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"scene.open_bag":"scene_bag"}}}""");
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"));var legacy=new MemoryStore(new JsonObject{["status"]="running",["character"]="旧姓名",["pending"]=new JsonObject{["kind"]="relogin_enter"}});
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());
        session.ConfigureCharacterSelection(()=>TestWho,(_,_)=>throw new InvalidOperationException("普通流程不查询槽位"),(_,_)=>throw new InvalidOperationException("普通流程不写关联"));
        session.ConfigureRelogin(legacy);session.Start();Assert.Equal("scene",session.Observe().View.Page);session.NavigateTarget("scene.open_bag");
        Assert.Equal(new ushort[]{73},native.Keys);Assert.False(session.Guard.Paused);Assert.NotNull(legacy.Read()!["pending"]);
    }
    private static RgbImage CharacterSelectionFrame(ScreenRoutes routes,int selected)
    {
        var image=Stamp(routes.Profile,"relogin.character_title","relogin.start_game");var pixels=image.CopyPixels();var i=selected-1;var left=new[]{87,368,649,930}[i%4];var top=i<4?117:565;
        for(var y=top+15;y<Math.Min(top+380,960);y++)for(var x=left;x<left+4;x++){var p=(y*1280+x)*3;pixels[p]=220;pixels[p+1]=180;pixels[p+2]=80;}
        return new(1280,960,pixels);
    }
    [Fact] public void ReloginLearnsAccountSlotBeforeEnteringAndNeverReplaysEntry()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var clicks=new List<PixelPoint>();var native=new NoInputOps(CharacterSelectionFrame(routes,3),clicks);var store=new MemoryStore();int? learned=null;
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.ConfigureCharacterSelection(()=>TestWho,(_,_)=>learned,(who,slot)=>{Assert.Equal(TestWho,who);learned=slot;});session.Start();var relogin=session.ConfigureRelogin(store);relogin.BeginCurrent(true,true);
        relogin.Tick(false);relogin.Tick(false);Assert.Equal(3,learned);Assert.Equal(3,relogin.CharacterSlot);Assert.Empty(clicks);Assert.Empty(native.Keys);
        relogin.Tick(false);var sent=clicks.Count+native.Keys.Count;Assert.Equal(1,sent);Assert.Equal("sync",relogin.Snapshot()["phase"]!.GetValue<string>());Assert.NotNull(store.Read()!["pending"]);
        relogin.Tick(false);Assert.Equal(sent,clicks.Count+native.Keys.Count);Assert.Equal("running",relogin.Snapshot()["status"]!.GetValue<string>());
    }
    [Fact] public void ReloginReassociatesCurrentSelectionEvenWhenSlotWasKnown()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var clicks=new List<PixelPoint>();var native=new NoInputOps(CharacterSelectionFrame(routes,3),clicks);
        var associated=0;using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.ConfigureCharacterSelection(()=>TestWho,(_,_)=>3,(_,slot)=>{Assert.Equal(3,slot);associated++;});session.Start();var relogin=session.ConfigureRelogin(new MemoryStore());relogin.BeginCurrent(true,true);
        relogin.Tick(false);relogin.Tick(false);Assert.Equal(1,associated);Assert.Equal(3,relogin.CharacterSlot);Assert.Equal("enter",relogin.Snapshot()["phase"]!.GetValue<string>());Assert.Empty(clicks);Assert.Empty(native.Keys);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void ReloginRejectsSlotConflictOrForeignPacketBeforeEntering(bool slotConflict)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var clicks=new List<PixelPoint>();var native=new NoInputOps(CharacterSelectionFrame(routes,3),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.ConfigureCharacterSelection(()=>slotConflict?TestWho:new("other","character","抓包名"),(_,_)=>null,(_,_)=>throw new InvalidDataException("顺序冲突"));session.Start();var relogin=session.ConfigureRelogin(new MemoryStore());relogin.Begin(TestWho,true,true);
        relogin.Tick(false);relogin.Tick(false);Assert.Equal("blocked",relogin.Snapshot()["status"]!.GetValue<string>());Assert.Empty(clicks);Assert.Empty(native.Keys);Assert.Null(relogin.Snapshot()["pending"]);
    }
    [Fact] public void CurrentSelectedCardUsesSlotWithoutNameOcr()
    {
        var root=RecognitionTests.Root();using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/screen_ui/relogin-third-selected-current.jpg"));using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        using var session=new ScreenWorkflowSession(ScreenRoutes.Load(root),Window(),new NoInputOps(frame),new EmptyOcr(),new MemoryStore());
        Assert.Equal(3,session.ConfigureRelogin(new MemoryStore()).SelectedSlot(new(Window(),frame,1)));
    }
    [Fact] public void NewsNoticeUsesStableTabAndEscapeWithoutTouchingBanner()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());Assert.DoesNotContain("news_brand",routes.Configuration["notifications"]!["flows"]!["news"]!["anchors"]!.AsArray().Select(v=>v!.GetValue<string>()));
        var native=new NoInputOps(Stamp(routes.Profile,"notification.news_tabs"));using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        Assert.Equal("notification_news",session.Observe().View.Page);Assert.True(session.DismissNotice());Assert.Equal(new ushort[]{27},native.Keys);
    }
    [Fact] public void SurplusMissingItemSearchIsBoundedAndNeverConfirmsSale()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Configuration["maintenance"]!["surplus"]!["selection_scroll_delay_s"]=0;
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"bag":{"anchors":["scene_bag"],"targets":{}}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var store=new MemoryStore(new JsonObject{["status"]="running",["phase"]="select",["item_id"]=1L,["name"]="missing",["amount"]=1L});var port=session.CreateSurplusPort("bounded-find",store,_=>300);
        for(var i=0;i<25;i++)port.Tick();Assert.Equal("plan",port.Snapshot()["phase"]!.GetValue<string>());Assert.Null(port.Snapshot()["pending"]);Assert.Empty(clicks);Assert.Empty(native.Keys);Assert.Equal(288,native.WheelDeltas.Count);Assert.Equal(-120,native.WheelDeltas[0]);Assert.Equal(120,native.WheelDeltas[^1]);
    }
    [Theory][InlineData("gathering_soft_log",4744486152423685L)][InlineData("gathering_hard_log",4688397999765803L)]
    public void LogUsesUserCorrectedArmoredTreeRouteAndObserver(string key,long item)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var config=routes.Configuration;
        var soft=config["gathering_routes"]![key]!;var armored=config["gathering_routes"]!["gathering_high_log_plus"]!;
        Assert.Equal("甲胄樹",soft["node"]!.GetValue<string>());Assert.Equal(item,soft["target_item_id"]!.GetValue<long>());
        Assert.True(JsonNode.DeepEquals(armored["navigation"],soft["navigation"]));
        Assert.True(JsonNode.DeepEquals(config["gathering_observer"]!["routes"]!["gathering_high_log_plus"],config["gathering_observer"]!["routes"]![key]));
        var fallback=Assert.Single(soft["fallback_sources"]!.AsArray())!;Assert.Equal("成材樹",fallback["node"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(config["gathering_routes"]!["gathering_high_log"]!["navigation"],fallback["navigation"]));
        Assert.Equal("尖葉樹",config["gathering_routes"]!["gathering_sap"]!["node"]!.GetValue<string>());
    }
    [Theory][InlineData("gathering_soft_log")][InlineData("gathering_hard_log")]
    public void MissingArmoredTreeFallsBackOnlyAfterBoundedSearchAndKeepsMaterialIdentity(string key)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        var native=new NoInputOps(Stamp(routes.Profile,"preset_logging_title"),[]);var clicks=new List<PixelPoint>();
        native=new NoInputOps(Stamp(routes.Profile,"preset_logging_title"),clicks);var journal=new MemoryStore();
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),journal);session.Start();
        var route=new RuntimeCatalog(routes.Configuration).Gathering[key];var port=session.CreateGatheringPort(key,"fallback",new MemoryStore());
        for(var i=0;i<12;i++){port.ObserveOnly(route,"fallback");port.Navigate(route);}
        Assert.Null(journal.Read()?["ports"]?["gathering:fallback"]?["source_fallback_index"]);
        port.ObserveOnly(route,"fallback");port.Navigate(route);Assert.Equal(0,journal.Read()!["ports"]!["gathering:fallback"]!["source_fallback_index"]!.GetValue<int>());
        native.CurrentImage=Stamp(routes.Profile,"preset_logging_title","preset_catalog_point_logging_03_node");
        port.ObserveOnly(route,"fallback");port.Navigate(route);Assert.Single(clicks);
        native.CurrentImage=Stamp(routes.Profile,"preset_catalog_point_logging_03_name","preset_catalog_point_logging_03_find");
        var frame=port.ObserveOnly(route,"fallback");Assert.Equal(key,frame.RouteKey);Assert.True(frame.StartAvailable);
        Assert.DoesNotContain(native.Keys,k=>k==32);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void LockedArmoredRowFallsBackAfterNavigationTimeoutButNeverAfterPendingStart(bool pending)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Configuration["navigation_retry"]!["timeout_s"]=.001;
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"preset_logging_title","preset_catalog_point_logging_04_node"),clicks);var journal=new MemoryStore();
        var store=new MemoryStore(pending?new JsonObject{["pending"]=new JsonObject{["kind"]="gathering"}}:null);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),journal);session.Start();var route=new RuntimeCatalog(routes.Configuration).Gathering["gathering_soft_log"];var port=session.CreateGatheringPort(route.Key,"locked",store);
        port.ObserveOnly(route,"locked");port.Navigate(route);Thread.Sleep(10);port.ObserveOnly(route,"locked");
        if(pending)Assert.Throws<InputBlockedException>(()=>port.Navigate(route));else port.Navigate(route);
        Assert.Equal(!pending,journal.Read()?["ports"]?["gathering:locked"]?["source_fallback_index"] is not null);Assert.Single(clicks);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void DecompositionUnknownNeverReplaysAndRequiresBothFreshPacketBalances(bool known)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"));
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var stock=new MabiAgent.Core.InventorySnapshot(session.Scope.SessionId);
        stock.Set(new(985189850,known?9:null,session.Scope.SessionId,"packet_capture","new-input",true));
        stock.Set(new(4558467832867117L,known?5:null,session.Scope.SessionId,"packet_capture","new-output",true));
        var store=new MemoryStore(new JsonObject{["status"]="running",["phase"]="result",["scope"]=session.Scope.SessionId,["amount"]=1L,["input_before"]=10L,["output_before"]=4L,["input_version"]="old-input",["output_version"]="old-output",["pending"]=new JsonObject{["kind"]="decompose"}});
        var port=new ScreenDecompositionPort(session,"restore-conversion",store,()=>stock);Assert.False(port.Request(1));
        Assert.Equal(known?"complete":"unknown",port.Tick());Assert.Empty(native.Keys);
        Assert.Equal(!known,port.Snapshot()["pending"] is not null);
    }
    [Fact] public void PrivateTheaterDoesNotTreatBagLabelBehindUnknownOverlayAsReady()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=new JsonObject();
        var native=new NoInputOps(Stamp(routes.Profile,"bag_tools_label"));
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenPrivateTheaterRefresh(session,()=>now);refresh.Start(now);
        Assert.False(refresh.Tick(now));Assert.Equal("open_bag",refresh.CurrentStep);Assert.Empty(native.Keys);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void SurplusRestartResumesOnlyUnsentNavigation(bool pending)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{}}}""");
        var store=new MemoryStore(new JsonObject{["status"]="running",["phase"]="input",["deadline"]=1d,["pending"]=pending?new JsonObject{["kind"]="sale"}:null});
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(Stamp(routes.Profile,"scene_bag")),new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateSurplusPort("resume-sale",store,_=>1);
        Assert.Equal(pending?"unknown":"running",port.Tick());
        Assert.Equal(pending?"input":"select",port.Snapshot()["phase"]!.GetValue<string>());
    }
    [Theory][InlineData(299,300,0)][InlineData(300,300,0)][InlineData(500,300,200)][InlineData(500,450,50)]
    public void SurplusPlansOnlyPacketStockAboveEffectiveReserve(long stock,long keep,long sale)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(),new EmptyOcr(),new MemoryStore());
        var inventory=new MabiAgent.Core.InventorySnapshot(session.Scope.SessionId);inventory.Set(new(198875762,stock,session.Scope.SessionId,"packet_capture","reserve-test",true));
        var store=new MemoryStore();var port=session.CreateSurplusPort("floor-plan",store,_=>keep,()=>inventory);port.Begin();
        Assert.Equal(sale==0?"complete":"running",port.Tick());Assert.Null(port.Snapshot()["pending"]);
        if(sale>0){Assert.Equal(sale,port.Snapshot()["amount"]!.GetValue<long>());Assert.Equal(keep,port.Snapshot()["minimum_keep"]!.GetValue<long>());}
    }
    [Theory][InlineData(483,183)][InlineData(300,0)]
    public void SurplusPlansVerifiedDecompositionInputByNameAboveDefaultFloor(long stock,long sale)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(),new EmptyOcr(),new MemoryStore());
        var inventory=new MabiAgent.Core.InventorySnapshot(session.Scope.SessionId);inventory.Set(new(985189850,stock,session.Scope.SessionId,"packet_capture","branch-v1",true));
        var store=new MemoryStore();var port=session.CreateSurplusPort("branch-floor",store,_=>300,()=>inventory);port.Begin();
        Assert.Equal(sale==0?"complete":"running",port.Tick());Assert.Null(port.Snapshot()["pending"]);
        if(sale>0){Assert.Equal("樹枝",port.Snapshot()["name"]!.GetValue<string>());Assert.Equal(sale,port.Snapshot()["amount"]!.GetValue<long>());Assert.Equal(300,port.Snapshot()["minimum_keep"]!.GetValue<long>());}
    }
    [Fact] public void SurplusSelectionLeavesToolsBeforeWaitingForItemDetail()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"repair_tools":{"anchors":["scene_bag"],"targets":{}}}""");
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"));
        var store=new MemoryStore(new JsonObject{["status"]="running",["phase"]="select",["item_id"]=1L,["name"]="ore",["amount"]=1L,["deadline"]=(DateTimeOffset.UtcNow-DateTimeOffset.UnixEpoch).TotalSeconds+60});
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateSurplusPort("tools-sale",store,_=>1);Assert.Equal("running",port.Tick());Assert.Equal(new ushort[]{83},native.Keys);
        port.Tick();Assert.Single(native.Keys);Assert.Null(store.Read()!["pending"]);
    }
    [Fact] public void RepeatedSurplusPassSwitchesToolsTabOncePerPass()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"repair_tools":{"anchors":["scene_bag"],"targets":{}}}""");
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"));using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var inventory=new MabiAgent.Core.InventorySnapshot(session.Scope.SessionId);inventory.Set(new(198875762,378,session.Scope.SessionId,"packet_capture","current",true));
        var store=new MemoryStore();var port=session.CreateSurplusPort("repeat-tools-sale",store,_=>300,()=>inventory);
        for(var pass=1;pass<=2;pass++)
        {
            Assert.Equal("running",port.Begin());port.Tick();port.Tick();port.Tick();
            Assert.Equal(pass,native.Keys.Count);Assert.All(native.Keys,key=>Assert.Equal((ushort)83,key));
            Assert.Null(store.Read()!["pending"]);Assert.Equal(300,store.Read()!["minimum_keep"]!.GetValue<long>());
        }
    }
    [Fact] public void DevicePanelCloseUsesBackArrowAndCurrencyHasSharedRecovery()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());
        foreach(var device in routes.Devices.Values)
        {
            var box=NavigationProfile.ReadBox(routes.Profile.Definition["pages"]![device.QueuePage]!["coordinate_targets"]!["page.close"]!);
            Assert.True(box.Left>1200,device.Key);
        }
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]!["wood_panel"]!["anchors"]=new JsonArray("scene_bag");
        var previewClicks=new List<PixelPoint>();var previewNative=new NoInputOps(Stamp(routes.Profile,"scene_bag","screen_overlay_back"),previewClicks);
        using(var preview=new ScreenWorkflowSession(routes,Window(),previewNative,new EmptyOcr(),new MemoryStore()))
        {preview.Start();var closePort=preview.CreateMaintenancePort("remote-close",new MemoryStore());closePort.Observe();Assert.False(closePort.ReleaseNavigation());Assert.True(Assert.Single(previewClicks).X<100);}
        var root=RecognitionTests.Root();using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/currency-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var clicks=new List<PixelPoint>();var native=new NoInputOps(new(rgb.Width,rgb.Height,pixels),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("currency-recovery",new MemoryStore());Assert.Equal("currency",port.Observe().Page);
        Assert.False(port.ReleaseNavigation());Assert.Single(clicks);Assert.True(clicks[0].X>1200);
    }
    [Fact] public void ConfirmedDeviceArrivalDoesNotExpireWhileReadingItsCurrentRecipe()
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/fabric-detail-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var native=new NoInputOps(new(rgb.Width,rgb.Height,pixels));
        var journal=new MemoryStore(new JsonObject{["ports"]=new JsonObject{["production:arrival"]=new JsonObject{["device_arrived"]=true}}});
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),journal);session.Start();
        var port=session.CreateProductionPort("catalog_fabric_04","arrival",new MemoryStore());
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(ScreenProductionPort).GetField("queueArrived",flags)!.SetValue(port,true);
        typeof(ScreenProductionPort).GetField("queueEpoch",flags)!.SetValue(port,session.InputEpoch);
        typeof(ScreenProductionPort).GetField("queueAt",flags)!.SetValue(port,0d);
        typeof(ScreenProductionPort).GetField("queueEmpty",flags)!.SetValue(port,new[]{0,1,2,3,4});
        var frame=port.Observe();Assert.Equal("recipe_detail",frame.Page);Assert.Equal("catalog_fabric_04",frame.RecipeKey);Assert.True(frame.AtDevice);Assert.True(frame.CraftAvailable);
    }
    [Fact] public void TheaterCreationWaitsForCurrentStableTitleBeforeReviewOrCreate()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var rows=routes.Configuration["private_theater_refresh"]!["steps"]!.AsArray();
        routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"bag":{"anchors":["bag_tools_label"]}}""");
        while(rows[0]!["id"]!.GetValue<string>()!="create_ready")rows.RemoveAt(0);
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"bag_tools_label","bag_close","maint_tools_selected"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenPrivateTheaterRefresh(session,()=>now);refresh.Start(now);
        Assert.False(refresh.Tick(now));Assert.Empty(clicks);Assert.Empty(native.Keys);
        now=now.AddSeconds(45);Assert.False(refresh.Tick(now));Assert.Empty(clicks);
        native.CurrentImage=Stamp(routes.Profile,"private_theater_create_title");
        Assert.True(refresh.Tick(now),$"{refresh.Status}: {refresh.CurrentStep}; {refresh.Failure}; page={session.CurrentPage}");Assert.Equal("leader_review",refresh.CurrentStep);Assert.Empty(clicks);
        now=now.AddSeconds(1);Assert.True(refresh.Tick(now));Assert.Single(clicks);
        Assert.Empty(native.Keys);
    }
    [Fact] public void CurrentTheaterCreationFrameCannotCountAsLoadedOrReturned()
    {
        var root=RecognitionTests.Root();using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/private-theater/create-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var frame=new RgbImage(rgb.Width,rgb.Height,pixels);using var matcher=new TemplateMatcher(ScreenRoutes.Load(root).Profile);
        Assert.NotNull(matcher.Anchor(frame,"private_theater_create_title"));
        Assert.Null(matcher.Anchor(frame,"private_theater_loaded_text"));Assert.Null(matcher.Anchor(frame,"scene_bag"));Assert.Null(matcher.Anchor(frame,"theater_returned_menu_glyph"));
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void OnlyCurrentDispatchedStartCanBindSharedStableTaskTitle(bool currentStart)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var scope=ScreenWorkflowSession.ProcessScope(Window());
        var journal=new MemoryStore(new JsonObject{["ports"]=new JsonObject{["gathering:own-start"]=new JsonObject
        { ["started"]=true,["action_id"]="own-start",["session"]=scope.SessionId,["awaiting_task_until"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d+(currentStart?300:-1)}}});
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","maint_scene_map","gathering_observer.task_title"));var ocr=new EmptyOcr();
        using var session=new ScreenWorkflowSession(routes,Window(),native,ocr,journal);session.Start();
        var route=new RuntimeCatalog(routes.Configuration).Gathering["gathering_high_log"];
        var frame=session.CreateGatheringPort(route.Key,"own-start",new MemoryStore()).ObserveOnly(route,"own-start");
        Assert.Equal(currentStart,frame.TaskStatus=="active");Assert.Empty(native.Keys);
        if(currentStart)Assert.Equal(0,ocr.Calls);
    }
    [Fact] public void CurrentDailyGatheringListUsesItsStableTitle()
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/daily-nodes-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var frame=new RgbImage(1280,960,pixels);using var matcher=new TemplateMatcher(routes.Profile);Assert.NotNull(matcher.Anchor(frame,"preset_daily_title"));
        using var recognizer=new ScreenPageRecognizer(routes.Profile,matcher);var view=recognizer.Recognize(frame);
        Assert.True(view.Page=="daily_nodes","page="+view.Page+"; matched="+string.Join(",",routes.Profile.Definition["pages"]!.AsObject().Concat(routes.Profile.Definition["page_variants"]!.AsObject()).Where(p=>p.Value?["anchors"] is JsonArray a&&a.Count>0&&a.All(n=>matcher.Anchor(frame,n!.GetValue<string>()) is not null)).Select(p=>p.Key)));
    }
    [Fact] public void NextGatheringClosesPreviousPointDetailBeforeOpeningItsOwnRoute()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var clicks=new List<PixelPoint>();
        var native=new NoInputOps(Stamp(routes.Profile,"preset_catalog_point_daily_03_name","preset_catalog_point_daily_03_find","detail_confirm_close"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var route=new RuntimeCatalog(routes.Configuration).Gathering["gathering_clay"];
        var port=session.CreateGatheringPort(route.Key,"next-point",new MemoryStore());
        var frame=port.Observe(route,"next-point");Assert.Equal("point_detail_daily_3",frame.Page);
        port.Navigate(route);Assert.Single(clicks);Assert.True(routes.Profile.Templates["detail_confirm_close"].Box.Contains(clicks[0]));
    }
    [Theory]
    [InlineData("wood_panel")]
    [InlineData("metal_panel")]
    [InlineData("chemical_panel")]
    [InlineData("recipe_detail")]
    public void GatheringLeavesPreviousProductionPageThroughSharedClose(string page)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        var close=page=="recipe_detail"?"recipe_detail.close":"page.close";
        routes.Profile.Definition["pages"]=new JsonObject{[page]=new JsonObject{["anchors"]=new JsonArray("scene_bag"),["coordinate_targets"]=new JsonObject{[close]=new JsonArray(20,20,60,60)}}};
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var route=new RuntimeCatalog(routes.Configuration).Gathering["gathering_soft_log"];var port=session.CreateGatheringPort(route.Key,"handoff",new MemoryStore());
        port.ObserveOnly(route,"handoff");port.Navigate(route);Assert.Single(clicks);Assert.Equal(new PixelPoint(40,40),clicks[0]);
        port.ObserveOnly(route,"handoff");port.Navigate(route);Assert.Single(clicks);Assert.False(port.CanReleaseForeground());
    }
    [Fact] public void DispatchedGatheringStillTravellingDoesNotReleaseOnOldIdleScene()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var scope=ScreenWorkflowSession.ProcessScope(Window());
        var journal=new MemoryStore(new JsonObject{["ports"]=new JsonObject{["gathering:travelling"]=new JsonObject{["started"]=true,["action_id"]="travelling",["session"]=scope.SessionId}}});
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","maint_scene_map"));
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),journal);session.Start();
        session.BeginNavigation("gathering:travelling",300);
        var route=new RuntimeCatalog(routes.Configuration).Gathering["gathering_ore"];
        var port=session.CreateGatheringPort(route.Key,"travelling",new MemoryStore());
        for(var i=0;i<5;i++)
        {
            var frame=port.ObserveOnly(route,"travelling");
            Assert.Equal("unknown",frame.TaskStatus);Assert.False(frame.Evidence?["navigation_idle_confirmed"]?.GetValue<bool>()==true);
        }
        Assert.Empty(native.Keys);
    }
    [Theory]
    [InlineData("ore-task-current.jpg","gathering_ore","ore_objective",true)]
    [InlineData("hard-log-task-current.jpg","gathering_hard_log","hard_log_objective",false)]
    [InlineData("high-log-task-current.jpg","gathering_high_log","high_log_objective",true)]
    public void CurrentTaskReconcilesOriginalStartWithoutReadingProgressOrSendingInput(string file,string key,string objective,bool expectedActive)
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production",file));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var scope=ScreenWorkflowSession.ProcessScope(Window());var journal=new MemoryStore(new JsonObject
        {
            ["ports"]=new JsonObject{["gathering:existing-ore"]=new JsonObject{["started"]=true,["action_id"]="existing-ore",["session"]=scope.SessionId}}
        });
        var native=new NoInputOps(new(1280,960,pixels));var ocr=new EmptyOcr();
        using var session=new ScreenWorkflowSession(routes,Window(),native,ocr,journal);session.Start();
        Assert.NotNull(session.Matcher.Anchor(new(1280,960,pixels),"gathering_observer."+objective,minimum:.8));
        Assert.NotNull(session.Matcher.Anchor(new(1280,960,pixels),"gathering_observer.task_title",minimum:.8));
        var route=new RuntimeCatalog(routes.Configuration).Gathering[key];
        var frame=session.CreateGatheringPort(route.Key,"existing-ore",new MemoryStore()).ObserveOnly(route,"existing-ore");
        Assert.Equal(expectedActive?"active":"unknown",frame.TaskStatus);Assert.Equal(expectedActive,frame.TaskId is not null);Assert.Equal(expectedActive?0:1,ocr.Calls);Assert.Empty(native.Keys);
    }
    [Fact] public void EveryConfiguredProductionAndGatheringRouteHasItsRequiredSharedDefinitions()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());
        var pages=routes.Profile.Definition["pages"]!.AsObject();
        foreach(var recipe in routes.Recipes.Values)
        {
            var device=routes.Devices[recipe.Device];Assert.NotNull(pages[device.QueuePage]);
            Assert.All(recipe.ConfirmAnchors,a=>Assert.Contains(a,routes.Profile.Templates.Keys));
            Assert.All(device.ArrivalAnchors,a=>Assert.Contains(a,routes.Profile.Templates.Keys));
            Assert.NotNull(pages[device.QueuePage]?["targets"]?[recipe.OpenTarget]);
        }
        foreach(var (key,node) in routes.Configuration["gathering_routes"]!.AsObject())
        {
            Assert.NotNull(node?["navigation"]?["detail_page"]);
            Assert.NotNull(routes.Configuration["gathering_observer"]?["routes"]?[key]);
            foreach(var step in node!["navigation"]!["steps"]!.AsArray())Assert.NotNull(pages[step!["page"]!.GetValue<string>()]);
        }
    }
    [Fact] public void PrivateTheaterDoesNotAdvancePastUnselectedToolsTab()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"bag":{"anchors":["bag_tools_label"]},"repair_tools":{"anchors":["maint_tools_selected"]}}""");
        var steps=routes.Configuration["private_theater_refresh"]!["steps"]!.AsArray();
        var selected=steps.OfType<JsonObject>().Single(step=>step["id"]!.GetValue<string>()=="tools_selected");
        Assert.Equal("wait",selected["kind"]!.GetValue<string>());
        Assert.Equal("maint_tools_selected",selected["anchors"]![0]!.GetValue<string>());
        Assert.Contains("maint_tools_selected",routes.Profile.Templates.Keys);
        var toolsStep=steps.OfType<JsonObject>().Single(step=>step["id"]!.GetValue<string>()=="tools_tab");
        Assert.Equal("key",toolsStep["kind"]!.GetValue<string>());
        Assert.Equal(83,toolsStep["key"]!.GetValue<int>());
        var gameWait=steps.OfType<JsonObject>().Single(step=>step["id"]!.GetValue<string>()=="game_tab_selected");
        Assert.Equal("wait",gameWait["kind"]!.GetValue<string>());
        Assert.Equal("bag_game_selected",gameWait["anchors"]![0]!.GetValue<string>());
        Assert.Contains("bag_game_selected",routes.Profile.Templates.Keys);
        using(var matcher=new TemplateMatcher(routes.Profile))Assert.NotNull(matcher.Anchor(Stamp(routes.Profile,"maint_tools_selected"),"maint_tools_selected"));
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"bag_tools_label"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var start=DateTimeOffset.UnixEpoch;var refresh=new ScreenPrivateTheaterRefresh(session,()=>start);refresh.Start(start);
        refresh.Tick(start);Assert.True(refresh.Tick(start.AddSeconds(2)),"page="+session.CurrentPage+"; step="+refresh.CurrentStep+"; failure="+refresh.Failure);
        Assert.True(refresh.Tick(start.AddSeconds(4)));Assert.Equal("tools_selected",refresh.CurrentStep);
        Assert.False(refresh.Tick(start.AddSeconds(6)));Assert.Empty(clicks);Assert.Contains((ushort)83,native.Keys);
        Assert.False(refresh.Tick(start.AddSeconds(70)));Assert.Equal(PrivateTheaterRefreshStatus.Failed,refresh.Status);
        Assert.Contains("tools_selected",refresh.Failure);Assert.Empty(clicks);
    }
    [Fact] public void PrivateTheaterGameCategoryAdvancesWhileToolsTabRemainsSelected()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"bag":{"anchors":["bag_tools_label"]},"repair_tools":{"anchors":["maint_tools_selected"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"bag_tools_label"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var start=DateTimeOffset.UnixEpoch;var refresh=new ScreenPrivateTheaterRefresh(session,()=>start);refresh.Start(start);
        refresh.Tick(start);Assert.True(refresh.Tick(start.AddSeconds(2)),"page="+session.CurrentPage+"; step="+refresh.CurrentStep+"; failure="+refresh.Failure);
        native.CurrentImage=Stamp(routes.Profile,"maint_tools_selected");
        Assert.True(refresh.Tick(start.AddSeconds(4)));Assert.True(refresh.Tick(start.AddSeconds(6)));
        Assert.True(refresh.Tick(start.AddSeconds(7)));Assert.Equal("game_tab_selected",refresh.CurrentStep);
        Assert.False(refresh.Tick(start.AddSeconds(11)));
        native.CurrentImage=Stamp(routes.Profile,"maint_tools_selected","bag_game_selected");
        Assert.True(refresh.Tick(start.AddSeconds(12)));Assert.Equal("private_theater_item",refresh.CurrentStep);
        Assert.Single(clicks);
    }
    [Fact] public void PrivateTheaterSelectedGameAnchorDistinguishesLiveCategoryFrames()
    {
        var root=RecognitionTests.Root();using var matcher=new TemplateMatcher(ScreenRoutes.Load(root).Profile);
        RgbImage Read(string file)
        {
            using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/private-theater",file));
            using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
            var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
            return new(rgb.Width,rgb.Height,pixels);
        }
        Assert.NotNull(matcher.Anchor(Read("game-selected-20261102.jpg"),"bag_game_selected"));
        Assert.Null(matcher.Anchor(Read("game-unselected-20261102.jpg"),"bag_game_selected"));
    }
    [Theory][InlineData("metal","metal_title","metal_equipment")][InlineData("metal","arrived_title","arrived_equipment")][InlineData("wood","preset_wood_title","preset_wood_equipment")]
    [InlineData("leather","preset_leather_title","preset_leather_equipment")][InlineData("fabric","preset_fabric_title","preset_fabric_equipment")]
    [InlineData("chemical","preset_chemical_title","preset_chemical_equipment")][InlineData("food","preset_food_title","preset_food_equipment")]
    public void EquipmentPageNeedsOnlyStableIdentityAndClosesAtTopRight(string device,string title,string equipment)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,title,equipment),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var definition=routes.Recipes.Values.First(r=>r.Device==device);var recipe=new ProductionRecipe(definition.Key,device,60);
        var port=session.CreateProductionPort(recipe.Key,"close",new MemoryStore());Assert.Equal(device+"_panel",port.Observe().Page);
        Assert.False(session.LastObservation!.View.NeedsDeviceArrival);port.Navigate("leave",recipe);
        Assert.True(new PixelBox(1222,20,1264,65).Contains(Assert.Single(clicks)));
    }
    [Fact] public void DeviceExitRetriesAreBoundedAndSuccessfulCloseReleasesPage()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"preset_chemical_title","preset_chemical_equipment"),clicks);var time=new RecoveryTime();
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var recipe=new ProductionRecipe("catalog_chemical_14","chemical",10800);var port=new ScreenProductionPort(session,recipe.Key,"exit",new MemoryStore(),time);
        port.Observe();port.Navigate("leave",recipe);Assert.Single(clicks);
        for(var i=0;i<2;i++){time.Seconds+=5;port.Observe();port.Navigate("leave",recipe);}
        Assert.Equal(3,clicks.Count);time.Seconds+=5;port.Observe();Assert.Throws<InputBlockedException>(()=>port.Navigate("leave",recipe));Assert.Equal(3,clicks.Count);
        // An explicit retry may close again; a changed page ends the reversible wait.
        port.Navigate("leave",recipe);native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map");Assert.Equal("scene",port.Observe().Page);
        port.Navigate("leave",recipe);Assert.Equal(4,clicks.Count);Assert.True(port.CanReleaseForeground());
    }
    [Fact] public void ObservedRecipeOpenDoesNotBlockExitAfterCraftAutomaticallyClosesIt()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"metal_panel":{"anchors":["scene_bag"],"coordinate_targets":{"metal.open_iron_ingot_from_iron_ore":[650,270,770,320],"page.close":[1222,20,1264,65]}},"recipe_detail":{"anchors":["scene_skills"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateProductionPort("iron_ingot_from_iron_ore","recipe-round-trip",new MemoryStore());var recipe=new ProductionRecipe("iron_ingot_from_iron_ore","metal",60);
        port.Observe();port.Navigate("recipe_preview",recipe);Assert.Single(clicks);
        native.CurrentImage=Stamp(routes.Profile,"scene_skills");Assert.Equal("recipe_detail",port.Observe().Page);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag");port.Observe();port.Navigate("leave",recipe);
        Assert.Equal(2,clicks.Count);Assert.True(new PixelBox(1222,20,1264,65).Contains(clicks[^1]));
    }
    [Fact] public void ProductionLeaveDismissesDelayedCollectionResultBeforeClosingDevice()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"metal_panel":{"anchors":["scene_bag"],"coordinate_targets":{"page.close":[1222,20,1264,65]}},"production_complete":{"anchors":["scene_skills"],"coordinate_targets":{"completion.confirm":[390,872,889,934]}}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateProductionPort("iron_ingot_from_iron_ore","late-result",new MemoryStore());var recipe=new ProductionRecipe("iron_ingot_from_iron_ore","metal",60);
        port.Observe();port.Navigate("leave",recipe);Assert.Single(clicks);
        native.CurrentImage=Stamp(routes.Profile,"scene_skills");port.Observe();port.Navigate("leave",recipe);
        Assert.Equal(2,clicks.Count);Assert.True(new PixelBox(390,872,889,934).Contains(clicks[^1]));
        port.Observe();port.Navigate("leave",recipe);Assert.Equal(2,clicks.Count);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag");port.Observe();port.Navigate("leave",recipe);
        Assert.Equal(3,clicks.Count);Assert.True(new PixelBox(1222,20,1264,65).Contains(clicks[^1]));
    }
    [Fact] public void DeviceTravelSurvivesIntermediatePagesAndRestartUntilEquipmentPageAutomaticallyReopens()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"metal_panel":{"anchors":["scene_bag"],"coordinate_targets":{"metal.open_iron_ingot_from_iron_ore":[250,250,330,290]}},"scene":{"anchors":["scene_skills"]},"wood_panel":{"anchors":["maint_scene_map"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);var journal=new MemoryStore();var recipe=new ProductionRecipe("iron_ingot_from_iron_ore","metal",60);
        using(var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),journal))
        {
            session.Start();var port=session.CreateProductionPort(recipe.Key,"travel",new MemoryStore());
            Assert.False(port.Observe().AtDevice);port.Navigate("queue",recipe);Assert.Single(clicks);
            Assert.True(port.Observe().NavigationActive);port.Navigate("queue",recipe);Assert.Single(clicks);
            foreach(var names in new[]{new[]{"scene_skills"},Array.Empty<string>(),new[]{"maint_scene_map"},new[]{"scene_skills"}})
            {native.CurrentImage=Stamp(routes.Profile,names);Assert.True(port.Observe().NavigationActive);port.Navigate("queue",recipe);Assert.Single(clicks);Assert.False(port.CanReleaseForeground());}
            session.Pause();Assert.Throws<InputBlockedException>(()=>port.Resume());session.ResumeExplicit();
            Assert.True(port.Observe().NavigationActive);port.Navigate("queue",recipe);Assert.Single(clicks);
        }
        using(var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),journal))
        {
            session.Start();var port=session.CreateProductionPort(recipe.Key,"travel",new MemoryStore());
            Assert.True(port.Observe().NavigationActive);port.Navigate("queue",recipe);Assert.Single(clicks);
            // Arrival uses the automatically reopened page alone: arrival anchors are absent.
            native.CurrentImage=Stamp(routes.Profile,"scene_bag");var frame=port.Observe();Assert.False(frame.NavigationActive);Assert.True(frame.AtDevice);
            port.Navigate("recipe",recipe);Assert.Equal(2,clicks.Count);Assert.NotEqual(clicks[0],clicks[1]);
        }
    }
    [Fact] public void DeviceTravelConfirmsCrossRegionPromptOnceAndKeepsWaitingAcrossRestart()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"metal_panel":{"anchors":["scene_bag"]},"device_travel_confirm":{"anchors":["scene_skills"],"coordinate_targets":{"device.confirm_travel":[646,833,861,896]}},"scene":{"anchors":["maint_scene_map"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);var journal=new MemoryStore();var recipe=new ProductionRecipe("iron_ingot_from_iron_ore","metal",60);
        using(var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),journal))
        {
            session.Start();var port=session.CreateProductionPort(recipe.Key,"travel-confirm",new MemoryStore());
            port.Observe();port.Navigate("queue",recipe);Assert.Single(clicks);
            native.CurrentImage=Stamp(routes.Profile,"scene_skills");Assert.True(port.Observe().NavigationActive);
            port.Navigate("queue",recipe);Assert.Equal(2,clicks.Count);Assert.True(new PixelBox(646,833,861,896).Contains(clicks[^1]));
            Assert.True(port.Observe().NavigationActive);port.Navigate("queue",recipe);Assert.Equal(2,clicks.Count);
        }
        using(var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),journal))
        {
            session.Start();var port=session.CreateProductionPort(recipe.Key,"travel-confirm",new MemoryStore());
            Assert.True(port.Observe().NavigationActive);port.Navigate("queue",recipe);Assert.Equal(2,clicks.Count);
            native.CurrentImage=Stamp(routes.Profile,"maint_scene_map");Assert.True(port.Observe().NavigationActive);port.Navigate("queue",recipe);Assert.Equal(2,clicks.Count);
            native.CurrentImage=Stamp(routes.Profile,"scene_bag");var arrived=port.Observe();Assert.False(arrived.NavigationActive);Assert.True(arrived.AtDevice);
        }
    }
    [Fact] public void GatheringReversibleNavigationRetriesAreBoundedAndDoNotRepeatNativeStart()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Configuration["navigation_retry"]=new JsonObject{["interval_s"]=8,["max_retries"]=4,["timeout_s"]=90};
        routes.Profile.Definition["page_variants"]=new JsonObject();
        routes.Profile.Definition["coordinate_control_targets"]=JsonNode.Parse("""{"scene":{"test.open_menu":[10,10,30,30]}}""");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"coordinate_targets":{"test.open_menu":[10,10,30,30]}}}""");
        routes.Configuration["gathering_routes"]!["iron_ore_nearby"]!["navigation"]!["steps"]=JsonNode.Parse("""[{"page":"scene","target":"test.open_menu","expected_page":"character_menu"}]""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var route=new GatheringRoute("iron_ore_nearby",198875762,"mining","iron");var port=session.CreateGatheringPort(route.Key,"retry",new MemoryStore());
        port.ObserveOnly(route,"retry");port.Navigate(route);Assert.Single(clicks);
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var at=typeof(ScreenGatheringPort).GetField("pendingAt",flags)!;
        for(var i=0;i<6;i++){at.SetValue(port,session.LastCapture!.CapturedAt-9);port.ObserveOnly(route,"retry");port.Navigate(route);}
        Assert.Equal(5,clicks.Count);
        session.SavePortState("gathering:retry",new JsonObject{["started"]=true,["session"]=session.Scope.SessionId,["action_id"]="retry"});
        // Existing start state blocks navigation retries even after their wait expires.
        var started=session.CreateGatheringPort(route.Key,"retry",new MemoryStore());started.ObserveOnly(route,"retry");started.Navigate(route);Assert.Equal(5,clicks.Count);
        typeof(ScreenGatheringPort).GetField("pendingSince",flags)!.SetValue(port,session.LastCapture!.CapturedAt-91);
        Assert.Throws<InputBlockedException>(()=>port.Navigate(route));Assert.Equal(5,clicks.Count);
    }
    [Fact] public void GatheringMenuWaitsForDeclaredPageAndStartedTravelDoesNotReopenMenus()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"coordinate_targets":{"test.open_menu":[250,250,330,290]}},"character_menu":{"anchors":["scene_skills"],"coordinate_targets":{"test.open_skills":[350,250,430,290]}},"unrelated":{"anchors":["maint_scene_map"]}}""");
        routes.Configuration["gathering_routes"]!["iron_ore_nearby"]!["navigation"]!["steps"]=JsonNode.Parse("""[{"page":"scene","target":"test.open_menu","expected_page":"character_menu"},{"page":"character_menu","target":"test.open_skills","expected_page":"character_life_skills"}]""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var route=new GatheringRoute("iron_ore_nearby",198875762,"mining","iron");var port=session.CreateGatheringPort(route.Key,"menu",new MemoryStore());
        port.ObserveOnly(route,"a");port.Navigate(route);Assert.Single(clicks);
        native.CurrentImage=Stamp(routes.Profile,"maint_scene_map");port.ObserveOnly(route,"a");port.Navigate(route);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag");port.ObserveOnly(route,"a");port.Navigate(route);Assert.Single(clicks);
        native.CurrentImage=Stamp(routes.Profile,"scene_skills");port.ObserveOnly(route,"a");port.Navigate(route);Assert.Equal(2,clicks.Count);
        session.SavePortState("gathering:started",new JsonObject{["started"]=true,["session"]=session.Scope.SessionId,["action_id"]="a"});
        var started=session.CreateGatheringPort(route.Key,"started",new MemoryStore());
        foreach(var name in new[]{"scene_skills","maint_scene_map","scene_bag"})
        {native.CurrentImage=Stamp(routes.Profile,name);started.ObserveOnly(route,"a");started.Navigate(route);Assert.Equal(2,clicks.Count);}
    }
    [Fact] public void ProcessingNavigationWaitIgnoresUnrelatedPageAndContinuesToMetal()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"skills":{"anchors":["scene_bag"],"targets":{"skills.open_processing":"scene_bag"}},"category_page":{"anchors":["preset_category_title","preset_category_processing"],"coordinate_targets":{"category.open_metal":[240,245,320,290]}},"unrelated":{"anchors":["preset_category_title","preset_category_processing"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateProductionPort("iron_ingot_from_iron_ore","p",new MemoryStore());var recipe=new ProductionRecipe("iron_ingot_from_iron_ore","metal",60);
        port.Observe();port.Navigate("recipe_preview",recipe);Assert.Single(clicks);
        native.CurrentImage=Stamp(routes.Profile,"preset_category_title","preset_category_processing");
        Assert.Equal("category_page",port.Observe().Page);port.Navigate("recipe_preview",recipe);
        Assert.Equal(2,clicks.Count);Assert.Equal(new PixelPoint(280,267),clicks[^1]);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void RepairKeepsOnlyCurrentPrimaryQuoteAcrossConfirmation(bool resume)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"repair_equipment":{"anchors":["scene_bag"]},"repair_quote":{"anchors":["scene_skills"]},"scene":{"anchors":["maint_scene_map"]}}""");
        routes.Configuration["maintenance"]!["gold"]=null;routes.Configuration["maintenance"]!["gold_by_page"]=null;
        routes.Configuration["maintenance"]!["repair"]!["quote"]=JsonNode.Parse("""{"region":[5,5,60,25],"format":"grouped_integer"}""");
        var ocr=new MutableQuoteOcr();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),[]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,ocr,new MemoryStore());session.Start();var port=session.CreateMaintenancePort("preview",new MemoryStore());
        port.Observe();ocr.Text="1万";native.CurrentImage=Stamp(routes.Profile,"scene_skills");
        var quoted=port.Observe();Assert.Equal(19913,quoted.Quote);Assert.False(quoted.QuoteIsUpperBound);
        if(resume){session.Pause();session.ResumeExplicit();}else{native.CurrentImage=Stamp(routes.Profile,"maint_scene_map");port.Observe();native.CurrentImage=Stamp(routes.Profile,"scene_skills");}
        var after=port.Observe();Assert.Equal(20000,after.Quote);Assert.True(after.QuoteIsUpperBound);
    }
    private sealed class MutableQuoteOcr:IOcrEngine
    {public string Text="19913";public OcrLine? Recognize(int width,int height,byte[] grayPixels)=>new(Text,.99);}
    [Theory][InlineData(true,0,true)][InlineData(false,0,false)][InlineData(true,20,false)]
    public void NavigationWingsUseOneCurrentQuoteAndPreserveUnknownIntent(bool enabled,int reserve,bool sent)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","travel.wing_quote_icon"),clicks);var ocr=new WingsOcr();var store=new MemoryStore();
        using var session=new ScreenWorkflowSession(routes,Window(),native,ocr,new MemoryStore());session.Start();session.BeginNavigation("trip");
        session.ConfigureTravel(store,()=>new JsonObject{["travel"]=new JsonObject{["auto_use_wings"]=enabled,["wing_reserve_min"]=(long)reserve}});
        native.OnPointerDown=()=>Assert.Equal("use_wings",store.Read()!["pending"]!["kind"]!.GetValue<string>());
        Assert.Equal(sent,session.TryTravel());Assert.Equal(sent?1:0,clicks.Count);
        if(sent){Assert.Equal(1,native.Captures);Assert.Equal(1,ocr.Calls);Assert.False(session.TryTravel());session.BeginNavigation("other-trip");Assert.True(session.TryTravel());Assert.Equal(2,clicks.Count);Assert.NotNull(store.Read()!["unresolved"]!["trip"]);}
    }
    private sealed class WingsOcr:IOcrEngine
    {public int Calls;public OcrLine? Recognize(int width,int height,byte[] grayPixels){Calls++;return new("1/20",.99);}}
    [Theory][InlineData("10/20",0,true)][InlineData("10/20",15,false)][InlineData("invalid",0,false)]
    public void ChangingWingsCostUsesFirstReadAndNeverRecalculatesThisTrip(string first,long reserve,bool use)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","travel.wing_quote_icon"),clicks);var ocr=new DecreasingWingsOcr(first);var store=new MemoryStore();
        using var session=new ScreenWorkflowSession(routes,Window(),native,ocr,new MemoryStore());session.Start();session.BeginNavigation("trip");
        session.ConfigureTravel(store,()=>new JsonObject{["travel"]=new JsonObject{["auto_use_wings"]=true,["wing_reserve_min"]=reserve}});
        Assert.Equal(use,session.TryTravel());Assert.Equal(1,ocr.Calls);Assert.Equal(use?1:0,clicks.Count);
        for(var i=0;i<3;i++)Assert.False(session.TryTravel());
        Assert.Equal(1,ocr.Calls);Assert.Equal(use,store.Read()!["decisions"]!["trip"]!["use"]!.GetValue<bool>());
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","travel.wing_quote_icon");session.BeginNavigation("next");session.TryTravel();Assert.Equal(2,ocr.Calls);
        if(use){Assert.NotNull(store.Read()!["unresolved"]!["trip"]);Assert.Equal("use_wings",store.Read()!["pending"]!["kind"]!.GetValue<string>());}
    }
    private sealed class DecreasingWingsOcr(string first):IOcrEngine
    {public int Calls;public OcrLine? Recognize(int w,int h,byte[] pixels)=>new(++Calls==1?first:"1/20",.99);}
    [Fact] public void ExplicitCurrentScriptHotpathOfflineMeasurement()
    {
        if(Environment.GetEnvironmentVariable("MABI_SCRIPT_HOTPATH")!="1")return;
        var samples=new JsonArray();
        for(var sample=0;sample<3;sample++)
        {
            var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
            routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
            routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
            routes.Profile.Definition["idle_fishing"]=JsonNode.Parse("""{"controls":{"start":[1175,860,1235,915],"move":[100,100,200,200]}}""");
            var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks){Background=true};var ocr=new EmptyOcr();
            using var session=new ScreenWorkflowSession(routes,Window(),native,ocr,new MemoryStore());session.Start();
            var store=new MemoryStore();var port=session.CreateFishingPort("point_fishing_00","hotpath-"+sample,store);
            var wait=System.Diagnostics.Stopwatch.StartNew();Assert.True(port.ObserveOnly().Stable);wait.Stop();
            var waitCaptures=native.Captures;var waitCaptureMs=native.CaptureMilliseconds;var waitOcr=ocr.Calls;
            long submit=0,delivered=0;var captureAtSubmit=0;var ocrAtSubmit=0;double captureMsAtSubmit=0;
            native.OnPointerDown=()=>delivered=System.Diagnostics.Stopwatch.GetTimestamp();
            var movement=System.Diagnostics.Stopwatch.StartNew();
            port.MoveWhileObserving(new("move",Direction:"w",DurationSeconds:.06),frame=>
            {
                Assert.True(frame.FishingButton);captureAtSubmit=native.Captures;ocrAtSubmit=ocr.Calls;captureMsAtSubmit=native.CaptureMilliseconds;
                store.Flush(new JsonObject{["pending"]=new JsonObject{["kind"]="start"}});
                submit=System.Diagnostics.Stopwatch.GetTimestamp();port.Perform(new("start"));return true;
            });
            movement.Stop();Assert.NotEqual(0,delivered);Assert.Single(clicks);Assert.Equal(waitCaptures,native.Captures);
            Assert.Equal(captureAtSubmit,native.Captures);Assert.Equal(ocrAtSubmit,ocr.Calls);Assert.Equal(0,ocr.Calls);
            samples.Add(new JsonObject{["sample"]=sample,["wait_observe_ms"]=wait.Elapsed.TotalMilliseconds,["wait_capture_calls"]=waitCaptures,["wait_fake_capture_ms"]=waitCaptureMs,["wait_recognition_and_guard_ms"]=Math.Max(0,wait.Elapsed.TotalMilliseconds-waitCaptureMs),["wait_ocr_calls"]=waitOcr,["movement_to_release_ms"]=movement.Elapsed.TotalMilliseconds,["movement_capture_calls"]=native.Captures-waitCaptures,["movement_fake_capture_ms"]=captureMsAtSubmit-waitCaptureMs,["fixed_submit_to_fake_down_ms"]=System.Diagnostics.Stopwatch.GetElapsedTime(submit,delivered).TotalMilliseconds,["fixed_extra_capture_calls"]=native.Captures-captureAtSubmit,["fixed_extra_ocr_calls"]=ocr.Calls-ocrAtSubmit,["fake_pointer_down_calls"]=clicks.Count});
        }
        var output=new JsonObject{["status"]="measured_offline",["at"]=DateTimeOffset.UtcNow.ToString("O"),["evidence"]="Stamped fixture, actual ScreenFishingPort/session/shared guard, in-memory durable store and fake background transport. No game or native touch delivery.",["limits"]="Recognition-and-guard is a residual segment including hashing and bookkeeping; fake capture is an in-memory return. Submission timing includes durable authority and guard until fake pointer-down, not OS/game delivery. Setup excluded; first sample includes JIT. No planner runs inside this callback; scheduler contention is not measured. No old baseline threshold.",["samples"]=samples};
        var path=Path.Combine(RecognitionTests.Root(),"build/script-hotpath-current.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,output.ToJsonString(new(){WriteIndented=true}));
    }
    [Theory][InlineData("none",false)][InlineData("compass",false)][InlineData("nav",false)][InlineData("wings",false)][InlineData("wing_without_quote",false)][InlineData("quote_without_wing",false)][InlineData("unknown",false)]
    public void LegacyCastAnchorCannotAuthorizeInputOutsideFixedMovement(string conflict,bool allowed)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=conflict=="unknown"?new JsonObject():JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        routes.Profile.Definition["idle_fishing"]=JsonNode.Parse("""{"anchors":{"cast":"scene_bag"},"controls":{"start":[1175,860,1235,915]}}""");
        if(conflict is "compass" or "nav")routes.Profile.Definition["idle_fishing"]!["anchors"]![conflict]="scene_skills";
        var names=new List<string>{"scene_bag","scene_skills"};if(conflict=="wings")names.Add("travel.wing_quote_icon");
        if(conflict=="quote_without_wing")names.Add("travel.wing_quote_icon");
        var clicks=new List<PixelPoint>();using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(Stamp(routes.Profile,names.ToArray()),clicks),new EmptyOcr(),new MemoryStore());session.Start();
        var store=new MemoryStore(new JsonObject{["pending"]=new JsonObject{["kind"]="start"}});var port=session.CreateFishingPort("point_fishing_00","f",store);
        port.ObserveOnly();Assert.Equal(allowed,port.ObserveOnly().FishingButton);
        if(allowed){port.Perform(new("start"));Assert.Single(clicks);}else{Assert.Throws<InputRejectedBeforeDispatchException>(()=>port.Perform(new("start")));Assert.Empty(clicks);}
    }
    [Fact] public void FixedCastAfterLightTapReleasesKeyBeforeClickAndKeepsDurableDeduplication()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        routes.Profile.Definition["idle_fishing"]=JsonNode.Parse("""{"controls":{"start":[1175,860,1235,915],"move":[100,100,200,200]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var store=new MemoryStore();var port=session.CreateFishingPort("point_fishing_00","f",store);
        port.ObserveOnly();port.ObserveOnly();var captures=native.Captures;
        native.OnPointerDown=()=>Assert.Empty(native.HeldKeys);
        port.MoveWhileObserving(new("move",Direction:"w",DurationSeconds:.06),frame=>{Assert.True(frame.FishingButton);store.Flush(new JsonObject{["pending"]=new JsonObject{["kind"]="start"}});port.Perform(new("start"));return true;});
        Assert.Single(clicks);Assert.Equal(new ushort[]{87},native.Keys);Assert.Equal(captures,native.Captures);
        Assert.Empty(native.HeldKeys);
        Assert.Throws<InputBlockedException>(()=>port.MoveWhileObserving(new("move",Direction:"s",DurationSeconds:2),_=>true));
        Assert.Throws<InputRejectedBeforeDispatchException>(()=>port.MoveWhileObserving(new("move",Direction:"s",DurationSeconds:.06),_=>{port.Perform(new("start"));return true;}));
        Assert.Single(clicks);Assert.False(session.Guard.Paused);
    }
    [Fact] public void FullBackpackToastIsForwardedToMaintenanceOnlyFromStableCurrentFrame()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");routes.Profile.Definition.Remove("page_variants");
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","overlay.backpack_full_toast"));
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();var port=session.CreateMaintenancePort("m",new MemoryStore());
        Assert.True(port.Observe().BackpackFull);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag");Assert.False(port.Observe().BackpackFull);
    }
    [Fact] public void ProductionNavigationUsesSingleFrameShortcutWithoutPhantomWait()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"scene.open_skills":"scene_bag"}}}""");routes.Profile.Definition.Remove("page_variants");
        var image=Stamp(routes.Profile,"scene_bag");var native=new NoInputOps(image);var empty=new RgbImage(1280,960,new byte[1280*960*3]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();var key=routes.DefaultRecipe;var port=session.CreateProductionPort(key,"p",new MemoryStore());var recipe=routes.Recipes[key];
        port.Observe();port.Observe();native.CaptureFrame=n=>n>=5?empty:image;
        port.Navigate("queue",new(key,recipe.Device,recipe.DurationSeconds));Assert.Equal(new ushort[]{75},native.Keys);
        native.CaptureFrame=null;port.Observe();port.Observe();port.Navigate("queue",new(key,recipe.Device,recipe.DurationSeconds));Assert.Equal(new ushort[]{75},native.Keys);
    }
    [Fact] public void NavigationUsesFixedTargetWithoutReobservingMovingNpc()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"repair.talk":"maint_ferghus_scene_name"}}}""");
        RgbImage Frame(int offset)
        {
            var pixels=Stamp(routes.Profile,"scene_bag").CopyPixels();var t=routes.Profile.Templates["maint_ferghus_scene_name"];var source=t.Image.CopyPixels();
            for(var y=0;y<t.Image.Height;y++)Array.Copy(source,y*t.Image.Width*3,pixels,((t.Box.Top+y)*1280+t.Box.Left+offset)*3,t.Image.Width*3);
            return new(1280,960,pixels);
        }
        var original=Frame(0);var shifted=Frame(2);var clicks=new List<PixelPoint>();
        var ops=new NoInputOps(original,clicks){CaptureFrame=n=>n>=5?shifted:original};
        using var session=new ScreenWorkflowSession(routes,Window(),ops,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("moving-npc",new MemoryStore());port.Observe();port.Observe();
        var step=new JsonObject{["target"]="repair.talk",["settle_s"]=0};
        Assert.Null(port.TestNavigationStep(step).Error);Assert.Single(clicks);Assert.Equal("dispatch",port.LastTestNavigationTrace!.Phase);Assert.False(session.Guard.Paused);
        Assert.Null(port.TestNavigationStep(step).Error);Assert.Single(clicks);Assert.Equal("confirmed",port.LastTestNavigationTrace!.Phase);
        Assert.Null(port.TestNavigationStep(step).Error);Assert.Single(clicks);
    }
    [Fact] public void FishingUsesFixedMovementStepWithoutDynamicCastTemplate()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag","maint_scene_map"]}}""");
        routes.Profile.Definition["idle_fishing"]=JsonNode.Parse("""{"anchors":{"hook":"fishing_hook"},"controls":{"start":[1175,860,1235,915],"move":[400,300,800,700]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","maint_scene_map"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var time=new RecoveryTime();var store=new MemoryStore(new JsonObject{["pending"]=new JsonObject{["kind"]="start"}});var port=new ScreenFishingPort(session,"point_fishing_00","f",store,time);
        port.ObserveOnly();Assert.True(port.ObserveOnly().Stable);Assert.False(port.ObserveOnly().FishingButton);
        port.MoveWhileObserving(new("move",Direction:"w",DurationSeconds:.06),frame=>
        {
            Assert.True(frame.FishingButton);Assert.Equal(new PixelBox(1175,860,1235,915),session.LastObservation!.View.TargetRegions!["fishing.start"]);
            port.Perform(new("start"));return true;
        });
        Assert.Single(clicks);Assert.Contains((ushort)87,native.Keys);
        var afterCast=port.ObserveOnly();Assert.False(afterCast.FishingButton);Assert.False(afterCast.RoundActive);Assert.Equal("scene",afterCast.Page);
        Assert.NotNull(store.Read()!["pending"]);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map","fishing_hook");
        var hooked=port.ObserveOnly();Assert.True(hooked.HookVisible);Assert.True(hooked.RoundActive);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map");Assert.True(port.ObserveOnly().RoundActive);
        time.Seconds=1.1;Assert.False(port.ObserveOnly().RoundActive);
    }
    [Fact] public void MidGaugeHookMissCannotEndRoundOrSendAnotherCast()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag","maint_scene_map"]}}""");
        routes.Profile.Definition["idle_fishing"]=JsonNode.Parse("""{"anchors":{"hook":"fishing_hook"},"controls":{"start":[1175,860,1235,915],"move":[400,300,800,700]},"gauge":{"calibrated":true,"region":[580,430,680,440],"fill_rgb":[40,220,88],"empty_rgb":[39,42,50],"tolerance":20}}""");
        RgbImage Frame(bool hook,int? fill)
        {
            var pixels=Stamp(routes.Profile,hook?["scene_bag","maint_scene_map","fishing_hook"]:["scene_bag","maint_scene_map"]).CopyPixels();
            if(fill is {} count)for(var y=430;y<440;y++)for(var x=580;x<680;x++)
            {var at=(y*1280+x)*3;var rgb=x-580<count?new byte[]{40,220,88}:new byte[]{39,42,50};Array.Copy(rgb,0,pixels,at,3);}
            return new(1280,960,pixels);
        }
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Frame(false,null),clicks);var time=new RecoveryTime();
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var store=new MemoryStore();var port=new ScreenFishingPort(session,"point_fishing_00","f",store,time);
        var runner=new IdleFishingRunner(new("point_fishing_00",60),port,store);var state=runner.Snapshot();state["phase"]="refresh";store.Flush(state);
        runner=new(new("point_fishing_00",60),port,store);runner.Resume();runner.Tick();Assert.Single(clicks);
        native.CurrentImage=Frame(true,65);runner.Tick();Assert.True(runner.Snapshot()["round_seen"]!.GetValue<bool>());
        native.CurrentImage=Frame(false,50);time.Seconds=3;runner.Tick();
        Assert.Equal(.5,port.LastFrame!.Gauge);Assert.True(port.LastFrame.RoundActive);Assert.False(port.LastFrame.Compass);
        Assert.Equal(0,runner.Snapshot()["completed"]!.GetValue<int>());Assert.Single(clicks);
        native.CurrentImage=Frame(false,null);runner.Tick();Assert.True(port.LastFrame!.RoundActive);
        native.CurrentImage=new(1280,960,new byte[1280*960*3]);time.Seconds=4;runner.Tick();
        native.CurrentImage=Frame(false,null);time.Seconds=5;runner.Tick();Assert.True(port.LastFrame!.RoundActive);
        time.Seconds=6.1;runner.Tick();Assert.Equal(1,runner.Snapshot()["completed"]!.GetValue<int>());Assert.Single(clicks);
        Assert.Equal("refresh",runner.Snapshot()["phase"]!.GetValue<string>());Assert.Null(runner.Snapshot()["pending"]);
    }
    [Fact] public void DispatchedFishingTravelDoesNotReopenNavigationBeforeTaskIsObserved()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"point_detail_fishing_0":{"anchors":["scene_bag"],"targets":{"gathering.start":"scene_bag"}},"scene":{"anchors":["scene_skills"],"targets":{"character.open":"scene_skills"}}}""");
        routes.Profile.Definition["idle_fishing"]=JsonNode.Parse("""{"anchors":{"nav":"fishing_navigation_task"}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        var store=new MemoryStore(new JsonObject{["status"]="running"});
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateFishingPort("point_fishing_00","f",store);port.Observe();port.Observe();port.Perform(new("navigate","point_fishing_00"));
        Assert.Single(clicks);native.CurrentImage=Stamp(routes.Profile,"scene_skills");
        Assert.Null(port.Observe().NavigationActive);Assert.Null(port.Observe().NavigationActive);
        Assert.Single(clicks);Assert.NotNull(session.CurrentNavigationId);
        native.CurrentImage=Stamp(routes.Profile,"scene_skills","fishing_navigation_task");Assert.True(port.Observe().NavigationActive);
        native.CurrentImage=Stamp(routes.Profile,"scene_skills");Assert.False(port.Observe().NavigationActive);Assert.False(port.Observe().NavigationActive);
        Assert.Single(clicks);Assert.Null(session.CurrentNavigationId);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void FishingMenuWaitRetriesOnlyOriginalPageAndStopsAtBound(bool unexpectedPage)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"character_menu":{"anchors":["scene_bag"],"coordinate_targets":{"character.open_life_skills":[60,560,61,561]}},"scene":{"anchors":["scene_skills"]}}""");
        routes.Profile.Definition["idle_fishing"]=new JsonObject();
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);var time=new RecoveryTime();
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=new ScreenFishingPort(session,"point_fishing_00","menu",new MemoryStore(new JsonObject{["status"]="running"}),time);
        port.Observe();port.Perform(new("navigate","point_fishing_00"));Assert.Single(clicks);
        port.Observe();Assert.Single(clicks);
        if(unexpectedPage)native.CurrentImage=Stamp(routes.Profile,"scene_skills");
        else
        {
            time.Seconds=8;port.Observe();Assert.Equal(2,clicks.Count);
            time.Seconds=16;port.Observe();Assert.Equal(3,clicks.Count);
            time.Seconds=24;port.Observe();Assert.Equal(4,clicks.Count);
            time.Seconds=32;port.Observe();Assert.Equal(5,clicks.Count);
        }
        time.Seconds=40;var error=Assert.Throws<TimeoutException>(()=>port.Observe());
        Assert.Contains("character.open_life_skills",error.Message);Assert.Contains("expected=character_life_skills",error.Message);
        port.Observe();Assert.Equal(unexpectedPage?1:5,clicks.Count);Assert.False(session.Guard.Paused);
    }
    [Fact] public void FishingNavigationScrollsMissingSkillWithinBoundWithoutExtraCapture()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        var native=new NoInputOps(Stamp(routes.Profile,"preset_life_header","skills_close","preset_life_0"),[]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateFishingPort("point_fishing_00","scroll",new MemoryStore(new JsonObject{["status"]="running"}));
        port.Observe();Assert.Equal("character_life_skills",session.CurrentPage);var captures=native.Captures;
        port.Perform(new("navigate","point_fishing_00"));Assert.Equal(captures,native.Captures);Assert.Equal(3,native.WheelDeltas.Count);
        for(var i=0;i<11;i++)port.Observe();
        Assert.Equal(36,native.WheelDeltas.Count);Assert.All(native.WheelDeltas,delta=>Assert.Equal(-120,delta));Assert.Throws<TimeoutException>(()=>port.Observe());
        port.Observe();Assert.Equal(36,native.WheelDeltas.Count);Assert.False(session.Guard.Paused);
    }
    [Fact] public void FishingMenuAdvancesOnExpectedPageWithoutRetryingPreviousClick()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"character_menu":{"anchors":["scene_bag"],"coordinate_targets":{"character.open_life_skills":[60,560,61,561]}},"character_life_skills":{"anchors":["scene_skills"],"coordinate_targets":{"gathering.open_skill_8":[100,100,101,101]}}}""");
        routes.Profile.Definition["idle_fishing"]=new JsonObject();var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateFishingPort("point_fishing_00","advance",new MemoryStore(new JsonObject{["status"]="running"}));
        port.Observe();port.Perform(new("navigate","point_fishing_00"));native.CurrentImage=Stamp(routes.Profile,"scene_skills");
        port.Observe();port.Observe();Assert.Equal(new PixelPoint[]{new(60,560),new(100,100)},clicks);
    }
    [Fact] public void FishingDoesNotRequireNicknamePreparationOrModifySettingsOnResume()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateFishingPort("point_fishing_00","f",new MemoryStore());
        Assert.True(port.ObserveOnly().PreparationComplete);session.Pause();session.Resume();port.InvalidatePreparation();
        Assert.True(port.ObserveOnly().PreparationComplete);Assert.Empty(clicks);Assert.Empty(native.Keys);Assert.Empty(native.WheelDeltas);
    }
    [Fact] public void FishingUnstablePreflightCannotBecomeUnknownConsumption()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"scene.dismount":"scene_bag"}}}""");
        routes.Profile.Definition["idle_fishing"]=new JsonObject();var clicks=new List<PixelPoint>();
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks),new EmptyOcr(),new MemoryStore());session.Start();
        var fishingState=new MemoryStore();
        var fishing=session.CreateFishingPort("point_fishing_00","f",fishingState);
        Assert.Throws<InputRejectedBeforeDispatchException>(()=>fishing.Perform(new("start")));Assert.Empty(clicks);
        Assert.True(fishing.ObserveOnly().Mounted);fishing.Perform(new("dismount"));Assert.Single(clicks);
    }
    [Fact] public void MaintenanceCanLeaveRecognizedSharedPageBeforeInspectingBackpack()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"character_life_skills":{"anchors":["scene_bag"],"coordinate_targets":{"page.close":[1222,18,1262,58]}}}""");
        var clicks=new List<PixelPoint>();using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks),new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("m",new MemoryStore());port.Observe();port.Observe();Assert.Null(port.Navigate("backpack_weight"));Assert.Single(clicks);
        Assert.Null(port.Navigate("backpack_weight"));Assert.Single(clicks);
    }
    [Fact] public void MaintenanceAndFishingUseTheirCurrentFramesAfterExplicitResume()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"character.open":"scene_bag"}}}""");
        routes.Profile.Definition["idle_fishing"]=JsonNode.Parse("""{"anchors":{"cast":"scene_bag"}}""");
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(Stamp(routes.Profile,"scene_bag")),new EmptyOcr(),new MemoryStore());session.Start();
        var maintenance=session.CreateMaintenancePort("m",new MemoryStore());var fishing=session.CreateFishingPort("point_fishing_00","f",new MemoryStore());
        maintenance.Observe();Assert.True(fishing.ObserveOnly().Stable);
        Assert.True(maintenance.Observe().Stable);Assert.True(fishing.ObserveOnly().Stable);
        session.Pause();session.Resume();Assert.True(maintenance.Observe().Stable);Assert.True(fishing.ObserveOnly().Stable);
        Assert.True(maintenance.Observe().Stable);Assert.True(fishing.ObserveOnly().Stable);
    }
    [Fact] public void FixedFishingMovementSurfacePreservesSceneCharacterNavigation()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"character.open":"scene_bag"}}}""");
        routes.Profile.Definition["idle_fishing"]=JsonNode.Parse("""{"controls":{"move":[100,100,200,200]}}""");
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(Stamp(routes.Profile,"scene_bag")),new EmptyOcr(),new MemoryStore());session.Start();
        var fishing=session.CreateFishingPort("point_fishing_00","f",new MemoryStore());fishing.ObserveOnly();Assert.True(fishing.ObserveOnly().Stable);
        Assert.Contains("character.open",session.LastObservation!.View.Targets.Keys);Assert.DoesNotContain("fishing.leave",session.LastObservation.View.Targets.Keys);
        Assert.Contains("fishing.move",session.LastObservation.View.Targets.Keys);fishing.Perform(new("move",Direction:"w",DurationSeconds:.12));
    }
    [Theory][InlineData(false,true)][InlineData(true,false)]
    public void RepairToolStatusUsesVisibleDisabledGlyphAndNeverWhiteEnabledText(bool enabled,bool expectedSkip)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"repair_equipment":{"anchors":["scene_bag"],"targets":{"repair.tools_status":"maint_tools_no_repair"}}}""");
        var pixels=Stamp(routes.Profile,"scene_bag").CopyPixels();
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(RecognitionTests.Root(),"tests/fixtures/screen_ui/repair-tools-disabled.png"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var width=rgb.Width;var height=rgb.Height;var source=new byte[width*height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,source,0,source.Length);
        for(var y=0;y<height;y++)for(var x=0;x<width;x++)
        {
            var s=(y*width+x)*3;var p=((824+y)*1280+310+x)*3;
            var white=enabled&&x>=17&&x<106&&y>=18&&y<43&&source[s]>110;
            for(var c=0;c<3;c++)pixels[p+c]=white?(byte)240:source[s+c];
        }
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(new(1280,960,pixels)),new EmptyOcr(),new MemoryStore());
        session.Start();var port=session.CreateMaintenancePort("m",new MemoryStore());port.Observe();
        Assert.Equal(expectedSkip,port.Observe().RepairNotNeeded);
    }
    [Theory][InlineData(false)][InlineData(true)] public void CurrentEnabledToolsLabelAllowsQuoteNavigationWithoutPreview(bool known)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"repair_equipment":{"anchors":["scene_bag"],"targets":{"repair.tools_status":"maint_tools_no_repair"},"coordinate_targets":{"repair.prepare":[485,900,565,925]}}}""");
        var pixels=Stamp(routes.Profile,"scene_bag").CopyPixels();
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(RecognitionTests.Root(),"tests/fixtures/screen_ui/repair-controls-enabled.png"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var width=rgb.Width;var height=rgb.Height;var source=new byte[width*height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,source,0,source.Length);
        for(var y=0;y<height;y++)Array.Copy(source,y*width*3,pixels,((824+y)*1280+280)*3,width*3);
        var clicks=new List<PixelPoint>();var ops=new NoInputOps(new(1280,960,pixels),clicks);using var session=new ScreenWorkflowSession(routes,Window(),ops,known?new PreviewOcr():new EmptyOcr(),new MemoryStore());
        session.Start();var port=session.CreateMaintenancePort("m",new MemoryStore());port.Observe();var current=port.Observe();
        Assert.False(current.RepairNotNeeded);Assert.Contains("repair.tools_status",session.LastObservation!.View.Targets.Keys);
        Assert.Null(port.Navigate("repair_quote"));Assert.Single(clicks);
    }
    private sealed class PreviewOcr:IOcrEngine{public OcrLine? Recognize(int width,int height,byte[] grayPixels)=>new("21886",.99);}
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)]
    public void SelectedCharacterUsesOneOfSixAccountSlots(int slot)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var pixels=new byte[1280*960*3];var i=slot-1;var left=new[]{87,368,649,930}[i%4];var top=i<4?117:565;
        for(var y=top+15;y<Math.Min(top+380,960);y++)for(var x=left;x<left+4;x++){var p=(y*1280+x)*3;pixels[p]=220;pixels[p+1]=180;pixels[p+2]=80;}
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(),new EmptyOcr(),new MemoryStore());var relogin=session.ConfigureRelogin(new MemoryStore());
        Assert.Equal(slot,relogin.SelectedSlot(new(Window(),new(1280,960,pixels),1)));
        Assert.Null(relogin.SelectedSlot(new(Window(),new(1280,960,new byte[pixels.Length]),2)));
        Assert.True(new PixelBox(0,0,1280,960).Contains(ScreenRelogin.SlotCenter(slot)));
    }
    [Theory][InlineData("account","character","改名",true)][InlineData("other","character","抓包名",false)][InlineData("account","other","抓包名",false)]
    public void ReloginIdentityUsesAccountAndCharacterId(string account,string character,string name,bool matches)
    {
        using var session=new ScreenWorkflowSession(ScreenRoutes.Load(RecognitionTests.Root()),Window(),new NoInputOps(),new EmptyOcr(),new MemoryStore());session.Start();var relogin=session.ConfigureRelogin(new MemoryStore());relogin.Begin(TestWho,true,true);
        Assert.Equal(matches,relogin.MatchesPacketIdentity(new(account,character,name)));Assert.False(relogin.MatchesPacketIdentity(null));
    }
    [Theory][InlineData("scene","scene.open_bag",73)][InlineData("bag","bag.close",73)][InlineData("repair_tools","bag.close",73)][InlineData("scene","scene.open_skills",75)][InlineData("skills","skills.close",75)]
    public void KnownNavigationUsesGuardedShortcutInBothEntryPoints(string page,string target,ushort key)
    {
        foreach(var step in new[]{false,true})
        {
            var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
            routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
            routes.Profile.Definition["pages"]=new JsonObject{[page]=new JsonObject{["anchors"]=new JsonArray("scene_bag"),["targets"]=new JsonObject{[target]="scene_bag"}}};
            var clicks=new List<PixelPoint>();var ops=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks){Background=true};
            using var session=new ScreenWorkflowSession(routes,Window(),ops,new EmptyOcr(),new MemoryStore());
            session.Start();session.Observe();session.Observe();
            if(step)session.Step(new JsonObject{["target"]=target});else session.NavigateTarget(target);
            Assert.Equal(new[]{key},ops.Keys);Assert.Empty(clicks);
            session.Pause();Assert.Throws<InputBlockedException>(()=>session.NavigateTarget(target));Assert.Single(ops.Keys);
        }
    }
    [Fact] public void PacketGoldExpiresAndSpendingInvalidatesOnlyCurrentFullSync()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var packet=new PacketGoldObservation(609136,"a:1",System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency);
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(Stamp(routes.Profile,"scene_bag")),new EmptyOcr(),new MemoryStore());
        session.SetPacketGoldProvider(()=>packet);Assert.Equal(609136,session.ResolveGold(null));Assert.Equal(600000,session.ResolveGold(600000));
        session.InvalidatePacketGold();Assert.Null(session.ResolveGold(null));Assert.Equal(500000,session.ResolveGold(500000));
        packet=packet with{Version="a:2",Balance=500000};Assert.Equal(500000,session.ResolveGold(null));packet=packet with{At=packet.At-31};Assert.Null(session.ResolveGold(null));
    }
    [Fact] public void FreshPacketGoldSkipsOcrAndStaleOrSpentGoldRequiresScreen()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var now=System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency;
        var packet=new PacketGoldObservation(609136,"a:1",now);var reads=0;
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(),new EmptyOcr(),new MemoryStore());
        session.SetPacketGoldProvider(()=>packet);
        long? Screen(){reads++;return 590000;}
        Assert.Equal(609136,session.ReadGold(Screen));Assert.Equal(0,reads);
        session.InvalidatePacketGold();Assert.Equal(590000,session.ReadGold(Screen));Assert.Equal(1,reads);
        packet=packet with{Version="a:2",At=now-31};Assert.Equal(590000,session.ReadGold(Screen));
        packet=packet with{At=now+60};Assert.Null(session.CurrentPacketGold());
        packet=packet with{At=double.NaN};Assert.Null(session.CurrentPacketGold());
        packet=packet with{At=now,Balance=-1};Assert.Null(session.CurrentPacketGold());Assert.Null(session.ResolveGold(-1));
    }
    [Theory] [InlineData("click",false)] [InlineData("right",true)] [InlineData("drag",true)]
    public void NavigationStepRejectsConsumptionImageAliasesAndCoordinateAliases(string kind,bool coordinate)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        var template=routes.Profile.Templates["scene_bag"];
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"purchase.confirm":"scene_bag","debug.alias":"scene_bag"},"coordinate_targets":{"debug.safe":[100,100,110,110]}}}""");
        if(coordinate){routes.Profile.Definition["pages"]!["scene"]!["targets"]!.AsObject().Remove("debug.alias");routes.Profile.Definition["pages"]!["scene"]!["coordinate_targets"]!["debug.alias"]=new JsonArray(template.Box.Left,template.Box.Top,template.Box.Right,template.Box.Bottom);}
        var clicks=new List<PixelPoint>();using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks),new EmptyOcr(),new MemoryStore());session.Start();session.Observe();session.Observe();
        var step=new JsonObject{["target"]="debug.alias",["kind"]=kind,["destination"]=new JsonArray(300,300)};
        Assert.Throws<InputBlockedException>(()=>session.Step(step));Assert.Empty(clicks);
        session.Step(new JsonObject{["target"]="debug.safe"});Assert.Single(clicks);
        // The trusted consumption path remains separate and requires its persisted pending intent.
        var store=new MemoryStore(new JsonObject{["pending"]=new JsonObject{["kind"]="purchase"}});
        session.Consume("purchase.confirm","test",store,"purchase",_=>true);Assert.Equal(2,clicks.Count);
    }
    [Fact] public void FixedFishingSurfacePreservesSceneAndConfiguredShape()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"coordinate_targets":{"fishing.start":{"box":[1160,850,1220,910],"shape":"circle","random":true}}}}""");
        routes.Profile.Definition["idle_fishing"]=JsonNode.Parse("""{"controls":{"start":[1160,850,1220,910]}}""");
        routes.Configuration["gathering_routes"]!["test_fishing"]=JsonNode.Parse("""{"navigation":{"steps":[]}}""");
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(Stamp(routes.Profile,"scene_bag")),new EmptyOcr(),new MemoryStore());
        session.Start();var port=session.CreateFishingPort("test_fishing","f",new MemoryStore());port.ObserveOnly();port.ObserveOnly();
        var view=session.LastObservation!.View;Assert.Equal("scene",view.Page);
        Assert.Contains("fishing.start",view.RandomTargets!);Assert.Equal(ClickRegionShape.Ellipse,view.TargetShapes!["fishing.start"]);
        var region=view.TargetRegions!["fishing.start"];Assert.Equal(region.Width,region.Height);Assert.True(region.Contains(view.Targets["fishing.start"],ClickRegionShape.Ellipse));
    }
    [Theory] [InlineData("rectangle",ClickRegionShape.Rectangle)] [InlineData("ellipse",ClickRegionShape.Ellipse)] [InlineData("circle",ClickRegionShape.Ellipse)]
    public void CoordinateShapeKeepsVerifiedRegionAndRejectsMalformedConfiguration(string shape,ClickRegionShape expected)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var definition=(JsonObject)routes.Profile.Definition.DeepClone();definition.Remove("page_variants");
        definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"coordinate_targets":{"scene.open_bag":{"box":[100,100,140,140],"shape":"rectangle"}}}}""");
        definition["pages"]!["scene"]!["coordinate_targets"]!["scene.open_bag"]!["shape"]=shape;
        var profile=NavigationProfile.FromDefinition(RecognitionTests.Root(),definition);using var recognizer=new ScreenPageRecognizer(profile);var view=recognizer.Recognize(Stamp(profile,"scene_bag"));
        Assert.Equal(new PixelBox(100,100,140,140),view.TargetRegions!["scene.open_bag"]);Assert.Equal(expected,view.TargetShapes!["scene.open_bag"]);
        definition["pages"]!["scene"]!["coordinate_targets"]!["scene.open_bag"]!["random"]=true;
        var randomized=NavigationProfile.FromDefinition(RecognitionTests.Root(),definition);using var randomRecognizer=new ScreenPageRecognizer(randomized);
        Assert.Contains("scene.open_bag",randomRecognizer.Recognize(Stamp(randomized,"scene_bag")).RandomTargets!);
        definition["pages"]!["scene"]!["coordinate_targets"]!["scene.open_bag"]!["shape"]="triangle";Assert.Throws<InvalidDataException>(()=>NavigationProfile.FromDefinition(RecognitionTests.Root(),definition));
        definition["pages"]!["scene"]!["coordinate_targets"]!["scene.open_bag"]!["shape"]="circle";definition["pages"]!["scene"]!["coordinate_targets"]!["scene.open_bag"]!["box"]=new JsonArray(100,100,140,160);Assert.Throws<InvalidDataException>(()=>NavigationProfile.FromDefinition(RecognitionTests.Root(),definition));
    }
    [Fact] public void ImageRandomClickUsesMatchedBoundsAndInscribesCircle()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var definition=(JsonObject)routes.Profile.Definition.DeepClone();definition.Remove("page_variants");definition.Remove("coordinate_control_targets");
        definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"scene.open_bag":"scene_bag"},"random_targets":{"scene.open_bag":{"shape":"circle"}}}}""");
        var profile=NavigationProfile.FromDefinition(RecognitionTests.Root(),definition);using var recognizer=new ScreenPageRecognizer(profile);var view=recognizer.Recognize(Stamp(profile,"scene_bag"));
        var actual=view.TargetRegions!["scene.open_bag"];Assert.Equal(actual.Width,actual.Height);Assert.Equal(ClickRegionShape.Ellipse,view.TargetShapes!["scene.open_bag"]);Assert.Contains("scene.open_bag",view.RandomTargets!);
        Assert.True(profile.Templates["scene_bag"].Box.Contains(actual.Center));
    }
    [Theory] [InlineData(-20,true)] [InlineData(20,true)] [InlineData(20,false)]
    public void RepairTravelCardAllowsLayoutShiftButRequiresNpcName(int shift,bool named)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var pixels=new byte[1280*960*3];
        foreach(var key in named?new[]{"maint_ferghus_name","maint_map_close","purchase.map_region"}:new[]{"maint_map_close","purchase.map_region"})
        {
            var template=routes.Profile.Templates[key];var source=template.Image.CopyPixels();
            for(var y=0;y<template.Image.Height;y++)Array.Copy(source,y*template.Image.Width*3,pixels,((template.Box.Top+y+(key=="maint_ferghus_name"?shift:0))*1280+template.Box.Left)*3,template.Image.Width*3);
        }
        using var recognizer=new ScreenPageRecognizer(routes.Profile);var view=recognizer.Recognize(new RgbImage(1280,960,pixels));
        if(named){Assert.Equal("repair_travel",view.Page);Assert.True(view.Targets.ContainsKey("repair.travel"));}
        else Assert.False(view.Targets.ContainsKey("repair.travel"));
    }
    [Fact] public void MaintenanceWaitsForDestinationInsteadOfIssuingNextStageOnSamePage()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"scene.open_bag":"scene_bag"}}}""");
        routes.Configuration["maintenance"]!["navigation"]!["test"]=JsonNode.Parse("""{"scene":{"start":"first","stages":{"first":{"choices":[{"target":"scene.open_bag","next":"second","settle_s":0,"wait_for_pages":["repair_quote"]}]},"second":{"choices":[{"target":"scene.open_bag"}]}}}}""");
        var template=routes.Profile.Templates["scene_bag"];var pixels=new byte[1280*960*3];var source=template.Image.CopyPixels();
        for(var y=0;y<template.Image.Height;y++)Array.Copy(source,y*template.Image.Width*3,pixels,((template.Box.Top+y)*1280+template.Box.Left)*3,template.Image.Width*3);
        var clicks=new List<PixelPoint>();var ops=new NoInputOps(new(1280,960,pixels),clicks);using var session=new ScreenWorkflowSession(routes,Window(),ops,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("m",new MemoryStore());port.Observe();port.Observe();
        for(var n=0;n<5;n++)Assert.Null(port.Navigate("test"));Assert.Empty(clicks);Assert.Equal(new ushort[]{73},ops.Keys);Assert.NotNull(session.CurrentNavigationId);
        port.ResetNavigation();Assert.Null(session.CurrentNavigationId);
    }
    [Fact] public void MapFeedbackIgnoresTinyEffectsAndRecognizesChangedTerrain()
    {
        var before=new byte[40*24*3];var tiny=(byte[])before.Clone();tiny[0]=255;
        Assert.False(ScreenMaintenancePort.MapChanged(before,tiny));Assert.True(ScreenMaintenancePort.MapChanged(before,Enumerable.Repeat((byte)100,before.Length).ToArray()));
    }
    [Fact] public void CoordinateControlsNeedPageIdentityButNotTheirButtonGlyphs()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var definition=(JsonObject)routes.Profile.Definition.DeepClone();
        definition["pages"]=JsonNode.Parse("""{"bag":{"anchors":["bag_fashion_tab","bag_close"],"targets":{"bag.close":"bag_close"}}}""");definition.Remove("page_variants");
        var profile=NavigationProfile.FromDefinition(RecognitionTests.Root(),definition);using var recognizer=new ScreenPageRecognizer(profile);
        var image=Stamp(profile,"bag_fashion_tab");var view=recognizer.Recognize(image);
        Assert.Equal("bag",view.Page);Assert.Equal(profile.Templates["bag_close"].Box.Center,view.Targets["bag.close"]);
        Assert.DoesNotContain(view.Hits,h=>h.Name=="bag_close");Assert.Empty(recognizer.Recognize(new RgbImage(1280,960,new byte[1280*960*3])).Targets);
        definition["pages"]!["bag"]!["coordinate_targets"]=JsonNode.Parse("""{"bag.close":[1270,900,1300,950]}""");
        Assert.Throws<InvalidDataException>(()=>NavigationProfile.FromDefinition(RecognitionTests.Root(),definition));
    }
    [Fact] public void DialogueExitUsesEscapeAtTheObservedMenuInsteadOfAnOldButtonCoordinate()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        var definition=routes.Profile.Definition;
        definition["pages"]=JsonNode.Parse("""{"repair_dialog":{"anchors":["maint_dialog_shop"],"targets":{"dialog.end":"maint_dialog_end"}}}""");
        definition.Remove("page_variants");
        var ops=new NoInputOps(Stamp(routes.Profile,"maint_dialog_shop","maint_dialog_end"),[]);
        using var session=new ScreenWorkflowSession(routes,Window(),ops,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("dialogue-exit",new MemoryStore());port.Observe();port.Observe();
        port.Navigate("repair_tools");Assert.Equal(new ushort[]{27},ops.Keys);
        port.Navigate("repair_tools");Assert.Single(ops.Keys);
    }
    [Fact] public void AllFlowsShareCoordinateControlsAndKeepDynamicEvidence()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var pages=routes.Profile.Definition["pages"]!;
        Assert.NotNull(pages["skills"]?["coordinate_targets"]?["skills.close"]);
        foreach(var (page,target) in new[]{("scene","scene.open_map"),("bag","bag.search_open"),("bag_search","bag.search_apply"),("category_page","category.open_metal"),("recipe_detail","recipe_detail.close"),("iron_vein_detail","gathering.start"),("storage_detail","storage.prepare"),("storage_transfer","storage.detail_close"),("tidy","tidy.cancel"),("repair_quote","repair.back"),("shop","purchase.increase")})
            Assert.NotNull(pages[page]?["coordinate_targets"]?[target]);
        Assert.NotNull(pages["region_map"]!["targets"]!["map.repair_npc"]);Assert.NotNull(pages["shop"]!["targets"]!["purchase.confirm"]);
        Assert.Null(pages["shop_region_map"]);Assert.NotNull(pages["bag"]!["targets"]!["bag.items_selected"]);
        var nav=routes.Configuration["maintenance"]!["navigation"]!;
        foreach(var destination in new[]{"repair_quote","purchase_shop"})
        {
            var stages=nav[destination]!["region_map"]!["stages"]!;Assert.Equal("zoom_max",stages["zoom"]!["search"]![0]!["kind"]!.GetValue<string>());
            Assert.Null(stages["portrait"]!["search"]);
        }
    }
    [Fact] public void MaximumZoomMustSaturateBeforeNpcSelection()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"region_map":{"anchors":["scene_bag"],"coordinate_targets":{"map.canvas":[400,300,800,700],"map.npc":[600,400,620,420]}}}""");routes.Profile.Definition.Remove("page_variants");
        routes.Configuration["maintenance"]!["navigation"]!["test"]=JsonNode.Parse("""{"region_map":{"start":"zoom","stages":{"zoom":{"search":[{"target":"map.canvas","kind":"zoom_max","settle_s":0,"next":"portrait"}]},"portrait":{"choices":[{"target":"map.npc","kind":"right","settle_s":0,"wait_for_pages":["repair_travel"]}]}}}}""");
        var clicks=new List<PixelPoint>();var ops=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);using var session=new ScreenWorkflowSession(routes,Window(),ops,new EmptyOcr(),new MemoryStore());session.Start();var port=session.CreateMaintenancePort("m",new MemoryStore());port.Observe();port.Observe();
        Assert.Null(port.Navigate("test"));Assert.Equal(12,ops.Wheels);Assert.Empty(clicks);
        Assert.Null(port.Navigate("test"));Assert.Single(clicks);Assert.Equal(new PixelPoint(610,410),clicks[0]);
        Assert.Null(port.Navigate("test"));Assert.Single(clicks);Assert.Equal(12,ops.Wheels);
    }
    private static RgbImage Stamp(NavigationProfile profile,params string[] names)
    {
        var pixels=new byte[1280*960*3];foreach(var name in names){var t=profile.Templates[name];var source=t.Image.CopyPixels();for(var y=0;y<t.Image.Height;y++)Array.Copy(source,y*t.Image.Width*3,pixels,((t.Box.Top+y)*1280+t.Box.Left)*3,t.Image.Width*3);}return new(1280,960,pixels);
    }
    [Theory] [InlineData("craft_unavailable")] [InlineData("materials_unknown")] [InlineData("device_full")] [InlineData("device_batch_not_ready")]
    public void LocalPauseDoesNotStopSharedGuardOrClearManualPause(string reason)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(),new EmptyOcr(),new MemoryStore());
        var port=session.CreateMaintenancePort("m",new MemoryStore());session.Start();var epoch=session.InputEpoch;port.Pause(reason);
        Assert.False(session.Guard.Paused);Assert.Equal(epoch,session.InputEpoch);
        session.Pause();port.Pause(reason);port.PauseLocal("等待补充空瓶");Assert.True(session.Guard.Paused);Assert.Equal("manual",session.Guard.PauseReason);Assert.Throws<InputBlockedException>(port.Resume);
    }
    [Theory] [InlineData("user_input")] [InlineData("runtime_error")] [InlineData("reconciliation_required")]
    public void PortResumeCannotClearAnyGlobalInputPause(string reason)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(),new EmptyOcr(),new MemoryStore());
        session.Start();session.Pause(reason);Assert.Throws<InputBlockedException>(session.CreateMaintenancePort("m",new MemoryStore()).Resume);Assert.True(session.Guard.Paused);Assert.Equal(reason,session.Guard.PauseReason);
    }
    [Fact] public void ReloginClearsOldWorkflowRecognitionAndTimesOutWithoutInput()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition["pages"]=JsonNode.Parse("{\"scene\":{\"anchors\":[\"scene_bag\"],\"targets\":{}}}");
        var template=routes.Profile.Templates["scene_bag"];var pixels=new byte[1280*960*3];var source=template.Image.CopyPixels();
        for(var y=0;y<template.Image.Height;y++)Array.Copy(source,y*template.Image.Width*3,pixels,((template.Box.Top+y)*1280+template.Box.Left)*3,template.Image.Width*3);
        using var s=new ScreenWorkflowSession(routes,Window(),new NoInputOps(new(1280,960,pixels)),new EmptyOcr(),new MemoryStore());
        s.Start();s.Use((_,view)=>view with{Page="fishing"});Assert.Equal("fishing",s.Observe().View.Page);
        var time=new RecoveryTime();var relogin=s.ConfigureRelogin(new MemoryStore(),time);relogin.Begin(TestWho,true,true);
        Assert.Equal("scene",s.Observe().View.Page);s.Use((_,view)=>view with{Page="fishing"});Assert.Equal("scene",s.Observe().View.Page);
        time.Seconds=61;Assert.Equal("running",relogin.Tick(false,noticeDismissed:true));
        time.Seconds=121;Assert.Equal("blocked",relogin.Tick(false,noticeDismissed:true));Assert.Contains("确认抓包角色超时",relogin.Snapshot()["reason"]!.GetValue<string>());Assert.False(relogin.Active);
    }
    [Fact] public void ReloginRecoveryCanCloseAnAlreadyOpenedMenu()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"relogin.menu_settings"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();var relogin=session.ConfigureRelogin(new MemoryStore());relogin.Begin(TestWho,true,true);
        Assert.Equal("relogin_menu",session.Observe().View.Page);Assert.True(session.LastObservation!.View.Targets.ContainsKey("page.close"));relogin.Tick(false);
        Assert.Equal(0,clicks.Count+native.Keys.Count);Assert.Equal("exit",relogin.Snapshot()["phase"]!.GetValue<string>());Assert.Null(relogin.Snapshot()["pending"]);
    }
    [Fact] public void ReloginLeavesSkillsThroughSharedShortcutAndHonorsPause()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=new JsonObject{["skills"]=new JsonObject{["anchors"]=new JsonArray("scene_bag"),["targets"]=new JsonObject{["skills.close"]="scene_bag"}}};
        var clicks=new List<PixelPoint>();var ops=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks){Background=true};
        using var session=new ScreenWorkflowSession(routes,Window(),ops,new EmptyOcr(),new MemoryStore());session.Start();
        var relogin=session.ConfigureRelogin(new MemoryStore());relogin.Begin(TestWho,true,true);session.Observe();session.Observe();
        Assert.Equal("running",relogin.Tick(false));Assert.Equal(new ushort[]{75},ops.Keys);Assert.Empty(clicks);Assert.Null(relogin.Snapshot()["pending"]);
        session.Pause("manual");Assert.Equal("blocked",relogin.Tick(false));Assert.Single(ops.Keys);
    }
    [Fact] public void NoticeDispatchUsesDeclaredObservationWithoutAnotherCapture()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=new JsonObject{["scene"]=new JsonObject{["anchors"]=new JsonArray("scene_bag"),["targets"]=new JsonObject()}};
        var clicks=new List<PixelPoint>();var ops=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks){Background=true};
        using var session=new ScreenWorkflowSession(routes,Window(),ops,new EmptyOcr(),new MemoryStore());session.Start();var reads=0;
        session.Use((_,view)=>++reads<=3?view with{Page="notification_test",Targets=new Dictionary<string,PixelPoint>{{"notification.close",new(20,20)}}}:view with{Page="unknown",Targets=new Dictionary<string,PixelPoint>()});
        session.Observe();session.Observe();var captures=ops.Captures;Assert.True(session.DismissNotice());Assert.Single(clicks);Assert.False(session.Guard.Paused);Assert.Equal(captures+1,ops.Captures);
        session.Pause("manual");Assert.False(session.DismissNotice());Assert.Single(clicks);
    }
    [Fact] public void ReloginPauseAndRestoredIntentNeverResumeOrReplay()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var store=new MemoryStore();
        using var s=new ScreenWorkflowSession(routes,Window(),new NoInputOps(),new EmptyOcr(),new MemoryStore());s.Start();
        var relogin=s.ConfigureRelogin(store);relogin.Begin(TestWho,true,true);s.Pause("focus_lost");
        Assert.Equal("blocked",relogin.Tick(false));Assert.Contains("focus_lost",relogin.Snapshot()["reason"]!.GetValue<string>());
        store.Flush(new JsonObject{["status"]="running",["phase"]="enter",["pending"]=new JsonObject{["kind"]="relogin_enter"}});
        relogin=s.ConfigureRelogin(store);Assert.False(relogin.Active);Assert.NotNull(relogin.Snapshot()["pending"]);Assert.Throws<InputBlockedException>(()=>relogin.Begin(TestWho,true,true));
    }
    private sealed class RecoveryTime:TimeProvider{public double Seconds;public override DateTimeOffset GetUtcNow()=>DateTimeOffset.UnixEpoch.AddSeconds(Seconds);}
    [Theory][InlineData("bag","recipe_preview")][InlineData("repair_tools","recipe_preview")][InlineData("repair_tools","queue")][InlineData("repair_tools","leave")] public void ProductionNavigationLeavesFilteredBagBeforeOpeningSkills(string page,string destination)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition["pages"]=new JsonObject{[page]=JsonNode.Parse("""{"anchors":["bag_close"],"targets":{"bag.close":"bag_close"}}""")};
        var template=routes.Profile.Templates["bag_close"];var pixels=new byte[1280*960*3];var source=template.Image.CopyPixels();for(var y=0;y<template.Image.Height;y++)Array.Copy(source,y*template.Image.Width*3,pixels,((template.Box.Top+y)*1280+template.Box.Left)*3,template.Image.Width*3);
        var clicks=new List<PixelPoint>();var ops=new NoInputOps(new(1280,960,pixels),clicks);using var session=new ScreenWorkflowSession(routes,Window(),ops,new EmptyOcr(),new MemoryStore());var route=routes.Recipes["iron_ingot_from_iron_ore"];var port=session.CreateProductionPort(route.Key,"test",new MemoryStore());session.Start();port.Observe();port.Observe();port.Navigate(destination,new(route.Key,route.Device,route.DurationSeconds));Assert.Empty(clicks);Assert.Equal(new ushort[]{73},ops.Keys);port.Navigate(destination,new(route.Key,route.Device,route.DurationSeconds));Assert.Single(ops.Keys);
    }
    [Theory][InlineData("gathering_soft_log")][InlineData("gathering_hard_log")]
    public void GatheringNavigationClosesToolBagOnceBeforeSelectingSource(string key)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"repair_tools":{"anchors":["bag_close"],"targets":{"bag.close":"bag_close"}},"scene":{"anchors":["scene_bag"],"targets":{"character.open":"scene_bag"}}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"bag_close"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var route=new RuntimeCatalog(routes.Configuration).Gathering[key];var port=session.CreateGatheringPort(key,"bag-handoff",new MemoryStore());
        Assert.Equal("repair_tools",port.ObserveOnly(route,"bag-handoff").Page);port.Navigate(route);port.Navigate(route);
        Assert.Equal(new ushort[]{73},native.Keys);Assert.Empty(clicks);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag");Assert.Equal("scene",port.ObserveOnly(route,"bag-handoff").Page);port.Navigate(route);
        Assert.Single(clicks);Assert.Single(native.Keys);
    }
    [Fact] public void RunningPortResumePreservesEpochAndExplicitResumeUsesOneCurrentFrame()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());
        routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"scene.open_bag":"scene_bag"}}}""");
        var template=routes.Profile.Templates["scene_bag"];var pixels=new byte[1280*960*3];var source=template.Image.CopyPixels();
        for(var y=0;y<template.Image.Height;y++)Array.Copy(source,y*template.Image.Width*3,pixels,((template.Box.Top+y)*1280+template.Box.Left)*3,template.Image.Width*3);
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(new(1280,960,pixels)),new EmptyOcr(),new MemoryStore());
        var port=session.CreateMaintenancePort("m",new MemoryStore());session.Start();
        Assert.True(session.Observe().Stable);
        var epoch=session.InputEpoch;port.Resume();port.Resume();
        Assert.Equal(epoch,session.InputEpoch);Assert.True(session.Observe().Stable);
        session.ResumeExplicit();Assert.True(session.InputEpoch>epoch);Assert.True(session.Observe().Stable);
        Assert.True(session.Observe().Stable);session.Pause();Assert.Throws<InputBlockedException>(port.Resume);
        session.ResumeExplicit();Assert.True(session.Observe().Stable);
    }
    [Fact] public void ProcessFallbackNeverInventsAccountOrCharacter()
    {
        var first=ScreenWorkflowSession.ProcessScope(Window());var second=ScreenWorkflowSession.ProcessScope(Window() with{ProcessStartUtcTicks=44});
        Assert.Null(first.AccountId);Assert.Null(first.CharacterId);Assert.False(first.HasIdentity);Assert.NotEqual(first,second);
    }
    [Fact] public void DurableIntentFlushPrecedesAuthorizationAndIsNeverReplayedAfterRecreation()
    {
        var state=new MemoryStore(new JsonObject{["pending"]=new JsonObject{["kind"]="craft",["at"]=123d}});var journal=new MemoryStore();var scope=ScreenWorkflowSession.ProcessScope(Window());
        var (proof,view)=Evidence(scope);var hash=Hash(state.Read()!["pending"]!);
        var authority=new ScreenWorkflowSession.DurableAuthority(journal,state,scope,"batch",hash,"craft",_=>true);
        Assert.True(authority.AllowsProcessScope(scope));Assert.True(authority.VerifyPersistedIntent("batch:"+hash,proof,view));Assert.Equal(1,journal.Writes);
        var recreated=new ScreenWorkflowSession.DurableAuthority(journal,state,scope,"batch",hash,"craft",_=>true);Assert.False(recreated.VerifyPersistedIntent("batch:"+hash,proof,view));Assert.Equal(1,journal.Writes);
    }
    [Fact] public void JournalFailureAndChangedPendingCannotAuthorizeConsumption()
    {
        var state=new MemoryStore(new JsonObject{["pending"]=new JsonObject{["kind"]="craft",["at"]=123d}});var scope=ScreenWorkflowSession.ProcessScope(Window());var (proof,view)=Evidence(scope);
        var hash=Hash(state.Read()!["pending"]!);var bad=new MemoryStore(){Fail=true};var authority=new ScreenWorkflowSession.DurableAuthority(bad,state,scope,"b",hash,"craft",_=>true);Assert.Throws<IOException>(()=>authority.VerifyPersistedIntent("x",proof,view));
        state.Flush(new JsonObject{["pending"]=new JsonObject{["kind"]="craft",["at"]=124d}});Assert.False(authority.VerifyPersistedIntent("x",proof,view));
    }
    [Fact] public void AllFactoriesShareProcessGuardWithoutInvokingNativeInputOrRequiringPackets()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var native=new NoInputOps();var activated=0;using var s=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore(),()=>activated++);
        foreach(var recipe in routes.Recipes.Keys)Assert.NotNull(s.CreateProductionPort(recipe,recipe,new MemoryStore()));
        foreach(var route in routes.Configuration["gathering_routes"]!.AsObject())Assert.NotNull(s.CreateGatheringPort(route.Key,route.Key,new MemoryStore()));
        Assert.NotNull(s.CreateMaintenancePort("m",new MemoryStore()));
        s.Start();Assert.Equal(1,activated);s.Pause();Assert.True(s.Guard.Paused);s.ResumeExplicit();Assert.Equal(2,activated);
        Assert.Equal("unknown",s.CurrentPage);
    }
    [Fact] public void CurrentStorageHideRowIsSelectableBySharedPort()
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/storage-hide-row-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(new RgbImage(rgb.Width,rgb.Height,pixels)),ocr,new MemoryStore());session.Start();
        session.SetVerifiedOwnerResolver(()=>new("capture","24081432","25251211640832"));
        var plan=new WithdrawalPlan(580126530,"高級生皮",1,"character:[\"24081432\",\"25251211640832\"]");var port=session.CreateStoragePort(plan,"row",new MemoryStore());var frame=port.Observe();
        Assert.True(frame.SelectAvailable,"page="+frame.Page+"; scope="+frame.Scope+"; name="+frame.Name+"; hits="+string.Join(",",new[]{"storage_character_live","storage_backpack_title","storage_close_live"}.Select(n=>n+"="+session.Matcher.Anchor(session.LastCapture!.Image,n)?.Score)));Assert.Equal(plan.ItemId,frame.ItemId);
    }
    [Theory]
    [InlineData("storage-detail-current.jpg","storage_detail",null)]
    [InlineData("storage-transfer-keyboard-current.jpg","storage_transfer",3)]
    public void CurrentStorageDetailRetainsOwnerAndItemWithoutScreenInventoryCounts(string fixture,string page,int? quantity)
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production",fixture));using var rgb=new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(new RgbImage(rgb.Width,rgb.Height,pixels)),ocr,new MemoryStore());session.Start();
        session.SetVerifiedOwnerResolver(()=>new("capture","24081432","25251211640832"));
        var plan=new WithdrawalPlan(580126530,"高級生皮",1,"character:[\"24081432\",\"25251211640832\"]");var frame=session.CreateStoragePort(plan,"detail",new MemoryStore()).Observe();
        Assert.Equal(page,frame.Page);Assert.Equal(plan.Scope,frame.Scope);Assert.Equal(plan.ItemId,frame.ItemId);
        if(quantity is null)Assert.True(frame.TransferAvailable);else {Assert.True(frame.TransferDialog);Assert.Equal(quantity,frame.DialogQuantity);}
        Assert.Null(frame.Available);Assert.Null(frame.BagQuantity);
    }
    [Fact] public void InnRoomExitUsesMapButtonWhileTownSceneNeedsNoExtraExit()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"storage.room_exit":"storage_room_exit_icon"}},"storage_room_exit":{"anchors":["scene_skills"],"coordinate_targets":{"storage.exit_room":[645,833,862,895]}}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","storage_room_exit_icon"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("room-exit",new MemoryStore());port.Observe();Assert.False(port.ReleaseNavigation());Assert.Single(clicks);
        Assert.True(new PixelBox(1220,8,1275,60).Contains(clicks[0]));
        native.CurrentImage=Stamp(routes.Profile,"scene_skills");Thread.Sleep(320);port.Observe();Assert.False(port.ReleaseNavigation());Assert.Equal(2,clicks.Count);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag");port.Observe();Assert.True(port.ReleaseNavigation());Assert.Equal(2,clicks.Count);Assert.Empty(native.Keys);
    }
    [Fact] public void SharedNavigatorHonorsExpectedPageBeforeCompletingStep()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"storage_detail":{"anchors":["scene_bag"],"coordinate_targets":{"storage.detail_close":[1200,20,1240,60]}},"scene":{"anchors":["scene_skills"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("expected-page",new MemoryStore());var step=new JsonObject{["target"]="storage.detail_close",["expected_page"]="scene",["settle_s"]=0};
        Assert.False(port.TestNavigationStep(step).Completed);Assert.Single(clicks);
        Assert.False(port.TestNavigationStep(step).Completed);Assert.Single(clicks);
        native.CurrentImage=Stamp(routes.Profile,"scene_skills");Assert.True(port.TestNavigationStep(step).Completed);Assert.Single(clicks);
    }
    [Fact] public void StorageMenuMayReappearAfterDialogueBeforeOpeningContainer()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"storage_dialog":{"anchors":["scene_bag"],"coordinate_targets":{"storage.open":[514,865,644,916]}},"npc_dialogue":{"anchors":["scene_skills"],"coordinate_targets":{"dialog.advance":[500,100,600,200]}}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var plan=new WithdrawalPlan(198875762,"鐵礦石",1,"character_storage");var port=session.CreateStoragePort(plan,"menu-return",new MemoryStore());
        Assert.Equal("storage_dialog",port.Observe().Page);port.Perform(new("navigate",plan.ItemId,plan.Name,1,plan.Scope));Assert.Single(clicks);
        port.Observe();port.Perform(new("navigate",plan.ItemId,plan.Name,1,plan.Scope));Assert.Single(clicks);
        native.CurrentImage=Stamp(routes.Profile,"scene_skills");Assert.Equal("npc_dialogue",port.Observe().Page);port.Perform(new("navigate",plan.ItemId,plan.Name,1,plan.Scope));Assert.Equal(2,clicks.Count);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag");Thread.Sleep(1600);Assert.Equal("storage_dialog",port.Observe().Page);port.Perform(new("navigate",plan.ItemId,plan.Name,1,plan.Scope));Assert.Equal(3,clicks.Count);Assert.Empty(native.Keys);
    }
    [Fact] public void StorageExitAdvancesDialogueWithoutUsingInnShortcut()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        var leave=routes.Profile.Definition["pages"]!["storage_dialog"]!["coordinate_targets"]!["storage_dialog.leave"]!.AsArray();
        Assert.Equal(654,leave[0]!.GetValue<int>());Assert.Equal(770,leave[2]!.GetValue<int>());
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"npc_dialogue":{"anchors":["scene_skills"],"coordinate_targets":{"dialog.advance":[500,100,600,200]}},"scene":{"anchors":["scene_bag"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_skills"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("storage-exit-dialogue",new MemoryStore());Assert.Equal("npc_dialogue",port.Observe().Page);Assert.False(port.ReleaseNavigation());Assert.Single(clicks);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag");Assert.Equal("scene",port.Observe().Page);Assert.True(port.ReleaseNavigation());Assert.Empty(native.Keys);
    }
    [Fact] public void StorageSearchStartsDownwardThroughSharedListScrolling()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"storage_character":{"anchors":["scene_bag"],"surfaces":{"storage.canvas":[50,280,625,850]}}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var plan=new WithdrawalPlan(198875762,"鐵礦石",1,"character_storage");var port=session.CreateStoragePort(plan,"search-down",new MemoryStore());
        Assert.Equal(plan.Scope,port.Observe().Scope);port.Perform(new("locate",plan.ItemId,plan.Name,plan.Quantity,plan.Scope,0));
        Assert.Equal(3,native.WheelDeltas.Count);Assert.All(native.WheelDeltas,x=>Assert.Equal(-120,x));
    }
    [Fact] public void CaptureStorageScopesRequireMatchingVerifiedOwner()
    {
        var owner=new SessionScope("s","account-a","character-b");
        Assert.True(ScreenStorageSupplyPort.ScopeMatches("account:\"account-a\"",owner));
        Assert.True(ScreenStorageSupplyPort.ScopeMatches("character:[\"account-a\",\"character-b\"]",owner));
        Assert.False(ScreenStorageSupplyPort.ScopeMatches("character:[\"account-a\",\"other\"]",owner));
        Assert.False(ScreenStorageSupplyPort.ScopeMatches("account:\"account-a\"",ScreenWorkflowSession.ProcessScope(Window())));
    }
    [Fact] public void OptionalPortsDoNotInventShopCalibrationOrTriggerRelogin()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["purchase"]=new JsonObject();using var s=new ScreenWorkflowSession(routes,Window(),new NoInputOps(),new EmptyOcr(),new MemoryStore());
        var purchase=s.CreatePurchasePort(123,"空瓶","p",()=>null,()=>new());Assert.Equal("shop_calibration_missing",purchase.CalibrationStatus);
        Assert.Throws<InputBlockedException>(purchase.NavigateToShop);
        var travel=s.ConfigureTravel(new MemoryStore(),()=>new JsonObject());Assert.False(s.TryTravel());Assert.Null(s.CurrentNavigationId);
        var relogin=s.ConfigureRelogin(new MemoryStore());Assert.False(relogin.Active);Assert.Throws<InputBlockedException>(()=>relogin.Begin(TestWho,true,false));
        var surplus=s.CreateSurplusPort("sale",new MemoryStore(),_=>null);Assert.Equal("running",surplus.Begin());Assert.Equal("complete",surplus.Tick());
    }
    [Fact] public void OwnerResolverLossCannotReviveConstructionIdentity()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());using var s=new ScreenWorkflowSession(routes,Window(),new NoInputOps(),new EmptyOcr(),new MemoryStore(),scope:new("session","account","character"));
        Assert.NotNull(s.VerifiedOwner);s.SetVerifiedOwnerResolver(()=>null);Assert.Null(s.VerifiedOwner);
        s.SetVerifiedOwnerResolver(()=>new("current","account","character"));Assert.Equal("current",s.VerifiedOwner!.SessionId);Assert.Equal("session",s.Scope.SessionId);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void GatheringCompletesOnlyAfterBoundStableLabelsDisappearOnScene(bool witnessedTask)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag","maint_scene_map"]}}""");
        routes.Configuration["gathering_observer"]!["completion_idle_seconds"]=0;routes.Configuration["gathering_observer"]!["completion_idle_observations"]=2;
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","maint_scene_map"));
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        session.SavePortState("gathering:observed",new JsonObject{["started"]=true,["action_id"]="a",["session"]=session.Scope.SessionId});
        var port=session.CreateGatheringPort("iron_ore_nearby","observed",new MemoryStore());
        var route=new GatheringRoute("iron_ore_nearby",198875762,"mining","iron");
        port.ObserveOnly(route,"a");port.ObserveOnly(route,"a");Assert.NotEqual("complete",port.ObserveOnly(route,"a").TaskStatus);
        if(witnessedTask)
        {
            native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map","gathering_observer.task_title","gathering_observer.iron_vein_objective");
            Assert.Equal("active",port.ObserveOnly(route,"a").TaskStatus);
            native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map","gathering_observer.task_title");
            Assert.Equal("active",port.ObserveOnly(route,"a").TaskStatus);
        }
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map");
        var absent=port.ObserveOnly(route,"a");Assert.NotEqual("complete",absent.TaskStatus);
        absent=port.ObserveOnly(route,"a");Assert.Equal(witnessedTask?"complete":"unknown",absent.TaskStatus);
    }
    [Fact] public void GatheringNavigationScrollsSkillListWhenPointIsOffscreen()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        var image=Stamp(routes.Profile,"preset_catalog_point_daily_00_node","preset_gathering_close");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(image,clicks);using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        session.Observe();session.Observe();
        var node=routes.Configuration["gathering_routes"]!["point_daily_05"]!;
        var route=new GatheringRoute("point_daily_05",node["target_item_id"]!.GetValue<long>(),node["skill"]!.GetValue<string>(),node["node"]!.GetValue<string>(),node["attempts_per_batch"]!.GetValue<int>());
        var port=session.CreateGatheringPort(route.Key,"scroll",new MemoryStore());port.Navigate(route);
        Assert.Equal(3,native.WheelDeltas.Count);Assert.All(native.WheelDeltas,delta=>Assert.Equal(-120,delta));
        native.CurrentImage=Stamp(routes.Profile,"preset_catalog_point_daily_05_node","preset_gathering_close");
        port.ObserveOnly(route,"scroll");var captures=native.Captures;port.Navigate(route);
        Assert.Equal(3,native.WheelDeltas.Count);Assert.Equal(captures,native.Captures);Assert.Single(clicks);
    }
    [Fact] public void GatheringNavigationScrollsLifeSkillPageWhenSkillIsOffscreen()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        var image=Stamp(routes.Profile,"preset_life_header","skills_close","preset_life_0");
        var native=new NoInputOps(image,[]);using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();session.Observe();session.Observe();
        var node=routes.Configuration["gathering_routes"]!["point_mining_00"]!;
        var route=new GatheringRoute("point_mining_00",node["target_item_id"]!.GetValue<long>(),node["skill"]!.GetValue<string>(),node["node"]!.GetValue<string>(),node["attempts_per_batch"]!.GetValue<int>());
        session.CreateGatheringPort(route.Key,"skill-scroll",new MemoryStore()).Navigate(route);
        Assert.Equal(3,native.WheelDeltas.Count);Assert.All(native.WheelDeltas,delta=>Assert.Equal(-120,delta));
    }
    private static string Hash(JsonNode node)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString())));
    private static (TargetProof,UiObservation) Evidence(SessionScope scope){var v=new UiObservation("sha",1,scope,new("recipe_detail",false,[],new Dictionary<string,PixelPoint>{{"production.craft",new(1,1)}},[]),true,true);return(new("production.craft","recipe_detail",new(1,1),"sha",1,scope),v);}
    private static NativeWindowState Window()=>new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,1280,960,96),new(0,0,1280,960),1,true,false,true,new(0,0));
    [Fact] public void BackgroundSessionAndExplicitResumeDoNotActivateGame()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=System.Text.Json.Nodes.JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"scene.open_bag":"scene_bag"}}}""");
        var activate=0;var ops=new NoInputOps(Stamp(routes.Profile,"scene_bag")){Background=true};using var session=new ScreenWorkflowSession(routes,Window(),ops,new EmptyOcr(),new MemoryStore(),()=>activate++);
        session.Start();session.Observe();session.Observe();Assert.True(session.InputAvailable);Assert.False(session.Guard.RequiresForeground);Assert.Equal(0,activate);
        session.Pause();Assert.True(session.Guard.Paused);session.ResumeExplicit();Assert.False(session.Guard.Paused);Assert.Equal(0,activate);
        ops.OtherForeground=101;session.Observe();session.Observe();Assert.True(session.InputAvailable);Assert.False(session.Guard.Paused);Assert.Equal(0,activate);
        ops.Minimized=true;var captures=ops.Captures;session.Observe();Assert.False(session.InputAvailable);Assert.True(session.Guard.Paused);Assert.Equal("window_minimized",session.Guard.PauseReason);Assert.Equal(captures,ops.Captures);
        session.Pause("focus_lost");Assert.Equal("window_minimized",session.Guard.PauseReason);
        Assert.Throws<InputBlockedException>(session.ResumeExplicit);Assert.Equal(0,activate);
        ops.Minimized=false;session.ResumeExplicit();Assert.False(session.Guard.Paused);Assert.True(session.Observe().Stable);Assert.Equal(0,activate);
    }
    private sealed class EmptyOcr:IOcrEngine{public int Calls;public OcrLine? Recognize(int width,int height,byte[] grayPixels){Calls++;return null;}}
    private sealed class MemoryStore(JsonObject? initial=null):IWorkflowStore
    {private JsonObject? value=initial;public int Writes;public bool Fail;public JsonObject? Read()=>value is null?null:(JsonObject)value.DeepClone();public void Flush(JsonObject state){if(Fail)throw new IOException("disk");Writes++;value=(JsonObject)state.DeepClone();}}
    private sealed class NoInputOps(RgbImage? image=null,List<PixelPoint>? clicks=null):INativeWindowsOps,IGuardedWindowTransport
    {
        public RgbImage? CurrentImage;public Func<int,RgbImage>? CaptureFrame;
        public double CaptureMilliseconds;public Action? OnPointerDown;public HashSet<ushort> HeldKeys=[];
        public bool Background,Minimized;public nint OtherForeground=99;public int Captures;public List<ushort> Keys {get;}=[];public bool RequiresForeground=>!Background;public void BindGuard(ForegroundInputGuard guard){}
        private PixelPoint point;public int Wheels;public List<int> WheelDeltas {get;}=[];
        public NativeWindowState ReadWindow(nint hwnd)=>Window() with{ForegroundId=Background?OtherForeground:1,Minimized=Minimized};public RgbImage CaptureClient(NativeWindowState state){var began=System.Diagnostics.Stopwatch.GetTimestamp();Captures++;var result=CaptureFrame?.Invoke(Captures)??CurrentImage??image??throw new InvalidOperationException("no capture in construction tests");CaptureMilliseconds+=System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;return result;}
        public void MoveClient(nint hwnd,PixelPoint position){if(clicks is null)throw new InvalidOperationException("no input");point=position;}public void MouseButton(MouseButton button,bool down){if(clicks is null)throw new InvalidOperationException("no input");if(down){OnPointerDown?.Invoke();clicks.Add(point);}}public void Wheel(int delta){if(clicks is null)throw new InvalidOperationException("no input");Wheels++;WheelDeltas.Add(delta);}public void Key(ushort key,bool down,bool unicode=false){if(down){Keys.Add(key);HeldKeys.Add(key);}else HeldKeys.Remove(key);}public bool AnyPhysicalKeyDown()=>false;
    }
}

