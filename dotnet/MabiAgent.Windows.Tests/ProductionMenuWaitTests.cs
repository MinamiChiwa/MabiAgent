using System.Text.Json.Nodes;
using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Windows.Tests;

public sealed class ProductionMenuWaitTests
{
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
        public RgbImage CurrentImage=image;public List<PixelPoint> Clicks{get;}=clicks??[];
        public bool RequiresForeground=>true;
        private PixelPoint point;
        public void BindGuard(ForegroundInputGuard guard){}
        public NativeWindowState ReadWindow(nint hwnd)=>Window();
        public RgbImage CaptureClient(NativeWindowState state)=>CurrentImage;
        public void MoveClient(nint hwnd,PixelPoint position)=>point=position;
        public void MouseButton(MouseButton button,bool down){if(down)Clicks.Add(point);}
        public void Wheel(int delta){}
        public void Key(ushort key,bool down,bool unicode=false){}
        public bool AnyPhysicalKeyDown()=>false;
    }
}
