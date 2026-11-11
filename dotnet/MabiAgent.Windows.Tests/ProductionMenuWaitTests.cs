using System.Text.Json.Nodes;
using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Windows.Tests;

public sealed class ProductionMenuWaitTests
{
    [Theory][InlineData("preview_queue")][InlineData("queue")]
    public void RestartedQueueDoesNotInheritPreviousRecipeSelectionDeadline(string phase)
    {
        var (routes,recipe,_)=Routes("wood_first");var clock=new WaitClock{Seconds=100};
        var native=new WaitOps(Stamp(routes.Profile,"scene_bag"),[]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        session.SavePortState("production:resumed-queue",new JsonObject{["recipe_selection_deadline"]=45d,["recipe_selection_purpose"]="recipe"});
        var store=new MemoryStore(new JsonObject{["phase"]=phase,["pending"]=null});
        var port=new ScreenProductionPort(session,recipe.Key,"resumed-queue",store,clock);
        Assert.Equal("skills",port.Observe().Page);Assert.Null(session.PortState("production:resumed-queue")["recipe_selection_deadline"]);
        port.Navigate("queue_preview",new(recipe.Key,recipe.Device,recipe.DurationSeconds));Assert.Single(native.Clicks);
    }

    [Theory][InlineData(0)][InlineData(255)]
    public void RemoteWoodIdentitySurvivesAnimatedTravelButtonChanges(byte value)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());
        var pixels=FishingFeedbackTests.ReadImage(Path.Combine(RecognitionTests.Root(),"tests/fixtures/production/wood-remote-after-collection-current.jpg")).CopyPixels();
        foreach(var box in new[]{new PixelBox(70,180,224,226),new PixelBox(774,867,1217,935)})
            for(var y=box.Top;y<box.Bottom;y++)for(var x=box.Left;x<box.Right;x++){var i=(y*1280+x)*3;pixels[i]=pixels[i+1]=pixels[i+2]=value;}
        var native=new WaitOps(new(1280,960,pixels),[]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();session.ConfirmDeviceArrival("wood");
        var frame=session.CreateProductionPort("catalog_wood_01","animated-preview",new MemoryStore()).Observe();
        Assert.Equal("recipe_detail",frame.Page);Assert.Equal("catalog_wood_01",frame.RecipeKey);Assert.True(session.LastObservation!.View.NeedsDeviceArrival);Assert.False(frame.AtDevice);Assert.False(frame.CraftAvailable);
    }

