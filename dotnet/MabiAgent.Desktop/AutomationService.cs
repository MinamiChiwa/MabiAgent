using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiAgent.Core;
using MabiAgent.Capture;
using MabiAgent.Windows;
using MabiAgent.Workflows;
using MabiAgent.Desktop.Editors;
using InventorySnapshot=MabiAgent.Core.InventorySnapshot;
namespace MabiAgent.Desktop;

// One worker and one gate own every workflow tick. Capture never sends input.
public sealed partial class AutomationService:IAsyncDisposable
{
    private readonly GameClientLayoutResolver clientLayout = new(NativeWindowsOps.ReadProcessIdentity);
    public string ClientLayoutStatus => clientLayout.Status;
    public bool ClientDirectoryRequired => clientLayout.NeedsDirectory || itemNamesDirectoryRequired;
    public async Task RetryClientDirectoryAsync(string directory)
    {
        clientLayout.FallbackDirectory=string.IsNullOrWhiteSpace(directory)?null:Path.GetFullPath(directory,workspace.Root);
        await RebindCaptureAsync();
    }
    private readonly Workspace workspace;private readonly GameWindowSelection selection;private readonly SemaphoreSlim gate=new(1,1);
    private readonly RuntimeActivity activity;private readonly SchedulingWake wake=new();
    public string ExecutionStage=>activity.Description;
    private readonly object lifecycle=new(),ocrGate=new();private readonly CancellationTokenSource lifetime=new();
    private CancellationTokenSource executionLifetime;private CancellationTokenSource? workbenchCancellation;
    private Task? worker,captureTask;private CancellationTokenSource? captureLifetime;private readonly CaptureCoordinator? capture;
    private readonly CaptureStatus captureUnavailable;private AutomationCoordinator? host;private ScreenWorkflowSession? session;private DesktopPorts? ports;private OnnxTextRecognizer? ocr;
    private InputOptions? hostInput;private long inputSettingsStamp=-1;
    private NativeWindowState? hostWindow;private int configurationRevision,builtRevision,commandRevision;private bool disposed,captureEnabled;private string? rebuildReason;
    private string? recoveryEpoch,recoveryIdentity;private bool recovering,resumeAfterRecovery;private int recoveryCommand;private double recoveryStartedAt;
    private bool refreshingInventory;private int refreshCommand;private double refreshStartedAt;private DateTimeOffset? refreshCompletedAt;
    public AccountItemService Accounts{get;}
    public EditorRepository Repository{get;}
    public IOcrEngine GetOcr(){lock(ocrGate){ObjectDisposedException.ThrowIf(disposed,this);return ocr??=new(FindModel(),lowCpu:ReadOcrLowCpu());}}
    internal bool OcrLoaded{get{lock(ocrGate)return ocr is not null;}}
    private bool ReadOcrLowCpu()=>ReadRuntimeSettings()["recognition"]?["low_cpu"]?.GetValue<bool>()??false;
    public IReadOnlyList<string> CaptureDeviceSetting=>ReadRuntimeSettings()["capture"]?["devices"] is JsonArray devices?devices.Select(d=>d?.GetValue<string>()).OfType<string>().ToArray():[];
    public string DiagnosticsFile=>Path.Combine(workspace.DataPath,"runtime-events.jsonl");
    public int? SelectedProcessId=>selection.Selected?.State.ProcessId;
    public IReadOnlyList<CaptureDevice> DiscoverCaptureDevices()=>NpcapPacketSource.DiscoverDeviceDetails();
    public void ApplyCaptureDevices(IReadOnlyCollection<string> devices){workspace.SaveCaptureDevices(devices);capture?.SelectDevices(devices);NotifyChanged();}
    public async Task RebindCaptureAsync()
    {
        clientLayout.ResetFailure();
        capture?.SelectProcess(selection.Selected?.State.ProcessId);
        await StopCaptureAsync().ConfigureAwait(false);StartCapture();
    }
    public void NotifyRuntimeChanged()
    {
        if(hostInput is {} prior)
        {
            var path=Path.Combine(workspace.DataPath,"settings.json");var stamp=File.Exists(path)?File.GetLastWriteTimeUtc(path).Ticks:0;
            if(Interlocked.Exchange(ref inputSettingsStamp,stamp)!=stamp)
                try{if(prior!=InputOptions.FromJson(ReadRuntimeSettings()))RequestRebuild("操作方案已变更，请重新开始");}
                catch(Exception error){RequestRebuild("操作设置无效："+error.Message);}
        }
        wake.Signal();
    }
    private void ApplyOcrPreferences()
    {lock(ocrGate)if(ocr is not null)try{ocr.ConfigureLowCpu(ReadOcrLowCpu());}catch(Exception error)when(error is not OutOfMemoryException){activity.Warning("ocr","识别省电设置未应用，沿用原配置："+error.Message);}}
    public CaptureStatus CaptureStatus=>capture is null?captureUnavailable:!captureEnabled?new(CaptureState.Stopped,"抓包已停止",null,null):capture.Snapshot();
    public AutomationStatus Status{get;private set;}=new(false,"未开始",null,new Dictionary<string,string>(),0);
    public DateTimeOffset Heartbeat{get;private set;}=DateTimeOffset.Now;
    private string? lastFault;
    private string? lastCaptureWarning,lastRuntimeWarning;
    public event Action? Changed;public bool IsRunning=>Status.Running||recovering||refreshingInventory;
    /// <summary>True once the input guard is paused, i.e. no input can be sent to the game.</summary>
    public bool InputPaused=>session?.Guard.Paused==true;
    public AutomationService(Workspace workspace,GameWindowSelection selection)
    {
        this.workspace=workspace;this.selection=selection;activity=new(workspace.DataPath);executionLifetime=new();
        Accounts=new(Path.Combine(workspace.DataPath,"accounts"),Path.Combine(workspace.Root,"config/items.verified.tw.json"),Path.Combine(workspace.Root,"config/item-name-candidates.tw.json"));Repository=new(workspace.Root,workspace.DataPath,ResolveVerifiedName);
        captureUnavailable=new(CaptureState.Stopped,"抓包不可用",null,null);
        try{capture=new(_=>throw new InvalidDataException("必须先解析当前客户端"),Accounts,diagnostics:new RuntimeDiagnostics(workspace.DataPath),resolveLayout:clientLayout.Resolve);capture.SelectProcess(selection.Selected?.State.ProcessId);capture.SelectDevices(CaptureDeviceSetting);}
        catch(Exception e)when(e is not OutOfMemoryException){captureUnavailable=captureUnavailable with{Message="抓包配置不可用："+e.Message};activity.Fault(e,"抓包配置",scope:"capture");}
        selection.Changed+=SelectionChanged;Repository.CatalogChanged+=CatalogChanged;workspace.Changed+=NotifyRuntimeChanged;if(capture is not null)capture.Changed+=NotifyRuntimeChanged;
        _=InitializeStopHotkeyAsync();
    }
    private static bool SameWindow(NativeWindowState? a,NativeWindowState? b)=>a is not null&&b is not null&&a.WindowId==b.WindowId&&a.ProcessId==b.ProcessId&&a.ProcessStartUtcTicks==b.ProcessStartUtcTicks&&string.Equals(a.Executable,b.Executable,StringComparison.OrdinalIgnoreCase)&&a.Geometry==b.Geometry;
    private void SelectionChanged(GameWindow? game)
    {if(disposed)return;capture?.SelectProcess(game?.State.ProcessId);if(hostWindow is not null&&!SameWindow(hostWindow,game?.State))RequestRebuild("游戏窗口已变化，请重新开始");wake.Signal();}
    private void CatalogChanged()=>RequestRebuild("任务链已更新，请重新开始");
    private long? ResolveVerifiedName(string name)
    {var selected=selection.Selected?.State;var raw=selected is null?null:DesktopPorts.VerifiedCapture(CaptureStatus,selected);return raw?.Scope is{} scope?Accounts.ReadNames(scope.AccountId).ResolveVerified(name):null;}
    private void RequestRebuild(string reason)
    {rebuildReason=reason;Interlocked.Increment(ref configurationRevision);session?.Guard.RequestPause();wake.Signal();}
    private void EnsureWorker(){lock(lifecycle){ObjectDisposedException.ThrowIf(disposed,this);if(worker is null||worker.IsCompleted)worker=Task.Run(()=>RunAsync(lifetime.Token));}}
    private CancellationToken ExecutionToken(){lock(lifecycle){ObjectDisposedException.ThrowIf(disposed,this);return executionLifetime.Token;}}
    private void ThrowIfStopResetPending(){if(File.Exists(Path.Combine(workspace.DataPath,"stop-reset.json")))throw new InvalidOperationException("上次停止重置未完成，请先重试停止全部");}
    private void NotifyChanged()
    {
        activity.Progress(Status.Message+" · "+Status.ActivePhase+" · 页面 "+(session?.CurrentPage??"未识别")+" · "+Status.PauseReason);
        if(Status.Fault is{} fault&&fault!=lastFault){lastFault=fault;RecordFault(fault);}
        var captureWarning=CaptureStatus.DiagnosticWriteError??CaptureStatus.AccountWriteError??(CaptureStatus.State==CaptureState.Retry?CaptureStatus.Message:null);
        if(captureWarning is not null&&captureWarning!=lastCaptureWarning)activity.Warning("capture",captureWarning,page:session?.CurrentPage);lastCaptureWarning=captureWarning;
        var warning=Status.PauseReason+":"+Status.Message;
        if(Status.PauseReason is not null&&warning!=lastRuntimeWarning)activity.Warning("runtime",Status.Message,session?.Scope.SessionId,Status.ActiveJob,session?.CurrentPage,"global");lastRuntimeWarning=warning;
        if(activity.DiagnosticWriteError is{} logError){var marker="诊断写入失败："+logError;if(Status.Fault?.Contains(marker,StringComparison.Ordinal)!=true)Status=Status with{Fault=(Status.Fault??"")+"\n"+marker};}
        foreach(var callback in Changed?.GetInvocationList()??[])try{((Action)callback)();}catch(Exception e){activity.Fault(e,"状态订阅",session?.Scope.SessionId,Status.ActiveJob,Status.ActivePhase,session?.CurrentPage,"subscriber");RecordFault("状态订阅异常："+e);}
    }
    private void RecordFault(string message)
    {
        try{var path=Path.Combine(workspace.DataPath,"runtime-faults.log");Directory.CreateDirectory(workspace.DataPath);
            if(File.Exists(path)&&new FileInfo(path).Length>262144){var lines=File.ReadAllLines(path);File.WriteAllLines(path,lines.TakeLast(100));}
            File.AppendAllText(path,DateTimeOffset.Now.ToString("O")+" "+message[..Math.Min(message.Length,8192)]+System.Environment.NewLine);}
        catch{}
    }
    internal async Task MutateQueueAsync(Action mutation)
    {
        await activity.AcquireAsync(gate,"编辑任务",lifetime.Token).ConfigureAwait(false);
        try{workspace.Read();mutation();host?.SynchronizeQueue();if(host is not null)Status=host.Status;NotifyChanged();}
        finally{gate.Release();wake.Signal();}
    }
    public void StartCapture()
    {
        EnsureWorker();lock(lifecycle)
        {if(capture is null)return;if(captureTask is{IsCompleted:false})return;captureLifetime?.Dispose();captureLifetime=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);captureEnabled=true;captureTask=capture.RunAsync(captureLifetime.Token);}
        wake.Signal();NotifyChanged();
    }
    public async Task StopCaptureAsync()
    {Task? pending;lock(lifecycle){captureEnabled=false;captureLifetime?.Cancel();pending=captureTask;}if(pending is not null)await pending.ConfigureAwait(false);wake.Signal();NotifyChanged();}
    // Read-only probe for the existing app diagnostic entry; no activation or input.
    public Task<ShopObservation> ObserveShopAsync()=>Task.Run(async()=>
    {
        await activity.AcquireAsync(gate,"只读观测",lifetime.Token).ConfigureAwait(false);using var stage=activity.Enter("只读观测",true);try
        {
            ObjectDisposedException.ThrowIf(disposed,this);var game=selection.Selected??throw new InvalidOperationException("请先选择游戏窗口");
            var routes=ScreenRoutes.FromConfiguration(workspace.Root,PublishedCatalogCompiler.CompileAvailable(Repository),Repository.UserAssetRoot);var catalog=new RuntimeCatalog(routes.Configuration);
            var bottles=catalog.Names.Where(p=>p.Value=="空瓶").ToArray();if(bottles.Length!=1)throw new InvalidOperationException("空瓶名称没有唯一目录关联");
            using var observed=new ScreenWorkflowSession(routes,game.State,new NativeWindowsOps(),GetOcr(),new ObservationStore());
            var readOnlyPorts=new DesktopPorts(observed,()=>CaptureStatus,game.State,new CapturePlanningAdapter(Accounts,catalog.Names),catalog,ReadRuntimeSettings);
            var port=observed.CreatePurchasePort(bottles[0].Key,bottles[0].Value,"read-only-shop",()=>null,()=>new(),readOnlyPorts.Inventory);
            return port.Observe();
        }finally{gate.Release();}
    });
    private sealed class ObservationStore:IWorkflowStore
    {public JsonObject? Read()=>null;public void Flush(JsonObject state)=>throw new InvalidOperationException("只读观测不写消费记录");}
    public Task StartAsync()
    {ThrowIfStopResetPending();EnsureWorker();var token=ExecutionToken();var command=Volatile.Read(ref commandRevision);return Task.Run(async()=>{await activity.AcquireAsync(gate,"开始",token).ConfigureAwait(false);using var stage=activity.Enter("开始或继续",true);try
        {
            ObjectDisposedException.ThrowIf(disposed,this);ThrowIfStopResetPending();token.ThrowIfCancellationRequested();if(recovering||refreshingInventory)throw new InvalidOperationException("公共恢复或库存刷新仍在执行，请先暂停");
            var game=selection.Selected??throw new InvalidOperationException("请先选择游戏窗口");
            if(host is not null&&(builtRevision!=Volatile.Read(ref configurationRevision)||!SameWindow(hostWindow,game.State)))DestroyHost();
            if(host is null||host.SafeToSwitch)ApplyOcrPreferences();
            if(host is null){BuildHost(game);try{if(command!=Volatile.Read(ref commandRevision))throw new InvalidOperationException("开始已取消");host!.Start();}catch{DestroyHost();throw;}}else{if(command!=Volatile.Read(ref commandRevision))throw new InvalidOperationException("继续已取消");host.Continue();}Status=host.Status;NotifyChanged();
        }catch(Exception e){activity.Fault(e,"开始或继续",session?.Scope.SessionId,Status.ActiveJob,Status.ActivePhase,session?.CurrentPage);RecordFault(e.ToString());session?.Pause("start_failed");Status=new(false,"无法开始："+e.Message,null,new Dictionary<string,string>(),0){Fault=e.ToString()};NotifyChanged();throw;}finally{gate.Release();wake.Signal();}});}
    private void BuildHost(GameWindow game)
    {
        using var stage=activity.Enter("加载任务配置与识别资源",true);
        ApplyOcrPreferences();
        var revision=Volatile.Read(ref configurationRevision);var routes=ScreenRoutes.FromConfiguration(workspace.Root,PublishedCatalogCompiler.CompileAvailable(Repository),Repository.UserAssetRoot);var catalog=new RuntimeCatalog(routes.Configuration);
        var scope=ScreenWorkflowSession.ProcessScope(game.State);var current=DesktopPorts.VerifiedCapture(CaptureStatus,game.State);
        if(current?.Scope is{} owner)scope=scope with{SessionId=scope.SessionId+":owner:"+JsonSerializer.Serialize(new[]{owner.AccountId,owner.CharacterId})};
        var input=InputOptions.FromJson(ReadRuntimeSettings());var diagnostics=new RuntimeDiagnostics(workspace.DataPath);var inputOps=InputTransportFactory.Create(game.State,input,diagnostics);
        if(inputOps is TouchInputOps touch)touch.DiagnosticWriteFailed+=failure=>{activity.Warning("touch",$"触控诊断写入失败：{failure.GestureId}/{failure.Stage}；{failure.Error}");NotifyChanged();};
        var next=new ScreenWorkflowSession(routes,game.State,inputOps,GetOcr(),new JsonWorkflowStore(Path.Combine(workspace.DataPath,"native-input-intents.json")),()=>NativeWindowActivation.Activate(game.State),scope,diagnostics,inputOptions:input);
        try
        {
            next.LifeVipEnabled=()=>DesktopPreferences.FromJson(ReadRuntimeSettings()).LifeVip;
            next.ConfigureTravel(new JsonWorkflowStore(Path.Combine(workspace.DataPath,"travel-wings.json")),ReadRuntimeSettings);
            next.ConfigureCharacterSelection(()=>DesktopPorts.VerifiedCapture(CaptureStatus,game.State)?.Identity is{} who?new CharacterSlotIdentity(who.AccountId,who.CharacterId,who.Label):null,
                Accounts.CharacterSlot,(who,slot)=>Accounts.RecordCharacterSlot(who.AccountId,who.CharacterId,slot));
            next.ConfigureRelogin(new JsonWorkflowStore(Path.Combine(workspace.DataPath,"relogin.json")));next.ConfigureRuntimeLimitRecognition(workspace.Root);
            var adapter=new DesktopPorts(next,()=>CaptureStatus,game.State,new CapturePlanningAdapter(Accounts,catalog.Names),catalog,ReadRuntimeSettings);
            var coordinator=new AutomationCoordinator(catalog,new RuntimeQueue(workspace.DataPath),adapter,ReadMaintenance);
            if(revision!=Volatile.Read(ref configurationRevision)||!SameWindow(game.State,selection.Selected?.State)){coordinator.Dispose();throw new InvalidOperationException("配置或窗口已变化，请重新开始");}
            session=next;hostInput=input;Interlocked.Exchange(ref inputSettingsStamp,-1);ports=adapter;host=coordinator;hostWindow=game.State;builtRevision=revision;rebuildReason=null;
        }catch{next.Dispose();throw;}
    }
    public string FindModel()
    {var path=Path.Combine(workspace.Root,"models",OnnxTextRecognizer.DefaultModelFileName);return File.Exists(path)?path:throw new FileNotFoundException("缺少 OCR 模型，请检查安装目录",path);}
    // Runtime reads must not advance Workspace's UI optimistic-write version.
    private JsonObject ReadRuntimeSettings()
    {var path=Path.Combine(workspace.DataPath,"settings.json");return File.Exists(path)?JsonNode.Parse(File.ReadAllBytes(path))!.AsObject():JsonNode.Parse("""
        {"repair":{"enabled":true,"interval_minutes":30,"gold_reserve_min":40000,"auto_buy_bottles":true},"backpack":{"enabled":true,"threshold_percent":90,"sell_surplus_materials":true,"material_reserve_min":300,"material_reserves":{}},"travel":{"auto_use_wings":true,"wing_reserve_min":0}}
        """)!.AsObject();}
    private MaintenancePolicy ReadMaintenance(){var p=DesktopPreferences.FromJson(ReadRuntimeSettings());return new(p.RepairEnabled,p.BackpackEnabled,p.RepairMinutes*60,p.GoldReserve,WeightThresholdPercent:p.BackpackThreshold,SellSurplus:p.SellSurplus,ProductionFirst:true,BackpackCheckIntervalSeconds:120);}
    private async Task RunAsync(CancellationToken token)
    {
        var delay=AutomationCadence.Health;
        try{while(!token.IsCancellationRequested)
        {
            await wake.WaitAsync(delay,token).ConfigureAwait(false);
            await gate.WaitAsync(token).ConfigureAwait(false);using var stage=activity.Enter("调度检查");try
            {
                var tickStarted=System.Diagnostics.Stopwatch.GetTimestamp();
                if(host is not null)
                {
                    if(builtRevision!=Volatile.Read(ref configurationRevision)){if(host.Status.Running||recovering)host.Pause("configuration_changed");recovering=false;Status=host.Status with{Message=rebuildReason??"配置已变化，请重新开始"};}
                    else if(ports!.IdentityChanged()){RequestRebuild("当前角色已变化，请重新开始");host.Pause("configuration_changed");recovering=false;Status=host.Status with{Message=rebuildReason!};}
                    else if(refreshingInventory)
                    {
                        using var refreshStage=activity.Enter("快速重进刷新库存");
                        if(refreshCommand!=Volatile.Read(ref commandRevision)||session!.Guard.Paused)
                        {refreshingInventory=false;resumeAfterRecovery=false;ports!.CancelInventorySync();host.Pause("manual");Status=host.Status with{ActivePhase="quick_reentry",Message="快速重进已暂停",PauseReason="manual"};NotifyChanged();continue;}
                        var advanced=ports!.TickQuickReentry(DateTimeOffset.UtcNow);
                        var complete=ports.QuickReentryStatus==QuickReentryStatus.Complete;
                        var failed=ports.QuickReentryStatus==QuickReentryStatus.Failed;
                        var fresh=ports.VerifiedCapture() is {} sync&&sync.FullSyncAt>refreshStartedAt&&ports.CurrentEpoch is{} epoch&&epoch!=recoveryEpoch&&(recoveryIdentity is null||ports.CurrentIdentity==recoveryIdentity);
                        Status=host.Status with{Running=false,ActivePhase="quick_reentry",Message=failed?"快速重进失败："+(ports.QuickReentryFailure??QuickReentryStepLabel(ports.QuickReentryStep)):complete&&fresh?"库存刷新已完成，请显式继续":"快速重进中 · "+QuickReentryStepLabel(ports.QuickReentryStep)};
                        if(complete)refreshCompletedAt??=DateTimeOffset.UtcNow;
                        var stopped=refreshCommand!=Volatile.Read(ref commandRevision)||session!.Guard.Paused;
                        var captureExpired=refreshCompletedAt is{} finished&&DateTimeOffset.UtcNow-finished>=TimeSpan.FromSeconds(session!.Routes.Configuration["quick_reentry"]?["capture_wait_s"]?.GetValue<int>()??8);
                        if(stopped||failed||complete&&(fresh||captureExpired))
                        {
                            refreshingInventory=false;
                            if(complete&&fresh&&resumeAfterRecovery&&!stopped){host.ContinueAfterInventorySync();Status=host.Status;}
                            else if(complete&&fresh&&!stopped)
                            {
                                host.Pause("inventory_sync_complete");
                                Status=host.Status with{Running=false,ActivePhase="quick_reentry",Message="库存刷新已完成，请显式继续",PauseReason="inventory_sync_complete"};
                            }
                            else if(stopped)
                            {var reason=failed?ports.QuickReentryFailure??"快速重进失败":"刷新已中止，未取得完整抓包";host.Pause("manual");Status=host.Status with{Running=false,ActivePhase="quick_reentry",Message=reason+"；已暂停",PauseReason="manual"};}
                            else
                            {
                                var reason=failed?ports.QuickReentryFailure??"快速重进失败":"快速重进已完成但未收到完整新抓包";
                                host.FinishInventorySyncWithoutCoverage(reason);Status=host.Status;
                            }
                            resumeAfterRecovery=false;
                        }
                    }
                    else if(recovering)
                    {
                        var fresh=ports.VerifiedCapture() is {} sync&&sync.FullSyncAt>recoveryStartedAt&&ports.CurrentEpoch is{} epoch&&epoch!=recoveryEpoch&&session!.Relogin!.MatchesPacketIdentity(new(sync.Identity!.AccountId,sync.Identity.CharacterId,sync.Identity.Label));
                        using var recoveryStage=activity.Enter("重登画面与库存核验");
                        var dismissed=session!.DismissNotice();
                        var state=session.Relogin!.Tick(fresh,dismissed);
                        Status=RecoveryStatus(host.Status,session.Relogin!.Snapshot(),session.Guard.Paused?session.Guard.PauseReason:null);
                        if(state!="running"||session.Guard.Paused)
                        {
                            recovering=false;
                            if(state=="complete"&&resumeAfterRecovery&&!session.Guard.Paused&&recoveryCommand==Volatile.Read(ref commandRevision)){host.ContinueAfterInventorySync();Status=host.Status;}
                            else
                            {
                                var reason=session.Guard.PauseReason??(state=="complete"?"inventory_sync_complete":"relogin_blocked");
                                session.Relogin.Pause();host.Pause(reason);
                                Status=RecoveryStatus(host.Status,session.Relogin.Snapshot(),reason);
                            }
                            resumeAfterRecovery=false;
                        }
                    }
                    else
                    {
                        using(var inventoryStage=activity.Enter("刷新库存依据"))ports.RefreshInventoryBasis();
                        if(host.Status.Running){using var tickStage=activity.Enter("执行任务："+(Status.ActivePhase??Status.Message));Status=host.Tick();}
                        else{host.ObservePausedWork();Status=Status with{ActiveConfirmed=host.Status.ActiveConfirmed,ReconciliationCount=host.Status.ReconciliationCount,Issues=host.Status.Issues};}
                        if(Status.Running&&session!.Guard.Paused){var reason=session.Guard.PauseReason;host.Pause(reason??"input_paused");Status=host.Status with{Message=reason=="restart_required"?"游戏运行时限已到，请重启后核账":"输入已暂停，请核实后继续"};}
                        else if(host.Status.Running&&ports.InventorySyncRequested&&host.SafeToSwitch)
                        {
                            if(captureEnabled&&CaptureStatus.State==CaptureState.Listening){ports.InventorySyncRequested=false;BeginQuickReentry(true);}
                            else Status=host.Status with{Message=captureEnabled?"快速重进刷新等待库存监听就绪":"快速重进刷新需要完整库存监听，请先开启监听"};
                        }
                    }
                }
                delay=recovering||refreshingInventory||ports?.InventorySyncRequested==true?AutomationCadence.Active:host?.NextCheckDelay()??AutomationCadence.Health;
                if(delay==AutomationCadence.Active||delay==AutomationCadence.Fishing)delay=TimeSpan.FromTicks(Math.Max(0,delay.Ticks-System.Diagnostics.Stopwatch.GetElapsedTime(tickStarted).Ticks));
            }catch(Exception e)
            {
                activity.Fault(e,"调度检查",session?.Scope.SessionId,Status.ActiveJob,Status.ActivePhase,session?.CurrentPage);
                Exception fault=e;
                try{session?.Pause("runtime_error");session?.Relogin?.Pause();host?.Pause("runtime_error");}
                catch(Exception pauseError){fault=new AggregateException("执行异常且暂停状态保存失败",e,pauseError);}
                recovering=false;refreshingInventory=false;resumeAfterRecovery=false;delay=AutomationCadence.Active;Status=Status with{Running=false,Message="执行已暂停："+e.Message,PauseReason="runtime_error",Fault=fault.ToString()};
            }finally{Heartbeat=DateTimeOffset.Now;gate.Release();}
            NotifyChanged();
        }}catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception e){activity.Fault(e,"调度线程停止",session?.Scope.SessionId,Status.ActiveJob,Status.ActivePhase,session?.CurrentPage);RecordFault("调度线程停止："+e);session?.Guard.RequestPause();recovering=false;resumeAfterRecovery=false;Status=Status with{Running=false,PauseReason="runtime_error",Message="调度线程异常，请核实后继续",Fault=e.ToString()};NotifyChanged();}
    }
    public Task ReloginAsync()
    {ThrowIfStopResetPending();EnsureWorker();var token=ExecutionToken();var command=Volatile.Read(ref commandRevision);return Task.Run(async()=>{await activity.AcquireAsync(gate,"登录恢复",token).ConfigureAwait(false);using var stage=activity.Enter("准备登录恢复",true);var created=false;var initiated=false;try
        {
            ObjectDisposedException.ThrowIf(disposed,this);ThrowIfStopResetPending();token.ThrowIfCancellationRequested();var game=selection.Selected??throw new InvalidOperationException("请先选择游戏窗口");
            if(host is not null&&(builtRevision!=Volatile.Read(ref configurationRevision)||!SameWindow(hostWindow,game.State)))DestroyHost();
            created=host is null;if(created)BuildHost(game);
            if(!host!.SafeToSwitch)throw new InvalidOperationException("当前批次或消费意图尚未到安全边界");
            if(command!=Volatile.Read(ref commandRevision))throw new InvalidOperationException("重登已取消");
            if(created)session!.Start();else session!.ResumeExplicit();BeginRecovery(false);initiated=true;
            Status=RecoveryStatus(host.Status,session.Relogin!.Snapshot(),null);NotifyChanged();
        }catch{if(created||initiated){session?.Relogin?.Pause();DestroyHost();}throw;}finally{gate.Release();wake.Signal();}});}
    // User-triggered inventory refresh: the same private theater script the scheduler uses, behind the same safety checks.
    public Task RefreshInventoryAsync()
    {ThrowIfStopResetPending();EnsureWorker();var token=ExecutionToken();var command=Volatile.Read(ref commandRevision);return Task.Run(async()=>{await activity.AcquireAsync(gate,"库存刷新",token).ConfigureAwait(false);using var stage=activity.Enter("准备快速重进刷新",true);var created=false;var initiated=false;try
        {
            ObjectDisposedException.ThrowIf(disposed,this);ThrowIfStopResetPending();token.ThrowIfCancellationRequested();var game=selection.Selected??throw new InvalidOperationException("请先选择游戏窗口");
            if(!captureEnabled||CaptureStatus.State!=CaptureState.Listening)throw new InvalidOperationException("请先开启库存监听并等待就绪");
            if(host is not null&&(builtRevision!=Volatile.Read(ref configurationRevision)||!SameWindow(hostWindow,game.State)))DestroyHost();
            created=host is null;if(created)BuildHost(game);
            if(!host!.CanPrepareInventoryRefresh)throw new InvalidOperationException("当前批次或消费意图尚未到安全边界");
            if(command!=Volatile.Read(ref commandRevision))throw new InvalidOperationException("库存刷新已取消");
            if(created)session!.Start();else session!.ResumeExplicit();BeginQuickReentry(false);initiated=true;NotifyChanged();
        }catch{if(created||initiated){ports?.CancelInventorySync();DestroyHost();}throw;}finally{gate.Release();wake.Signal();}});}
    private void BeginRecovery(bool automatic)
    {
        if(host is null||ports is null||session is null||!host.SafeToSwitch)throw new InvalidOperationException("当前批次尚未到安全边界");
        if(!captureEnabled||CaptureStatus.State!=CaptureState.Listening)throw new InvalidOperationException("请先开启库存监听并等待就绪");
        if(automatic&&session.Guard.Paused)throw new InvalidOperationException("输入已暂停，不启动重登");
        if(ports.IdentityChanged())throw new InvalidOperationException("当前角色已变化，请重新开始");
        var raw=ports.VerifiedCapture()??throw new InvalidOperationException("重登需要当前完整抓包身份，请先恢复监听");Accounts.Record(raw);
        session.Relogin!.Begin(new CharacterSlotIdentity(raw.Identity!.AccountId,raw.Identity.CharacterId,raw.Identity.Label),true,true);
        recoveryCommand=Volatile.Read(ref commandRevision);recoveryStartedAt=System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency;recoveryIdentity=ports.CurrentIdentity;recoveryEpoch=ports.CurrentEpoch;
        host.SuspendForInventorySync();session.ResumeWorkflow();
        if(recoveryCommand!=Volatile.Read(ref commandRevision)){session.Pause("manual");throw new InvalidOperationException("重登已取消");}
        resumeAfterRecovery=automatic;recovering=true;Status=RecoveryStatus(host.Status,session.Relogin!.Snapshot(),null);
    }
    private void BeginQuickReentry(bool automatic)
    {
        if(refreshingInventory||recovering)throw new InvalidOperationException("库存刷新已在进行中，请等待当前脚本结束");
        if(host is null||ports is null||session is null||!host.CanPrepareInventoryRefresh)throw new InvalidOperationException("当前批次尚未到安全边界");
        if(!captureEnabled||CaptureStatus.State!=CaptureState.Listening)throw new InvalidOperationException("请先开启库存监听并等待就绪");
        if(automatic&&session.Guard.Paused)throw new InvalidOperationException("输入已暂停，不启动快速重进");
        if(ports.IdentityChanged())throw new InvalidOperationException("当前角色已变化，请重新开始");
        refreshCommand=Volatile.Read(ref commandRevision);refreshCompletedAt=null;refreshStartedAt=System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency;recoveryIdentity=ports.CurrentIdentity;recoveryEpoch=ports.CurrentEpoch;
        host.SuspendForInventorySync();session.ResumeWorkflow();
        if(refreshCommand!=Volatile.Read(ref commandRevision)){session.Pause("manual");throw new InvalidOperationException("库存刷新已取消");}
        ports.BeginQuickReentry(DateTimeOffset.UtcNow);resumeAfterRecovery=automatic;refreshingInventory=true;Status=host.Status with{Running=false,ActivePhase="quick_reentry",Message="快速重进已开始"};
    }
    private static string QuickReentryStepLabel(string? step)=>step switch
    {"escape_menu"=>"按 ESC 打开菜单","menu_ready"=>"等待菜单出现","appearance"=>"点击變更外貌/重生","rebirth_ready"=>"等待重生页加载","escape_exit"=>"按 ESC 打开退出菜单","exit_menu_ready"=>"等待退出菜单","exit"=>"空格退出",_=>step??"等待库存"};
    internal static AutomationStatus RecoveryStatus(AutomationStatus prior,JsonObject recovery,string? pauseReason)
    {
        var state=recovery["status"]?.GetValue<string>();var phase=recovery["phase"]?.GetValue<string>();
        var message=state=="complete"?"登录恢复已完成，请显式继续":state=="running"&&pauseReason is null?"登录恢复："+ScreenRelogin.PhaseLabel(phase):recovery["reason"]?.GetValue<string>()??"重登已暂停，请核实当前页面";
        return prior with{Running=false,ActivePhase=phase,Message=message,PauseReason=state=="running"&&pauseReason is null?null:pauseReason??"relogin_blocked"};
    }
    public Task PauseAsync()
    {Interlocked.Increment(ref commandRevision);session?.Guard.RequestPause();wake.Signal();return Task.Run(async()=>{await activity.AcquireAsync(gate,"暂停",lifetime.Token).ConfigureAwait(false);using var stage=activity.Enter("暂停并保存",true);try{recovering=false;refreshingInventory=false;resumeAfterRecovery=false;session?.Relogin?.Pause();ports?.CancelInventorySync();host?.Pause();if(host is not null)Status=host.Status;NotifyChanged();}finally{gate.Release();wake.Signal();}});}
    private void DestroyHost(){host?.Dispose();host=null;session=null;ports=null;hostWindow=null;hostInput=null;recovering=false;refreshingInventory=false;}
    private Task? stopAllTask;
    public Task StopAllAsync()
    {
        session?.Guard.RequestPause();
        lock(lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(stopAllTask is{IsCompleted:false})return stopAllTask;
            return stopAllTask=StopAllCoreAsync();
        }
    }
    private Task StopAllCoreAsync()
    {
        Interlocked.Increment(ref commandRevision);session?.Guard.RequestPause();
        lock(lifecycle){executionLifetime.Cancel();workbenchCancellation?.Cancel();}wake.Signal();
        return Task.Run(async()=>
        {
            await activity.AcquireAsync(gate,"停止全部",lifetime.Token).ConfigureAwait(false);
            try
            {
                resumeAfterRecovery=false;ports?.CancelInventorySync();DestroyHost();
                Status=new(false,"正在清除全部执行状态",null,new Dictionary<string,string>(),0);
                new RuntimeQueue(workspace.DataPath).ResetAllExecution();
                lock(lifecycle){executionLifetime.Dispose();executionLifetime=new();}
                Status=new(false,"已停止全部，执行进度与核账已清除；再次开始将重新规划",null,new Dictionary<string,string>(),0);
            }
            catch(Exception error){Status=Status with{Message="停止重置未完成，请重试停止全部",Fault=error.ToString()};throw;}
            finally{NotifyChanged();gate.Release();wake.Signal();}
        });
    }
    public async ValueTask DisposeAsync()
    {
        stopHotkey?.Dispose();
        lock(lifecycle){if(disposed)return;disposed=true;Interlocked.Increment(ref commandRevision);lifetime.Cancel();executionLifetime.Cancel();workbenchCancellation?.Cancel();captureLifetime?.Cancel();captureEnabled=false;}
        selection.Changed-=SelectionChanged;Repository.CatalogChanged-=CatalogChanged;workspace.Changed-=NotifyRuntimeChanged;if(capture is not null)capture.Changed-=NotifyRuntimeChanged;session?.Guard.Pause("closed");
        if(worker is not null)await worker.ConfigureAwait(false);await gate.WaitAsync().ConfigureAwait(false);try{DestroyHost();lock(ocrGate){ocr?.Dispose();ocr=null;}}finally{gate.Release();}
        if(capture is not null)await capture.DisposeAsync();if(captureTask is not null)await captureTask.ConfigureAwait(false);captureLifetime?.Dispose();executionLifetime.Dispose();lifetime.Dispose();
    }
}

