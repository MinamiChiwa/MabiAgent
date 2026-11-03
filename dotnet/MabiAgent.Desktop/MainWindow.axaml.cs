using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ursa.Controls;
namespace MabiAgent.Desktop;
public partial class MainWindow:UrsaWindow
{
    Editors.AnchorWorkbenchWindow? workbenchWindow;
    MiniWindow? miniWindow;
    public Func<Task<bool>>? CloseRequest {get;set;}
    protected override Task<bool> CanClose()=>CloseRequest?.Invoke()??Task.FromResult(true);
    public MainWindow():this(new MainViewModel(new Workspace(AppContext.BaseDirectory),new MabiAgent.Windows.GameWindowSelection(()=>[]))){}
    public MainWindow(MainViewModel model)
    {
        InitializeComponent();DataContext=model;Controls.SearchablePicker.OpenOnFocus<GatheringChoice>(GatheringPicker,g=>g.Name);
        SizeChanged+=(_,_)=>UpdateResponsiveLayout();
        Opened+=(_,_)=>{Controls.WindowSizing.FitToWorkingArea(this);UpdateResponsiveLayout();};
        model.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(model.IsMiniMode))ApplyMiniMode(model);if(e.PropertyName==nameof(model.SelectedPage))UpdatePage();if(e.PropertyName==nameof(model.AddDialogOpen))Avalonia.Threading.Dispatcher.UIThread.Post(()=>{if(model.AddDialogOpen){Controls.Motion.FadeIn(AddOverlay);Controls.Motion.PopIn(AddGoalCard);TaskKindPicker.Focus();}else Navigation.Focus();});
            if(e.PropertyName==nameof(model.CaptureSettingsOpen)&&model.CaptureSettingsOpen)Avalonia.Threading.Dispatcher.UIThread.Post(()=>{Controls.Motion.FadeIn(CaptureOverlay);Controls.Motion.SlideIn(CaptureDrawer,48);CaptureDrawer.Focus();},Avalonia.Threading.DispatcherPriority.Loaded);};UpdatePage();
        Closed+=(_,_)=>{miniWindow?.CloseWithOwner();workbenchWindow?.CloseWithOwner();model.PrepareClose();};
    }
    internal void UpdateResponsiveLayout(Size? viewport=null)
    {
        var size=viewport??Bounds.Size;
        var compact=size.Width<1120;
        AppName.Text=ProductInfo.Title;
        ContentLayout.Margin=new Thickness(compact?16:24,4,compact?16:24,0);
        BottomBar.Margin=new Thickness(compact?16:24,compact?8:10,compact?16:24,compact?8:12);
        AddGoalCard.MaxHeight=Math.Max(180,size.Height-80);
        RuntimeViewer.MaxHeight=size.Height<700?132:200;
        GlobalErrorViewer.MaxHeight=size.Height<700?44:80;
        SearchRow.Width=compact?320:360;
        // Narrow windows move the window picker and actions under the summary line.
        Grid.SetRow(RuntimeControls,compact?1:0);Grid.SetColumn(RuntimeControls,compact?0:1);Grid.SetColumnSpan(RuntimeControls,compact?2:1);
        RuntimeControls.Margin=new Thickness(0,compact?10:0,0,0);
        RuntimeWindowRow.Width=compact?double.NaN:260;
        Grid.SetRow(RuntimeMeta,compact?2:1);Grid.SetRow(RuntimeDetails,compact?3:2);
    }
    // The compact window replaces the main window on screen; both stay bound to the same model.
    internal MiniWindow? Mini=>miniWindow;
    private void ApplyMiniMode(MainViewModel model)
    {
        if(model.IsMiniMode)
        {
            miniWindow??=new MiniWindow(model);
            miniWindow.ShowNear(this);Hide();
        }
        else
        {
            Show();if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Activate();
            miniWindow?.HideToOwner();
        }
    }
    private void UpdatePage()
    {
        if(DataContext is not MainViewModel vm)return;
        Control[] pages=[QueuePage,RecipePage,SettingsPage,AccountsPanel,DebugLauncher];
        var shown=vm.SelectedPage<0?null:pages[Math.Min(vm.SelectedPage,pages.Length-1)];
        var changed=shown?.IsVisible==false;
        foreach(var page in pages)page.IsVisible=ReferenceEquals(page,shown);
        if(ReferenceEquals(shown,DebugLauncher))UpdateWorkbenchSummary(vm);
        if(changed&&shown is not null)Avalonia.Threading.Dispatcher.UIThread.Post(()=>Controls.Motion.FadeIn(shown),Avalonia.Threading.DispatcherPriority.Loaded);
    }
    sealed record SummaryRow(string Name,string Detail,object Item);
    const int SummaryLimit=240;
    // Chain and anchor pages list what the workbench holds; editing stays in the workbench window.
    private void UpdateWorkbenchSummary(MainViewModel vm)
    {
        var document=vm.Workbench?.Document;var chains=vm.SelectedPage==4;
        WorkbenchSummaryIcon.Kind=chains?"chain":"anchor";
        WorkbenchSummaryTitle.Text=chains?"任务链":"锚点";
        var filter=WorkbenchSummaryFilter.Text?.Trim()??"";
        SummaryRow[] all=document is null?[]:chains
            ?document.Chains.Select(c=>new SummaryRow(c.Name,c.KindLabel+" · "+c.Cards.Count+" 步",c)).ToArray()
            :document.Anchors.Select(a=>new SummaryRow(a.Name,(a.PageId.Length>0?Editors.WorkbenchLabels.Page(a.PageId)+" · ":"")+a.States.Count+" 个状态",a)).ToArray();
        var matched=filter.Length==0?all:all.Where(r=>r.Name.Contains(filter,StringComparison.OrdinalIgnoreCase)||r.Detail.Contains(filter,StringComparison.OrdinalIgnoreCase)).ToArray();
        WorkbenchSummaryList.ItemsSource=matched.Take(SummaryLimit).ToArray();WorkbenchSummaryList.IsVisible=matched.Length>0;
        WorkbenchSummaryFilter.IsVisible=all.Length>0;
        WorkbenchSummaryCaption.Text=document is null?"工作台未就绪。":all.Length==0
            ?(chains?"还没有任务链，打开工作台从模板新建。":"还没有锚点，打开工作台截图创建。")
            :matched.Length==0?"没有匹配的项目。"
            :(filter.Length>0?$"匹配 {matched.Length} / {all.Length} 项":$"共 {all.Length} 项")+(matched.Length>SummaryLimit?$" · 显示前 {SummaryLimit} 项，输入名称缩小范围":"")+" · 点击任一项在工作台中打开";
    }
    private void FilterSummary(object? sender,TextChangedEventArgs e)
    {if(DataContext is MainViewModel vm&&DebugLauncher.IsVisible)UpdateWorkbenchSummary(vm);}
    private void OpenSummaryItem(object? sender,Avalonia.Input.TappedEventArgs e)
    {if((e.Source as Control)?.DataContext is SummaryRow row)OpenSummaryRow(row);}
    private void SummaryKeyDown(object? sender,KeyEventArgs e)
    {if(e.Key==Key.Enter&&WorkbenchSummaryList.SelectedItem is SummaryRow row){OpenSummaryRow(row);e.Handled=true;}}
    private void OpenSummaryRow(SummaryRow row)
    {
        if(DataContext is not MainViewModel{Workbench:{} workbench})return;
        ShowWorkbench();
        if(row.Item is Editors.WorkbenchChain chain)workbench.SelectedChain=chain;else if(row.Item is Editors.WorkbenchAnchor anchor)workbench.SelectedAnchor=anchor;
    }
    private void OpenWorkbench(object? sender,RoutedEventArgs e)=>ShowWorkbench();
    public void ShowWorkbench()
    {
        if(DataContext is not MainViewModel model||model.Workbench is null)return;
        try
        {
            model.Workbench.Category=model.SelectedPage==4?0:2;
            if(workbenchWindow is null){workbenchWindow=new(model.Workbench);workbenchWindow.Closed+=(_,_)=>workbenchWindow=null;workbenchWindow.Show(this);}else{workbenchWindow.Show();workbenchWindow.Activate();}
        }
        catch(Exception error){model.Error="工作台打开失败："+error.Message;}
    }
    public async Task<bool> ConfirmCloseAsync()
    {
        if(DataContext is not MainViewModel model)return true;
        if((model.ChainEditor?.HasUnsavedChanges==true||model.AnchorEditor?.HasUnsavedChanges==true||model.Workbench?.HasUnsavedChanges==true||model.AccountItems?.HasDirtyEdits==true)&&!await UiDialogs.Confirm(this,"任务链、锚点或道具名称尚未保存，放弃并关闭？"))return false;
        return model.SaveSettings()||await UiDialogs.Confirm(this,"设置未保存，仍要放弃修改并关闭？");
    }
    private async void ShowSponsor(object? sender,RoutedEventArgs e)=>await new SponsorWindow().ShowDialog(this);
    private void ToggleTheme(object? sender,RoutedEventArgs e)
    {
        if(DataContext is not MainViewModel model)return;
        model.ToggleThemeCommand.Execute(null);
        Controls.Motion.Rotate(ThemeToggleIcon);
    }
    private void OpenRepository(object? sender,RoutedEventArgs e)
    {try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/MinamiChiwa/MabiAgent"){UseShellExecute=true});}catch(Exception error){if(DataContext is MainViewModel model)model.Error="无法打开仓库："+error.Message;}}
    private void OpenDataDirectory(object? sender,RoutedEventArgs e)
    {if(DataContext is not MainViewModel model)return;try{System.IO.Directory.CreateDirectory(model.DataPath);System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(model.DataPath){UseShellExecute=true});}catch(Exception error){model.Error="无法打开数据目录："+error.Message;}}
    // Clicking the dimmed backdrop (not the drawer itself) closes the capture drawer.
    private void CaptureOverlayPressed(object? sender,PointerPressedEventArgs e)
    {if(ReferenceEquals(e.Source,CaptureOverlay)&&DataContext is MainViewModel model){model.CloseCaptureSettingsCommand.Execute(null);e.Handled=true;}}
    private void HandleKeys(object? sender,KeyEventArgs e)
    {if(DataContext is not MainViewModel model)return;var control=e.KeyModifiers.HasFlag(KeyModifiers.Control);var overlay=model.AddDialogOpen||model.CaptureSettingsOpen;if(e.Key==Key.M&&control&&!model.AddDialogOpen){model.IsMiniMode=true;e.Handled=true;return;}if(control&&!overlay&&e.Key==Key.N){model.SelectedPage=0;model.OpenAddCommand.Execute(null);e.Handled=true;return;}var tab=e.Key is>=Key.D1 and<=Key.D6?e.Key-Key.D1:e.Key is>=Key.NumPad1 and<=Key.NumPad6?e.Key-Key.NumPad1:-1;if(control&&!overlay&&tab>=0){model.SelectedPage=tab;Navigation.Focus();e.Handled=true;return;}if(e.Key==Key.Escape&&model.CaptureSettingsOpen){model.CloseCaptureSettingsCommand.Execute(null);e.Handled=true;return;}if(e.Key==Key.Escape&&model.AddDialogOpen){model.CloseAddCommand.Execute(null);e.Handled=true;}else if(e.Key==Key.F&&e.KeyModifiers.HasFlag(KeyModifiers.Control)&&!model.AddDialogOpen&&!model.CaptureSettingsOpen){if(model.SelectedPage==3){AccountsPanel.FocusSearch();e.Handled=true;return;}if(model.SelectedPage>=4){ShowWorkbench();workbenchWindow?.FocusSearch();e.Handled=true;return;}if(model.SelectedPage>=2)return;SearchBox.Focus();SearchBox.SelectAll();e.Handled=true;}}
}
