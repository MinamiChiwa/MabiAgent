namespace MabiAgent.Workflows;

/// <summary>固定的库存刷新脚本。目标名由宿主映射到已验证的稳定控件或坐标，不在此处猜坐标。</summary>
public enum PrivateTheaterStepKind
{
    Click,
    PressKey,
    PressSpace,
    WaitForStable
}

public sealed record PrivateTheaterStep(
    string Id,
    PrivateTheaterStepKind Kind,
    string Target,
    TimeSpan MinimumDelay)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record PrivateTheaterScriptDefinition(IReadOnlyList<PrivateTheaterStep> Steps)
{
    public static PrivateTheaterScriptDefinition Default { get; } = new([
        new("open_bag", PrivateTheaterStepKind.Click, "scene.open_bag", TimeSpan.Zero),
        new("tools_tab", PrivateTheaterStepKind.Click, "bag_tools_tab", TimeSpan.Zero),
        new("tools_selected", PrivateTheaterStepKind.WaitForStable, "bag.tools_selected", TimeSpan.Zero),
        new("game_tab", PrivateTheaterStepKind.Click, "bag.games_tab", TimeSpan.Zero),
        new("private_theater_item", PrivateTheaterStepKind.Click, "bag.private_theater_item", TimeSpan.Zero),
        new("use_item", PrivateTheaterStepKind.PressSpace, "bag.private_theater_item", TimeSpan.Zero),
        new("leader_review", PrivateTheaterStepKind.Click, "private_theater.join_leader_review", TimeSpan.Zero),
        new("create", PrivateTheaterStepKind.PressSpace, "private_theater.create", TimeSpan.Zero),
        new("map_loaded", PrivateTheaterStepKind.WaitForStable, "private_theater.map_loaded", TimeSpan.Zero),
        new("map_exit", PrivateTheaterStepKind.Click, "private_theater.map_exit", TimeSpan.Zero),
        new("leave", PrivateTheaterStepKind.PressSpace, "private_theater.leave", TimeSpan.Zero),
    ]);

    public PrivateTheaterScriptDefinition Validate()
    {
        if (Steps.Count == 0) throw new ArgumentException("私人剧场脚本不能为空");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id) || !ids.Add(step.Id))
                throw new ArgumentException("私人剧场脚本步骤 ID 必须唯一");
            if (string.IsNullOrWhiteSpace(step.Target))
                throw new ArgumentException($"私人剧场步骤缺少稳定目标：{step.Id}");
            if (step.MinimumDelay < TimeSpan.Zero || step.Timeout <= TimeSpan.Zero || step.Timeout > TimeSpan.FromMinutes(2))
                throw new ArgumentOutOfRangeException(nameof(Steps));
        }
        return this;
    }
}

public enum PrivateTheaterRefreshStatus { Idle, Running, Paused, Complete, Failed }

/// <summary>
/// 离线可测的最小脚本状态机。执行器只在稳定等待节点调用识别，输入结果未知时不会自动重放。
/// </summary>
public sealed class PrivateTheaterRefreshScript
{
    private readonly PrivateTheaterScriptDefinition definition;
    private readonly Func<DateTimeOffset>? completedAt;
    private int index;
    private DateTimeOffset nextAt;
    private DateTimeOffset stepEnteredAt;
    public PrivateTheaterRefreshStatus Status { get; private set; } = PrivateTheaterRefreshStatus.Idle;
    public string? Failure { get; private set; }
    public int CurrentIndex => index;
    public PrivateTheaterStep? Current => index < definition.Steps.Count ? definition.Steps[index] : null;

    public PrivateTheaterRefreshScript(PrivateTheaterScriptDefinition? definition = null, Func<DateTimeOffset>? completedAt = null)
    {
        this.definition = (definition ?? PrivateTheaterScriptDefinition.Default).Validate();
        this.completedAt = completedAt;
    }

    public void Start(DateTimeOffset now)
    {
        if (Status is PrivateTheaterRefreshStatus.Running) return;
        if (Status is PrivateTheaterRefreshStatus.Complete or PrivateTheaterRefreshStatus.Failed)
            throw new InvalidOperationException("已结束的剧场刷新不可自动重放");
        Status = PrivateTheaterRefreshStatus.Running;
        nextAt = now;
        stepEnteredAt = now;
    }

    public void Pause()
    {
        if (Status == PrivateTheaterRefreshStatus.Running) Status = PrivateTheaterRefreshStatus.Paused;
    }

    public void Continue(DateTimeOffset now)
    {
        if (Status != PrivateTheaterRefreshStatus.Paused) throw new InvalidOperationException("剧场刷新未处于暂停状态");
        Status = PrivateTheaterRefreshStatus.Running;
        nextAt = now;
        stepEnteredAt = now;
    }

    public bool Tick(DateTimeOffset now, Func<PrivateTheaterStep, bool> dispatch, Func<string, bool>? stable = null)
    {
        if (Status != PrivateTheaterRefreshStatus.Running || now < nextAt) return false;
        var step = Current;
        if (step is null) { Status = PrivateTheaterRefreshStatus.Complete; return false; }
        if(now-stepEnteredAt>step.Timeout){Status=PrivateTheaterRefreshStatus.Failed;Failure="私人剧场步骤超时："+step.Id;return false;}
        bool accepted;
        try { if (step.Kind == PrivateTheaterStepKind.WaitForStable && !(stable?.Invoke(step.Target) ?? false)) return false;accepted = dispatch(step); }
        catch (Exception error) { Status = PrivateTheaterRefreshStatus.Failed; Failure = error.Message; return false; }
        if (!accepted) return false;
        index++;
        nextAt = (completedAt?.Invoke() ?? now) + step.MinimumDelay;
        stepEnteredAt = nextAt;
        if (index >= definition.Steps.Count) Status = PrivateTheaterRefreshStatus.Complete;
        return true;
    }
}
