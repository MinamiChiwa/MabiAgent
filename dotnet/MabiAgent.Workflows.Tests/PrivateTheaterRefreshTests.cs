using MabiAgent.Workflows;
using Xunit;

namespace MabiAgent.Workflows.Tests;

public sealed class PrivateTheaterRefreshTests
{
    [Fact] public void DelayStartsAfterDispatchCompletes()
    {
        var now=DateTimeOffset.UnixEpoch;
        var script=new PrivateTheaterRefreshScript(new([
            new("click",PrivateTheaterStepKind.Click,"button",TimeSpan.FromSeconds(1)),
            new("next",PrivateTheaterStepKind.Click,"next",TimeSpan.Zero)]),()=>now);
        script.Start(now);
        Assert.True(script.Tick(now,_=>{now=now.AddSeconds(2);return true;}));
        Assert.False(script.Tick(now.AddMilliseconds(999),_=>throw new InvalidOperationException()));
        Assert.True(script.Tick(now.AddSeconds(1),_=>true));
        Assert.Equal(PrivateTheaterRefreshStatus.Complete,script.Status);
    }
    [Fact] public void StableWaitTimeoutEndsLocallyWithoutSendingOrReplay()
    {
        var script=new PrivateTheaterRefreshScript(new([new("load",PrivateTheaterStepKind.WaitForStable,"theater_label",TimeSpan.Zero){Timeout=TimeSpan.FromSeconds(2)}]));
        script.Start(DateTimeOffset.UnixEpoch);var sent=0;
        Assert.False(script.Tick(DateTimeOffset.UnixEpoch,_=>{sent++;return true;},_=>false));
        Assert.False(script.Tick(DateTimeOffset.UnixEpoch.AddSeconds(3),_=>{sent++;return true;},_=>false));
        Assert.Equal(PrivateTheaterRefreshStatus.Failed,script.Status);Assert.Equal(0,sent);
    }
    [Fact]
    public void DefaultScriptKeepsConfirmedOrderAndDoesNotInventCoordinates()
    {
        var script = new PrivateTheaterRefreshScript();
        var seen = new List<string>();
        script.Start(DateTimeOffset.UnixEpoch);
        while (script.Status == PrivateTheaterRefreshStatus.Running)
            script.Tick(DateTimeOffset.UnixEpoch, step => { seen.Add(step.Id + ":" + step.Target); return true; }, _ => true);

        Assert.Equal(PrivateTheaterRefreshStatus.Complete, script.Status);
        Assert.Equal([
            "open_bag:scene.open_bag",
            "tools_tab:bag_tools_tab",
            "tools_selected:bag.tools_selected",
            "game_tab:bag.games_tab",
            "private_theater_item:bag.private_theater_item",
            "use_item:bag.private_theater_item",
            "leader_review:private_theater.join_leader_review",
            "create:private_theater.create",
            "map_loaded:private_theater.map_loaded",
            "map_exit:private_theater.map_exit",
            "leave:private_theater.leave",
        ], seen);
    }

    [Fact]
    public void StableWaitAndPauseRequireExplicitProgress()
    {
        var definition = new PrivateTheaterScriptDefinition([
            new("wait", PrivateTheaterStepKind.WaitForStable, "stable.map", TimeSpan.Zero),
            new("leave", PrivateTheaterStepKind.PressSpace, "leave", TimeSpan.Zero),
        ]);
        var script = new PrivateTheaterRefreshScript(definition);
        script.Start(DateTimeOffset.UnixEpoch);
        Assert.False(script.Tick(DateTimeOffset.UnixEpoch, _ => true, _ => false));
        script.Pause();
        Assert.False(script.Tick(DateTimeOffset.UnixEpoch, _ => throw new InvalidOperationException(), _ => true));
        script.Continue(DateTimeOffset.UnixEpoch);
        Assert.True(script.Tick(DateTimeOffset.UnixEpoch, _ => true, _ => true));
        Assert.True(script.Tick(DateTimeOffset.UnixEpoch, _ => true, _ => true));
        Assert.Equal(PrivateTheaterRefreshStatus.Complete, script.Status);
    }

    [Fact]
    public void DispatchFailureEndsWithoutAutomaticReplay()
    {
        var script = new PrivateTheaterRefreshScript();
        script.Start(DateTimeOffset.UnixEpoch);
        Assert.False(script.Tick(DateTimeOffset.UnixEpoch, _ => throw new InvalidOperationException("unknown"), _ => true));
        Assert.Equal(PrivateTheaterRefreshStatus.Failed, script.Status);
        Assert.Equal("unknown", script.Failure);
        Assert.Throws<InvalidOperationException>(() => script.Start(DateTimeOffset.UnixEpoch));
    }
}
