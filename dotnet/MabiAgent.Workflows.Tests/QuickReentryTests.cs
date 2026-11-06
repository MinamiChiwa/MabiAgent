using MabiAgent.Workflows;
using Xunit;

namespace MabiAgent.Workflows.Tests;

public sealed class QuickReentryRefreshTests
{
    [Fact] public void DelayStartsAfterDispatchCompletes()
    {
        var now=DateTimeOffset.UnixEpoch;
        var script=new QuickReentryScript(new([
            new("click",QuickReentryStepKind.Click,"button",TimeSpan.FromSeconds(1)),
            new("next",QuickReentryStepKind.Click,"next",TimeSpan.Zero)]),()=>now);
        script.Start(now);
        Assert.True(script.Tick(now,_=>{now=now.AddSeconds(2);return true;}));
        Assert.False(script.Tick(now.AddMilliseconds(999),_=>throw new InvalidOperationException()));
        Assert.True(script.Tick(now.AddSeconds(1),_=>true));
        Assert.Equal(QuickReentryStatus.Complete,script.Status);
    }
    [Fact] public void StableWaitTimeoutEndsLocallyWithoutSendingOrReplay()
    {
        var script=new QuickReentryScript(new([new("load",QuickReentryStepKind.WaitForStable,"theater_label",TimeSpan.Zero){Timeout=TimeSpan.FromSeconds(2)}]));
        script.Start(DateTimeOffset.UnixEpoch);var sent=0;
        Assert.False(script.Tick(DateTimeOffset.UnixEpoch,_=>{sent++;return true;},_=>false));
        Assert.False(script.Tick(DateTimeOffset.UnixEpoch.AddSeconds(3),_=>{sent++;return true;},_=>false));
        Assert.Equal(QuickReentryStatus.Failed,script.Status);Assert.Equal(0,sent);
    }
    [Fact]
    public void DefaultScriptKeepsConfirmedOrderAndDoesNotInventCoordinates()
    {
        var script = new QuickReentryScript();
        var seen = new List<string>();
        var now = DateTimeOffset.UnixEpoch;script.Start(now);
        for(var guard=0;guard<64&&script.Status==QuickReentryStatus.Running;guard++)
        {
            script.Tick(now, step => { seen.Add(step.Id + ":" + step.Target); return true; }, _ => true);
            now = now.AddSeconds(5);
        }

        Assert.Equal(QuickReentryStatus.Complete, script.Status);
        Assert.Equal([
            "escape_menu:quick_reentry.menu",
            "menu_ready:quick_reentry.menu_ready",
            "appearance:quick_reentry.appearance",
            "rebirth_ready:quick_reentry.rebirth_ready",
            "escape_back:quick_reentry.escape_back",
            "dialog_kind:quick_reentry.dialog_kind",
            "escape_if_confirm:quick_reentry.escape_if_confirm",
            "cancel_ready:quick_reentry.cancel_ready",
            "exit:quick_reentry.exit",
            "loaded:quick_reentry.loaded",
        ], seen);
    }

    [Fact]
    public void StableWaitAndPauseRequireExplicitProgress()
    {
        var definition = new QuickReentryScriptDefinition([
            new("wait", QuickReentryStepKind.WaitForStable, "stable.map", TimeSpan.Zero),
            new("leave", QuickReentryStepKind.PressSpace, "leave", TimeSpan.Zero),
        ]);
        var script = new QuickReentryScript(definition);
        script.Start(DateTimeOffset.UnixEpoch);
        Assert.False(script.Tick(DateTimeOffset.UnixEpoch, _ => true, _ => false));
        script.Pause();
        Assert.False(script.Tick(DateTimeOffset.UnixEpoch, _ => throw new InvalidOperationException(), _ => true));
        script.Continue(DateTimeOffset.UnixEpoch);
        Assert.True(script.Tick(DateTimeOffset.UnixEpoch, _ => true, _ => true));
        Assert.True(script.Tick(DateTimeOffset.UnixEpoch, _ => true, _ => true));
        Assert.Equal(QuickReentryStatus.Complete, script.Status);
    }

    [Fact]
    public void DispatchFailureEndsWithoutAutomaticReplay()
    {
        var script = new QuickReentryScript();
        script.Start(DateTimeOffset.UnixEpoch);
        Assert.False(script.Tick(DateTimeOffset.UnixEpoch, _ => throw new InvalidOperationException("unknown"), _ => true));
        Assert.Equal(QuickReentryStatus.Failed, script.Status);
        Assert.Equal("unknown", script.Failure);
        Assert.Throws<InvalidOperationException>(() => script.Start(DateTimeOffset.UnixEpoch));
    }
}
