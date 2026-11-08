using MabiAgent.Windows;
namespace MabiAgent.Desktop;

public sealed partial class AutomationService
{
    private readonly SemaphoreSlim hotkeySettingsGate=new(1,1);
    private GlobalStopHotkey? stopHotkey;
    private GlobalStopHotkey? pauseHotkey;
    private int hotkeyPausePending;
    public string PauseHotkeyStatus{get;private set;}="全局暂停快捷键正在注册";
    public string? ActivePauseHotkey=>pauseHotkey?.Active;
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
        try
        {
            pauseHotkey=new(()=>
            {
                session?.Guard.RequestPause();
                if(Interlocked.Exchange(ref hotkeyPausePending,1)==0)_=PauseFromHotkeyAsync();
            },e=>RecordFault("全局暂停快捷键："+e));
            await ApplyPauseHotkeyAsync(workspace.ReadSettings()["input"]?["pause_hotkey"]?.GetValue<string>()??StopHotkey.PauseDefault,false).ConfigureAwait(false);
        }
        catch(Exception error){PauseHotkeyStatus="全局暂停快捷键不可用："+error.Message;RecordFault(PauseHotkeyStatus);NotifyChanged();}
    }
    private async Task PauseFromHotkeyAsync()
    {
        try{await PauseAsync().ConfigureAwait(false);}
        catch(Exception error){RecordFault("全局暂停失败："+error);}
        finally{Interlocked.Exchange(ref hotkeyPausePending,0);}
    }
    public async Task ApplyPauseHotkeyAsync(string text,bool save=true)
    {
        await hotkeySettingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var receiver=pauseHotkey??throw new InvalidOperationException("全局暂停快捷键尚未初始化");
            var prior=receiver.Active;
            await receiver.ApplyAsync(text).ConfigureAwait(false);
            if(save)
                try{workspace.SavePauseHotkey(receiver.Active!);}
                catch
                {
                    if(prior is not null)await receiver.ApplyAsync(prior).ConfigureAwait(false);
                    throw;
                }
            PauseHotkeyStatus="全局暂停快捷键已生效："+receiver.Active;
        }
        catch(Exception error)
        {
            PauseHotkeyStatus=(pauseHotkey?.Active is{} active?"当前全局暂停快捷键："+active+"；":"全局暂停快捷键不可用：")+error.Message;
            throw;
        }
        finally{hotkeySettingsGate.Release();NotifyChanged();}
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
