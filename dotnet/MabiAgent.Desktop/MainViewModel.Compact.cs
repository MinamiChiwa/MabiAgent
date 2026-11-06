using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace MabiAgent.Desktop;
public sealed record CompactJob(string Name,string Progress,string Status,double Percent,bool Active,bool Warning);
// Summary state for the compact window. The main window shows the same data in full; both bind one model.
public partial class MainViewModel
{
    [ObservableProperty] private bool isMiniMode;
    [ObservableProperty] private bool miniExpanded;
    [ObservableProperty] private bool miniPinned=true;
    [ObservableProperty] private string activeJobName="暂无运行中的任务";
    [ObservableProperty] private string activeProgressText="";
    [ObservableProperty] private double activeProgress;
    [ObservableProperty] private bool hasActiveJob;
    [ObservableProperty] private string goalSummary="目标 0/0";
    [ObservableProperty] private int issueCount;
    public ObservableCollection<CompactJob> UpcomingJobs {get;}=[];
    public ObservableCollection<string> IssueLines {get;}=[];
    public string? IssueBadge=>IssueCount>0?IssueCount.ToString():null;
    public bool HasUpcomingJobs=>UpcomingJobs.Count>0;
    public string MiniExpandLabel=>MiniExpanded?"收起详情":"展开详情";
    public string MiniExpandIcon=>MiniExpanded?"chevron-up":"chevron-down";
    public string MiniPinTip=>MiniPinned?"已置顶，点击取消":"未置顶，点击置顶";
    partial void OnIssueCountChanged(int value)=>OnPropertyChanged(nameof(IssueBadge));
    partial void OnMiniExpandedChanged(bool value){OnPropertyChanged(nameof(MiniExpandLabel));OnPropertyChanged(nameof(MiniExpandIcon));}
    partial void OnMiniPinnedChanged(bool value)=>OnPropertyChanged(nameof(MiniPinTip));
    [RelayCommand] private void EnterMini()=>IsMiniMode=true;
    [RelayCommand] private void ExitMini()=>IsMiniMode=false;
    [RelayCommand] private void ToggleMiniExpanded()=>MiniExpanded=!MiniExpanded;
    private void UpdateCompactSummary(JobRow? active,long activeConfirmed,JobRow[] goals)
    {
        GoalSummary=$"目标 {goals.Count(j=>j.Completed>=j.Target)}/{goals.Length}";
        HasActiveJob=active is not null;
        if(active is null)
        {
            ActiveJobName=IsRunning?"正在核验或等待":"暂无运行中的任务";ActiveProgressText=goals.Length==0?"":GoalSummary;ActiveProgress=0;
        }
        else
        {
            var done=Math.Min(active.Target,active.ConfirmedQuantity);
            ActiveJobName=active.Name;
            ActiveProgressText=(active.IsFishing?JobRow.Duration(done):$"{done}{active.QuantityUnit}")+" / "+active.TargetLabel;
            ActiveProgress=active.Target<=0?0:Math.Clamp(done*100d/active.Target,0,100);
        }
        var upcoming=goals.Where(j=>j.Completed<j.Target).OrderBy(j=>j.Order).Take(4).Select(j=>new CompactJob(j.Name,j.CompletedLabel+" / "+j.TargetLabel,j.Status,
            j.Target<=0?0:Math.Clamp(j.ConfirmedQuantity*100d/j.Target,0,100),j.Id==active?.Id,j.IsWarningState)).ToArray();
        if(!UpcomingJobs.SequenceEqual(upcoming)){UpcomingJobs.Clear();foreach(var row in upcoming)UpcomingJobs.Add(row);OnPropertyChanged(nameof(HasUpcomingJobs));}
    }
    partial void OnRuntimeIssuesChanged(string value)
    {
        var issues=value.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(!IssueLines.SequenceEqual(issues)){IssueLines.Clear();foreach(var line in issues)IssueLines.Add(line);}
        IssueCount=issues.Length;
    }
}
