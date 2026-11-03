using System.Text.Json.Nodes;

using Avalonia.Media.Imaging;
using MabiAgent.Windows;
namespace MabiAgent.Desktop.Editors;
public sealed record AnchorTestResult(bool Matched,double? Score,string Message,PixelBox? Bounds=null);
public interface IAnchorTestService { AnchorTestResult Test(AnchorEntry entry,JsonObject spec,Bitmap capture); }
public sealed class NativeAnchorTestService(EditorRepository repository):IAnchorTestService
{
    public AnchorTestResult Test(AnchorEntry entry,JsonObject spec,Bitmap capture)
    {
        if(entry.Id.StartsWith("reading:",StringComparison.Ordinal))return new(false,null,"数量读取请使用运行器的双帧核账，不把单图读数当库存。");
        if(spec["box"] is null&&spec["crop_native"] is null)return new(false,null,"此内联图片需先配置原生区域和搜索 ROI。");
        var current=(JsonObject)spec.DeepClone();current["box"]??=current["crop_native"]!.DeepClone();current["search"]??=current["box"]!.DeepClone();if(EditorRepository.Text(current,"match_mode")=="gold_text")current["match_mode"]="yellow_text";
        var definition=new JsonObject{["client_size"]=repository.Catalog["client_size"]!.DeepClone(),["dpi"]=repository.Catalog["dpi"]!.DeepClone(),["templates"]=new JsonObject{["test"]=current},["pages"]=new JsonObject()};
        var profile=NavigationProfile.FromDefinition(repository.Root,definition,repository.UserAssetRoot);var data=ImagePixels.Rgb(capture);using var matcher=new TemplateMatcher(profile);var hit=matcher.Match(new RgbImage(capture.PixelSize.Width,capture.PixelSize.Height,data),"test");return hit is null?new(false,null,"当前画面未达到运行识别标准或匹配不唯一，请调整范围或阈值。"):new(true,hit.Score,$"匹配 {hit.Score:P1} · 位置 {hit.Box.Left}, {hit.Box.Top}",hit.Box);
    }
}
