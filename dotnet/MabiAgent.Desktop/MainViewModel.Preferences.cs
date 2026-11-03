using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Styling;
namespace MabiAgent.Desktop;
public sealed record ReserveChoice(string Id,string Name);
public sealed record ReserveRow(string Id,string Name,long Quantity);
public sealed record ThemeChoice(string Key,string Label);
public partial class MainViewModel
{
    public ObservableCollection<ReserveChoice> ReserveChoices {get;}=[];
    public ObservableCollection<ReserveRow> MaterialReserves {get;}=[];
    public ThemeChoice[] ThemeChoices {get;}=[new("system","跟随系统"),new("light","浅色"),new("dark","深色")];
    public ThemeChoice[] InputChoices {get;}=[new("touch","后台触控（默认）"),new("mouse","前台鼠标")];
    [ObservableProperty] private bool lifeVip;
    partial void OnLifeVipChanged(bool value)
    {
        var selected=SelectedRecipe?.Id;var catalogSelected=CatalogSelectedRecipe?.Id;
        for(var i=0;i<Recipes.Count;i++)Recipes[i]=Recipes[i] with{LifeVip=value};
        SelectedRecipe=Recipes.FirstOrDefault(r=>r.Id==selected);CatalogSelectedRecipe=Recipes.FirstOrDefault(r=>r.Id==catalogSelected);
        ScheduleSettings();
    }
    [ObservableProperty] private string inputScheme="touch";
    [ObservableProperty] private bool touchCenterOffset=true;
    public bool IsTouchInput=>InputScheme=="touch";
    partial void OnInputSchemeChanged(string value){OnPropertyChanged(nameof(IsTouchInput));ScheduleSettings();}
    partial void OnTouchCenterOffsetChanged(bool value)=>ScheduleSettings();
    [ObservableProperty] private string themeKey="system";
    [ObservableProperty] private bool recognitionLowCpu;
    [ObservableProperty] private ReserveChoice? selectedReserveItem;
    [ObservableProperty] private ReserveRow? selectedReserve;
    [ObservableProperty] private string reserveQuantity="300";
    [ObservableProperty] private string editedHours="0";
    [ObservableProperty] private string editedMinutes="0";
    [ObservableProperty] private string editedSeconds="0";
    public bool IsFishingSelection=>SelectedJob?.IsFishing==true;
    public bool IsQuantitySelection=>SelectedJob is {IsFishing:false};
    partial void OnEditedHoursChanged(string value)=>EditDurationChanged();
    partial void OnEditedMinutesChanged(string value)=>EditDurationChanged();
    partial void OnEditedSecondsChanged(string value)=>EditDurationChanged();
    void EditDurationChanged(){SaveQuantityCommand.NotifyCanExecuteChanged();OnPropertyChanged(nameof(QuantityError));}
    bool TryEditedTarget(out long value)
    {value=0;if(!IsFishingSelection)return FormNumbers.TryWhole(EditedQuantity,Math.Max(1,SelectedJob?.Completed??0),FormNumbers.Maximum,out value);
    if(!FormNumbers.TryWhole(EditedHours,0,8760,out var h)||!FormNumbers.TryWhole(EditedMinutes,0,59,out var m)||!FormNumbers.TryWhole(EditedSeconds,0,59,out var s))return false;value=h*3600+m*60+s;return value>=Math.Max(1,SelectedJob?.Completed??0);}
    partial void OnSelectedReserveChanged(ReserveRow? value){if(value is null)return;SelectedReserveItem=ReserveChoices.FirstOrDefault(x=>x.Id==value.Id);ReserveQuantity=value.Quantity.ToString();}
    [RelayCommand] void SetMaterialReserve()
    {if(SelectedReserveItem is not {} item){SettingsError="请选择材料";return;}if(!FormNumbers.TryWhole(ReserveQuantity,0,FormNumbers.Maximum,out var q)){SettingsError="留存数量请输入非负整数";return;}var old=MaterialReserves.FirstOrDefault(x=>x.Id==item.Id);if(old is not null)MaterialReserves.Remove(old);MaterialReserves.Add(new(item.Id,item.Name,q));SettingsError=null;ScheduleSettings();}
    [RelayCommand] void RemoveMaterialReserve()
    {if(SelectedReserve is not {} row)return;MaterialReserves.Remove(row);SelectedReserve=null;ScheduleSettings();}
    partial void OnThemeKeyChanged(string value){ApplyTheme();ScheduleSettings();}
    partial void OnRecognitionLowCpuChanged(bool value)=>ScheduleSettings();
    void ApplyTheme()
    {if(Application.Current is {} app)app.RequestedThemeVariant=ThemeKey switch{"light"=>ThemeVariant.Light,"dark"=>ThemeVariant.Dark,_=>ThemeVariant.Default};}
    void LoadExtraSettings(DesktopPreferences p)
    {var names=workspace.ReadItemNames();ReserveChoices.Clear();foreach(var x in names.OrderBy(x=>x.Value))ReserveChoices.Add(new(x.Key,x.Value));MaterialReserves.Clear();foreach(var x in p.MaterialReserves??new Dictionary<string,long>())MaterialReserves.Add(new(x.Key,names.GetValueOrDefault(x.Key)??$"未关联道具 {x.Key}",x.Value));ThemeKey=p.Theme??"system";ApplyTheme();}
    [ObservableProperty] private string captureDetails="当前会话数量未知；账户道具页显示历史记录。";
    void UpdateCaptureDetails(MabiAgent.Capture.CaptureStatus state)
    {
        var inv=state.Inventory;var identity=state.CurrentIdentity;var details=identity is null?"当前角色：尚未验证":"当前角色："+identity.Label;
        if(inv?.FullSyncAt is {} timestamp&&double.IsFinite(timestamp)){var age=System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency-timestamp;if(age>=0&&age<long.MaxValue)details+="；完整同步距今："+JobRow.Duration((long)age);}
        details+=inv?.VerifiedThrough is not null?"；当前会话库存仅供规划":"；当前会话数量未知";
        if(inv?.Gold is{} gold&&inv.GoldAt is{} goldAt){var age=System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency-goldAt;details+=$"；抓包金币：{gold:N0}（{Math.Max(0,age):F0}秒前）";}
        if(state.State==MabiAgent.Capture.CaptureState.Listening)details+=$"；报文 {state.Metrics.Packets:N0}，解析帧 {state.Metrics.DecodedFrames:N0}，连接 {state.Metrics.Connections}，失效 {state.Metrics.Invalidations}";
        if(!string.IsNullOrWhiteSpace(inv?.Error))details+="；同步依据："+inv.Error;
        if(!string.IsNullOrWhiteSpace(state.AccountWriteError))details+="；账户记录未保存："+state.AccountWriteError;
        CaptureDetails=details;
        if(CaptureSettingsOpen)CaptureSettings.Update(state);
    }
    CaptureSettingsViewModel? captureSettings;
    public CaptureSettingsViewModel CaptureSettings=>captureSettings??=new(()=>automation,e=>activity.Report(e));
    [ObservableProperty] private bool captureSettingsOpen;
    [RelayCommand] void OpenCaptureSettings(){CaptureSettings.Load();CaptureSettingsOpen=true;}
    [RelayCommand] void CloseCaptureSettings()=>CaptureSettingsOpen=false;    [RelayCommand] void StartCapture(){try{automation?.StartCapture();Error=null;}catch(Exception e){activity.Report(e);Error=e.Message;}}
    [RelayCommand] async Task StopCaptureAsync(){try{if(automation is not null)await automation.StopCaptureAsync();Error=null;}catch(Exception e){activity.Report(e);Error=e.Message;}}
    [RelayCommand] async Task ReloginAsync(){try{if(automation is not null)await automation.ReloginAsync();Error=null;}catch(Exception e){activity.Report(e);Error=e.Message;}}
}


