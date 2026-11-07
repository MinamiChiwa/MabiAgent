using MabiAgent.Windows;
namespace MabiAgent.Desktop;

public sealed partial class AutomationService
{
    private readonly SemaphoreSlim hotkeySettingsGate=new(1,1);
    private GlobalStopHotkey? stopHotkey;
    public string StopHotkeyStatus{get;private set;}="全局停止快捷键正在注册";
    public string? ActiveStopHotkey=>stopHotkey?.Active;
    private async Task InitializeStopHotkeyAsync()
    {
        try
        {
            stopHotkey=new(()=>{session?.Guard.RequestPause();_ = Task.Run(StopFromHotkeyAsync);},e=>RecordFault("全局停止快捷键："+e));
            await ApplyStopHotkeyAsync(workspace.ReadSettings()["input"]?["stop_hotkey"]?.GetValue<string>()??StopHotkey.Default,false).ConfigureAwait(false);
        }
        catch(Exception error){StopHotkeyStatus="全局停止快捷键不可用："+error.Message;RecordFault(StopHotkeyStatus);NotifyChanged();}
    }
    private async Task StopFromHotkeyAsync()
    {
        try{await StopAllAsync().ConfigureAwait(false);}
        catch(Exception error){RecordFault("全局停止失败："+error);}
    }
    public async Task ApplyStopHotkeyAsync(string text,bool save=true)
    {
        await hotkeySettingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var receiver=stopHotkey??throw new InvalidOperationException("全局停止快捷键尚未初始化");
            var prior=receiver.Active;
            await receiver.ApplyAsync(text).ConfigureAwait(false);
            if(save)
                try{workspace.SaveStopHotkey(receiver.Active!);}
                catch
                {
                    if(prior is not null)await receiver.ApplyAsync(prior).ConfigureAwait(false);
                    throw;
                }
            StopHotkeyStatus="全局停止快捷键已生效："+receiver.Active;
        }
        catch(Exception error)
        {
            StopHotkeyStatus=(stopHotkey?.Active is{} active?"当前全局停止快捷键："+active+"；":"全局停止快捷键不可用：")+error.Message;
            throw;
        }
        finally{hotkeySettingsGate.Release();NotifyChanged();}
    }
}
