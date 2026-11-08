using System.Collections.ObjectModel;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MabiAgent.Core;
using MabiAgent.Windows;
using Avalonia.Threading;
using MabiAgent.Desktop.Editors;
using MabiAgent.Desktop.Accounts;
namespace MabiAgent.Desktop;
public partial class MainViewModel:ObservableObject
{
    private readonly Workspace workspace;
    private readonly RuntimeActivity activity;
    private readonly UiLanguageService language;
    private readonly GameWindowSelection windowSelection;
    private AutomationService? automation;
    private Lazy<ChainEditorViewModel>? chainEditor;
    private Lazy<AnchorEditorViewModel>? anchorEditor;
    public ChainEditorViewModel? ChainEditor {get=>chainEditor?.Value;private set=>chainEditor=value is null?null:new(()=>value);}
    public AnchorEditorViewModel? AnchorEditor {get=>anchorEditor?.Value;private set=>anchorEditor=value is null?null:new(()=>value);}
    internal bool HasUnsavedEditorChanges => chainEditor is {IsValueCreated:true}&&chainEditor.Value.HasUnsavedChanges
        ||anchorEditor is {IsValueCreated:true}&&anchorEditor.Value.HasUnsavedChanges||workbench is {IsValueCreated:true}&&workbench.Value.HasUnsavedChanges;
    internal bool CompatibilityEditorsCreated=>chainEditor?.IsValueCreated==true||anchorEditor?.IsValueCreated==true;
    private Lazy<AnchorWorkbenchViewModel>? workbench;
    public AnchorWorkbenchViewModel? Workbench {get=>workbench?.Value;private set=>workbench=value is null?null:new(()=>value);}
    internal bool WorkbenchCreated=>workbench?.IsValueCreated==true;
    public AccountItemsViewModel? AccountItems {get;private set;}
    [ObservableProperty] private string executionStatus="未开始";
    [ObservableProperty] private string captureStatus="抓包未启动";
    [ObservableProperty] private bool isRunning;
    public void AttachAutomation(AutomationService service)
    {
        automation=service;
        PauseHotkeyText=workspace.ReadSettings()["input"]?["pause_hotkey"]?.GetValue<string>()??StopHotkey.PauseDefault;PauseHotkeyStatus=service.PauseHotkeyStatus;
        StopHotkeyText=workspace.ReadSettings()["input"]?["stop_hotkey"]?.GetValue<string>()??StopHotkey.Default;StopHotkeyStatus=service.StopHotkeyStatus;
        var repository=service.Repository;workspace.CatalogProvider=()=>PublishedCatalogCompiler.CompileAvailable(repository);
        _=Task.Run(()=>{try{var cleaned=repository.CleanupStorage();if(cleaned.Revisions+cleaned.DeletedChains>0)activity.Log($"已自动清理 {cleaned.Revisions} 个旧任务链版本、{cleaned.DeletedChains} 个过期的已删除任务链");}catch(Exception e){activity.Report(e);}});
        IAnchorTextDetector? detector=null;try{detector=new OnnxLineAnchorTextDetector(service.GetOcr());}catch(Exception e){activity.Report(e);Error="文字检测未加载："+e.Message;}
        var capture=new SelectedGameCaptureService(windowSelection,new NativeWindowsOps(),()=>service.IsRunning);
        chainEditor=new(()=>new(repository,capture,detector));anchorEditor=new(()=>new(repository,capture,detector));AccountItems=new(service.Accounts,service.UpdateItemAssociationsAsync);
        if(workbench is {IsValueCreated:true})workbench.Value.Dispose();
        workbench=new(()=>new(this,repository,capture,detector,service.RunWorkbenchTestAsync));
        reserveAccounts=service.Accounts;RefreshReserveChoices();
        AccountItems.AssociationsChanged+=(_,_)=>Dispatcher.UIThread.InvokeAsync(async()=>await RefreshAsync());
        repository.CatalogChanged+=()=>{gatheringSkillsLoaded=false;Dispatcher.UIThread.InvokeAsync(async()=>await RefreshAsync());};
        OnPropertyChanged(nameof(ChainEditor));OnPropertyChanged(nameof(AnchorEditor));OnPropertyChanged(nameof(Workbench));OnPropertyChanged(nameof(AccountItems));StartCommand.NotifyCanExecuteChanged();liveTimer.Start();
        service.Changed+=()=>{if(Interlocked.Exchange(ref runtimeNotificationQueued,1)!=0)return;Dispatcher.UIThread.Post(()=>
        {try{if(!closing){StopHotkeyStatus=service.StopHotkeyStatus;PauseHotkeyStatus=service.PauseHotkeyStatus;ExecutionStatus=service.Status.Message;IsRunning=service.IsRunning;CaptureStatus=service.CaptureStatus.Message;UpdateCaptureDetails(service.CaptureStatus);UpdateRuntime();}}catch(Exception error){activity.Report(error);Error="状态显示失败："+error.Message;}finally{Interlocked.Exchange(ref runtimeNotificationQueued,0);}},DispatcherPriority.Background);};
    }
    [RelayCommand(CanExecute=nameof(CanStart))] private async Task StartAsync()
    {
        if(automation is null){ExecutionStatus="运行服务尚未初始化";return;}
        try{if(!SaveSettings())return;await automation.StartAsync();Error=null;}
        catch(Exception e){activity.Report(e);Error=e.Message;}
    }
    [RelayCommand(CanExecute=nameof(CanPause))] private async Task PauseAsync(){try{if(automation is not null)await automation.PauseAsync();Error=null;}catch(Exception e){activity.Report(e);Error=e.Message;}}
    public ObservableCollection<GameWindow> GameWindows {get;}=[];
    [ObservableProperty] private GameWindow? selectedWindow;
    [ObservableProperty] private string windowStatus="未选择窗口";
    private bool loadingWindows;
    public QueueRows Jobs {get;}=[];
    public ObservableCollection<RecipeRow> Recipes {get;}=[];
    public ObservableCollection<GatheringChoice> GatheringChoices {get;}=[];
    public ObservableCollection<GoalCategoryChoice> GoalCategories {get;}=[];
    public string[] TaskKinds {get;}=["生产","采集","挂机钓鱼","原地钓鱼"];
    [ObservableProperty] private string taskKind="生产";
    [ObservableProperty] private GatheringChoice? selectedGathering;
    [ObservableProperty] private string fishingHours="0";
    [ObservableProperty] private string fishingMinutes="30";
    [ObservableProperty] private string fishingSeconds="0";
    private GatheringChoice[] allGathering=[];
    private IReadOnlyDictionary<string,string> gatheringSkills=new Dictionary<string,string>();
    private bool gatheringSkillsLoaded;
    private bool refreshingGoalCategories;
    public bool IsProductionGoal=>TaskKind=="生产";
    public bool IsGatheringGoal=>TaskKind=="采集";
    public string GoalQuantityLabel=>IsGatheringGoal?"采集轮数":"目标产物数量";
    public string GoalQuantityUnit=>IsGatheringGoal?"轮":"件";
    public bool IsFishingGoal=>TaskKind is "挂机钓鱼" or "原地钓鱼";
    public bool IsInPlaceFishing=>TaskKind=="原地钓鱼";
    public bool NeedsGatheringPoint=>!IsProductionGoal&&!IsInPlaceFishing;
    public string GoalCategoryLabel=>IsProductionGoal?"生产设施":"采集技能";
    public bool ShowGoalCategory=>IsProductionGoal||NeedsGatheringPoint;
    [ObservableProperty] private GoalCategoryChoice? selectedGoalCategory;
    partial void OnTaskKindChanged(string value)
    {
        RefreshGoalCategories();
        if(IsProductionGoal)RecipeChoices.Refresh();
        else if(NeedsGatheringPoint)RefreshGatheringChoices(null,true);
        else{GatheringChoices.Clear();SelectedGathering=null;}
        OnPropertyChanged(nameof(IsProductionGoal));OnPropertyChanged(nameof(IsGatheringGoal));OnPropertyChanged(nameof(GoalQuantityLabel));OnPropertyChanged(nameof(GoalQuantityUnit));OnPropertyChanged(nameof(IsFishingGoal));OnPropertyChanged(nameof(IsInPlaceFishing));OnPropertyChanged(nameof(NeedsGatheringPoint));AddGoalCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedGoalCategoryChanged(GoalCategoryChoice? value)
    {
        if(refreshingGoalCategories)return;
        if(IsProductionGoal)
        {
            RecipeChoices.Refresh();
            if(SelectedRecipe is{} recipe&&!RecipeChoices.Contains(recipe))SelectedRecipe=null;
        }
        else if(NeedsGatheringPoint)RefreshGatheringChoices(null,true);
        AddProductionCommand.NotifyCanExecuteChanged();AddGoalCommand.NotifyCanExecuteChanged();
    }
    private void RefreshGoalCategories()
    {
        var previous=SelectedGoalCategory?.Key;
        GoalCategoryChoice[] categories;
        if(IsProductionGoal)categories=GoalChoices.ProductionCategories(Recipes);
        else if(NeedsGatheringPoint)categories=GoalChoices.GatheringCategories(allGathering.Where(route=>route.Fishing==IsFishingGoal),CurrentGatheringSkills());
        else categories=[];
        refreshingGoalCategories=true;
        try
        {
            GoalCategories.Clear();foreach(var category in categories)GoalCategories.Add(category);
            SelectedGoalCategory=GoalCategories.FirstOrDefault(category=>category.Key==previous)??GoalCategories.FirstOrDefault();
        }
        finally{refreshingGoalCategories=false;}
        OnPropertyChanged(nameof(GoalCategoryLabel));OnPropertyChanged(nameof(ShowGoalCategory));
    }
    private IReadOnlyDictionary<string,string> CurrentGatheringSkills()
    {
        if(gatheringSkillsLoaded)return gatheringSkills;
        try{gatheringSkills=GoalChoices.LoadGatheringSkills(workspace.Root,workspace.CatalogProvider);}
        catch{gatheringSkills=new Dictionary<string,string>();}
        gatheringSkillsLoaded=true;return gatheringSkills;
    }
    private void RefreshGatheringChoices(string? preserveId=null,bool selectFirst=false)
    {
        if(IsInPlaceFishing){GatheringChoices.Clear();SelectedGathering=null;return;}
        var route=preserveId??SelectedGathering?.Id;
        var choices=GoalChoices.FilterAndSort(allGathering.Where(choice=>choice.Fishing==IsFishingGoal),SelectedGoalCategory,CurrentGatheringSkills()).ToArray();
        if(!GatheringChoices.SequenceEqual(choices)){GatheringChoices.Clear();foreach(var choice in choices)GatheringChoices.Add(choice);}
        SelectedGathering=GatheringChoices.FirstOrDefault(choice=>choice.Id==route)??(selectFirst?GatheringChoices.FirstOrDefault():null);
        AddGoalCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedGatheringChanged(GatheringChoice? value)=>AddGoalCommand.NotifyCanExecuteChanged();
    partial void OnFishingHoursChanged(string value)=>AddGoalCommand.NotifyCanExecuteChanged();
    partial void OnFishingMinutesChanged(string value)=>AddGoalCommand.NotifyCanExecuteChanged();
    partial void OnFishingSecondsChanged(string value)=>AddGoalCommand.NotifyCanExecuteChanged();
    public DataGridCollectionView QueueView {get;}
    public DataGridCollectionView RecipeChoices {get;}
    [ObservableProperty] private string recipeSearch="";
    partial void OnRecipeSearchChanged(string value)
    {
        RecipeChoices.Refresh();
        if(SelectedRecipe is {} r&&!RecipeChoices.Contains(r))SelectedRecipe=null;
        if(SelectedGoalCategory is{} category&&!GoalCategories.Contains(category))RefreshGoalCategories();
        AddProductionCommand.NotifyCanExecuteChanged();AddGoalCommand.NotifyCanExecuteChanged();
    }
    public DataGridCollectionView AutomaticView {get;}
    [ObservableProperty] private JobRow? selectedJob;
    [ObservableProperty] private RecipeRow? selectedRecipe;
    [ObservableProperty] private string targetQuantity="1";
    [ObservableProperty] private string editedQuantity="1";
    [ObservableProperty] private bool addDialogOpen;
    [ObservableProperty] private string? addError;
    [ObservableProperty] private int selectedPage;
    [ObservableProperty] private string uiLanguageCode=UiLanguageService.SimplifiedChinese;
    public string LanguageButtonLabel=>language.Culture==UiLanguageService.TraditionalChinese?"繁中":"简中";
    public string LanguageButtonTooltip=>$"切换界面语言；当前为{language.LanguageName}";
    public string PageTitle=>SelectedPage switch{1=>"仓库",2=>"设置",_=>"任务队列"};
    public string PageDescription=>SelectedPage switch{1=>"按账户查看角色与仓库数量，确认道具名称关联。",2=>"设置自动维护、材料留存、外观、数据监听与高级工作台。",_=>"安排任务，查看进度、自动补料和待确认的操作结果。"};
    public bool ShowGlobalSearch=>SelectedPage==0;
    public string SearchHint=>"搜索目标名称或状态";
    public bool CanStart=>automation is not null&&SelectedWindow is not null&&!IsBusy&&!IsRunning&&!closing;
    public bool CanPause=>automation?.IsRunning==true&&!closing;
    [RelayCommand] private async Task StopAllAsync()
    {
        if(automation is null||closing)return;
        try{await automation.StopAllAsync();await RefreshAsync();Error=null;}
        catch(Exception e){activity.Report(e);Error=e.Message;}
    }
    private bool closing;
    public void PrepareClose(){closing=true;if(workbench is {IsValueCreated:true})workbench.Value.Dispose();saveTimer.Stop();liveTimer.Stop();language.LanguageChanged-=LanguageChanged;StartCommand.NotifyCanExecuteChanged();PauseCommand.NotifyCanExecuteChanged();}
    /// <summary>
    /// Persists pending settings before a self-update closes the app; an invalid input keeps the
    /// reason on screen and refuses. Queue state needs no flush — <c>RuntimeQueue.Edit</c>
    /// writes it atomically on every change.
    /// </summary>
    public bool PrepareUpdate() => SaveSettings();
    partial void OnIsRunningChanged(bool value){StartCommand.NotifyCanExecuteChanged();PauseCommand.NotifyCanExecuteChanged();}
    partial void OnSelectedPageChanged(int value){OnPropertyChanged(nameof(PageTitle));OnPropertyChanged(nameof(PageDescription));OnPropertyChanged(nameof(ShowGlobalSearch));OnPropertyChanged(nameof(SearchHint));}
    private void LanguageChanged(object? sender,EventArgs e)
    {
        UiLanguageCode=language.Culture;OnPropertyChanged(nameof(LanguageButtonLabel));OnPropertyChanged(nameof(LanguageButtonTooltip));
        if(chainEditor is {IsValueCreated:true})chainEditor.Value.RefreshLanguage();
        OnPropertyChanged(nameof(PageTitle));OnPropertyChanged(nameof(PageDescription));OnPropertyChanged(nameof(SearchHint));OnPropertyChanged(string.Empty);
    }
    [RelayCommand] private void UseSimplifiedChinese()=>ChangeLanguage(UiLanguageService.SimplifiedChinese);
    [RelayCommand] private void UseTraditionalChinese()=>ChangeLanguage(UiLanguageService.TraditionalChinese);
    public void ChangeLanguage(string culture)
    {
        var normalized=UiLanguageService.Normalize(culture);if(normalized is null)return;language.SetLanguage(normalized);UiLanguageCode=normalized;
        try{workspace.SaveUiLanguage(normalized);SettingsError=null;SettingsStatus="语言偏好已保存";}
        catch(Exception e){activity.Report(e);SettingsError=e.Message;SettingsStatus="语言偏好保存失败："+e.Message;}
    }
    
    public int ManualCount=>Jobs.Count(j=>!j.Automatic);
    public bool HasSelection=>SelectedJob is not null;
    public bool CanEditQuantity=>SelectedJob is {Automatic:false,Pending:false,Cancelled:false}&&!IsBusy;
    public bool CanAdd=>!IsBusy&&SelectedRecipe is not null&&RecipeChoices.Contains(SelectedRecipe)&&FormNumbers.TryWhole(TargetQuantity,1,FormNumbers.Maximum,out _);
    public bool CanSaveQuantity=>CanEditQuantity&&TryEditedTarget(out _);
    public string? QuantityError=>CanEditQuantity&&!CanSaveQuantity?$"请输入不低于已确认{SelectedJob?.QuantityUnit}数的正整数":null;
    public string? TargetError=>IsFishingGoal?null:FormNumbers.TryWhole(TargetQuantity,1,FormNumbers.Maximum,out _)?null:"请输入有效的正整数";
    partial void OnTargetQuantityChanged(string value){AddProductionCommand.NotifyCanExecuteChanged();AddGoalCommand.NotifyCanExecuteChanged();OnPropertyChanged(nameof(TargetError));}
    partial void OnEditedQuantityChanged(string value){SaveQuantityCommand.NotifyCanExecuteChanged();OnPropertyChanged(nameof(QuantityError));}
    partial void OnSelectedRecipeChanged(RecipeRow? value){AddProductionCommand.NotifyCanExecuteChanged();AddGoalCommand.NotifyCanExecuteChanged();}
    [ObservableProperty] private string operationStatus="";
    public int AutomaticCount=>Jobs.Count(j=>j.Automatic);
    [ObservableProperty] private string search="";
    [ObservableProperty] private string status="尚未加载";
    [ObservableProperty] private string? error;
    [ObservableProperty] private bool repairEnabled;
    [ObservableProperty] private bool autoBuyBottles;
    [ObservableProperty] private string repairMinutes="30";
    [ObservableProperty] private string goldReserve="40000";
    [ObservableProperty] private bool backpackEnabled;
    [ObservableProperty] private string backpackThreshold="90";
    [ObservableProperty] private bool sellSurplus;
    [ObservableProperty] private string materialReserve="300";
    [ObservableProperty] private bool autoUseWings;
    [ObservableProperty] private string wingReserve="0";
    [ObservableProperty] private bool settingsDirty;
    [ObservableProperty] private string? settingsError;
    [ObservableProperty] private string settingsStatus="";
    private bool loadingSettings;
    [ObservableProperty] private bool isBusy;
    public bool CanEdit=>!IsBusy;
    partial void OnIsBusyChanged(bool value){OnPropertyChanged(nameof(CanEdit));OnPropertyChanged(nameof(CanBatchEdit));StartCommand.NotifyCanExecuteChanged();UpdateActions();}
    private readonly DispatcherTimer liveTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly DispatcherTimer saveTimer=new(){Interval=TimeSpan.FromMilliseconds(350)};
    public string DataPath=>workspace.DataPath;
    public MainViewModel(Workspace workspace,GameWindowSelection windowSelection,UiLanguageService? language=null)
    {
        this.windowSelection=windowSelection;
        this.workspace=workspace;this.language=language??UiLanguageService.Shared;UiLanguageCode=this.language.Culture;activity=new(workspace.DataPath);QueueView=new DataGridCollectionView(Jobs);AutomaticView=new DataGridCollectionView(Jobs);RecipeChoices=new DataGridCollectionView(Recipes);
        this.language.LanguageChanged+=LanguageChanged;
        RecipeChoices.Filter=x=>x is RecipeRow r&&GoalChoices.Matches(r,SelectedGoalCategory)&&(r.Name.Contains(RecipeSearch,StringComparison.OrdinalIgnoreCase)||r.Device.Contains(RecipeSearch,StringComparison.OrdinalIgnoreCase));
        RecipeChoices.SortDescriptions.Add(DataGridSortDescription.FromComparer(GoalChoices.RecipeComparer));
        RefreshGoalCategories();
        saveTimer.Tick+=(_,_)=>SaveSettings();
        liveTimer.Tick+=async(_,_)=>await RefreshLiveQueueAsync();
        bool Match(JobRow j)=>j.Name.Contains(Search,StringComparison.OrdinalIgnoreCase)||j.Status.Contains(Search);
        QueueView.Filter=x=>x is JobRow j&&!j.Automatic&&Match(j);
        AutomaticView.Filter=x=>x is JobRow j&&j.Automatic&&Match(j);
    }
    partial void OnSearchChanged(string value)
    {
        QueueView.Refresh();AutomaticView.Refresh();
        if(SelectedJob is {} j&&!(j.Automatic?AutomaticView:QueueView).Contains(j))SelectedJob=null;
        SetQueueSelection(Jobs.Where(j=>SelectedJobIds.Contains(j.Id)&&(j.Automatic?AutomaticView:QueueView).Contains(j)).ToArray());
    }
    partial void OnSelectedJobChanged(JobRow? value){if(value is not null){EditedQuantity=value.Target.ToString();EditedHours=(value.Target/3600).ToString();EditedMinutes=(value.Target%3600/60).ToString();EditedSeconds=(value.Target%60).ToString();}OnPropertyChanged(nameof(IsFishingSelection));OnPropertyChanged(nameof(IsQuantitySelection));UpdateActions();}
    private void UpdateActions()
    {
        OnPropertyChanged(nameof(HasSelection));OnPropertyChanged(nameof(CanEditQuantity));OnPropertyChanged(nameof(QuantityError));
        AddProductionCommand.NotifyCanExecuteChanged();AddGoalCommand.NotifyCanExecuteChanged();SaveQuantityCommand.NotifyCanExecuteChanged();ControlCommand.NotifyCanExecuteChanged();MoveCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedWindowChanged(GameWindow? value)
    {
        StartCommand.NotifyCanExecuteChanged();if(loadingWindows)return;
        try{windowSelection.Select(value);WindowStatus=value is null?"请选择游戏窗口":"窗口已连接";}
        catch(Exception e){activity.Report(e);WindowStatus=e.Message;}
    }
    [RelayCommand] public async Task RefreshWindowsAsync()
    {
        try
        {
            await Task.Run(windowSelection.Refresh);
            loadingWindows=true;
            try{GameWindows.Clear();foreach(var w in windowSelection.Available)GameWindows.Add(w);SelectedWindow=windowSelection.Selected;}
            finally{loadingWindows=false;}
            WindowStatus=SelectedWindow is not null?"已选择游戏窗口":GameWindows.Count==0?"未找到游戏窗口":"检测到多个游戏窗口，请选择";
        }
        catch(Exception e){activity.Report(e);WindowStatus="窗口读取失败："+e.Message;}
    }
    partial void OnRepairEnabledChanged(bool value)=>ScheduleSettings();
    partial void OnAutoBuyBottlesChanged(bool value)=>ScheduleSettings();
    partial void OnRepairMinutesChanged(string value)=>ScheduleSettings();
    partial void OnGoldReserveChanged(string value)=>ScheduleSettings();
    partial void OnBackpackEnabledChanged(bool value)=>ScheduleSettings();
    partial void OnBackpackThresholdChanged(string value)=>ScheduleSettings();
    partial void OnSellSurplusChanged(bool value)=>ScheduleSettings();
    partial void OnMaterialReserveChanged(string value)=>ScheduleSettings();
    partial void OnAutoUseWingsChanged(bool value)=>ScheduleSettings();
    partial void OnWingReserveChanged(string value)=>ScheduleSettings();
    private void ScheduleSettings(){if(loadingSettings)return;SettingsDirty=true;saveTimer.Stop();saveTimer.Start();SettingsStatus="等待保存";}
    [RelayCommand] private void RetrySettings()=>SaveSettings();
    public bool SaveSettings()
    {
        saveTimer.Stop();if(!SettingsDirty)return true;
        try
        {
            if(!FormNumbers.TryWhole(RepairMinutes,1,1440,out var minutes))throw new ArgumentException("修理间隔请输入 1–1440 分钟");
            if(!FormNumbers.TryWhole(GoldReserve,0,FormNumbers.Maximum,out var gold))throw new ArgumentException("金币留存请输入非负整数");
            if(!FormNumbers.TryWhole(BackpackThreshold,1,100,out var threshold))throw new ArgumentException("背包阈值请输入 1–100%");
            if(!FormNumbers.TryWhole(MaterialReserve,0,FormNumbers.Maximum,out var material))throw new ArgumentException("材料留存请输入非负整数");
            if(!FormNumbers.TryWhole(WingReserve,0,FormNumbers.Maximum,out var wings))throw new ArgumentException("精灵之翼留存请输入非负整数");
            workspace.SavePreferences(new(RepairEnabled,(int)minutes,gold,AutoBuyBottles,BackpackEnabled,(int)threshold,SellSurplus,material,AutoUseWings,wings,MaterialReserves.ToDictionary(x=>x.Id,x=>x.Quantity),ThemeKey,RecognitionLowCpu,InputScheme,TouchCenterOffset,LifeVip,UiLanguageCode));
            SettingsDirty=false;SettingsError=null;SettingsStatus="已自动保存";return true;
        }
        catch(Exception e){activity.Fault(e,nameof(SaveSettings),scope:"settings");SettingsError=e.Message;SettingsStatus="未保存，输入已保留";return false;}
    }
    [RelayCommand] private void ReloadSettings()
    {
        try{LoadSettings();SettingsDirty=false;SettingsError=null;saveTimer.Stop();}
        catch(Exception e){activity.Report(e);SettingsError=e.Message;}
    }
    private void LoadSettings()
    {
        var p=DesktopPreferences.FromJson(workspace.ReadSettings());loadingSettings=true;
        try
        {
            LifeVip=p.LifeVip;RepairEnabled=p.RepairEnabled;RepairMinutes=p.RepairMinutes.ToString();GoldReserve=p.GoldReserve.ToString();AutoBuyBottles=p.AutoBuyBottles;
            BackpackEnabled=p.BackpackEnabled;BackpackThreshold=p.BackpackThreshold.ToString();SellSurplus=p.SellSurplus;MaterialReserve=p.MaterialReserve.ToString();
            AutoUseWings=p.AutoUseWings;WingReserve=p.WingReserve.ToString();RecognitionLowCpu=p.RecognitionLowCpu;InputScheme=p.InputScheme;TouchCenterOffset=p.TouchCenterOffset;LoadExtraSettings(p);SettingsStatus="已读取";
        }
        finally{loadingSettings=false;}
    }
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if(IsBusy||refreshingQueue)return;IsBusy=true;
        try
        {
            Status="正在读取现有配置与队列";Error=null;
            var saved=SaveSettings();
            var data=await Task.Run(()=>workspace.Read(true));
            if(saved)LoadSettings();
            Apply(data);
            Status=$"{Jobs.Count} 条目标 · {Recipes.Count} 个配方";
        }
        catch(Exception e){activity.Fault(e,nameof(RefreshAsync),scope:"ui");Error=e.Message;Status="读取失败："+e.Message;}
        finally{IsBusy=false;}
    }
    private string? lastQueueStamp;
    private bool refreshingQueue;private int runtimeNotificationQueued;
    internal bool IsApplyingQueue{get;private set;}
    private string? accountStamp;
    private string? queueRefreshError;
    internal async Task RefreshLiveQueueAsync()
    {
        var reading=false;
        try
        {
        UpdateRuntime();
        if(SelectedPage==2)RefreshReserveChoices();
        var inventory=automation?.CaptureStatus.Inventory;
        var nextStamp=inventory?.Scope?.SessionId+":"+inventory?.Revision+":"+inventory?.VerifiedThrough.HasValue+":"+automation?.Accounts.Version;
        if(nextStamp!=accountStamp&&SelectedPage==1&&!IsBusy&&!closing&&AccountItems?.IsCellEditing!=true){accountStamp=nextStamp;await AccountItems!.RefreshLiveAsync(nextStamp);}
        if(IsBusy||closing||refreshingQueue)return;
        var path=System.IO.Path.Combine(DataPath,"work-queue.json");
        var info=new System.IO.FileInfo(path);var stamp=info.Exists?$"{info.LastWriteTimeUtc.Ticks}:{info.Length}":"missing";
        if(stamp==lastQueueStamp)return;
        refreshingQueue=true;reading=true;
        Apply(await Task.Run(()=>workspace.Read()),true);lastQueueStamp=stamp;Status=$"{Jobs.Count} 条目标 · {Recipes.Count} 个配方";
        if(Error==queueRefreshError)Error=null;queueRefreshError=null;
        }
        catch(Exception e)
        {
            var message="队列刷新失败："+e.Message;
            if(message!=queueRefreshError)activity.Fault(e,nameof(RefreshLiveQueueAsync),scope:"ui");if(message!=queueRefreshError)activity.Log("队列刷新异常："+e);
            queueRefreshError=message;Error=message;
        }
        finally{if(reading)refreshingQueue=false;}
    }
    internal void Apply(WorkspaceSnapshot data,bool preserveDraft=false)
    {
        var selectedId=SelectedJob?.Id;var recipeId=SelectedRecipe?.Id;
        var draft=(EditedQuantity,EditedHours,EditedMinutes,EditedSeconds);var selectedIds=SelectedJobIds.ToArray();
        if(data.Jobs.Select(j=>j.Id).Distinct().Count()!=data.Jobs.Length)throw new InvalidDataException("队列含重复任务，保留当前显示");
        IsApplyingQueue=true;
        try
        {
            var refreshViews=Jobs.Count==data.Jobs.Length&&Jobs.Zip(data.Jobs).Any(pair=>pair.First.Id==pair.Second.Id&&
                (pair.First.Automatic!=pair.Second.Automatic||Search.Length>0&&(pair.First.Name!=pair.Second.Name||pair.First.Status!=pair.Second.Status)
                ||(QueueView.SortDescriptions.Count>0||AutomaticView.SortDescriptions.Count>0)));
            Jobs.ApplySnapshot(data.Jobs);
            if(refreshViews){QueueView.Refresh();AutomaticView.Refresh();}
            SelectedJob=Jobs.FirstOrDefault(j=>j.Id==selectedId);
            OnPropertyChanged(nameof(SelectedJob));
            if(preserveDraft&&SelectedJob is not null&&SelectedJob.Id==selectedId){(EditedQuantity,EditedHours,EditedMinutes,EditedSeconds)=draft;}
        }
        finally{IsApplyingQueue=false;}
        SetQueueSelection(Jobs.Where(j=>selectedIds.Contains(j.Id)&&(j.Automatic?AutomaticView:QueueView).Contains(j)));
        if(SelectedJob is{} visible&&!(visible.Automatic?AutomaticView:QueueView).Contains(visible))SelectedJob=null;
        OnPropertyChanged(nameof(SelectedJob));
        var timedRecipes=data.Recipes.Select(r=>r with{LifeVip=LifeVip}).ToArray();
        if(!Recipes.SequenceEqual(timedRecipes)){Recipes.Clear();foreach(var recipe in timedRecipes)Recipes.Add(recipe);RefreshGoalCategories();RecipeChoices.Refresh();}
        var nextGathering=data.Gathering??[];if(!allGathering.SequenceEqual(nextGathering)){var route=SelectedGathering?.Id;allGathering=nextGathering;gatheringSkillsLoaded=false;RefreshGoalCategories();RefreshGatheringChoices(route,true);}
        SelectedRecipe=Recipes.FirstOrDefault(r=>r.Id==recipeId&&RecipeChoices.Contains(r))??RecipeChoices.Cast<RecipeRow>().FirstOrDefault();
        if(Jobs.Any(j=>j.Status=="任务链需更新"))Error="部分旧任务链与当前预设不兼容，已保留并隔离；其他任务可继续。请在设置的高级工作台中更新身份锚点。";
        Status=$"{Jobs.Count} 条目标 · {Recipes.Count} 个配方";
        OnPropertyChanged(nameof(AutomaticCount));
        OnPropertyChanged(nameof(ManualCount));
        UpdateRuntime();
    }
    private async Task<bool> ChangeAsync(Action change,string message)
    {
        while((refreshingQueue||IsBusy)&&!closing)await Task.Delay(10);
        if(closing)return false;IsBusy=true;
        try
        {
            if(automation is not null)await automation.MutateQueueAsync(change);
            else await Task.Run(change);
            var data=await Task.Run(()=>workspace.Read());
            Apply(data);OperationStatus=message;Error=null;return true;
        }
        catch(Exception e){activity.Fault(e,nameof(ChangeAsync),scope:"queue");activity.Log("队列操作异常："+e);Error=e.Message;OperationStatus="未完成："+e.Message;return false;}
        finally{IsBusy=false;}
    }
    public bool CanAddGoal=>!IsBusy&&(IsProductionGoal?CanAdd:(IsInPlaceFishing||SelectedGathering is not null)&&(IsFishingGoal?TryFishingDuration(out _):FormNumbers.TryWhole(TargetQuantity,1,FormNumbers.Maximum,out _)));
    bool TryFishingDuration(out long seconds)
    {seconds=0;if(!long.TryParse(FishingHours,out var h)||h<0||h>8760||!int.TryParse(FishingMinutes,out var m)||m is <0 or >59||!int.TryParse(FishingSeconds,out var s)||s is <0 or >59)return false;seconds=h*3600+m*60+s;return seconds>0;}
    [RelayCommand(CanExecute=nameof(CanAddGoal))] private async Task AddGoalAsync()
    {if(!CanAddGoal)return;if(IsProductionGoal){await AddProductionAsync();return;}var route=IsInPlaceFishing?MabiAgent.Workflows.FishingPlan.CurrentLocation:SelectedGathering?.Id;if(route is null)return;long quantity;if(IsFishingGoal){if(!TryFishingDuration(out quantity))return;}else if(!FormNumbers.TryWhole(TargetQuantity,1,FormNumbers.Maximum,out quantity))return;if(await ChangeAsync(()=>workspace.AddGathering(IsFishingGoal?"idle_fishing":"gathering",route,quantity),IsInPlaceFishing?"已加入原地钓鱼目标":IsFishingGoal?"已加入挂机钓鱼目标":"已加入采集目标"))AddDialogOpen=false;else AddError=Error;}
    public void InitializeEditorSelfCheck(EditorRepository repository,MabiAgent.Capture.AccountItemService accounts)
    {workspace.CatalogProvider=()=>PublishedCatalogCompiler.CompileAvailable(repository);gatheringSkillsLoaded=false;ChainEditor=new(repository);AnchorEditor=new(repository);Workbench=new(this,repository);AccountItems=new(accounts);reserveAccounts=accounts;RefreshReserveChoices();OnPropertyChanged(nameof(ChainEditor));OnPropertyChanged(nameof(AnchorEditor));OnPropertyChanged(nameof(Workbench));OnPropertyChanged(nameof(AccountItems));}
    [RelayCommand] private void OpenAdd(){AddError=null;AddDialogOpen=true;}
    [RelayCommand] private void CloseAdd(){if(!IsBusy)AddDialogOpen=false;}
    [RelayCommand(CanExecute=nameof(CanAdd))] private async Task AddProductionAsync()
    {
        var recipe=SelectedRecipe;if(!CanAdd||recipe is null||!FormNumbers.TryWhole(TargetQuantity,1,FormNumbers.Maximum,out var quantity))return;
        if(await ChangeAsync(()=>workspace.AddProduction(recipe.Id,quantity),"已加入生产目标"))AddDialogOpen=false;
        else AddError=Error;
    }
    [RelayCommand(CanExecute=nameof(CanSaveQuantity))] private Task SaveQuantityAsync()
    {
        var job=SelectedJob;
        return job is null||!CanEditQuantity||!TryEditedTarget(out var quantity)?Task.CompletedTask:ChangeAsync(()=>workspace.EditQueue(q=>q.SetTargetQuantity(job.Id,quantity)),"目标计划已保存");
    }
    private bool CanControl(string command)=>!IsBusy&&SelectedJob is {} j&&!j.Cancelled&&(command switch{"pause"=>!j.Paused,"continue"=>j.Paused,"cancel"=>true,_=>false});
    [RelayCommand(CanExecute=nameof(CanControl))] private Task ControlAsync(string command)
    {
        var job=SelectedJob;if(job is null)return Task.CompletedTask;
        if(command is not ("pause" or "continue" or "cancel"))return Task.CompletedTask;
        var message=command switch{"pause"=>"目标已暂停","continue"=>"目标已解除暂停；按开始／继续执行",_=>job.Automatic?"补料已取消，相关目标已暂停":job.Pending?"已取消后续任务，正在结束当前批次":"任务已取消"};
        return ChangeAsync(()=>workspace.ControlJob(job.Id,command),message);
    }
    private bool CanMove=>!IsBusy&&HasSelection;
    [RelayCommand(CanExecute=nameof(CanMove))] private Task MoveAsync(string direction)
    {
        var job=SelectedJob;if(job is null)return Task.CompletedTask;
        var step=direction=="up"?-1:1;var view=job.Automatic?AutomaticView:QueueView;
        if(view.SortDescriptions.Count>0){OperationStatus="请先清除表头排序，再调整执行顺序";return Task.CompletedTask;}
        var visible=view.Cast<JobRow>().ToArray();var at=Array.FindIndex(visible,j=>j.Id==job.Id)+step;
        if(at<0||at>=visible.Length)return Task.CompletedTask;
        return ChangeAsync(()=>workspace.EditQueue(q=>q.Move(job.Id,step,visible[at].Id)),"执行顺序已保存");
    }
    [RelayCommand] private void ResetSort(){QueueView.SortDescriptions.Clear();AutomaticView.SortDescriptions.Clear();OperationStatus="已恢复执行顺序";}
}





