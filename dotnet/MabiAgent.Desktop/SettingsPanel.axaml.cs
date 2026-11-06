using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MabiAgent.Desktop.Controls;

namespace MabiAgent.Desktop;

public partial class SettingsPanel : UserControl
{
    private bool? stackedLayout;
    private bool initialLayoutCaptured;
    private Stopwatch? visibleToLoadedWatch;
    private int renderCount;
    private double firstRenderMs;
    private int measureCount;
    private int arrangeCount;
    private double measureTotalMs;
    private double arrangeTotalMs;
    private double maxMeasureMs;
    private double maxArrangeMs;

    internal int InitialMeasureCount { get; private set; }
    internal int InitialArrangeCount { get; private set; }
    internal double InitialMeasureTotalMs { get; private set; }
    internal double InitialArrangeTotalMs { get; private set; }
    internal double InitialMaxMeasureMs { get; private set; }
    internal double InitialMaxArrangeMs { get; private set; }
    internal double InitialVisibleToLoadedMs { get; private set; }
    internal double InitialRenderMs { get; private set; }
    internal int InitialRenderCount { get; private set; }

    public event EventHandler? OpenWorkbenchRequested;
    public event EventHandler? PageShown;
    private int updateRevealGeneration;
    private MainViewModel? settingsModel;

    public SettingsPanel()
    {
        InitializeComponent();
        DataContextChanged+=(_,_)=>BindSettingsModel(DataContext as MainViewModel);
        OperationSettingsSection.ContentBuilt+=(_,_)=>BindSettingChoiceEvents(OperationSettingsSection);
        AppearanceSettingsSection.ContentBuilt+=(_,_)=>BindSettingChoiceEvents(AppearanceSettingsSection);
    }

