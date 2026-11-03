using Avalonia.Controls;
using Avalonia.Interactivity;
namespace MabiAgent.Desktop.Editors;
public partial class AnchorEditorPanel:UserControl
{
    public void FocusSearch(){AnchorSearchBox.Focus();AnchorSearchBox.SelectAll();}
    private AnchorEditorViewModel? previous;
    public AnchorEditorPanel(){InitializeComponent();DataContextChanged+=(_,_)=>{if(previous is {} old)old.PropertyChanged-=Changed;previous=DataContext as AnchorEditorViewModel;if(previous is {} vm){vm.PropertyChanged+=Changed;vm.RequestDiscard=retry=>_ = UiDialogs.RetryDiscard(this,()=>{var confirm=vm.ConfirmDiscard;vm.ConfirmDiscard=()=>true;try{retry();}finally{vm.ConfirmDiscard=confirm;}});}RefreshPreview();};}
    void SetPreview(Avalonia.Media.Imaging.Bitmap? image){var old=PreviewImage.Source;PreviewImage.Source=image;PreviewImage.IsVisible=image is not null;if(!ReferenceEquals(old,image)&&old is IDisposable disposable)disposable.Dispose();}
    void Changed(object? sender,System.ComponentModel.PropertyChangedEventArgs e){if(e.PropertyName==nameof(AnchorEditorViewModel.Selected))RefreshPreview();}
    void RefreshPreview(){if(DataContext is AnchorEditorViewModel vm&&vm.Selected is {} selected){try{SetPreview(EditorImagePicker.Preview(vm.Repository.ResolveImagePath(EditorRepository.Text(selected.Spec,"path")),vm.TextDetector));}catch(Exception error){vm.ReportError(error);SetPreview(null);}}else SetPreview(null);}
    async void TestClick(object? sender,RoutedEventArgs e)
    {if(DataContext is not AnchorEditorViewModel vm||vm.Selected is null||vm.Fields is null)return;try{var path=await EditorImagePicker.OpenImage(TopLevel.GetTopLevel(this));if(path is not null){using var image=StableAnchorCrop.Load(path);await ShowTest(vm,image);}}catch(Exception error){vm.ReportError(error);}}
    async void LiveTestClick(object? sender,RoutedEventArgs e)
    {if(DataContext is not AnchorEditorViewModel vm||vm.Selected is null||vm.Fields is null)return;try{if(vm.CaptureService is null)throw new InvalidOperationException("请先选择游戏窗口");using var image=await vm.CaptureService.CaptureAsync();await ShowTest(vm,image);}catch(Exception error){vm.ReportError(error);}}
    async Task ShowTest(AnchorEditorViewModel vm,Avalonia.Media.Imaging.Bitmap image)
    {
        if(vm.Selected is null||vm.Fields is null||TopLevel.GetTopLevel(this) is not Window owner)return;
        var result=vm.TestService.Test(vm.Selected,vm.Fields.Data,image);vm.Status=result.Message;
        var size=vm.Repository.Catalog["client_size"]!.AsArray();var reference=new Avalonia.PixelSize(size[0]!.GetValue<int>(),size[1]!.GetValue<int>());
        var roi=ReadRegion(vm.Fields.Data["search"]);if(roi is {} configured)roi=EditorImagePicker.ToReference(configured,reference,image.PixelSize);
        var bounds=result.Bounds is {} hit?new Avalonia.PixelRect(hit.Left,hit.Top,hit.Width,hit.Height):(Avalonia.PixelRect?)null;
        var preview=new CropSelectionWindow(image,roi,bounds){Title=result.Message+" · 蓝框为搜索范围，拖动可调整"};
        if(await preview.ShowDialog<bool>(owner)&&preview.Selection is {} selection){var normalized=EditorImagePicker.ToReference(selection,image.PixelSize,reference);vm.Fields.Search=string.Join(",",normalized.X,normalized.Y,normalized.Right,normalized.Bottom);vm.Status="搜索范围已调整；保存后生效。";}
    }
    static Avalonia.PixelRect? ReadRegion(System.Text.Json.Nodes.JsonNode? node)
    {if(node is not System.Text.Json.Nodes.JsonArray a||a.Count!=4)return null;try{int l=a[0]!.GetValue<int>(),t=a[1]!.GetValue<int>(),r=a[2]!.GetValue<int>(),b=a[3]!.GetValue<int>();return r>l&&b>t?new(l,t,r-l,b-t):null;}catch{return null;}}
    async void PickSearchClick(object? sender,RoutedEventArgs e)=>await PickGeometry(true);
    async void PickRegionClick(object? sender,RoutedEventArgs e)=>await Pick(true);
    async Task PickGeometry(bool search)
    {
        if(DataContext is not AnchorEditorViewModel vm||vm.Fields is null||TopLevel.GetTopLevel(this) is not Window owner)return;
        try{using var result=await LiveTargetPicker.PickAsync(owner,vm.CaptureService,TargetSelectionKind.Region);if(result is null)return;var size=vm.Repository.Catalog["client_size"]!.AsArray();var box=EditorImagePicker.ToReference(result.Region,result.Capture.PixelSize,new(size[0]!.GetValue<int>(),size[1]!.GetValue<int>()));var text=string.Join(",",box.X,box.Y,box.Right,box.Bottom);if(search)vm.Fields.Search=text;else vm.Fields.Box=text;vm.Status=search?"搜索范围已定位；保存后生效。":"图片位置已定位；保存后生效。";}catch(Exception error){vm.ReportError(error);}
    }
    async void BrowseClick(object sender,RoutedEventArgs e)=>await Pick(false);
    async void CaptureClick(object sender,RoutedEventArgs e)=>await Pick(true);
    async Task Pick(bool game){if(DataContext is not AnchorEditorViewModel vm||vm.Selected is null)return;try{var result=await EditorImagePicker.Pick(TopLevel.GetTopLevel(this) as Window,vm.Repository,vm.CaptureService,vm.TextDetector,game);if(result is {} r){vm.SetImage(r.Asset,r.Box);SetPreview(r.Preview);}}catch(Exception e){vm.ReportError(e);}}
}

