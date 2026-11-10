using System.Text.Json.Nodes;
using MabiAgent.Workflows;
using Xunit;

namespace MabiAgent.Windows.Tests;

public sealed class FishingNavigationTimeoutTests
{
    [Fact]
    public void SelectionBudgetExpiresOnUnknownFramesAndReleasesUnsentNavigation()
    {
        var routes = ScreenRoutes.Load(RecognitionTests.Root());
        routes.Configuration["notifications"] = new JsonObject();
        var clock = new Clock();
        var ops = new Ops(Stamp(routes.Profile, "scene_bag", "maint_scene_map"));
        using var session = new ScreenWorkflowSession(routes, Window(), ops, new EmptyOcr(), new Store());
        session.Start();
        var store = new Store(new() { ["status"] = "running", ["phase"] = "navigate" });
        var port = new ScreenFishingPort(session, "point_fishing_03", "selection-timeout", store, clock);
        Assert.Equal("scene", port.Observe().Page);
        port.Perform(new("navigate", "point_fishing_03"));
        Assert.False(port.CanReleaseForeground());
        var actions = ops.Actions;
        ops.Image = new(1280, 960, new byte[1280 * 960 * 3]);
        clock.Seconds = 89;
        Assert.Equal("unknown", port.Observe().Page);
        clock.Seconds = 90;
        Assert.Throws<TimeoutException>(() => port.Observe());
        Assert.True(port.CanReleaseForeground());
        Assert.Null(session.CurrentNavigationId);
        Assert.Equal(actions, ops.Actions);
    }

    [Fact]
    public void PassiveObservationRetainsSelectionBudgetUntilRunningObservation()
    {
        var routes = ScreenRoutes.Load(RecognitionTests.Root());
        routes.Configuration["notifications"] = new JsonObject();
        var clock = new Clock();
        var ops = new Ops(Stamp(routes.Profile, "scene_bag", "maint_scene_map"));
        using var session = new ScreenWorkflowSession(routes, Window(), ops, new EmptyOcr(), new Store());
        session.Start();
        var store = new Store(new() { ["status"] = "running", ["phase"] = "navigate" });
        var port = new ScreenFishingPort(session, "point_fishing_03", "passive-selection", store, clock);
        port.Observe();
        port.Perform(new("navigate", "point_fishing_03"));
        var actions = ops.Actions;
        ops.Image = new(1280, 960, new byte[1280 * 960 * 3]);
        clock.Seconds = 90;

        Assert.Equal("unknown", port.ObserveOnly().Page);
        Assert.False(port.CanReleaseForeground());
        Assert.Throws<TimeoutException>(() => port.Observe());
        Assert.True(port.CanReleaseForeground());
        Assert.Equal(actions, ops.Actions);
    }

    [Fact]
    public void SentNavigationSurvivesSelectionBudgetAndPortRecreation()
    {
        var routes = ScreenRoutes.Load(RecognitionTests.Root());
        routes.Configuration["notifications"] = new JsonObject();
        var clock = new Clock();
        var ops = new Ops(Stamp(routes.Profile, "preset_catalog_point_fishing_03_name", "preset_catalog_point_fishing_03_find"));
        var journal = new Store();
        using var session = new ScreenWorkflowSession(routes, Window(), ops, new EmptyOcr(), journal);
        session.Start();
        var store = new Store(new() { ["status"] = "running", ["phase"] = "navigate" });
        var port = new ScreenFishingPort(session, "point_fishing_03", "sent-navigation", store, clock);
        Assert.Equal("point_detail_fishing_3", port.Observe().Page);
        port.Perform(new("navigate", "point_fishing_03"));
        Assert.True(port.NavigationIssued);
        Assert.NotNull(session.CurrentNavigationId);
        var actions = ops.Actions;
        ops.Image = new(1280, 960, new byte[1280 * 960 * 3]);
        clock.Seconds = 90;
        Assert.Equal("unknown", port.Observe().Page);
        port.AbandonUnsentNavigation();
        Assert.True(port.NavigationIssued);

        var restored = new ScreenFishingPort(session, "point_fishing_03", "sent-navigation", store, clock);
        clock.Seconds = 181;
        Assert.Equal("unknown", restored.ObserveOnly().Page);
        Assert.True(restored.NavigationIssued);
        Assert.False(restored.CanReleaseForeground());
        ops.Image = Stamp(routes.Profile, "scene_bag", "maint_scene_map");
        clock.Seconds = 182;
        restored.ObserveOnly();
        clock.Seconds = 185;
        Assert.False(restored.ObserveOnly().NavigationActive);
        Assert.False(restored.NavigationIssued);
        Assert.Null(session.CurrentNavigationId);
        Assert.Equal(actions, ops.Actions);
    }

