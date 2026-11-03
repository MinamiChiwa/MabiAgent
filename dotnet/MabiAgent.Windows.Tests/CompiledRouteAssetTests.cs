using System.Text.Json.Nodes;
using MabiAgent.Windows;
using Xunit;
public sealed class CompiledRouteAssetTests
{
    [Fact] public void InMemoryCompiledTemplateAcceptsInt64FileLengthAndRejectsMismatch()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var definition=new MabiAgent.Core.CatalogLoader(root).Load("config/navigation_ui.1280x960.tw.json");
        var templates=definition["templates"]!.AsObject();var first=templates.First();
        definition["templates"]=new JsonObject{[first.Key]=first.Value!.DeepClone()};
        var spec=definition["templates"]![first.Key]!;
        spec["bytes"]=new FileInfo(Path.Combine(root,spec["path"]!.GetValue<string>())).Length;
        Assert.Single(NavigationProfile.FromDefinition(root,definition).Templates);
        spec["bytes"]=spec["bytes"]!.GetValue<long>()+1;
        Assert.Throws<InvalidDataException>(()=>NavigationProfile.FromDefinition(root,definition));
    }
    [Fact] public void TemplateResolverRejectsUserAssetTraversalAndNonManifestFiles()
    {
        var root=Path.Combine(Path.GetTempPath(),"route-root");var assets=Path.Combine(root,"state/task-chains/assets");
        Assert.Throws<InvalidDataException>(()=>NavigationProfile.ResolveTemplatePath(root,Path.Combine(assets,"../outside.png"),assets));
        Assert.Throws<InvalidDataException>(()=>NavigationProfile.ResolveTemplatePath(root,Path.Combine(assets,"image_valid/payload.exe"),assets));
        Assert.Equal(Path.GetFullPath(Path.Combine(assets,"image_valid/original.bin")),NavigationProfile.ResolveTemplatePath(root,Path.Combine(assets,"image_valid/original.bin"),assets));
    }
    [Fact] public void FromConfigurationPreservesOverlaySettingsAndLoadsCompiledUserTemplate()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));var baseConfig=new MabiAgent.Core.CatalogLoader(root).Load("config/screen_catalog.1280x960.tw.json");
        var routes=ScreenRoutes.FromConfiguration(root,baseConfig);
        Assert.Contains("overlay.production_complete",routes.Profile.Templates.Keys);
        Assert.NotNull(routes.Configuration["travel_wings"]);Assert.NotNull(routes.Configuration["relogin"]);Assert.NotNull(routes.Configuration["notifications"]);
    }
}

