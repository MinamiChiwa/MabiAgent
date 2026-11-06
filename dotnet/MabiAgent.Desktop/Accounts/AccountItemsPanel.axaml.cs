using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
namespace MabiAgent.Desktop.Accounts;

public partial class AccountItemsPanel : UserControl
{
    private AccountItemsViewModel? model;
    private sealed record SortColumnKey(string Id,Func<AccountItemEditRow,object?> Value);
    private readonly Dictionary<DataGridColumn,SortColumnKey> sortKeys = [];
    private string? sortedColumnId;
    private ListSortDirection? sortedDirection;
    private bool syncingSelection;
    private Queue<Action>? pendingColumns;
    private bool gridLoadQueued;
    private bool gridReady;
    private int gridStage;
    private Stopwatch? gridLoadWatch;
    private int measureCount;
    private int arrangeCount;
    private double measureTotalMs;
    private double arrangeTotalMs;
    private double maxMeasureMs;
    private double maxArrangeMs;
    internal int GridMeasureCount=>measureCount;
    internal int GridArrangeCount=>arrangeCount;
    internal double GridMeasureMs=>measureTotalMs;
    internal double GridArrangeMs=>arrangeTotalMs;
    internal double GridMaxMeasureMs=>maxMeasureMs;
    internal double GridMaxArrangeMs=>maxArrangeMs;
    internal double GridLoadMs{get;private set;}

    public AccountItemsPanel()
    {
        InitializeComponent();
        ItemsGrid.BeginningEdit += (_, _) => { if (model is not null) model.IsCellEditing = true; };
        ItemsGrid.CellEditEnded += (_, _) => { if (model is not null) model.IsCellEditing = false; };
        DataContextChanged += (_, _) => Subscribe(DataContext as AccountItemsViewModel);
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        AttachedToVisualTree += (_, _) => { if(!gridReady){ItemsGrid.IsVisible=false;ItemsSkeleton.IsVisible=true;ItemsGrid.ItemsSource=null;ItemsGrid.Columns.Clear();sortKeys.Clear();} Subscribe(DataContext as AccountItemsViewModel); QueueGridLoad(); };
        EffectiveViewportChanged += (_, _) => QueueGridLoad();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var watch=Stopwatch.StartNew();
        try{return base.MeasureOverride(availableSize);}
        finally{var elapsed=watch.Elapsed.TotalMilliseconds;measureCount++;measureTotalMs+=elapsed;maxMeasureMs=Math.Max(maxMeasureMs,elapsed);}
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var watch=Stopwatch.StartNew();
        try{return base.ArrangeOverride(finalSize);}
        finally{var elapsed=watch.Elapsed.TotalMilliseconds;arrangeCount++;arrangeTotalMs+=elapsed;maxArrangeMs=Math.Max(maxArrangeMs,elapsed);}
    }
    public AccountItemsPanel(AccountItemsViewModel viewModel) : this() => DataContext = viewModel;

    private void Unsubscribe()
    {
        if (model is not null)
        {
            model.ColumnsChanged -= RebuildColumns;
            model.PropertyChanged -= ModelChanged;
            model.ItemsView.SortDescriptions.CollectionChanged -= SortChanged;
        }
        model = null;
    }

