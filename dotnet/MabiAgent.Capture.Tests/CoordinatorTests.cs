using System.Collections.Concurrent;
using System.Text.Json;
using MabiAgent.Capture;
using Xunit;

namespace MabiAgent.Capture.Tests;

public class CoordinatorTests
{
    [Fact] public async Task SameProcessReconnectRetainsOnlyIdentityAndDifferentProcessCannotReuseIt()
    {
        var opened=0;var environment=new Environment{Target=Target(1),SourceFactory=()=>Interlocked.Increment(ref opened)==1?new Source([1],9007199254740993,stopAfterInitial:true):new Source([1],9007199254740993,sendProfile:false)};
        await using var coordinator=new CaptureCoordinator(_=>InventoryTests.Login(),environment:environment,pollInterval:TimeSpan.FromMilliseconds(10));
        _=coordinator.RunAsync();await Until(()=>environment.Opened>=2&&coordinator.Snapshot().CurrentIdentity is not null);
        Assert.Equal(1,coordinator.Snapshot().Inventory!.FullSyncRevision);
        environment.Target=Target(2);coordinator.SelectProcess(2);
        await Until(()=>coordinator.Snapshot().Target?.ProcessId==2&&coordinator.Snapshot().Metrics.DecodedFrames>=1);
        Assert.Null(coordinator.Snapshot().CurrentIdentity);Assert.Null(coordinator.Snapshot().Inventory!.VerifiedThrough);
    }
    [Fact] public async Task AccountIdentityMetadataBindsFreshSceneSyncWithoutLoadingHistoricalQuantities()
    {
        var folder=Path.Combine(Path.GetTempPath(),"mabi-identity-seed-"+Guid.NewGuid().ToString("N"));
        try
        {
            var accounts=new AccountItemService(folder);var prior=new StreamingInventory(_=>InventoryTests.Login(quantity:777),"old");
            prior.Accept(new(0,GameProtocol.LoginOpcode,0,[1]),"old",1);prior.Accept(new(0,GameProtocol.ProfileOpcode,0,InventoryTests.Profile(9007199254740993)),"old",2);accounts.Record(prior.Snapshot());
            var environment=new Environment{Target=Target(1),SourceFactory=()=>new Source([1],9007199254740993,sendProfile:false)};
            await using var coordinator=new CaptureCoordinator(_=>InventoryTests.Login(quantity:19),accounts,environment,TimeSpan.FromMilliseconds(10));
            Assert.Null(coordinator.Snapshot().Inventory);_=coordinator.RunAsync();await Until(()=>coordinator.Snapshot().CurrentIdentity is not null);
            Assert.Equal(19,coordinator.Snapshot().Inventory!.Quantity(InventoryContainer.Backpack,123));
            Assert.Equal(3,coordinator.Snapshot().Inventory!.Counts.Count);
            await coordinator.StopAsync();
        }finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
    [Fact] public async Task InventoryChangesWakeSchedulerWithoutWakingOnEveryHealthPoll()
    {
        var source=new Source([1],9007199254740993);var environment=new Environment{Target=Target(1),SourceFactory=()=>source};
        await using var coordinator=new CaptureCoordinator(_=>InventoryTests.Login(),environment:environment,pollInterval:TimeSpan.FromMilliseconds(10));
        var changes=0;coordinator.Changed+=()=>Interlocked.Increment(ref changes);
        _=coordinator.RunAsync();await Until(()=>coordinator.Snapshot().State==CaptureState.Listening&&coordinator.Snapshot().CurrentIdentity is not null);
        await Task.Delay(35);var settled=Volatile.Read(ref changes);Assert.True(settled>0);
        await Task.Delay(35);Assert.Equal(settled,Volatile.Read(ref changes));
        source.Gap!("packet gap");await Until(()=>Volatile.Read(ref changes)>settled);
        Assert.Null(coordinator.Snapshot().CurrentIdentity);await coordinator.StopAsync();
    }
    [Fact] public async Task CaptureFailureKeepsFullExceptionContextAndReconnects()
    {
        var folder=Path.Combine(Path.GetTempPath(),"mabi-capture-fault-"+Guid.NewGuid().ToString("N"));
        try
        {
            var environment=new Environment{Target=Target(1),Failures=1};var log=new MabiAgent.Core.RuntimeDiagnostics(folder);
            await using var coordinator=new CaptureCoordinator(_=>InventoryTests.Login(),environment:environment,pollInterval:TimeSpan.FromMilliseconds(10),diagnostics:log);
            _=coordinator.RunAsync();await Until(()=>coordinator.Snapshot().CurrentIdentity is not null);await coordinator.StopAsync();
            var events=File.ReadLines(Path.Combine(folder,"runtime-events.jsonl")).Select(line=>JsonSerializer.Deserialize<MabiAgent.Core.RuntimeFaultEvent>(line)!).ToArray();
            var fault=Assert.Single(events,e=>e.Severity=="error");Assert.Equal("capture",fault.Scope);Assert.Contains("test adapter absent",fault.Exception);Assert.Contains("CreateSource",fault.Exception);Assert.NotNull(fault.Trigger);
        }finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
    [Fact] public async Task MissingProcessDoesNotStartSourceAndCanStop()
    {
        var environment = new Environment();
        await using var coordinator = new CaptureCoordinator(_ => InventoryTests.Login(), environment: environment, pollInterval: TimeSpan.FromMilliseconds(10));
        var running = coordinator.RunAsync();
        await Until(() => coordinator.Snapshot().State == CaptureState.WaitingForProcess);
        Assert.Equal(0, environment.Opened); Assert.Null(coordinator.Snapshot().CurrentIdentity);
        await coordinator.StopAsync(); await running;
        Assert.Equal(CaptureState.Stopped, coordinator.Snapshot().State);
    }
    [Fact] public async Task RealParserPublishesOnlyCurrentFullSyncAndGapInvalidatesImmediately()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/python-parity.json")));
        var body = Convert.FromBase64String(fixture.RootElement.GetProperty("body_base64").GetString()!);
        var character = long.Parse(fixture.RootElement.GetProperty("character_id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "dotnet/MabiAgent.Capture.Tests/Fixtures/client-inventory-schema.historical.json"))) root = root.Parent;
        Assert.NotNull(root);
        var decoder = ClientInventoryDecoder.Load(Path.Combine(root.FullName, "dotnet/MabiAgent.Capture.Tests/Fixtures/client-inventory-schema.historical.json"));
        var source = new Source(body, character); var environment = new Environment { Target = Target(1), SourceFactory = () => source };
        await using var coordinator = new CaptureCoordinator(decoder.Decode, environment: environment, pollInterval: TimeSpan.FromMilliseconds(10));
        _ = coordinator.RunAsync(); await Until(() => coordinator.Snapshot().CurrentIdentity is not null);
        var snapshot = coordinator.Snapshot(); Assert.Equal(character.ToString(), snapshot.CurrentIdentity!.CharacterId);
        Assert.False(snapshot.Inventory!.ExecutionEligible);
        await Until(()=>coordinator.Snapshot().Metrics.Packets==2);
        Assert.Equal(2,coordinator.Snapshot().Metrics.DecodedFrames);Assert.Equal(1,coordinator.Snapshot().Metrics.Connections);
        source.Gap!("test packet loss");
        Assert.Null(coordinator.Snapshot().CurrentIdentity);
        Assert.Null(coordinator.Snapshot().Inventory!.Quantity(InventoryContainer.Backpack, 123));
        await coordinator.StopAsync(); Assert.True(source.Disposed);
        Assert.Equal(new CaptureMetrics(),coordinator.Snapshot().Metrics);
    }
    [Fact] public async Task ProcessSwitchClearsIdentitySynchronouslyAndClosesOldSource()
    {
        var environment = new Environment { Target = Target(1) };
        await using var coordinator = new CaptureCoordinator(_ => InventoryTests.Login(), environment: environment, pollInterval: TimeSpan.FromMilliseconds(10));
        _ = coordinator.RunAsync(); await Until(() => coordinator.Snapshot().CurrentIdentity is not null);
        var first = environment.Sources.Single(); var oldScope = coordinator.Snapshot().Inventory!.Scope;
        environment.Target = Target(2); coordinator.SelectProcess(2);
        Assert.Null(coordinator.Snapshot().CurrentIdentity);
        await Until(() => coordinator.Snapshot().Target?.ProcessId == 2 && coordinator.Snapshot().CurrentIdentity is not null);
        Assert.True(first.Disposed); Assert.NotEqual(oldScope, coordinator.Snapshot().Inventory!.Scope);
    }
    [Fact] public async Task RetryAfterDeviceFailureAndPidReuseStartsFreshSession()
    {
        var environment = new Environment { Target = Target(1), Failures = 1 };
        await using var coordinator = new CaptureCoordinator(_ => InventoryTests.Login(), environment: environment, pollInterval: TimeSpan.FromMilliseconds(10));
        _ = coordinator.RunAsync(); await Until(() => coordinator.Snapshot().CurrentIdentity is not null);
        Assert.True(environment.Opened >= 2);
        var scope = coordinator.Snapshot().Inventory!.Scope;
        environment.Target = new(1, Target(1).StartedUtc.AddSeconds(1));
        await Until(() => coordinator.Snapshot().Inventory?.Scope is { } next && next != scope);
        Assert.NotEqual(scope, coordinator.Snapshot().Inventory!.Scope);
        await coordinator.StopAsync(); Assert.All(environment.Sources, s => Assert.True(s.Disposed));
    }
    [Fact] public async Task SelectedDevicesLimitListeningAndMissingChoiceIsReportedNotWidened()
    {
        var environment = new Environment { Target = Target(1), Devices = ["nic-a", "nic-b"] };
        await using var coordinator = new CaptureCoordinator(_ => InventoryTests.Login(), environment: environment, pollInterval: TimeSpan.FromMilliseconds(10));
        coordinator.SelectDevices(["nic-b"]);
        _ = coordinator.RunAsync(); await Until(() => coordinator.Snapshot().State == CaptureState.Listening && environment.Created.Contains("nic-b"));
        Assert.Equal(["nic-b"], coordinator.Snapshot().Devices); Assert.Equal(["nic-b"], environment.Created.ToArray());
        coordinator.SelectDevices(["nic-gone"]);
        await Until(() => coordinator.Snapshot().State == CaptureState.Retry && coordinator.Snapshot().Message.Contains("网卡"));
        Assert.DoesNotContain("nic-a", environment.Created);
        coordinator.SelectDevices([]);
        await Until(() => coordinator.Snapshot().State == CaptureState.Listening && coordinator.Snapshot().Devices.Count == 2);
        await coordinator.StopAsync();
    }
    [Fact] public async Task CorruptAssociationCsvIsolatesAccountWriteWithoutReconnectingCapture()
    {
        var folder=Path.Combine(Path.GetTempPath(),"mabi-corrupt-account-"+Guid.NewGuid().ToString("N"));
        try
        {
            var accounts=new AccountItemService(folder);var prior=new StreamingInventory(_=>InventoryTests.Login(),"old");
            prior.Accept(new(0,GameProtocol.LoginOpcode,0,[1]),"old",1);prior.Accept(new(0,GameProtocol.ProfileOpcode,0,InventoryTests.Profile(9007199254740993)),"old",2);accounts.Record(prior.Snapshot());
            File.WriteAllText(accounts.Files(prior.Snapshot().Identity!.AccountId).Associations,"item_id,name\n\"unclosed,");
            var environment=new Environment{Target=Target(1)};
            await using var coordinator=new CaptureCoordinator(_=>InventoryTests.Login(),accounts,environment,TimeSpan.FromMilliseconds(10));
            _=coordinator.RunAsync();await Until(()=>coordinator.Snapshot().AccountWriteError is not null);
            Assert.Equal(CaptureState.Listening,coordinator.Snapshot().State);Assert.Contains("账户记录未保存",coordinator.Snapshot().AccountWriteError);
            await Task.Delay(50);Assert.Equal(1,environment.Opened);Assert.Equal(CaptureState.Listening,coordinator.Snapshot().State);
            await coordinator.StopAsync();
        }finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
    private static CaptureTarget Target(int id) => new(id, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(5, timeout.Token); }
    private sealed class Environment : ICaptureEnvironment
    {
        public CaptureTarget? Target; public int Opened, Failures;
        public readonly ConcurrentBag<Source> Sources = [];
        public Func<Source>? SourceFactory;
        public CaptureTarget? FindTarget(int? selectedProcessId) => Target is { } target && (!selectedProcessId.HasValue || selectedProcessId == target.ProcessId) ? target : null;
        public string[] Devices = ["test-device"];
        public readonly ConcurrentQueue<string> Created = [];
        public IReadOnlyList<string> DiscoverDevices() => Devices;
        public IDisposable OpenEndpointFilter(CaptureTarget target, out Func<TcpEndpoint, bool> allows) { allows = _ => Target == target; return new Filter(); }
        public IPassivePacketSource CreateSource(string device)
        { Created.Enqueue(device); Interlocked.Increment(ref Opened); if (Interlocked.Decrement(ref Failures) >= 0) throw new IOException("test adapter absent"); var source = SourceFactory?.Invoke() ?? new Source([1], 9007199254740993); Sources.Add(source); return source; }
    }
    private sealed class Filter : IDisposable { public void Dispose() { } }
    private sealed class Source(byte[] body, long character,bool sendProfile=true,bool stopAfterInitial=false) : IPassivePacketSource
    {
        public Action<string>? Gap; public bool Disposed;
        public void Run(Action<TcpPacket> onPacket, Action<string> onGap, CancellationToken token)
        {
            Gap = onGap; var endpoint = new TcpEndpoint("127.0.0.1", 1111, "127.0.0.1", 5678);
            var login = ProtocolTests.Frame(GameProtocol.LoginOpcode, body);
            onPacket(new(endpoint, 100, login, false, false, false, 1));
            if(sendProfile)onPacket(new(endpoint, 100 + (uint)login.Length, ProtocolTests.Frame(GameProtocol.ProfileOpcode, InventoryTests.Profile(character)), false, false, false, 2));
            if(stopAfterInitial){token.WaitHandle.WaitOne(100);return;}
            token.WaitHandle.WaitOne();
        }
        public void Dispose() => Disposed = true;
    }
}
