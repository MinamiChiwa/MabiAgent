using System.Diagnostics;
using System.Text.Json;
using MabiAgent.Core;

namespace MabiAgent.Capture;

public sealed record CaptureTarget(int ProcessId, DateTime StartedUtc);
public sealed record CaptureDevice(string Name, string Description);
public enum CaptureState { Stopped, WaitingForProcess, Starting, Listening, Retry }
public sealed record CaptureStatus(CaptureState State, string Message, CaptureTarget? Target, InventorySnapshot? Inventory)
{
    public CharacterIdentity? CurrentIdentity => Inventory?.VerifiedThrough is not null ? Inventory.Identity : null;
    public string? AccountWriteError { get; init; }
    public string? DiagnosticWriteError{get;init;}
    public CaptureMetrics Metrics { get; init; }=new();
    public IReadOnlyList<string> Devices { get; init; }=[];
}
public interface ICaptureEnvironment
{
    CaptureTarget? FindTarget(int? selectedProcessId);
    IReadOnlyList<string> DiscoverDevices();
    IDisposable OpenEndpointFilter(CaptureTarget target, out Func<TcpEndpoint, bool> allows);
    IPassivePacketSource CreateSource(string device);
}

/// <summary>Optional background planning feed. Missing capture never blocks screen startup.</summary>
public sealed class CaptureCoordinator : IAsyncDisposable
{
    public event Action? Changed;
    private readonly object gate = new();
    private readonly Func<byte[], LoginInventory> decodeLogin;
    private readonly Func<CaptureTarget,CancellationToken,ClientCaptureLayout>? resolveLayout;
    private readonly CaptureProtocol? protocol;
    private readonly Func<byte[],IReadOnlyList<ConsumableStack>>? decodeUpdates;
    private readonly AccountItemService? accounts;
    private readonly ICaptureEnvironment environment;
    private readonly TimeSpan interval;
    private CancellationTokenSource? lifetime, attempt;
    private Task? running;
    private int? selected;
    private long selectionVersion;
    private HashSet<string> preferredDevices = new(StringComparer.Ordinal);
    private bool disposed;
    private StreamingInventory? currentInventory;
    private CaptureTarget? identityTarget;
    private IReadOnlyList<CharacterIdentity> retainedIdentities=[];
    private readonly IReadOnlyList<CharacterIdentity> identityMetadata;
    private readonly Action<GameFrame,string,double>? observeFrame;
    private CaptureStatus status = new(CaptureState.Stopped, "抓包未启动", null, null);
    private readonly RuntimeDiagnostics? diagnostics;private readonly string trace=Guid.NewGuid().ToString("N");private string? lastFault;
    private void Record(Exception error,string stage)
    {var key=stage+":"+error.GetType().FullName+":"+error.Message;if(key==lastFault)return;lastFault=key;diagnostics?.Write(diagnostics.Create(trace,"error","capture",stage,"capture",Snapshot().Inventory?.Scope?.SessionId,null,null,null,"bounded_reconnect",error.Message,error,trigger:"capture > "+stage));}
    public CaptureCoordinator(Func<byte[], LoginInventory> decodeLogin, AccountItemService? accounts = null, ICaptureEnvironment? environment = null, TimeSpan? pollInterval = null,RuntimeDiagnostics? diagnostics=null,IEnumerable<CharacterIdentity>? identityMetadata=null,Action<GameFrame,string,double>? observeFrame=null,Func<CaptureTarget,CancellationToken,ClientCaptureLayout>? resolveLayout=null,CaptureProtocol? protocol=null,Func<byte[],IReadOnlyList<ConsumableStack>>? decodeUpdates=null)
    {
        this.diagnostics=diagnostics;this.observeFrame=observeFrame;this.resolveLayout=resolveLayout;this.protocol=protocol;this.decodeUpdates=decodeUpdates;
        this.decodeLogin = decodeLogin; this.accounts = accounts; this.environment = environment ?? new WindowsCaptureEnvironment();
        this.identityMetadata=identityMetadata?.Take(256).ToArray()??[];
        interval = pollInterval ?? TimeSpan.FromSeconds(1);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }
    public CaptureStatus Snapshot() { lock (gate) return (currentInventory is null ? status : status with { Inventory = currentInventory.Snapshot() }) with{DiagnosticWriteError=diagnostics?.WriteError}; }
    public void SelectProcess(int? processId)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (selected == processId) return;
            selected = processId; selectionVersion++; attempt?.Cancel();
            currentInventory = null;
            identityTarget=null;retainedIdentities=[];
            status = new(CaptureState.WaitingForProcess, "等待所选游戏进程", null, null);
        }
    }
    /// <summary>Restricts listening to the named devices; empty means every available device. Restarts the current attempt.</summary>
    public void SelectDevices(IEnumerable<string>? names)
    {
        var next = new HashSet<string>((names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Take(64), StringComparer.Ordinal);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (next.SetEquals(preferredDevices)) return;
            preferredDevices = next; selectionVersion++; attempt?.Cancel(); currentInventory = null;
            status = new(CaptureState.Starting, "网卡设置已变化，重新连接", null, null);
        }
    }
    public IReadOnlyCollection<string> PreferredDevices { get { lock (gate) return preferredDevices.ToArray(); } }
    private string[] UsableDevices()
    {
        HashSet<string> preferred; lock (gate) preferred = preferredDevices;
        var all = environment.DiscoverDevices().Distinct(StringComparer.Ordinal).ToArray();
        // A chosen device that disappeared is reported, never silently replaced by every adapter.
        var chosen = preferred.Count == 0 ? all : all.Where(preferred.Contains).ToArray();
        if (preferred.Count > 0 && chosen.Length == 0) throw new IOException("所选抓包网卡当前不可用，请在抓包设置中重新选择");
        return chosen;
    }
    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running is { IsCompleted: false }) throw new InvalidOperationException("Capture already running");
            lifetime?.Dispose(); lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return running = Task.Run(() => LoopAsync(lifetime.Token), CancellationToken.None);
        }
    }
    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                long version; int? pid;
                lock (gate) { version = selectionVersion; pid = selected; }
                try
                {
                    var target = environment.FindTarget(pid);
                    if (target is null) Set(version, new(CaptureState.WaitingForProcess, "等待唯一或所选游戏进程", null, null));
                    else await CaptureAsync(target, pid, version, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { }
                catch (Exception error) { Record(error,"listen");Set(version, new(CaptureState.Retry, "抓包暂不可用：" + error.Message[..Math.Min(200, error.Message.Length)], null, null)); }
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { lock (gate) { currentInventory = null; status = new(CaptureState.Stopped, "抓包已停止", null, null); } }
    }
    private async Task CaptureAsync(CaptureTarget target, int? pid, long version, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (gate) { if (selectionVersion != version) return; attempt = stop; }
        Task? capture = null; IDisposable? filter = null; PassiveCaptureService? service = null;
        try
        {
            Set(version, new(CaptureState.Starting, "连接被动抓包设备", target, null));
            var layout=resolveLayout?.Invoke(target,stop.Token);
            var decoder=layout?.CreateDecoder();
            Func<byte[],LoginInventory> decode=decoder is null?decodeLogin:decoder.Decode;
            var devices = UsableDevices();
            if (devices.Length == 0 || devices.Length > 64) throw new IOException("未找到可用抓包设备");
            filter = environment.OpenEndpointFilter(target, out var allows);
            IReadOnlyList<CharacterIdentity> seeds;
            lock(gate)
            {
                if(identityTarget!=target){identityTarget=target;retainedIdentities=[];}
                seeds=retainedIdentities.Concat(identityMetadata).ToArray();
            }
            // Historical account files contribute identity edges only. All
            // quantities and coverage still require this attempt's full frame.
            if(accounts is not null)
            {
                try{seeds=seeds.Concat(accounts.KnownCaptureIdentities()).GroupBy(i=>i.CharacterId,StringComparer.Ordinal).Select(g=>g.First()).Take(256).ToArray();}
                catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException){Record(error,"identity_metadata");}
            }
            var inventory = new StreamingInventory(decode, Guid.NewGuid().ToString("N"),seeds,layout?.Protocol??protocol,decoder is null?decodeUpdates:decoder.DecodeUpdates);
            lock (gate) { if (selectionVersion != version) return; currentInventory = inventory; }
            service = new PassiveCaptureService(inventory, () => new DeviceGroup(devices, environment), endpoint =>
            {
                    lock (gate) return selectionVersion == version && !stop.IsCancellationRequested && allows(endpoint);
            },observeFrame);
            capture = service.RunAsync(stop.Token); long recordedRevision = -1; var devicesCheckedAt = Stopwatch.StartNew(); string? accountError = null;var accountRetryAt=DateTimeOffset.MinValue;
            while (!capture.IsCompleted && !stop.IsCancellationRequested)
            {
                if (environment.FindTarget(pid) != target) { stop.Cancel(); break; }
                if (devicesCheckedAt.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    devicesCheckedAt.Restart();
                    if (!devices.ToHashSet(StringComparer.Ordinal).SetEquals(UsableDevices())) { stop.Cancel(); break; }
                }
                var current = inventory.Snapshot();
                lock(gate)if(selectionVersion==version&&identityTarget==target)retainedIdentities=inventory.KnownIdentities;
                if(service.LastException is{} decodeError)Record(decodeError,"decode");
                Set(version, new(CaptureState.Listening, current.VerifiedThrough.HasValue ? "当前会话库存已同步" : "监听中，等待完整库存", target, current) { AccountWriteError = accountError,Metrics=service.MetricsSnapshot(),Devices=devices });
                if (current.VerifiedThrough.HasValue && current.Revision != recordedRevision && DateTimeOffset.UtcNow>=accountRetryAt)
                {
                    try { accounts?.Record(current); recordedRevision=current.Revision; accountError = null; }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                    { Record(error,"account_write");accountError = "账户记录未保存：" + error.Message[..Math.Min(200, error.Message.Length)]; }
                    accountRetryAt=DateTimeOffset.UtcNow.AddSeconds(accountError is null?0:2);
                }
                await Task.WhenAny(capture, Task.Delay(interval, stop.Token)).ConfigureAwait(false);
            }
        }
        finally
        {
            stop.Cancel();
            Set(version, new(CaptureState.Retry, "连接已结束，等待重新同步", null, null));
            try { if (capture is not null) await capture.ConfigureAwait(false); }
            finally { service?.Dispose(); filter?.Dispose(); lock (gate) { if (ReferenceEquals(attempt, stop)) { attempt = null; currentInventory = null; } } }
        }
    }
    private void Set(long version, CaptureStatus next)
    {
        bool changed;
        lock(gate)
        {
            if(selectionVersion!=version)return;
            changed=status.State!=next.State||status.Target!=next.Target||status.Message!=next.Message
                ||status.Inventory?.Revision!=next.Inventory?.Revision||status.Inventory?.FullSyncRevision!=next.Inventory?.FullSyncRevision
                ||status.AccountWriteError!=next.AccountWriteError;
            status=next;
        }
        if(changed)foreach(var callback in Changed?.GetInvocationList()??[])
            try{((Action)callback)();}catch(Exception error)when(error is not OutOfMemoryException){Record(error,"status_subscriber");}
    }
    public async Task StopAsync()
    {
        Task? task; lock (gate) { lifetime?.Cancel(); attempt?.Cancel(); currentInventory = null; status = new(CaptureState.Stopped, "抓包已停止", null, null); task = running; }
        if (task is not null) await task.ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) disposed = true; await StopAsync().ConfigureAwait(false);
        lock (gate) { lifetime?.Dispose(); lifetime = null; }
        GC.SuppressFinalize(this);
    }
    private sealed class DeviceGroup(string[] devices, ICaptureEnvironment environment) : IPassivePacketSource
    {
        public void Run(Action<TcpPacket> onPacket, Action<string> onGap, CancellationToken token)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var tasks = devices.Select(device => Task.Factory.StartNew(() =>
            {
                try { using var source = environment.CreateSource(device); source.Run(onPacket, onGap, stop.Token); }
                finally { stop.Cancel(); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
        }
        public void Dispose() { }
    }
}

public sealed class WindowsCaptureEnvironment : ICaptureEnvironment
{
    public CaptureTarget? FindTarget(int? selectedProcessId)
    {
        var targets = new List<CaptureTarget>();
        foreach (var process in Process.GetProcessesByName("MabinogiMobile"))
            using (process)
            {
                if (selectedProcessId.HasValue && process.Id != selectedProcessId) continue;
                try { if (!process.HasExited) targets.Add(new(process.Id, process.StartTime.ToUniversalTime())); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        return targets.Count == 1 ? targets[0] : null;
    }
    public IReadOnlyList<string> DiscoverDevices() => NpcapPacketSource.DiscoverDevices();
    public IDisposable OpenEndpointFilter(CaptureTarget target, out Func<TcpEndpoint, bool> allows)
    {
        var filter = new ProcessEndpointFilter(target.ProcessId, target.StartedUtc); allows = filter.Allows; return filter;
    }
    public IPassivePacketSource CreateSource(string device) => new NpcapPacketSource(device);
}
