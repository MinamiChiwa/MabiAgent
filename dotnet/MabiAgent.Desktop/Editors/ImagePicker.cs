using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
namespace MabiAgent.Desktop.Editors;
public static class EditorImagePicker
{
    public static async Task<string?> OpenImage(TopLevel? owner)
    {if(owner is null)return null;var files=await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title="选择截图或名称图片",AllowMultiple=false,FileTypeFilter=[FilePickerFileTypes.ImageAll]});return files.FirstOrDefault()?.TryGetLocalPath();}
    public static async Task<(System.Text.Json.Nodes.JsonObject Asset,int[] Box,Bitmap Preview)?> Pick(Window? owner,EditorRepository repository,IEditorCaptureService? capture,IAnchorTextDetector? detector,bool game)
    {
        if(owner is null)return null;Bitmap source;Avalonia.PixelRect? picked=null;
        if(game)
        {
            var live=await LiveTargetPicker.PickAsync(owner,capture,TargetSelectionKind.Region);if(live is null)return null;
            source=live.Capture;picked=live.Region;
        }
        else{var path=await OpenImage(owner);if(path is null)return null;source=StableAnchorCrop.Load(path);}
        using(source)
        {
            if(picked is null){var select=new CropSelectionWindow(source);if(!await select.ShowDialog<bool>(owner))return null;picked=select.Selection;}
            if(picked is not {} box)return null;
            Bitmap image=ImagePixels.Crop(source,box);var offset=new Avalonia.PixelRect(0,0,box.Width,box.Height);
            if(detector is not null){var stable=StableAnchorCrop.RemoveLevels(image,detector);image.Dispose();image=stable.Image;offset=stable.Offset;}
            var native=new[]{box.X+offset.X,box.Y+offset.Y,box.X+offset.X+offset.Width,box.Y+offset.Y+offset.Height};
            if(game)
            {
                var size=repository.Catalog["client_size"]!.AsArray();var reference=new Avalonia.PixelSize(size[0]!.GetValue<int>(),size[1]!.GetValue<int>());
                var normalized=ToReference(new(native[0],native[1],native[2]-native[0],native[3]-native[1]),source.PixelSize,reference);
                native=[normalized.X,normalized.Y,normalized.Right,normalized.Bottom];
                if(image.PixelSize.Width!=normalized.Width||image.PixelSize.Height!=normalized.Height){var resized=ImagePixels.Resize(image,normalized.Width,normalized.Height);image.Dispose();image=resized;}
            }
            var asset=repository.ImportAsset(StableAnchorCrop.Png(image),image.PixelSize.Width,image.PixelSize.Height,native,ImagePixels.Preview(image));return(asset,native,image);
        }
    }
    public static Bitmap? Preview(string path,IAnchorTextDetector? detector)
    {using var image=StableAnchorCrop.Load(path);return detector is null?ImagePixels.Crop(image,new(0,0,image.PixelSize.Width,image.PixelSize.Height)):StableAnchorCrop.RemoveLevels(image,detector).Image;}
    public static Avalonia.PixelRect ToReference(Avalonia.PixelRect native,Avalonia.PixelSize actual,Avalonia.PixelSize reference)
    {
        var box=EditorSelectionGeometry.ToReference(new(native.X,native.Y,native.Right,native.Bottom),actual.Width,actual.Height,reference.Width,reference.Height);
        return new(box.Left,box.Top,box.Width,box.Height);
    }
}
