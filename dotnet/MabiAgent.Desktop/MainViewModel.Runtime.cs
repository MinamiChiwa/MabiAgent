using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace MabiAgent.Desktop;
public partial class MainViewModel
{
    [ObservableProperty] private string runtimeTask="当前任务：无";
    [ObservableProperty] private string runtimeProgress="尚无确认进度";
    [ObservableProperty] private string runtimeHealth="等待启动";
    [ObservableProperty] private string runtimeIssues="";
    public void SelectJob(JobRow row){SelectedJob=row;SetQueueSelection([row]);}
    private void UpdateRuntime()
    {
        ApplyRuntimeStates();
        var status=automation?.Status;var active=Jobs.FirstOrDefault(j=>j.Id==status?.ActiveJob);
        RuntimeIssues=status is null?"":string.Join("\n",status.Issues.Where(p=>
        {
            var job=Jobs.FirstOrDefault(j=>j.Id==p.Key);var state=status.Jobs.GetValueOrDefault(p.Key);
            if(job is not null&&(job.Cancelled||job.Completed>=job.Target)&&state?.PendingConsumption!=true)return false;
            return MabiAgent.Workflows.RuntimeIssuePresentation.RequiresPlayerAction(p.Value,state);
        }).Select(p=>(Jobs.FirstOrDefault(j=>j.Id==p.Key)?.Name??ModuleLabel(p.Key))+"："+PauseLabel(p.Value)).Distinct());
        if(activity.DiagnosticWriteError is{} diagnosticError)RuntimeIssues+=(RuntimeIssues.Length==0?"":"\n")+"故障日志写入失败："+diagnosticError;
        RuntimeTask=active is null?"当前任务："+(IsRunning?"正在核验或等待":"无"):"当前任务："+active.Name;
        var goals=Jobs.Where(j=>!j.Automatic&&!j.Cancelled).ToArray();
        RuntimeProgress=$"目标 {goals.Count(j=>j.Completed>=j.Target)}/{goals.Length} 已完成 · {Jobs.Count(j=>j.Pending)} 个活动批次 · {status?.ReconciliationCount??0} 项待核账"+(active is null?"":$" · 当前已确认 {active.CompletedLabel} / {active.TargetLabel}");
        RuntimeHealth=automation is null?"等待启动":$"心跳 {automation.Heartbeat:HH:mm:ss} · {automation.ExecutionStage} · 计划 {status!.PlanRevision}"+(DateTimeOffset.Now-automation.Heartbeat>TimeSpan.FromSeconds(5)?" · 心跳延迟，请查看异常记录":"")+
            (status.PauseReason is null?"":" · 暂停："+(status.PauseReason=="fishing_result_pending"?"钓鱼动作结果待确认":PauseLabel(status.PauseReason)))+(status.RecoveryAttempt==0?"":$" · 自动重试 {status.RecoveryAttempt}/3");
        UpdateCompactSummary(active,status?.ActiveConfirmed??0,goals);
    }
    private void ApplyRuntimeStates()
    {var states=automation?.Status.Jobs;foreach(var row in Jobs)row.ApplyRuntime(states?.GetValueOrDefault(row.Id));}
    private static string PauseLabel(string reason)=>reason switch{"manual"=>"手动暂停","focus_lost"=>"游戏失焦","window_minimized"=>"游戏已最小化，还原后继续","user_input"=>"检测到手动输入","runtime_error"=>"运行异常","configuration_changed"=>"配置已变化","maintenance_required"=>"背包需要处理","input_outcome_unknown"=>"输入结果待核账","restart_required"=>"游戏需要重启","unrecognized_page"=>"页面识别超时","relogin_blocked"=>"重登待核实","inventory_sync_complete"=>"同步完成，等待继续","fishing_action_confirmed"=>"钓鱼动作已确认，等待继续",_=>reason};
    private static string ModuleLabel(string value)=>value switch{"maintenance"=>"维护","storage"=>"仓库","purchase"=>"采购","diagnostics"=>"故障记录","maintenance_fault"=>"维护",_=>value};
    public Task ReorderAsync(JobRow source,JobRow target)
    {
        if(IsBusy||source.Id==target.Id||source.Automatic!=target.Automatic)return Task.CompletedTask;
        var view=source.Automatic?AutomaticView:QueueView;
        if(view.SortDescriptions.Count>0){OperationStatus="请先重置表头排序，再拖动任务";return Task.CompletedTask;}
        if(!view.Contains(source)||!view.Contains(target))return Task.CompletedTask;
        return ChangeAsync(()=>workspace.EditQueue(q=>q.Move(source.Id,source.Order<target.Order?1:-1,target.Id)),"列表位置已保存");
    }
}
