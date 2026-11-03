using System.ComponentModel;

namespace MabiAgent.Desktop;

public partial class MainViewModel
{
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool HasRuntimeIssues => !string.IsNullOrWhiteSpace(RuntimeIssues);
    public bool HasRuntimeDetails => IsRunning || Jobs.Count > 0 || HasRuntimeIssues;
    public bool HasAutomaticTasks => AutomaticCount > 0;
    public bool HasOperationStatus => !string.IsNullOrWhiteSpace(OperationStatus);
    public bool HasSettingsError => !string.IsNullOrWhiteSpace(SettingsError);
    public bool HasSettingsStatus => !string.IsNullOrWhiteSpace(SettingsStatus);
    public bool HasQuantityError => !string.IsNullOrWhiteSpace(QuantityError);
    public bool HasTargetError => !string.IsNullOrWhiteSpace(TargetError);
    public bool HasAddError => !string.IsNullOrWhiteSpace(AddError);
    public bool HasExecutionWarning => !IsRunning && (ExecutionStatus.Contains("暂停",StringComparison.Ordinal) || ExecutionStatus.Contains("失焦",StringComparison.Ordinal) || ExecutionStatus.Contains("异常",StringComparison.Ordinal) || ExecutionStatus.Contains("失败",StringComparison.Ordinal) || ExecutionStatus.Contains("待确认",StringComparison.Ordinal));

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        var visibility = e.PropertyName switch
        {
            nameof(Error) => nameof(HasError),
            nameof(RuntimeIssues) => nameof(HasRuntimeIssues),
            nameof(OperationStatus) => nameof(HasOperationStatus),
            nameof(SettingsError) => nameof(HasSettingsError),
            nameof(SettingsStatus) => nameof(HasSettingsStatus),
            nameof(QuantityError) => nameof(HasQuantityError),
            nameof(TargetError) => nameof(HasTargetError),
            nameof(AddError) => nameof(HasAddError),
            _ => null
        };
        if (visibility is not null) base.OnPropertyChanged(new PropertyChangedEventArgs(visibility));
        if (e.PropertyName is nameof(IsRunning) or nameof(ManualCount) or nameof(AutomaticCount) or nameof(RuntimeIssues))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasRuntimeDetails)));
        if (e.PropertyName == nameof(AutomaticCount))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasAutomaticTasks)));
        if (e.PropertyName is nameof(ExecutionStatus) or nameof(IsRunning))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasExecutionWarning)));
    }
}
