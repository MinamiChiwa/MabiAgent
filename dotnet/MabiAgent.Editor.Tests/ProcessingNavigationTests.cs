using System.Text.Json.Nodes;
using MabiAgent.Core;
using MabiAgent.Desktop.Editors;
using MabiAgent.Windows;
using Xunit;

public sealed class ProcessingNavigationTests
{
    [Theory]
    [InlineData("production/daily-nodes-current.jpg","preset_daily_title")]
    [InlineData("private-theater/create-current.jpg","private_theater_create_title")]
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
        Assert.Contains(routes.Configuration["private_theater_refresh"]!["steps"]!.AsArray(),s=>s!["id"]!.GetValue<string>()=="create_ready");
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
    [Fact] public void PublishedRuntimeRecognizesCrossRegionDeviceTravelConfirmation()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"mabi-device-confirm-"+Guid.NewGuid().ToString("N")));
        var profile=ScreenRoutes.FromConfiguration(root,PublishedCatalogCompiler.CompileAvailable(repository),repository.UserAssetRoot).Profile;
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/device-travel-confirm.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        using var matcher=new TemplateMatcher(profile);using var recognizer=new ScreenPageRecognizer(profile,matcher);
        var view=recognizer.Recognize(new RgbImage(rgb.Width,rgb.Height,pixels));
        Assert.Equal("device_travel_confirm",view.Page);
        Assert.Contains("device.confirm_travel",view.Targets.Keys);
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