internal sealed class DesktopPorts:IAutomationPorts
{
    private readonly ScreenWorkflowSession session;private readonly Func<CaptureStatus> capture;private readonly NativeWindowState window;private readonly CapturePlanningAdapter mapping;
    private readonly RuntimeCatalog catalog;private readonly Func<JsonObject> settings;private readonly ScreenOwnerTracker ownerTracker=new();
    public string Scope=>session.Scope.SessionId;public string? CurrentDevice=>session.CurrentDevice;public IGatheringConsumables? GatheringConsumables{get;}
    public string? InputPauseReason=>session.Guard.Paused?session.Guard.PauseReason??"input_paused":null;
    public string? CurrentPage=>session.CurrentPage;
    public string? OnsiteDevice=>session.OnsiteDevice;
    public MaintenancePreferences PurchasePreferences=>MaintenancePreferences.FromJson(settings());
    public DesktopPorts(ScreenWorkflowSession session,Func<CaptureStatus> capture,NativeWindowState window,CapturePlanningAdapter mapping,RuntimeCatalog catalog,Func<JsonObject> settings)
    {
        this.session=session;this.capture=capture;this.window=window;this.mapping=mapping;this.catalog=catalog;this.settings=settings;
        session.SetVerifiedOwnerResolver(()=>VerifiedCapture()?.Scope is{} s?new SessionScope(s.SessionId,s.AccountId,s.CharacterId):null);
        session.SetPacketGoldProvider(()=>VerifiedCapture() is{Gold:>=0,GoldAt:{} at,Scope:{} scope} raw?new PacketGoldObservation(raw.Gold.Value,scope.SessionId+":"+raw.FullSyncRevision,at):null);
        _=IdentityChanged();
        GatheringConsumables=new GatheringConsumables(MabiAgent.Workflows.GatheringConsumables.RulesFor(catalog),Inventory,()=>PurchasePreferences);
    }
    internal MabiAgent.Capture.InventorySnapshot? VerifiedCapture()=>VerifiedCapture(capture(),window);
    internal static MabiAgent.Capture.InventorySnapshot? VerifiedCapture(CaptureStatus status,NativeWindowState window)
    {
        var raw=status.Inventory;
        if(status.Target is not{} target||target.ProcessId!=window.ProcessId||target.StartedUtc.ToUniversalTime().Ticks!=window.ProcessStartUtcTicks||raw?.Scope is not{} scope||raw.Identity is not{} identity||raw.VerifiedThrough is null||!double.IsFinite(raw.VerifiedThrough.Value)||raw.FullSyncRevision<=0||raw.Error is not null||scope.AccountId!=identity.AccountId||scope.CharacterId!=identity.CharacterId||string.IsNullOrWhiteSpace(scope.AccountId)||string.IsNullOrWhiteSpace(scope.CharacterId))return null;
        return raw;
    }
    internal string? CurrentIdentity=>VerifiedCapture()?.Scope is{} s?JsonSerializer.Serialize(new[]{s.AccountId,s.CharacterId}):null;
    internal string? CurrentEpoch=>VerifiedCapture() is{} r?CapturePlanningAdapter.ScopeKey(r.Scope!)+":"+r.FullSyncRevision:null;
    internal bool IdentityChanged(){var scope=VerifiedCapture()?.Scope;return ownerTracker.Observe(scope?.AccountId,scope?.CharacterId);}
    internal void RefreshInventoryBasis(){}
    public void Start()=>session.Start();public void Continue()=>session.ResumeExplicit();public void Pause(string reason)=>session.Pause(reason);public void Dispose(){session.Dispose();}
    public InventorySnapshot Inventory()
    {
        if(IdentityChanged())throw new InvalidOperationException("当前角色已变化，请重新开始任务");
        var result=new InventorySnapshot(Scope);var raw=VerifiedCapture();var mapped=mapping.Convert(raw);var epoch=raw is null?null:CapturePlanningAdapter.ScopeKey(raw.Scope!)+":"+raw.FullSyncRevision;
        foreach(var id in catalog.Names.Keys)
        {
            var packet=mapped.Inventory?.Query(id);
            if(packet is not null)result.Set(packet with{Scope=Scope});
        }
        if(mapped.Inventory is not null)foreach(var (scope,values) in mapped.Inventory.Storage)result.SetStorage(scope,values);return result;
    }
    public bool PreferBulkInventorySync=>capture().State==CaptureState.Listening;
    public QuickReentryStatus QuickReentryStatus=>refresh?.Status??QuickReentryStatus.Idle;
    public string? QuickReentryFailure=>refresh?.Failure;
    public string? QuickReentryStep=>refresh?.CurrentStep;
    public bool InventorySyncRequested{get;set;}
    public void RequestInventorySync(){InventorySyncRequested=true;}
    private ScreenQuickReentry? refresh;
    public void BeginQuickReentry(DateTimeOffset now){refresh=new ScreenQuickReentry(session);refresh.Start(now);}
    public bool TickQuickReentry(DateTimeOffset now)
    {
        if(refresh is null)return false;
        return refresh.Tick(now);
    }
    public void CancelInventorySync(){InventorySyncRequested=false;refresh?.Pause();}
    public void ResumeAfterInventorySync()=>session.ResumeWorkflow();
    public bool BeforeTick(bool safeBoundary)
    {if(session.Guard.Paused||!safeBoundary)return false;return session.DismissNotice()||session.TryTravel();}
    public bool BeforeNavigationTick()=>!session.Guard.Paused&&session.TryTravel();
    public IProductionPort Production(string key,string id,IWorkflowStore store)=>session.CreateProductionPort(key,id,store);
    public IGatheringPort Gathering(string key,string id,IWorkflowStore store)=>session.CreateGatheringPort(key,id,store);
    public IStorageSupplyPort Storage(WithdrawalPlan plan,string id,IWorkflowStore store)=>session.CreateStoragePort(plan,id,store,Inventory);
    public IFishingPort Fishing(string key,string id,IWorkflowStore store)=>session.CreateFishingPort(key,id,store);
    private InventorySnapshot InventoryWithDecompositionInput()
    {
        var result=Inventory();var decomposition=catalog.Configuration["decomposition"];
        if(decomposition?["input_wire_id"] is not {} input)return result;
        var item=input.GetValue<long>();var raw=VerifiedCapture();var quantity=raw?.Quantity(MabiAgent.Capture.InventoryContainer.Backpack,checked((uint)item));
        result.Set(new(item,quantity,Scope,"packet_capture",raw?.Scope?.SessionId+":"+raw?.Revision,quantity is not null));return result;
    }
    public IDecompositionPort Decomposition(string id,IWorkflowStore store)=>new ScreenDecompositionPort(session,id,store,InventoryWithDecompositionInput);
    public IMaintenancePort Maintenance(string id,IWorkflowStore store)=>session.CreateMaintenancePort(id,store);
    public bool IsExplicitSurplusMaterial(long item)=>DesktopPreferences.FromJson(settings()).IsExplicitSurplusMaterial(item);
    public IPurchasePort Purchase(GatheringSupplyRequest request,string batchId,Func<PurchaseState?> persisted)=>session.CreatePurchasePort(request.ItemId,request.Name,batchId,persisted,()=>PurchasePreferences,()=>Inventory());
    public ISurplusMaintenancePort Surplus(string batchId,IWorkflowStore store,Func<long,long?> requiredReserve)=>session.CreateSurplusPort(batchId,store,id=>
    DesktopPreferences.FromJson(settings()).RequiredMaterialReserve(id,requiredReserve(id)),InventoryWithDecompositionInput);
}
