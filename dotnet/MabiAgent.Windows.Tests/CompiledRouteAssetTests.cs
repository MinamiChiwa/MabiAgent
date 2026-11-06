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
    [Fact] public void OversizeTemplateIsRejectedByLengthBeforeOpeningForRead()
    {
        var source=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));var root=Path.Combine(Path.GetTempPath(),"mabi-oversize-template-"+Guid.NewGuid().ToString("N"));
        try
        {
            var directory=Path.Combine(root,"assets/templates");Directory.CreateDirectory(directory);var definition=new MabiAgent.Core.CatalogLoader(source).Load("config/navigation_ui.1280x960.tw.json");
            var first=definition["templates"]!.AsObject().First();definition["templates"]=new JsonObject{[first.Key]=first.Value!.DeepClone()};definition["templates"]![first.Key]!["path"]="assets/templates/oversize.png";
            using var stream=new FileStream(Path.Combine(directory,"oversize.png"),FileMode.CreateNew,FileAccess.Write,FileShare.None);stream.SetLength(8*1024*1024+1L);
            Assert.Throws<InvalidDataException>(()=>NavigationProfile.FromDefinition(root,definition));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact] public void OversizeRuntimeLimitTemplateIsRejectedBeforeOpeningForRead()
    {
        var root=Path.Combine(Path.GetTempPath(),"mabi-oversize-runtime-"+Guid.NewGuid().ToString("N"));
        try
        {
            var directory=Path.Combine(root,"assets/templates/session_interruptions");Directory.CreateDirectory(directory);
            using var stream=new FileStream(Path.Combine(directory,"session_expired_message.png"),FileMode.CreateNew,FileAccess.Write,FileShare.None);stream.SetLength(65537);
            Assert.Throws<InvalidDataException>(()=>new RuntimeLimitRecognition(root));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact] public void FromConfigurationPreservesOverlaySettingsAndLoadsCompiledUserTemplate()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));var baseConfig=new MabiAgent.Core.CatalogLoader(root).Load("config/screen_catalog.1280x960.tw.json");
        var routes=ScreenRoutes.FromConfiguration(root,baseConfig);
        Assert.Contains("overlay.production_complete",routes.Profile.Templates.Keys);
        Assert.NotNull(routes.Configuration["travel_wings"]);Assert.NotNull(routes.Configuration["relogin"]);Assert.NotNull(routes.Configuration["notifications"]);
    }
}