    [Fact]
    public void RemoteWoodPageOverridesPreviousCollectionArrivalAndTravelsBeforeCraft()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration.Remove("workbench_chains");
        var recipe=routes.Recipes["catalog_wood_01"];var requested=new ProductionRecipe(recipe.Key,recipe.Device,recipe.DurationSeconds);
        var remote=FishingFeedbackTests.ReadImage(Path.Combine(RecognitionTests.Root(),"tests/fixtures/production/wood-remote-after-collection-current.jpg"));
        var native=new WaitOps(remote,[]);var clock=new WaitClock();var store=new MemoryStore();
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        session.ConfirmDeviceArrival("wood");
        var port=new ScreenProductionPort(session,recipe.Key,"remote-after-collect",store,clock);
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(ScreenProductionPort).GetField("queueArrived",flags)!.SetValue(port,true);
        typeof(ScreenProductionPort).GetField("queueEpoch",flags)!.SetValue(port,session.InputEpoch);
        typeof(ScreenProductionPort).GetField("queueAt",flags)!.SetValue(port,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d);
        typeof(ScreenProductionPort).GetField("queueEmpty",flags)!.SetValue(port,new[]{0,1,2,3,4});
        var frame=port.Observe();Assert.Equal("recipe_detail",frame.Page);Assert.Equal(recipe.Key,frame.RecipeKey);
        Assert.False(frame.AtDevice);Assert.False(frame.CraftAvailable);
        port.Navigate("recipe",requested);Assert.Single(native.Clicks);
        native.CurrentImage=FishingFeedbackTests.ReadImage(Path.Combine(RecognitionTests.Root(),"tests/fixtures/production/wood-remote-panel-after-collection-current.jpg"));
        frame=port.Observe();Assert.Equal("wood_panel",frame.Page);Assert.False(frame.AtDevice);Assert.Null(session.OnsiteDevice);port.Navigate("queue",requested);
        Assert.Equal(2,native.Clicks.Count);Assert.True(session.PortState("production:remote-after-collect")["device_travel"]!.GetValue<bool>());
        native.CurrentImage=new RgbImage(1280,960,new byte[1280*960*3]);port.Observe();
        native.CurrentImage=FishingFeedbackTests.ReadImage(Path.Combine(RecognitionTests.Root(),"tests/fixtures/production/wood-panel-current.png"));
        Assert.True(port.Observe().AtDevice);port.Navigate("recipe",requested);Assert.Equal(3,native.Clicks.Count);
        // 实机故障帧只作识别回归；到场后的配方沿既有无损夹具验证委托。
        native.CurrentImage=FishingFeedbackTests.ReadImage(Path.Combine(RecognitionTests.Root(),"tests/fixtures/production/wood-plus-recipe-current.png"));
        var pixels=native.CurrentImage.CopyPixels();var back=routes.Profile.Templates["screen_overlay_back"].Box;
        for(var y=back.Top;y<back.Bottom;y++)for(var x=back.Left;x<back.Right;x++){var i=(y*1280+x)*3;pixels[i]=pixels[i+1]=pixels[i+2]=0;}
        native.CurrentImage=new(1280,960,pixels);frame=port.Observe();Assert.True(frame.AtDevice);Assert.True(frame.CraftAvailable);
        store.Flush(new JsonObject{["pending"]=new JsonObject{["kind"]="craft",["before_empty"]=new JsonArray(0,1,2,3,4)}});
        port.Click("craft",requested);Assert.Contains((ushort)32,native.Keys);
    }

    [Theory]
    [InlineData("iron_ingot_from_iron_ore")]
    [InlineData("wood_first")]
    [InlineData("leather_first")]
    [InlineData("fabric_first")]
    [InlineData("chemical_first")]
    [InlineData("food_first")]
    public void RecipeOverlayCoversEquipmentAcrossDimmedBackgrounds(string recipeKey)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var recipe=routes.Recipes[recipeKey];var device=routes.Devices[recipe.Device];
        using var recognizer=new ScreenPageRecognizer(routes.Profile);
        foreach(var brightness in new[]{70,150,255})
        {
            var pixels=Stamp(routes.Profile,device.ArrivalAnchors.ToArray()).CopyPixels();
            for(var i=0;i<pixels.Length;i++)pixels[i]=(byte)(pixels[i]*brightness/255);
            foreach(var name in recipe.ConfirmAnchors)
            {var spec=routes.Profile.Templates[name];var source=spec.Image.CopyPixels();for(var y=0;y<spec.Image.Height;y++)Array.Copy(source,y*spec.Image.Width*3,pixels,((spec.Box.Top+y)*1280+spec.Box.Left)*3,spec.Image.Width*3);}
            var view=recognizer.Recognize(new RgbImage(1280,960,pixels));
            Assert.Equal("recipe_detail",view.Page);Assert.Contains("recipe_detail.close",view.Targets.Keys);
            Assert.DoesNotContain(recipe.OpenTarget,view.Targets.Keys);
        }
    }
    [Theory]
    [InlineData("metal")][InlineData("wood")][InlineData("leather")]
    [InlineData("fabric")][InlineData("chemical")][InlineData("food")]
    public void DeviceTravelConfirmationCoversEveryEquipmentPageAndDismissalRestoresIt(string deviceKey)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var device=routes.Devices[deviceKey];var profile=routes.Profile;
        var confirmation=profile.Definition["pages"]!["device_travel_confirm"]!.DeepClone();
        confirmation["anchors"]=new JsonArray("scene_skills");
        profile.Definition["pages"]=new JsonObject
        {
            [device.QueuePage]=new JsonObject{["anchors"]=new JsonArray("scene_bag")},
            ["device_travel_confirm"]=confirmation
        };
        profile.Definition.Remove("page_variants");
        using var recognizer=new ScreenPageRecognizer(profile);
        var view=recognizer.Recognize(Stamp(profile,"scene_bag","scene_skills"));
        Assert.Equal("device_travel_confirm",view.Page);Assert.Contains("device.confirm_travel",view.Targets.Keys);
        Assert.Contains("device.cancel_travel",view.Targets.Keys);
        Assert.Equal(device.QueuePage,recognizer.Recognize(Stamp(profile,"scene_bag")).Page);
    }
    [Theory][InlineData("catalog_wood_01",true)][InlineData("catalog_wood_02",false)]
    public void RestoredWoodRecipeIdentifiesOnlyItsOwnRecipeAndClosesBeforeTravel(string recipeKey,bool matches)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var recipe=routes.Recipes[recipeKey];
        var image=FishingFeedbackTests.ReadImage(Path.Combine(RecognitionTests.Root(),"tests/fixtures/production/wood-plus-recipe-current.png"));
        var native=new WaitOps(image,[]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateProductionPort(recipeKey,"restored-overlay",new MemoryStore());var view=port.Observe();
        Assert.Equal("recipe_detail",view.Page);Assert.Equal(matches?recipeKey:null,view.RecipeKey);Assert.Equal(matches,view.CraftAvailable);Assert.False(view.AtDevice);
        port.Navigate("queue",new(recipeKey,recipe.Device,recipe.DurationSeconds));Assert.Single(native.Clicks);
    }
    [Theory][InlineData(10)][InlineData(90)][InlineData(150)]
    public void StableEquipmentGlyphsIgnoreSceneBackground(byte background)
    {
        var profile=ScreenRoutes.Load(RecognitionTests.Root()).Profile;using var matcher=new TemplateMatcher(profile);
        foreach(var spec in profile.Templates.Values.Where(t=>t.Path.Contains("equipment_stable",StringComparison.Ordinal)&&!t.Name.Contains("detail",StringComparison.Ordinal)))
        {
            var pixels=Stamp(profile,spec.Name).CopyPixels();
            for(var y=spec.Box.Top;y<spec.Box.Top+spec.Image.Height;y++)for(var x=spec.Box.Left;x<spec.Box.Left+spec.Image.Width;x++)
            {var i=(y*1280+x)*3;if(Math.Max(pixels[i],Math.Max(pixels[i+1],pixels[i+2]))<160)pixels[i]=pixels[i+1]=pixels[i+2]=background;}
            Assert.True(matcher.Anchor(new RgbImage(1280,960,pixels),spec.Name) is not null,spec.Name+"; background="+background);
        }
    }
    [Theory][InlineData("chemical-panel-20261108.jpg")][InlineData("chemical-panel-ready-current.jpg")]
    public void CurrentChemicalEquipmentPageIsRecognizedWithScrolledRecipeList(string fixture)
    {
        var root=RecognitionTests.Root();using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production",fixture));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var profile=ScreenRoutes.Load(root).Profile;var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        using var matcher=new TemplateMatcher(profile);using var recognizer=new ScreenPageRecognizer(profile,matcher);
        var view=recognizer.Recognize(frame);
        Assert.Equal("chemical_panel",view.Page);
    }

    [Fact]
    public void ProcessingMenuTimeoutAlsoExpiresOnUnrecognizedFrames()
    {
        var (routes,recipe,_)=Routes("chemical_first");var clock=new WaitClock();
        var native=new WaitOps(Stamp(routes.Profile,"scene_bag"),[]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=new ScreenProductionPort(session,recipe.Key,"expired-menu",new MemoryStore(),clock);
        port.Observe();port.Navigate("recipe_preview",new(recipe.Key,recipe.Device,recipe.DurationSeconds));
        native.CurrentImage=new RgbImage(1280,960,new byte[1280*960*3]);
        clock.Seconds=44;Assert.False(port.Observe().Stable);Assert.False(port.CanReleaseForeground());
        clock.Seconds=45;Assert.Throws<TimeoutException>(()=>port.Observe());
        Assert.True(port.CanReleaseForeground());Assert.Single(native.Clicks);
    }

    [Theory]
    [InlineData("iron_ingot_from_iron_ore")]
    [InlineData("wood_first")]
    [InlineData("leather_first")]
    [InlineData("fabric_first")]
    [InlineData("chemical_first")]
    [InlineData("food_first")]
    public void ProcessingWaitKeepsPublicNoticeVisibleAndResumesSameMenu(string recipeKey)
    {
        var (routes,recipe,device)=Routes(recipeKey);
        routes.Configuration["notifications"]=ScreenRoutes.Load(RecognitionTests.Root()).Configuration["notifications"]!.DeepClone();
        var native=new WaitOps(Stamp(routes.Profile,"scene_bag"),[]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateProductionPort(recipe.Key,"late-ad",new MemoryStore());var requested=new ProductionRecipe(recipe.Key,recipe.Device,recipe.DurationSeconds);
        port.Observe();port.Navigate("recipe_preview",requested);
        native.CurrentImage=Stamp(routes.Profile,"notification.news_tabs");
        Assert.Equal("notification_news",port.Observe().Page);
        Assert.True(session.DismissNotice(notificationsOnly:true));
        native.CurrentImage=Stamp(routes.Profile,device.ArrivalAnchors.ToArray());
        Assert.Equal(device.QueuePage,port.Observe().Page);
        port.Navigate("recipe_preview",requested);Assert.Equal(2,native.Clicks.Count);
    }

    [Theory]
    [InlineData("maint_scene_map")]
    [InlineData("scene_skills")]
    public void DeviceTravelTimeoutSurvivesUnknownFramesPauseAndPortRestart(string finalAnchor)
    {
        var (routes,recipe,device)=Routes("chemical_first");var clock=new WaitClock();var journal=new MemoryStore();
        routes.Profile.Definition["pages"]=new JsonObject{[device.QueuePage]=Page(Names("scene_bag"))};
        var native=new WaitOps(Stamp(routes.Profile,"scene_bag"),[]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),journal);session.Start();
        var port=new ScreenProductionPort(session,recipe.Key,"expired-trip",new MemoryStore(),clock);var requested=new ProductionRecipe(recipe.Key,recipe.Device,recipe.DurationSeconds);
        port.Observe();port.Navigate("queue",requested);Assert.Single(native.Clicks);
        native.CurrentImage=new RgbImage(1280,960,new byte[1280*960*3]);clock.Seconds=100;
        Assert.True(port.Observe().NavigationActive);
        session.Pause("manual");session.ResumeExplicit();
        port=new ScreenProductionPort(session,recipe.Key,"expired-trip",new MemoryStore(),clock);
        clock.Seconds=121;native.CurrentImage=Stamp(routes.Profile,finalAnchor);
        Assert.Throws<TimeoutException>(()=>port.Observe());
        Assert.True(port.CanReleaseForeground());Assert.Null(session.CurrentNavigationId);Assert.Single(native.Clicks);
        Assert.False(journal.Read()!["ports"]!["production:expired-trip"]!["device_travel"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("iron_ingot_from_iron_ore")]
    [InlineData("wood_first")]
    [InlineData("leather_first")]
    [InlineData("fabric_first")]
    [InlineData("chemical_first")]
    [InlineData("food_first")]
    public void ProcessingWaitRecognizesDirectCurrentDevicePageForEveryDevice(string recipeKey)
    {
        var (routes,recipe,device)=Routes(recipeKey);var clicks=new List<PixelPoint>();
        var native=new WaitOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateProductionPort(recipe.Key,"direct-device",new MemoryStore());var requested=new ProductionRecipe(recipe.Key,recipe.Device,recipe.DurationSeconds);

        Assert.Equal("skills",port.Observe().Page);port.Navigate("recipe_preview",requested);Assert.Single(clicks);
        native.CurrentImage=Stamp(routes.Profile,device.ArrivalAnchors.ToArray());

        Assert.Equal(device.QueuePage,port.Observe().Page);
        port.Navigate("recipe_preview",requested);
        Assert.Equal(2,clicks.Count);
    }

    [Fact]
    public void ProcessingWaitRecognizesDirectRecipePageWithoutBecomingUnknown()
    {
        var (routes,recipe,_)=Routes("iron_ingot_from_iron_ore");var clicks=new List<PixelPoint>();
        var native=new WaitOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateProductionPort(recipe.Key,"direct-recipe",new MemoryStore());var requested=new ProductionRecipe(recipe.Key,recipe.Device,recipe.DurationSeconds);

        port.Observe();port.Navigate("recipe_preview",requested);Assert.Single(clicks);
        native.CurrentImage=Stamp(routes.Profile,recipe.ConfirmAnchors.ToArray());
        var observed=port.Observe();

        Assert.Equal(recipe.Page,observed.Page);Assert.Equal(recipe.Key,observed.RecipeKey);
        port.Navigate("recipe_preview",requested);Assert.Equal(2,clicks.Count);
    }

    [Fact]
    public void ProcessingWaitDoesNotRecognizeOrRepeatInputOnUnrelatedDevicePage()
    {
        var (routes,recipe,device)=Routes("iron_ingot_from_iron_ore");var unrelated=routes.Devices.Values.First(d=>d.Key!=device.Key);
        var native=new WaitOps(Stamp(routes.Profile,"scene_bag"),[]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateProductionPort(recipe.Key,"unrelated",new MemoryStore());var requested=new ProductionRecipe(recipe.Key,recipe.Device,recipe.DurationSeconds);

        port.Observe();port.Navigate("recipe_preview",requested);Assert.Single(native.Clicks);
        native.CurrentImage=Stamp(routes.Profile,unrelated.ArrivalAnchors.ToArray());

        Assert.Equal("unknown",port.Observe().Page);port.Navigate("recipe_preview",requested);port.Navigate("recipe_preview",requested);Assert.Single(native.Clicks);Assert.False(port.CanReleaseForeground());
        native.CurrentImage=Stamp(routes.Profile,device.ArrivalAnchors.ToArray());Assert.Equal(device.QueuePage,port.Observe().Page);port.Navigate("recipe_preview",requested);Assert.Equal(2,native.Clicks.Count);
    }

    [Fact]
    public void ProcessingWaitStillAdvancesFromCategoryPage()
    {
        var (routes,recipe,_)=Routes("iron_ingot_from_iron_ore");var clicks=new List<PixelPoint>();
        var native=new WaitOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreateProductionPort(recipe.Key,"category",new MemoryStore());var requested=new ProductionRecipe(recipe.Key,recipe.Device,recipe.DurationSeconds);

        port.Observe();port.Navigate("recipe_preview",requested);Assert.Single(clicks);
        native.CurrentImage=Stamp(routes.Profile,"preset_category_title","preset_category_processing");

        Assert.Equal("category_page",port.Observe().Page);port.Navigate("recipe_preview",requested);Assert.Equal(2,clicks.Count);
    }

    [Fact]
    public void KnownSkillsPageKeepsTwoBoundedNavigationRetries()
    {
        var (routes,recipe,_)=Routes("iron_ingot_from_iron_ore");var clicks=new List<PixelPoint>();var clock=new WaitClock();
        var native=new WaitOps(Stamp(routes.Profile,"scene_bag"),clicks);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=new ScreenProductionPort(session,recipe.Key,"bounded",new MemoryStore(),clock);var requested=new ProductionRecipe(recipe.Key,recipe.Device,recipe.DurationSeconds);

        port.Observe();port.Navigate("recipe_preview",requested);Assert.Single(clicks);
        foreach(var seconds in new[]{6d,12d}){clock.Seconds=seconds;port.Observe();port.Navigate("recipe_preview",requested);}
        Assert.Equal(3,clicks.Count);
        clock.Seconds=18;port.Observe();Assert.Throws<InputBlockedException>(()=>port.Navigate("recipe_preview",requested));Assert.Equal(3,clicks.Count);
    }

    [Theory][InlineData("none")][InlineData("manual")][InlineData("navigation")][InlineData("travel")][InlineData("round")]
    public void UnknownFishingPreparationReleasesOnlyWithoutNativeActivity(string blocker)
    {
        var (routes,_,_)=Routes("wood_first");var native=new WaitOps(new(1280,960,new byte[1280*960*3]),[]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var saved=new JsonObject();
        if(blocker=="travel")saved["travel_issued"]=true;
        if(blocker=="round")saved["round_observed"]=true;
        session.SavePortState("fishing:unknown-preparation",saved);
        var port=new ScreenFishingPort(session,FishingPlan.CurrentLocation,"unknown-preparation",
            new MemoryStore(new JsonObject{["phase"]="refresh",["status"]="running"}));
        Assert.Equal(blocker=="round"?"fishing":"unknown",port.Observe().Page);Assert.False(port.CanReleaseForeground());
        if(blocker=="manual")session.Pause("manual");
        if(blocker=="navigation")session.BeginNavigation("other");
        Assert.Equal(blocker=="none",port.TryReleasePreparation());Assert.Equal(blocker=="none",port.CanReleaseForeground());
        Assert.Empty(native.Clicks);Assert.Empty(native.Keys);
    }

    [Fact] public void FishingMenuTotalDeadlineIncludesUnknownFrames()
    {
        var (routes,_,_)=Routes("wood_first");var clock=new WaitClock();var point="point_fishing_03";
        var nav=routes.Configuration["gathering_routes"]![point]!["navigation"]!;
        nav["steps"]=new JsonArray(new JsonObject{["page"]="skills",["target"]="skills.open_processing",["expected_page"]="category_page"});
        var native=new WaitOps(Stamp(routes.Profile,"scene_bag"),[]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=new ScreenFishingPort(session,point,"unknown-menu",
            new MemoryStore(new JsonObject{["phase"]="navigate",["status"]="running"}),clock);
        port.Observe();port.Perform(new("navigate",point));Assert.Single(native.Clicks);
        native.CurrentImage=new(1280,960,new byte[1280*960*3]);clock.Seconds=91;
        Assert.Throws<TimeoutException>(()=>port.Observe());Assert.True(port.CanReleaseForeground());
        Assert.Single(native.Clicks);Assert.Empty(native.Keys);Assert.Null(session.CurrentNavigationId);
    }

    [Fact] public void FishingMenuExpiryKeepsAnAlreadyDispatchedNativeTrip()
    {
        var (routes,_,_)=Routes("wood_first");var clock=new WaitClock();var point="point_fishing_03";
        var nav=routes.Configuration["gathering_routes"]![point]!["navigation"]!;
        var page=nav["detail_page"]!.GetValue<string>();var target=nav["start_target"]!.GetValue<string>();
        routes.Profile.Definition["pages"]=new JsonObject{[page]=Page(Names("scene_bag"),boxes:Boxes((target,new[]{40,850,120,930})))};
        var native=new WaitOps(Stamp(routes.Profile,"scene_bag"),[]);
        using var session=new ScreenWorkflowSession(routes,Window(),native,new EmptyOcr(),new MemoryStore());session.Start();
        var port=new ScreenFishingPort(session,point,"unknown-trip",
            new MemoryStore(new JsonObject{["phase"]="navigate",["status"]="running"}),clock);
        port.Observe();port.Perform(new("navigate",point));Assert.Single(native.Clicks);Assert.NotNull(session.CurrentNavigationId);
        native.CurrentImage=new(1280,960,new byte[1280*960*3]);clock.Seconds=91;port.Observe();
        Assert.False(port.TryReleasePreparation());Assert.False(port.CanReleaseForeground());
        Assert.True(session.PortState("fishing:unknown-trip")["travel_issued"]!.GetValue<bool>());
        Assert.NotNull(session.CurrentNavigationId);Assert.Single(native.Clicks);Assert.Empty(native.Keys);
    }

    private static (ScreenRoutes Routes,RecipeRoute Recipe,DeviceRoute Device) Routes(string recipeKey)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
        routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
        var recipe=routes.Recipes[recipeKey];var device=routes.Devices[recipe.Device];var unrelated=routes.Devices.Values.First(d=>d.Key!=device.Key);
        var pages=new JsonObject
        {
            ["skills"]=Page(Names("scene_bag"),targets:Targets(("skills.open_processing","scene_bag"))),
            ["category_page"]=Page(Names("preset_category_title","preset_category_processing"),
                boxes:Boxes((device.CategoryTarget,new[]{240,245,320,290}))),
            [device.QueuePage]=Page(Names(device.ArrivalAnchors.ToArray()),
                boxes:Boxes((recipe.OpenTarget,new[]{820,860,930,930}),(device.GotoTarget,new[]{40,850,120,930}))),
            [recipe.Page]=Page(Names(recipe.ConfirmAnchors.ToArray()),
                boxes:Boxes((device.RecipeCloseTarget,new[]{40,40,120,120}))),
            [unrelated.QueuePage]=Page(Names(unrelated.ArrivalAnchors.ToArray()))
        };
        routes.Profile.Definition["pages"]=pages;return(routes,recipe,device);
    }

    private static JsonObject Page(JsonArray anchors,JsonObject? targets=null,JsonObject? boxes=null)
    {
        var page=new JsonObject{["anchors"]=anchors};if(targets is not null)page["targets"]=targets;if(boxes is not null)page["coordinate_targets"]=boxes;return page;
    }

    private static JsonArray Names(params string[] values)=>new(values.Select(value=>(JsonNode?)JsonValue.Create(value)).ToArray());

    private static JsonObject Targets(params (string Name,string Anchor)[] values)
    {
        var result=new JsonObject();foreach(var (name,anchor) in values)result[name]=anchor;return result;
    }

    private static JsonObject Boxes(params (string Name,int[] Values)[] values)
    {
        var result=new JsonObject();foreach(var (name,numbers) in values)result[name]=new JsonArray(numbers.Select(value=>(JsonNode?)JsonValue.Create(value)).ToArray());return result;
    }

    private static RgbImage Stamp(NavigationProfile profile,params string[] names)
    {
        var pixels=new byte[1280*960*3];foreach(var name in names){var template=profile.Templates[name];var source=template.Image.CopyPixels();for(var y=0;y<template.Image.Height;y++)Array.Copy(source,y*template.Image.Width*3,pixels,((template.Box.Top+y)*1280+template.Box.Left)*3,template.Image.Width*3);}return new(1280,960,pixels);
    }

    private static NativeWindowState Window()=>new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,1280,960,96),new(0,0,1280,960),1,true,false,true,new(0,0));

    private sealed class WaitClock:TimeProvider
    {
        public double Seconds;
        public override DateTimeOffset GetUtcNow()=>DateTimeOffset.UnixEpoch.AddSeconds(Seconds);
    }

    private sealed class EmptyOcr:IOcrEngine
    {
        public OcrLine? Recognize(int width,int height,byte[] grayPixels)=>null;
    }

    private sealed class MemoryStore(JsonObject? initial=null):IWorkflowStore
    {
        private JsonObject? value=initial;
        public JsonObject? Read()=>value is null?null:(JsonObject)value.DeepClone();
        public void Flush(JsonObject state)=>value=(JsonObject)state.DeepClone();
    }

    private sealed class WaitOps(RgbImage image,List<PixelPoint>? clicks):INativeWindowsOps,IGuardedWindowTransport
    {
        public RgbImage CurrentImage=image;public List<PixelPoint> Clicks{get;}=clicks??[];public List<ushort> Keys{get;}=[];
        public bool RequiresForeground=>true;
        private PixelPoint point;
        public void BindGuard(ForegroundInputGuard guard){}
        public NativeWindowState ReadWindow(nint hwnd)=>Window();
        public RgbImage CaptureClient(NativeWindowState state)=>CurrentImage;
        public void MoveClient(nint hwnd,PixelPoint position)=>point=position;
        public void MouseButton(MouseButton button,bool down){if(down)Clicks.Add(point);}
        public void Wheel(int delta){}
        public void Key(ushort key,bool down,bool unicode=false){if(down)Keys.Add(key);}
        public bool AnyPhysicalKeyDown()=>false;
    }
}
