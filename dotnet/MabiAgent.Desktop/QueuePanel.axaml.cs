using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.ComponentModel;
using MabiAgent.Desktop.Controls;
namespace MabiAgent.Desktop;
public partial class QueuePanel:UserControl
{
    private MainViewModel? model;private bool syncing;
    public QueuePanel()
    {
        InitializeComponent();
        DataContextChanged+=(_,_)=>{if(model is not null)model.PropertyChanged-=ModelChanged;model=DataContext as MainViewModel;if(model is not null)model.PropertyChanged+=ModelChanged;foreach(var g in new[]{ManualGrid,AutomaticGrid})if(g.ContextMenu is{} menu)BuildMenu(menu);UpdateListHeight();};
        SizeChanged+=(_,_)=>UpdateListHeight();
        foreach(var grid in new[]{ManualGrid,AutomaticGrid})
        {
            grid.AddHandler(KeyDownEvent,QueueKeyDown,RoutingStrategies.Bubble);
            var menu=new ContextMenu();menu.Items.Add(new MenuItem{Header="任务操作",IsEnabled=false});grid.ContextMenu=menu;
            menu.Opening+=(_,_)=>BuildMenu(menu);
            grid.AddHandler(PointerPressedEvent,Pressed,RoutingStrategies.Tunnel,true);
            var target=grid;
            _=new RowDragReorder(grid,v=>v.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault(),IsHandle,CanDrag,
                ()=>target.ItemsSource?.Cast<object>().ToArray()??[],item=>target.ScrollIntoView(item,null),DropRow);
        }
    }
    private static DataGridRow? Row(object? source)=>(source as Visual)?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
    private static bool IsHandle(Visual source)=>source.GetSelfAndVisualAncestors().OfType<Control>().Any(c=>c.Classes.Contains("queue-drag-handle"));
    private bool CanDrag(object item)
    {
        if(model is null||item is not JobRow row||model.IsBusy)return false;
        if((row.Automatic?model.AutomaticView:model.QueueView).SortDescriptions.Count==0)return true;
        model.OperationStatus="请先重置表头排序，再拖动任务";return false;
    }
    // Converts a drop slot (before/after a row) into the anchor row ReorderAsync expects:
    // moving down places the job after the anchor, moving up places it before.
    private async Task DropRow(object dragged,object over,bool after)
    {
        if(model is null||dragged is not JobRow source||over is not JobRow target||source.Automatic!=target.Automatic)return;
        var view=(source.Automatic?model.AutomaticView:model.QueueView).Cast<JobRow>().ToList();
        var from=view.IndexOf(source);var at=view.IndexOf(target);if(from<0||at<0)return;
        var slot=after?at+1:at;if(slot>from)slot--;if(slot==from)return;
        var rest=view.Where(j=>!ReferenceEquals(j,source)).ToList();
        await model.ReorderAsync(source,slot>from?rest[slot-1]:rest[slot]);
    }
    private void UpdateListHeight()
    {
        if (Bounds.Height <= 0) return;
        var available = Bounds.Height - 80 - (model?.AutomaticCount > 0 ? 48 : 0) - (model?.HasQueueSelection == true ? 56 : 0);
        // The list grows with its rows up to the page height; a short queue leaves no blank panel below it.
        ManualCard.MaxHeight = Math.Clamp(available, 200, 900);
    }
    private void ModelChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(MainViewModel.QueueSelectionCount) or nameof(MainViewModel.AutomaticCount)){UpdateListHeight();foreach(var g in new[]{ManualGrid,AutomaticGrid})if(g.ContextMenu is{} menu)BuildMenu(menu);}
        if(e.PropertyName!=nameof(MainViewModel.SelectedJob)||model is null||model.IsApplyingQueue||syncing)return;
        syncing=true;
        try
        {
            var ids=model.SelectedJobIds.ToHashSet();if(ids.Count==0&&model.SelectedJob is{} primary)ids.Add(primary.Id);
            foreach(var grid in new[]{ManualGrid,AutomaticGrid})
            {
                var selected=grid.ItemsSource.Cast<JobRow>().Where(j=>ids.Contains(j.Id)).ToArray();
                if(grid.SelectedItems.Cast<JobRow>().ToHashSet().SetEquals(selected))continue;
                grid.SelectedItems.Clear();foreach(var row in selected)grid.SelectedItems.Add(row);
            }
        }
        finally{syncing=false;}
    }
    private void SelectQueueRow(object sender,SelectionChangedEventArgs e)
    {
        if(model is null||model.IsApplyingQueue||syncing)return;
        syncing=true;
        try{var rows=ManualGrid.SelectedItems.Cast<JobRow>().Concat(AutomaticGrid.SelectedItems.Cast<JobRow>()).ToArray();model.SetQueueSelection(rows);model.SelectedJob=e.AddedItems.OfType<JobRow>().LastOrDefault()??rows.FirstOrDefault();}
        finally{syncing=false;}
    }
    private void Pressed(object? sender,PointerPressedEventArgs e)
    {
        if(Row(e.Source)?.DataContext is not JobRow row||sender is not DataGrid grid)return;
        var properties=e.GetCurrentPoint(this).Properties;
        if(properties.IsRightButtonPressed)
        {if(!grid.SelectedItems.Contains(row)){(ReferenceEquals(grid,ManualGrid)?AutomaticGrid:ManualGrid).SelectedItems.Clear();grid.SelectedItem=row;}else e.Handled=true;return;}
        if(properties.IsLeftButtonPressed&&e.KeyModifiers==KeyModifiers.None)(ReferenceEquals(grid,ManualGrid)?AutomaticGrid:ManualGrid).SelectedItems.Clear();
    }
    private void BuildMenu(ContextMenu menu)
    {
        if(model is null)return;
        menu.Items.Clear();
        void Add(string text,System.Windows.Input.ICommand command,object? parameter=null,bool enabled=true)=>menu.Items.Add(new MenuItem{Header=text,Command=command,CommandParameter=parameter,IsEnabled=enabled});
        Add("暂停所选",model.ControlSelectedCommand,"pause",model.HasQueueSelection);
        Add("恢复所选",model.ControlSelectedCommand,"continue",model.HasQueueSelection);
        menu.Items.Add(new Separator());
        Add("上移",model.MoveCommand,"up",model.QueueSelectionCount==1);Add("下移",model.MoveCommand,"down",model.QueueSelectionCount==1);
        Add("重置列表排序",model.ResetSortCommand);
        if(model.SelectedJob is{} row&&model.QueueSelectionCount==1)menu.Items.Add(new MenuItem{Header=row.DependencyLabel,IsEnabled=false});
        menu.Items.Add(new Separator());Add("删除所选（Delete）",model.DeleteSelectedCommand,null,model.HasQueueSelection);
    }
    private void QueueKeyDown(object? sender,KeyEventArgs e)
    {
        if(model is null||(e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TextBox>().Any()==true)return;
        if(e.Key==Key.Delete){model.DeleteSelectedCommand.Execute(null);e.Handled=true;}
        else if(e.Key==Key.A&&e.KeyModifiers.HasFlag(KeyModifiers.Control)&&sender is DataGrid grid)
        {foreach(var row in grid.ItemsSource.Cast<JobRow>())if(!grid.SelectedItems.Contains(row))grid.SelectedItems.Add(row);e.Handled=true;}
    }
    private void TargetCellDoubleTapped(object? sender,RoutedEventArgs e)
    {
        if(sender is not Control{DataContext:JobRow row})return;
        if(!row.BeginTargetEdit())return;
        e.Handled=true;
    }
    internal async void CommitRow(JobRow row)
    {
        if(model is null||model.IsApplyingQueue||model.IsBusy)return;
        await model.SaveRowTargetAsync(row);
    }
}
