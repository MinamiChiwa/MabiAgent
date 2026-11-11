using System.Text.Json.Nodes;
using MabiAgent.Desktop.Editors;
using MabiAgent.Windows;
using OpenCvSharp;
using Xunit;
namespace MabiAgent.Desktop.Tests;
public sealed class ProductionConfigurationTests
{
    [Fact]
    public void CompiledWoodRecipeRetainsCurrentIdentityAnchors()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var repository=new EditorRepository(root,Path.Combine(Path.GetTempPath(),"mabi-recipe-config-"+Guid.NewGuid().ToString("N")));
        var config=PublishedCatalogCompiler.CompileAvailable(repository);var routes=ScreenRoutes.FromConfiguration(root,config,repository.UserAssetRoot);
        using var bgr=Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/wood-recipe-current.png"));using var rgb=new Mat();Cv2.CvtColor(bgr,rgb,ColorConversionCodes.BGR2RGB);var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var image=new RgbImage(rgb.Width,rgb.Height,pixels);
        using var matcher=new TemplateMatcher(routes.Profile);using var recognizer=new ScreenPageRecognizer(routes.Profile,matcher);
        var hits=routes.Recipes["wood_first"].ConfirmAnchors.Select(name=>new{name,hit=matcher.Anchor(image,name),spec=routes.Profile.Definition["templates"]![name]}).ToArray();
        Assert.All(hits,row=>Assert.NotNull(row.hit));Assert.Equal("recipe_detail",recognizer.Recognize(image).Page);
    }
}
