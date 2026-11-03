using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MabiAgent.Capture;
namespace MabiAgent.Desktop;

public sealed partial class CaptureDeviceRow(string name,string description,bool selected):ObservableObject
{
    public string Name {get;}=name;
    public string Description {get;}=description;
    [ObservableProperty] private bool selected=selected;
}
public sealed record CaptureFact(string Label,string Value,bool Good=false,bool Warning=false);

// Drawer behind 维护设置 › 数据同步: device choice, process binding, per-container coverage and counters.
// Shows only what the capture service reports; unknown values stay "—", never zero.
public sealed partial class CaptureSettingsViewModel(Func<AutomationService?> automation,Action<Exception> report):ObservableObject
{
    public ObservableCollection<CaptureDeviceRow> Devices {get;}=[];
    public ObservableCollection<CaptureFact> Binding {get;}=[];
    public ObservableCollection<CaptureFact> Coverage {get;}=[];
    public ObservableCollection<CaptureFact> Diagnostics {get;}=[];
    [ObservableProperty] private bool autoDevices=true;
    [ObservableProperty] private string deviceStatus="";
    [ObservableProperty] private string syncSummary="";
    [ObservableProperty] private string? actionMessage;
    [ObservableProperty] private bool working;
    public bool HasActionMessage=>!string.IsNullOrWhiteSpace(ActionMessage);
    partial void OnActionMessageChanged(string? value)=>OnPropertyChanged(nameof(HasActionMessage));
    public bool DevicesDirty=>Devices.Count>0&&!SameSelection(automation()?.CaptureDeviceSetting??[]);

