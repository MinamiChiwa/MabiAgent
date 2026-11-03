using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace MabiAgent.Desktop.Editors;

public partial class AnchorSetPicker:UserControl
{
    public static readonly StyledProperty<IEnumerable<WorkbenchAnchor>?> AnchorsProperty=AvaloniaProperty.Register<AnchorSetPicker,IEnumerable<WorkbenchAnchor>?>(nameof(Anchors));
    public static readonly StyledProperty<string> SelectedIdsProperty=AvaloniaProperty.Register<AnchorSetPicker,string>(nameof(SelectedIds),"",defaultBindingMode:BindingMode.TwoWay);
    public IEnumerable<WorkbenchAnchor>? Anchors {get=>GetValue(AnchorsProperty);set=>SetValue(AnchorsProperty,value);}
    public string SelectedIds {get=>GetValue(SelectedIdsProperty);set=>SetValue(SelectedIdsProperty,value);}
    public AnchorSetPicker(){InitializeComponent();PropertyChanged+=(_,e)=>{if(e.Property==AnchorsProperty||e.Property==SelectedIdsProperty)Refresh();};}
    void Refresh()
    {
        var ids=WorkbenchModel.SplitIds(SelectedIds??"").ToHashSet();var all=(Anchors??[]).ToArray();
        Chosen.ItemsSource=all.Where(a=>ids.Contains(a.Id)).ToArray();EmptyHint.IsVisible=ids.Count==0;
        var search=Filter.Text?.Trim()??"";
        Available.ItemsSource=all.Where(a=>!ids.Contains(a.Id)&&(search.Length==0||(a.DisplayName+" "+a.Id).Contains(search,StringComparison.OrdinalIgnoreCase))).Take(80).ToArray();
    }
    void SearchChanged(object? sender,TextChangedEventArgs e)=>Refresh();
    void Add(object? sender,RoutedEventArgs e)
    {if(Available.SelectedItem is WorkbenchAnchor anchor)SetCurrentValue(SelectedIdsProperty,string.Join(", ",WorkbenchModel.SplitIds(SelectedIds??"").Append(anchor.Id).Distinct()));}
    void Remove(object? sender,RoutedEventArgs e)
    {if(Chosen.SelectedItem is WorkbenchAnchor anchor)SetCurrentValue(SelectedIdsProperty,string.Join(", ",WorkbenchModel.SplitIds(SelectedIds??"").Where(id=>id!=anchor.Id)));}
}