    [Fact]
    public void SelectionTimeoutRetainsObservedNavigationAcrossUnknownFrames()
    {
        var routes = ScreenRoutes.Load(RecognitionTests.Root());
        routes.Configuration["notifications"] = new JsonObject();
        var clock = new Clock();
        var ops = new Ops(Stamp(routes.Profile, "scene_bag", "maint_scene_map"));
        using var session = new ScreenWorkflowSession(routes, Window(), ops, new EmptyOcr(), new Store());
        session.Start();
        var store = new Store(new() { ["status"] = "running", ["phase"] = "navigate" });
        var port = new ScreenFishingPort(session, "point_fishing_03", "external-navigation", store, clock);
        port.Observe();
        port.Perform(new("navigate", "point_fishing_03"));
        var actions = ops.Actions;
        ops.Image = Stamp(routes.Profile, "scene_bag", "maint_scene_map", "navigation_task_prefix");
        clock.Seconds = 1;
        Assert.True(port.Observe().NavigationActive);
        clock.Seconds = 90;
        Assert.Throws<TimeoutException>(() => port.Observe());
        Assert.False(port.CanReleaseForeground());

        ops.Image = new(1280, 960, new byte[1280 * 960 * 3]);
        port.ObserveOnly();
        Assert.False(port.CanReleaseForeground());
        ops.Image = Stamp(routes.Profile, "scene_bag", "maint_scene_map");
        clock.Seconds = 91;
        Assert.False(port.ObserveOnly().NavigationActive);
        Assert.True(port.CanReleaseForeground());
        Assert.Equal(actions, ops.Actions);
    }

    private static RgbImage Stamp(NavigationProfile profile, params string[] names)
    {
        var pixels = new byte[1280 * 960 * 3];
        foreach (var name in names)
        {
            var template = profile.Templates[name];
            var source = template.Image.CopyPixels();
            for (var y = 0; y < template.Image.Height; y++) Array.Copy(source, y * template.Image.Width * 3, pixels, ((template.Box.Top + y) * 1280 + template.Box.Left) * 3, template.Image.Width * 3);
        }
        return new(1280, 960, pixels);
    }

    private static NativeWindowState Window() => new(1, 3, 42, @"C:\Games\mabinogimobile.exe", "UnityWndClass", new(0, 0, 1280, 960, 96), new(0, 0, 1280, 960), 1, true, false, true, new(0, 0));

    private sealed class Clock : TimeProvider
    {
        public double Seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Seconds);
    }

    private sealed class EmptyOcr : IOcrEngine
    {
        public OcrLine? Recognize(int width, int height, byte[] pixels) => null;
    }

    private sealed class Store(JsonObject? initial = null) : IWorkflowStore
    {
        private JsonObject? value = initial;
        public JsonObject? Read() => value?.DeepClone().AsObject();
        public void Flush(JsonObject state) => value = state.DeepClone().AsObject();
    }

    private sealed class Ops(RgbImage image) : INativeWindowsOps, IGuardedWindowTransport
    {
        public RgbImage Image = image;
        public int Actions;
        public bool RequiresForeground => false;
        public void BindGuard(ForegroundInputGuard guard) { }
        public NativeWindowState ReadWindow(nint hwnd) => Window();
        public RgbImage CaptureClient(NativeWindowState window) => Image;
        public void MoveClient(nint hwnd, PixelPoint point) => Actions++;
        public void MouseButton(MouseButton button, bool down) { if (down) Actions++; }
        public void Wheel(int delta) => Actions++;
        public void Key(ushort key, bool down, bool unicode = false) { if (down) Actions++; }
        public bool AnyPhysicalKeyDown() => false;
    }
}
