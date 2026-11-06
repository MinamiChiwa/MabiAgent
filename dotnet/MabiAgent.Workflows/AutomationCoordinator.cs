using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MabiAgent.Core;
namespace MabiAgent.Workflows;

public sealed record AutomationJobStatus(string ActionId,string? Status,string? Phase,string? Reason,bool PendingConsumption)
{public long Confirmed{get;init;}}
public sealed record AutomationStatus(bool Running,string Message,string? ActiveJob,IReadOnlyDictionary<string,string> Issues,int PlanRevision)
{public string? PauseReason{get;init;} public int RecoveryAttempt{get;init;} public string? Fault{get;init;} public long? ActiveConfirmed{get;init;} public string? ActivePhase{get;init;} public int ReconciliationCount{get;init;} public IReadOnlyDictionary<string,AutomationJobStatus> Jobs{get;init;}=new Dictionary<string,AutomationJobStatus>();}
public sealed class AutomationCoordinator:IDisposable
{
    private static long ConfirmedProgress(Run run,JsonObject state)=>run.Runner switch
    {
        ProductionRunner=>(state["completed"]?.GetValue<int>()??0)*(run.Batch["output_per_round"]?.GetValue<long>()??1),
        GatheringRunner=>state["completed"]?.GetValue<int>()??0,
        IdleFishingRunner=>state["elapsed_s"] is{} elapsed?(long)Math.Floor(elapsed.GetFiniteDouble()):0,_=>0
    };
    private sealed record Run(JsonObject Batch,PersistentWorkflow Runner,string Folder);
    private readonly RuntimeCatalog catalog;private readonly RuntimeQueue queue;private readonly IAutomationPorts ports;
    private readonly WorkflowDispatcher dispatcher;private readonly TimeProvider clock;private readonly RuntimeInventory inventory;
    private readonly RuntimeDiagnostics diagnostics;private string traceId=Guid.NewGuid().ToString("N"),stage="restore";
    private readonly Dictionary<string,string> recordedWarnings=[];private bool maintenanceFaulted;
    private readonly Dictionary<string,string> recordedRunnerFaults=[],recordedObservations=[];
    private readonly Dictionary<string,string> recordedStates=[];
    private readonly Dictionary<string,Run> runs=[];private readonly Dictionary<string,string> issues=[];
    private readonly Dictionary<string,string> deferred=[];
    private readonly Dictionary<string,double> deviceRetry=[];
    private readonly Dictionary<string,string> gatheringSupplyVersions=[];
    private bool inventorySyncRequested,inventorySyncFailed;private string? inventorySyncKey;
    private string? pauseReason;private int recoveryAttempt;private DateTimeOffset retryAfter;
    private StorageSupplyRunner? withdrawal;private string? withdrawalKey;private string? withdrawalVersion;private bool withdrawalFaulted;
    private BottlePurchase? purchase;private GatheringSupplyRequest? purchaseRequest;private string? purchaseAction;private bool purchaseRestoreBlocked;private double? purchaseCompletionBaseline;
    private readonly IDecompositionPort? decomposition;private string? decompositionVersion;
    private MaintenancePolicy appliedPolicy;
    private MaintenancePreferences? purchasePreferences;
    private readonly Dictionary<string,PlannedTask> tasks=[];private readonly Dictionary<string,long> readyRounds=[];
    private readonly Func<MaintenancePolicy> preferences;private readonly MaintenanceRunner maintenance;
    private InventorySnapshot current;private string? planFingerprint;private MaterialPlan? plan;private bool running,disposed,started,readyAtLastPlan=true;private int planRevision;
    public AutomationStatus Status {get;private set;}=new(false,"尚未开始",null,new Dictionary<string,string>(),0);
    public bool SafeToSwitch
    {
        get
        {
            var state=maintenance.StatusSnapshot();
            return dispatcher.SafeToSwitch&&StorageReleasable
                &&purchase is null&&!purchaseRestoreBlocked&&decomposition?.Snapshot()["status"]?.GetValue<string>()!="running"&&state["pending"] is null&&state["navigation_reserved"]?.GetValue<bool>()!=true;
        }
    }
    // A storage exit that already gave up must not keep the whole queue parked.
    private bool StorageReleasable=>withdrawal is null||withdrawalFaulted||withdrawal.NavigationAbandoned
        ||withdrawal.SafeToSwitch&&(withdrawal.Snapshot()["pending"] is null||withdrawal.NavigationReleased);
    public bool CanPrepareInventoryRefresh
    {
        get
        {
            var state=maintenance.StatusSnapshot();
            var pending=withdrawal?.Snapshot()["pending"];
            return dispatcher.SafeToSwitch&&(withdrawal is null||withdrawalFaulted||withdrawal.NavigationAbandoned
                    ||pending is null||pending["kind"]?.GetValue<string>()=="prepare_transfer"||withdrawal?.NavigationReleased==true)
                &&purchase is null&&!purchaseRestoreBlocked&&decomposition?.Snapshot()["status"]?.GetValue<string>()!="running"&&state["pending"] is null
                &&state["navigation_reserved"]?.GetValue<bool>()!=true;
        }
    }
    // Read under the host's existing single-worker gate. A long wait is only an
    // optimization of a proven idle boundary, never permission to skip confirmation.
    public TimeSpan NextCheckDelay()
    {
        if(running&&recoveryAttempt>0)
        {
            var retry=AutomationCadence.Until(Now,(retryAfter-DateTimeOffset.UnixEpoch).TotalSeconds);
            return retry<=TimeSpan.Zero?AutomationCadence.Active:retry;
        }
        var auxiliary=maintenance.StatusSnapshot();
        if(!SafeToSwitch||auxiliary["surplus"]?["pending"] is not null||withdrawal is not null||purchase is not null||inventorySyncRequested)
            return AutomationCadence.Active;
        foreach(var run in runs.Values)
        {
            var state=run.Runner.Snapshot();
            if(state["pending"] is not null||state["status"]?.GetValue<string>()=="unknown"
                ||state["status"]?.GetValue<string>()!="complete"&&(run.Runner is not ProductionRunner||state["phase"]?.GetValue<string>()!="waiting"))
                return AutomationCadence.Active;
        }
        if(!running)return AutomationCadence.Health;
        if(plan is null||plan.Issues.Any(i=>i.Kind=="inventory_unknown")||readyAtLastPlan)
            return AutomationCadence.Active;
        var delay=AutomationCadence.Health;
        foreach(var retry in deviceRetry.Values.Where(at=>at>Now))
        {
            var scheduled=AutomationCadence.Until(Now,retry);
            if(scheduled<delay)delay=scheduled;
        }
        if(appliedPolicy.RepairEnabled&&!maintenanceFaulted&&auxiliary["repair"]?["status"]?.GetValue<string>()=="idle"&&auxiliary["repair"]?["due_at"] is{} repair)
        {var scheduled=AutomationCadence.Until(Now,repair.GetFiniteDouble());if(scheduled<delay)delay=scheduled;}
        foreach(var run in runs.Values.Where(r=>r.Runner is ProductionRunner))
        {
            var state=run.Runner.Snapshot();
            if(state["status"]?.GetValue<string>()=="complete")continue;
            var due=((ProductionRunner)run.Runner).NextCollectionAt(run.Batch["recipe"]?["device"]?.GetValue<string>()==ports.OnsiteDevice);
            var scheduled=AutomationCadence.Until(Now,due);
            if(scheduled<delay)delay=scheduled;
        }
        return delay<=TimeSpan.Zero?AutomationCadence.Active:delay;
    }
    public AutomationCoordinator(RuntimeCatalog catalog,RuntimeQueue queue,IAutomationPorts ports,Func<MaintenancePolicy>? preferences=null,TimeProvider? time=null)
    {
        this.catalog=catalog;this.queue=queue;this.ports=ports;this.preferences=preferences??(()=>new());clock=time??TimeProvider.System;
        diagnostics=new(queue.DirectoryPath,clock);dispatcher=new(clock,OnRunnerFault);current=new(ports.Scope);
        inventory=new(new JsonWorkflowStore(Path.Combine(queue.DirectoryPath,"runtime-inventory.json")));
        var store=new JsonWorkflowStore(Path.Combine(queue.DirectoryPath,"maintenance.json"));
        appliedPolicy=this.preferences();
        maintenance=new(ports.Maintenance("maintenance",store),store,appliedPolicy,clock,surplus:ports.Surplus("maintenance-surplus",new JsonWorkflowStore(Path.Combine(queue.DirectoryPath,"surplus-sale.json")),RequiredReserve));
        decomposition=ports.Decomposition("material-decomposition",new JsonWorkflowStore(Path.Combine(queue.DirectoryPath,"decomposition.json")));
        RestoreReservations();Restore();RestoreWithdrawal();RestorePurchase();
    }
    private double Now=>(clock.GetUtcNow()-DateTimeOffset.UnixEpoch).TotalSeconds;
    private void Record(string severity,string module,string boundary,string recovery,string message,Exception? error=null,string? job=null)
    {
        var run=job is null?null:runs.GetValueOrDefault(job);var state=run?.Runner.Snapshot();
        diagnostics.Write(diagnostics.Create(traceId,severity,module,stage,boundary,ports.Scope,job,run?.Batch["action_id"]?.GetValue<string>(),
            ports.CurrentPage,recovery,message,error,state?["pending"] is not null,"AutomationCoordinator.Tick > "+stage));
    }
    private void OnRunnerFault(string job,Exception error,string boundary)
    {issues[job]="任务异常："+error.Message;Record("error","workflow",boundary,boundary=="task"?"explicit_continue":"observe_then_explicit_continue",error.Message,error,job);}
    private void StartMaintenanceSchedule()
    {maintenance.RestartRepairSchedule();maintenance.RestartBackpackSchedule();started=true;}
    public void Start()
    {ObjectDisposedException.ThrowIf(disposed,this);ports.Start();StartMaintenanceSchedule();ResetInventorySync(true);maintenanceFaulted=false;recoveryAttempt=0;pauseReason=null;retryAfter=DateTimeOffset.MinValue;running=true;ApplyQueueControls();dispatcher.Continue();withdrawal?.Resume(true);maintenance.Resume(true);Publish("正在核验任务与库存");}
    public void Continue()
    {ObjectDisposedException.ThrowIf(disposed,this);ports.Continue();if(!started)StartMaintenanceSchedule();ResetInventorySync(true);if(decompositionVersion=="pending-refresh")decompositionVersion=null;foreach(var key in issues.Keys.Where(k=>k.StartsWith("inventory:",StringComparison.Ordinal)||dispatcher.Faults.ContainsKey(k)||k=="maintenance_fault"||withdrawalFaulted&&k==withdrawalKey).ToArray())issues.Remove(key);maintenanceFaulted=false;withdrawalFaulted=false;recoveryAttempt=0;pauseReason=null;retryAfter=DateTimeOffset.MinValue;running=true;ApplyQueueControls();dispatcher.Continue();withdrawal?.ResetRefreshRequest();withdrawal?.Resume(true);maintenance.Resume(true);Publish("继续执行");}
    public void ContinueAfterInventorySync()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(ManualPausePending){Publish("库存同步完成，等待显式继续");return;}
        ports.ResumeAfterInventorySync();inventorySyncFailed=false;ResetInventorySync();running=true;ApplyQueueControls();dispatcher.Continue(false);if(!withdrawalFaulted)withdrawal?.Resume(true);if(!maintenanceFaulted)maintenance.Resume(true);Publish("库存同步完成，继续执行");
    }
    public void FinishInventorySyncWithoutCoverage(string reason)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(ManualPausePending)
        {
            inventorySyncFailed=true;issues["inventory_sync"]=reason+"；已暂停，等待显式继续";Publish(reason+"；已暂停");return;
        }
        ports.ResumeAfterInventorySync();inventorySyncFailed=true;ResetInventorySync();issues["inventory_sync"]=reason+"；等待后续库存同步，独立任务继续";running=true;pauseReason=null;ApplyQueueControls();dispatcher.Continue(false);if(!withdrawalFaulted)withdrawal?.Resume(true);if(!maintenanceFaulted)maintenance.Resume(true);Publish(reason+"；相关材料保持未知，独立任务继续");
    }
    private bool ManualPausePending=>pauseReason=="manual"||dispatcher.PauseReason=="manual";
    public void Pause(string reason="manual")
    {running=false;pauseReason=reason;ports.Pause(reason);dispatcher.Pause(reason);withdrawal?.Pause(reason);maintenance.Pause(reason);Publish("已暂停");}
    public void SuspendForInventorySync()
    {running=false;ports.Pause("inventory_sync");dispatcher.Pause("inventory_sync");withdrawal?.Pause("inventory_sync");maintenance.Pause("inventory_sync");Publish("准备同步库存");}
    public void ObservePausedFishing()
        =>ObservePausedWork();
    public void ObservePausedWork()
    {
        if(running)return;
        ApplyQueueControls();
        if(runs.Count==0&&withdrawal is null&&purchase?.State.Intent is null&&maintenance.StatusSnapshot()["pending"] is null&&maintenance.StatusSnapshot()["surplus"]?["pending"] is null)return;
        var jobs=queue.Read().Jobs.ToDictionary(job=>job.Id);
        foreach(var (id,run) in runs)
        {
            try
            {
                if(run.Runner is IdleFishingRunner fishing){if(jobs.GetValueOrDefault(id)?.Cancelled==true&&fishing.Snapshot()["stop_requested"]?.GetValue<bool>()!=true)fishing.StopSubmitting();fishing.ObserveOnly();}
                else if(run.Runner is ProductionRunner production)production.ObserveOnly();else if(run.Runner is GatheringRunner gathering)gathering.ObserveOnly();
                recordedObservations.Remove(id);
            }
            catch(Exception error)when(error is not OutOfMemoryException)
            {if(recordedObservations.GetValueOrDefault(id)!=error.Message){recordedObservations[id]=error.Message;Record("error","passive_observation","task","observe_only",error.Message,error,id);}}
        }
        ObserveAuxiliary("storage",()=>withdrawal?.ObserveOnly());
        ObserveAuxiliary("maintenance",maintenance.ObserveOnly);
        ObserveAuxiliary("purchase",()=>purchase?.ObserveOnly());
        Reconcile();Publish(Status.Message);
    }
    private void ObserveAuxiliary(string module,Action observe)
    {
        try{observe();recordedObservations.Remove(module);}
        catch(Exception error)when(error is not (IOException or UnauthorizedAccessException or OutOfMemoryException))
        {if(recordedObservations.GetValueOrDefault(module)!=error.Message){recordedObservations[module]=error.Message;Record("error",module,"module","observe_only",error.Message,error);}}
    }
    private void Publish(string message)
    {
        var snapshots=runs.Select(p=>(p.Key,p.Value.Runner.Snapshot())).Append(("maintenance",maintenance.StatusSnapshot())).ToList();
        if(withdrawal is not null)snapshots.Add(("storage",withdrawal.Snapshot()));
        foreach(var (id,state) in snapshots)
        {
            var reason=state["reason"]?.GetValue<string>()??state["pause_reason"]?.GetValue<string>()??state["paused"]?.GetValue<string>();
            if(reason is not null&&recordedStates.GetValueOrDefault(id)!=reason){recordedStates[id]=reason;Record("warning",id,state["pending"] is null?"task":"foreground",state["pending"] is null?"review":"observe_only",reason,job:runs.ContainsKey(id)?id:null);}
            else if(reason is null)recordedStates.Remove(id);
            if(state["last_fault"] is not JsonObject failure||failure["id"]?.GetValue<string>() is not{} failureId||recordedRunnerFaults.GetValueOrDefault(id)==failureId)continue;
            recordedRunnerFaults[id]=failureId;
            diagnostics.Write(diagnostics.Create(traceId,"error",failure["module"]!.GetValue<string>(),failure["phase"]?.GetValue<string>()??stage,
                "foreground",ports.Scope,id,runs.GetValueOrDefault(id)?.Batch["action_id"]?.GetValue<string>(),ports.CurrentPage,
                "observe_then_explicit_continue",failure["operation"]!.GetValue<string>()) with
                {EventId=failureId,At=DateTimeOffset.Parse(failure["at"]!.GetValue<string>()),Exception=failure["exception"]!.GetValue<string>(),Pending=failure["pending"]!.GetValue<bool>(),Trigger="runner > "+failure["operation"]!.GetValue<string>()});
        }
        foreach(var key in recordedRunnerFaults.Keys.Where(k=>k is not ("maintenance" or "storage")&&!runs.ContainsKey(k)).ToArray()){recordedRunnerFaults.Remove(key);recordedStates.Remove(key);}
        foreach(var (key,warning) in issues)
            if(recordedWarnings.GetValueOrDefault(key)!=warning){recordedWarnings[key]=warning;Record("warning",key,"dependency","resolve_dependency",warning,job:runs.ContainsKey(key)?key:null);}
        foreach(var key in recordedWarnings.Keys.Where(k=>!issues.ContainsKey(k)).ToArray())recordedWarnings.Remove(key);
        long? confirmed=null;string? phase=null;
        if(dispatcher.ActiveId is{} active&&runs.TryGetValue(active,out var run))
        {
            var state=run.Runner.Snapshot();phase=state["phase"]?.GetValue<string>();
            confirmed=ConfirmedProgress(run,state);
        }
        var visibleIssues=new Dictionary<string,string>(issues);if(diagnostics.WriteError is{} logError)visibleIssues["diagnostics"]="故障日志写入失败："+logError;
        foreach(var missing in plan?.Issues.Where(i=>i.Kind=="no_source")??[])
        {
            var name=catalog.Names.GetValueOrDefault(missing.ItemId,missing.ItemId.ToString());
            var source=catalog.Configuration["material_sources"]?[missing.ItemId.ToString()]?["kind"]?.GetValue<string>();
            if(name is "空瓶" or "紙"&&ports.PurchasePreferences.AutoBuyBottles&&!issues.ContainsKey("purchase"))continue;
            if(source=="decomposition"&&decomposition?.Snapshot()["status"]?.GetValue<string>()=="running")continue;
            if(source=="fishing"&&queue.Read().Jobs.Any(j=>j.Kind=="idle_fishing"&&j.ItemId==missing.ItemId&&!j.Cancelled&&!j.Paused&&j.Target>j.Completed))continue;
            var detail=name is "空瓶" or "紙"?"，等待补料":source=="decomposition"?"，请分解樹枝补充；其他队列继续":source=="fishing"?"，按来源钓鱼补料，以抓包确认库存；其他队列继续":"，不会自动购买，请手动补充；其他队列继续";
            visibleIssues["material:"+missing.ItemId]=name+"材料不足（背包及仓库不足）"+detail;
        }
        foreach(var (id,state) in snapshots)
            if(state["status"]?.GetValue<string>() is "unknown" or "blocked"||state["paused"] is not null||state["status"]?.GetValue<string>()=="paused"&&state["pause_reason"]?.GetValue<string>() is not ("manual" or "closed"))
                visibleIssues.TryAdd(id,state["reason"]?.GetValue<string>()??state["pause_reason"]?.GetValue<string>()??state["paused"]?.GetValue<string>()??"等待核实");
        var maintenanceState=maintenance.StatusSnapshot();
        if(new[]{"repair","backpack"}.Any(part=>maintenanceState[part]?["status"]?.GetValue<string>() is "blocked" or "unknown" or "manual_required"))
            visibleIssues.TryAdd("maintenance",maintenanceState["reason"]?.GetValue<string>()??"维护等待核实");
        Status=new(running,message,dispatcher.ActiveId,visibleIssues,planRevision)
        {PauseReason=running?null:pauseReason,RecoveryAttempt=recoveryAttempt,ActiveConfirmed=confirmed,ActivePhase=phase,
            Jobs=snapshots.Where(p=>runs.ContainsKey(p.Item1)).ToDictionary(p=>p.Item1,p=>new AutomationJobStatus(runs[p.Item1].Batch["action_id"]!.GetValue<string>(),p.Item2["status"]?.GetValue<string>(),p.Item2["phase"]?.GetValue<string>(),p.Item2["reason"]?.GetValue<string>()??p.Item2["pause_reason"]?.GetValue<string>(),p.Item2["pending"] is not null){Confirmed=ConfirmedProgress(runs[p.Item1],p.Item2)}),
            ReconciliationCount=runs.Values.Count(r=>r.Runner is not IdleFishingRunner&&r.Runner.Snapshot()["status"]?.GetValue<string>()=="unknown")
                +(withdrawal?.Snapshot()["status"]?.GetValue<string>()=="unknown"?1:0)+(purchase?.State.Intent is not null&&!purchase.State.PaymentConfirmed&&purchase.State.Stage is not(PurchaseStage.Complete or PurchaseStage.Exit)?1:0)
                +(maintenanceState["pending"] is not null?1:0)+(maintenanceState["surplus"]?["pending"] is not null?1:0)};
    }
    private void Restore()
    {
        foreach(var node in queue.Read().ToJson()["jobs"]!.AsArray())
        {
            if(node!["pending"] is null)continue;var id=node["job_id"]!.GetValue<string>();
            try
            {
                var saved=queue.Restore(node.AsObject(),ports.Scope);
                if(saved is null)
                {
                    if(queue.ReleaseRetiredUnsubmitted(node.AsObject()))
                    {var action=node["pending"]!["action_id"]!.GetValue<string>();inventory.ReleaseUnsubmitted(action);ports.GatheringConsumables?.ReleaseUnsubmitted(action);SaveReservations();}
                    else issues[id]="旧批次需要核账，未创建新消费动作";
                    continue;
                }
                LoadRun(saved.Value.Batch,saved.Value.Folder);
            }
            catch(Exception e){issues[id]="恢复失败："+e.Message;Record("error","restore","task","manual_review",issues[id],e,id);}
        }
    }
    private void LoadRun(JsonObject batch,string folder)
    {
        var id=batch["job_id"]!.GetValue<string>();var action=batch["action_id"]!.GetValue<string>();var kind=batch["kind"]!.GetValue<string>();PersistentWorkflow runner;
        if(kind=="production")
        {
            var key=batch["recipe"]!["key"]!.GetValue<string>();var recipe=catalog.Production[key];
            var store=new SubmissionStore(new JsonWorkflowStore(Path.Combine(folder,"production.json")),inventory,action,catalog.Recipes[key],ports.Inventory);
            var production=new ProductionRunner(ports.Production(key,action,store),store,recipe,batch["rounds"]!.GetValue<int>(),clock,
                availableRounds:(_,limit)=>(int)Math.Min(limit,readyRounds.GetValueOrDefault(id)),
                deviceReadyRounds:device=>(int)Math.Min(int.MaxValue,readyRounds.Where(p=>tasks.GetValueOrDefault(p.Key)?.Device==device).Sum(p=>p.Value)),
                deviceQueueTail:DeviceTail,allowPartialRefill:BackpackRelief,collectionOwners:()=>DeviceOwners(recipe.Device));
            dispatcher.Register(id,production,recipe.Device);runner=production;
        }
        else if(kind=="gathering")
        {
            var key=batch["route_key"]!.GetValue<string>();var store=new ReservationStore(new JsonWorkflowStore(Path.Combine(folder,"gathering.json")),SaveReservations);
            if(IsBottleRoute(catalog.Gathering[key])&&ports.GatheringConsumables is null)throw new InvalidOperationException("水或牛奶采集缺少空瓶供料适配，不能启动");
            var existing=store.Read();
            if((existing?["pending"]?["consumables"]??existing?["consumables"]) is JsonObject held)ports.GatheringConsumables?.RestoreReservation(held);
            // Gathering output is reconciled from packet inventory after the native batch.
            // The screen workflow only confirms the fixed task lifecycle.
            var gathering=new GatheringRunner(ports.Gathering(key,action,store),store,catalog.Gathering[key],action,clock,consumables:ports.GatheringConsumables,inventoryBasis:()=>ports.Inventory().Query(catalog.Gathering[key].TargetItemId));
            dispatcher.Register(id,gathering);runner=gathering;
        }
        else if(kind=="idle_fishing")
        {
            var key=batch["point_key"]!.GetValue<string>();var store=new JsonWorkflowStore(Path.Combine(folder,"idle-fishing.json"));
            var fishing=new IdleFishingRunner(new(key,batch["duration_s"]!.GetFiniteDouble()),ports.Fishing(key,action,store),store,time:clock);
            dispatcher.Register(id,fishing);runner=fishing;
        }
        else throw new InvalidDataException("不支持的批次类型");
        runs.Add(id,new(batch,runner,folder));
        var saved=runner.Snapshot();
        if(runner is ProductionRunner&&saved["pending"] is not null)dispatcher.RestoreOwnership(id);
        if(runner is GatheringRunner g&&!g.SafeToSwitch&&(saved["submitted"]?.GetValue<int>()>0||saved["pending"] is not null))dispatcher.RestoreOwnership(id);
        if(runner is IdleFishingRunner f&&!f.SafeToSwitch&&(saved["round_seen"]?.GetValue<bool>()==true||saved["pending"] is not null||saved["navigation_seen"]?.GetValue<bool>()==true))dispatcher.RestoreOwnership(id);
    }
    private bool IsBottleRoute(GatheringRoute route)=>GatheringConsumables.RequiresBottles(route);
    private double DeviceTail(string device)=>runs.Values.Where(r=>r.Batch["recipe"]?["device"]?.GetValue<string>()==device).Select(r=>r.Runner.Snapshot()["production_due_at"]?.GetFiniteDouble()??0).DefaultIfEmpty(0).Max();
    private IReadOnlyDictionary<string,int[]> DeviceOwners(string device)=>runs.Values.Where(r=>r.Runner is ProductionRunner&&r.Runner.Snapshot()["pending"] is null
        &&r.Runner.Snapshot()["status"]?.GetValue<string>()!="unknown"&&r.Batch["recipe"]?["device"]?.GetValue<string>()==device)
        .ToDictionary(r=>r.Batch["action_id"]!.GetValue<string>(),r=>r.Runner.Snapshot()["owned_slots"]!.AsArray().Select(s=>s!.GetValue<int>()).ToArray());
    private bool BackpackRelief()=>maintenance.StatusSnapshot()["backpack"]?["status"]?.GetValue<string>()=="production_relief";
    private static bool InPlaceFishing(JobView job)=>job.Kind=="idle_fishing"&&job.Recipe==FishingPlan.CurrentLocation;
    private bool InPlaceFishingTurn(WorkCandidate[] candidates)
    {
        var selected=new Scheduler().Select(candidates,new(dispatcher.ActiveId,!dispatcher.SafeToSwitch,ports.CurrentDevice,BackpackRelief()));
        return selected is not null&&queue.Read().Jobs.Any(j=>j.Id==selected.Id&&InPlaceFishing(j));
    }
    private static string Hash(JsonNode value)=>Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.ToJsonString())));
    private string Fingerprint(QueueDocument q,InventorySnapshot stock)
    {
        var data=new JsonObject{["queue"]=q.ToJson(),["stock"]=JsonSerializer.SerializeToNode(stock.Observations.OrderBy(p=>p.Key).Select(p=>new{p.Key,Value=p.Value with{VerifiedThrough=null}})),["storage"]=JsonSerializer.SerializeToNode(stock.Storage),
            ["runs"]=new JsonArray(runs.Values.Select(r=>(JsonNode?)new JsonObject{["action"]=r.Batch["action_id"]!.DeepClone(),["submitted"]=r.Runner.Snapshot()["submitted"]?.DeepClone(),["completed"]=r.Runner.Snapshot()["completed"]?.DeepClone()}).ToArray())};
        return Hash(data);
    }
    private void Replan()
    {
        var q=queue.Read();var next=inventory.Apply(ports.Inventory());if(next.Scope!=ports.Scope)throw new InvalidOperationException("角色范围已变化，请重新开始");
        var fingerprint=Fingerprint(q,next);current=next;
        UpdateConsumableDemand(q);
        // A late complete balance can satisfy planning without another refresh trip.
        if(inventorySyncFailed&&current.Observations.Count>0&&current.Observations.Values.All(o=>o.Confirmed&&o.Quantity is not null)
            &&ports.GatheringConsumables?.AwaitingInventoryItemIds.Count is not >0)issues.Remove("inventory_sync");
        foreach(var (id,run) in runs.Where(p=>p.Value.Runner is GatheringRunner))
        {
            var job=q.Jobs.FirstOrDefault(j=>j.Id==id);if(job is null)continue;
            var key=SupplyKey(job);
            if(gatheringSupplyVersions.TryGetValue(id,out var prior)&&prior!=key)dispatcher.SupplyChanged(id);
            gatheringSupplyVersions[id]=key;
        }
        foreach(var id in gatheringSupplyVersions.Keys.Where(id=>!runs.ContainsKey(id)).ToArray())gatheringSupplyVersions.Remove(id);
        if(fingerprint==planFingerprint)
        {
            if(plan is not null&&q.Jobs.Any(j=>j.Id.StartsWith("auto-material:")&&j.Kind=="idle_fishing"&&!j.Pending&&j.Completed>=j.Target))SyncFishingSources(plan);
            return;
        }
        var goals=q.Jobs.Where(j=>!j.Id.StartsWith("auto-material:")&&!j.Cancelled&&!j.Paused&&j.Target>j.Completed&&j.Kind=="production"&&(j.Pending||q.Status(j.Id)=="queued"))
            .Select(j=>new MaterialGoal(j.Id,j.ItemId,j.Target-j.Completed,j.Kind,j.Kind=="production"?j.Recipe:null,j.Kind=="gathering"?j.Recipe:null));
        var future=runs.Values.Where(r=>r.Runner is ProductionRunner).Select(r=>
        {var state=r.Runner.Snapshot();var qty=(state["submitted"]!.GetValue<int>()-state["completed"]!.GetValue<int>())*r.Batch["output_per_round"]!.GetValue<long>();
            return new FutureSupply(r.Batch["action_id"]!.GetValue<string>(),r.Batch["target_item_id"]!.GetValue<long>(),qty,current.Scope,r.Batch["job_id"]!.GetValue<string>());}).Where(f=>f.Quantity>0);
        plan=catalog.Planner().Plan(goals,current,future);SyncAutomatic(q,plan);SyncFishingSources(plan);q=queue.Read();UpdateConsumableDemand(q);tasks.Clear();readyRounds.Clear();
        foreach(var task in plan.Tasks)
        {
            var matching=q.Jobs.Where(j=>!j.Cancelled&&!j.Paused&&(j.Pending||q.Status(j.Id)=="queued")&&j.ItemId==task.ItemId&&j.Kind==task.Kind&&j.Recipe==(task.RecipeKey??task.RouteKey)).ToArray();
            var available=task.ReadyRounds;
            foreach(var job in matching)
            {
                tasks[job.Id]=task;var remaining=Math.Max(0,job.Target-job.Completed);var each=task.OutputPerRound??1;var wanted=(remaining+each-1)/each;
                readyRounds[job.Id]=Math.Min(available,wanted);available-=readyRounds[job.Id];
                if(!deferred.ContainsKey(job.Id)||deferred[job.Id]!=SupplyKey(job))dispatcher.SupplyChanged(job.Id);
            }
        }
        foreach(var (id,run) in runs)
            if(run.Runner is GatheringRunner gather&&id.StartsWith("auto-material:")&&!tasks.ContainsKey(id))gather.StopSubmitting();
        planFingerprint=Fingerprint(q,current);planRevision++;
    }
    private void SyncAutomatic(QueueDocument q,MaterialPlan plan)
    {
        var needed=new Dictionary<string,(PlannedTask Task,long Quantity)>();
        foreach(var t in plan.Tasks)
        {
            var roots=t.Demands.Where(d=>d.Kind=="root").Sum(d=>d.Quantity);var quantity=Math.Max(0,t.RequiredQuantity-roots);
            if(quantity>0)
            {
                // Automatic gathering targets are native-batch rounds, not a
                // pretend material count.  The expected yield is deliberately
                // coarse; only packet inventory can confirm what a batch made.
                var rounds=quantity;
                if(t.Kind=="gathering"&&t.RouteKey is { } routeKey&&catalog.Gathering.TryGetValue(routeKey,out var route))
                {
                    var unknown=plan.Issues.Any(i=>i.Kind=="inventory_unknown"&&i.ItemId==t.ItemId);
                    // Attempts are not item yield. Without a configured reference,
                    // start one round and refresh instead of inventing a yield.
                    rounds=route.ReferenceYieldPerBatch is{} reference?MaterialPlanner.EstimateGatheringRounds(t.ItemId,quantity,reference,unknown).Rounds:1;
                }
                needed[t.TaskId]=(t,Math.Max(1,rounds));
            }
        }
        queue.Edit(document=>
        {
            var used=new HashSet<string>();
            var withdrawn=document.ToJson()["jobs"]!.AsArray().Where(n=>n!["automatic_withdrawn"]?.GetValue<bool>()==true).Select(n=>n!["job_id"]!.GetValue<string>()).ToHashSet();
            foreach(var (key,value) in needed)
            {
                var t=value.Task;var existing=document.Jobs.FirstOrDefault(j=>j.Id.StartsWith("auto-material:")&&(!j.Cancelled||withdrawn.Contains(j.Id))&&j.Kind==t.Kind&&j.ItemId==t.ItemId&&j.Recipe==(t.RecipeKey??t.RouteKey));
                var id=existing?.Id??"auto-material:"+Guid.NewGuid().ToString("N");
                if(existing is null)document.Add(id,t.Kind,t.ItemId,value.Quantity,t.RecipeKey??t.RouteKey,[]);
                used.Add(id);
                if(t.Kind=="gathering")
                    document.ReplanGatheringGroup(id,value.Quantity,Hash(JsonSerializer.SerializeToNode(new{Stock=current.Query(t.ItemId) with{VerifiedThrough=null},Demand=t.RequiredQuantity,ReferenceRounds=value.Quantity})!),inventory.HasPendingRefresh(t.ItemId,current.Scope),current.Query(t.ItemId).VerifiedThrough);
                else if(existing is not null&&!existing.Pending)document.ReplanAutomatic(id,value.Quantity,true);
            }
            foreach(var job in document.Jobs.Where(j=>j.Id.StartsWith("auto-material:")&&j.Kind!="idle_fishing"&&!j.Pending&&!used.Contains(j.Id)&&!j.Cancelled))
                if(!plan.Issues.Any(i=>i.ItemId==job.ItemId&&i.Kind is "inventory_unknown" or "recipe_cycle" or "no_source"))document.ReplanAutomatic(job.Id,0);
            document.PruneCancelled();
        });
    }
    private void SyncFishingSources(MaterialPlan materialPlan)
    {
        var needed=materialPlan.Issues.Where(i=>i.Kind=="no_source").Select(i=>i.ItemId).Distinct()
            .Where(item=>catalog.Configuration["material_sources"]?[item.ToString()]?["kind"]?.GetValue<string>()=="fishing")
            .ToHashSet();
        queue.Edit(document=>
        {
            foreach(var item in needed)
            {
                var point=catalog.Configuration["material_sources"]![item.ToString()]!["point_key"]?.GetValue<string>();
                if(point is null||!catalog.Gathering.ContainsKey(point))continue;
                var basis=current.Query(item);if(!basis.Confirmed||inventory.HasPendingRefresh(item,current.Scope))continue;
                var existing=document.Jobs.FirstOrDefault(j=>j.Id.StartsWith("auto-material:")&&j.Kind=="idle_fishing"&&j.ItemId==item&&j.Recipe==point&&!j.Cancelled);
                if(existing is null)document.Add("auto-material:"+Guid.NewGuid().ToString("N"),"idle_fishing",item,60,point,[]);
                else if(!existing.Pending&&existing.Completed>=existing.Target)
                {
                    var row=document.ToJson()["jobs"]!.AsArray().First(j=>j!["job_id"]!.GetValue<string>()==existing.Id)!;
                    // Only capture coverage beyond the persisted batch-end point can
                    // extend timed fishing. Versions and quantities may be rebuilt or stale.
                    var previous=row["receipts"]?.AsArray().LastOrDefault()?["action_id"]?.GetValue<string>();
                    var endedThrough=previous is null?null:new JsonWorkflowStore(Path.Combine(queue.BatchDirectory(ports.Scope,existing.Id,previous),"batch.json")).Read()?["batch_end_through"]?.GetFiniteDouble();
                    if(endedThrough is{} ended&&basis.VerifiedThrough is{} through&&double.IsFinite(through)&&through>ended)document.ReplanAutomatic(existing.Id,60,true);
                }
            }
            foreach(var job in document.Jobs.Where(j=>j.Id.StartsWith("auto-material:")&&j.Kind=="idle_fishing"&&!j.Cancelled&&!j.Pending&&!needed.Contains(j.ItemId)&&!materialPlan.Issues.Any(i=>i.Kind=="inventory_unknown"&&i.ItemId==j.ItemId)).ToArray())document.ReplanAutomatic(job.Id,0);
        });
        foreach(var (id,run) in runs)
            if(id.StartsWith("auto-material:")&&run.Runner is IdleFishingRunner fish&&!needed.Contains(run.Batch["target_item_id"]!.GetValue<long>()))fish.StopSubmitting();
    }
    private WorkCandidate[] Candidates()
    {
        var document=queue.Read();var jobs=document.Jobs;var result=new List<WorkCandidate>();
        foreach(var (job,index) in jobs.Select((j,i)=>(j,i)))
        {
            if(job.Paused||dispatcher.Faults.ContainsKey(job.Id)||job.Target<=job.Completed||job.Cancelled&&!runs.ContainsKey(job.Id))continue;var automatic=job.Id.StartsWith("auto-material:");
            if(runs.TryGetValue(job.Id,out var run))
            {
                var s=run.Runner.Snapshot();var device=run.Batch["recipe"]?["device"]?.GetValue<string>();
                var waiting=s["phase"]?.GetValue<string>()=="waiting";var due=run.Runner is ProductionRunner production?production.NextCollectionAt(device==ports.OnsiteDevice):s["due_at"]?.GetFiniteDouble();
                var collectible=waiting&&due is not null&&due.Value<=Now;
                var filling=job.Kind=="production"&&!waiting&&s["submitted"]!.GetValue<int>()<s["count"]!.GetValue<int>()&&readyRounds.GetValueOrDefault(job.Id)>0&&!job.Cancelled;
                var finishing=job.Kind=="production"&&!waiting&&(s["submitted"]!.GetValue<int>()>0||s["phase"]?.GetValue<string>()=="leave");
                // Reconciliation stays observable but must not win selection ahead of unclaimed independent work.
                // An unsafe active transaction keeps ownership in the dispatcher regardless of readiness.
                var readyForDispatch=dispatcher.CanSchedule(job.Id)&&(!waiting||collectible);
                result.Add(new(job.Id,job.Kind,device,readyForDispatch,filling?FreeSlots(device):finishing?1:0,index,Collectible:collectible,Automatic:automatic,Blocked:maintenance.Blocked(job.Kind,!InPlaceFishing(job)&&(job.Kind is "gathering" or "idle_fishing"))));continue;
            }
            if(job.Pending||issues.ContainsKey(job.Id))continue;
            if(document.Status(job.Id)!="queued")continue;
            if(deferred.TryGetValue(job.Id,out var blockedAt)&&blockedAt==SupplyKey(job))continue;
            var task=tasks.GetValueOrDefault(job.Id);
            if(job.Kind=="idle_fishing"){result.Add(new(job.Id,job.Kind,null,true,0,index,Blocked:maintenance.Blocked(job.Kind,!InPlaceFishing(job))));continue;}
            if(job.Kind=="gathering"&&!automatic){result.Add(new(job.Id,job.Kind,null,true,0,index,Blocked:maintenance.Blocked(job.Kind,true)));continue;}
            if(task is null)continue;
            var ready=job.Kind=="production"?readyRounds.GetValueOrDefault(job.Id)>0&&deviceRetry.GetValueOrDefault(task.Device!)<=Now:true;
            double coverage=0;var satisfied=false;
            if(automatic&&job.Kind=="gathering")
            {
                var demand=plan!.Tasks.Where(t=>t.Kind=="production"&&t.Inputs.ContainsKey(job.ItemId)).Sum(t=>checked(t.Inputs[job.ItemId]*Math.Min(5,t.Rounds)));
                var observation=current.Query(job.ItemId);var quantity=observation.Quantity;
                var awaitingFresh=string.Equals(observation.UnknownReason,"采集批结束，等待新库存",StringComparison.Ordinal);
                coverage=quantity is null?0:demand>0?(double)quantity.Value/demand:1;
                satisfied=demand==0||quantity>=demand;
                // An unknown initial baseline still gets one coarse batch. A
                // batch that already ended must wait for its next packet
                // snapshot, otherwise the scheduler would replay forever.
                ready=!awaitingFresh&&!satisfied;
            }
            result.Add(new(job.Id,job.Kind,task.Device,ready,FreeSlots(task.Device),index,coverage,Automatic:automatic,BufferSatisfied:satisfied,Blocked:maintenance.Blocked(job.Kind,job.Kind=="gathering")));
        }
        return result.ToArray();
    }
    private int FreeSlots(string? device)
    {
        if(device is null)return 0;var occupied=runs.Values.Where(r=>r.Batch["recipe"]?["device"]?.GetValue<string>()==device).Sum(r=>r.Runner.Snapshot()["owned_slots"]?.AsArray().Count??0);
        return Math.Max(0,5-occupied);
    }
    private string SupplyKey(JobView job)
    {
        var ids=job.Kind=="production"&&job.Recipe is not null&&catalog.Recipes.TryGetValue(job.Recipe,out var recipe)?recipe.Inputs.Keys.ToArray():[job.ItemId];
        if(job.Kind=="gathering")
            ids=ids.Concat(catalog.Names.Where(p=>p.Value=="空瓶").Select(p=>p.Key)).Distinct().ToArray();
        return Hash(JsonSerializer.SerializeToNode(ids.Order().Select(i=>current.Query(i) with{VerifiedThrough=null}))!);
    }
    private void CreateRun(WorkCandidate candidate)
    {
        var job=queue.Read().Jobs.Single(j=>j.Id==candidate.Id);var details=new JsonObject();string resource;
        if(job.Kind=="production")
        {
            var recipe=catalog.Production[job.Recipe!];var definition=catalog.Recipes[job.Recipe!];
            var rounds=(int)Math.Min(5,Math.Min(readyRounds.GetValueOrDefault(job.Id),(job.Target-job.Completed+definition.OutputPerRound-1)/definition.OutputPerRound));
            if(rounds<=0)return;details["rounds"]=rounds;details["output_per_round"]=definition.OutputPerRound;
            details["recipe"]=new JsonObject{["key"]=recipe.Key,["device"]=recipe.Device,["duration_s"]=recipe.DurationSeconds};resource="device:"+recipe.Device;
        }
        else if(job.Kind=="gathering"){details["route_key"]=job.Recipe;resource="character:gathering";}
        else{details["point_key"]=job.Recipe;details["duration_s"]=job.Target-job.Completed;if(job.Id.StartsWith("auto-material:"))details["source_basis"]=JsonSerializer.SerializeToNode(current.Query(job.ItemId));resource="character:idle_fishing";}
        var batch=queue.Claim(job,ports.Scope,job.Kind,resource,details);LoadRun(batch,queue.BatchDirectory(ports.Scope,job.Id,batch["action_id"]!.GetValue<string>()));
    }
    public AutomationStatus Tick()
    {
        if(!running)return Status;
        if(clock.GetUtcNow()<retryAfter)return Status;
        traceId=Guid.NewGuid().ToString("N");
        var inputPhase=false;
        try
        {
            stage="plan";ApplyQueueControls();Reconcile();Replan();var candidates=Candidates();readyAtLastPlan=candidates.Any(c=>c.Ready&&!c.Blocked);
            var latestPolicy=preferences();if(latestPolicy!=appliedPolicy){maintenance.Configure(latestPolicy);appliedPolicy=latestPolicy;}
            var inPlace=InPlaceFishingTurn(candidates);
            var fishingYieldRequested=maintenance.RequiresTurnFor(!inPlace)||candidates.Any(c=>c.Kind!="idle_fishing"&&c.Ready&&!c.Blocked);
            foreach(var run in runs.Values)if(run.Runner is IdleFishingRunner fish)fish.RequestYield(fishingYieldRequested);
            var safeAuxiliary=SafeToSwitch;
            inputPhase=true;recoveryAttempt=0;
            stage="auxiliary";if(safeAuxiliary&&ports.BeforeTick(true)){Publish("正在处理当前界面");return Status;}
            if(decomposition?.Snapshot()["status"]?.GetValue<string>()=="running")
            {decomposition.Tick();Publish("正在分解樹枝补料");return Status;}
            if(decomposition is not null&&decomposition.Snapshot()["phase"]?.GetValue<string>()=="leave"&&safeAuxiliary)
            {if(!decomposition.ReleaseNavigation()){Publish("分解已核账，正在退出背包");return Status;}}
            if(!safeAuxiliary&&maintenance.StatusSnapshot()["pending"] is null&&maintenance.StatusSnapshot()["surplus"]?["pending"] is null&&ports.BeforeNavigationTick())
            {Publish("正在使用导航精灵之翼");return Status;}
            if(purchase is not null&&TickPurchase(false))return Status;
            // Continue can revive a previously blocked, unsent storage search while
            // production still owns the character. Finish that owner before storage
            // navigation; an outstanding transfer keeps its accounting boundary.
            if(withdrawal is not null&&(dispatcher.SafeToSwitch||withdrawal.Snapshot()["pending"] is not null)
                &&(!withdrawal.SafeToSwitch||withdrawal.Snapshot()["pending"] is not null||withdrawal.Snapshot()["status"]?.GetValue<string>()=="complete"))
            {if(SupplyFromStorage())return Status;if(withdrawal is not null&&!withdrawal.SafeToSwitch){Publish("仓库取料结果待核实，仅观察，不派发新动作");return Status;}}
            stage="maintenance";JsonObject maintenanceState;
            try{maintenanceState=maintenanceFaulted?maintenance.StatusSnapshot():maintenance.Tick(dispatcher.SafeToSwitch, true, deferIdleObservation:true,allowRepair:!inPlace);}
            catch(Exception error)when(error is not (IOException or UnauthorizedAccessException or OutOfMemoryException)&&maintenance.CanIsolateFault)
            {maintenanceFaulted=true;maintenance.Quarantine();issues["maintenance_fault"]="维护异常，独立任务继续："+error.Message;Record("error","maintenance","module","explicit_continue",error.Message,error);maintenanceState=maintenance.StatusSnapshot();}
            if(maintenanceState["paused"]?.GetValue<string>()=="resume_observation_timeout")issues["maintenance"]="维护等待画面超时，请核实页面后继续";
            else if(issues.GetValueOrDefault("maintenance")=="维护等待画面超时，请核实页面后继续")issues.Remove("maintenance");
            if(maintenanceState["global_pause_required"]?.GetValue<bool>()==true){Pause("maintenance_required");Publish("背包需要手动处理，任务已暂停");return Status;}
            if(maintenanceState["backpack"]?["status"]?.GetValue<string>()=="blocked"&&maintenanceState["reason"]?.GetValue<string>()=="backpack_relief_exhausted")
            {Pause("backpack_player_action_required");Publish("背包空间不足，无法继续贩售或生产；请玩家处理后显式继续");return Status;}
            if(maintenanceState["navigation_reserved"]?.GetValue<bool>()==true
                &&!candidates.Any(c=>c.Kind=="production"&&c.Ready&&!c.Blocked)){Publish("正在维护");return Status;}
            if(maintenanceState["surplus"]?["pending"] is not null&&maintenanceState["active"]?.GetValue<string>()!="backpack"){Publish("贩售结果待核实，仅观察，不派发新动作");return Status;}
            if(maintenanceState["pending"] is not null){Publish("维护动作结果待核实，仅观察，不派发新动作");return Status;}
            stage="purchase";if(TickPurchase(true))return Status;
            if(dispatcher.ActiveId is {} activeId&&runs.TryGetValue(activeId,out var activeRun)&&activeRun.Runner is ProductionRunner)
            {
                var state=activeRun.Runner.Snapshot();var device=activeRun.Batch["recipe"]?["device"]?.GetValue<string>();
                if(state["phase"]?.GetValue<string>()=="leave"&&state["pending"] is null&&device==ports.CurrentDevice)
                {
                    var peer=candidates.FirstOrDefault(c=>c.Id!=activeId&&c.Kind=="production"&&c.Device==device&&c.Ready&&!c.Blocked&&c.FreeSlots>0&&!runs.ContainsKey(c.Id));
                    if(peer is not null)CreateRun(peer);
                }
            }
            stage="dispatch";var selected=new Scheduler().Select(candidates,new(dispatcher.ActiveId,!dispatcher.SafeToSwitch,ports.CurrentDevice,BackpackRelief()));
            if(selected?.Kind is not "production"&&SupplyFromStorage())return Status;
            if(selected is not null&&!runs.ContainsKey(selected.Id))CreateRun(selected);
            var before=runs.ToDictionary(p=>p.Key,p=>p.Value.Runner.Snapshot());var baseline=ports.Inventory();
            dispatcher.Tick(candidates,ports.CurrentDevice,BackpackRelief(),ports.OnsiteDevice,true);
            foreach(var (id,run) in runs)
            {
                if(run.Runner is not ProductionRunner production)continue;var state=production.Snapshot();var old=before.GetValueOrDefault(id)?["submitted"]?.GetValue<int>()??0;var submitted=state["submitted"]!.GetValue<int>();
                if(state["collection_not_before"] is{} trip&&!JsonNode.DeepEquals(before.GetValueOrDefault(id)?["collection_not_before"],trip))
                    foreach(var peer in runs.Values.Where(r=>r.Runner is ProductionRunner&&r.Batch["recipe"]?["device"]?.GetValue<string>()==run.Batch["recipe"]?["device"]?.GetValue<string>()))
                        ((ProductionRunner)peer.Runner).DeferOffsiteCollection(trip.GetFiniteDouble());
                for(var n=old+1;n<=submitted;n++)inventory.Submitted(run.Batch["action_id"]!.GetValue<string>(),n,catalog.Recipes[run.Batch["recipe"]!["key"]!.GetValue<string>()],baseline);
                if(submitted>old)maintenance.CheckProductionWeight(run.Batch["action_id"]!.GetValue<string>(),submitted);
            }
            stage="reconcile";Reconcile();
            inventory.Compact(runs.Values.Select(r=>r.Batch["action_id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal));
            ReconcileReservations();
            if(dispatcher.ActiveId is null&&dispatcher.SafeToSwitch)
            {
                if(BackpackRelief()&&!candidates.Any(c=>c.Ready&&c.Kind=="production"))
                {
                    maintenance.ProductionReliefExhausted();
                    var relief=maintenance.StatusSnapshot();
                    if(relief["backpack"]?["status"]?.GetValue<string>()=="blocked"&&relief["reason"]?.GetValue<string>()=="backpack_relief_exhausted")
                    {Pause("backpack_player_action_required");Publish("背包空间不足，无法继续贩售或生产；请玩家处理后显式继续");return Status;}
                }
                else RequestInventoryRefresh();
            }
            if(dispatcher.IsPaused){running=false;pauseReason=ports.InputPauseReason??dispatcher.PauseReason??"focus_lost";ports.Pause(pauseReason);}
            Publish(dispatcher.ActiveId is null?dispatcher.Status:dispatcher.Status);
        }
        catch(IOException error) when(!inputPhase && ((error.HResult&0xffff) is 32 or 33) && recoveryAttempt<3)
        {
            recoveryAttempt++;retryAfter=clock.GetUtcNow().AddMilliseconds(250*Math.Pow(2,recoveryAttempt-1));Record("warning","queue","tick","bounded_retry_before_input",error.Message,error);
            Publish($"队列暂被占用，等待重试 {recoveryAttempt}/3");Status=Status with{Fault=error.ToString()};
        }
        catch(Exception error)
        {
            var inputPause=ports.InputPauseReason;
            Record("error",stage,inputPause is null?"module":"input_guard",inputPause is null?"continue_independent_work":"explicit_continue",error.Message,error,dispatcher.ActiveId);
            Exception fault=error;
            if(inputPause is not null)
            {
                try{Pause(inputPause);}
                catch(Exception pauseError){fault=new AggregateException("输入暂停且暂停记录保存失败",error,pauseError);Record("error","pause","global","manual_review",pauseError.Message,fault);}
                Publish("输入守卫已暂停，核实后继续："+error.Message);Status=Status with{Running=false,Fault=fault.ToString()};
            }
            else
            {
                Publish("当前步骤异常，已保留现场并继续调度："+error.Message);Status=Status with{Fault=fault.ToString()};
            }
        }
        return Status;
    }
    private void RequestInventoryRefresh()
    {
        if(plan is null||inventorySyncFailed||BackpackRelief()||!ports.PreferBulkInventorySync)return;
        var unknown=plan.Issues.Where(i=>i.Kind=="inventory_unknown"||i.Kind=="no_source"&&StorageUnknown(i.ItemId)).Select(i=>i.ItemId).Concat(ports.GatheringConsumables?.AwaitingInventoryItemIds??[]).Distinct().ToArray();
        foreach(var key in issues.Keys.Where(k=>k.StartsWith("inventory:",StringComparison.Ordinal)&&long.TryParse(k[10..],out var id)&&!unknown.Contains(id)).ToArray())issues.Remove(key);
        if(unknown.Length==0){ResetInventorySync();inventorySyncKey=null;return;}
        var syncKey=string.Join("|",unknown.Order().Select(item=>item+":"+current.Query(item).Source+":"+current.Query(item).Version));
        if(!SafeToSwitch||Candidates().Any(c=>c.Ready&&!c.Blocked&&c.Kind!="idle_fishing")||inventorySyncKey==syncKey)return;
        inventorySyncKey=syncKey;inventorySyncRequested=true;ports.RequestInventorySync();
    }
    private void ResetInventorySync(bool explicitRetry=false)
    {inventorySyncRequested=false;ports.CancelInventorySync();if(explicitRetry){inventorySyncKey=null;inventorySyncFailed=false;}issues.Remove("inventory_sync");}
    private bool StorageUnknown(long item)=>current.Storage.Count<2||current.Storage.Values.Any(rows=>!rows.TryGetValue(item,out var quantity)||quantity is null);
    private bool SupplyFromDecomposition()
    {
        if(decomposition is null||!dispatcher.SafeToSwitch||withdrawal is not null||purchase is not null)return false;
        decomposition.ObserveOnly();var saved=decomposition.Snapshot();
        if(saved["pending"] is null&&saved["status"]?.GetValue<string>()=="complete")issues.Remove("decomposition");
        if(saved["pending"] is not null)
        {
            issues["decomposition"]="分解结果待核账，不重放；其他队列继续";
            if(!decomposition.ReleaseNavigation()){Publish("保留分解意图，正在退出结果页");return true;}
            if(!inventorySyncFailed&&ports.PreferBulkInventorySync&&decompositionVersion!="pending-refresh")
            {decompositionVersion="pending-refresh";inventorySyncRequested=true;ports.RequestInventorySync();Publish("保留分解意图，刷新抓包核账");return true;}
            return false;
        }
        if(saved["status"]?.GetValue<string>()=="blocked"){issues["decomposition"]=saved["reason"]?.GetValue<string>()??"分解局部受阻";return false;}
        var missing=plan?.Issues.FirstOrDefault(i=>i.Kind=="no_source"&&catalog.Configuration["material_sources"]?[i.ItemId.ToString()]?["kind"]?.GetValue<string>()=="decomposition");
        if(missing is null)return false;
        if(StorageUnknown(missing.ItemId)){issues["decomposition"]="分解前仓库库存未知，等待完整抓包";return false;}
        var version=current.Query(missing.ItemId).Version;if(version==decompositionVersion)return false;
        // No conversion ratio is assumed: one input, then use the actual packet output.
        if(!decomposition.Request(1))return false;decompositionVersion=version;issues.Remove("decomposition");Publish("从樹枝分解补充單寧粉末");return true;
    }
    private bool SupplyFromStorage()
    {
        if(!dispatcher.SafeToSwitch||BackpackRelief())return false;
        if(withdrawal is not null)
        {
            if(withdrawalFaulted)return false;
            if(plan is not null)withdrawal.RetireSatisfiedPlans(item=>current.Query(item).Confirmed&&plan.Allocations.Any(a=>a.SourceKind=="inventory"&&a.ItemId==item&&a.Quantity>0)&&!plan.Allocations.Any(a=>a.SourceKind=="storage"&&a.ItemId==item&&a.Quantity>0));
            withdrawal.RetireUnneededPlans(WithdrawalStillNeeded);
            var state=withdrawal.Snapshot();
            if(state["status"]?.GetValue<string>()=="complete")
            {
                if(withdrawal.NavigationAbandoned)
                {
                    // The exit was already given up after a bounded number of tries.
                    // The transfer stays accounted for, but the chain has to let go so
                    // the gathering that waits on this withdrawal can run again.
                    issues[withdrawalKey!]="取料已核账，退出仓库未确认（画面可能仍在仓库），已放弃退出并继续其他任务";
                    withdrawal=null;planFingerprint=null;return false;
                }
                if(!withdrawal.ReleaseNavigation()){Publish("取料已核账，正在退出仓库");return true;}
                var stopped=state["reason"]?.GetValue<string>()=="material_demand_stopped";
                issues.Remove(withdrawalKey!);withdrawal=null;planFingerprint=null;
                if(stopped)
                {
                    File.Delete(Path.Combine(queue.DirectoryPath,"storage-supply.json"));
                    withdrawalKey=null;withdrawalVersion=null;
                }
                return false;
            }
            if(state["status"]?.GetValue<string>() is "unknown" or "blocked" or "paused")
            {
                if(withdrawal.NavigationAbandoned)
                {
                    // Keep the persisted intent for accounting, but stop driving a page
                    // we cannot leave: an unprovable exit must not park every other task.
                    issues[withdrawalKey!]="仓库取料待核对，退出仓库未确认（画面可能仍在仓库），已放弃退出并继续其他任务";
                    withdrawal=null;planFingerprint=null;return false;
                }
                withdrawal.ObserveOnly();if(withdrawal.Snapshot()["status"]?.GetValue<string>()=="complete")return true;
                issues[withdrawalKey!]=(state["pending"] is null?"仓库取料局部受阻：":"仓库取料待核对：")+state["reason"]?.GetValue<string>();
                if(state["pending"]?["kind"]?.GetValue<string>() is "confirm_transfer" or "prepare_transfer")
                {
                    if(!withdrawal.ReleaseNavigation()){Publish("保留取料意图，正在退出仓库准备核账");return true;}
                    if(!inventorySyncFailed&&ports.PreferBulkInventorySync&&withdrawal.RequestRefreshOnce())
                    {inventorySyncRequested=true;ports.RequestInventorySync();Publish("保留取料意图，刷新抓包核账");return true;}
                }
                return false;
            }
            try{withdrawal.Tick();}
            catch(Exception error)when(error is not (IOException or UnauthorizedAccessException or OutOfMemoryException)&&withdrawal.CanIsolateFault)
            {withdrawalFaulted=true;issues[withdrawalKey!]="仓库取料异常，独立任务继续："+error.Message;Record("error","storage","module","explicit_continue",error.Message,error);return false;}
            Publish("正在从仓库取料");return true;
        }
        var allocation=plan?.Allocations.FirstOrDefault(a=>a.SourceKind=="storage"&&a.Quantity>0);
        var supply=NextPurchaseRequest();
        if(allocation is null&&supply is not null)
        {
            var stock=current.Storage.FirstOrDefault(s=>s.Value.GetValueOrDefault(supply.ItemId)>0);
            if(stock.Key is not null)allocation=new(supply.ItemId,Math.Min(supply.Quantity,stock.Value[supply.ItemId]!.Value),"storage",stock.Key,supply.ActionId,supply.ActionId);
        }
        if(allocation is null)return SupplyFromDecomposition();
        var key=allocation.SourceId+":"+allocation.ItemId;var version=Hash(JsonSerializer.SerializeToNode(current.Storage)!);
        if(key==withdrawalKey&&version==withdrawalVersion)return false;
        if(!catalog.Names.TryGetValue(allocation.ItemId,out var name)){issues[key]="仓库道具缺少已关联名称";return false;}
        var matching=plan?.Allocations.Where(a=>a.SourceKind=="storage"&&a.SourceId==allocation.SourceId&&a.ItemId==allocation.ItemId).ToArray()??[];
        var qty=matching.Sum(a=>a.Quantity);var goalIds=matching.Select(a=>a.GoalId).Distinct().ToList();
        if(supply is not null&&supply.ItemId==allocation.ItemId&&current.Storage.TryGetValue(allocation.SourceId!,out var source))
        {
            var spare=Math.Max(0,(source.GetValueOrDefault(allocation.ItemId)??0)-qty);
            var extra=Math.Min(supply.Quantity,spare);qty=checked(qty+extra);
            if(extra>0)goalIds.Add(supply.ActionId);
        }
        if(qty==0)qty=allocation.Quantity;
        var p=new WithdrawalPlan(allocation.ItemId,name,qty,allocation.SourceId!,goalIds.Distinct().ToArray());
        var path=Path.Combine(queue.DirectoryPath,"storage-supply.json");var store=new JsonWorkflowStore(path);
        if(store.Read() is {} existing&&existing["status"]?.GetValue<string>()!="complete")
        {issues[key]="已有仓库取料记录待恢复，不重复取料";withdrawalKey=key;withdrawalVersion=version;return false;}
        if(File.Exists(path))File.Delete(path);
        var action="withdrawal:"+Guid.NewGuid().ToString("N");var bound=new WithdrawalStore(store,ports.Scope,action);
        withdrawal=new([p],ports.Storage(p,action,bound),bound,time:clock);
        withdrawalKey=key;withdrawalVersion=version;withdrawal.Resume(true);Publish("准备从仓库取料");return true;
    }
    private bool WithdrawalStillNeeded(WithdrawalPlan withdrawalPlan)
    {
        var goals=withdrawalPlan.GoalIds??[];
        if(goals.Length==0)return true;
        var activeStorage=plan?.Allocations.Where(a=>a.SourceKind=="storage"&&a.Quantity>0&&a.ItemId==withdrawalPlan.ItemId&&a.SourceId==withdrawalPlan.Scope)
            .Select(a=>a.GoalId).ToHashSet(StringComparer.Ordinal)??[];
        var activeSupply=ports.GatheringConsumables?.PendingRequests.Where(r=>r.ItemId==withdrawalPlan.ItemId&&r.Scope==ports.Scope)
            .Select(r=>r.ActionId).ToHashSet(StringComparer.Ordinal)??[];
        return goals.Any(goal=>activeStorage.Contains(goal)||activeSupply.Contains(goal));
    }
    private void Reconcile()
    {
        // Durable cross-recipe receipts close the crash gap between one collect-all input and peer journals.
        foreach(var run in runs.Values.Where(r=>r.Runner is ProductionRunner))
        {
            var production=(ProductionRunner)run.Runner;var receipts=production.Snapshot()["collection_receipts"]?.AsArray();if(receipts is null)continue;
            foreach(var receipt in receipts)
            {
                var unresolved=false;foreach(var (action,slots) in receipt!["owners"]!.AsObject())
                {
                    var peer=runs.Values.FirstOrDefault(r=>r.Batch["action_id"]?.GetValue<string>()==action);
                    if(peer?.Runner is ProductionRunner target)
                    {
                        var affected=slots!.AsArray().Select(s=>s!.GetValue<int>()).ToArray();
                        var compact=receipt["compact"]?.GetValue<bool>()==true;
                        var cleared=compact?receipt["cleared"]!.AsArray().Select(s=>s!.GetValue<int>()).ToArray():affected;
                        target.ReconcilePeerCollection(cleared,receipt["id"]!.GetValue<string>(),compact);
                        if(compact?!target.HasAppliedCollection(receipt["id"]!.GetValue<string>()):target.Snapshot()["owned_slots"]!.AsArray().Any(s=>affected.Contains(s!.GetValue<int>())))unresolved=true;
                    }
                    else if(!queue.Read().IsActionDeleted(action)&&!queue.Read().ToJson()["jobs"]!.AsArray().Any(j=>j?["receipts"]?.AsArray().Any(r=>r?["action_id"]?.GetValue<string>()==action)==true))unresolved=true;
                }
                var jobId=run.Batch["job_id"]!.GetValue<string>();
                if(!unresolved){production.AcknowledgeCollection(receipt["id"]!.GetValue<string>());if(issues.GetValueOrDefault(jobId)=="跨配方领取记录仍待核对")issues.Remove(jobId);}
                else issues[jobId]="跨配方领取记录仍待核对";
            }
        }
        foreach(var (id,run) in runs.ToArray())
        {
            var state=run.Runner.Snapshot();var status=state["status"]?.GetValue<string>();
            if(run.Runner is GatheringRunner legacy&&queue.Read().IsLegacyGathering(id))
            {
                if(state["pending"] is null&&(state["submitted"]?.GetValue<int>()==0||status=="complete"||legacy.NavigationReleased))
                {
                    if(state["submitted"]?.GetValue<int>()>0)
                    {
                        var basis=state["inventory_basis"]?.Deserialize<StockObservation>()??ports.Inventory().Query(run.Batch["target_item_id"]!.GetValue<long>());
                        inventory.RequireFresh(run.Batch["action_id"]!.GetValue<string>(),basis,Now);
                    }
                    queue.Edit(q=>q.CloseLegacyGathering(id,run.Batch["action_id"]!.GetValue<string>(),state["submitted"]?.GetValue<int>()>0));
                    dispatcher.Remove(id);runs.Remove(id);planFingerprint=null;
                }
                continue; // Legacy material progress is never credited as native rounds.
            }
            if(run.Runner is GatheringRunner ended&&(ended.NavigationReleased||status=="complete")&&state["pending"] is null&&state["submitted"]?.GetValue<int>()>0)
            {
                var job=queue.Read().Jobs.FirstOrDefault(j=>j.Id==id);
                // Keep the rest of an estimated group runnable. Only the final
                // native round requests one merged packet refresh.
                if(job is null||job.Cancelled||job.Completed+1>=job.Target)
                {
                    var basis=state["inventory_basis"]?.Deserialize<StockObservation>()??ports.Inventory().Query(run.Batch["target_item_id"]!.GetValue<long>());
                    inventory.RequireFresh(run.Batch["action_id"]!.GetValue<string>(),basis,Now);
                }
            }
            if(run.Runner is GatheringRunner gathering&&gathering.NavigationReleased&&state["pending"] is null&&state["submitted"]?.GetValue<int>()>0&&status=="unknown")
            {
                queue.Edit(q=>q.ReleaseFinishedGathering(id,run.Batch["action_id"]!.GetValue<string>()));
                {
                    queue.Finish(run.Batch,1,"native_gathering_round:"+run.Batch["action_id"]!.GetValue<string>());dispatcher.Remove(id);runs.Remove(id);planFingerprint=null;
                    Record("warning","gathering","task","no_history_recount","自动补采原生批已结束；产量未知不计零，不为历史结果核量或重登");continue;
                }
            }
            if(status=="complete")
            {
                if(state["collection_receipts"] is JsonArray {Count:>0})continue;
                if(run.Runner is IdleFishingRunner&&run.Batch["source_basis"]?.Deserialize<StockObservation>() is{} fishingBasis&&state["submitted"]?.GetValue<int>()>0)
                {
                    var endedThrough=ports.Inventory().Query(fishingBasis.ItemId).VerifiedThrough;
                    inventory.RequireFresh(run.Batch["action_id"]!.GetValue<string>(),fishingBasis,Now);
                    run.Batch["batch_end_through"]=endedThrough;AtomicJson.Write(Path.Combine(run.Folder,"batch.json"),run.Batch);
                }
                // Gathering jobs count native batches as rounds.  The
                // produced material remains unknown until a newer packet
                // snapshot arrives, so one completed batch credits one round
                // and never fabricates an item quantity.
                var automaticGathering=run.Runner is GatheringRunner;
                var quantity=automaticGathering?state["completed"]!.GetValue<int>():run.Runner switch{ProductionRunner=>state["completed"]!.GetValue<int>()*run.Batch["output_per_round"]!.GetValue<long>(),IdleFishingRunner=>(long)Math.Floor(state["elapsed_s"]!.GetFiniteDouble()),_=>0};
                if(quantity==0&&(run.Runner is not GatheringRunner||state["submitted"]!.GetValue<int>()==0))queue.Release(run.Batch);else queue.Finish(run.Batch,quantity,automaticGathering?"native_gathering_round:"+run.Batch["action_id"]!.GetValue<string>():"screen_confirmed_native_batch:"+run.Batch["action_id"]!.GetValue<string>());
                dispatcher.Remove(id);runs.Remove(id);planFingerprint=null;continue;
            }
            if(run.Runner is ProductionRunner production&&state["pending"] is null&&status=="paused"&&state["pause_reason"]?.GetValue<string>() is "craft_unavailable" or "materials_unknown" or "device_full" or "device_batch_not_ready")
            {
                if(state["submitted"]!.GetValue<int>()>0)production.StopSubmitting();
                else
                {
                    var job=queue.Read().Jobs.Single(j=>j.Id==id);
                    queue.Release(run.Batch);dispatcher.Remove(id);runs.Remove(id);
                    if(state["pause_reason"]?.GetValue<string>() is "device_full" or "device_batch_not_ready")
                    {var device=run.Batch["recipe"]!["device"]!.GetValue<string>();deviceRetry[device]=Math.Max(Now+60,DeviceTail(device));}
                    else deferred[id]=SupplyKey(job);
                }
            }
        }
    }
    public void SynchronizeQueue()
    {ApplyQueueControls();planFingerprint=null;Publish(running?"队列已更新":"任务已更新");}
    private void ApplyQueueControls()
    {
        var jobs=queue.Read().Jobs.ToDictionary(j=>j.Id);
        foreach(var id in deferred.Keys.Where(id=>!jobs.ContainsKey(id)).ToArray())deferred.Remove(id);
        foreach(var (id,run) in runs.ToArray())
        {
            if(!jobs.TryGetValue(id,out var job))
            {
                dispatcher.Remove(id);runs.Remove(id);issues.Remove(id);tasks.Remove(id);readyRounds.Remove(id);
                recordedStates.Remove(id);recordedRunnerFaults.Remove(id);recordedObservations.Remove(id);planFingerprint=null;
                try
                {
                    var action=run.Batch["action_id"]!.GetValue<string>();var deletedState=run.Runner.Snapshot();
                    if(deletedState["pending"] is null&&deletedState["submitted"]?.GetValue<int>()==0){ports.GatheringConsumables?.ReleaseUnsubmitted(action);inventory.ReleaseUnsubmitted(action);}
                    else ports.GatheringConsumables?.ReconcileEndedBatch(action);
                    SaveReservations();
                }
                catch(Exception error)when(error is not OutOfMemoryException){Record("warning","queue_delete","task","detached",error.Message,error);}
                continue;
            }
            var state=run.Runner.Snapshot();var submitted=state["submitted"]?.GetValue<int>()??0;
            if((job.Paused||job.Cancelled)&&submitted==0&&state["pending"] is null
                &&(run.Runner is not IdleFishingRunner||state["round_seen"]?.GetValue<bool>()!=true&&state["navigation_seen"]?.GetValue<bool>()!=true))
            {ports.GatheringConsumables?.ReleaseUnsubmitted(run.Batch["action_id"]!.GetValue<string>());SaveReservations();queue.Release(run.Batch);dispatcher.Remove(id);runs.Remove(id);planFingerprint=null;continue;}
            dispatcher.SetHeld(id,job.Paused);
            if(job.Cancelled)
                switch(run.Runner){case ProductionRunner p:p.StopSubmitting();break;case GatheringRunner g:g.StopSubmitting();break;case IdleFishingRunner f:f.StopSubmitting();break;}
        }
    }
    private sealed class WithdrawalStore(IWorkflowStore inner,string scope,string action):IWorkflowStore
    {
        public JsonObject? Read()=>inner.Read();
        public void Flush(JsonObject state){state["runtime_scope"]=scope;state["runtime_action"]=action;inner.Flush(state);}
    }
    private void RestoreWithdrawal()
    {
        var store=new JsonWorkflowStore(Path.Combine(queue.DirectoryPath,"storage-supply.json"));var saved=store.Read();
        if(saved is null||saved["status"]?.GetValue<string>()=="complete")return;
        try
        {
            if(saved["runtime_scope"]?.GetValue<string>()!=ports.Scope||saved["runtime_action"]?.GetValue<string>() is not {} action)throw new InvalidDataException("取料记录范围未核实，保留待核账");
            if(saved["plans"] is not JsonArray {Count:1} rows)throw new InvalidDataException("取料计划不能完整恢复");
            var row=rows[0]!;var p=new WithdrawalPlan(row["item_id"]!.GetValue<long>(),row["name"]!.GetValue<string>(),row["quantity"]!.GetValue<long>(),row["scope"]!.GetValue<string>(),row["goal_ids"]!.AsArray().Select(n=>n!.GetValue<string>()).ToArray());
            var bound=new WithdrawalStore(store,ports.Scope,action);withdrawal=new([p],ports.Storage(p,action,bound),bound,time:clock);withdrawalKey=p.Scope+":"+p.ItemId;
        }
        catch(Exception error){issues["storage_restore"]="仓库取料仅核账："+error.Message;Record("error","storage_restore","module","manual_review",error.Message,error);}
    }
    private long? RequiredReserve(long item)
    {
        try
        {
            var q=queue.Read();
            if(!ports.IsExplicitSurplusMaterial(item)&&!q.Jobs.Any(j=>j.Kind=="gathering"&&!j.Cancelled&&!j.Paused&&j.ItemId==item))return null;
            if(q.Jobs.Any(j=>j.Pending&&!runs.ContainsKey(j.Id)))return null;
            var goals=q.Jobs.Where(j=>!j.Cancelled&&j.Target>j.Completed&&!j.Id.StartsWith("auto-material:")&&j.Kind=="production")
                .Select(j=>new MaterialGoal(j.Id,j.ItemId,j.Target-j.Completed,j.Kind,j.Kind=="production"?j.Recipe:null,j.Kind=="gathering"?j.Recipe:null));
            var snapshot=inventory.Apply(ports.Inventory());var required=catalog.Planner().Plan(goals,snapshot);
            if(required.Issues.Any(i=>i.Kind is "inventory_unknown" or "recipe_cycle" or "no_source"))return null;
            var inputs=required.Allocations.Where(a=>a.ItemId==item&&a.SourceKind=="inventory").Sum(a=>a.Quantity);
            var retained=q.Jobs.Where(j=>!j.Cancelled&&j.ItemId==item&&!j.Id.StartsWith("auto-material:")&&j.Kind=="production").Sum(j=>j.Completed);
            issues.Remove("surplus_reserve");
            return checked(inputs+retained);
        }
        catch(Exception error){issues["surplus_reserve"]="贩售保留量待核对："+error.Message;Record("error","surplus_reserve","module","manual_review",error.Message,error);return null;}
    }
    private string ReservationPath=>Path.Combine(queue.DirectoryPath,"gathering-reservations.json");
    private void SaveReservations()
    {
        if(ports.GatheringConsumables is not {} consumables)return;
        AtomicJson.Write(ReservationPath,new JsonObject{["schema_version"]=1,["reservations"]=new JsonArray(consumables.Reservations.Select(r=>(JsonNode?)r.DeepClone()).ToArray())});
    }
    private void RestoreReservations()
    {
        if(!File.Exists(ReservationPath))return;
        var saved=JsonNode.Parse(File.ReadAllText(ReservationPath))!.AsObject();
        if(saved["reservations"] is not JsonArray rows||rows.Count>10000)throw new InvalidDataException("耗材预留记录无效");
        foreach(var row in rows)
        {if(ports.GatheringConsumables is not {} consumables)throw new InvalidDataException("已有耗材预留但适配器不可用");consumables.RestoreReservation(row!.AsObject());}
    }
    private void ReconcileReservations()
    {
        if(ports.GatheringConsumables is not {} consumables)return;
        var active=runs.Values.Select(r=>r.Batch["action_id"]!.GetValue<string>()).ToHashSet();var changed=false;
        foreach(var held in consumables.Reservations)
        {var action=held["action_id"]!.GetValue<string>();if(!active.Contains(action))changed|=consumables.ReconcileEndedBatch(action);}
        if(changed)SaveReservations();
    }
    private sealed class ReservationStore(IWorkflowStore inner,Action save):IWorkflowStore
    {public JsonObject? Read()=>inner.Read();public void Flush(JsonObject state){save();inner.Flush(state);}}
    private string PurchasePath=>Path.Combine(queue.DirectoryPath,"bottle-purchase.json");
    private string PurchaseContextPath=>Path.Combine(queue.DirectoryPath,"bottle-purchase-context.json");
    private PurchaseState? PersistedPurchase()=>File.Exists(PurchasePath)?JsonSerializer.Deserialize<PurchaseState>(File.ReadAllText(PurchasePath)):null;
    private bool PurchaseArrivalCovered(GatheringSupplyRequest request)
    {
        var arrived=current.Query(request.ItemId);
        if(!arrived.Confirmed||arrived.Quantity is not{} quantity||quantity<request.TargetStock||arrived.VerifiedThrough is not{} through||!double.IsFinite(through))return false;
        if(purchase!.State.CompletedThrough is{} completed)return through>=completed;
        // Legacy complete records have no persisted completion watermark. A later
        // advancing watermark is safe; a renamed version or instance is not.
        if(purchaseCompletionBaseline is null){purchaseCompletionBaseline=through;return false;}
        return through>purchaseCompletionBaseline.Value;
    }
    private void RestorePurchase()
    {
        if(!File.Exists(PurchasePath))return;
        try
        {
            var saved=PersistedPurchase();if(saved is null)return;
            var context=JsonNode.Parse(File.ReadAllText(PurchaseContextPath))!.AsObject();
            var request=context["request"]!.Deserialize<GatheringSupplyRequest>()!;var action=context["action"]!.GetValue<string>();
            if(request.Scope!=ports.Scope)throw new InvalidDataException("采购记录角色不匹配");
            var adapter=ports.Purchase(request,action,PersistedPurchase)??throw new InvalidOperationException("采购适配不可用");
            purchaseRequest=request;purchaseAction=action;purchaseCompletionBaseline=null;purchase=new(PurchasePath,adapter,clock,()=>ports.PurchasePreferences,()=>purchaseRequest is{} active?current.Query(active.ItemId).VerifiedThrough:null);
        }
        catch(Exception error){purchaseRestoreBlocked=true;issues["purchase_restore"]="采购仅核账："+error.Message;Record("error","purchase_restore","module","manual_review",error.Message,error);}
    }
    private bool TickPurchase(bool allowNew)
    {
        if(!dispatcher.SafeToSwitch||withdrawal is not null&&!withdrawal.SafeToSwitch)return false;
        if(purchase is not null)
        {
            if(purchase.State.Intent is null&&purchaseRequest is{} activeSupply&&activeSupply.Name is not ("空瓶" or "紙"))
            {issues["purchase"]="该材料禁止自动购买，请手动补充";purchase=null;purchaseRequest=null;return false;}
            if(purchase.State.Intent is null&&purchase.State.Stage is (PurchaseStage.Navigate or PurchaseStage.Confirm or PurchaseStage.Blocked or PurchaseStage.UnknownStock)
                &&purchaseRequest is{} unsubmitted)
            {
                if(StorageUnknown(unsubmitted.ItemId)){issues["purchase"]="购买前仓库库存未知，等待完整抓包";return false;}
                if(current.Storage.Values.Any(rows=>rows.GetValueOrDefault(unsubmitted.ItemId)>0))
                {
                    AtomicJson.Write(PurchasePath,JsonSerializer.SerializeToNode(purchase.State with{Stage=PurchaseStage.Complete,Reason="仓库已有材料，未付款采购撤回"})!);
                    purchase=null;purchaseRequest=null;purchaseAction=null;issues.Remove("purchase");planFingerprint=null;return false;
                }
            }
            var liveRequest=purchaseRequest?.RouteKey=="material_supply"?queue.Read().Jobs.Any(j=>j.Id==purchaseRequest.ActionId&&!j.Cancelled&&!j.Paused&&j.Completed<j.Target):purchaseRequest is not null&&runs.Values.Any(r=>r.Batch["action_id"]!.GetValue<string>()==purchaseRequest.ActionId)
                &&queue.Read().Jobs.Any(j=>j.Pending&&!j.Cancelled&&!j.Paused&&runs.GetValueOrDefault(j.Id)?.Batch["action_id"]?.GetValue<string>()==purchaseRequest.ActionId);
            if(purchase.State.Intent is null&&!liveRequest)
            {
                AtomicJson.Write(PurchasePath,JsonSerializer.SerializeToNode(purchase.State with{Stage=PurchaseStage.Complete,Reason="采集目标已停止，未付款采购撤回"})!);
                purchase=null;purchaseRequest=null;purchaseAction=null;return false;
            }
            if(purchase.State.Stage==PurchaseStage.Complete)
            {
                if(!liveRequest){purchase=null;purchaseRequest=null;purchaseAction=null;issues.Remove("purchase");planFingerprint=null;return false;}
                var action=purchaseRequest?.ActionId;
                if(purchaseRequest is {} completedRequest&&catalog.Gathering.TryGetValue(completedRequest.RouteKey,out var route))
                {
                    var alreadyReserved=ports.GatheringConsumables?.Reservations.Any(r=>r["action_id"]?.GetValue<string>()==completedRequest.ActionId)==true;
                    if(purchase.State.Intent is not null&&!alreadyReserved&&!PurchaseArrivalCovered(completedRequest))
                    {issues["purchase"]="采购已完成，等待覆盖完成点的新抓包到货并安全预留";return false;}
                    var reservation=ports.GatheringConsumables?.ReserveForBatch(route,completedRequest.ActionId);
                    if(reservation is {Ready:false}){issues["purchase"]="采购已完成，等待新抓包到货并安全预留";return false;}
                    SaveReservations();
                }
                else if(purchaseRequest is{} materialRequest&&purchase.State.Intent is not null&&!PurchaseArrivalCovered(materialRequest))
                {issues["purchase"]="采购已完成，等待覆盖完成点的新抓包到货";return false;}
                purchase=null;purchaseRequest=null;purchaseAction=null;issues.Remove("purchase");planFingerprint=null;
                if(action is not null)foreach(var pair in runs.Where(r=>r.Value.Batch["action_id"]!.GetValue<string>()==action))dispatcher.SupplyChanged(pair.Key);
                return false;
            }
            if(purchase.State.Stage is PurchaseStage.Blocked or PurchaseStage.UnknownStock)
            {
                purchase.ObserveOnly();
                if(purchase.State.Stage==PurchaseStage.Complete)return true;
                if(purchase.State.Intent is not null){issues["purchase"]=purchase.State.Reason??"已有付款记录待核对";Publish(issues["purchase"]);return true;}
                if(purchaseRequest is {} priorRequest&&priorRequest.RouteKey!="material_supply")
                {
                    var latest=ports.GatheringConsumables?.PendingRequests.FirstOrDefault(r=>r.ActionId==priorRequest.ActionId);
                    if(latest is null)
                    {
                        AtomicJson.Write(PurchasePath,JsonSerializer.SerializeToNode(purchase.State with{Stage=PurchaseStage.Complete,Reason="采集耗材需求已由当前库存满足"})!);
                        purchase=null;purchaseRequest=null;purchaseAction=null;issues.Remove("purchase");planFingerprint=null;return false;
                    }
                    if(latest!=priorRequest||purchasePreferences!=ports.PurchasePreferences)
                    {purchaseRequest=latest;purchasePreferences=ports.PurchasePreferences;purchase.Request(Math.Max(0,latest.TargetStock-latest.Quantity),targetStock:latest.TargetStock);}
                }
                issues["purchase"]=purchase.State.Reason??"采购条件尚未满足";return false;
            }
            purchase.Tick();
            if(purchase.State.Stage==PurchaseStage.Reconcile){issues["purchase"]="采购结果仅核账，未重放付款";Publish(issues["purchase"]);return true;}
            Publish(purchase.State.Intent is null?"正在补充采集耗材":"采购结果已核对，正在退出商店");return true;
        }
        if(!allowNew||purchaseRestoreBlocked||!ports.PurchasePreferences.AutoBuyBottles)return false;
        var request=NextPurchaseRequest();
        if(request is null)return false;
        if(request.Name is not ("空瓶" or "紙")){issues["purchase"]="该材料禁止自动购买，请手动补充";return false;}
        if(withdrawal is not null)return false;
        if(current.Storage.Values.Any(s=>s.GetValueOrDefault(request.ItemId)>0))return SupplyFromStorage();
        if(StorageUnknown(request.ItemId))
        {issues["purchase"]="购买前仓库库存未知，等待完整抓包";return false;}
        var actionId="purchase:"+Guid.NewGuid().ToString("N");var port=ports.Purchase(request,actionId,PersistedPurchase);
        if(port is null){issues["purchase"]="采购适配尚未连接";return false;}
        var old=PersistedPurchase();if(old is not null&&old.Stage!=PurchaseStage.Complete){issues["purchase"]="已有采购记录待核账";return false;}
        AtomicJson.Write(PurchaseContextPath,new JsonObject{["request"]=JsonSerializer.SerializeToNode(request),["action"]=actionId});
        purchaseRequest=request;purchaseAction=actionId;purchasePreferences=ports.PurchasePreferences;purchaseCompletionBaseline=null;purchase=new(PurchasePath,port,clock,()=>ports.PurchasePreferences,()=>purchaseRequest is{} active?current.Query(active.ItemId).VerifiedThrough:null);
        purchase.Request(Math.Max(0,request.TargetStock-request.Quantity),targetStock:request.TargetStock);Publish("准备补充"+request.Name);return true;
    }
    private void UpdateConsumableDemand(QueueDocument q)
        =>ports.GatheringConsumables?.SetRemainingDemand(q.Jobs
            .Where(j=>j.Kind=="gathering"&&!j.Paused&&!j.Cancelled&&j.Target>j.Completed&&(j.Pending||q.Status(j.Id)=="queued"))
            .Select(j=>(j.Recipe??"",j.Target-j.Completed)));
    private GatheringSupplyRequest? NextPurchaseRequest()
    {
        var bottle=ports.GatheringConsumables?.PendingRequests.FirstOrDefault(r=>r.Scope==ports.Scope&&runs.Values.Any(run=>run.Batch["action_id"]!.GetValue<string>()==r.ActionId));
        if(bottle is not null)return bottle;
        foreach(var missing in plan?.Issues.Where(i=>i.Kind=="no_source")??[])
        {
            var name=catalog.Names.GetValueOrDefault(missing.ItemId);if(name is not ("空瓶" or "紙")||missing.GoalId is null)continue;
            var stock=current.Query(missing.ItemId);if(!stock.Confirmed||stock.Quantity is null)continue;
            var deficit=Math.Min(999,Math.Max(1,missing.RequiredQuantity));
            return new("material_supply",missing.GoalId,missing.ItemId,name,deficit,ports.Scope,stock.Version,checked(stock.Quantity.Value+deficit));
        }
        return null;
    }
    public void Dispose(){if(disposed)return;disposed=true;running=false;ports.Pause("closed");ports.Dispose();}
}
