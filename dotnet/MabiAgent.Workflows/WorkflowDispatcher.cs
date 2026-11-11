using System.Text.Json.Nodes;
using MabiAgent.Core;
namespace MabiAgent.Workflows;

// Receives ready work from the material planner; owns only the foreground turn.
// A queued candidate never authorizes a click. Each runner/port still reviews
// its current screen and durable intent before dispatch.
public sealed class WorkflowDispatcher(TimeProvider? time=null,Action<string,Exception,string>? fault=null)
{
    private sealed record Entry(string? Device,Func<JsonObject> Snapshot,Func<bool> Safe,Action Tick,Action<bool> Resume,Action<string> Pause,Func<bool>? Hold,Action? Observe=null,Func<bool>? Isolatable=null,Func<bool>? Recover=null);
    private readonly Dictionary<string,Entry> entries=[];
    private readonly Scheduler scheduler=new();
    private readonly HashSet<string> supplyChanged=[];
    private readonly HashSet<string> explicitlyResumable=[];
    private readonly HashSet<string> held=[];
    private readonly Dictionary<string,string> quarantined=[];
    public IReadOnlyDictionary<string,string> Faults=>new Dictionary<string,string>(quarantined);
    private readonly TimeProvider clock=time??TimeProvider.System;
    private bool paused=true;
    private bool restartAuthorized;
    public string? ActiveId {get;private set;}
    public string Status {get;private set;}="尚未开始";
    public bool IsPaused=>paused;
    public string? PauseReason{get;private set;}
    public bool SafeToSwitch=>ActiveId is null||!entries.TryGetValue(ActiveId,out var e)||e.Safe();
    public bool CanSchedule(string id)
    {
        if(held.Contains(id)||quarantined.ContainsKey(id))return false;
        if(!entries.TryGetValue(id,out var entry))return true;
        var state=entry.Snapshot();var status=state["status"]?.GetValue<string>();
        return status!="complete"&&(status!="unknown"||explicitlyResumable.Contains(id))
            &&(status!="paused"||supplyChanged.Contains(id)||explicitlyResumable.Contains(id));
    }
    public void RestoreOwnership(string id)
    {if(!entries.ContainsKey(id))throw new ArgumentException("恢复目标不存在");if(ActiveId is not null&&ActiveId!=id)throw new InvalidDataException("多个任务的操作尚未结束，请确认上次操作结果");ActiveId=id;}
    public void Remove(string id){entries.Remove(id);supplyChanged.Remove(id);explicitlyResumable.Remove(id);held.Remove(id);quarantined.Remove(id);if(ActiveId==id)ActiveId=null;}
    public void SetHeld(string id,bool value)
    {if(value)held.Add(id);else if(held.Remove(id))explicitlyResumable.Add(id);}
    public void Register(string id,StorageSupplyRunner runner)=>entries.Add(id,new(null,runner.Snapshot,()=>runner.SafeToSwitch,()=>runner.Tick(),runner.Resume,runner.Pause,null,runner.ObserveOnly,()=>runner.CanIsolateFault));
    public void Register(string id,IdleFishingRunner runner)
    {
        entries.Add(id,new(null,runner.Snapshot,()=>runner.SafeToSwitch,()=>runner.Tick(),runner.Resume,runner.Pause,null,runner.ObserveOnly,()=>runner.CanIsolateFault));
        ResumeDeferredRestart(id,runner.Snapshot(),resumeCastWait:true);
    }
    public void Register(string id,ProductionRunner runner,string device)
    {
        entries.Add(id,new(device,runner.Snapshot,()=>runner.SafeToSwitch,()=>runner.Tick(),explicitRequest=>runner.Resume(explicitRequest),reason=>runner.Pause(reason),runner.HoldForDeviceRefill,runner.ObserveOnly,()=>runner.CanIsolateFault,runner.RecoverCollection));
        // Restored batches may be registered after Start/Continue during deferred
        // inventory bootstrap. Carry that explicit start to safe restart records.
        ResumeDeferredRestart(id,runner.Snapshot());
    }
    private void ResumeDeferredRestart(string id,JsonObject state,bool resumeCastWait=false)
    {
        // 钓鱼未起竿已有20秒恢复规则；接回原等待不立即重放抛竿。
        if(restartAuthorized&&state["status"]?.GetValue<string>()=="paused"&&state["pause_reason"]?.GetValue<string>()=="restart"
            &&(state["pending"] is null||resumeCastWait&&state["pending"]?["kind"]?.GetValue<string>()=="start"))
            explicitlyResumable.Add(id);
    }
    public void Register(string id,GatheringRunner runner)
    {
        entries.Add(id,new(null,runner.Snapshot,()=>runner.SafeToSwitch||runner.WaitingForConsumables,()=>runner.Tick(),explicitRequest=>runner.Resume(explicitRequest),reason=>runner.Pause(reason),null,runner.ObserveOnly,()=>runner.CanIsolateFault));
        ResumeDeferredRestart(id,runner.Snapshot());
    }
    public void SupplyChanged(string id)
    {
        if(paused||quarantined.ContainsKey(id)||!entries.TryGetValue(id,out var entry))return;
        var state=entry.Snapshot();
        if(state["status"]?.GetValue<string>()=="paused"&&state["pause_reason"]?.GetValue<string>() is not ("manual" or "focus_lost")&&entry.Safe())supplyChanged.Add(id);
    }
    public void Continue(bool explicitRequest=true)
    {
        if(explicitRequest){quarantined.Clear();restartAuthorized=true;}paused=false;PauseReason=null;Status="等待可执行任务";
        foreach(var id in entries.Keys.Where(id=>!held.Contains(id)&&!quarantined.ContainsKey(id)))
        {
            var pauseReason=entries[id].Snapshot()["pause_reason"]?.GetValue<string>();
            // A manual pause supersedes an earlier broad Continue marker. An
            // inventory-sync suspension is the only paused state that a refresh
            // completion may resume without a new explicit user request.
            if(pauseReason=="manual")explicitlyResumable.Remove(id);
            if(explicitRequest||pauseReason=="inventory_sync")explicitlyResumable.Add(id);
        }
    }
    public void Pause(string reason="manual"){paused=true;if(reason!="inventory_sync")restartAuthorized=false;PauseReason=reason;Status="已暂停";if(ActiveId is not null&&entries.TryGetValue(ActiveId,out var active))active.Pause(reason);}
    public string? Tick(IEnumerable<WorkCandidate> planned,string? currentDevice,bool backpackRelief=false,string? onsiteDevice=null,bool locationVerified=false)
    {
        try{return TickCore(planned,currentDevice,backpackRelief,locationVerified?onsiteDevice:currentDevice);}
        catch(Exception error)when(error is not (IOException or UnauthorizedAccessException or OutOfMemoryException))
        {
            var id=ActiveId;
            if(id is null||!entries.TryGetValue(id,out var entry))throw;
            if(quarantined.ContainsKey(id)){Status="任务异常已隔离，等待上次操作确认或点击继续";return id;}
            var isolated=false;
            try{isolated=entry.Isolatable?.Invoke()==true;}catch(Exception checkError)when(checkError is not OutOfMemoryException){error.Data["release_check_failure"]=checkError.ToString();}
            entry.Pause("module_fault");
            quarantined[id]=error.Message;explicitlyResumable.Remove(id);supplyChanged.Remove(id);
            fault?.Invoke(id,error,isolated?"task":"foreground");
            if(!isolated){Status="任务异常已隔离，保留上次操作占用";throw;}
            ActiveId=null;Status="单个任务异常已隔离，继续独立任务";return null;
        }
    }
    private string? TickCore(IEnumerable<WorkCandidate> planned,string? currentDevice,bool backpackRelief,string? onsiteDevice)
    {
        if(paused)return ActiveId;
        var now=(clock.GetUtcNow()-DateTimeOffset.UnixEpoch).TotalSeconds;
        var candidates=planned.Where(p=>entries.ContainsKey(p.Id)&&!held.Contains(p.Id)&&!quarantined.ContainsKey(p.Id)).ToArray();
        if(ActiveId is not null&&entries.TryGetValue(ActiveId,out var active))
        {
            if(quarantined.ContainsKey(ActiveId))
            {
                active.Observe?.Invoke();
                if(active.Safe()||active.Isolatable?.Invoke()==true||active.Snapshot()["status"]?.GetValue<string>()=="complete")
                {ActiveId=null;Status="上次操作已确认，继续独立任务";return null;}
                Status="任务异常已隔离，等待上次操作确认或点击继续";return ActiveId;
            }
            var state=active.Snapshot();
            var status=state["status"]?.GetValue<string>();
            if(explicitlyResumable.Remove(ActiveId)){active.Resume(true);Status="核验任务页面";return ActiveId;}
            if(held.Contains(ActiveId))
            {
                active.Observe?.Invoke();
                if(active.Safe()||active.Snapshot()["status"]?.GetValue<string>()=="complete"||active.Device is not null&&active.Snapshot()["pending"] is null)ActiveId=null;
                else {Status="目标已暂停，等待当前原生批结束";return ActiveId;}
            }
            if(ActiveId is null)return TickCore(candidates,currentDevice,backpackRelief,onsiteDevice);
            if(status=="paused"&&state["pause_reason"]?.GetValue<string>() is "manual" or "focus_lost")
            {paused=true;PauseReason=state["pause_reason"]!.GetValue<string>();Status=state["pause_reason"]!.GetValue<string>()=="manual"?"已暂停":"游戏失焦，等待继续";return ActiveId;}
            if(status=="paused"&&!active.Safe())
            {
                active.Observe?.Invoke();
                if(active.Safe()||active.Snapshot()["status"]?.GetValue<string>()=="complete"){ActiveId=null;Status="当前轮次已确认结束";return null;}
                Status="当前任务尚未结束，已暂停操作，请点击继续";return ActiveId;
            }
            var refill=state["phase"]?.GetValue<string>()=="leave"&&active.Device==currentDevice&&currentDevice is not null
                &&candidates.Any(p=>p.Id!=ActiveId&&p.Kind=="production"&&p.Device==currentDevice&&p.Ready&&!p.Blocked&&p.FreeSlots>0&&!(p.Automatic&&p.BufferSatisfied));
            if(refill&&active.Hold is not null)active.Hold();
            state=active.Snapshot();status=state["status"]?.GetValue<string>();
            if(status!="complete"&&!active.Safe())
            {
                if(status=="unknown")
                {
                    var released=active.Recover?.Invoke();
                    if(released is null)active.Observe?.Invoke();
                    var after=active.Snapshot();
                    // Passive confirmation never grants permission to restart input.
                    if(released==true||after["status"]?.GetValue<string>()=="complete"||active.Safe())ActiveId=null;
                    Status=candidates.Any(c=>c.Id==ActiveId&&c.Kind=="idle_fishing")?"钓鱼动作结果待确认，请核实后继续":"正在确认上次操作结果";return ActiveId;
                }
                active.Tick();Status="执行当前批次";return ActiveId;
            }
        }
        var eligible=candidates.Select(candidate=>
        {
            var state=entries[candidate.Id].Snapshot();var status=state["status"]?.GetValue<string>();
            if(!CanSchedule(candidate.Id))return candidate with{Ready=false};
            if(state["phase"]?.GetValue<string>()=="waiting")
            {
                var due=ProductionRunner.CollectionWakeAt(state,candidate.Device==onsiteDevice);
                return candidate with{Ready=due is not null&&due<=now,Collectible=due is not null&&due<=now,FreeSlots=0};
            }
            return candidate;
        }).ToArray();
        var next=scheduler.Select(eligible,new(null,false,currentDevice,backpackRelief));
        if(next is null){ActiveId=null;Status="等待材料或加工到期";return null;}
        var selected=entries[next.Id];ActiveId=next.Id;
        var explicitResume=explicitlyResumable.Remove(next.Id);var supplied=supplyChanged.Remove(next.Id);
        if(selected.Snapshot()["status"]?.GetValue<string>() is "idle" or "yielded"||supplied||explicitResume){selected.Resume(true);Status="核验任务页面";return ActiveId;}
        if(selected.Hold is not null&&next.Device==currentDevice&&currentDevice is not null
            &&eligible.Any(p=>p.Id!=next.Id&&p.Device==currentDevice&&p.Kind=="production"&&p.Ready&&!p.Blocked&&p.FreeSlots>0&&!(p.Automatic&&p.BufferSatisfied))
            &&selected.Hold())
        {Status="保留设备页面，继续补槽";return ActiveId;}
        selected.Tick();Status="执行任务";return ActiveId;
    }
}
