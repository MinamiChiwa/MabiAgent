namespace MabiAgent.Workflows;

/// <summary>固定的库存刷新脚本。目标名由宿主映射到已验证的稳定控件或坐标，不在此处猜坐标。</summary>
public enum QuickReentryStepKind
{
    Click,
    ClickText,
    PressKey,
    PressSpace,
    WaitForStable
}

public sealed record QuickReentryStep(
    string Id,
    QuickReentryStepKind Kind,
    string Target,
    TimeSpan MinimumDelay)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record QuickReentryScriptDefinition(IReadOnlyList<QuickReentryStep> Steps)
{
    public static QuickReentryScriptDefinition Default { get; } = new([
        new("escape_menu", QuickReentryStepKind.PressKey, "quick_reentry.menu", TimeSpan.FromMilliseconds(1500)){Timeout=TimeSpan.FromSeconds(15)},
        new("menu_ready", QuickReentryStepKind.WaitForStable, "quick_reentry.menu_ready", TimeSpan.FromMilliseconds(250)){Timeout=TimeSpan.FromSeconds(30)},
        new("appearance", QuickReentryStepKind.ClickText, "quick_reentry.appearance", TimeSpan.FromMilliseconds(1500)){Timeout=TimeSpan.FromSeconds(45)},
        new("rebirth_ready", QuickReentryStepKind.WaitForStable, "quick_reentry.rebirth_ready", TimeSpan.FromMilliseconds(500)){Timeout=TimeSpan.FromSeconds(45)},
        new("escape_back", QuickReentryStepKind.PressKey, "quick_reentry.escape_back", TimeSpan.FromMilliseconds(1800)){Timeout=TimeSpan.FromSeconds(15)},
        new("dialog_kind", QuickReentryStepKind.WaitForStable, "quick_reentry.dialog_kind", TimeSpan.FromMilliseconds(300)){Timeout=TimeSpan.FromSeconds(20)},
        new("escape_if_confirm", QuickReentryStepKind.PressKey, "quick_reentry.escape_if_confirm", TimeSpan.FromMilliseconds(1800)){Timeout=TimeSpan.FromSeconds(15)},
        new("cancel_ready", QuickReentryStepKind.WaitForStable, "quick_reentry.cancel_ready", TimeSpan.FromMilliseconds(300)){Timeout=TimeSpan.FromSeconds(20)},
        new("exit", QuickReentryStepKind.PressKey, "quick_reentry.exit", TimeSpan.FromMilliseconds(3000)){Timeout=TimeSpan.FromSeconds(15)},
        new("loaded", QuickReentryStepKind.WaitForStable, "quick_reentry.loaded", TimeSpan.FromMilliseconds(500)){Timeout=TimeSpan.FromSeconds(120)},
    ]);

    public QuickReentryScriptDefinition Validate()
    {
        if (Steps.Count == 0) throw new ArgumentException("快速重进脚本不能为空");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id) || !ids.Add(step.Id))
                throw new ArgumentException("快速重进脚本步骤 ID 必须唯一");
            if (string.IsNullOrWhiteSpace(step.Target))
                throw new ArgumentException($"快速重进步骤缺少稳定目标：{step.Id}");
            if (step.MinimumDelay < TimeSpan.Zero || step.Timeout <= TimeSpan.Zero || step.Timeout > TimeSpan.FromMinutes(2))
                throw new ArgumentOutOfRangeException(nameof(Steps));
        }
        return this;
    }
}

public enum QuickReentryStatus { Idle, Running, Paused, Complete, Failed }

/// <summary>
/// 离线可测的最小脚本状态机。执行器只在稳定等待节点调用识别，输入结果未知时不会自动重放。
/// </summary>
public sealed class QuickReentryScript
{
    private readonly QuickReentryScriptDefinition definition;
    private readonly Func<DateTimeOffset>? completedAt;
    private int index;
    private DateTimeOffset nextAt;
    private DateTimeOffset stepEnteredAt;
    public QuickReentryStatus Status { get; private set; } = QuickReentryStatus.Idle;
    public string? Failure { get; private set; }
    public int CurrentIndex => index;
    public QuickReentryStep? Current => index < definition.Steps.Count ? definition.Steps[index] : null;

    public QuickReentryScript(QuickReentryScriptDefinition? definition = null, Func<DateTimeOffset>? completedAt = null)
    {
        this.definition = (definition ?? QuickReentryScriptDefinition.Default).Validate();
        this.completedAt = completedAt;
    }

    public void Start(DateTimeOffset now)
    {
        if (Status is QuickReentryStatus.Running) return;
        if (Status is QuickReentryStatus.Complete or QuickReentryStatus.Failed)
            throw new InvalidOperationException("已结束的快速重进不可自动重放");
        Status = QuickReentryStatus.Running;
        nextAt = now;
        stepEnteredAt = now;
    }

    public void Pause()
    {
        if (Status == QuickReentryStatus.Running) Status = QuickReentryStatus.Paused;
    }

    public void Fail(string failure)
    {
        if (Status is QuickReentryStatus.Complete or QuickReentryStatus.Failed) return;
        Failure = string.IsNullOrWhiteSpace(failure) ? "快速重进已中止" : failure;
        Status = QuickReentryStatus.Failed;
    }

    public void Continue(DateTimeOffset now)
    {
        if (Status != QuickReentryStatus.Paused) throw new InvalidOperationException("快速重进未处于暂停状态");
        Status = QuickReentryStatus.Running;
        nextAt = now;
        stepEnteredAt = now;
    }

    public bool Tick(DateTimeOffset now, Func<QuickReentryStep, bool> dispatch, Func<string, bool>? stable = null)
    {
        if (Status != QuickReentryStatus.Running || now < nextAt) return false;
        var step = Current;
        if (step is null) { Status = QuickReentryStatus.Complete; return false; }
        if(now-stepEnteredAt>step.Timeout){Status=QuickReentryStatus.Failed;Failure="快速重进步骤超时："+step.Id;return false;}
        bool accepted;
        try { if (step.Kind == QuickReentryStepKind.WaitForStable && !(stable?.Invoke(step.Target) ?? false)) return false;accepted = dispatch(step); }
        catch (Exception error) { Status = QuickReentryStatus.Failed; Failure = error.Message; return false; }
        if (!accepted) return false;
        index++;
        nextAt = (completedAt?.Invoke() ?? now) + step.MinimumDelay;
        stepEnteredAt = nextAt;
        if (index >= definition.Steps.Count) Status = QuickReentryStatus.Complete;
        return true;
    }
}
