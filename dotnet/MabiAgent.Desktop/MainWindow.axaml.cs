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
        SizeChanged+=(_,_)=>{UpdateResponsiveLayout();Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTabIndicator,Avalonia.Threading.DispatcherPriority.Loaded);};
        Opened+=(_,_)=>{Controls.WindowSizing.FitToWorkingArea(this);UpdateResponsiveLayout();UpdateTabIndicator();};
        model.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(model.IsMiniMode))ApplyMiniMode(model);if(e.PropertyName==nameof(model.SelectedPage))UpdatePage();if(e.PropertyName==nameof(model.AddDialogOpen))Avalonia.Threading.Dispatcher.UIThread.Post(()=>{if(model.AddDialogOpen){Controls.Motion.FadeIn(AddOverlay);Controls.Motion.PopIn(AddGoalCard);TaskKindPicker.Focus();}else Navigation.Focus();});
            if(e.PropertyName==nameof(model.CaptureSettingsOpen)&&model.CaptureSettingsOpen)Avalonia.Threading.Dispatcher.UIThread.Post(()=>{Controls.Motion.FadeIn(CaptureOverlay);Controls.Motion.SlideIn(CaptureDrawer,48);CaptureDrawer.Focus();},Avalonia.Threading.DispatcherPriority.Loaded);};UpdatePage();
        SettingsPage.OpenWorkbenchRequested+=HandleOpenWorkbenchRequested;
        SettingsPage.PageShown+=HandleSettingsPageShown;
        model.UpdateSectionRequested=RequestUpdateSection;
        Closed+=(_,_)=>{SettingsPage.OpenWorkbenchRequested-=HandleOpenWorkbenchRequested;SettingsPage.PageShown-=HandleSettingsPageShown;model.UpdateSectionRequested=null;guideWindow?.Close();miniWindow?.CloseWithOwner();workbenchWindow?.CloseWithOwner();model.PrepareClose();model.ReleaseUpdates();};
        model.LoadUpdatePreferences();
        model.ShutdownRequested??=()=>Avalonia.Threading.Dispatcher.UIThread.Post(Close);
        if(model.AutoCheckUpdates)Avalonia.Threading.Dispatcher.UIThread.Post(async()=>await model.CheckUpdateAsync(),Avalonia.Threading.DispatcherPriority.Background);
    }
    internal void UpdateResponsiveLayout(Size? viewport=null)
    {
        var size=viewport??Bounds.Size;
        var compact=size.Width<1120;
        GitHubButton.IsVisible=!compact;SponsorButton.IsVisible=!compact;
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
    private void NavigationChanged(object? sender,SelectionChangedEventArgs e)
    {Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTabIndicator,Avalonia.Threading.DispatcherPriority.Loaded);}
    private void UpdateTabIndicator()
    {
        if(Navigation?.SelectedItem is not ListBoxItem item||TabIndicator is null||TabTrack is null||item.Bounds.Width<=0)return;
        if(item.TranslatePoint(default,TabTrack) is not {} origin)return;
        if(TabIndicator.Width!=item.Bounds.Width)TabIndicator.Width=item.Bounds.Width;
        if(TabIndicator.Height!=item.Bounds.Height)TabIndicator.Height=item.Bounds.Height;
        if(TabIndicator.RenderTransform is Avalonia.Media.TranslateTransform slide&&slide.X!=origin.X)slide.X=origin.X;
    }
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
    Control? visiblePage;
    private void UpdatePage()
    {
        if(DataContext is not MainViewModel vm)return;
        Control[] pages=[QueuePage,AccountsPanel,SettingsPage];
        Control? shown=vm.SelectedPage switch{0=>QueuePage,1=>AccountsPanel,2=>SettingsPage,_=>null};
        if(!ReferenceEquals(visiblePage,shown))
        {
            if(visiblePage is not null)visiblePage.IsVisible=false;
            else foreach(var page in pages)if(!ReferenceEquals(page,shown))page.IsVisible=false;
            if(shown is not null)shown.IsVisible=true;
            visiblePage=shown;
        }
    }
    private void HandleOpenWorkbenchRequested(object? sender,EventArgs e)=>ShowWorkbench();
    bool updateSectionPending;
    private void RequestUpdateSection()
    {
        if(DataContext is not MainViewModel model)return;
        updateSectionPending=true;
        model.SelectedPage=2;UpdatePage();
        Avalonia.Threading.Dispatcher.UIThread.Post(RevealRequestedUpdateSection,Avalonia.Threading.DispatcherPriority.Loaded);
    }
    private void HandleSettingsPageShown(object? sender,EventArgs e)
    {
        if(updateSectionPending)Avalonia.Threading.Dispatcher.UIThread.Post(RevealRequestedUpdateSection,Avalonia.Threading.DispatcherPriority.Loaded);
    }
    private void RevealRequestedUpdateSection()
    {
        if(!updateSectionPending||DataContext is not MainViewModel{SelectedPage:2}||!SettingsPage.IsVisible)return;
        updateSectionPending=false;SettingsPage.ShowUpdateSection();
    }
    public void ShowWorkbench()
    {
        if(DataContext is not MainViewModel model||model.Workbench is null)return;
        try
        {
            if(workbenchWindow is null){workbenchWindow=new(model.Workbench);workbenchWindow.Closed+=(_,_)=>workbenchWindow=null;workbenchWindow.Show(this);}else{workbenchWindow.Show();workbenchWindow.Activate();}
        }
        catch(Exception error){model.Error="工作台打开失败："+error.Message;}
    }
    public async Task<bool> ConfirmCloseAsync()
    {
        if(DataContext is not MainViewModel model)return true;
        if((model.HasUnsavedEditorChanges||model.AccountItems?.HasDirtyEdits==true)&&!await UiDialogs.Confirm(this,"任务链、锚点或道具名称尚未保存，放弃并关闭？"))return false;
        return model.SaveSettings()||await UiDialogs.Confirm(this,"设置未保存，仍要放弃修改并关闭？");
    }
    private async void ShowSponsor(object? sender,RoutedEventArgs e)=>await new SponsorWindow().ShowDialog(this);
    private void ToggleTheme(object? sender,RoutedEventArgs e)
    {
        if(DataContext is not MainViewModel model)return;
        model.ToggleThemeCommand.Execute(null);
        Controls.Motion.Rotate(ThemeToggleIcon);
    }
    UserGuideWindow? guideWindow;
    internal UserGuideWindow? Guide => guideWindow;
    private void OpenUserGuide(object? sender,RoutedEventArgs e) => ShowUserGuide();
    internal void ShowUserGuide()
    {
        if(DataContext is not MainViewModel model)return;
        if(guideWindow is null)
        {
            guideWindow=new UserGuideWindow(model);
            guideWindow.Closed+=(_,_)=>guideWindow=null;
            guideWindow.Show(this);
        }
        else { guideWindow.Show(); guideWindow.Activate(); }
    }
    private void OpenRepository(object? sender,RoutedEventArgs e)
    {try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/MinamiChiwa/MabiAgent"){UseShellExecute=true});}catch(Exception error){if(DataContext is MainViewModel model)model.Error="无法打开仓库："+error.Message;}}
    private void OpenDataDirectory(object? sender,RoutedEventArgs e)
    {if(DataContext is not MainViewModel model)return;try{System.IO.Directory.CreateDirectory(model.DataPath);System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(model.DataPath){UseShellExecute=true});}catch(Exception error){model.Error="无法打开数据目录："+error.Message;}}
    // Clicking the dimmed backdrop (not the drawer itself) closes the capture drawer.
    private void CaptureOverlayPressed(object? sender,PointerPressedEventArgs e)
    {if(ReferenceEquals(e.Source,CaptureOverlay)&&DataContext is MainViewModel model){model.CloseCaptureSettingsCommand.Execute(null);e.Handled=true;}}
    private void HandleKeys(object? sender,KeyEventArgs e)
    {if(DataContext is not MainViewModel model)return;var control=e.KeyModifiers.HasFlag(KeyModifiers.Control);var overlay=model.AddDialogOpen||model.CaptureSettingsOpen;if(e.Key==Key.M&&control&&!model.AddDialogOpen){model.IsMiniMode=true;e.Handled=true;return;}if(control&&!overlay&&e.Key==Key.N){model.SelectedPage=0;model.OpenAddCommand.Execute(null);e.Handled=true;return;}var tab=e.Key is>=Key.D1 and<=Key.D3?e.Key-Key.D1:e.Key is>=Key.NumPad1 and<=Key.NumPad3?e.Key-Key.NumPad1:-1;if(control&&!overlay&&tab>=0){model.SelectedPage=tab;Navigation.Focus();e.Handled=true;return;}if(e.Key==Key.Escape&&model.CaptureSettingsOpen){model.CloseCaptureSettingsCommand.Execute(null);e.Handled=true;return;}if(e.Key==Key.Escape&&model.AddDialogOpen){model.CloseAddCommand.Execute(null);e.Handled=true;}else if(e.Key==Key.F&&e.KeyModifiers.HasFlag(KeyModifiers.Control)&&!model.AddDialogOpen&&!model.CaptureSettingsOpen){if(model.SelectedPage==1){AccountsPanel.FocusSearch();e.Handled=true;return;}if(model.SelectedPage!=0)return;SearchBox.Focus();SearchBox.SelectAll();e.Handled=true;}}
}
