using System.Text.Json.Nodes;
using OpenCvSharp;
using Xunit;

namespace MabiAgent.Windows.Tests;

public sealed class WoodTitleCalibrationTests
{
    [Theory]
    [InlineData("screen_routes.1280x960.tw.json")]
    [InlineData("screen_catalog.1280x960.tw.json")]
    public void CurrentWoodGlyphPlacementIsRecognizedFromEachConfiguration(string configuration)
    {
        var root = RecognitionTests.Root();
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config", "screen_catalog.1280x960.tw.json")))!;
        var templates = catalog["ui"]!["templates"]!;
        var pixels = new byte[1280 * 960 * 3];
        foreach (var name in new[] { "preset_wood_title", "preset_wood_equipment" })
        {
            var spec = templates[name]!;
            using var bgr = Cv2.ImRead(Path.Combine(root, spec["path"]!.GetValue<string>()));
            using var rgb = new Mat();
            Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);
            var width = rgb.Width;
            var height = rgb.Height;
            var source = new byte[width * height * 3];
            System.Runtime.InteropServices.Marshal.Copy(rgb.Data, source, 0, source.Length);
            var box = spec["box"]!.AsArray();
            var left = box[0]!.GetValue<int>();
            var top = box[1]!.GetValue<int>();
            for (var y = 0; y < height; y++) Array.Copy(source, y * width * 3, pixels, ((top + y) * 1280 + left) * 3, width * 3);
        }
        var routes = ScreenRoutes.Load(root, Path.Combine("config", configuration));
        using var recognizer = new ScreenPageRecognizer(routes.Profile);
        Assert.Equal("wood_panel", recognizer.Recognize(new RgbImage(1280, 960, pixels)).Page);
    }
}
