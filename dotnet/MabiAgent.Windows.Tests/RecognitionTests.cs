using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;
public class RecognitionTests
{
    [Fact] public void CompletedHarvestSceneWithNearbyCharactersRemainsRecognizable()
    {
        var routes=ScreenRoutes.Load(Root());var frame=FishingFeedbackTests.ReadImage(Path.Combine(Root(),"tests/fixtures/production/harvest-idle-current.jpg"));
        using var matcher=new TemplateMatcher(routes.Profile);using var recognizer=new ScreenPageRecognizer(routes.Profile,matcher);
        var view=recognizer.Recognize(frame);
        var profile=routes.Profile.Definition;
        var definitions=profile["pages"]!.AsObject().Concat(profile["page_variants"]?.AsObject()??[]);
        var matches=definitions.Where(p=>NavigationProfile.Names(p.Value?["anchors"]).Length>0&&NavigationProfile.Names(p.Value?["anchors"]).All(n=>matcher.Anchor(frame,n) is not null)).Select(p=>p.Key);
        Assert.True(view.Page=="scene","page="+view.Page+"; matched="+string.Join(',',matches)+"; anchors="+string.Join(';',new[]{"scene_bag","scene_skills","scene_menu","maint_scene_map"}.Select(n=>n+"="+matcher.Anchor(frame,n,.5,false,.5)?.Score)));
    }
    [Fact] public void CurrentFullBackpackTidyIsRecognized()
    {
        var routes=ScreenRoutes.Load(Root());var frame=FishingFeedbackTests.ReadImage(Path.Combine(Root(),"tests/fixtures/production/backpack-full-tidy-current.png"));
        using var recognizer=new ScreenPageRecognizer(routes.Profile);Assert.Equal("tidy",recognizer.Recognize(frame).Page);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void HarvestSceneRequiresBothBagCoreAndMap(bool removeBag)
    {
        var routes=ScreenRoutes.Load(Root());var frame=FishingFeedbackTests.ReadImage(Path.Combine(Root(),"tests/fixtures/production/harvest-idle-current.jpg"));
        using var matcher=new TemplateMatcher(routes.Profile);var hit=matcher.Anchor(frame,removeBag?"scene_bag":"maint_scene_map")!;
        var pixels=frame.CopyPixels();for(var y=hit.Box.Top;y<hit.Box.Bottom;y++)Array.Clear(pixels,(y*frame.Width+hit.Box.Left)*3,hit.Box.Width*3);
        using var recognizer=new ScreenPageRecognizer(routes.Profile);Assert.NotEqual("scene",recognizer.Recognize(new RgbImage(frame.Width,frame.Height,pixels)).Page);
    }
    [Theory][InlineData("tir-inn-map-current.png","map.inn")][InlineData("tir-inn-wide-current.png","map.inn")][InlineData("tir-inn-player-covered-current.png","map.inn")][InlineData("tir-inn-zoom-current.png","map.storage_box")][InlineData("tir-inn-box-unselected-current.png","map.storage_box")][InlineData("tir-inn-box-shifted-current.png","map.storage_box")][InlineData("tir-storage-travel-current.png","storage.travel")]
    public void InnMapUsesBuildingAndStorageBoxTargets(string fixture,string target)
    {
        var routes=ScreenRoutes.Load(Root());using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/production",fixture));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var recognizer=new ScreenPageRecognizer(routes.Profile);var view=recognizer.Recognize(new RgbImage(rgb.Width,rgb.Height,pixels));
        Assert.Equal(target=="storage.travel"?"storage_travel":"region_map",view.Page);Assert.Contains(target,view.Targets.Keys);
        if(view.Page=="region_map")Assert.Contains("map.current_is_tir",view.Targets.Keys);
    }
    internal static MabiAgent.Capture.ClientCaptureLayout CurrentClientLayout()
    {
        var game=Assert.Single(GameWindowDiscovery.List());
        return new MabiAgent.Capture.GameClientLayoutResolver(NativeWindowsOps.ReadProcessIdentity).Resolve(new(game.State.ProcessId,new DateTime(game.State.ProcessStartUtcTicks,DateTimeKind.Utc)),default);
    }

    [Fact] public void LongItemDescriptionKeepsFunctionalDetailAnchorAndSaleTarget()
    {
        var routes=ScreenRoutes.Load(Root());using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/production/high-log-plus-detail-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        using var matcher=new TemplateMatcher(routes.Profile);Assert.NotNull(matcher.Anchor(frame,"surplus_item_source"));
        using var recognizer=new ScreenPageRecognizer(routes.Profile,matcher);var view=recognizer.Recognize(frame);Assert.Equal("bag_item_detail",view.Page);Assert.Contains("bag.sell_open",view.Targets.Keys);
    }
    [Fact] public void CurrentPaperMerchantMenuExposesShopAction()
    {
        var root=Root();var routes=ScreenRoutes.Load(root);using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/paper-merchant-current.jpg"));using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        using var matcher=new TemplateMatcher(routes.Profile);using var recognizer=new ScreenPageRecognizer(routes.Profile,matcher);var view=recognizer.Recognize(frame);
        Assert.True(view.Page=="shop_dialog","page="+view.Page+"; "+string.Join("; ",new[]{"purchase.dialog_name","purchase.dialog_shop","purchase.dialog_end"}.Select(n=>n+"="+matcher.Anchor(frame,n)?.Score)));Assert.Contains("purchase.open",view.Targets.Keys);
    }
    [Fact] public void CurrentEastWallFishingListExposesNamedNavigationTarget()
    {
        var root=Root();using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/east-wall-fishing-list-current.jpg"));using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var recognizer=new ScreenPageRecognizer(ScreenRoutes.Load(root).Profile);var view=recognizer.Recognize(new RgbImage(rgb.Width,rgb.Height,pixels));
        Assert.Equal("fishing_nodes",view.Page);Assert.Contains("gathering.open_point_fishing_02",view.Targets.Keys);
    }
    [Fact] public void CurrentStorageDetailUsesStableHeaderAndExposesPrepareTarget()
    {
        var profile=ScreenRoutes.Load(Root()).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/production/storage-detail-current.jpg"));using var rgb=new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var frame=new RgbImage(rgb.Width,rgb.Height,pixels);using var matcher=new TemplateMatcher(profile);using var recognizer=new ScreenPageRecognizer(profile,matcher);var view=recognizer.Recognize(frame);
        Assert.True(view.Page=="storage_detail","page="+view.Page+"; "+string.Join("; ",new[]{"storage_take_button","storage_detail_weight_header","storage_close_live","storage_character_dim"}.Select(n=>n+"="+matcher.Anchor(frame,n)?.Score)));
        Assert.Contains("storage.prepare",view.Targets.Keys);
    }
    [Theory][InlineData("storage-bank-menu-current.jpg")][InlineData("storage-bank-three-options.png")]
    public void BankStorageMenuOverridesOrdinaryNpcDialogue(string fixture)
    {
        var profile=ScreenRoutes.Load(Root()).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/production",fixture));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var recognizer=new ScreenPageRecognizer(profile);var view=recognizer.Recognize(new RgbImage(rgb.Width,rgb.Height,pixels));
        Assert.Equal("storage_dialog",view.Page);Assert.Contains("storage.open",view.Targets.Keys);Assert.Contains("storage_dialog.leave",view.Targets.Keys);
    }
    [Fact] public void BankSkipDialogueIsNotARepairResult()
    {
        var profile=ScreenRoutes.Load(Root()).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/production/bank-dialogue-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var recognizer=new ScreenPageRecognizer(profile);var view=recognizer.Recognize(new RgbImage(rgb.Width,rgb.Height,pixels));
        Assert.Equal("npc_dialogue",view.Page);Assert.Contains("dialog.advance",view.Targets.Keys);
    }
    [Theory][InlineData("storage-exit-current.jpg")][InlineData("storage-exit-dunbarton.jpg")][InlineData("storage-exit-my-home.png")]
    public void CurrentInnExitRecognizesStableConfirmationWithoutDialogue(string fixture)
    {
        var profile=ScreenRoutes.Load(Root()).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/production",fixture));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var recognizer=new ScreenPageRecognizer(profile);var view=recognizer.Recognize(new RgbImage(rgb.Width,rgb.Height,pixels));
        Assert.Equal("storage_room_exit",view.Page);Assert.Contains("storage.exit_room",view.Targets.Keys);
    }
    [Fact] public void StorageOpeningUsesFunctionalMenuInsteadOfDialogueOrAnimatedChest()
    {
        var profile=ScreenRoutes.Load(Root()).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/production/storage-open-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var recognizer=new ScreenPageRecognizer(profile);var view=recognizer.Recognize(new RgbImage(rgb.Width,rgb.Height,pixels));
        Assert.Equal("storage_dialog",view.Page);Assert.Contains("storage.open",view.Targets.Keys);
    }
    [Fact] public void CurrentArrivedFabricPanelUsesStableTitleAndEquipmentText()
    {
        var profile=ScreenRoutes.Load(Root()).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/production/fabric-arrived-current.png"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var frame=new RgbImage(rgb.Width,rgb.Height,pixels);using var matcher=new TemplateMatcher(profile);using var recognizer=new ScreenPageRecognizer(profile,matcher);
        Assert.NotNull(matcher.Anchor(frame,"preset_fabric_title"));Assert.NotNull(matcher.Anchor(frame,"preset_fabric_equipment"));
        var view=recognizer.Recognize(frame);Assert.Equal("fabric_panel",view.Page);Assert.Contains("fabric.open_fabric_first",view.Targets.Keys);
    }
    [Fact] public void CurrentArrivedWoodPanelUsesStableTitleAndEquipmentText()
    {
        var profile=ScreenRoutes.Load(Root()).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/production/wood-arrived-20261102.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var frame=new RgbImage(rgb.Width,rgb.Height,pixels);using var matcher=new TemplateMatcher(profile);using var recognizer=new ScreenPageRecognizer(profile,matcher);
        Assert.NotNull(matcher.Anchor(frame,"preset_wood_title"));Assert.NotNull(matcher.Anchor(frame,"preset_wood_equipment"));
        var view=recognizer.Recognize(frame);Assert.Equal("wood_panel",view.Page);Assert.Contains("wood.open_catalog_wood_03",view.Targets.Keys);
        Assert.Null(matcher.Anchor(frame,"preset_fabric_title"));
    }
    [Fact] public void CurrentTidyResultUsesItsOwnStableCompletionTitle()
    {
        var root=Root();var profile=ScreenRoutes.Load(root).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/screen_ui/tidy-result-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var frame=new RgbImage(rgb.Width,rgb.Height,pixels);using var matcher=new TemplateMatcher(profile);using var recognizer=new ScreenPageRecognizer(profile);var view=recognizer.Recognize(frame);
        Assert.True(view.Page=="tidy_result","page="+view.Page+"; score="+matcher.Match(frame,"maint_tidy_done",unique:false,thresholdOverride:.1)?.Score);Assert.Contains("completion.confirm",view.Targets.Keys);
        Assert.DoesNotContain("tidy.confirm",view.Targets.Keys);
    }
    [Theory][InlineData("production/metal-arrived-20261102.jpg")][InlineData("private-theater/game-selected-20261102.jpg")]
    public void TidyCompletionTitleDoesNotMatchOtherCurrentPages(string file)
    {
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures",file));using var rgb=new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var matcher=new TemplateMatcher(ScreenRoutes.Load(Root()).Profile);Assert.Null(matcher.Anchor(new(rgb.Width,rgb.Height,pixels),"maint_tidy_done"));
    }
    [Fact] public void CurrentArrivedMetalPanelUsesStableTitleAndEquipmentText()
    {
        var root=Root();var profile=ScreenRoutes.Load(root).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/metal-arrived-20261102.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var frame=new RgbImage(rgb.Width,rgb.Height,pixels);using var matcher=new TemplateMatcher(profile);
        using var recognizer=new ScreenPageRecognizer(profile,matcher);var view=recognizer.Recognize(frame);
        Assert.Equal("metal_panel",view.Page);Assert.NotNull(matcher.Anchor(frame,"arrived_title"));
        Assert.Null(matcher.Anchor(frame,"preset_fabric_title"));
        Assert.Contains("metal.open_iron_ingot_from_iron_ore",view.Targets.Keys);
    }
    [Fact] public void SixProcessingCategoriesOverrideSharedSkillsSidebar()
    {
        var profile=ScreenRoutes.Load(Root()).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/category-skills-overlap.webp"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        using var matcher=new TemplateMatcher(profile);using var recognizer=new ScreenPageRecognizer(profile,matcher);var view=recognizer.Recognize(frame);
        Assert.True(view.Page=="category_page","page="+view.Page+"; "+string.Join("; ",new[]{"category_title","category_metal","category_food","category_processing","preset_category_title","preset_category_processing","preset_category_wood","skills_processing_inactive","skills_close"}.Select(n=>n+"="+matcher.Anchor(frame,n)?.Score)));
        foreach(var device in new[]{"metal","wood","fabric","leather","chemical","food"})Assert.Contains("category.open_"+device,view.Targets.Keys);
        Assert.DoesNotContain("production.craft",view.Targets.Keys);
    }
    [Fact] public void GreenSceneryDoesNotOverrideDeclaredSceneAnchors()
    {
        var profile=ScreenRoutes.Load(Root()).Profile;var pixels=new byte[1280*960*3];
        for(var y=48;y<219;y++)for(var x=433;x<833;x++)pixels[(y*1280+x)*3+1]=180;
        foreach(var name in new[]{"scene_bag","maint_scene_map"})
        {var t=profile.Templates[name];var source=t.Image.CopyPixels();for(var y=0;y<t.Image.Height;y++)Array.Copy(source,y*t.Image.Width*3,pixels,((t.Box.Top+y)*1280+t.Box.Left)*3,t.Image.Width*3);}
        using var recognizer=new ScreenPageRecognizer(profile);var view=recognizer.Recognize(new RgbImage(1280,960,pixels));
        Assert.Equal("scene",view.Page);Assert.False(view.Modal);
    }
    [Theory][InlineData(false,false)][InlineData(true,false)][InlineData(true,true)]
    public void CurrentRegionShortcutRequiresVerifiedTownHeader(bool correctRegion,bool worldHeader)
    {
        var routes=ScreenRoutes.Load(Root());var profile=routes.Profile;var pixels=new byte[1280*960*3];
        foreach(var name in new[]{"maint_map_close","maint_region_world"}.Concat(correctRegion?[worldHeader?"maint_tir_world_header":"maint_tir_region_header"]:Array.Empty<string>()))
        {
            var t=profile.Templates[name];var source=t.Image.CopyPixels();
            for(var y=0;y<t.Image.Height;y++)Array.Copy(source,y*t.Image.Width*3,pixels,((t.Box.Top+y)*1280+t.Box.Left)*3,t.Image.Width*3);
        }
        using var recognizer=new ScreenPageRecognizer(profile);var view=recognizer.Recognize(new RgbImage(1280,960,pixels));
        Assert.Equal("region_map",view.Page);Assert.Equal(correctRegion,view.Targets.ContainsKey("map.current_is_tir"));
        foreach(var destination in new[]{"repair_quote","purchase_shop"})
        {
            var choice=routes.Configuration["maintenance"]!["navigation"]![destination]!["region_map"]!["stages"]!["world"]!["choices"]!.AsArray().Single(c=>c!["target"]!.GetValue<string>()=="map.current_region");
            Assert.Equal("map.current_is_tir",choice!["requires"]![0]!.GetValue<string>());Assert.True(choice["search_only"]!.GetValue<bool>());
        }
    }
    [Fact]
    public void MerchantPlayerMarkerUsesLowerFaceWithoutMatchingRepairNpc()
    {
        using var source=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/screen_ui/shop-portrait-player-marker.png"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(source,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var crop=new byte[64*64*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,crop,0,crop.Length);var pixels=new byte[1280*960*3];
        for(var y=0;y<64;y++)Array.Copy(crop,y*64*3,pixels,((252+y)*1280+297)*3,64*3);
        using var matcher=new TemplateMatcher(ScreenRoutes.Load(Root()).Profile);var frame=new RgbImage(1280,960,pixels);var hit=matcher.Anchor(frame,"purchase.map_portrait");
        Assert.NotNull(hit);Assert.True(hit.Score>=.97);Assert.Equal("purchase.map_portrait_player_lowerface",hit.Variant);Assert.Null(matcher.Anchor(frame,"maint_ferghus_portrait"));
    }
    [Theory][InlineData(false,"bag")][InlineData(true,"repair_tools")]
    public void SpecificToolTabCoversBagWithoutHidingOrdinaryBackpack(bool tools,string expected)
    {
        var profile=ScreenRoutes.Load(Root()).Profile;var pixels=new byte[1280*960*3];
        var names=new List<string>{"bag_tools_tab","bag_fashion_tab","bag_auto_wear"};
        if(tools)names.AddRange(["maint_tools_selected","maint_auto_equip"]);
        foreach(var name in names)
        {
            var t=profile.Templates[name];var source=t.Image.CopyPixels();
            for(var y=0;y<t.Image.Height;y++)Array.Copy(source,y*t.Image.Width*3,pixels,((t.Box.Top+y)*1280+t.Box.Left)*3,t.Image.Width*3);
        }
        using var recognizer=new ScreenPageRecognizer(profile);
        Assert.Equal(expected,recognizer.Recognize(new RgbImage(1280,960,pixels)).Page);
    }
    [Fact] public void DismountMustNotMatchTheObservedOnFootMountButton()
    {
        var profile=ScreenRoutes.Load(Root()).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/screen_ui/mount-button-on-foot.png"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var width=rgb.Width;var height=rgb.Height;var crop=new byte[width*height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,crop,0,crop.Length);
        var pixels=new byte[1280*960*3];for(var y=0;y<height;y++)Array.Copy(crop,y*width*3,pixels,((779+y)*1280+1098)*3,width*3);
        using var matcher=new TemplateMatcher(profile);
        Assert.Null(matcher.Anchor(new(1280,960,pixels),"maint_dismount"));
        var template=profile.Templates["maint_dismount"];var reference=template.Image.CopyPixels();var positive=new byte[1280*960*3];
        for(var y=0;y<template.Image.Height;y++)Array.Copy(reference,y*template.Image.Width*3,positive,((template.Box.Top+y)*1280+template.Box.Left)*3,template.Image.Width*3);
        Assert.NotNull(matcher.Anchor(new(1280,960,positive),"maint_dismount"));
        var legacy=(System.Text.Json.Nodes.JsonObject)profile.Definition.DeepClone();legacy["templates"]!["maint_dismount"]!["match_mode"]="light_text";
        using var old=new TemplateMatcher(NavigationProfile.FromDefinition(Root(),legacy));Assert.NotNull(old.Anchor(new(1280,960,pixels),"maint_dismount"));
    }
    [Fact]
    public void RepairEquipmentFixedControlsDoNotRequireAnimatedRepairButton()
    {
        var profile=ScreenRoutes.Load(Root()).Profile;var pixels=new byte[1280*960*3];
        foreach(var name in new[]{"maint_repair_title","maint_repair_items","maint_tools_no_repair"})
        {
            var template=profile.Templates[name];var source=template.Image.CopyPixels();
            for(var y=0;y<template.Image.Height;y++)Array.Copy(source,y*template.Image.Width*3,pixels,((template.Box.Top+y)*1280+template.Box.Left)*3,template.Image.Width*3);
        }
        using var recognizer=new ScreenPageRecognizer(profile);var view=recognizer.Recognize(new RgbImage(1280,960,pixels));
        Assert.Equal("repair_equipment",view.Page);Assert.Contains("repair.close",view.Targets.Keys);
        Assert.Equal(new PixelPoint(525,912),view.Targets["repair.prepare"]);
        Assert.DoesNotContain(view.Hits,h=>h.Name=="maint_repair_all");
        Assert.Equal(2,view.StableBoxes.Count);
    }
    [Fact]
    public void PortraitCoreAcceptsObservedSubpixelShiftWithoutLoweringThreshold()
    {
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/screen_ui/ferghus-portrait-subpixel.png"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var width=rgb.Width;var height=rgb.Height;var crop=new byte[width*height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,crop,0,crop.Length);
        var pixels=new byte[1280*960*3];for(var y=0;y<height;y++)Array.Copy(crop,y*width*3,pixels,((200+y)*1280+400)*3,width*3);
        using var matcher=new TemplateMatcher(ScreenRoutes.Load(Root()).Profile);
        Assert.NotNull(matcher.Anchor(new(1280,960,pixels),"maint_ferghus_portrait"));
    }
    [Fact]
    public void PortraitUsesStableLowerFaceWhenPlayerMarkerCoversForehead()
    {
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/screen_ui/ferghus-portrait-player-marker.png"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var crop=new byte[64*64*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,crop,0,crop.Length);
        var pixels=new byte[1280*960*3];for(var y=0;y<64;y++)Array.Copy(crop,y*64*3,pixels,((200+y)*1280+400)*3,64*3);
        var profile=ScreenRoutes.Load(Root()).Profile;using var matcher=new TemplateMatcher(profile);
        var hit=matcher.Anchor(new(1280,960,pixels),"maint_ferghus_portrait");
        Assert.NotNull(hit);Assert.True(hit.Score>=.97);Assert.Equal(new PixelBox(416,216,447,247),hit.Box);
        Assert.Null(matcher.Anchor(new(1280,960,pixels),"purchase.map_portrait"));
    }
    [Theory][InlineData(false,"character_menu")][InlineData(true,"character_life_skills")]
    public void LifeSkillsHeaderDisambiguatesSharedCharacterSidebar(bool life,string expected)
    {
        var routes=ScreenRoutes.Load(Root());var pixels=new byte[1280*960*3];
        foreach(var name in life?new[]{"character_combat_label","preset_life_header"}:new[]{"character_combat_label"})
        {
            var t=routes.Profile.Templates[name];var source=t.Image.CopyPixels();
            for(var y=0;y<t.Image.Height;y++)Array.Copy(source,y*t.Image.Width*3,pixels,((t.Box.Top+y)*1280+t.Box.Left)*3,t.Image.Width*3);
        }
        using var recognizer=new ScreenPageRecognizer(routes.Profile);Assert.Equal(expected,recognizer.Recognize(new RgbImage(1280,960,pixels)).Page);
    }
    [Fact]
    public void RepairGoldCoinAcceptsCurrentRasterizationButRejectsSilver()
    {
        using var source=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/screen_ui/repair-compact-quote.png"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(source,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var width=rgb.Width;var height=rgb.Height;var crop=new byte[width*height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,crop,0,crop.Length);
        RgbImage Frame(bool silver)
        {
            var pixels=new byte[1280*960*3];
            for(var y=0;y<height;y++)for(var x=0;x<width;x++)
            {
                var s=(y*width+x)*3;var d=((884+y)*1280+566+x)*3;
                for(var channel=0;channel<3;channel++)pixels[d+channel]=silver?(byte)((crop[s]+crop[s+1]+crop[s+2])/3):crop[s+channel];
            }
            return new(1280,960,pixels);
        }
        using var matcher=new TemplateMatcher(ScreenRoutes.Load(Root()).Profile);
        var hit=matcher.Match(Frame(false),"maint_repair_gold");Assert.NotNull(hit);Assert.True(hit.Score>=.97);
        Assert.Null(matcher.Match(Frame(true),"maint_repair_gold"));
    }
    [Theory]
    [InlineData("maint_ferghus_portrait",0)]
    [InlineData("purchase.map_portrait",64)]
    public void MapPortraitCoreToleratesRasterizationWithoutAcceptingAnotherNpc(string name,int sourceX)
    {
        // Two 64px crops from the current 1280x960 map; excludes character/account information.
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(Root(),"tests/fixtures/screen_ui/map-portrait-cores.png"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var crops=new byte[128*64*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,crops,0,crops.Length);
        var profile=ScreenRoutes.Load(Root()).Profile;using var matcher=new TemplateMatcher(profile);
        RgbImage Frame(int x)
        {
            var pixels=new byte[1280*960*3];for(var row=0;row<64;row++)Array.Copy(crops,(row*128+x)*3,pixels,((200+row)*1280+400)*3,64*3);
            return new(1280,960,pixels);
        }
        var hit=matcher.Anchor(Frame(sourceX),name);Assert.NotNull(hit);Assert.True(hit.Score>=.97);
        Assert.InRange(hit.Box.Center.X,400,464);Assert.InRange(hit.Box.Center.Y,200,264);
        Assert.Null(matcher.Anchor(Frame(sourceX==0?64:0),name));
        var definition=(System.Text.Json.Nodes.JsonObject)profile.Definition.DeepClone();definition["templates"]![name]!["match_mode"]="grayscale";
        definition["anchor_variants"]?.AsObject().Remove(name);
        using var unfiltered=new TemplateMatcher(NavigationProfile.FromDefinition(Root(),definition));Assert.Null(unfiltered.Anchor(Frame(sourceX),name));
    }
    [Fact] public void OriginalProfileLoadsVerifiedAssetsByReference()
    {
        var profile=NavigationProfile.Load(Root(),"config/navigation_ui.1280x960.tw.json");using var matcher=new TemplateMatcher(profile);
        var template=profile.Templates["scene_bag"];Assert.True(template.Threshold>=.97);
        var bytes=new byte[1280*960*3];var pixels=template.Image.CopyPixels();
        for(var row=0;row<template.Image.Height;row++)Array.Copy(pixels,row*template.Image.Width*3,bytes,((template.Box.Top+row)*1280+template.Box.Left)*3,template.Image.Width*3);
        var hit=matcher.Match(new RgbImage(1280,960,bytes),"scene_bag");Assert.NotNull(hit);Assert.True(hit.Score>.999);Assert.Equal(template.Box,hit.Box);
    }
    [Fact] public void CatalogMergesAllCurrentRoutesWithoutDuplicatingAssets()
    {
        var routes=ScreenRoutes.Load(Root());Assert.True(routes.Recipes.Count>10);Assert.True(routes.Devices.Count>2);Assert.True(routes.Profile.Templates.Count>100);Assert.All(routes.Recipes.Values,r=>Assert.True(routes.Devices.ContainsKey(r.Device)));
        Assert.DoesNotContain(routes.Profile.Templates.Keys,key=>key.StartsWith("bag.label:",StringComparison.Ordinal));
    }
    [Fact] public void ThresholdDisagreementLeavesQuantityUnknown()
    {
        // A mid-gray stroke produces different masks at 165 and 210; identical
        // masks intentionally share one OCR result within the same frame.
        var pixels=new byte[192];pixels[27]=pixels[28]=pixels[29]=190;
        var reader=new ScreenQuantityReader(new SequenceOcr([new("2",.99),new("3",.99)]));var read=reader.Read(new RgbImage(8,8,pixels),new(10,"bag",new(0,0,8,8)));Assert.Null(read.Value);Assert.Equal(2,read.Readings.Count);
    }
    [Fact] public void CurrentRecognitionDoesNotRequireHistoricalPair()
    {
        var scope=new SessionScope("s","a","c");var view=new PageView("scene",false,[],new Dictionary<string,PixelPoint>{{"scene.open_bag",new(4,4)}},[]);var observer=new ScreenObserver(_=>scope,_=>view);
        var native=new NativeWindowState(1,2,3,"game.exe","UnityWndClass",new(0,0,8,8,96),new(0,0,8,8),1,true,false,true,new(0,0));var capture=new ClientCapture(native,new(8,8,new byte[192]),1);
        Assert.True(observer.Observe(capture).Stable);Assert.True(observer.Observe(capture).Stable);Assert.True(observer.Observe(capture with{CapturedAt=1.1}).Stable);
    }
    [Fact] public void DuplicateCalibratedTargetsAreAmbiguous()
    {
        var profile=NavigationProfile.Load(Root(),"config/navigation_ui.1280x960.tw.json");var definition=(System.Text.Json.Nodes.JsonObject)profile.Definition.DeepClone();definition["templates"]!["scene_bag"]!["search"]=new System.Text.Json.Nodes.JsonArray(0,0,1280,960);var widened=NavigationProfile.FromDefinition(Root(),definition);using var matcher=new TemplateMatcher(widened);var template=widened.Templates["scene_bag"];var bytes=new byte[1280*960*3];var pixels=template.Image.CopyPixels();foreach(var x in new[]{100,300})for(var row=0;row<template.Image.Height;row++)Array.Copy(pixels,row*template.Image.Width*3,bytes,((100+row)*1280+x)*3,template.Image.Width*3);var image=new RgbImage(1280,960,bytes);Assert.Null(matcher.Match(image,"scene_bag"));Assert.NotNull(matcher.Match(image,"scene_bag",unique:false));
    }
    public static string Root(){var path=new DirectoryInfo(AppContext.BaseDirectory);while(path is not null&&!File.Exists(Path.Combine(path.FullName,"config","navigation_ui.1280x960.tw.json")))path=path.Parent;return path?.FullName??throw new DirectoryNotFoundException();}
    sealed class SequenceOcr(Queue<OcrLine> lines):IOcrEngine{public SequenceOcr(OcrLine[] lines):this(new Queue<OcrLine>(lines)){}public OcrLine? Recognize(int width,int height,byte[] pixels)=>lines.Dequeue();}
}
