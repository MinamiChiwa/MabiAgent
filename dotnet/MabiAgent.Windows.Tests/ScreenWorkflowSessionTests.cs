using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Windows.Tests;
public sealed class ScreenWorkflowSessionTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void SharedExitClosesExistingSkillsBeforeRefresh(bool reentry)
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        var frame=FishingFeedbackTests.ReadImage(Path.Combine(root,"tests/fixtures/production/reentry-skills-current.jpg"));
        var clicks=new List<PixelPoint>();var native=new NoInputOps(frame,clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        ScreenQuickReentry? refresh=null;var now=DateTimeOffset.UnixEpoch;
        if(reentry){refresh=new(session,()=>now);refresh.Start(now);refresh.Tick(now);}
        else{var port=session.CreateMaintenancePort("skills-exit",new MemoryStore());Assert.Equal("skills",port.Observe().Page);Assert.False(port.ReleaseNavigation());}
        Assert.Equal("skills",session.CurrentPage);Assert.True(session.LastObservation!.View.Targets.ContainsKey("page.close"));
        Assert.Equal((ushort)75,Assert.Single(native.Keys));Assert.Empty(clicks);
        if(refresh is not null)
        {
            native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map");now=now.AddSeconds(3);refresh.Tick(now);
            Assert.Equal("menu_ready",refresh.CurrentStep);Assert.Contains((ushort)27,native.Keys);
        }
    }
    private static readonly MabiAgent.Core.CharacterSlotIdentity TestWho=new("account","character","抓包名");
    [Theory][InlineData(false,0)][InlineData(false,1)][InlineData(true,0)][InlineData(true,1)]
    public void FishingNavigationClicksVisibleRowWithoutScrolling(bool video,int row)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        var frame=FishingFeedbackTests.ReadImage(Path.Combine(RecognitionTests.Root(),"tests/fixtures/production",video?"fishing-nodes-video-current.jpg":"east-wall-fishing-list-current.jpg"));
        var clicks=new List<PixelPoint>();var native=new NoInputOps(frame,clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var key=$"point_fishing_{row:00}";var port=session.CreateFishingPort(key,"visible",new MemoryStore(new JsonObject{["status"]="running"}));
        port.Observe();port.Perform(new("navigate",key));
        var top=(video?610:393)+96*row;var click=Assert.Single(clicks);Assert.InRange(click.X,799,984);Assert.InRange(click.Y,top,top+34);Assert.Empty(native.WheelDeltas);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void FishingWrongPointClosesAndReselectsOrReleasesAtBound(bool repeated)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"fishing_nodes":{"anchors":["scene_bag"],"coordinate_targets":{"gathering.open_point_fishing_01":[850,490,852,492]}},"point_detail_fishing_2":{"anchors":["scene_skills"],"coordinate_targets":{"gathering.close":[1200,30,1202,32],"gathering.start":[900,700,902,702]}},"point_detail_fishing_1":{"anchors":["maint_scene_map"],"coordinate_targets":{"gathering.start":[900,800,902,802]}}}""");
        routes.Profile.Definition["idle_fishing"]=new JsonObject();
        var list=Stamp(routes.Profile,"scene_bag");var wrong=Stamp(routes.Profile,"scene_skills");var correct=Stamp(routes.Profile,"maint_scene_map");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(list,clicks);var time=new RecoveryTime();
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var workflow=new MemoryStore();var port=new ScreenFishingPort(session,"point_fishing_01","reselect",workflow,time);
        var runner=new IdleFishingRunner(new("point_fishing_01",120),port,workflow);
        port.Observe();port.Perform(new("navigate","point_fishing_01"));
        for(var i=0;i<(repeated?3:1);i++)
        {
            native.CurrentImage=wrong;time.Seconds+=1;port.Observe();
            Assert.Equal(new PixelPoint(1201,31),clicks[^1]);
            native.CurrentImage=list;time.Seconds+=1;port.Observe();Assert.Equal(new PixelPoint(851,491),clicks[^1]);
        }
        if(repeated)
        {
            native.CurrentImage=wrong;time.Seconds+=1;
            var peerPort=new IndependentFishingPort();var peer=new IdleFishingRunner(new(FishingPlan.CurrentLocation,120),peerPort,new MemoryStore());
            var dispatcher=new WorkflowDispatcher();dispatcher.Register("reselect",runner);dispatcher.Register("peer",peer);dispatcher.RestoreOwnership("reselect");dispatcher.Continue();
            MabiAgent.Core.WorkCandidate[] plan=[new("reselect","idle_fishing",null,true,0,0),new("peer","idle_fishing",null,true,0,1)];
            dispatcher.Tick(plan,null);dispatcher.Tick(plan,null);
            Assert.Contains("reselect",dispatcher.Faults.Keys);Assert.False(dispatcher.IsPaused);
            Assert.True(port.CanReleaseForeground());Assert.Null(session.CurrentNavigationId);Assert.False(session.Guard.Paused);
            var count=clicks.Count;port.Observe();Assert.Equal(count,clicks.Count);
            dispatcher.Tick(plan,null);dispatcher.Tick(plan,null);Assert.Contains("start",peerPort.Actions);
        }
        else
        {
            native.CurrentImage=correct;time.Seconds+=1;port.Observe();Assert.Equal(new PixelPoint(901,801),clicks[^1]);Assert.Equal(4,clicks.Count);
        }
        Assert.DoesNotContain(new PixelPoint(901,701),clicks);
    }
    [Fact] public void FishingUnknownMenuHasOneTotalDeadlineAndReleasesTheVisibleList()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"fishing_nodes":{"anchors":["scene_bag"]}}""");routes.Profile.Definition["idle_fishing"]=new JsonObject();
        // 模拟没有派发步骤的稳定菜单，仍必须受整段期限限制。
        routes.Configuration["gathering_routes"]!["point_fishing_01"]!["navigation"]!["steps"]=new JsonArray();
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),[]);var time=new RecoveryTime();
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=new ScreenFishingPort(session,"point_fishing_01","deadline",new MemoryStore(new JsonObject{["status"]="running"}),time);
        port.Observe();port.Perform(new("navigate","point_fishing_01"));time.Seconds=91;
        Assert.Throws<TimeoutException>(()=>port.Observe());Assert.True(port.CanReleaseForeground());Assert.Null(session.CurrentNavigationId);
        Assert.False(session.Guard.Paused);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ModePreparationUsesFixedClickAndStableDialogThenRunsOnlyOnce(bool focused)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag","maint_scene_map"]}}""");
        var scene=Stamp(routes.Profile,"scene_bag","maint_scene_map");var clicks=new List<PixelPoint>();var native=new NoInputOps(scene,clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var store=new MemoryStore();var port=session.CreateFishingPort(FishingPlan.CurrentLocation,"mode",store);var runner=new IdleFishingRunner(new(FishingPlan.CurrentLocation,300),port,store);
        runner.Tick();Assert.Single(clicks);Assert.False(port.ModePreparationRequired);
        var title=focused?"fishing_leisure_dialog_title":"fishing_mode_confirm";
        native.CurrentImage=Stamp(routes.Profile,title);runner.Tick();
        Assert.Equal(2,clicks.Count);Assert.InRange(clicks[1].X,focused?484:729,focused?577:775);
        native.CurrentImage=scene;runner.Tick();Assert.Equal(2,clicks.Count);Assert.False(port.ModePreparationRequired);
    }
    [Fact] public void UserFocusedDialogIsRecognizedDuringTheFirstCastWait()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        var image=FishingFeedbackTests.ReadImage(Path.Combine(RecognitionTests.Root(),"tests/fixtures/production/fishing-mode-confirm-user.png"));
        var clicks=new List<PixelPoint>();var native=new NoInputOps(image,clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var store=new MemoryStore(new JsonObject{["pending"]=new JsonObject{["kind"]="start"}});var port=session.CreateFishingPort(FishingPlan.CurrentLocation,"dialog",store);
        var frame=port.Observe();Assert.True(frame.ModeConfirm);Assert.True(frame.Stable);
        store.Flush(new JsonObject{["pending"]=new JsonObject{["kind"]="confirm_mode"}});port.Perform(new("confirm_mode"));
        var click=Assert.Single(clicks);Assert.InRange(click.X,631,844);Assert.InRange(click.Y,829,892);
    }
    [Theory][InlineData("bag")][InlineData("repair_tools")]
    public async Task RepeatedBagCleanupRetriesBeforeTheMaintenanceBudgetExpires(string page)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"bag":{"anchors":["bag_close"],"targets":{"bag.close":"bag_close"}},"scene":{"anchors":["maint_scene_map"]}}""");
        if(page!="bag"){var pages=routes.Profile.Definition["pages"]!.AsObject();pages[page]=pages["bag"]!.DeepClone();pages.Remove("bag");}
        var bag=Stamp(routes.Profile,"bag_close");var scene=Stamp(routes.Profile,"maint_scene_map");
        var native=new NoInputOps(bag);using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("repeated-close",new MemoryStore());
        for(var inspection=0;inspection<2;inspection++)
        {
            var attempts=0;native.CurrentImage=bag;
            native.OnKeyDown=key=>{Assert.Equal((ushort)73,key);if(++attempts==2)native.CurrentImage=scene;};
            var store=new MemoryStore();var policy=new MaintenancePolicy(RepairEnabled:false);
            var seed=new MaintenanceRunner(port,store,policy).Snapshot();seed["navigation_cleanup"]=true;store.Flush(seed);
            var runner=new MaintenanceRunner(port,store,policy);var elapsed=System.Diagnostics.Stopwatch.StartNew();
            while(runner.StatusSnapshot()["navigation_reserved"]!.GetValue<bool>()&&elapsed.Elapsed.TotalSeconds<12)
            {runner.Tick();await Task.Delay(40);}
            Assert.False(runner.StatusSnapshot()["navigation_reserved"]!.GetValue<bool>());
            Assert.Equal(2,attempts);Assert.Equal("scene",session.CurrentPage);Assert.Null(session.CurrentNavigationId);
            Assert.True(elapsed.Elapsed.TotalSeconds<15);Assert.Null(runner.Snapshot()["pending"]);
        }
    }
    [Fact] public void FishingClosesALeftoverBagUsingTheCurrentObservationAndResetsForTheNextCheck()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"bag":{"anchors":["bag_close"],"targets":{"bag.close":"bag_close"}},"scene":{"anchors":["scene_bag","maint_scene_map"]}}""");
        routes.Profile.Definition["idle_fishing"]=JsonNode.Parse("""{"anchors":{"hook":"fishing_hook"},"controls":{"start":[1175,860,1235,915],"move":[400,300,800,700]}}""");
        var scene=Stamp(routes.Profile,"scene_bag","maint_scene_map");var native=new NoInputOps(Stamp(routes.Profile,"bag_close","bag_tools_tab"));
        native.OnKeyDown=key=>{if(key==73)native.CurrentImage=scene;};
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var store=new MemoryStore();var port=session.CreateFishingPort(FishingPlan.CurrentLocation,"bag-recovery",store);
        var runner=new IdleFishingRunner(new(FishingPlan.CurrentLocation,300),port,store);
        for(var inspection=0;inspection<2;inspection++)
        {
            native.CurrentImage=Stamp(routes.Profile,"bag_close","bag_tools_tab");var captures=native.Captures;runner.Tick();
            Assert.Equal(captures+1,native.Captures);Assert.Equal(inspection+1,native.Keys.Count(k=>k==73));
            runner.Tick();Assert.Equal("confirm_start",runner.Snapshot()["phase"]!.GetValue<string>());
        }
        Assert.Equal(2,native.Keys.Count(k=>k==32));Assert.Equal(2,native.Keys.Count(k=>k is 87 or 83));
    }
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
    [Theory][InlineData(false)][InlineData(true)]
    public void SurplusSearchUsesItemsTabAndSkipsMissingResultWithoutScrolling(bool selected)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"bag":{"anchors":["scene_bag"],"coordinate_targets":{"bag.open_items":[900,900,902,902],"bag.search_open":[715,135,717,137]}},"bag_search":{"anchors":["scene_skills"],"coordinate_targets":{"bag.search_input":[800,170,1100,200],"bag.search_reset":[810,890,850,920],"bag.search_apply":[1000,880,1200,920]}}}""");
        if(selected)routes.Profile.Definition["pages"]!["bag"]!["coordinate_targets"]!["bag.items_selected"]=new JsonArray(950,900,952,902);
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks){Background=true};using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var store=new MemoryStore(new JsonObject{["status"]="running",["phase"]="select",["item_id"]=1L,["name"]="銀礦石",["amount"]=1L});var port=session.CreateSurplusPort("bounded-find",store,_=>300);
        port.Tick();Assert.Empty(native.Keys);
        if(!selected){Assert.Equal(new PixelPoint(901,901),clicks[0]);Assert.Equal("select",port.Snapshot()["phase"]!.GetValue<string>());Assert.Empty(native.WheelDeltas);return;}
        port.Tick();port.Tick();Assert.Equal("select",port.Snapshot()["phase"]!.GetValue<string>());
        native.CurrentImage=Stamp(routes.Profile,"scene_skills");port.Tick();port.Tick();
        Assert.Contains((ushort)'銀',native.Keys);Assert.Equal(2,native.Keys.Count(k=>k==13));port.Tick();
        native.CurrentImage=Stamp(routes.Profile,"scene_bag");port.Tick();Assert.Equal("plan",port.Snapshot()["phase"]!.GetValue<string>());
        Assert.Null(port.Snapshot()["pending"]);Assert.Empty(native.WheelDeltas);
    }
    [Fact] public void SurplusSearchResultUsesVisibleNameWithoutReadingStock()
    {
        var root=RecognitionTests.Root();var frame=FishingFeedbackTests.ReadImage(Path.Combine(root,"tests/fixtures/production/surplus-search-silver-current.jpg"));
        using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var session=new ScreenWorkflowSession(ScreenRoutes.Load(root),Window(),new NoInputOps(frame),ocr,new MemoryStore());session.Start();session.Observe();
        var item=session.FindBagItem("銀礦石");Assert.NotNull(item);Assert.InRange(item.Value.X,720,800);Assert.InRange(item.Value.Y,200,240);
    }
    [Theory][InlineData("bag")][InlineData("repair_tools")]
    public void DecompositionSelectsItemsBeforeSearching(string page)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("{\""+page+"\":{\"anchors\":[\"scene_bag\"],\"coordinate_targets\":{\"bag.open_items\":[950,900,952,902],\"bag.search_open\":[715,135,717,137]}}}");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks){Background=true};using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var stock=new MabiAgent.Core.InventorySnapshot(session.Scope.SessionId);stock.Set(new(985189850,10,stock.Scope,"packet_capture","input",true));stock.Set(new(4558467832867117L,4,stock.Scope,"packet_capture","output",true));
        var port=new ScreenDecompositionPort(session,"search-branch",new MemoryStore(),()=>stock);Assert.True(port.Request(1));port.Tick();
        Assert.Equal(new PixelPoint(951,901),Assert.Single(clicks));Assert.Empty(native.Keys);Assert.Empty(native.WheelDeltas);Assert.Null(port.Snapshot()["pending"]);
    }
    [Fact] public void SurplusFinishingClearsSearchAndWaitsForBackpack()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"bag":{"anchors":["scene_bag"],"coordinate_targets":{"bag.items_selected":[950,900,952,902],"bag.search_open":[715,135,717,137]}},"bag_search":{"anchors":["scene_skills"],"coordinate_targets":{"bag.search_reset":[810,890,850,920],"bag.search_apply":[1000,880,1200,920]}}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks){Background=true};using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var store=new MemoryStore(new JsonObject{["status"]="running",["phase"]="plan",["item_id"]=1L,["search_dirty"]=true,["attempted"]=new JsonArray()});var port=session.CreateSurplusPort("clear-find",store,_=>0);
        Assert.Equal("running",port.Tick());port.Tick();native.CurrentImage=Stamp(routes.Profile,"scene_skills");port.Tick();port.Tick();
        Assert.Equal("search_cleared",port.Snapshot()["phase"]!.GetValue<string>());native.CurrentImage=Stamp(routes.Profile,"scene_bag");Assert.Equal("complete",port.Tick());
        Assert.False(port.Snapshot()["search_dirty"]!.GetValue<bool>());Assert.Equal(3,clicks.Count);Assert.Empty(native.Keys);Assert.Empty(native.WheelDeltas);
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
        for(var i=0;i<ScreenWorkflowSession.ListSearchLimit;i++){port.ObserveOnly(route,"fallback");port.Navigate(route);}
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
    [Fact] public void DecompositionAcceptsStockFromLateVerifiedOwner()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(),new EmptyOcr(),new MemoryStore());
        var stock=new MabiAgent.Core.InventorySnapshot(session.Scope.SessionId+":owner:character");
        stock.Set(new(985189850,10,stock.Scope,"packet_capture","input",true));stock.Set(new(4558467832867117L,4,stock.Scope,"packet_capture","output",true));
        var port=new ScreenDecompositionPort(session,"late-owner-conversion",new MemoryStore(),()=>stock);
        Assert.True(port.Request(1));Assert.Equal(stock.Scope,port.Snapshot()["scope"]!.GetValue<string>());Assert.Null(port.Snapshot()["pending"]);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void QuickReentryClosesNewsDuringLoadAndWhileWaitingForCapture(bool complete)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        routes.Configuration["quick_reentry"]!["steps"]=JsonNode.Parse("""[{"id":"loaded","kind":"wait","delay_ms":0,"timeout_s":120}]""");
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"));
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var script=new ScreenQuickReentry(session,()=>now);script.Start(now);
        if(complete){for(var i=0;i<3;i++)script.Tick(now);Assert.True(script.Status==QuickReentryStatus.Complete,$"{script.Status}/{script.CurrentStep}/{session.CurrentPage}/{session.StabilityReason}/{script.Failure}");}
        native.CurrentImage=Stamp(routes.Profile,"notification.news_tabs","notification.news_close");script.Tick(now.AddSeconds(2));
        Assert.Equal(new ushort[]{27},native.Keys);Assert.False(session.Guard.Paused);
        script.Pause();session.Pause("manual");script.Tick(now.AddSeconds(3));Assert.Single(native.Keys);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void QuickReentryLeavesExistingRebirthBeforeRestarting(bool unresponsive)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["idle_fishing"]=new JsonObject();
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        var pixels=new byte[1280*960*3];
        foreach(var box in new[]{new PixelBox(560,900,730,925),new PixelBox(520,690,730,715)})
            for(var y=box.Top;y<box.Bottom;y++)for(var x=box.Left;x<box.Right;x++)for(var c=0;c<3;c++)pixels[(y*1280+x)*3+c]=255;
        var ocr=new RebirthPreparationOcr();var native=new NoInputOps(new(1280,960,pixels));
        native.OnKeyDown=key=>{if(unresponsive)return;if(key==27){ocr.Text="取消重生";var changed=(byte[])pixels.Clone();changed[0]=1;native.CurrentImage=new(1280,960,changed);}if(key==32)native.CurrentImage=Stamp(routes.Profile,"scene_bag");};
        using var session=new ScreenWorkflowSession(routes,Window(),native,ocr,new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);
        refresh.Tick(now);Assert.Equal(new ushort[]{27},native.Keys);
        refresh.Tick(now.AddMilliseconds(500));Assert.Single(native.Keys);
        refresh.Tick(now.AddSeconds(1));
        if(unresponsive)
        {refresh.Tick(now.AddSeconds(2));refresh.Tick(now.AddSeconds(3));Assert.Equal(3,native.Keys.Count);Assert.Equal(QuickReentryStatus.Failed,refresh.Status);}
        else
        {Assert.Equal(new ushort[]{27,32},native.Keys);refresh.Tick(now.AddSeconds(2));Assert.Equal(new ushort[]{27,32,27},native.Keys);Assert.Equal("menu_ready",refresh.CurrentStep);}
    }
    private sealed class RebirthPreparationOcr:IColorOcrEngine
    {public string Text="進行重生";public OcrLine? Recognize(int width,int height,byte[] pixels)=>new(Text,.99);public OcrLine? RecognizeRgb(RgbImage image)=>new(Text,.99);}
    [Fact] public void QuickReentryConfigContainsOnlyTheFastReentryChain()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());
        var steps=routes.Configuration["quick_reentry"]!["steps"]!.AsArray().OfType<JsonObject>().ToArray();
        Assert.Equal(["escape_menu","menu_ready","appearance","rebirth_ready","escape_back","dialog_kind","escape_if_confirm","cancel_ready","exit","loaded"],steps.Select(step=>step["id"]!.GetValue<string>()));
        Assert.Equal(27,steps[0]["key"]!.GetValue<int>());
        Assert.Equal("click_text",steps[2]["kind"]!.GetValue<string>());
        Assert.Equal(27,steps[4]["key"]!.GetValue<int>());
        Assert.Equal(32,steps[8]["key"]!.GetValue<int>());
        Assert.DoesNotContain(steps,step=>step["point"] is not null);
        Assert.DoesNotContain(steps,step=>step["anchors"] is not null);
        Assert.Contains("變更外貌/重生",steps[2]["search"]!["labels"]!.AsArray().Select(label=>label!.GetValue<string>()));
        Assert.Equal("storage.room_exit",routes.Configuration["quick_reentry"]!["field_exit"]!["exit_target"]!.GetValue<string>());
        Assert.Equal("storage_room_exit",routes.Configuration["quick_reentry"]!["field_exit"]!["confirmation_page"]!.GetValue<string>());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void QuickReentryDiscardsSlowLoadingFramesWithinTheOriginalDeadline(bool expires)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        routes.Configuration["quick_reentry"]!["steps"]=JsonNode.Parse("""[{"id":"menu_ready","kind":"wait","timeout_s":2,"delay_ms":0}]""");
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","relogin.menu_exit"));
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);
        // 准备阶段复用新鲜场景，声明等待处注入加载慢帧。
        native.CaptureFrame=number=>number==1?Stamp(routes.Profile,"scene_bag"):throw new InputBlockedException("slow capture"){CaptureTimedOut=true};
        refresh.Tick(now);refresh.Tick(now.AddSeconds(1));
        Assert.Equal(QuickReentryStatus.Running,refresh.Status);Assert.Empty(native.Keys);
        if(expires)
        {
            refresh.Tick(now.AddSeconds(3));Assert.Equal(QuickReentryStatus.Failed,refresh.Status);Assert.Contains("步骤超时",refresh.Failure);
        }
        else
        {
            native.CaptureFrame=null;refresh.Tick(now.AddMilliseconds(1500));Assert.Equal(QuickReentryStatus.Complete,refresh.Status);
        }
        Assert.Empty(native.Keys);Assert.False(session.Guard.Paused);
    }
    [Fact] public void QuickReentryDoesNotRetryOtherCaptureGuardFailures()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        routes.Configuration["quick_reentry"]!["steps"]=JsonNode.Parse("""[{"id":"rebirth_ready","kind":"wait","delay_ms":0}]""");
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"));
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);
        native.CaptureFrame=number=>number==1?Stamp(routes.Profile,"scene_bag"):throw new InputBlockedException("截图期间窗口变化");
        refresh.Tick(now);Assert.Equal(QuickReentryStatus.Failed,refresh.Status);Assert.Equal("截图期间窗口变化",refresh.Failure);Assert.Empty(native.Keys);
    }
    [Fact] public void QuickReentryLeavesHuntingFieldBeforeSendingEscape()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag","maint_scene_map"],"targets":{"storage.room_exit":"storage_room_exit_icon"}},"storage_room_exit":{"anchors":["storage_room_exit_title","storage_room_exit_cancel"],"targets":{"storage.exit_room":"storage_room_exit_button"}}}""");
        var field=Stamp(routes.Profile,"scene_bag","maint_scene_map","storage_room_exit_icon");
        var confirm=Stamp(routes.Profile,"storage_room_exit_title","storage_room_exit_cancel","storage_room_exit_button");
        var town=Stamp(routes.Profile,"scene_bag","maint_scene_map");
        var clicks=new List<PixelPoint>();var page=0;
        var native=new NoInputOps(field,clicks){CaptureFrame=_=>page==0?field:page==1?confirm:town,OnPointerDown=()=>page++};
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);
        refresh.Tick(now);
        Assert.Single(clicks);Assert.Empty(native.Keys);
        Assert.InRange(clicks[0].X,1225,1265);Assert.InRange(clicks[0].Y,15,50);
        Thread.Sleep(400);now=now.AddSeconds(1);refresh.Tick(now);
        Assert.Equal(2,clicks.Count);Assert.Empty(native.Keys);
        Assert.InRange(clicks[1].X,730,777);Assert.InRange(clicks[1].Y,850,878);
        now=now.AddSeconds(1);refresh.Tick(now);
        Assert.Equal(new ushort[]{27},native.Keys);
        Assert.Equal(2,clicks.Count);
    }
    [Fact] public void QuickReentryWaitsThroughDirectLoadingBeforeEscape()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag","maint_scene_map"],"targets":{"storage.room_exit":"storage_room_exit_icon"}},"storage_room_exit":{"anchors":["storage_room_exit_title","storage_room_exit_cancel"],"targets":{"storage.exit_room":"storage_room_exit_button"}}}""");
        var field=Stamp(routes.Profile,"scene_bag","maint_scene_map","storage_room_exit_icon");
        var town=Stamp(routes.Profile,"scene_bag","maint_scene_map");
        var loading=new RgbImage(1280,960,new byte[1280*960*3]);
        var clicks=new List<PixelPoint>();var state="field";
        var native=new NoInputOps(field,clicks){CaptureFrame=_=>state=="field"?field:state=="loading"?loading:town,OnPointerDown=()=>{if(state=="field")state="loading";}};
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);refresh.Tick(now);
        Assert.Single(clicks);Assert.Empty(native.Keys);Assert.Equal(QuickReentryStatus.Running,refresh.Status);
        for(var seconds=1;seconds<=16;seconds++)
        {
            now=now.AddSeconds(1);refresh.Tick(now);
            Assert.Equal(QuickReentryStatus.Running,refresh.Status);Assert.Single(clicks);Assert.Empty(native.Keys);
        }
        state="town";
        for(var attempt=0;attempt<5&&!native.Keys.Contains((ushort)27);attempt++){Thread.Sleep(400);now=now.AddSeconds(1);refresh.Tick(now);}
        Assert.Contains((ushort)27,native.Keys);Assert.Single(clicks);
    }
    [Fact] public void SharedExitCancelsAbandonedDeviceTravelBeforeReturningToScene()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["maint_scene_map"]},"wood_panel":{"anchors":["scene_bag"],"coordinate_targets":{"page.close":[20,20,60,60]}},"device_travel_confirm":{"anchors":["scene_skills"],"coordinate_targets":{"device.cancel_travel":[440,833,620,896]}}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_skills"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("abandoned-device",new MemoryStore());port.Observe();Assert.False(port.ReleaseNavigation());
        Assert.InRange(Assert.Single(clicks).X,440,620);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag");Thread.Sleep(600);port.Observe();Assert.False(port.ReleaseNavigation());
        port.Observe();Assert.False(port.ReleaseNavigation());Assert.Equal(2,clicks.Count);Assert.InRange(clicks[1].X,20,60);
        native.CurrentImage=Stamp(routes.Profile,"maint_scene_map");port.Observe();Assert.True(port.ReleaseNavigation());Assert.Empty(native.Keys);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void QuickReentryRetriesDroppedMenuKeyOnlyWhileStillInScene(bool unresponsive)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        routes.Configuration["quick_reentry"]!["steps"]=JsonNode.Parse("""[{"id":"escape_menu","kind":"key","key":27,"delay_ms":0},{"id":"menu_ready","kind":"wait","delay_ms":0}]""");
        var town=Stamp(routes.Profile,"scene_bag","scene_menu");var menu=Stamp(routes.Profile,"relogin.menu_exit");
        var native=new NoInputOps(town);var attempts=0;
        native.OnKeyDown=key=>{if(key==27&&++attempts==2&&!unresponsive)native.CurrentImage=menu;};
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);refresh.Tick(now);
        Assert.Single(native.Keys);refresh.Tick(now.AddSeconds(1));Assert.Single(native.Keys);
        refresh.Tick(now.AddSeconds(3));Assert.Equal(2,native.Keys.Count);
        refresh.Tick(now.AddSeconds(6));
        if(unresponsive)
        {Assert.Equal(3,native.Keys.Count);refresh.Tick(now.AddSeconds(9));Assert.Equal(QuickReentryStatus.Failed,refresh.Status);Assert.Contains("菜单打开未生效",refresh.Failure);}
        else
        {Assert.Equal(QuickReentryStatus.Complete,refresh.Status);refresh.Tick(now.AddSeconds(9));Assert.Equal(2,native.Keys.Count);}
        Assert.False(session.Guard.Paused);
    }
    [Fact] public void QuickReentryDismountsInTownBeforeOpeningRebirthMenu()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag","maint_scene_map"],"targets":{"scene.dismount":"maint_dismount"}}}""");
        var mounted=Stamp(routes.Profile,"scene_bag","maint_scene_map","maint_dismount");
        var town=Stamp(routes.Profile,"scene_bag","maint_scene_map");var clicks=new List<PixelPoint>();var riding=true;
        var native=new NoInputOps(mounted,clicks){CaptureFrame=_=>riding?mounted:town,OnPointerDown=()=>riding=false};
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);refresh.Tick(now);
        Assert.Single(clicks);Assert.Empty(native.Keys);
        refresh.Tick(now.AddMilliseconds(500));Assert.Empty(native.Keys);
        refresh.Tick(now.AddSeconds(1));Assert.Equal(new ushort[]{27},native.Keys);Assert.Single(clicks);
    }
    [Fact] public void QuickReentrySkipsMapExitWhenAlreadyInTown()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag","maint_scene_map"],"targets":{"storage.room_exit":"storage_room_exit_icon"}},"storage_room_exit":{"anchors":["storage_room_exit_title","storage_room_exit_cancel"],"targets":{"storage.exit_room":"storage_room_exit_button"}}}""");
        var town=Stamp(routes.Profile,"scene_bag","maint_scene_map");var clicks=new List<PixelPoint>();
        var native=new NoInputOps(town,clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);refresh.Tick(now);
        Assert.Empty(clicks);Assert.Equal(new ushort[]{27},native.Keys);
    }
    [Fact] public void QuickReentryMapExitTimeoutNeverSendsEscapeOrRebirth()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Configuration["quick_reentry"]!["field_exit"]!["timeout_s"]=2;
        routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag","maint_scene_map"],"targets":{"storage.room_exit":"storage_room_exit_icon"}},"storage_room_exit":{"anchors":["storage_room_exit_title","storage_room_exit_cancel"],"targets":{"storage.exit_room":"storage_room_exit_button"}}}""");
        var field=Stamp(routes.Profile,"scene_bag","maint_scene_map","storage_room_exit_icon");var clicks=new List<PixelPoint>();
        var loading=new RgbImage(1280,960,new byte[1280*960*3]);
        var native=new NoInputOps(field,clicks){CaptureFrame=_=>clicks.Count==0?field:loading};
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);refresh.Tick(now);
        now=now.AddSeconds(3);refresh.Tick(now);
        Assert.Equal(QuickReentryStatus.Failed,refresh.Status);Assert.Contains("地图出口超时",refresh.Failure);
        Assert.Single(clicks);Assert.Empty(native.Keys);Assert.False(session.Guard.Paused);
    }
    [Fact] public void QuickReentryFieldExitPauseStopsInputWithoutPausingSession()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag","maint_scene_map"],"targets":{"storage.room_exit":"storage_room_exit_icon"}},"storage_room_exit":{"anchors":["storage_room_exit_title","storage_room_exit_cancel"],"targets":{"storage.exit_room":"storage_room_exit_button"}}}""");
        var field=Stamp(routes.Profile,"scene_bag","maint_scene_map","storage_room_exit_icon");var clicks=new List<PixelPoint>();
        var native=new NoInputOps(field,clicks){CaptureFrame=_=>field};
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);refresh.Tick(now);
        refresh.Pause();now=now.AddSeconds(10);refresh.Tick(now);
        Assert.Single(clicks);Assert.Empty(native.Keys);Assert.False(session.Guard.Paused);
    }
    [Fact] public void QuickReentryFindsRightHandRebirthIconWithoutOcr()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","quick_reentry_rebirth_icon"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);
        for(var i=0;i<8&&clicks.Count==0;i++)refresh.Tick(now.AddSeconds(i));
        var point=Assert.Single(clicks);Assert.InRange(point.X,874,902);Assert.InRange(point.Y,765,798);
        Assert.Equal("rebirth_ready",refresh.CurrentStep);Assert.Equal(new ushort[]{27},native.Keys);
    }
    [Fact] public void QuickReentrySearchesAfterTheAppearanceClickStayInMeasuredBands()
    {
        // Measured on live frames (1280x960, 2026-11-04): the confirm button label sits at
        // x598-682/y906-924, 下一步 at x609-671/y894-911, and 要取消重生嗎 at x554-722/y699-720.
        // The three post-click searches must stay inside those bands; a wider region costs OCR
        // calls per tick and was the reason the chain crawled.
        var configuration=ScreenRoutes.Load(RecognitionTests.Root()).Configuration["quick_reentry"]!;
        foreach(var (key,left,right,top,bottom) in new[]{("confirm_search",540,820,878,960),("rebirth_search",560,780,878,960),("cancel_search",500,800,660,780)})
        {
            var search=configuration[key]!.AsObject();
            var region=search["region"]!.AsArray().Select(value=>value!.GetValue<int>()).ToArray();
            var cropWidth=search["crop_width"]!.GetValue<int>();var stepX=search["step_x"]!.GetValue<int>();
            Assert.Equal(left,region[0]);Assert.Equal(right,region[2]);Assert.Equal(top,region[1]);Assert.Equal(bottom,region[3]);
            Assert.True(region[2]-region[0]>=cropWidth,$"{key} 区域宽度必须容纳裁剪宽度");
            Assert.InRange((region[2]-region[0]-cropWidth)/stepX+1,1,4);
        }
    }
    [Fact] public void QuickReentryReopensTheMenuWhenTheClickOnlyClosedIt()
    {
        // A tap that lands outside the entry closes the menu and leaves the HUD back on screen.
        // That is a different miss than "menu never moved", so the chain reopens the menu, clicks
        // once more, and then fails with its own message instead of timing out.
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag","scene_menu"]}}""");
        var image=Stamp(routes.Profile,"scene_bag","scene_menu");var pixels=image.CopyPixels();
        for(var y=806;y<830;y++)for(var x=868;x<980;x++){var i=(y*1280+x)*3;pixels[i]=255;pixels[i+1]=255;pixels[i+2]=255;}
        var scene=new RgbImage(1280,960,pixels);
        var clicks=new List<PixelPoint>();var native=new NoInputOps(scene,clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new WideMenuOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);
        refresh.Tick(now);
        var failureAt=TimeSpan.Zero;
        for(var step=1;step<=100;step++)
        {
            var at=now.AddMilliseconds(step*250);refresh.Tick(at);
            if(refresh.Status==QuickReentryStatus.Failed&&failureAt==TimeSpan.Zero)failureAt=at-now;
        }
        Assert.True(refresh.Status==QuickReentryStatus.Failed,$"step={refresh.CurrentStep}; status={refresh.Status}; clicks={clicks.Count}; esc={native.Keys.Count(key=>key==27)}; failure={refresh.Failure}; at={failureAt}");
        Assert.Contains("菜单被关闭但未进入重生界面",refresh.Failure);
        Assert.Equal(3,clicks.Count);
        // One ESC opens the menu at the start, one per reopen before the retry click.
        Assert.Equal(3,native.Keys.Count(key=>key==27));
        Assert.True(failureAt<TimeSpan.FromSeconds(15),$"failure_at={failureAt}");
    }
    [Fact] public void QuickReentryRetriesTheAppearanceClickWhenTheMenuNeverMoves()
    {
        // Background touch reports success even when the game drops the click. The chain must
        // re-click the same spot instead of sitting out the whole rebirth_confirm timeout, and
        // must say so when the entry never leaves the menu.
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        var image=Stamp(routes.Profile,"scene_bag");var pixels=image.CopyPixels();
        for(var y=800;y<846;y++)for(var x=860;x<930;x++){var i=(y*1280+x)*3;pixels[i]=255;pixels[i+1]=255;pixels[i+2]=255;}
        var menu=new RgbImage(1280,960,pixels);
        var clicks=new List<PixelPoint>();var native=new NoInputOps(menu,clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new MenuLabelOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);
        refresh.Tick(now);
        var failureAt=TimeSpan.Zero;
        for(var step=1;step<=80;step++)
        {
            var at=now.AddMilliseconds(step*250);refresh.Tick(at);
            if(refresh.Status==QuickReentryStatus.Failed&&failureAt==TimeSpan.Zero)failureAt=at-now;
        }
        Assert.Equal(QuickReentryStatus.Failed,refresh.Status);
        Assert.Equal("rebirth_ready",refresh.CurrentStep);
        Assert.Contains("点击未生效",refresh.Failure);
        Assert.Equal(3,clicks.Count);
        Assert.True(failureAt<TimeSpan.FromSeconds(12),$"failure_at={failureAt}");
    }
    [Fact] public void QuickReentryRetriesWhenMenuChromeStaysButAppearanceTextFades()
    {
        // The ESC menu can stay on screen while its animated entry text briefly becomes
        // unreadable. The structural menu buttons are enough to retry the same measured click.
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        var clicks=new List<PixelPoint>();var ocr=new FadeMenuLabelOcr();
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","relogin.menu_exit"),clicks);native.OnPointerDown=()=>ocr.Enabled=false;
        using var session=new ScreenWorkflowSession(routes,Window(),native,ocr,new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);refresh.Tick(now);
        var failureAt=TimeSpan.Zero;
        for(var step=1;step<=80;step++)
        {
            var at=now.AddMilliseconds(step*250);refresh.Tick(at);
            if(refresh.Status==QuickReentryStatus.Failed&&failureAt==TimeSpan.Zero)failureAt=at-now;
        }
        Assert.Equal(QuickReentryStatus.Failed,refresh.Status);
        Assert.Equal("rebirth_ready",refresh.CurrentStep);
        Assert.Contains("点击未生效",refresh.Failure);
        Assert.Equal(3,clicks.Count);
        Assert.True(failureAt<TimeSpan.FromSeconds(12),$"failure_at={failureAt}");
    }
    [Fact] public void BackpackRecognitionDoesNotRequireEquipmentOnlyAutoWear()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");
        var bag=routes.Profile.Definition["pages"]!["bag"]!.DeepClone();
        routes.Profile.Definition["pages"]=new JsonObject{["bag"]=bag};
        var image=Stamp(routes.Profile,"bag_tools_tab","bag_fashion_tab","bag_close");
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(image,[]),new EmptyOcr(),new MemoryStore());session.Start();
        Assert.Equal("bag",session.Observe().View.Page);
    }
    [Fact] public void QuickReentrySendsOnlyEscapeWhenMenuTextIsUnreadable()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);
        refresh.Tick(now);
        for(var step=1;step<=160;step++)refresh.Tick(now.AddMilliseconds(step*250));
        Assert.True(refresh.CurrentStep=="menu_ready",$"step={refresh.CurrentStep}; status={refresh.Status}; page={session.CurrentPage}; failure={refresh.Failure}");
        Assert.Equal(QuickReentryStatus.Failed,refresh.Status);
        Assert.Contains("menu_ready",refresh.Failure);
        Assert.Empty(clicks);
        Assert.Contains((ushort)27,native.Keys);
        Assert.All(native.Keys,key=>Assert.Equal((ushort)27,key));
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
    public void SurplusPlansStockAfterOwnerIdentityArrives(long stock,long sale)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(),new EmptyOcr(),new MemoryStore());
        var inventory=new MabiAgent.Core.InventorySnapshot(session.Scope.SessionId+":owner:character");inventory.Set(new(198875762,stock,inventory.Scope,"packet_capture","owner-v1",true));
        var store=new MemoryStore();var port=session.CreateSurplusPort("owner-floor",store,_=>300,()=>inventory);port.Begin();
        Assert.Equal(sale==0?"complete":"running",port.Tick());
        if(sale>0)Assert.Equal(sale,port.Snapshot()["amount"]!.GetValue<long>());
    }
    [Theory][InlineData(false,true)][InlineData(true,true)][InlineData(true,false)]
    public void SurplusReceiptUsesCurrentInventoryOwner(bool savedScope,bool sameOwner)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(),new EmptyOcr(),new MemoryStore());
        var scope=session.Scope.SessionId+":owner:character";
        var inventory=new MabiAgent.Core.InventorySnapshot(scope);inventory.Set(new(198875762,100,scope,"packet_capture","after",true));
        var pending=new JsonObject{["before"]=270L,["amount"]=170L,["before_version"]="before"};
        if(savedScope)pending["inventory_scope"]=sameOwner?scope:scope+"-other";
        var store=new MemoryStore(new JsonObject{["status"]="unknown",["phase"]="result",["item_id"]=198875762L,["amount"]=170L,["attempted"]=new JsonArray(),["receipts"]=new JsonArray(),["pending"]=pending});
        var port=session.CreateSurplusPort("owner-receipt",store,_=>100,()=>inventory);port.ObserveOnly();
        Assert.Equal(savedScope&&!sameOwner,port.Snapshot()["pending"] is not null);
        Assert.Equal(savedScope&&!sameOwner?0:1,port.Snapshot()["receipts"]!.AsArray().Count);
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
    [Theory][InlineData("tidy")][InlineData("tidy_confirm")][InlineData("tidy_result")]
    public void SurplusSelectionClosesTidyPagesThroughSharedNavigation(string page)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=new JsonObject{[page]=JsonNode.Parse("""{"anchors":["scene_bag"],"targets":{"page.close":"scene_bag"}}""")};
        routes.Configuration["maintenance"]!["exit_navigation"]![page]=new JsonObject{["target"]="page.close",["expected_page"]="bag"};
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        var store=new MemoryStore(new JsonObject{["status"]="running",["phase"]="select",["item_id"]=1L,["name"]="ore",["amount"]=1L,["deadline"]=(DateTimeOffset.UtcNow-DateTimeOffset.UnixEpoch).TotalSeconds+60});
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateSurplusPort("tidy-sale",store,_=>1);Assert.Equal("running",port.Tick());Assert.True(clicks.Count==1,$"page={session.CurrentPage}; paused={session.Guard.Paused}; reason={session.Guard.PauseReason}; state={port.Snapshot()}");
        port.Tick();Assert.Single(clicks);Assert.Equal(0,native.Wheels);Assert.Null(store.Read()!["pending"]);
    }
    [Theory][InlineData("repair_tools")][InlineData("bag")]
    public void SurplusSelectionSelectsItemsBeforeSearchingAnyBackpackTab(string page)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=new JsonObject{[page]=JsonNode.Parse("""{"anchors":["scene_bag"],"targets":{"bag.open_items":"scene_bag"}}""")};
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        var store=new MemoryStore(new JsonObject{["status"]="running",["phase"]="select",["item_id"]=1L,["name"]="ore",["amount"]=1L,["deadline"]=(DateTimeOffset.UtcNow-DateTimeOffset.UnixEpoch).TotalSeconds+60});
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateSurplusPort("tools-sale",store,_=>1);Assert.Equal("running",port.Tick());Assert.Single(clicks);Assert.Empty(native.Keys);
        port.Tick();Assert.Single(clicks);Assert.Equal(0,native.Wheels);Assert.Null(store.Read()!["pending"]);
    }
    [Fact] public void RepeatedSurplusPassSwitchesToolsTabOncePerPass()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"repair_tools":{"anchors":["scene_bag"],"targets":{"bag.open_items":"scene_bag"}}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var inventory=new MabiAgent.Core.InventorySnapshot(session.Scope.SessionId);inventory.Set(new(198875762,378,session.Scope.SessionId,"packet_capture","current",true));
        var store=new MemoryStore();var port=session.CreateSurplusPort("repeat-tools-sale",store,_=>300,()=>inventory);
        for(var pass=1;pass<=2;pass++)
        {
            Assert.Equal("running",port.Begin());port.Tick();port.Tick();port.Tick();
            Assert.Equal(pass,clicks.Count);Assert.Empty(native.Keys);Assert.Equal(0,native.Wheels);
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
    [Fact] public void CurrentOpaqueWhiteRemoteMetalBackUsesSharedBrightnessInvariantGlyph()
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);var back=routes.Profile.Templates["preset_metal_steel_detail_back"];
        Assert.Equal("light_text",back.Mode);Assert.True(back.RelativeContrast);Assert.Equal(80,back.RelativeContrastThreshold!.Value);
        Assert.Equal(0.97,back.Threshold,3);Assert.Equal(0.97,back.GlyphThreshold!.Value,3);

        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/metal-remote-back-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        using(var currentMatcher=new TemplateMatcher(routes.Profile)){var hit=currentMatcher.Anchor(frame,"screen_overlay_back");Assert.NotNull(hit);Assert.True(hit!.Score>=.97);Assert.Equal("preset_metal_steel_detail_back",hit.Variant);}

        var legacyDefinition=(System.Text.Json.Nodes.JsonObject)routes.Profile.Definition.DeepClone();var legacy=legacyDefinition["templates"]!["preset_metal_steel_detail_back"]!.AsObject();
        legacy["match_mode"]="grayscale";legacy.Remove("glyph_threshold");legacy.Remove("relative_contrast");legacy.Remove("relative_contrast_threshold");
        using(var legacyMatcher=new TemplateMatcher(NavigationProfile.FromDefinition(root,legacyDefinition)))Assert.Null(legacyMatcher.Anchor(frame,"screen_overlay_back"));

        var clicks=new List<PixelPoint>();var native=new NoInputOps(frame,clicks);
        using(var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore()))
        {
            session.Start();var port=session.CreateMaintenancePort("remote-white-back",new MemoryStore());Assert.Equal("metal_panel",port.Observe().Page);
            Assert.False(port.ReleaseNavigation());var click=Assert.Single(clicks);Assert.InRange(click.X,24,52);Assert.InRange(click.Y,23,57);
        }

    }
    [Fact] public void FocusedProcessingWaitClearsSkillsThenUsesSharedCloseResolver()
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);routes.Profile.Definition.Remove("page_variants");routes.Configuration["notifications"]=new JsonObject();routes.Configuration.Remove("workbench_chains");
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/metal-remote-back-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var remote=new RgbImage(rgb.Width,rgb.Height,pixels);
        var recipe=routes.Recipes["iron_ingot_from_iron_ore"];var requested=new ProductionRecipe(recipe.Key,recipe.Device,recipe.DurationSeconds);var clicks=new List<PixelPoint>();
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","maint_scene_map"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateProductionPort(recipe.Key,"focused-remote-back",new MemoryStore());
        var scene=port.Observe();Assert.Equal("scene",scene.Page);Assert.True(scene.Stable);Assert.True(session.InputAvailable);Assert.Null(routes.Configuration["workbench_chains"]);Assert.False(port.HasExtensionPending);Assert.Contains("scene.open_skills",session.LastObservation!.View.Targets.Keys);port.Navigate("recipe_preview",requested);Assert.Empty(clicks);Assert.Contains((ushort)75,native.Keys);
        native.CurrentImage=Stamp(routes.Profile,NavigationProfile.Names(routes.Profile.Definition["pages"]!["skills"]!["anchors"]));
        var skills=port.Observe();Assert.Equal("skills",skills.Page);Assert.True(skills.Stable);Assert.True(session.InputAvailable);port.Navigate("recipe_preview",requested);Assert.Single(clicks);
        native.CurrentImage=remote;Assert.Equal("metal_panel",port.Observe().Page);
        Assert.Equal("metal_panel",port.Observe().Page);Assert.Contains("page.close",session.LastObservation!.View.Targets.Keys);
        port.Navigate("leave",requested);Assert.Equal(2,clicks.Count);Assert.InRange(clicks[^1].X,24,52);Assert.InRange(clicks[^1].Y,23,57);
    }
    [Fact] public void ConfirmedDeviceArrivalDoesNotExpireWhileReadingItsCurrentRecipe()
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/fabric-detail-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var native=new NoInputOps(new(rgb.Width,rgb.Height,pixels));
        var journal=new MemoryStore();
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),journal);session.Start();
        session.ConfirmDeviceArrival("fabric");
        var port=session.CreateProductionPort("catalog_fabric_04","arrival",new MemoryStore());
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(ScreenProductionPort).GetField("queueArrived",flags)!.SetValue(port,true);
        typeof(ScreenProductionPort).GetField("queueEpoch",flags)!.SetValue(port,session.InputEpoch);
        typeof(ScreenProductionPort).GetField("queueAt",flags)!.SetValue(port,0d);
        typeof(ScreenProductionPort).GetField("queueEmpty",flags)!.SetValue(port,new[]{0,1,2,3,4});
        var frame=port.Observe();Assert.Equal("recipe_detail",frame.Page);Assert.Equal("catalog_fabric_04",frame.RecipeKey);Assert.True(frame.AtDevice);Assert.True(frame.CraftAvailable);
    }
    [Fact] public void FullCatalogAllWaterWellAndCowEntrypointsReserveTenBottles()
    {
        var catalog=new RuntimeCatalog(ScreenRoutes.Load(RecognitionTests.Root()).Configuration);var rules=GatheringConsumables.RulesFor(catalog);
        foreach(var key in new[]{"point_daily_02","point_daily_03","point_daily_04","gathering_water_bottle","gathering_water_bottle_well","gathering_material_4706539586426517"})
        {Assert.True(rules.ContainsKey(key),key);Assert.Equal(10,rules[key].RequiredAttempts);Assert.Equal(1202292919,rules[key].ItemId);}
    }
    [Fact] public void ExplicitMissingConsumablesUsesStableRefusalAndEscWithoutReadingQuantity()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var native=new NoInputOps(Stamp(routes.Profile,"missing_consumables"));
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        session.SavePortState("gathering:missing",new JsonObject{["started"]=true,["action_id"]="a",["session"]=session.Scope.SessionId});
        var route=new RuntimeCatalog(routes.Configuration).Gathering["point_daily_03"];var port=session.CreateGatheringPort(route.Key,"missing",new MemoryStore());
        var frame=port.Observe(route,"a");Assert.True(frame.MissingConsumables);Assert.Null(frame.ActualQuantity);
        Assert.True(port.DismissMissingConsumables());Assert.Equal(new ushort[]{27},native.Keys);Assert.Null(session.CurrentNavigationId);
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
    [Fact] public void RejectedGatheringStartDoesNotRetainStartedState()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"iron_vein_detail":{"anchors":["detail_confirm_close"],"targets":{"gathering.start":"detail_confirm_close"}}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"detail_confirm_close"),clicks);var journal=new MemoryStore();
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),journal);session.Start();
        var route=new RuntimeCatalog(routes.Configuration).Gathering["point_mining_01"];var store=new MemoryStore(new JsonObject{["pending"]=new JsonObject{["kind"]="gathering",["action_id"]="not-sent"}});
        var port=session.CreateGatheringPort(route.Key,"not-sent",store);port.ObserveOnly(route,"not-sent");native.Minimized=true;
        Assert.Throws<InputRejectedBeforeDispatchException>(()=>port.Start(route,"not-sent"));
        Assert.Null(journal.Read()?["ports"]?["gathering:not-sent"]?["started"]);Assert.Equal(0,journal.Read()?["intents"]?.AsObject().Count??0);Assert.Empty(clicks);
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
    [Theory][InlineData("region_map",false)][InlineData("storage_travel",false)][InlineData("repair_travel",false)][InlineData("region_map",true)][InlineData("storage_travel",true)][InlineData("repair_travel",true)]
    public void GatheringReleasesPreviousMapThroughSharedExitAndContinues(string page,bool production)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=new JsonObject{[page]=JsonNode.Parse("""{"anchors":["scene_skills"],"coordinate_targets":{"map.close":[1220,20,1260,58]}}"""),["scene"]=JsonNode.Parse("""{"anchors":["scene_bag"],"coordinate_targets":{"character.open":[20,20,60,60],"scene.open_skills":[20,20,60,60]}}""")};
        routes.Configuration["maintenance"]!["exit_navigation"]![page]=JsonNode.Parse("""{"target":"map.close","expected_page":"scene","settle_s":0,"timeout_s":2}""");
        var clicks=new List<PixelPoint>();var ops=new NoInputOps(Stamp(routes.Profile,"scene_skills"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),ops,new EmptyOcr(),new MemoryStore());session.Start();
        var route=new RuntimeCatalog(routes.Configuration).Gathering["gathering_soft_log"];var port=session.CreateGatheringPort(route.Key,"map-handoff",new MemoryStore());
        var recipe=routes.Recipes["iron_ingot_from_iron_ore"];var making=session.CreateProductionPort(recipe.Key,"map-handoff",new MemoryStore());
        void Observe(){if(production)making.Observe();else port.ObserveOnly(route,"map-handoff");}
        void Navigate(){if(production)making.Navigate("queue",new(recipe.Key,recipe.Device,recipe.DurationSeconds));else port.Navigate(route);}
        Observe();var captures=ops.Captures;Navigate();Assert.Equal(captures,ops.Captures);
        Assert.Equal(new PixelPoint(1240,39),Assert.Single(clicks));Observe();Navigate();Assert.Single(clicks);
        ops.CurrentImage=Stamp(routes.Profile,"scene_bag");Observe();Navigate();
        if(production){Assert.Equal(new ushort[]{75},ops.Keys);Assert.Single(clicks);}
        else{Assert.Equal(new PixelPoint(40,40),clicks[1]);Assert.Equal(2,clicks.Count);}
        Assert.Null(session.CurrentNavigationId);Assert.False(session.Guard.Paused);
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
    [Fact] public void RecognizedToolsBagAllowsIndependentWorkflowHandoff()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition["pages"]=JsonNode.Parse("""{"repair_tools":{"anchors":["maint_tools_selected"]}}""");
        var native=new NoInputOps(Stamp(routes.Profile,"maint_tools_selected"),[]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        Assert.True(session.CanReleaseForeground());Assert.Empty(native.Keys);
        session.Pause("manual");Assert.False(session.CanReleaseForeground());
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
    [Theory][InlineData("metal")][InlineData("wood")][InlineData("leather")][InlineData("fabric")][InlineData("chemical")][InlineData("food")]
    public void UnchangedProcessingMenuRetriesOnlyOnKnownOriginalPageAndStopsAfterTwoRetries(string deviceKey)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        var device=routes.Devices[deviceKey];var route=routes.Recipes.Values.First(r=>r.Device==deviceKey);
        routes.Profile.Definition["pages"]=new JsonObject{
            ["category_page"]=new JsonObject{["anchors"]=new JsonArray("scene_bag"),["coordinate_targets"]=new JsonObject{[device.CategoryTarget]=new JsonArray(240,245,320,290)}},
            [device.QueuePage]=new JsonObject{["anchors"]=new JsonArray("scene_skills")}};
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);var time=new RecoveryTime();
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var recipe=new ProductionRecipe(route.Key,deviceKey,60);var port=new ScreenProductionPort(session,route.Key,"menu-retry",new MemoryStore(),time);
        port.Observe();port.Navigate("queue_preview",recipe);Assert.Single(clicks);
        time.Seconds+=4;port.Observe();port.Navigate("queue_preview",recipe);Assert.Single(clicks);
        time.Seconds+=2;native.CurrentImage=new RgbImage(1280,960,new byte[1280*960*3]);port.Observe();port.Navigate("queue_preview",recipe);Assert.Single(clicks);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag");port.Observe();port.Navigate("queue_preview",recipe);Assert.Equal(2,clicks.Count);
        time.Seconds+=5;port.Observe();port.Navigate("queue_preview",recipe);Assert.Equal(3,clicks.Count);
        time.Seconds+=5;port.Observe();Assert.Throws<InputBlockedException>(()=>port.Navigate("queue_preview",recipe));Assert.Equal(3,clicks.Count);
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
            // Another batch at the same physical device reuses this arrival.
            var peer=session.CreateProductionPort(recipe.Key,"next-batch",new MemoryStore());
            Assert.True(peer.Observe().AtDevice);Assert.Equal("metal",session.OnsiteDevice);
            peer.Navigate("recipe",recipe);Assert.Equal(3,clicks.Count);Assert.Equal(clicks[1],clicks[2]);
            session.BeginNavigation("gathering:elsewhere");
            Assert.False(peer.Observe().AtDevice);Assert.False(port.Observe().AtDevice);Assert.Null(session.OnsiteDevice);
        }
    }
    [Fact] public void PersistedDeviceArrivalDoesNotConfirmTheCurrentPosition()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"metal_panel":{"anchors":["scene_bag"]}}""");
        var journal=new MemoryStore(new JsonObject{["ports"]=new JsonObject{["production:old-arrival"]=new JsonObject{["device_arrived"]=true}}});
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(Stamp(routes.Profile,"scene_bag")),new EmptyOcr(),journal);session.Start();
        var frame=session.CreateProductionPort("iron_ingot_from_iron_ore","old-arrival",new MemoryStore()).Observe();
        Assert.False(frame.AtDevice);Assert.False(frame.NavigationActive);Assert.Null(session.OnsiteDevice);
    }
    [Fact] public void CurrentWoodEquipmentPageDoesNotRequestAnotherTrip()
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);routes.Configuration.Remove("workbench_chains");
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/wood-arrived-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var clicks=new List<PixelPoint>();var native=new NoInputOps(new(rgb.Width,rgb.Height,pixels),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateProductionPort("catalog_wood_01","current-wood",new MemoryStore());var frame=port.Observe();
        Assert.Equal("wood_panel",frame.Page);Assert.True(frame.AtDevice,"needs="+session.LastObservation!.View.NeedsDeviceArrival+"; hits="+string.Join(",",session.LastObservation.View.Hits.Select(h=>h.Name)));
        port.Navigate("queue",new("catalog_wood_01","wood",720));Assert.Empty(clicks);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QuickReentryReturnAdvancesObservedNpcDialogueThenWaitsForScene(bool returnsToScene)
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        routes.Configuration["quick_reentry"]!["steps"]=JsonNode.Parse("""[{"id":"escape_menu","kind":"key","key":27,"delay_ms":0},{"id":"loaded","kind":"wait","delay_ms":0,"timeout_s":120}]""");
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/reentry-return-dialog-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var scene=Stamp(routes.Profile,"scene_bag","maint_scene_map");var clicks=new List<PixelPoint>();var native=new NoInputOps(scene,clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var now=DateTimeOffset.UnixEpoch;var refresh=new ScreenQuickReentry(session,()=>now);refresh.Start(now);refresh.Tick(now);
        Assert.Equal("loaded",refresh.CurrentStep);native.CurrentImage=new(rgb.Width,rgb.Height,pixels);now=now.AddSeconds(1);refresh.Tick(now);
        Assert.Equal(QuickReentryStatus.Running,refresh.Status);Assert.Equal("npc_dialogue",session.CurrentPage);Assert.Single(clicks);
        if(returnsToScene)
        {
            native.CurrentImage=scene;now=now.AddSeconds(1);refresh.Tick(now);Assert.Equal(QuickReentryStatus.Complete,refresh.Status);
        }
        else
        {
            now=now.AddSeconds(121);refresh.Tick(now);Assert.Equal(QuickReentryStatus.Failed,refresh.Status);
        }
        Assert.Single(clicks);
    }
    [Fact] public void AlreadyOnsitePageReleasesTravelWithoutAnObservedPanelClosure()
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);routes.Configuration.Remove("workbench_chains");
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/wood-arrived-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var clicks=new List<PixelPoint>();var native=new NoInputOps(new(rgb.Width,rgb.Height,pixels),clicks);
        var journal=new MemoryStore(new JsonObject{["ports"]=new JsonObject{["production:onsite-travel"]=new JsonObject{["device_travel"]=true,["panel_closed"]=false,["travel_deadline"]=0d}}});
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),journal);session.Start();
        var port=session.CreateProductionPort("catalog_wood_01","onsite-travel",new MemoryStore());var frame=port.Observe();
        Assert.True(frame.AtDevice);Assert.False(frame.NavigationActive);Assert.True(session.HasDeviceArrival("wood"));Assert.Empty(clicks);
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
            native.CurrentImage=Stamp(routes.Profile,"scene_bag");Assert.True(port.Observe().NavigationActive);
            native.CurrentImage=Stamp(routes.Profile,"scene_skills");Assert.True(port.Observe().NavigationActive);
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
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","travel.wing_quote_icon"),clicks);var ocr=new WingsOcr();var store=new MemoryStore();var settingsReads=0;
        using var session=new ScreenWorkflowSession(routes,Window(),native,ocr,new MemoryStore());session.Start();session.BeginNavigation("trip");
        session.ConfigureTravel(store,()=>{settingsReads++;return new JsonObject{["travel"]=new JsonObject{["auto_use_wings"]=enabled,["wing_reserve_min"]=(long)reserve}};});
        native.OnPointerDown=()=>Assert.Equal("use_wings",store.Read()!["pending"]!["kind"]!.GetValue<string>());
        Assert.Equal(sent,session.TryTravel());Assert.Equal(1,settingsReads);Assert.Equal(sent?1:0,clicks.Count);
        if(sent)
        {
            Assert.Equal(1,native.Captures);Assert.Equal(1,ocr.Calls);Assert.False(session.TryTravel());
            session.BeginNavigation("trip");Assert.True(session.TryTravel());Assert.Equal(2,clicks.Count);
            Assert.Contains(store.Read()!["unresolved"]!.AsObject(),pair=>pair.Value!["navigation_id"]!.GetValue<string>()=="trip");
        }
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
        if(use)
        {
            Assert.Contains(store.Read()!["unresolved"]!.AsObject(),pair=>pair.Value!["navigation_id"]!.GetValue<string>()=="trip");
            Assert.Equal("use_wings",store.Read()!["pending"]!["kind"]!.GetValue<string>());
        }
    }
    [Fact] public void ReusedNavigationIdGetsAFreshFirstQuoteDecision()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","travel.wing_quote_icon"),clicks);var ocr=new DecreasingWingsOcr("10/20");var store=new MemoryStore();
        using var session=new ScreenWorkflowSession(routes,Window(),native,ocr,new MemoryStore());session.Start();session.BeginNavigation("maintenance:maintenance");
        session.ConfigureTravel(store,()=>new JsonObject{["travel"]=new JsonObject{["auto_use_wings"]=true,["wing_reserve_min"]=15L}});
        Assert.False(session.TryTravel());Assert.False(session.TryTravel());Assert.Equal(1,ocr.Calls);Assert.Empty(clicks);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","travel.wing_quote_icon");session.BeginNavigation("maintenance:maintenance");var reused=session.TryTravel();Assert.True(reused,store.Read()?.ToJsonString()??"null");Assert.Equal(2,ocr.Calls);Assert.Single(clicks);
        var decision=store.Read()!["decisions"]!["maintenance:maintenance"]!;
        Assert.True(decision["use"]!.GetValue<bool>());Assert.Equal(1,decision["cost"]!.GetValue<long>());
        Assert.Equal(session.Scope.SessionId+":maintenance:maintenance@2",decision["instance"]!.GetValue<string>());
    }
    [Fact] public void WingReserveAcceptsIntLongAndParsedJsonWithoutLoweringReserve()
    {
        var sources=new (string Name,Func<long,JsonNode> Create)[]
        {
            ("int",value=>JsonValue.Create((int)value)),
            ("long",value=>JsonValue.Create(value)),
            ("json",value=>JsonNode.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture))!)
        };
        foreach(var source in sources)
        foreach(var reserve in new long[]{0,20})
        {
            var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
            routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
            var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","travel.wing_quote_icon"),clicks);var ocr=new WingsOcr();
            using var session=new ScreenWorkflowSession(routes,Window(),native,ocr,new MemoryStore());session.Start();session.BeginNavigation("trip");
            session.ConfigureTravel(new MemoryStore(),()=>new JsonObject{["travel"]=new JsonObject{["auto_use_wings"]=true,["wing_reserve_min"]=source.Create(reserve)}});
            var use=reserve==0;
            Assert.True(use==session.TryTravel(),$"{source.Name} reserve {reserve}");
            Assert.Equal(use?1:0,clicks.Count);
            Assert.Equal(1,ocr.Calls);
        }
    }
    [Theory][InlineData("string")][InlineData("fraction")][InlineData("parsed_fraction")]
    public void MalformedWingReserveFailsClosedWithoutInput(string kind)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","travel.wing_quote_icon"),clicks);var ocr=new WingsOcr();var store=new MemoryStore();
        using var session=new ScreenWorkflowSession(routes,Window(),native,ocr,new MemoryStore());session.Start();session.BeginNavigation("trip");
        JsonNode malformed=kind switch
        {
            "string"=>JsonValue.Create("0")!,
            "fraction"=>JsonValue.Create(0.5)!,
            "parsed_fraction"=>JsonNode.Parse("0.5")!,
            _=>throw new ArgumentOutOfRangeException(nameof(kind))
        };
        session.ConfigureTravel(store,()=>new JsonObject{["travel"]=new JsonObject{["auto_use_wings"]=true,["wing_reserve_min"]=malformed}});
        Assert.False(session.TryTravel());Assert.Empty(clicks);Assert.Equal(0,ocr.Calls);Assert.Null(store.Read());
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
            native.OnKeyDown=key=>{if(key==32){Assert.DoesNotContain((ushort)87,native.HeldKeys);delivered=System.Diagnostics.Stopwatch.GetTimestamp();}};
            var movement=System.Diagnostics.Stopwatch.StartNew();
            port.MoveWhileObserving(new("move",Direction:"w",DurationSeconds:.06),frame=>
            {
                Assert.True(frame.FishingButton);captureAtSubmit=native.Captures;ocrAtSubmit=ocr.Calls;captureMsAtSubmit=native.CaptureMilliseconds;
                store.Flush(new JsonObject{["pending"]=new JsonObject{["kind"]="start"}});
                submit=System.Diagnostics.Stopwatch.GetTimestamp();port.Perform(new("start"));return true;
            });
            movement.Stop();Assert.NotEqual(0,delivered);Assert.Empty(clicks);Assert.Equal(new ushort[]{87,32},native.Keys);Assert.Equal(waitCaptures,native.Captures);
            Assert.Equal(captureAtSubmit,native.Captures);Assert.Equal(ocrAtSubmit,ocr.Calls);Assert.Equal(0,ocr.Calls);
            samples.Add(new JsonObject{["sample"]=sample,["wait_observe_ms"]=wait.Elapsed.TotalMilliseconds,["wait_capture_calls"]=waitCaptures,["wait_fake_capture_ms"]=waitCaptureMs,["wait_recognition_and_guard_ms"]=Math.Max(0,wait.Elapsed.TotalMilliseconds-waitCaptureMs),["wait_ocr_calls"]=waitOcr,["movement_to_release_ms"]=movement.Elapsed.TotalMilliseconds,["movement_capture_calls"]=native.Captures-waitCaptures,["movement_fake_capture_ms"]=captureMsAtSubmit-waitCaptureMs,["fixed_submit_to_fake_space_ms"]=System.Diagnostics.Stopwatch.GetElapsedTime(submit,delivered).TotalMilliseconds,["fixed_extra_capture_calls"]=native.Captures-captureAtSubmit,["fixed_extra_ocr_calls"]=ocr.Calls-ocrAtSubmit,["fake_pointer_down_calls"]=clicks.Count});
        }
        var output=new JsonObject{["status"]="measured_offline",["at"]=DateTimeOffset.UtcNow.ToString("O"),["evidence"]="Stamped fixture, actual ScreenFishingPort/session/shared guard, in-memory durable store and fake background transport. No game or native touch delivery.",["limits"]="Recognition-and-guard is a residual segment including hashing and bookkeeping; fake capture is an in-memory return. Submission timing includes durable authority, movement, response interval and guard until fake Space key-down, not OS/game delivery. Setup excluded; first sample includes JIT. No planner runs inside this callback; scheduler contention is not measured. No old baseline threshold.",["samples"]=samples};
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
    [Fact] public void FixedCastAfterLightTapReleasesKeyBeforeSpaceAndKeepsDurableDeduplication()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        routes.Profile.Definition["idle_fishing"]=JsonNode.Parse("""{"controls":{"start":[1175,860,1235,915],"move":[100,100,200,200]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var store=new MemoryStore();var port=session.CreateFishingPort("point_fishing_00","f",store);
        port.ObserveOnly();port.ObserveOnly();var captures=native.Captures;
        native.OnKeyDown=key=>{if(key==32)Assert.DoesNotContain((ushort)87,native.HeldKeys);};
        port.MoveWhileObserving(new("move",Direction:"w",DurationSeconds:.06),frame=>{Assert.True(frame.FishingButton);store.Flush(new JsonObject{["pending"]=new JsonObject{["kind"]="start"}});port.Perform(new("start"));return true;});
        Assert.Empty(clicks);Assert.Equal(new ushort[]{87,32},native.Keys);Assert.Equal(captures,native.Captures);
        Assert.Empty(native.HeldKeys);
        Assert.Throws<InputBlockedException>(()=>port.MoveWhileObserving(new("move",Direction:"s",DurationSeconds:2),_=>true));
        Assert.Equal(new ushort[]{87,32},native.Keys);Assert.Equal(captures,native.Captures);
        Assert.Throws<InputRejectedBeforeDispatchException>(()=>port.MoveWhileObserving(new("move",Direction:"s",DurationSeconds:.06),_=>{port.Perform(new("start"));return true;}));
        Assert.Equal(new ushort[]{87,32},native.Keys);Assert.Empty(clicks);
        session.Pause();
        Assert.Throws<InputBlockedException>(()=>port.MoveWhileObserving(new("move",Direction:"s",DurationSeconds:.06),_=>true));
        Assert.Equal(new ushort[]{87,32},native.Keys);Assert.Equal(captures,native.Captures);Assert.Empty(clicks);Assert.True(session.Guard.Paused);
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
        Assert.Empty(clicks);Assert.Equal(new ushort[]{87,32},native.Keys);
        var afterCast=port.ObserveOnly();Assert.False(afterCast.FishingButton);Assert.False(afterCast.RoundActive);Assert.Equal("scene",afterCast.Page);
        Assert.NotNull(store.Read()!["pending"]);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map","fishing_hook");
        var hooked=port.ObserveOnly();Assert.True(hooked.HookVisible);Assert.True(hooked.RoundActive);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map");Assert.True(port.ObserveOnly().RoundActive);
        time.Seconds=2.9;Assert.True(port.ObserveOnly().RoundActive);
        time.Seconds=3.05;var ended=port.ObserveOnly();Assert.False(ended.RoundActive);Assert.True(ended.FishingButton);
        var captures=native.Captures;
        store.Flush(new JsonObject{["pending"]=new JsonObject{["kind"]="start",["at"]=1}});port.Perform(new("start"));
        Assert.Equal(new ushort[]{87,32,32},native.Keys);Assert.Equal(captures,native.Captures);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map","fishing_hook");time.Seconds=6;port.ObserveOnly();
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map");port.ObserveOnly();
        time.Seconds=11.1;Assert.False(port.ObserveOnly().FishingButton);
        store.Flush(new JsonObject{["pending"]=new JsonObject{["kind"]="start",["at"]=2}});
        Assert.Throws<InputRejectedBeforeDispatchException>(()=>port.Perform(new("start")));
        Assert.Equal(new ushort[]{87,32,32},native.Keys);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void MidGaugeHookMissCannotEndRoundOrSendAnotherCast(bool firstHookVisible)
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
        var store=new MemoryStore();var port=new ScreenFishingPort(session,FishingPlan.CurrentLocation,"f",store,time);
        var runner=new IdleFishingRunner(new(FishingPlan.CurrentLocation,60),port,store);
        runner.Tick();Assert.Empty(clicks);Assert.Single(native.Keys,k=>k==32);
        native.CurrentImage=Frame(false,0);runner.Tick();
        Assert.Null(port.LastFrame!.Gauge);Assert.False(port.LastFrame.RoundActive);
        Assert.Equal(0,runner.Snapshot()["submitted"]!.GetValue<int>());Assert.NotNull(runner.Snapshot()["pending"]);Assert.Empty(clicks);Assert.Single(native.Keys,k=>k==32);
        native.CurrentImage=Frame(firstHookVisible,65);runner.Tick();Assert.Equal(.65,port.LastFrame!.Gauge);Assert.True(runner.Snapshot()["round_seen"]!.GetValue<bool>());
        native.CurrentImage=Frame(false,50);time.Seconds=3;runner.Tick();
        Assert.Equal(.5,port.LastFrame!.Gauge);Assert.True(port.LastFrame.RoundActive);Assert.False(port.LastFrame.Compass);
        Assert.Equal(0,runner.Snapshot()["completed"]!.GetValue<int>());Assert.Empty(clicks);Assert.Single(native.Keys,k=>k==32);
        native.CurrentImage=Frame(false,null);runner.Tick();Assert.True(port.LastFrame!.RoundActive);
        native.CurrentImage=new(1280,960,new byte[1280*960*3]);time.Seconds=4;runner.Tick();
        native.CurrentImage=Frame(false,null);time.Seconds=5;runner.Tick();Assert.True(port.LastFrame!.RoundActive);
        time.Seconds=6.05;runner.Tick();Assert.Equal(1,runner.Snapshot()["completed"]!.GetValue<int>());Assert.Empty(clicks);Assert.Equal(2,native.Keys.Count(k=>k==32));
        Assert.Equal("confirm_start",runner.Snapshot()["phase"]!.GetValue<string>());Assert.NotNull(runner.Snapshot()["pending"]);
        Assert.Equal(1,native.Keys.Count(k=>k==87||k==83));
    }
    [Theory][InlineData(FishingPlan.CurrentLocation,false)][InlineData("point_fishing_00",false)]
    [InlineData(FishingPlan.CurrentLocation,true)][InlineData("point_fishing_00",true)]
    public void FishingRecastsImmediatelyWhenSceneHudReturnsAfterReelAnimation(string point,bool reel)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag","maint_scene_map"]}}""");
        routes.Profile.Definition["idle_fishing"]=JsonNode.Parse("""{"anchors":{"hook":"fishing_hook"},"controls":{"start":[1175,860,1235,915],"move":[400,300,800,700]}}""");
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","maint_scene_map"),[]);var time=new RecoveryTime();
        if(point!=FishingPlan.CurrentLocation)
        {
            routes.Configuration["gathering_routes"]![point]!["navigation"]=JsonNode.Parse("""{"detail_page":"scene","start_target":"gathering.start","steps":[]}""");
            routes.Profile.Definition["pages"]!["scene"]!["coordinate_targets"]=JsonNode.Parse("""{"gathering.start":[100,100,140,140]}""");
        }
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var store=new MemoryStore();var plan=new FishingPlan(point,60);var port=new ScreenFishingPort(session,point,"hud-return",store,time);
        if(point!=FishingPlan.CurrentLocation)
        {port.ObserveOnly();port.Perform(new("navigate",point));time.Seconds=.1;port.ObserveOnly();time.Seconds=3.2;port.ObserveOnly();Assert.True(port.CanResumeAtPoint);}
        var initial=new IdleFishingRunner(plan,port,store).Snapshot();initial["phase"]="round";initial["round_seen"]=true;initial["submitted"]=1;initial["clock_started"]=true;store.Flush(initial);
        var runner=new IdleFishingRunner(plan,port,store);runner.Resume();
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map","fishing_hook");time.Seconds=5;runner.Tick();
        if(reel)
        {
            var checkpoint=runner.Snapshot();checkpoint["phase"]="confirm_reel";
            checkpoint["pending"]=new JsonObject{["kind"]="reel",["at"]=5d,["capture_id"]="reel",["deadline"]=20d};store.Flush(checkpoint);
            port.Perform(new("reel"));runner=new IdleFishingRunner(plan,port,store);runner.Resume();
        }
        var before=native.Keys.Count;native.CurrentImage=new(1280,960,new byte[1280*960*3]);
        time.Seconds=6;runner.Tick();time.Seconds=7;runner.Tick();time.Seconds=8.9;runner.Tick();
        Assert.Equal(before,native.Keys.Count);
        time.Seconds=9.2;runner.Tick();Assert.Equal(before,native.Keys.Count);Assert.False(port.LastFrame!.Stable);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map");time.Seconds=9.3;
        var captures=native.Captures;runner.Tick();
        Assert.Equal(before+1,native.Keys.Count);Assert.Equal((ushort)32,native.Keys[^1]);Assert.Equal(captures+1,native.Captures);
        Assert.DoesNotContain(native.Keys,k=>k is 87 or 83);Assert.Equal(1,runner.Snapshot()["completed"]!.GetValue<int>());
        Assert.Equal("confirm_start",runner.Snapshot()["phase"]!.GetValue<string>());
    }
    [Fact] public void FishingReelWaitIncludesTimeSpentCapturingTheFirstAbsentFrame()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["idle_fishing"]=JsonNode.Parse("""{"anchors":{"hook":"fishing_hook"},"controls":{"start":[1175,860,1235,915]}}""");
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","maint_scene_map","fishing_hook"));var time=new RecoveryTime();
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=new ScreenFishingPort(session,FishingPlan.CurrentLocation,"frame-age",new MemoryStore(new JsonObject{["phase"]="round"}),time);
        time.Seconds=1;Assert.True(port.ObserveOnly().RoundActive);
        var scene=Stamp(routes.Profile,"scene_bag","maint_scene_map");
        native.CaptureFrame=_=>{Thread.Sleep(250);return scene;};time.Seconds=2;Assert.True(port.ObserveOnly().RoundActive);
        native.CaptureFrame=null;native.CurrentImage=scene;time.Seconds=4.8;
        Assert.True(port.ObserveOnly().FishingButton);Assert.Empty(native.Keys);
    }
    [Fact] public void FishingBackpackInterruptionClearsTheDirectRecastWindow()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag","maint_scene_map"]},"backpack":{"anchors":["bag_close","bag_tools_tab"]}}""");
        routes.Profile.Definition["idle_fishing"]=JsonNode.Parse("""{"anchors":{"hook":"fishing_hook"},"controls":{"start":[1175,860,1235,915],"move":[400,300,800,700]}}""");
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","maint_scene_map"),[]);var time=new RecoveryTime();
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var store=new MemoryStore(new JsonObject{["phase"]="round"});var port=new ScreenFishingPort(session,FishingPlan.CurrentLocation,"bag-interrupt",store,time);
        port.ObserveOnly();port.ObserveOnly();
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map","fishing_hook");port.ObserveOnly();
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map");port.ObserveOnly();time.Seconds=3.25;
        Assert.True(port.ObserveOnly().FishingButton);
        native.CurrentImage=Stamp(routes.Profile,"bag_close","bag_tools_tab");Assert.Equal("bag",port.ObserveOnly().Page);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map");time.Seconds=3.5;
        var restored=port.ObserveOnly();Assert.False(restored.FishingButton);Assert.False(restored.RoundActive);
        Assert.Empty(native.Keys);
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
        var time=new RecoveryTime();var port=new ScreenFishingPort(session,"point_fishing_00","f",store,time);port.Observe();port.Observe();port.Perform(new("navigate","point_fishing_00"));
        Assert.Single(clicks);native.CurrentImage=Stamp(routes.Profile,"scene_skills");
        Assert.Null(port.Observe().NavigationActive);Assert.Null(port.Observe().NavigationActive);
        Assert.Single(clicks);Assert.NotNull(session.CurrentNavigationId);
        native.CurrentImage=Stamp(routes.Profile,"scene_skills","fishing_navigation_task");time.Seconds=1;Assert.True(port.Observe().NavigationActive);
        native.CurrentImage=Stamp(routes.Profile,"scene_skills");time.Seconds=2;Assert.Null(port.Observe().NavigationActive);time.Seconds=5;Assert.False(port.Observe().NavigationActive);
        Assert.True(port.CanResumeAtPoint);Assert.Null(session.CurrentNavigationId);session.BeginNavigation("other");Assert.False(port.CanResumeAtPoint);
        Assert.Single(clicks);
    }
    [Fact] public void FishingUnsentNavigationEndsAfterFifteenSecondsWithoutPausingTheInputGuard()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"character_menu":{"anchors":["scene_bag"],"coordinate_targets":{"character.open_life_skills":[60,560,61,561]}}}""");
        routes.Profile.Definition["idle_fishing"]=new JsonObject();
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks){OnPointerDown=()=>throw new InputRejectedBeforeDispatchException("local frames missing")};
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var time=new RecoveryTime();var port=new ScreenFishingPort(session,"point_fishing_00","unsent",new MemoryStore(new JsonObject{["status"]="running"}),time);
        port.Observe();port.Observe();Assert.Throws<InputRejectedBeforeDispatchException>(()=>port.Perform(new("navigate","point_fishing_00")));
        time.Seconds=8;port.Observe();time.Seconds=15;Assert.Throws<TimeoutException>(()=>port.Observe());
        port.Observe();Assert.Empty(clicks);Assert.False(session.Guard.Paused);Assert.Null(session.CurrentNavigationId);
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
        Assert.True(port.CanReleaseForeground());Assert.Null(session.CurrentNavigationId);
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
        Assert.Equal(36,native.WheelDeltas.Count);Assert.All(native.WheelDeltas,delta=>Assert.Equal(-120,delta));
        for(var i=12;i<ScreenWorkflowSession.ListSearchLimit;i++)port.Observe();
        Assert.All(native.WheelDeltas.Skip(36),delta=>Assert.Equal(120,delta));Assert.Throws<TimeoutException>(()=>port.Observe());
        port.Observe();Assert.Equal(ScreenWorkflowSession.ListSearchLimit*3,native.WheelDeltas.Count);Assert.False(session.Guard.Paused);
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
    [Theory][InlineData("backpack_weight")][InlineData("backpack_tidy")][InlineData("repair_tools")][InlineData("backpack_weight","tidy")][InlineData("backpack_tidy","tidy")]
    public void MaintenanceBagOpenWaitsThroughUnrelatedPage(string destination,string result="bag")
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["idle_fishing"]=new JsonObject();
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"coordinate_targets":{"scene.open_bag":[100,100,140,140]}},"wood_panel":{"anchors":["scene_skills"]},"bag":{"anchors":["maint_scene_map"]}}""");
        if(result=="tidy"){var pages=routes.Profile.Definition["pages"]!.AsObject();pages["tidy"]=pages["bag"]!.DeepClone();pages.Remove("bag");}
        routes.Configuration["maintenance"]!["navigation"]![destination]!["scene"]!["settle_s"]=0;
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("bag-transition",new MemoryStore());port.Observe();port.Observe();
        Assert.Equal("scene",session.CurrentPage);Assert.True(session.LastObservation!.Stable);Assert.Contains("scene.open_bag",session.LastObservation.View.Targets.Keys);
        Assert.Null(port.Navigate(destination));Assert.Single(native.Keys);Assert.Equal("dispatch",port.LastTestNavigationTrace?.Phase);
        native.CurrentImage=Stamp(routes.Profile,"scene_skills");port.Observe();port.Observe();
        Assert.Null(port.Navigate(destination));Assert.Single(native.Keys);Assert.Equal("dispatch",port.LastTestNavigationTrace?.Phase);
        native.CurrentImage=Stamp(routes.Profile,"maint_scene_map");port.Observe();port.Observe();
        Assert.Null(port.Navigate(destination));Assert.Single(native.Keys);Assert.Equal("confirmed",port.LastTestNavigationTrace?.Phase);
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
            var recovery=Assert.Single(stages["portrait"]!["search"]!.AsArray());
            Assert.Equal("recenter",recovery!["next"]!.GetValue<string>());
            Assert.True(recovery["map_recovery"]!.GetValue<bool>());
            Assert.Equal(-4,recovery["steps"]!.GetValue<int>());
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
    private static RgbImage StampShifted(NavigationProfile profile,string name,int dx,int dy,params string[] names)
    {
        var pixels=new byte[1280*960*3];
        void Paste(string n,int x,int y){var t=profile.Templates[n];var source=t.Image.CopyPixels();for(var row=0;row<t.Image.Height;row++)Array.Copy(source,row*t.Image.Width*3,pixels,((t.Box.Top+y+row)*1280+t.Box.Left+x)*3,t.Image.Width*3);}
        Paste(name,dx,dy);foreach(var n in names)Paste(n,0,0);
        return new(1280,960,pixels);
    }
    [Fact] public void WingsUseBoxFollowsShiftedHudIcon()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        // The panel moves with its icon when the quote gains digits; a fixed screen
        // box would land beside the button.
        const int shiftX=18,shiftY=12;
        var clicks=new List<PixelPoint>();var native=new NoInputOps(StampShifted(routes.Profile,"travel.wing_quote_icon",shiftX,shiftY,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new WingsOcr(),new MemoryStore());session.Start();session.BeginNavigation("trip");
        session.ConfigureTravel(new MemoryStore(),()=>new JsonObject{["travel"]=new JsonObject{["auto_use_wings"]=true,["wing_reserve_min"]=0L}});
        var use=NavigationProfile.ReadBox(routes.Configuration["travel_wings"]!["use_box"]!);
        var expected=new PixelBox(use.Left+shiftX,use.Top+shiftY,use.Right+shiftX,use.Bottom+shiftY);
        var observed=session.Observe();
        Assert.Equal(expected,observed.View.TargetRegions!["travel.use_wings"]);
        Assert.Equal(expected.Center,observed.View.Targets["travel.use_wings"]);
        Assert.True(session.TryTravel());
        var click=Assert.Single(clicks);
        Assert.True(expected.Contains(click));
        Assert.True(Math.Abs(click.X-expected.Center.X)<=Math.Max(1,(int)Math.Floor(expected.Width*.3)));
        Assert.True(Math.Abs(click.Y-expected.Center.Y)<=Math.Max(1,(int)Math.Floor(expected.Height*.3)));
    }
    [Fact] public void WingsIconOutsideItsSearchWindowProducesNoClick()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        // Far enough that the icon leaves its calibrated search window: no anchor,
        // so no target and no guessed click.
        var clicks=new List<PixelPoint>();var native=new NoInputOps(StampShifted(routes.Profile,"travel.wing_quote_icon",60,0,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new WingsOcr(),new MemoryStore());session.Start();session.BeginNavigation("trip");
        session.ConfigureTravel(new MemoryStore(),()=>new JsonObject{["travel"]=new JsonObject{["auto_use_wings"]=true,["wing_reserve_min"]=0L}});
        Assert.DoesNotContain("travel.use_wings",session.Observe().View.Targets.Keys);
        Assert.False(session.TryTravel());Assert.Empty(clicks);
    }
    [Fact] public void WingsUseBoxIsRejectedWhenTheShiftLeavesTheClientArea()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"));using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();session.Observe();
        var spec=routes.Profile.Templates["travel.wing_quote_icon"];
        var use=NavigationProfile.ReadBox(routes.Configuration["travel_wings"]!["use_box"]!);
        var hit=new TemplateHit("travel.wing_quote_icon",.99,new PixelBox(1240,spec.Box.Top,1268,spec.Box.Bottom),"sha");
        // dx would push the declared button past the right edge; a clamped click is
        // worse than no click, so the target is dropped for this frame.
        Assert.Null(session.AnchoredBox("travel.wing_quote_icon",hit,use));
    }
    [Theory][InlineData(96)][InlineData(144)]
    public void WingsUseBoxScalesWithClientPixelsForEitherReportedDpi(int dpi)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition.Remove("page_variants");
        var state=Window() with{Geometry=new(0,0,1600,1200,dpi),WindowRect=new(0,0,1600,1200)};
        var native=new NoInputOps(new RgbImage(1600,1200,new byte[1600*1200*3])){State=state};
        using var session=new ScreenWorkflowSession(routes,state,native,new EmptyOcr(),new MemoryStore());session.Start();session.Observe();
        var layout=ReviewedLayout.For(1600,1200,dpi);var spec=routes.Profile.Templates["travel.wing_quote_icon"];
        var icon=layout.SearchBox(spec.Box);
        var hit=new TemplateHit("travel.wing_quote_icon",.99,new PixelBox(icon.Left+10,icon.Top+6,icon.Right+10,icon.Bottom+6),"sha");
        var use=NavigationProfile.ReadBox(routes.Configuration["travel_wings"]!["use_box"]!);
        var scaled=layout.SearchBox(use);
        var box=session.AnchoredBox("travel.wing_quote_icon",hit,use);
        // Captures are already physical client pixels, so the same client size gives
        // the same box at 96 and 144 dpi; dpi changes are handled by the guard.
        Assert.True(box.HasValue);
        Assert.Equal(new PixelBox(scaled.Left+10,scaled.Top+6,scaled.Right+10,scaled.Bottom+6),box.Value);
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
        var authority=new ScreenWorkflowSession.DurableAuthority(journal,state,scope,"batch",hash,"craft",_=>true,new("verified-capture","account","character"));
        Assert.True(authority.AllowsProcessScope(scope));Assert.True(authority.VerifyPersistedIntent("batch:"+hash,proof,view));Assert.Equal(1,journal.Writes);
        var saved=journal.Read()!["intents"]!["batch:"+hash]!;
        Assert.Equal("account",saved["account_id"]!.GetValue<string>());Assert.Equal("character",saved["character_id"]!.GetValue<string>());
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
    [Theory]
    [InlineData("storage-raw-log-detail-current.jpg",true)]
    [InlineData("storage-detail-current.jpg",false)]
    public void CurrentPublicStorageRawLogDetailRetainsSelectedOwner(string fixture,bool publicSelected)
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production",fixture));using var rgb=new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(new RgbImage(rgb.Width,rgb.Height,pixels)),ocr,new MemoryStore());session.Start();
        session.SetVerifiedOwnerResolver(()=>new("capture","24081432","25251211640832"));
        var plan=new WithdrawalPlan(4639622494368896,"原木",35,"account:\"24081432\"");var frame=session.CreateStoragePort(plan,"detail",new MemoryStore()).Observe();
        Assert.Equal("storage_detail",frame.Page);
        if(!publicSelected){Assert.Null(frame.Scope);Assert.False(frame.TransferAvailable);return;}
        Assert.True(frame.Scope==plan.Scope,"scope="+frame.Scope+"; anchor="+session.Matcher.Anchor(session.LastCapture!.Image,"storage_account_dim")?.Score);
        Assert.Equal(plan.ItemId,frame.ItemId);Assert.True(frame.TransferAvailable);
    }
    [Theory][InlineData("高級原木",true)][InlineData("高級原木+",false)]
    public void StorageSelectionFindsShiftedCompleteTitleWithoutMatchingAnotherItem(string name,bool matches)
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/storage-high-log-detail-current.jpg"));using var rgb=new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(new RgbImage(rgb.Width,rgb.Height,pixels)),ocr,new MemoryStore());session.Start();
        session.SetVerifiedOwnerResolver(()=>new("capture","24081432","25251211640832"));
        var plan=new WithdrawalPlan(4719508601690960,name,776,"character:[\"24081432\",\"25251211640832\"]");var frame=session.CreateStoragePort(plan,"detail",new MemoryStore()).Observe();
        Assert.Equal("storage_detail",frame.Page);Assert.Equal(plan.Scope,frame.Scope);
        Assert.Equal(matches,frame.TransferAvailable);Assert.Equal(matches?plan.ItemId:(long?)null,frame.ItemId);
    }
    [Fact] public void ForcedFullBackpackTidyExposesBoldSwitchAndProtectsConfirmation()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var image=FishingFeedbackTests.ReadImage(Path.Combine(RecognitionTests.Root(),"tests/fixtures/production/backpack-full-tidy-current.png"));
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(image),new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("forced-tidy",new MemoryStore());var frame=port.Observe();
        Assert.Equal("tidy",frame.Page);Assert.True(frame.BoldSelected);Assert.Null(frame.MaterialsSelected);
        Assert.Contains("tidy.bold",session.LastObservation!.View.Targets.Keys);Assert.DoesNotContain("tidy.prepare",session.LastObservation.View.Targets.Keys);
        foreach(var destination in new[]{"backpack_weight","backpack_tidy"})
            Assert.Contains("tidy",NavigationProfile.Names(routes.Configuration["maintenance"]!["navigation"]![destination]!["scene"]!["wait_for_pages"]));
    }
    [Theory][InlineData("production/tidy-result-current.jpg",true)][InlineData("screen_ui/tidy-result-current.jpg",true)][InlineData("screen_ui/production-complete-current.webp",false)]
    public void TidyResultUsesCompleteStableTitleAcrossBrightness(string fixture,bool expected)
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures",fixture));using var rgb=new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(new RgbImage(rgb.Width,rgb.Height,pixels)),new EmptyOcr(),new MemoryStore());session.Start();
        var frame=session.CreateMaintenancePort("tidy-result",new MemoryStore()).Observe();
        Assert.Equal(expected,frame.TidyComplete);if(expected)Assert.Equal("tidy_result",frame.Page);
    }
    [Fact] public void CapacityLimitedTransferReadsActualSelectedAmount()
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/storage-high-log-capacity-current.jpg"));using var rgb=new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(new RgbImage(rgb.Width,rgb.Height,pixels)),ocr,new MemoryStore());session.Start();
        session.SetVerifiedOwnerResolver(()=>new("capture","24081432","25251211640832"));
        var plan=new WithdrawalPlan(4719508601690960,"高級原木",776,"character:[\"24081432\",\"25251211640832\"]");
        var frame=session.CreateStoragePort(plan,"capacity",new MemoryStore()).Observe();
        Assert.True(frame.TransferDialog);Assert.Equal(650,frame.DialogQuantity);var capacity=routes.Profile.Definition["storage_supply"]!["transfer"]!["capacity"]!;
        var bag=session.QuantityReader.ColorText(session.LastCapture!.Image,session.Box(capacity["bag_region"]!),.75);
        var unit=session.QuantityReader.ColorText(session.LastCapture!.Image,session.Box(capacity["unit_region"]!),.75);
        Assert.True(frame.CapacityQuantity==650,$"bag={bag}; unit={unit}; limit={frame.CapacityQuantity}");Assert.False(frame.ConfirmAvailable);
    }
    [Fact] public void CurrentBottleTransferFindsShiftedExactTitleAndQuantity()
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/storage-bottle-transfer-current.jpg"));using var rgb=new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var session=new ScreenWorkflowSession(routes,Window(),new NoInputOps(new RgbImage(rgb.Width,rgb.Height,pixels)),ocr,new MemoryStore());session.Start();
        session.SetVerifiedOwnerResolver(()=>new("capture","24081432","25251211640832"));
        var plan=new WithdrawalPlan(1202292919,"空瓶",20,"character:[\"24081432\",\"25251211640832\"]");var frame=session.CreateStoragePort(plan,"bottle",new MemoryStore()).Observe();
        Assert.Equal("storage_transfer",frame.Page);Assert.Equal(plan.Scope,frame.Scope);Assert.Equal(plan.ItemId,frame.ItemId);Assert.True(frame.TransferDialog);Assert.Equal(1,frame.DialogQuantity);
        var wrong=plan with{ItemId=985189850,Name="樹枝"};var rejected=session.CreateStoragePort(wrong,"wrong",new MemoryStore()).Observe();
        Assert.Equal("storage_transfer",rejected.Page);Assert.Null(rejected.ItemId);Assert.False(rejected.TransferDialog);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void SharedExitWaitsForAnAlreadyDispatchedTravelLoad(bool travelling)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"]}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(new RgbImage(1280,960,new byte[1280*960*3]),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        if(travelling)session.BeginNavigation("maintenance:withdrawal:storage-navigation");
        var port=session.CreateStoragePort(new WithdrawalPlan(1,"ore",1,"account_storage"),"loading-exit",new MemoryStore());
        var runner=new StorageSupplyRunner([new(1,"ore",1,"account_storage")],port,new MemoryStore());
        Assert.True(runner.RetireSatisfiedPlans(_=>true));
        for(var tick=0;tick<(travelling?5:2);tick++)
        {Assert.False(runner.ReleaseNavigation());Assert.Equal(travelling,port.ReleaseWaiting);Assert.False(port.ReleaseAdvanced);Assert.False(runner.NavigationAbandoned);}
        Assert.Empty(clicks);Assert.Empty(native.Keys);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag");Assert.True(runner.ReleaseNavigation());Assert.False(port.ReleaseWaiting);
    }
    [Fact] public void InnRoomExitUsesMapButtonWhileTownSceneNeedsNoExtraExit()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"storage.room_exit":"storage_room_exit_icon"}},"storage_room_exit":{"anchors":["scene_skills"],"coordinate_targets":{"storage.exit_room":[645,833,862,895]}}}""");
        var clicks=new List<PixelPoint>();var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","storage_room_exit_icon"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("room-exit",new MemoryStore());port.Observe();Assert.False(port.ReleaseNavigation());Assert.Single(clicks);
        Assert.True(port.LastReleaseDispatched);Assert.True(port.ReleaseWaiting);
        Assert.False(port.ReleaseNavigation());Assert.Single(clicks);Assert.False(port.LastReleaseDispatched);
        native.CurrentImage=new RgbImage(1280,960,new byte[1280*960*3]);port.Observe();
        Assert.False(port.ReleaseNavigation());Assert.True(port.ReleaseWaiting);Assert.False(port.LastReleaseDispatched);
        Assert.True(new PixelBox(1220,8,1275,60).Contains(clicks[0]));
        native.CurrentImage=Stamp(routes.Profile,"scene_skills");Thread.Sleep(320);port.Observe();Assert.False(port.ReleaseNavigation());Assert.Equal(2,clicks.Count);
        native.CurrentImage=Stamp(routes.Profile,"scene_bag");port.Observe();Assert.True(port.ReleaseNavigation());Assert.Equal(2,clicks.Count);Assert.Empty(native.Keys);
    }
    [Fact] public void MaintenanceClosesAttendanceInterruptionAndResumesThePendingRoute()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"region_map":{"anchors":["scene_bag"],"coordinate_targets":{"map.npc":[600,400,620,420]}},"notification_attendance_reward":{"anchors":["scene_skills"],"coordinate_targets":{"notification.close":[100,100,140,140]}},"npc_dialogue":{"anchors":["maint_scene_map"]}}""");
        var clicks=new List<PixelPoint>();var ops=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),ops,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("notice-repair",new MemoryStore());port.Observe();port.Observe();
        var step=JsonNode.Parse("""{"target":"map.npc","expected_page":"npc_dialogue","settle_s":0}""")!.AsObject();
        Assert.False(port.TestNavigationStep(step).Completed);Assert.Single(clicks);
        ops.CurrentImage=Stamp(routes.Profile,"scene_skills");port.Observe();port.Observe();
        var interrupted=port.TestNavigationStep(step);Assert.Null(interrupted.Error);Assert.False(interrupted.Completed);
        Assert.Equal(2,clicks.Count);Assert.True(new PixelBox(100,100,140,140).Contains(clicks[1]));Assert.False(session.Guard.Paused);
        ops.CurrentImage=Stamp(routes.Profile,"maint_scene_map");port.Observe();port.Observe();
        Assert.True(port.TestNavigationStep(step).Completed);Assert.Equal(2,clicks.Count);
    }
    [Fact] public void MaintenanceWeightOnPetTabSelectsItemsOnceAndWaitsForSelectedTarget()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"bag":{"anchors":["scene_bag"],"targets":{"bag.items_selected":"scene_skills"},"coordinate_targets":{"bag.open_items":[600,800,660,850]}}}""");
        var clicks=new List<PixelPoint>();var ops=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),ops,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("pet-weight",new MemoryStore());port.Observe();var frame=port.Observe();
        Assert.Equal("backpack",frame.Page);Assert.True(frame.WeightTabRequired);Assert.Null(frame.Weight);
        Assert.Null(port.Navigate("backpack_weight"));Assert.Single(clicks);
        Thread.Sleep(340);port.Observe();Assert.Null(port.Navigate("backpack_weight"));Assert.Single(clicks);
        ops.CurrentImage=Stamp(routes.Profile,"scene_bag","scene_skills");
        port.Observe();var selected=port.Observe();Assert.False(selected.WeightTabRequired);
        // Item-tab OCR failure remains an unknown weight; it does not reselect the tab.
        Assert.Null(selected.Weight);Assert.Single(clicks);
    }
    [Fact] public void MaintenanceWeightTabHasBoundedWaitWhenSwitchDoesNotComplete()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"bag":{"anchors":["scene_bag"],"coordinate_targets":{"bag.open_items":[600,800,660,850]}}}""");
        routes.Configuration["maintenance"]!["navigation"]!["backpack_weight"]!["bag"]!["timeout_s"]=0;
        routes.Configuration["maintenance"]!["navigation"]!["backpack_weight"]!["bag"]!["settle_s"]=0;
        var clicks=new List<PixelPoint>();var ops=new NoInputOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),ops,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateMaintenancePort("pet-tab-timeout",new MemoryStore());port.Observe();port.Observe();
        Assert.Null(port.Navigate("backpack_weight"));port.Observe();
        Assert.Equal("navigation_result_not_observed:bag.open_items",port.Navigate("backpack_weight"));Assert.Single(clicks);
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
    [Fact] public void SharedListSearchCoversBothSidesOfRetainedOffsetAndStopsAtLimit()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"storage_character":{"anchors":["scene_bag"],"surfaces":{"storage.canvas":[50,280,625,850]}}}""");
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag"),[]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var plan=new WithdrawalPlan(198875762,"鐵礦石",1,"character_storage");var port=session.CreateStoragePort(plan,"search-both",new MemoryStore());
        var frame=port.Observe();var pass=frame.LocateLimit/3;
        Assert.True(frame.LocateIntervalSeconds>=1.2);
        for(var i=0;i<frame.LocateLimit;i++)
        {
            port.Observe();var captures=native.Captures;
            port.Perform(new("locate",plan.ItemId,plan.Name,1,plan.Scope,i));Assert.Equal(captures,native.Captures);
        }
        Assert.Equal(frame.LocateLimit*3,native.WheelDeltas.Count);
        Assert.All(native.WheelDeltas.Take(pass*3),delta=>Assert.Equal(-120,delta));
        Assert.All(native.WheelDeltas.Skip(pass*3),delta=>Assert.Equal(120,delta));
        Assert.Throws<InputBlockedException>(()=>port.Perform(new("locate",plan.ItemId,plan.Name,1,plan.Scope,frame.LocateLimit)));
        Assert.Empty(native.Keys);
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
    [Theory][InlineData(false,"iron_ore_nearby")][InlineData(true,"iron_ore_nearby")][InlineData(true,"point_daily_00")]
    public void GatheringCompletesOnlyAfterBoundStableLabelsDisappearOnScene(bool witnessedTask,string routeKey)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag","maint_scene_map"]}}""");
        routes.Configuration["gathering_observer"]!["completion_idle_seconds"]=0;routes.Configuration["gathering_observer"]!["completion_idle_observations"]=2;
        var native=new NoInputOps(Stamp(routes.Profile,"scene_bag","maint_scene_map"));
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        session.SavePortState("gathering:observed",new JsonObject{["started"]=true,["action_id"]="a",["session"]=session.Scope.SessionId,["awaiting_task_until"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d+300});
        var port=session.CreateGatheringPort(routeKey,"observed",new MemoryStore());
        var route=new RuntimeCatalog(routes.Configuration).Gathering[routeKey];
        session.BeginNavigation("gathering:observed",300);
        port.ObserveOnly(route,"a");port.ObserveOnly(route,"a");Assert.NotEqual("complete",port.ObserveOnly(route,"a").TaskStatus);
        if(witnessedTask)
        {
            native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map","gathering_observer.task_title","gathering_observer.iron_vein_objective");
            Assert.Equal("active",port.ObserveOnly(route,"a").TaskStatus);
            Assert.Equal("gathering:observed",session.CurrentNavigationId);
            native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map","gathering_observer.task_title");
            Assert.Equal("active",port.ObserveOnly(route,"a").TaskStatus);
        }
        native.CurrentImage=Stamp(routes.Profile,"scene_bag","maint_scene_map");
        var absent=port.ObserveOnly(route,"a");Assert.NotEqual("complete",absent.TaskStatus);
        absent=port.ObserveOnly(route,"a");Assert.Equal(witnessedTask?"complete":"unknown",absent.TaskStatus);
        Assert.Equal(witnessedTask?null:"gathering:observed",session.CurrentNavigationId);
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
    [Fact] public void CurrentWoolSelectionClicksOrdinarySheepWithoutScrolling()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        var clicks=new List<PixelPoint>();var native=new NoInputOps(GatheringLabelRecognitionTests.WoolFrame(),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var route=new RuntimeCatalog(routes.Configuration).Gathering["point_wool_00"];
        var port=session.CreateGatheringPort(route.Key,"wool-selection",new MemoryStore());
        Assert.Equal("wool_nodes",port.ObserveOnly(route,"wool-selection").Page);port.Navigate(route);
        Assert.Empty(native.WheelDeltas);var point=Assert.Single(clicks);Assert.InRange(point.X,803,834);Assert.InRange(point.Y,609,643);
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
    private sealed class FadeMenuLabelOcr:IColorOcrEngine
    {
        static readonly OcrLine Line=new("變更外貌/重生",.99);
        public bool Enabled=true;
        public OcrLine? Recognize(int width,int height,byte[] grayPixels)=>Enabled?Line:null;
        public OcrLine? RecognizeRgb(RgbImage image)=>Enabled?Line:null;
    }
    private sealed class MenuLabelOcr:IColorOcrEngine
    {
        static readonly OcrLine Line=new("變更外貌/重生",.99);
        public OcrLine? Recognize(int width,int height,byte[] grayPixels)=>Line;
        public OcrLine? RecognizeRgb(RgbImage image)=>Line;
    }
    // Same label for every search crop, but not for the 220x84 entry probe (height 84), so the
    // "menu already closed" branch is reachable while the menu itself still reads.
    private sealed class WideMenuOcr:IColorOcrEngine
    {
        static readonly OcrLine Line=new("變更外貌/重生",.99);
        public OcrLine? Recognize(int width,int height,byte[] grayPixels)=>Line;
        public OcrLine? RecognizeRgb(RgbImage image)=>image.Height<=60?Line:null;
    }
    private sealed class MemoryStore(JsonObject? initial=null):IWorkflowStore
    {private JsonObject? value=initial;public int Writes;public bool Fail;public JsonObject? Read()=>value is null?null:(JsonObject)value.DeepClone();public void Flush(JsonObject state){if(Fail)throw new IOException("disk");Writes++;value=(JsonObject)state.DeepClone();}}
    private sealed class IndependentFishingPort:IFishingPort
    {
        private int frame;public List<string> Actions=[];public bool CanResumeAtPoint=>true;
        public FishingFrame Observe()=>new(++frame,frame.ToString(),"fishing",FishingButton:true,LeisureMode:false,Compass:true);
        public void Perform(FishingAction action)=>Actions.Add(action.Kind);
        public void Pause(string reason){}public void Resume(){}
    }
    private sealed class NoInputOps(RgbImage? image=null,List<PixelPoint>? clicks=null):INativeWindowsOps,IGuardedWindowTransport
    {
        public RgbImage? CurrentImage;public Func<int,RgbImage>? CaptureFrame;public NativeWindowState? State;
        public double CaptureMilliseconds;public Action? OnPointerDown;public Action<ushort>? OnKeyDown;public HashSet<ushort> HeldKeys=[];
        public bool Background,Minimized;public nint OtherForeground=99;public int Captures;public List<ushort> Keys {get;}=[];public bool RequiresForeground=>!Background;public void BindGuard(ForegroundInputGuard guard){}
        private PixelPoint point;public int Wheels;public List<int> WheelDeltas {get;}=[];
        public NativeWindowState ReadWindow(nint hwnd)=>State??(Window() with{ForegroundId=Background?OtherForeground:1,Minimized=Minimized});public RgbImage CaptureClient(NativeWindowState state){var began=System.Diagnostics.Stopwatch.GetTimestamp();Captures++;var result=CaptureFrame?.Invoke(Captures)??CurrentImage??image??throw new InvalidOperationException("no capture in construction tests");CaptureMilliseconds+=System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;return result;}
        public void MoveClient(nint hwnd,PixelPoint position){if(clicks is null)throw new InvalidOperationException("no input");point=position;}public void MouseButton(MouseButton button,bool down){if(clicks is null)throw new InvalidOperationException("no input");if(down){OnPointerDown?.Invoke();clicks.Add(point);}}public void Wheel(int delta){if(clicks is null)throw new InvalidOperationException("no input");Wheels++;WheelDeltas.Add(delta);}public void Key(ushort key,bool down,bool unicode=false){if(down){OnKeyDown?.Invoke(key);Keys.Add(key);HeldKeys.Add(key);}else HeldKeys.Remove(key);}public bool AnyPhysicalKeyDown()=>false;
    }
}

