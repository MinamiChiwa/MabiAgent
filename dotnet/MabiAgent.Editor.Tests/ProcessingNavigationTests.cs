using System.Text.Json.Nodes;
using MabiAgent.Core;
using MabiAgent.Desktop.Editors;
using MabiAgent.Windows;
using Xunit;

public sealed class ProcessingNavigationTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void PublishedFishingNamesKeepLeadingGlyphMatchingAndVisibleRows(bool video)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"unused-fishing-names"));
        var profile=ScreenRoutes.FromConfiguration(root,PublishedCatalogCompiler.CompileAvailable(repository),repository.UserAssetRoot).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production",video?"fishing-nodes-video-current.jpg":"east-wall-fishing-list-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var recognizer=new ScreenPageRecognizer(profile);var view=recognizer.Recognize(new RgbImage(rgb.Width,rgb.Height,pixels));
        Assert.Equal("fishing_nodes",view.Page);
        for(var row=0;row<(video?4:6);row++)
        {
            var name=$"preset_catalog_point_fishing_{row:00}_node";Assert.Equal("list_name",profile.Templates[name].Mode);
            Assert.Equal(.93,profile.Templates[name].GlyphThreshold);var top=(video?610:393)+96*row;
            Assert.InRange(view.Targets[$"gathering.open_point_fishing_{row:00}"].Y,top,top+34);
        }
    }
    [Fact] public void EditedGatheringLabelKeepsItsRecognitionCalibrationWhenPublished()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"unused-gathering-label"));
        var template=repository.Templates.First(t=>t["kind"]!.GetValue<string>()=="gathering"&&t["parameters"]!["node"]!.GetValue<string>()=="羊"&&t["parameters"]!["target_item_name"]!.GetValue<string>()=="羊毛");
        var draft=repository.CreateDraft(template["template_key"]!.GetValue<string>());
        var name="preset_catalog_point_wool_00_node";
        var variants=draft["nodes"]!.AsArray().SelectMany(n=>n!["anchors"]!.AsArray()).Where(a=>a!["key"]!.GetValue<string>()==name).SelectMany(a=>a!["variants"]!.AsArray()).ToArray();
        Assert.NotEmpty(variants);
        foreach(var variant in variants)
        {
            Assert.Equal("list_label",variant!["match_mode"]!.GetValue<string>());Assert.True(variant["relative_contrast"]!.GetValue<bool>());
            Assert.Equal(.97,variant["glyph_threshold"]!.GetValue<double>());variant["relative_contrast_threshold"]=41;
        }
        draft["status"]="published";
        var routes=ScreenRoutes.FromConfiguration(root,PublishedCatalogCompiler.Compile(repository,[draft],includeWorkbench:false),repository.UserAssetRoot);
        var compiled=routes.Profile.Templates.Values.Where(t=>t.Name.EndsWith("__"+name,StringComparison.Ordinal)).ToArray();
        var label=Assert.Single(compiled);Assert.Equal("list_label",label.Mode);Assert.True(label.RelativeContrast);Assert.Equal(41,label.RelativeContrastThreshold);Assert.Equal(.97,label.GlyphThreshold);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/wool-nodes-current.png"));using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var matcher=new TemplateMatcher(routes.Profile);Assert.NotNull(matcher.Anchor(new(rgb.Width,rgb.Height,pixels),label.Name));
    }
    [Fact] public void SharedMapRouteRequiresTheNamedShopBeforeMaximumZoom()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var routes=ScreenRoutes.Load(root);var stages=routes.Configuration["maintenance"]!["map_navigation"]!["stages"]!.AsObject();
        Assert.False(routes.Profile.Templates.ContainsKey("maint_map_recenter"));
        Assert.Null(routes.Profile.Definition["pages"]!["region_map"]!["targets"]?["map.recenter"]);
        foreach(var (stage,definition) in stages)
        foreach(var step in new[]{"choices","search"}.SelectMany(k=>definition?[k]?.AsArray()??new JsonArray()))
        {
            Assert.NotEqual("map.recenter",step?["target"]?.GetValue<string>());
            if(step?["kind"]?.GetValue<string>()=="zoom_max")Assert.Equal("zoom",stage);
            if(step?["next"]?.GetValue<string>()!="zoom")continue;
            Assert.Equal("$shop",step["target"]!.GetValue<string>());Assert.Equal("drag",step["kind"]!.GetValue<string>());
            Assert.Equal(new[]{"map.current_is_tir"},step["requires"]!.AsArray().Select(n=>n!.GetValue<string>()));
            Assert.Equal(new[]{625,450},step["destination"]!.AsArray().Select(n=>n!.GetValue<int>()));
        }
    }
    [Theory][InlineData("tir-inn-wide-current.png")][InlineData("tir-inn-player-covered-current.png")]
    public void PublishedMapTitleSeparatesWhiteHeaderFromGrayMapLabels(string file)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"mabi-map-title-"+Guid.NewGuid().ToString("N")));
        var routes=ScreenRoutes.FromConfiguration(root,PublishedCatalogCompiler.CompileAvailable(repository),repository.UserAssetRoot);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production",file));using var rgb=new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var recognizer=new ScreenPageRecognizer(routes.Profile);var view=recognizer.Recognize(new RgbImage(rgb.Width,rgb.Height,pixels));
        Assert.Equal("region_map",view.Page);Assert.Contains("map.current_is_tir",view.Targets.Keys);
    }
    [Fact] public void PublishedRuntimePreservesSharedMapGeometryAndSearch()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"mabi-map-"+Guid.NewGuid().ToString("N")));
        var direct=ScreenRoutes.Load(root);
        var published=ScreenRoutes.FromConfiguration(root,PublishedCatalogCompiler.CompileAvailable(repository),repository.UserAssetRoot);
        foreach(var section in new[]{"pages","page_variants"})
        foreach(var (key,page) in direct.Profile.Definition[section]!.AsObject().Where(p=>p.Key=="region_map"||p.Value?["page"]?.GetValue<string>()=="region_map"))
            Assert.True(JsonNode.DeepEquals(page,published.Profile.Definition[section]?[key]),section+":"+key+"\nexpected="+page+"\nactual="+published.Profile.Definition[section]?[key]);
        Assert.True(JsonNode.DeepEquals(direct.Configuration["maintenance"]!["map_navigation"],published.Configuration["maintenance"]!["map_navigation"]));
        foreach(var destination in new[]{"storage","repair_quote","purchase_shop"})
        {
            Assert.True(JsonNode.DeepEquals(direct.Configuration["maintenance"]!["navigation"]![destination]!["region_map"],published.Configuration["maintenance"]!["navigation"]![destination]!["region_map"]),destination);
            var route=published.Configuration["maintenance"]!["navigation"]![destination]!["region_map"]!;
            var zoom=route["stages"]!["zoom"]!["search"]![0]!;
            Assert.Equal(4,zoom["steps"]!.GetValue<int>());Assert.Equal(9,zoom["max_sweeps"]!.GetValue<int>());
            Assert.DoesNotContain("$",zoom["center_target"]!.GetValue<string>());
            Assert.Equal("recenter",zoom["recovery_stage"]!.GetValue<string>());
            var recovery=route["stages"]!["portrait"]!["search"]![0]!;
            Assert.True(recovery["zoom"]!.GetValue<bool>());Assert.True(recovery["map_recovery"]!.GetValue<bool>());Assert.Equal(-4,recovery["steps"]!.GetValue<int>());
        }
    }
    [Theory]
    [InlineData("production/daily-nodes-current.jpg","preset_daily_title")]
    [InlineData("production/ore-task-current.jpg","gathering_observer.ore_objective")]
    [InlineData("production/hard-log-task-current.jpg","gathering_observer.hard_log_objective")]
    [InlineData("production/high-log-task-current.jpg","gathering_observer.high_log_objective")]
    public void PublishedRuntimePreservesCurrentStableWaitSources(string file,string anchor)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"mabi-current-waits-"+Guid.NewGuid().ToString("N")));
        var routes=ScreenRoutes.FromConfiguration(root,PublishedCatalogCompiler.CompileAvailable(repository),repository.UserAssetRoot);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures",file));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var matcher=new TemplateMatcher(routes.Profile);Assert.NotNull(matcher.Anchor(new(rgb.Width,rgb.Height,pixels),anchor));
        Assert.Contains(routes.Configuration["quick_reentry"]!["steps"]!.AsArray(),s=>s!["id"]!.GetValue<string>()=="appearance");
    }
    [Fact] public void PublishedFabricFacilityRecognizesArrivedPanel()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"mabi-fabric-"+Guid.NewGuid().ToString("N")));
        var profile=ScreenRoutes.FromConfiguration(root,PublishedCatalogCompiler.CompileAvailable(repository),repository.UserAssetRoot).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/fabric-arrived-current.png"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var recognizer=new ScreenPageRecognizer(profile);var view=recognizer.Recognize(new RgbImage(rgb.Width,rgb.Height,pixels));
        Assert.Equal("fabric_panel",view.Page);Assert.Contains("fabric.open_fabric_first",view.Targets.Keys);
        foreach(var name in new[]{"preset_fabric_title","preset_fabric_equipment"})
        {Assert.Equal("light_text",profile.Templates[name].Mode);Assert.Equal(.97,profile.Templates[name].GlyphThreshold);}
    }
    [Fact] public void PublishedChemicalFacilityRecognizesCurrentPanel()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"mabi-chemical-"+Guid.NewGuid().ToString("N")));
        var profile=ScreenRoutes.FromConfiguration(root,PublishedCatalogCompiler.CompileAvailable(repository),repository.UserAssetRoot).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/chemical-panel-current.jpg"));using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        using var matcher=new TemplateMatcher(profile);using var recognizer=new ScreenPageRecognizer(profile,matcher);var view=recognizer.Recognize(frame);
        Assert.True(view.Page=="chemical_panel","page="+view.Page+"; matched="+string.Join(",",profile.Definition["pages"]!.AsObject().Concat(profile.Definition["page_variants"]!.AsObject()).Where(p=>p.Value?["anchors"] is JsonArray a&&a.Count>0&&a.All(n=>matcher.Anchor(frame,n!.GetValue<string>()) is not null)).Select(p=>p.Key)));
        Assert.Contains("chemical.open_catalog_chemical_14",view.Targets.Keys);
    }
    [Fact] public void PublishedSoftwoodDetailRecognizesCurrentFrame()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"mabi-chemical-"+Guid.NewGuid().ToString("N")));
        var profile=ScreenRoutes.FromConfiguration(root,PublishedCatalogCompiler.CompileAvailable(repository),repository.UserAssetRoot).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/softwood-detail-current.jpg"));using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        using var matcher=new TemplateMatcher(profile);using var recognizer=new ScreenPageRecognizer(profile,matcher);var view=recognizer.Recognize(frame);
        Assert.True(view.Page=="recipe_detail","page="+view.Page+"; anchors="+string.Join(";",repository.Catalog["recipes"]!["catalog_wood_04"]!["confirm_anchors"]!.AsArray().Select(n=>n+"="+matcher.Anchor(frame,n!.GetValue<string>())?.Score))+"; matched="+string.Join(",",profile.Definition["pages"]!.AsObject().Concat(profile.Definition["page_variants"]!.AsObject()).Where(p=>p.Value?["anchors"] is JsonArray a&&a.Count>0&&a.All(n=>matcher.Anchor(frame,n!.GetValue<string>()) is not null)).Select(p=>p.Key)));
        
    }
    [Theory][InlineData("storage-bank-menu-current.jpg")][InlineData("storage-bank-menu-published-current.jpg")][InlineData("storage-open-current.jpg")]
    public void PublishedRuntimeRecognizesBothStorageMenus(string file)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"mabi-storage-menu-"+Guid.NewGuid().ToString("N")));
        var profile=ScreenRoutes.FromConfiguration(root,PublishedCatalogCompiler.CompileAvailable(repository),repository.UserAssetRoot).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production",file));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var recognizer=new ScreenPageRecognizer(profile);var view=recognizer.Recognize(new RgbImage(rgb.Width,rgb.Height,pixels));
        Assert.Equal("storage_dialog",view.Page);Assert.Contains("storage.open",view.Targets.Keys);
    }
    [Theory][InlineData("device-travel-confirm.jpg")][InlineData("device-travel-wood-confirm.jpg")]
    public void PublishedRuntimeRecognizesCrossRegionDeviceTravelConfirmation(string file)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"mabi-device-confirm-"+Guid.NewGuid().ToString("N")));
        var profile=ScreenRoutes.FromConfiguration(root,PublishedCatalogCompiler.CompileAvailable(repository),repository.UserAssetRoot).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production",file));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var matcher=new TemplateMatcher(profile);using var recognizer=new ScreenPageRecognizer(profile,matcher);
        var view=recognizer.Recognize(new RgbImage(rgb.Width,rgb.Height,pixels));
        Assert.Equal("device_travel_confirm",view.Page);
        Assert.Contains("device.confirm_travel",view.Targets.Keys);
        Assert.Contains("device.cancel_travel",view.Targets.Keys);
    }
    [Fact] public void CurrentWoodDeviceHasFiveEmptySlotsAndNoReadyOutput()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));var routes=ScreenRoutes.Load(root);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/wood-empty-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var frame=new RgbImage(rgb.Width,rgb.Height,pixels);var slots=routes.Devices["wood"].Slots;
        Assert.Equal(new[]{0,1,2,3,4},SlotRecognition.EmptyIndices(frame,slots));Assert.Empty(SlotRecognition.ReadyIndices(frame,slots)!);
    }
    [Fact] public void PublishedRecipeRecognitionSurvivesAnOccupiedDeviceSlot()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"mabi-busy-recipe-"+Guid.NewGuid().ToString("N")));
        var profile=ScreenRoutes.FromConfiguration(root,PublishedCatalogCompiler.CompileAvailable(repository),repository.UserAssetRoot).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/iron-detail-running-slot.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        using var matcher=new TemplateMatcher(profile);using var recognizer=new ScreenPageRecognizer(profile,matcher);var view=recognizer.Recognize(frame);
        var matched=profile.Definition["pages"]!.AsObject().Concat(profile.Definition["page_variants"]!.AsObject()).Where(p=>p.Value?["anchors"] is JsonArray a&&a.Count>0&&a.All(n=>matcher.Anchor(frame,n!.GetValue<string>()) is not null)).Select(p=>p.Key);
        Assert.True(view.Page=="recipe_detail","page="+view.Page+"; matched="+string.Join(",",matched));
    }
    [Fact] public void PublishedRuntimeKeepsRetiredPageDefinitionsRemoved()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"mabi-retired-pages-"+Guid.NewGuid().ToString("N")));
        var compiled=PublishedCatalogCompiler.CompileAvailable(repository);var profile=ScreenRoutes.FromConfiguration(root,compiled,repository.UserAssetRoot).Profile;
        foreach(var(section,names)in repository.Catalog["ui_remove"]!.AsObject())foreach(var name in names!.AsArray())
            Assert.Null(profile.Definition[section]?[name!.GetValue<string>()]);
    }
    [Fact] public void PublishedRuntimeRecognizesProcessingCategories()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"mabi-category-"+Guid.NewGuid().ToString("N")));
        var compiled=PublishedCatalogCompiler.CompileAvailable(repository);
        var profile=ScreenRoutes.FromConfiguration(root,compiled,repository.UserAssetRoot).Profile;
        var path=Environment.GetEnvironmentVariable("MABI_CATEGORY_CURRENT")??Path.Combine(root,"tests/fixtures/category-skills-overlap.webp");
        using var bgr=OpenCvSharp.Cv2.ImRead(path);using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        using var matcher=new TemplateMatcher(profile);using var recognizer=new ScreenPageRecognizer(profile,matcher);var view=recognizer.Recognize(frame);
        Assert.True(view.Page=="category_page","page="+view.Page+"; "+string.Join("; ",profile.Definition["pages"]!.AsObject().Concat(profile.Definition["page_variants"]!.AsObject()).Where(p=>p.Key.Contains("categor")||p.Key=="skills").Select(p=>p.Key+":"+p.Value!["anchors"]+"; hits="+string.Join(",",p.Value["anchors"]!.AsArray().Select(n=>n+"="+matcher.Anchor(frame,n!.GetValue<string>())?.Score)))));
        Assert.Contains("category.open_metal",view.Targets.Keys);
    }
}
