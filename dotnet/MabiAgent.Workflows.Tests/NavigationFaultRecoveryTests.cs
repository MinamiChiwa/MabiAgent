using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;

namespace MabiAgent.Workflows.Tests;

public sealed class NavigationFaultRecoveryTests
{
    [Fact]
    public void NavigationTimeoutStopsRepeatedDispatchUntilExplicitContinue()
    {
        var clock = new Clock();
        var port = new Port(clock) { Unknown = true };
        var runner = new IdleFishingRunner(new("point", 3600), port, new MemoryStore(), time: clock);
        var faults = new List<string>();
        var dispatcher = new WorkflowDispatcher(clock, (_, _, boundary) => faults.Add(boundary));
        dispatcher.Register("fish", runner);
        dispatcher.Continue();
        WorkCandidate[] plan = [new("fish", "idle_fishing", null, true, 0, 0)];
        dispatcher.Tick(plan, null);
        dispatcher.Tick(plan, null);
        clock.Seconds = 181;
        Assert.IsType<TimeoutException>(Record.Exception(() => dispatcher.Tick(plan, null)));

        for (var i = 0; i < 5; i++) Assert.Null(Record.Exception(() => dispatcher.Tick(plan, null)));
        Assert.Single(faults);
        Assert.Contains("fish", dispatcher.Faults.Keys);
        Assert.Equal("paused", runner.Snapshot()["status"]!.GetValue<string>());
        Assert.Empty(port.Actions);

        dispatcher.Continue(false);
        dispatcher.Tick(plan, null);
        Assert.Contains("fish", dispatcher.Faults.Keys);
        Assert.Empty(port.Actions);
        port.Unknown = false;
        dispatcher.Continue();
        dispatcher.Tick(plan, null);
        dispatcher.Tick(plan, null);
        Assert.Empty(dispatcher.Faults);
        Assert.Equal("navigate", Assert.Single(port.Actions));
    }

    [Fact]
    public void FailedObservationPreservesPendingCastAndStopsRepeatedFaultReports()
    {
        var clock = new Clock();
        var store = new MemoryStore();
        var port = new Port(clock);
        var runner = new IdleFishingRunner(new("point", 3600), port, store, time: clock);
        var state = runner.Snapshot();
        var pending = new JsonObject { ["kind"] = "start", ["at"] = 0d, ["deadline"] = 20d, ["capture_id"] = "original" };
        state["pending"] = pending.DeepClone();
        state["phase"] = "confirm_start";
        state["elapsed_s"] = 120d;
        store.Flush(state);
        runner = new(new("point", 3600), port, store, time: clock);
        var faults = 0;
        var dispatcher = new WorkflowDispatcher(clock, (_, _, _) => faults++);
        dispatcher.Register("fish", runner);
        dispatcher.RestoreOwnership("fish");
        dispatcher.Continue();
        WorkCandidate[] plan = [new("fish", "idle_fishing", null, true, 0, 0)];
        dispatcher.Tick(plan, null);
        port.FailObservation = true;
        Assert.IsType<TimeoutException>(Record.Exception(() => dispatcher.Tick(plan, null)));
        for (var i = 0; i < 5; i++) Assert.Null(Record.Exception(() => dispatcher.Tick(plan, null)));

        Assert.Equal(1, faults);
        Assert.Equal("fish", dispatcher.ActiveId);
        Assert.False(dispatcher.SafeToSwitch);
        Assert.True(JsonNode.DeepEquals(pending, runner.Snapshot()["pending"]));
        Assert.Equal(120d, runner.Snapshot()["elapsed_s"]!.GetFiniteDouble());
        Assert.Empty(port.Actions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SentNavigationContinuesObservationAfterFaultWithoutReplay(bool explicitContinue)
    {
        var clock = new Clock();
        var port = new Port(clock);
        var runner = new IdleFishingRunner(new("point", 3600), port, new MemoryStore(), time: clock);
        var faults = 0;
        var dispatcher = new WorkflowDispatcher(clock, (_, _, _) => faults++);
        dispatcher.Register("fish", runner);
        dispatcher.Continue();
        WorkCandidate[] plan = [new("fish", "idle_fishing", null, true, 0, 0)];
        dispatcher.Tick(plan, null);
        dispatcher.Tick(plan, null);
        Assert.Equal("navigate", Assert.Single(port.Actions));
        var deadline = runner.Snapshot()["navigation_deadline"]!.GetFiniteDouble();

        port.FailObservation = true;
        clock.Seconds = 1;
        Assert.Throws<TimeoutException>(() => dispatcher.Tick(plan, null));
        port.FailObservation = false;
        port.NavigationActive = true;
        clock.Seconds = 2;
        if (explicitContinue)
        {
            dispatcher.Continue();
            dispatcher.Tick(plan, null);
            Assert.NotNull(runner.Snapshot()["navigation_deadline"]);
            Assert.Equal(deadline, runner.Snapshot()["navigation_deadline"]!.GetFiniteDouble());
        }
        dispatcher.Tick(plan, null);
        Assert.True(runner.Snapshot()["navigation_seen"]!.GetValue<bool>());

        port.NavigationActive = false;
        clock.Seconds = 3;
        dispatcher.Tick(plan, null);
        Assert.Equal("refresh", runner.Snapshot()["phase"]!.GetValue<string>());
        if (!explicitContinue) Assert.Null(dispatcher.ActiveId);
        Assert.Equal("navigate", Assert.Single(port.Actions));
        Assert.Equal(1, faults);
    }

    [Fact]
    public void OtherNavigationCompletionReleasesFaultedWaitWithoutSendingInput()
    {
        var clock = new Clock();
        var port = new Port(clock) { NavigationActive = true };
        var runner = new IdleFishingRunner(new("point", 3600), port, new MemoryStore(), time: clock);
        var dispatcher = new WorkflowDispatcher(clock);
        dispatcher.Register("fish", runner);
        dispatcher.Continue();
        WorkCandidate[] plan = [new("fish", "idle_fishing", null, true, 0, 0)];
        dispatcher.Tick(plan, null);
        dispatcher.Tick(plan, null);
        clock.Seconds = 181;
        Assert.Throws<TimeoutException>(() => dispatcher.Tick(plan, null));
        Assert.Equal("fish", dispatcher.ActiveId);

        port.NavigationActive = false;
        clock.Seconds = 182;
        dispatcher.Tick(plan, null);
        Assert.Null(dispatcher.ActiveId);
        Assert.Contains("fish", dispatcher.Faults.Keys);
        Assert.Empty(port.Actions);
    }

    private sealed class Clock : TimeProvider
    {
        public double Seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Seconds);
    }

    private sealed class Port(Clock clock) : IFishingPort
    {
        public bool Unknown, FailObservation;
        public bool? NavigationActive;
        public bool NavigationIssued { get; private set; }
        public List<string> Actions = [];
        private int capture;
        public FishingFrame Observe() => FailObservation ? throw new TimeoutException("capture unavailable")
            : new(clock.Seconds, (++capture).ToString(), Unknown ? "unknown" : "scene", Stable: !Unknown, NavigationActive: NavigationActive);
        public void Perform(FishingAction action)
        {
            Actions.Add(action.Kind);
            if (action.Kind == "navigate") NavigationIssued = true;
        }
        public void Pause(string reason) { }
        public void Resume() { }
    }
}