    public void Load(){LoadDevices();Update(automation()?.CaptureStatus);}
    [RelayCommand] private void RefreshDevices()=>LoadDevices();
    private void LoadDevices()
    {
        var service=automation();Devices.Clear();
        if(service is null){DeviceStatus="未连接运行服务";return;}
        var saved=service.CaptureDeviceSetting.ToHashSet(StringComparer.Ordinal);
        try
        {
            var found=service.DiscoverCaptureDevices();
            foreach(var device in found)Devices.Add(Watch(new(device.Name,device.Description,saved.Contains(device.Name))));
            // Keep saved devices that are absent now so the user sees why listening is blocked.
            foreach(var missing in saved.Where(n=>found.All(d=>d.Name!=n)))Devices.Add(Watch(new(missing,"当前不可用 · "+missing,true)));
            DeviceStatus=found.Count==0?"未发现 Npcap 网卡":$"发现 {found.Count} 个网卡";
        }
        catch(Exception error){DeviceStatus="无法列出网卡："+error.Message;foreach(var name in saved)Devices.Add(Watch(new(name,name,true)));}
        AutoDevices=saved.Count==0;OnPropertyChanged(nameof(DevicesDirty));
    }
    CaptureDeviceRow Watch(CaptureDeviceRow row){row.PropertyChanged+=(_,_)=>OnPropertyChanged(nameof(DevicesDirty));return row;}
    partial void OnAutoDevicesChanged(bool value)=>OnPropertyChanged(nameof(DevicesDirty));
    bool SameSelection(IReadOnlyCollection<string> saved)=>AutoDevices?saved.Count==0:Devices.Where(d=>d.Selected).Select(d=>d.Name).ToHashSet(StringComparer.Ordinal).SetEquals(saved);
    [RelayCommand] private void ApplyDevices()
    {
        var service=automation();if(service is null)return;
        var chosen=AutoDevices?[]:Devices.Where(d=>d.Selected).Select(d=>d.Name).ToArray();
        if(!AutoDevices&&chosen.Length==0){ActionMessage="请至少勾选一个网卡，或选择自动";return;}
        try{service.ApplyCaptureDevices(chosen);ActionMessage=chosen.Length==0?"已改为自动使用全部网卡，监听正在重新连接":$"已指定 {chosen.Length} 个网卡，监听正在重新连接";OnPropertyChanged(nameof(DevicesDirty));}
        catch(Exception error){report(error);ActionMessage="网卡设置未保存："+error.Message;}
    }
    [RelayCommand] private async Task RebindAsync()
    {
        var service=automation();if(service is null)return;Working=true;
        try{await service.RebindCaptureAsync();ActionMessage="已重新绑定所选游戏进程并重启监听";}
        catch(Exception error){report(error);ActionMessage="重新绑定失败："+error.Message;}
        finally{Working=false;}
    }
    [RelayCommand] private async Task RefreshInventoryAsync()
    {
        var service=automation();if(service is null)return;Working=true;
        try{await service.RefreshInventoryAsync();ActionMessage="私人剧场刷新已开始，完成后接收新的完整库存";}
        catch(Exception error){ActionMessage=error.Message;}
        finally{Working=false;}
    }
    [RelayCommand] private void OpenDiagnostics()
    {
        var path=automation()?.DiagnosticsFile;if(path is null)return;
        try
        {
            var target=File.Exists(path)?path:Path.GetDirectoryName(path)!;Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target){UseShellExecute=true});
        }
        catch(Exception error){report(error);ActionMessage="无法打开诊断日志："+error.Message;}
    }

    public void Update(CaptureStatus? status)
    {
        List<CaptureFact> binding=[],coverage=[],diagnostics=[];
        try{Fill(status,binding,coverage,diagnostics);}
        finally{Sync(Binding,binding);Sync(Coverage,coverage);Sync(Diagnostics,diagnostics);}
    }
    // Replace only changed rows so a once-per-second status tick does not rebuild the visuals.
    static void Sync(ObservableCollection<CaptureFact> target,List<CaptureFact> next)
    {
        for(var i=0;i<next.Count;i++){if(i>=target.Count)target.Add(next[i]);else if(target[i]!=next[i])target[i]=next[i];}
        while(target.Count>next.Count)target.RemoveAt(target.Count-1);
    }
    void Fill(CaptureStatus? status,List<CaptureFact> Binding,List<CaptureFact> Coverage,List<CaptureFact> Diagnostics)
    {
        var service=automation();
        if(status is null){SyncSummary="抓包不可用";return;}
        var listening=status.State==CaptureState.Listening;
        Binding.Add(new("监听状态",status.Message,listening,status.State==CaptureState.Retry));
        Binding.Add(new("所选窗口进程",service?.SelectedProcessId is{} pid?$"PID {pid}":"未选择窗口",service?.SelectedProcessId is not null,service?.SelectedProcessId is null));
        Binding.Add(new("抓包目标",status.Target is{} t?$"PID {t.ProcessId} · 启动于 {t.StartedUtc.ToLocalTime():MM-dd HH:mm:ss}":"—",status.Target is not null&&status.Target.ProcessId==service?.SelectedProcessId,
            status.Target is not null&&service?.SelectedProcessId is{} chosen&&status.Target.ProcessId!=chosen));
        Binding.Add(new("使用网卡",status.Devices.Count==0?"—":string.Join("、",status.Devices.Select(Describe))));
        var inventory=status.Inventory;var verified=inventory?.VerifiedThrough is not null;
        foreach(var (container,label) in new[]{(InventoryContainer.Backpack,"背包"),(InventoryContainer.CharacterStorage,"角色仓"),(InventoryContainer.AccountStorage,"共享仓")})
        {
            if(!verified){Coverage.Add(new(label,"本会话未同步",false,true));continue;}
            Coverage.Add(inventory!.Counts.TryGetValue(container,out var counts)?new(label,$"已完整同步 · {counts.Count} 种",true):new(label,"未覆盖",false,true));
        }
        SyncSummary=inventory?.FullSyncAt is{} at&&double.IsFinite(at)
            ?$"最近完整同步 {JobRow.Duration((long)Math.Max(0,System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency-at))} 前 · 修订 {inventory.FullSyncRevision}"+(status.CurrentIdentity is{} who?" · "+who.Label:"")
            :"尚未收到完整库存";
        var m=status.Metrics;string Count(long value)=>listening?value.ToString("N0"):"—";
        Diagnostics.Add(new("报文",Count(m.Packets)));Diagnostics.Add(new("载荷",listening?$"{m.PayloadBytes/1024d:N0} KB":"—"));
        Diagnostics.Add(new("解析帧",Count(m.DecodedFrames)));Diagnostics.Add(new("连接",Count(m.Connections)));
        Diagnostics.Add(new("缓冲",listening?$"{m.BufferedBytes/1024d:N0} KB":"—"));Diagnostics.Add(new("失效/缺包",Count(m.Invalidations),false,listening&&m.Invalidations>0));
        if(!string.IsNullOrWhiteSpace(inventory?.Error))Diagnostics.Add(new("同步依据",inventory.Error,false,true));
    }
    string Describe(string name)=>Devices.FirstOrDefault(d=>d.Name==name)?.Description??name;
}
