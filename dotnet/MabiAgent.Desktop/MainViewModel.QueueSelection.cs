using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace MabiAgent.Desktop;

public partial class MainViewModel
{
    public HashSet<string> SelectedJobIds {get;}=[];
    public int QueueSelectionCount=>SelectedJobIds.Count;
    public bool HasQueueSelection=>QueueSelectionCount>0;
    public bool CanBatchEdit=>SelectedRows.Length>0&&SelectedRows.All(j=>j.CanEditTarget)&&SelectedRows.Select(j=>j.IsFishing).Distinct().Count()==1&&!IsBusy;
    public string BatchTargetHint=>SelectedRows.Select(j=>j.IsFishing).Distinct().Count()>1?"选同类任务批改计划":SelectedRows.FirstOrDefault()?.IsFishing==true?"统一时长（时:分:秒）":SelectedRows.Select(j=>j.QuantityUnit).Distinct().Count()>1?"统一计划（生产件／采集轮）":SelectedRows.FirstOrDefault()?.IsGathering==true?"统一轮数（1轮＝10次）":"统一产物件数";
    [ObservableProperty] private string batchTarget="";
    private JobRow[] SelectedRows=>Jobs.Where(j=>SelectedJobIds.Contains(j.Id)).ToArray();
    public void SetQueueSelection(IEnumerable<JobRow> rows)
    {
        var ids=rows.Select(j=>j.Id).ToArray();SelectedJobIds.Clear();SelectedJobIds.UnionWith(ids);
        OnPropertyChanged(nameof(QueueSelectionCount));OnPropertyChanged(nameof(HasQueueSelection));OnPropertyChanged(nameof(CanBatchEdit));OnPropertyChanged(nameof(BatchTargetHint));
    }
    internal static bool TryQueueTarget(JobRow row,string text,out long value)
    {
        value=0;
        if(!row.IsFishing)return FormNumbers.TryWhole(text,Math.Max(1,row.Completed),FormNumbers.Maximum,out value);
        var parts=text.Trim().Split(':','：');
        if(parts.Length!=3||!FormNumbers.TryWhole(parts[0],0,8760,out var h)||!FormNumbers.TryWhole(parts[1],0,59,out var m)||!FormNumbers.TryWhole(parts[2],0,59,out var s))return false;
        value=h*3600+m*60+s;return value>=Math.Max(1,row.Completed);
    }
    public async Task SaveRowTargetAsync(JobRow row)
    {
        if(IsApplyingQueue||!row.HasTargetDraft)return;
        if(!row.CanEditTarget){Error="此任务的计划由当前批次或自动补料管理";return;}
        if(!TryQueueTarget(row,row.TargetInput,out var target)){Error=row.IsFishing?"时长请输入 时:分:秒，且不低于已执行时长":$"计划请输入不低于已确认{row.QuantityUnit}数的正整数";return;}
        if(await ChangeAsync(()=>workspace.EditQueue(q=>q.SetTargetQuantity(row.Id,target)),"计划已保存"))row.ResetTargetDraft();
    }
    [RelayCommand] private async Task ApplyBatchTargetAsync()
    {
        var rows=SelectedRows;if(!CanBatchEdit)return;
        var targets=new Dictionary<string,long>();
        foreach(var row in rows)
        {
            if(!TryQueueTarget(row,BatchTarget,out var value)){Error="批量输入须适用于所有选中任务；生产用件数，采集用轮数，钓鱼用 时:分:秒，且不低于已确认进度";return;}
            targets.Add(row.Id,value);
        }
        if(await ChangeAsync(()=>workspace.EditQueue(q=>q.SetTargets(targets)), $"已修改 {rows.Length} 条计划"))foreach(var row in rows)row.ResetTargetDraft();
    }
    [RelayCommand] private Task DeleteSelectedAsync()
    {
        var ids=SelectedJobIds.ToArray();
        return ids.Length==0?Task.CompletedTask:ChangeAsync(()=>workspace.DeleteJobs(ids),$"已删除 {ids.Length} 条任务");
    }
    [RelayCommand] private Task ControlSelectedAsync(string command)
    {
        if(command is not("pause" or "continue"))return Task.CompletedTask;
        var rows=SelectedRows.Where(j=>!j.Cancelled).ToArray();
        return rows.Length==0?Task.CompletedTask:ChangeAsync(()=>workspace.EditQueue(q=>{foreach(var row in rows)q.Control(row.Id,command);}),command=="pause"?"所选任务已暂停":"所选任务已恢复");
    }
}