    private void BindSettingsModel(MainViewModel? value)
    {
        if(settingsModel is not null)settingsModel.PropertyChanged-=SettingsModelChanged;
        settingsModel=value;
        if(settingsModel is not null)settingsModel.PropertyChanged+=SettingsModelChanged;
        SyncSettingChoices();
    }
    private void SettingsModelChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(MainViewModel.InputScheme) or nameof(MainViewModel.ThemeKey) or nameof(MainViewModel.IsTouchInput))SyncSettingChoices();
    }
    private void BindSettingChoiceEvents(DeferredSettingsSection section)
    {
        foreach(var choice in section.GetVisualDescendants().OfType<RadioButton>())
            choice.IsCheckedChanged+=SettingChoiceChecked;
        SyncSettingChoices();
    }
    private void SettingChoiceChecked(object? sender,RoutedEventArgs e)
    {
        if(sender is not RadioButton { IsChecked:true } choice||choice.Tag is not string tag)return;
        if(DataContext is not MainViewModel model)return;
        if(tag=="input:touch")model.InputScheme="touch";
        else if(tag=="input:mouse")model.InputScheme="mouse";
        else if(tag.StartsWith("theme:",StringComparison.Ordinal))model.ThemeKey=tag[6..];
    }
    private void SyncSettingChoices()
    {
        if(settingsModel is null)return;
        if(!Dispatcher.UIThread.CheckAccess()){Dispatcher.UIThread.Post(SyncSettingChoices,DispatcherPriority.Loaded);return;}
        foreach(var choice in this.GetVisualDescendants().OfType<RadioButton>())
        {
            if(choice.Tag is not string tag)continue;
            var selected=tag.StartsWith("input:",StringComparison.Ordinal)?tag[6..]==settingsModel.InputScheme:tag.StartsWith("theme:",StringComparison.Ordinal)&&tag[6..]==settingsModel.ThemeKey;
            choice.SetCurrentValue(ToggleButton.IsCheckedProperty,selected);
        }
    }

    internal void CaptureInitialLayoutProfile()
    {
        if(initialLayoutCaptured)return;
        initialLayoutCaptured=true;
        InitialMeasureCount=measureCount;
        InitialArrangeCount=arrangeCount;
        InitialMeasureTotalMs=measureTotalMs;
        InitialArrangeTotalMs=arrangeTotalMs;
        InitialMaxMeasureMs=maxMeasureMs;
        InitialMaxArrangeMs=maxArrangeMs;
        InitialVisibleToLoadedMs=visibleToLoadedWatch?.Elapsed.TotalMilliseconds??0;
        InitialRenderMs=firstRenderMs;
        InitialRenderCount=renderCount;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width=double.IsFinite(availableSize.Width)?availableSize.Width:Bounds.Width;
        if(width>0)UpdateColumns(width);
        var watch=Stopwatch.StartNew();
        try{return base.MeasureOverride(availableSize);}
        finally
        {
            var elapsed=watch.Elapsed.TotalMilliseconds;
            measureCount++;measureTotalMs+=elapsed;maxMeasureMs=Math.Max(maxMeasureMs,elapsed);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var watch=Stopwatch.StartNew();
        try{return base.ArrangeOverride(finalSize);}
        finally
        {
            var elapsed=watch.Elapsed.TotalMilliseconds;
            arrangeCount++;arrangeTotalMs+=elapsed;maxArrangeMs=Math.Max(maxArrangeMs,elapsed);
        }
    }

    public override void Render(DrawingContext context)
    {
        var watch=Stopwatch.StartNew();
        try{base.Render(context);}
        finally
        {
            renderCount++;
            if(renderCount==1)firstRenderMs=watch.Elapsed.TotalMilliseconds;
        }
    }

    private void OpenWorkbench(object? sender,RoutedEventArgs e)=>OpenWorkbenchRequested?.Invoke(this,EventArgs.Empty);
    public void ShowUpdateSection()
    {
        if(!Dispatcher.UIThread.CheckAccess()){Dispatcher.UIThread.Post(ShowUpdateSection,DispatcherPriority.Normal);return;}
        var generation=++updateRevealGeneration;
        RevealUpdateSection(generation,0);
    }
    private void RevealUpdateSection(int generation,int pass)
    {
        if(generation!=updateRevealGeneration)return;
        if(IsEffectivelyVisible)
        {
            // This is an explicit navigation action, so realizing the requested card is
            // intentional. BringIntoView still supplies the finite outer viewport, and
            // later passes correct the scroll extent after the card acquires real height.
            UpdateSettingsSection.EnsureBuilt();
            UpdateSettingsSection.BringIntoView();
        }
        if(pass<4)Dispatcher.UIThread.Post(()=>RevealUpdateSection(generation,pass+1),DispatcherPriority.Loaded);
    }
    private void ReservePickerInitialized(object? sender,EventArgs e)
    {
        if(sender is AutoCompleteBox picker)Controls.SearchablePicker.OpenOnFocus<ReserveChoice>(picker,g=>g.Name);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if(change.Property==IsVisibleProperty&&change.GetNewValue<bool>())
        {
            visibleToLoadedWatch??=Stopwatch.StartNew();
            Dispatcher.UIThread.Post(()=>{if(IsEffectivelyVisible&&TopLevel.GetTopLevel(this) is not null){CaptureInitialLayoutProfile();PageShown?.Invoke(this,EventArgs.Empty);}},DispatcherPriority.Loaded);
        }
    }
    // Responsive columns are selected before this control's measure pass. Mutating
    // Grid definitions from SizeChanged can invalidate the first layout repeatedly.
    private void UpdateColumns(double width)
    {
        var single=width<820;
        if(stackedLayout==single)return;
        stackedLayout=single;
        SettingsColumns.ColumnDefinitions[1].Width=new GridLength(single?0:16);
        SettingsColumns.ColumnDefinitions[2].Width=single?new GridLength(0):new GridLength(1,GridUnitType.Star);
        Grid.SetColumnSpan(RunColumn,single?3:1);
        Grid.SetRow(MaintenanceColumn,single?1:0);Grid.SetColumn(MaintenanceColumn,single?0:2);Grid.SetColumnSpan(MaintenanceColumn,single?3:1);
        MaintenanceColumn.Margin=new Thickness(0,single?16:0,0,0);
    }
}
