using Avalonia.Controls;
using Avalonia.Interactivity;
namespace MabiAgent.Desktop.Editors;
public partial class ChainEditorPanel:UserControl
{
    private ChainEditorViewModel? previous;
    public ChainEditorPanel(){InitializeComponent();DataContextChanged+=(_,_)=>{if(previous is {} old)old.PropertyChanged-=Changed;previous=DataContext as ChainEditorViewModel;if(previous is {} vm){vm.PropertyChanged+=Changed;vm.RequestDiscard=retry=>_ = UiDialogs.RetryDiscard(this,()=>{var oldConfirm=vm.ConfirmDiscard;vm.ConfirmDiscard=()=>true;try{retry();}finally{vm.ConfirmDiscard=oldConfirm;}});}RefreshPreview();};}
    void SetPreview(Avalonia.Media.Imaging.Bitmap? image){var old=PreviewImage.Source;PreviewImage.Source=image;PreviewImage.IsVisible=image is not null;if(!ReferenceEquals(old,image)&&old is IDisposable disposable)disposable.Dispose();}
    void Changed(object? sender,System.ComponentModel.PropertyChangedEventArgs e){if(e.PropertyName==nameof(ChainEditorViewModel.SelectedVariant))RefreshPreview();}
    void RefreshPreview(){if(DataContext is ChainEditorViewModel vm&&vm.SelectedVariant is {} v){try{SetPreview(EditorImagePicker.Preview(vm.Repository.AssetPath(EditorRepository.Text(v.Data["asset"]!,"id")),vm.TextDetector));}catch(Exception error){vm.ReportError(error);SetPreview(null);}}else SetPreview(null);}
    async void BrowseClick(object sender,RoutedEventArgs e)=>await Pick(false,false);
    async void AppendClick(object sender,RoutedEventArgs e)=>await Pick(false,true);
    async void CaptureClick(object sender,RoutedEventArgs e)=>await Pick(true,false);
    async Task Pick(bool game,bool append){if(DataContext is not ChainEditorViewModel vm||vm.SelectedAnchor is null)return;try{var result=await EditorImagePicker.Pick(TopLevel.GetTopLevel(this) as Window,vm.Repository,vm.CaptureService,vm.TextDetector,game);if(result is {} r){vm.SetVariant(r.Asset,r.Box,append);SetPreview(vm.TextDetector is null?null:r.Preview);if(vm.TextDetector is null)r.Preview.Dispose();if(vm.TextDetector is null)vm.Status="图片已载入，请核对框选区域。";}}catch(Exception e){vm.ReportError(e);}}
}