    private void Subscribe(AccountItemsViewModel? value)
    {
        Unsubscribe();
        model = value;
        if (model is not null)
        {
            model.ColumnsChanged += RebuildColumns;
            model.PropertyChanged += ModelChanged;
            model.ItemsView.SortDescriptions.CollectionChanged += SortChanged;
        }
        if(gridReady){ItemsGrid.ItemsSource=model?.ItemsView;BuildColumns();}
        else if(TopLevel.GetTopLevel(this) is null)BuildColumns();
        else{ItemsGrid.Columns.Clear();sortKeys.Clear();pendingColumns=null;}
        QueueGridLoad();
    }
    private void SortChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateHeaders();

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountItemsViewModel.Search)) ItemsGrid.SelectedItems.Clear();
        else if (e.PropertyName == nameof(AccountItemsViewModel.SelectedCount)) RestoreGridSelection();
    }

    private void RebuildColumns(object? sender, EventArgs e)
    {
        if(gridReady)BuildColumns();else pendingColumns=null;
    }


    internal bool IsGridReady=>gridReady;

    private void QueueGridLoad()
    {
        if(gridReady||gridLoadQueued||!IsEffectivelyVisible)return;
        gridLoadWatch??=Stopwatch.StartNew();
        gridLoadQueued=true;
        Dispatcher.UIThread.Post(RunGridLoad,DispatcherPriority.Background);
    }

    private void RunGridLoad()
    {
        gridLoadQueued=false;
        if(gridReady||!IsEffectivelyVisible)return;
        if(gridStage==0)
        {
            ItemsSkeleton.IsVisible=true;
            ItemsGrid.ItemsSource=null;
            ItemsGrid.Columns.Clear();
            sortKeys.Clear();
            // Keep the DataGrid detached from layout until the page's first input frame
            // has rendered. Merely shrinking it to one pixel still initializes all of
            // its column and theme templates in the page's first synchronous measure.
            ItemsGrid.IsVisible=false;
            gridStage=1;
        }
        else if(gridStage==1)
        {
            PrepareColumns();
            gridStage=2;
        }
        else if(pendingColumns is null)PrepareColumns();
        else
        {
            for(var added=0;added<2&&pendingColumns.Count>0;added++)pendingColumns.Dequeue()();
            if(pendingColumns.Count==0)
            {
                ItemsGrid.Height=double.NaN;
                ItemsGrid.ItemsSource=model?.ItemsView;
                ItemsGrid.IsVisible=true;
                ItemsSkeleton.IsVisible=false;
                gridReady=true;
                gridLoadWatch?.Stop();
                GridLoadMs=gridLoadWatch?.Elapsed.TotalMilliseconds??0;
                return;
            }
        }
        QueueGridLoad();
    }
    private void BuildColumns()
    {
        PrepareColumns();
        while(pendingColumns is { Count:>0 })pendingColumns.Dequeue()();
    }

    private void PrepareColumns()
    {
        ItemsGrid.Columns.Clear();
        sortKeys.Clear();
        var plans=new Queue<Action>();
        if(model is null){pendingColumns=plans;return;}
        plans.Enqueue(()=>{
            Add("道具编号", "ItemId", r => r.ItemId, 125);
            Add("名称", "Name", r => r.Name, 170, true);
        });
        plans.Enqueue(()=>{
            var associationColumn=new DataGridTemplateColumn{Header="关联状态",Width=new DataGridLength(110),CanUserSort=true,CellTemplate=new FuncDataTemplate<AccountItemEditRow>((row,_)=>
            {
                var text=new TextBlock{Text=row.Association,VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center};
                text.Classes.Add(row.OriginalAssociation is MabiAgent.Capture.NameAssociation.UserConfirmed or MabiAgent.Capture.NameAssociation.BuiltinVerified?"status-success":row.OriginalAssociation==MabiAgent.Capture.NameAssociation.Candidate?"status-warning":"muted");
                return text;
            })};
            ItemsGrid.Columns.Add(associationColumn);sortKeys[associationColumn]=new("Association",r=>r.Association);UpdateHeader(associationColumn);
        });
        foreach (var column in model.CharacterColumns)
        {
            var index = column.Index;
            var characterId=column.Id;
            plans.Enqueue(() =>
            {
                var gridColumn = Add(column.Label + " [" + characterId + "]", $"CharacterTotals[{index}]", r => r.QuantityForCharacter(characterId), 130, sortId: "character:" + characterId);
                var theme = new ControlTheme(typeof(DataGridCell));
                theme.Setters.Add(new Setter(ToolTip.TipProperty, new Binding($"CharacterDetails[{index}]")));
                gridColumn.CellTheme = theme;
            });
        }
        plans.Enqueue(()=>{
            Add("共有仓", "SharedStorage", r => r.SharedStorage, 105);
            Add("账户总量", "AccountTotal", r => r.AccountTotal, 110);
            Add("已观察小计", "ObservedTotal", r => r.ObservedTotal, 110);
        });
        plans.Enqueue(RestoreColumnState);
        pendingColumns=plans;
    }

    private void RestoreColumnState()
    {
        if(model is null)return;
        if(model.ItemsView.SortDescriptions.Count==0){sortedColumnId=null;sortedDirection=null;}
        else if(sortedColumnId is not null&&sortedDirection is {} direction)
        {
            var restored=sortKeys.Values.FirstOrDefault(k=>k.Id==sortedColumnId);
            if(restored is null&&model.Items.Count>0){model.ItemsView.SortDescriptions.Clear();sortedColumnId=null;sortedDirection=null;}
            else if(restored is not null)ApplySort(restored,direction);
        }
        RestoreGridSelection();
    }
    private DataGridTextColumn Add(string header, string path, Func<AccountItemEditRow, object?> key, double width, bool edit = false, string? sortId = null)
    {
        var binding = new Binding(path) { Mode = edit ? BindingMode.TwoWay : BindingMode.OneWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
        if (path is not ("Name" or "Association" or "ItemId")) { binding.TargetNullValue = "未知"; binding.StringFormat = "N0"; }
        var column = new DataGridTextColumn { Header = header, Binding = binding, IsReadOnly = !edit, Width = new DataGridLength(Math.Max(width, 96)), MinWidth = Math.Min(width, 96), CanUserSort = true };
        if (edit) column.CellStyleClasses.Add("editable");
        ItemsGrid.Columns.Add(column);
        sortKeys[column] = new(sortId ?? path, key);
        UpdateHeader(column);
        return column;
    }

    private void UpdateHeader(DataGridColumn column)
    {
        var editable = !column.IsReadOnly;
        ListSortDirection? direction = model?.ItemsView.SortDescriptions.Count > 0 && sortKeys.TryGetValue(column, out var key) && key.Id == sortedColumnId ? sortedDirection : null;
        column.HeaderTemplate = new FuncDataTemplate<object?>((header, _) =>
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            if (editable) row.Children.Add(new Controls.Icon { Kind = "edit", Width = 12, Height = 12, Opacity = .8 });
            row.Children.Add(new TextBlock { Text = header?.ToString(), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            if (direction is { } d) row.Children.Add(new Controls.Icon { Kind = d == ListSortDirection.Ascending ? "sort-up" : "sort-down", Width = 10, Height = 10 });
            if (editable) ToolTip.SetTip(row, "可编辑：双击单元格修改名称");
            return row;
        });
    }

    private void UpdateHeaders() { foreach (var column in ItemsGrid.Columns) UpdateHeader(column); }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingSelection || model is null) return;
        syncingSelection = true;
        try { model.SetSelection(ItemsGrid.SelectedItems.OfType<AccountItemEditRow>()); }
        finally { syncingSelection = false; }
    }

    private void RestoreGridSelection()
    {
        if (model is null) return;
        syncingSelection = true;
        try
        {
            ItemsGrid.SelectedItems.Clear();
            foreach (var row in model.SelectedRows)
                if (ItemsGrid.ItemsSource?.Cast<object>().Contains(row) == true) ItemsGrid.SelectedItems.Add(row);
        }
        finally { syncingSelection = false; }
    }

    private void Commit()
    {
        ItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        model?.SetSelection(ItemsGrid.SelectedItems.OfType<AccountItemEditRow>());
    }

    private void ConfirmSelected(object sender, RoutedEventArgs e) { Commit(); model?.ConfirmSelectedCommand.Execute(null); }
    private void ClearSelected(object sender, RoutedEventArgs e) { Commit(); model?.ClearSelectedCommand.Execute(null); }
    private void DiscardEdits(object sender, RoutedEventArgs e) { ItemsGrid.CancelEdit(); model?.Refresh(true); }
    public void FocusSearch() { SearchBox.Focus(); SearchBox.SelectAll(); }
    private void HandleKeys(object sender, KeyEventArgs e)
    { if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { FocusSearch(); e.Handled = true; } }
    private void SortItems(object sender, DataGridColumnEventArgs e)
    {
        if (!sortKeys.ContainsKey(e.Column)) return;
        e.Handled = true;
        SortColumn(e.Column);
    }

    internal void SortColumn(DataGridColumn column)
    {
        if (model is null || !sortKeys.TryGetValue(column, out var key)) return;
        Commit();
        var direction = sortedColumnId == key.Id && sortedDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        sortedColumnId = key.Id;
        sortedDirection = direction;
        ApplySort(key, direction);
    }

    private void ApplySort(SortColumnKey key, ListSortDirection direction)
    {
        if (model is null) return;
        model.ItemsView.SortDescriptions.Clear();
        model.ItemsView.SortDescriptions.Add(DataGridSortDescription.FromComparer(new ItemComparer(key.Value, direction == ListSortDirection.Ascending ? 1 : -1)));
    }

    private sealed class ItemComparer(Func<AccountItemEditRow, object?> key, int direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not AccountItemEditRow a || y is not AccountItemEditRow b) return 0;
            var av = key(a);
            var bv = key(b);
            if (av is null) return bv is null ? a.ItemId.CompareTo(b.ItemId) : 1;
            if (bv is null) return -1;
            var result = av is IComparable comparable ? comparable.CompareTo(bv) : string.Compare(av.ToString(), bv.ToString(), StringComparison.CurrentCulture);
            return result == 0 ? a.ItemId.CompareTo(b.ItemId) : result * direction;
        }
    }

    private void OpenCsvMenu(object sender, RoutedEventArgs e)
    { if (sender is Button button && button.ContextMenu is { } menu) { PopulateFiles(); menu.Open(button); } }

    private async void ExportCsv(object? sender, RoutedEventArgs e)
    {
        if (model?.SelectedAccount is null || TopLevel.GetTopLevel(this) is not { } owner) return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出账户表",
            SuggestedFileName = "账户道具.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV 文件") { Patterns = ["*.csv"] }],
            ShowOverwritePrompt = true
        });
        if (file?.TryGetLocalPath() is { } path) model.ExportCsv(path);
    }

    private void PopulateCsvFiles(object? sender, RoutedEventArgs e) => PopulateFiles();
    private void PopulateFiles()
    {
        var menu = OpenFilesMenu;
        menu.Items.Clear();
        var paths = model?.CsvPaths();
        if (paths is not null) { AddFile(menu, "名称关联", paths.Associations); AddFile(menu, "库存明细", paths.Inventory); }
        if (model?.LastExportPath is { } export) AddFile(menu, "最近导出", export);
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "暂无表格文件", IsEnabled = false });
    }

    private void AddFile(MenuItem menu, string label, string path)
    {
        var item = new MenuItem { Header = label + " · " + path, IsEnabled = File.Exists(path) };
        item.Click += (_, _) =>
        {
            try
            {
                if (!File.Exists(path)) throw new FileNotFoundException("CSV 已不存在，请刷新", path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception error) { model?.ReportError(error); }
        };
        menu.Items.Add(item);
    }
}
