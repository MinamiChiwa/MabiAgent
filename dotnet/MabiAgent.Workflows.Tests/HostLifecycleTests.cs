using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Workflows.Tests;

public sealed class HostLifecycleTests : IDisposable
{
    [Fact] public void SurplusOnlyAcceptsCurrentGatheringTargetsAndExplicitMaterials()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("ore","gathering",1,1,"ore",[]));
        var ports=new Ports();ports.Set(1,483);ports.Set(2,483);using var host=Host(queue,ports,new ManualTime());
        Assert.Equal(0L,ports.Reserve!(1));Assert.Null(ports.Reserve(2));
        ports.ExplicitSurplus.Add(2);Assert.Equal(0L,ports.Reserve(2));ports.ExplicitSurplus.Clear();Assert.Null(ports.Reserve(2));
        queue.Edit(q=>q.Control("ore","pause"));Assert.Null(ports.Reserve(1));queue.Edit(q=>q.Control("ore","continue"));Assert.NotNull(ports.Reserve(1));
        queue.Edit(q=>q.Delete(["ore"]));Assert.Null(ports.Reserve(1));
    }
    [Fact] public void FirstContinueAfterBootstrapStartsFreshMaintenanceIntervalsAndLaterContinueKeepsThem()
    {
        var queue=new RuntimeQueue(directory);var ports=new Ports();var time=new ManualTime();
        var policy=new MaintenancePolicy(IntervalSeconds:30,BackpackCheckIntervalSeconds:120);
        var store=new JsonWorkflowStore(Path.Combine(directory,"maintenance.json"));
        var seed=new MaintenanceRunner(ports.Maintain,store,policy,time).Snapshot();seed["repair"]!["due_at"]=7200d;seed["backpack"]!["check_due_at"]=1d;store.Flush(seed);
        ports.Start();using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>policy,time);
        host.Pause("inventory_sync_complete");time.Seconds=500;host.Continue();
        Assert.Equal(1,ports.Starts);Assert.Equal(1,ports.Continues);
        var began=store.Read()!;Assert.Equal(530d,began["repair"]!["due_at"]!.GetValue<double>());Assert.Equal(620d,began["backpack"]!["check_due_at"]!.GetValue<double>());
        host.Pause();time.Seconds=510;host.Continue();var resumed=store.Read()!;
        Assert.Equal(530d,resumed["repair"]!["due_at"]!.GetValue<double>());Assert.Equal(620d,resumed["backpack"]!["check_due_at"]!.GetValue<double>());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void DecompositionRunsOnlyAfterWarehouseAllocationAndHoldsIndependentInput(bool inStorage)
    {
        var config=Catalog().Configuration;config["material_sources"]=new JsonObject{["2"]=new JsonObject{["kind"]="decomposition"}};
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("wood","production",4,1,"wood",[]));
        var ports=new Ports{Conversion=new()};ports.Set(2,0);if(inStorage)ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[2]=10});
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();host.Tick();
        if(inStorage){Assert.Equal(0,ports.Conversion.Requests);Assert.True(File.Exists(Path.Combine(directory,"storage-supply.json")));}
        else{Assert.Equal(1,ports.Conversion.Requests);Assert.False(host.SafeToSwitch);Assert.False(host.CanPrepareInventoryRefresh);var auxiliary=ports.AuxiliaryCalls;host.Tick();Assert.Equal(1,ports.Conversion.Ticks);Assert.Equal(auxiliary,ports.AuxiliaryCalls);host.Pause();host.Tick();Assert.Equal(1,ports.Conversion.Ticks);}
    }
    [Fact] public void OrdinaryQueueStillSchedulesPeriodicBackpackInspection()
    {
        var queue=new RuntimeQueue(directory);var ports=new Ports();var time=new ManualTime();
        using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:true,BackpackCheckIntervalSeconds:120),time);host.Start();
        host.Tick();Assert.Equal(0,ports.Maintain.Navigations);time.Seconds+=121;host.Tick();
        Assert.True(ports.Maintain.Navigations>0);Assert.Null(host.Status.Fault);
    }
    [Fact] public void ExhaustedBackpackReliefPausesForPlayerWithoutStorageTransfer()
    {
        var queue=new RuntimeQueue(directory);var ports=new Ports{Sale=new Ports.SurplusAdapter()};var time=new ManualTime();
        var policy=new MaintenancePolicy(RepairEnabled:false,WeightThresholdPercent:95,ProductionFirst:true,BackpackCheckIntervalSeconds:120);
        ports.Maintain.Frame=new("1","backpack",Weight:76968,Capacity:81000);
        using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>policy,time);host.Start();time.Seconds=121;
        for(var i=0;i<20&&host.Status.Running;i++)
        {
            ports.Maintain.Frame=ports.Maintain.Frame with{CaptureId=(i+2).ToString(),Page=ports.Sale.Ticks>0?"tidy":"backpack",BoldSelected=false,MaterialsSelected=false,TidyCount=0};
            host.Tick();
        }
        Assert.False(host.Status.Running);Assert.Equal("backpack_player_action_required",host.Status.PauseReason);
        Assert.Contains("请玩家处理",host.Status.Message);Assert.Empty(ports.Store.Actions);
    }
    [Fact] public void WaitingForUnknownBottlesDoesNotStarveIndependentGathering()
    {
        var config=Catalog().Configuration;config["item_names"]!["5"]="空瓶";config["item_names"]!["6"]="水";
        config["gathering_routes"]!["water"]=JsonNode.Parse("""{"target_item_id":6,"skill":"daily","node":"well","attempts_per_batch":10}""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("water","gathering",6,1,"water",[]));
        var ports=new Ports();ports.Gather.Frame=ports.Gather.Frame with{RouteKey="water"};
        ports.Consumables=new(new Dictionary<string,GatheringConsumableRule>{["water"]=new(5,"空瓶")},()=>ports.Stock,()=>new());
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();
        for(var n=0;n<5;n++)host.Tick();Assert.Equal("paused",host.Status.Jobs["water"].Status);Assert.Equal(0,ports.Gather.Starts);
        queue.Edit(q=>q.Add("ore","gathering",1,1,"ore",[]));ports.Gather.Frame=ports.Gather.Frame with{RouteKey="ore"};
        for(var n=0;n<10&&ports.Gather.Starts==0;n++)host.Tick();Assert.Equal(1,ports.Gather.Starts);
        Assert.Equal("paused",host.Status.Jobs["water"].Status);
    }
    [Fact] public void ReleasedUnknownGatheringDoesNotStarveNextUnclaimedGathering()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("first","gathering",1,1,"ore",[]));
        var ports=new Ports();ports.Set(1,0);var time=new ManualTime();using var host=Host(queue,ports,time);host.Start();
        for(var n=0;n<8&&ports.Gather.Starts==0;n++)host.Tick();Assert.Equal(1,ports.Gather.Starts);
        ports.Gather.Frame=ports.Gather.Frame with{TaskStatus="idle",Evidence=new JsonObject{["navigation_idle_confirmed"]=true}};
        for(var n=0;n<4;n++)host.Tick();Assert.True(host.SafeToSwitch);Assert.Equal("unknown",host.Status.Jobs["first"].Status);
        queue.Edit(q=>q.Add("second","gathering",1,1,"ore",[]));
        for(var n=0;n<10&&ports.Gather.Starts<2;n++)host.Tick();
        Assert.Equal(2,ports.Gather.Starts);Assert.True(queue.Read().Jobs.Single(j=>j.Id=="first").Pending);
        Assert.Equal("unknown",host.Status.Jobs["first"].Status);
    }
    [Fact] public void ExplicitStopClearsPendingAndProgressButKeepsUserGoalsAndSettings()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("user:a","production",3,6,"iron",[]);q.Add("auto-material:a","gathering",1,1,"mine",[]);});
        var batch=queue.Claim(queue.Read().Jobs.First(),"scope","production","device:metal",new JsonObject());
        var folder=queue.BatchDirectory("scope","user:a",batch["action_id"]!.GetValue<string>());
        File.WriteAllText(Path.Combine(folder,"production.json"),"{\"pending\":{\"kind\":\"craft\"}}");
        File.WriteAllText(Path.Combine(directory,"native-input-intents.json"),"{}");File.WriteAllText(Path.Combine(directory,"settings.json"),"keep");
        File.WriteAllText(Path.Combine(directory,"decomposition.json"),"{\"pending\":{\"kind\":\"decompose\"}}");
        queue.ResetAllExecution();
        var job=Assert.Single(queue.Read().Jobs);Assert.Equal("user:a",job.Id);Assert.Equal(6,job.Target);Assert.Equal(0,job.Completed);Assert.False(job.Pending);
        Assert.False(File.Exists(Path.Combine(folder,"production.json")));Assert.False(File.Exists(Path.Combine(directory,"native-input-intents.json")));Assert.Equal("keep",File.ReadAllText(Path.Combine(directory,"settings.json")));
        Assert.False(File.Exists(Path.Combine(directory,"decomposition.json")));
        queue.ResetAllExecution();Assert.Single(queue.Read().Jobs);
    }
    [Fact] public void UnknownWithdrawalLeavesRequestsOneRefreshThenAccountsWithoutAnotherTransfer()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));
        var ports=new Ports{BulkSync=true};ports.Set(1,0);ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[1]=10});var time=new ManualTime();
        using(var host=Host(queue,ports,time)){host.Start();host.Tick();host.Tick();Assert.Single(ports.Store.Actions);}
        var path=Path.Combine(directory,"storage-supply.json");var store=new JsonWorkflowStore(path);var saved=store.Read()!;
        // Restore the exact final-transfer boundary with unavailable packet balances.
        saved["pending"]!["kind"]="confirm_transfer";saved["pending"]!["deadline"]=101d;store.Flush(saved);
        using var restored=Host(queue,ports,time);ports.Store.LeaveAllowed=true;ports.Store.Frame=ports.Store.Frame with{At=116,CaptureId="late",Available=null,BagQuantity=null};restored.Start();
        for(var n=0;n<5;n++)restored.Tick();
        Assert.True(restored.SafeToSwitch);Assert.True(restored.CanPrepareInventoryRefresh);Assert.Equal(1,ports.SyncRequests);Assert.Single(ports.Store.Actions);Assert.NotNull(store.Read()!["pending"]);
        for(var n=0;n<3;n++)restored.Tick();Assert.Equal(1,ports.SyncRequests);
        ports.Store.Frame=ports.Store.Frame with{At=117,CaptureId="fresh",Available=0,BagQuantity=10,BalanceAt=117};ports.Set(1,10);ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[1]=0});
        restored.ContinueAfterInventorySync();for(var n=0;n<15;n++)Assert.Null(restored.Tick().Fault);
        Assert.True(store.Read()!["status"]!.GetValue<string>()=="complete",store.Read()!.ToJsonString());Assert.Null(store.Read()!["pending"]);Assert.Single(ports.Store.Actions);Assert.True(ports.Crafts>0);
    }
    [Fact] public void RuntimeProgressIsBoundToJobAndActionAcrossDeletionAndReordering()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,1,"iron",[]);q.Add("wood","production",4,1,"wood",[]);});
        var ports=new Ports();ports.Set(1,10);ports.Set(2,10);var time=new ManualTime();using var host=Host(queue,ports,time);host.Start();host.Tick();
        var first=host.Status.Jobs["iron"];Assert.False(first.PendingConsumption);Assert.NotEqual("unknown",first.Status);
        Assert.Equal(queue.Read().ToJson()["jobs"]![0]!["pending"]!["action_id"]!.GetValue<string>(),first.ActionId);
        queue.Edit(q=>{q.Delete(["iron"]);q.Add("new","production",3,1,"iron",[]);q.Move("new",-1,"wood");});host.SynchronizeQueue();host.Tick();
        Assert.DoesNotContain("iron",host.Status.Jobs.Keys);Assert.DoesNotContain(host.Status.Jobs.Values,p=>p.ActionId==first.ActionId);
        Assert.All(host.Status.Jobs,p=>Assert.Equal(queue.Read().ToJson()["jobs"]!.AsArray().Single(j=>j!["job_id"]!.GetValue<string>()==p.Key)!["pending"]!["action_id"]!.GetValue<string>(),p.Value.ActionId));
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void InPlaceFishingDefersRepairWhileKeepingBackpackInspectionAndGlobalPolicy(bool inPlace)
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("fish","idle_fishing",1,60,inPlace?FishingPlan.CurrentLocation:"lake",[]));
        var time=new ManualTime();var ports=new Ports();var policy=new MaintenancePolicy(IntervalSeconds:1,BackpackCheckIntervalSeconds:120);
        using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>policy,time);host.Start();time.Seconds+=2;
        for(var i=0;i<3;i++)host.Tick();
        if(inPlace)
        {
            Assert.Empty(ports.Maintain.Actions);Assert.Equal(0,ports.Maintain.Navigations);Assert.DoesNotContain(ports.Fish.Actions,a=>a.Kind=="navigate");
            queue.Edit(q=>q.Delete(["fish"]));host.SynchronizeQueue();
            ports.Maintain.Frame=new("2","scene");host.Tick();
            ports.Maintain.Frame=new("3","repair_quote",Gold:100000,Quote:1000,Currency:"gold",ConfirmAvailable:true);
            for(var i=0;i<3;i++)host.Tick();Assert.Contains("confirm_repair",ports.Maintain.Actions);
        }
        else
        {ports.Maintain.Frame=new("2","repair_quote",Gold:100000,Quote:1000,Currency:"gold",ConfirmAvailable:true);host.Tick();Assert.Contains("confirm_repair",ports.Maintain.Actions);}
        Assert.True(policy.RepairEnabled);
    }
    [Fact] public void WingsOfferRunsDuringMaintenanceNavigationButNotDuringPayment()
    {
        var ports=new Ports();ports.Maintain.Frame=new("1","scene");var time=new ManualTime();
        using var host=new AutomationCoordinator(Catalog(),new RuntimeQueue(directory),ports,()=>new(BackpackEnabled:false,IntervalSeconds:1),time);
        host.Start();time.Seconds+=2;host.Tick();Assert.False(host.SafeToSwitch);var safeCalls=ports.AuxiliaryCalls;
        ports.NavigationOffer=true;host.Tick();Assert.Equal(1,ports.NavigationAuxiliaryCalls);Assert.Equal(safeCalls,ports.AuxiliaryCalls);
        ports.NavigationOffer=false;ports.Maintain.Frame=new("2","repair_quote",Gold:100000,Quote:1000,Currency:"gold",ConfirmAvailable:true);host.Tick();
        Assert.Single(ports.Maintain.Actions,a=>a=="confirm_repair");var navigationCalls=ports.NavigationAuxiliaryCalls;
        ports.NavigationOffer=true;ports.Maintain.Frame=new("3","repair_quote",Gold:100000);host.Tick();Assert.Equal(navigationCalls,ports.NavigationAuxiliaryCalls);
    }
    [Fact] public void DeletingSubmittedTaskDetachesRunAndIndependentTaskContinuesAfterRestart()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,1,"iron",[]);q.Add("wood","production",4,1,"wood",[]);});
        var ports=new Ports();ports.Set(1,10);ports.Set(2,10);var time=new ManualTime();
        using(var host=Host(queue,ports,time))
        {
            host.Start();for(var n=0;n<12&&ports.Crafts==0;n++)host.Tick();Assert.Equal(1,ports.Crafts);
            queue.Edit(q=>q.Delete(["iron"]));host.SynchronizeQueue();
            for(var n=0;n<20&&ports.Crafts<2;n++)host.Tick();
            Assert.Equal(2,ports.Crafts);Assert.True(host.Status.Running);Assert.DoesNotContain("iron",host.Status.Issues.Keys);Assert.DoesNotContain(queue.Read().Jobs,j=>j.Id=="iron");
        }
        using var restored=Host(queue,ports,time);restored.Start();restored.Tick();Assert.DoesNotContain(queue.Read().Jobs,j=>j.Id=="iron");Assert.Equal(1,ports.IronInstances);
    }
    [Fact] public void DeletingClaimedTaskWithoutExplicitSynchronizeDoesNotPauseGlobalHost()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));var ports=new Ports();ports.Set(1,10);
        using var host=Host(queue,ports,new ManualTime());host.Start();host.Tick();Assert.True(queue.Read().Jobs[0].Pending);
        queue.Edit(q=>q.Delete(["iron"]));host.Tick();Assert.Empty(queue.Read().Jobs);Assert.Null(host.Status.Fault);Assert.Equal(0,ports.Crafts);
    }
    [Fact] public void DependencyBlockedGoalDoesNotClaimOrPauseIndependentWork()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,1,"iron",[]);q.Control("iron","pause");q.Add("wood","production",4,1,"wood",["iron"]);});
        var ports=new Ports();ports.Set(1,10);ports.Set(2,10);using var host=Host(queue,ports,new ManualTime());host.Start();host.Tick();
        Assert.True(host.Status.Running,host.Status.Fault);Assert.Null(host.Status.Fault);Assert.DoesNotContain(queue.Read().Jobs,j=>j.Pending);Assert.Equal(0,ports.Crafts);
        queue.Edit(q=>q.Add("independent","production",3,1,"iron",[]));for(var i=0;i<12&&ports.Crafts==0;i++)host.Tick();
        Assert.Equal(1,ports.Crafts);Assert.False(queue.Read().Jobs.Single(j=>j.Id=="wood").Pending);Assert.True(host.Status.Running);
    }
    [Fact] public void BlockedRepairDoesNotPollItsExpiredDeadline()
    {
        var ports=new Ports();ports.Maintain.Frame=new("1","repair_quote",Gold:40000,Quote:1000,Currency:"gold",ConfirmAvailable:true);var time=new ManualTime();
        using var host=new AutomationCoordinator(Catalog(),new RuntimeQueue(directory),ports,()=>new(BackpackEnabled:false,IntervalSeconds:1),time);host.Start();time.Seconds+=2;host.Tick();
        ports.Maintain.Frame=new("2","scene");host.Tick();
        Assert.True(host.Status.Running);Assert.Empty(ports.Maintain.Actions);Assert.Equal(AutomationCadence.Health,host.NextCheckDelay());
        Assert.Equal("repair_reserve_insufficient",host.Status.Issues["maintenance"]);
    }
    [Fact] public void EarlierDeviceRetryIsNotOverwrittenByLaterRepairDeadline()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));var ports=new Ports{MetalFull=true};ports.Set(1,10);var time=new ManualTime();
        using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>new(BackpackEnabled:false,IntervalSeconds:300),time);host.Start();for(var i=0;i<12;i++)host.Tick();
        time.Seconds+=59;Assert.Equal(1,host.NextCheckDelay().TotalSeconds,5);
    }
    [Fact] public void SchedulingCadenceKeepsPendingActionsFastAndSleepsOnlyAtProductionWait()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));
        var ports=new Ports();ports.Set(1,10);var time=new ManualTime();using var host=Host(queue,ports,time);
        Assert.Equal(AutomationCadence.Health,host.NextCheckDelay());host.Start();host.Tick();
        Assert.Equal(AutomationCadence.Active,host.NextCheckDelay());
        for(var n=0;n<20;n++)host.Tick();Assert.Equal(1,ports.Crafts);Assert.True(host.SafeToSwitch);
        Assert.Equal(AutomationCadence.Health,host.NextCheckDelay());
        var saved=JsonNode.Parse(File.ReadAllText(Directory.GetFiles(directory,"production.json",SearchOption.AllDirectories).Single()))!;
        var due=Math.Max(saved["due_at"]!.GetFiniteDouble(),saved["collection_not_before"]!.GetFiniteDouble());time.Seconds=due-1.25;
        Assert.Equal(1.25,host.NextCheckDelay().TotalSeconds,5);
        time.Seconds=due;Assert.Equal(AutomationCadence.Active,host.NextCheckDelay());
        host.Pause();Assert.Equal(AutomationCadence.Health,host.NextCheckDelay());
        queue.Edit(q=>q.Control("iron","cancel"));host.ObservePausedWork();Assert.False(host.Status.Running);Assert.Equal(1,ports.Crafts);
    }
    [Fact] public void OffsiteMultiSlotBatchWakesAndReturnsAtFirstSlotAfterRestart()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,3,"iron",[]));
        var ports=new Ports();ports.Set(1,30);var time=new ManualTime();var config=Catalog().Configuration;config["recipes"]!["iron"]!["duration_s"]=600;
        AutomationCoordinator NewHost()=>new(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time);
        using(var host=NewHost()){host.Start();for(var i=0;i<40;i++)host.Tick();Assert.Equal(3,ports.Crafts);Assert.Null(ports.CurrentDevice);}
        var saved=JsonNode.Parse(File.ReadAllText(Directory.GetFiles(directory,"production.json",SearchOption.AllDirectories).Single()))!;
        var first=saved["slot_due_at"]!.AsObject().Min(p=>p.Value!.GetFiniteDouble());var tail=saved["production_due_at"]!.GetFiniteDouble();Assert.True(first<tail);
        using var restarted=NewHost();restarted.Start();restarted.Tick();time.Seconds=first-1.25;
        Assert.Equal(1.25,restarted.NextCheckDelay().TotalSeconds,5);var visits=ports.Navigation.Count;
        time.Seconds=first;Assert.Equal(AutomationCadence.Active,restarted.NextCheckDelay());
        for(var i=0;i<4&&ports.Navigation.Count==visits;i++)restarted.Tick();
        Assert.True(ports.Navigation.Count>visits);Assert.Equal("metal",ports.CurrentDevice);Assert.Equal(3,ports.Crafts);Assert.Equal(0,ports.Collections);
    }
    [Fact] public void ShortPeerRecipesShareDeviceTripIntervalAcrossRestartAndCollectTogether()
    {
        var config=Catalog().Configuration;config["recipes"]!["steel"]=config["recipes"]!["iron"]!.DeepClone();config["recipes"]!["steel"]!["output_item_id"]=5L;
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,1,"iron",[]);q.Add("steel","production",5,1,"steel",[]);});var ports=new Ports();ports.Set(1,20);var time=new ManualTime();
        AutomationCoordinator NewHost()=>new(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time);
        using(var host=NewHost()){host.Start();for(var i=0;i<40;i++)host.Tick();Assert.Equal(2,ports.Crafts);Assert.Null(ports.CurrentDevice);}
        var saved=Directory.GetFiles(directory,"production.json",SearchOption.AllDirectories).Select(f=>JsonNode.Parse(File.ReadAllText(f))!).ToArray();Assert.Equal(2,saved.Length);Assert.All(saved,s=>Assert.Equal(400,s["collection_not_before"]!.GetFiniteDouble()));
        using var restarted=NewHost();restarted.Start();time.Seconds=220;ports.Ready=true;var visits=ports.Navigation.Count;
        for(var i=0;i<5;i++)restarted.Tick();Assert.Equal(visits,ports.Navigation.Count);Assert.Equal(0,ports.Collections);
        time.Seconds=400;for(var i=0;i<30;i++)restarted.Tick();Assert.Equal(1,ports.Collections);Assert.Equal(2,ports.Crafts);Assert.All(queue.Read().Jobs.Where(j=>j.Kind=="production"),j=>Assert.Equal(1,j.Completed));
    }
    [Fact] public void EmptyPausedHostUsesHealthCheckAndNewReadyWorkRestoresActiveCadence()
    {
        var queue=new RuntimeQueue(directory);var ports=new Ports();using var host=Host(queue,ports,new ManualTime());
        host.Start();host.Tick();Assert.Equal(AutomationCadence.Health,host.NextCheckDelay());
        queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));ports.Set(1,10);host.Tick();
        Assert.Equal(AutomationCadence.Active,host.NextCheckDelay());
    }
    [Fact] public void IdleHostHonorsShortPlanningRetryDeadline()
    {
        var ports=new Ports();var time=new ManualTime();using var host=Host(new RuntimeQueue(directory),ports,time);
        host.Start();host.Tick();Assert.Equal(AutomationCadence.Health,host.NextCheckDelay());
        ports.ReadFailures=1;host.Tick();Assert.Equal(1,host.Status.RecoveryAttempt);
        Assert.Equal(.25,host.NextCheckDelay().TotalSeconds,5);
        time.Seconds+=.125;Assert.Equal(.125,host.NextCheckDelay().TotalSeconds,5);
        time.Seconds+=.125;host.Tick();Assert.Equal(0,host.Status.RecoveryAttempt);Assert.Equal(AutomationCadence.Health,host.NextCheckDelay());
    }
    [Fact] public void HostIsolatesSafeTaskFailureAndRunsIndependentDeviceWithTrace()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,1,"iron",[]);q.Add("wood","production",4,1,"wood",[]);});
        var ports=new Ports{FailIronNavigation=true};ports.Set(1,10);ports.Set(2,10);using var host=Host(queue,ports,new ManualTime());host.Start();for(var n=0;n<15&&ports.Crafts==0;n++)host.Tick();
        Assert.True(host.Status.Running,host.Status.Fault);Assert.Equal(1,ports.Crafts);Assert.Contains("iron",host.Status.Issues.Keys);
        var faults=File.ReadLines(Path.Combine(directory,"runtime-events.jsonl")).Select(line=>System.Text.Json.JsonSerializer.Deserialize<RuntimeFaultEvent>(line)!).ToArray();
        var fault=Assert.Single(faults,e=>e.Severity=="error"&&e.Job=="iron");Assert.Equal("task",fault.Scope);Assert.NotNull(fault.Action);Assert.NotNull(fault.Trigger);Assert.Contains("Navigate",fault.Exception);
    }
    [Fact] public void PausedHostObservesProductionIntentAndDoesNotRepeatCraft()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));var ports=new Ports();ports.Set(1,10);using var host=Host(queue,ports,new ManualTime());
        host.Start();for(var n=0;n<12&&ports.Crafts==0;n++)host.Tick();Assert.Equal(1,ports.Crafts);
        host.Pause();host.ObservePausedWork();Assert.False(host.Status.Running);Assert.Equal(1,ports.Crafts);
        var state=JsonNode.Parse(File.ReadAllText(Directory.GetFiles(directory,"production.json",SearchOption.AllDirectories).Single()))!;Assert.Null(state["pending"]);Assert.Equal(1,state["submitted"]!.GetValue<int>());
    }
    [Fact] public void RestartWithProductionIntentRestoresCharacterOwnership()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));var ports=new Ports();ports.Set(1,10);
        using(var host=Host(queue,ports,new ManualTime())){host.Start();for(var n=0;n<12&&ports.Crafts==0;n++)host.Tick();Assert.Equal(1,ports.Crafts);}
        using var restored=Host(queue,ports,new ManualTime());Assert.False(restored.SafeToSwitch);restored.ObservePausedWork();Assert.Equal(1,ports.Crafts);Assert.False(restored.Status.Running);
    }
    private readonly string directory=Path.Combine(Path.GetTempPath(),"mabi-host-lifecycle-"+Guid.NewGuid().ToString("N"));
    private static RuntimeCatalog Catalog()=>new(JsonNode.Parse("""
    {"item_names":{"1":"ore","2":"log","3":"iron","4":"wood"},"recipes":{"iron":{"output_item_id":3,"output_per_round":1,"device":"metal","duration_s":60,"materials":[{"item_id":1,"quantity":10}]},"wood":{"output_item_id":4,"output_per_round":1,"device":"wood","duration_s":60,"materials":[{"item_id":2,"quantity":10}]}},"gathering_routes":{"ore":{"target_item_id":1,"skill":"mining","node":"ore","attempts_per_batch":10}}}
    """)!.AsObject());
    private AutomationCoordinator Host(RuntimeQueue queue,Ports ports,ManualTime time)=>new(Catalog(),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time);
    [Fact] public void QueuePauseBeforeCraftReleasesClaimAndContinueCreatesOnlyOneCraft()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));var ports=new Ports();ports.Set(1,10);var time=new ManualTime();
        using var host=Host(queue,ports,time);host.Start();host.Tick();Assert.True(queue.Read().Jobs[0].Pending);
        queue.Edit(q=>q.Control("iron","pause"));host.Tick();Assert.False(queue.Read().Jobs[0].Pending);Assert.Equal(0,ports.Crafts);
        queue.Edit(q=>q.Control("iron","continue"));for(var i=0;i<10;i++)host.Tick();Assert.Equal(1,ports.Crafts);
    }
    [Fact] public void AutomaticUnsubmittedGatheringDisappearsWithoutReceiptOrPending()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));var ports=new Ports();ports.Set(1,0);var time=new ManualTime();
        using var host=Host(queue,ports,time);host.Start();host.Tick();Assert.Contains(queue.Read().Jobs,j=>j.Id.StartsWith("auto-material:")&&j.Pending);
        ports.Set(1,10);for(var i=0;i<3;i++)host.Tick();
        Assert.DoesNotContain(queue.Read().Jobs,j=>j.Id.StartsWith("auto-material:")&&j.Pending&&!j.Cancelled);Assert.Equal(0,ports.Gather.Starts);
    }
    [Fact] public void TwoDevicesSubmitWhileFirstStillWaitingAndCollectAtDueTime()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,1,"iron",[]);q.Add("wood","production",4,1,"wood",[]);});
        var ports=new Ports();ports.Set(1,10);ports.Set(2,10);var time=new ManualTime();
        using var host=Host(queue,ports,time);host.Start();for(var i=0;i<24;i++)host.Tick();
        Assert.Equal(2,ports.Crafts);Assert.Equal(2,queue.Read().Jobs.Count(j=>j.Pending));
        time.Seconds=300;ports.Ready=true;var visits=ports.Navigation.Count;for(var i=0;i<5;i++)host.Tick();Assert.Equal(visits,ports.Navigation.Count);Assert.Equal(0,ports.Collections);
        time.Seconds=400;for(var i=0;i<30;i++)host.Tick();
        Assert.All(queue.Read().Jobs.Where(j=>j.Kind=="production"),j=>{Assert.Equal(1,j.Completed);Assert.False(j.Pending);});Assert.All(queue.Read().Jobs.Where(j=>j.Kind=="gathering"),j=>{Assert.True(j.Cancelled);Assert.False(j.Pending);Assert.Equal(0,j.Completed);});Assert.Equal(2,ports.Crafts);
    }
    [Fact] public void PersistedWithdrawalRestartsOnlyWithAccountingAndNoDuplicateTransfer()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));var ports=new Ports();ports.Set(1,0);ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[1]=10});var time=new ManualTime();
        using(var host=Host(queue,ports,time)){host.Start();host.Tick();host.Tick();Assert.Single(ports.Store.Actions);}
        var saved=new JsonWorkflowStore(Path.Combine(directory,"storage-supply.json")).Read()!;Assert.Equal("character",saved["runtime_scope"]!.GetValue<string>());
        ports.Store.Frame=ports.Store.Frame with{At=101,CaptureId="2",Available=0,BagQuantity=10,BalanceAt=101};ports.Set(1,10);
        using(var restored=Host(queue,ports,time)){restored.Start();restored.Tick();restored.Tick();}
        Assert.Single(ports.Store.Actions);Assert.Equal("complete",new JsonWorkflowStore(Path.Combine(directory,"storage-supply.json")).Read()!["status"]!.GetValue<string>());
    }
    [Fact] public void SettledLedgerCompactsOnlyAfterActionLeavesRuntime()
    {
        var store=new MemoryStore();var ledger=new RuntimeInventory(store);var stock=new InventorySnapshot("character");stock.Set(new(1,100,stock.Scope,"packet","v1",true));var recipe=new RecipeDefinition("r",3,1,new(){[1]=10},"metal");
        ledger.Submitted("a",1,recipe,stock);stock.Set(new(1,90,stock.Scope,"packet","v2",true));ledger.Apply(stock);
        ledger.Compact(new HashSet<string>{"a"});Assert.Single(store.Read()!["seen"]!.AsObject());ledger.Submitted("a",1,recipe,stock);Assert.Equal(90,ledger.Apply(stock).Query(1).Quantity);
        ledger.Compact(new HashSet<string>());Assert.Empty(store.Read()!["seen"]!.AsObject());
        ledger.Submitted("pending",1,recipe,stock);ledger.Compact(new HashSet<string>());Assert.Single(store.Read()!["debits"]!.AsObject());Assert.Single(store.Read()!["seen"]!.AsObject());
    }
    [Fact] public void FailedDebitPersistenceRetriesBeforeSavingCraftIntent()
    {
        var ledgerStore=new FailingDebitStore();var ledger=new RuntimeInventory(ledgerStore);var inner=new MemoryStore();
        Assert.Equal(0,ledgerStore.Attempts);Assert.Null(ledgerStore.Read());
        var stock=new InventorySnapshot("character");stock.Set(new(1,100,stock.Scope,"packet","v1",true));
        var recipe=new RecipeDefinition("r",3,1,new(){[1]=10},"metal");var journal=new SubmissionStore(inner,ledger,"a",recipe,()=>stock);
        var intent=new JsonObject{["submitted"]=0,["pending"]=new JsonObject{["kind"]="craft"}};
        Assert.Throws<IOException>(()=>journal.Flush(intent));Assert.Null(inner.Read());Assert.Null(ledgerStore.Read());
        Assert.Throws<IOException>(()=>journal.Flush(intent));Assert.Null(inner.Read());Assert.Null(ledgerStore.Read());
        journal.Flush(intent);Assert.Equal(3,ledgerStore.Attempts);Assert.NotNull(inner.Read()!["pending"]);
        Assert.Single(ledgerStore.Read()!["debits"]!.AsObject());Assert.Equal(90,ledger.Apply(stock).Query(1).Quantity);
    }
    [Fact] public void SameDeviceDifferentRecipesRefillBeforeLeaving()
    {
        var config=Catalog().Configuration;config["recipes"]!["steel"]=config["recipes"]!["iron"]!.DeepClone();config["recipes"]!["steel"]!["output_item_id"]=5L;
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,1,"iron",[]);q.Add("steel","production",5,1,"steel",[]);});
        var ports=new Ports();ports.Set(1,20);var time=new ManualTime();
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time);host.Start();
        for(var i=0;i<24&&ports.Crafts<2;i++)host.Tick();Assert.Equal(2,ports.Crafts);Assert.Equal(0,ports.Leaves);
    }
    [Fact] public void FullDeviceDoesNotRepeatClaimUntilRetryTimeAndOtherDeviceRuns()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,1,"iron",[]);q.Add("wood","production",4,1,"wood",[]);});
        var ports=new Ports{MetalFull=true};ports.Set(1,10);ports.Set(2,10);var time=new ManualTime();
        using var host=Host(queue,ports,time);host.Start();for(var i=0;i<25;i++)host.Tick();
        Assert.Equal(1,ports.Crafts);Assert.Equal(1,ports.IronInstances);Assert.False(queue.Read().Jobs.Single(j=>j.Id=="iron").Pending);
    }
    [Fact] public void MissingBottlesPurchaseIsJournaledThenGatheringReservesWholeBatch()
    {
        var config=Catalog().Configuration;config["item_names"]!["5"]="空瓶";config["item_names"]!["6"]="水";
        config["gathering_routes"]!["water"]=JsonNode.Parse("""{"target_item_id":6,"skill":"daily","node":"水","attempts_per_batch":10}""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("water","gathering",6,10,"water",[]));var ports=new Ports();ports.Set(5,3);ports.Gather.Frame=ports.Gather.Frame with{RouteKey="water"};
        ports.Consumables=new(new Dictionary<string,GatheringConsumableRule>{["water"]=new(5,"空瓶")},()=>ports.Stock,()=>new());
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();
        for(var i=0;i<20&&ports.Gather.Starts==0;i++)host.Tick();
        Assert.True(ports.Purchases==1,"purchases="+ports.Purchases+" "+host.Status.Message);Assert.True(ports.Gather.Starts==1,"starts="+ports.Gather.Starts+" "+host.Status.Message+" "+string.Join(";",host.Status.Issues.Values));Assert.True(ports.PurchaseWasDurable);
        var saved=JsonNode.Parse(File.ReadAllText(Path.Combine(directory,"gathering-reservations.json")))!;
        Assert.Equal(10,saved["reservations"]![0]!["quantity"]!.GetValue<long>());
    }
    [Fact] public void BottlesInStorageAreWithdrawnBeforeAnyPurchase()
    {
        var config=Catalog().Configuration;config["item_names"]!["5"]="空瓶";config["item_names"]!["6"]="水";
        config["gathering_routes"]!["water"]=JsonNode.Parse("""{"target_item_id":6,"skill":"daily","node":"水","attempts_per_batch":10}""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("water","gathering",6,1,"water",[]));var ports=new Ports();ports.Set(5,3);ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[5]=20});
        ports.Gather.Frame=ports.Gather.Frame with{RouteKey="water"};ports.Consumables=new(new Dictionary<string,GatheringConsumableRule>{["water"]=new(5,"空瓶")},()=>ports.Stock,()=>new());
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();
        for(var i=0;i<12;i++)Assert.Null(host.Tick().Fault);
        Assert.Equal(0,ports.Purchases);var state=new JsonWorkflowStore(Path.Combine(directory,"storage-supply.json")).Read()!;
        Assert.Equal(5,state["plans"]![0]!["item_id"]!.GetValue<long>());Assert.Equal(7,state["plans"]![0]!["quantity"]!.GetValue<long>());
    }
    [Fact] public void MissingManualPurchaseMaterialDoesNotBuyOrBlockIndependentProduction()
    {
        var config=Catalog().Configuration;config["item_names"]!["1"]="蜂蠟";config["gathering_routes"]!.AsObject().Clear();
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,1,"iron",[]);q.Add("wood","production",4,1,"wood",[]);});
        var ports=new Ports();ports.Set(1,0);ports.Set(2,10);using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();
        for(var i=0;i<15;i++)Assert.Null(host.Tick().Fault);
        Assert.Equal(0,ports.Purchases);Assert.True(ports.Crafts>0);Assert.Contains(host.Status.Issues.Values,s=>s.Contains("蜂蠟材料不足"));
    }
    [Fact] public void PaperRecipeRequestsPurchaseOnlyAfterKnownEmptyStorage()
    {
        var config=Catalog().Configuration;config["item_names"]!["5"]="紙";
        config["recipes"]!["iron"]!["materials"]=JsonNode.Parse("""[{"item_id":5,"quantity":10}]""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("paper-product","production",3,1,"iron",[]));var ports=new Ports{BulkSync=true};ports.Set(5,3);
        ports.Stock.SetStorage("character",new Dictionary<long,long?>{[5]=0});ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[5]=0});
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();
        for(var i=0;i<20&&ports.Purchases==0;i++)Assert.Null(host.Tick().Fault);
        Assert.Equal(1,ports.Purchases);Assert.True(ports.PurchaseWasDurable);
    }
    [Fact] public void PurchaseRestartKeepsIntentAndNeverPaysTwice()
    {
        var config=Catalog().Configuration;config["item_names"]!["5"]="空瓶";config["item_names"]!["6"]="水";
        config["gathering_routes"]!["water"]=JsonNode.Parse("""{"target_item_id":6,"skill":"daily","node":"水","attempts_per_batch":10}""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("water","gathering",6,10,"water",[]));var ports=new Ports{PurchaseVisible=false};ports.Set(5,3);ports.Gather.Frame=ports.Gather.Frame with{RouteKey="water"};
        ports.Consumables=new(new Dictionary<string,GatheringConsumableRule>{["water"]=new(5,"空瓶")},()=>ports.Stock,()=>new());
        using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime()))
        {host.Start();for(var i=0;i<10&&ports.Purchases==0;i++)host.Tick();Assert.Equal(1,ports.Purchases);}
        using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime()))
        {host.Start();for(var i=0;i<4;i++)host.Tick();Assert.Equal(1,ports.Purchases);Assert.Equal(0,ports.Gather.Starts);Assert.False(host.SafeToSwitch);}
    }
    [Fact] public void AuxiliaryNeverRunsAcrossNativeBatchAndSurplusReceivesQueueReserve()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("ore","gathering",1,10,"ore",[]));var ports=new Ports();ports.Set(1,0);var time=new ManualTime();
        using var host=Host(queue,ports,time);host.Start();host.Tick();host.Tick();var count=ports.AuxiliaryCalls;
        ports.Gather.Frame=ports.Gather.Frame with{TaskId=JsonValue.Create(1),TaskStatus="active"};for(var i=0;i<5;i++)host.Tick();
        Assert.Equal(count,ports.AuxiliaryCalls);Assert.NotNull(ports.Reserve);
    }
    [Fact] public void NativeBottleReservationSurvivesHostAndConsumablesRestart()
    {
        var config=Catalog().Configuration;config["item_names"]!["5"]="空瓶";config["item_names"]!["6"]="水";
        config["gathering_routes"]!["water"]=JsonNode.Parse("""{"target_item_id":6,"skill":"daily","node":"水","attempts_per_batch":10}""");
        var rules=new Dictionary<string,GatheringConsumableRule>{["water"]=new(5,"空瓶")};var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("water","gathering",6,10,"water",[]));
        var ports=new Ports();ports.Set(5,10);ports.Gather.Frame=ports.Gather.Frame with{RouteKey="water"};ports.Consumables=new(rules,()=>ports.Stock,()=>new());
        using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime()))
        {host.Start();host.Tick();host.Tick();Assert.Equal(1,ports.Gather.Starts);}
        ports.Consumables=new(rules,()=>ports.Stock,()=>new());
        using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime()))
        {
            Assert.Single(ports.Consumables.Reservations);host.Start();host.Tick();Assert.Equal(1,ports.Gather.Starts);
            Assert.False(ports.Consumables.ReserveForBatch(new("water",6,"daily","水"),"another").Ready);
        }
    }
    [Fact] public void LiveMaintenanceSettingsEnableRepairWithoutReconstructingHost()
    {
        var queue=new RuntimeQueue(directory);var ports=new Ports();var time=new ManualTime();var policy=new MaintenancePolicy(RepairEnabled:false,BackpackEnabled:false,IntervalSeconds:1);
        using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>policy,time);host.Start();time.Seconds+=2;host.Tick();Assert.Empty(ports.Maintain.Actions);
        ports.Maintain.Frame=new("2","repair_quote",Gold:100000,Quote:1000,Currency:"gold",ConfirmAvailable:true);
        policy=policy with{RepairEnabled=true};for(var i=0;i<4;i++)host.Tick();Assert.NotEmpty(ports.Maintain.Actions);
    }
    [Fact] public void ProductionReliefExhaustionInvokesSurplusThenProtectedTidy()
    {
        var queue=new RuntimeQueue(directory);var ports=new Ports{Sale=new()};ports.Maintain.Frame=new("1","backpack",Weight:95,Capacity:100);
        using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:true),new ManualTime());host.Start();
        for(var i=0;i<5;i++)host.Tick();Assert.Equal(1,ports.Sale.Begins);Assert.True(ports.Sale.Ticks>0);
        ports.Maintain.Frame=new("2","tidy",BoldSelected:true,MaterialsSelected:true,TidyCount:2);host.Tick();Assert.Contains("disable_bold",ports.Maintain.Actions);
    }
    [Fact] public void RetiredUnsubmittedClaimReleasesOnlyWithVerifiedEmptyJournal()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));
        var batch=queue.Claim(queue.Read().Jobs[0],"old-scope","production","device:metal",new JsonObject{["rounds"]=1,["output_per_round"]=1,["recipe"]=new JsonObject{["key"]="iron",["device"]="metal",["duration_s"]=60}});
        JsonObject Job()=>queue.Read().ToJson()["jobs"]![0]!.AsObject();
        Assert.False(queue.ReleaseRetiredUnsubmitted(Job(),_=>true));
        var file=Path.Combine(queue.BatchDirectory("old-scope","iron",batch["action_id"]!.GetValue<string>()),"production.json");
        var journal=new JsonObject{["schema_version"]=1,["recipe"]=batch["recipe"]!.DeepClone(),["count"]=1,["submitted"]=0,["completed"]=0,["owned_slots"]=new JsonArray(),["pending"]=null,["status"]="paused"};
        AtomicJson.Write(file,journal);Assert.False(queue.ReleaseRetiredUnsubmitted(Job(),_=>false));
        journal["pending"]=new JsonObject{["kind"]="craft"};AtomicJson.Write(file,journal);Assert.False(queue.ReleaseRetiredUnsubmitted(Job(),_=>true));
        journal["pending"]=null;journal["submitted"]=1;AtomicJson.Write(file,journal);Assert.False(queue.ReleaseRetiredUnsubmitted(Job(),_=>true));
        journal["submitted"]=0;AtomicJson.Write(file,journal);Assert.True(queue.ReleaseRetiredUnsubmitted(Job(),_=>true));Assert.False(queue.Read().Jobs[0].Pending);
        Assert.True(File.Exists(file));
    }
    [Fact] public void RetiredFishingReleasesOnlyVerifiedUnstartedClaim()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("fish","idle_fishing",1,60,"lake",[]));
        var batch=queue.Claim(queue.Read().Jobs[0],"old-scope","idle_fishing","character:idle_fishing",new JsonObject{["point_key"]="lake",["duration_s"]=60});
        JsonObject Job()=>queue.Read().ToJson()["jobs"]![0]!.AsObject();
        var folder=queue.BatchDirectory("old-scope","fish",batch["action_id"]!.GetValue<string>());var store=new JsonWorkflowStore(Path.Combine(folder,"idle-fishing.json"));
        var runner=new IdleFishingRunner(new("lake",60),new FishingPort(),store);var state=runner.Snapshot();
        foreach(var field in new[]{"round_seen","navigation_seen"}){var changed=(JsonObject)state.DeepClone();changed[field]=true;store.Flush(changed);Assert.False(queue.ReleaseRetiredUnsubmitted(Job(),_=>true));}
        var pending=(JsonObject)state.DeepClone();pending["pending"]=new JsonObject{["kind"]="start"};store.Flush(pending);Assert.False(queue.ReleaseRetiredUnsubmitted(Job(),_=>true));
        store.Flush(state);Assert.False(queue.ReleaseRetiredUnsubmitted(Job(),_=>false));Assert.True(queue.ReleaseRetiredUnsubmitted(Job(),_=>true));Assert.False(queue.Read().Jobs[0].Pending);
    }
    [Fact] public void KnownPacketInventoryNeverStartsRedundantScreenRead()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));var ports=new Ports();ports.Set(1,10);
        using var host=Host(queue,ports,new ManualTime());host.Start();for(var i=0;i<15;i++)host.Tick();Assert.Equal(1,ports.Crafts);
    }
    [Fact] public void ProvenUnsubmittedReleaseDropsOnlyItsOwnLedgerProjection()
    {
        var store=new MemoryStore();var ledger=new RuntimeInventory(store);var stock=new InventorySnapshot("old");stock.Set(new(1,100,"old","packet","v1",true));
        var recipe=new RecipeDefinition("r",3,1,new(){[1]=10},"metal");ledger.Submitted("unsubmitted",1,recipe,stock);ledger.Submitted("unsubmitted-other",1,recipe,stock);
        ledger.ReleaseUnsubmitted("unsubmitted");Assert.Equal(90,ledger.Apply(stock).Query(1).Quantity);Assert.Single(store.Read()!["debits"]!.AsObject());Assert.Single(store.Read()!["seen"]!.AsObject());
    }
    [Fact] public void FullDeviceCollectsPeerRecipesAndRefillsRemainingWithoutExtraTrip()
    {
        var config=Catalog().Configuration;config["recipes"]!["steel"]=config["recipes"]!["iron"]!.DeepClone();config["recipes"]!["steel"]!["output_item_id"]=5L;
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,4,"iron",[]);q.Add("steel","production",5,4,"steel",[]);});var ports=new Ports();ports.Set(1,80);var time=new ManualTime();
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time);host.Start();
        for(var i=0;i<65;i++)host.Tick();Assert.Equal(5,ports.Crafts);time.Seconds=1000;ports.Ready=true;
        for(var i=0;i<20&&ports.Collections==0;i++)host.Tick();Assert.Equal(1,ports.Collections);var leaves=ports.Leaves;
        for(var i=0;i<50&&ports.Crafts<8;i++)host.Tick();Assert.Equal(8,ports.Crafts);Assert.True(leaves==ports.Leaves,string.Join(";",ports.Navigation));
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
    private sealed class FailingDebitStore:IWorkflowStore
    {
        private JsonObject? saved;public int Attempts;
        public JsonObject? Read()=>saved?.DeepClone().AsObject();
        public void Flush(JsonObject state){if(++Attempts<=2)throw new IOException("test disk write failure");saved=state.DeepClone().AsObject();}
    }
    [Fact] public void ReadSharingViolationRetriesThenRecoversWithoutInputReplay()
    {
        var queue=new RuntimeQueue(directory);var ports=new Ports();var time=new ManualTime();using var host=Host(queue,ports,time);host.Start();ports.ReadFailures=2;
        var first=host.Tick();Assert.True(first.Running,first.Fault??first.Message);Assert.Equal(1,first.RecoveryAttempt);Assert.Contains("sharing",first.Fault);Assert.Equal(0,ports.Crafts);
        time.Seconds=101;Assert.Equal(2,host.Tick().RecoveryAttempt);time.Seconds=102;Assert.True(host.Tick().Running);Assert.Equal(0,host.Status.RecoveryAttempt);
    }
    [Fact] public void PersistentReadFailureKeepsWorkflowAvailableAndManualPauseStaysPaused()
    {
        var queue=new RuntimeQueue(directory);var ports=new Ports();var time=new ManualTime();using var host=Host(queue,ports,time);host.Start();ports.ReadFailures=20;
        for(var n=0;n<4;n++){time.Seconds=100+n*2;host.Tick();}
        Assert.True(host.Status.Running);Assert.NotNull(host.Status.Fault);
        ports.ReadFailures=0;time.Seconds=110;Assert.True(host.Tick().Running);
        host.Continue();host.Pause();time.Seconds=200;Assert.False(host.Tick().Running);Assert.Equal("manual",host.Status.PauseReason);
    }
    [Fact] public void SharingViolationAfterInputBoundaryKeepsWorkflowAvailableWithoutReplayingInput()
    {
        var queue=new RuntimeQueue(directory);var ports=new Ports();using var host=Host(queue,ports,new ManualTime());host.Start();ports.FailBeforeTick=true;
        var state=host.Tick();Assert.True(state.Running);Assert.Equal(0,state.RecoveryAttempt);Assert.NotNull(state.Fault);
        ports.FailBeforeTick=false;Assert.True(host.Tick().Running);Assert.Equal(0,ports.Crafts);
    }
    private sealed class Ports:IAutomationPorts
    {
        public bool BulkSync;public int SyncRequests;public bool PreferBulkInventorySync=>BulkSync;public void RequestInventorySync()=>SyncRequests++;
        public string Scope=>"character";public string? CurrentDevice{get;private set;}public InventorySnapshot Stock=new("character");public int Crafts,Leaves,IronInstances,Purchases,AuxiliaryCalls,Collections;public bool Ready,MetalFull,PurchaseWasDurable,PurchaseVisible=true;
        public GatheringConsumables? Consumables;public IGatheringConsumables? GatheringConsumables=>Consumables;public Func<long,long?>? Reserve;
        public readonly MaintenancePort Maintain=new(){Frame=new("1","scene")};
        public readonly List<string> Navigation=[];
        public SurplusAdapter? Sale;
        public HashSet<long> ExplicitSurplus=[];public bool IsExplicitSurplusMaterial(long item)=>ExplicitSurplus.Contains(item);
        public DecompositionAdapter? Conversion;public IDecompositionPort? Decomposition(string batch,IWorkflowStore store)=>Conversion;
        public sealed class DecompositionAdapter:IDecompositionPort
        {
            public int Requests,Ticks;private bool running;
            public JsonObject Snapshot()=>new(){["status"]=running?"running":"idle"};
            public bool Request(long quantity){Assert.Equal(1,quantity);Requests++;running=true;return true;}
            public string Tick(){Ticks++;return "running";}public void ObserveOnly(){}public bool ReleaseNavigation()=>true;
        }
        public ISurplusMaintenancePort? Surplus(string batch,IWorkflowStore store,Func<long,long?> reserve){Reserve=reserve;return Sale;}
        public bool FailBeforeTick,FailIronNavigation;
        public bool NavigationOffer;public int NavigationAuxiliaryCalls;
        public bool BeforeNavigationTick(){NavigationAuxiliaryCalls++;return NavigationOffer;}
        public bool BeforeTick(bool safe){if(FailBeforeTick)throw new IOException("sharing after boundary",unchecked((int)0x80070020));Assert.True(safe);AuxiliaryCalls++;return false;}
        public IPurchasePort? Purchase(GatheringSupplyRequest request,string batch,Func<PurchaseState?> persisted)=>new PurchaseAdapter(this,persisted);
        public readonly GatheringPort Gather=new();public readonly StoragePort Store=new(){Frame=new(100,"1","storage",Scope:"shared",ItemId:1,Name:"ore",Available:10,BagQuantity:0,BagScope:"character",TransferAvailable:true,BalanceSource:"packet_capture",BalanceAt:100)};
        private int version;private string page="scene",recipe="";private readonly Dictionary<string,int> occupied=[];
        public void Set(long item,long quantity)=>Stock.Set(new(item,quantity,Scope,"packet","v"+(++version),true));
        public int ReadFailures;public string? PauseReason;
        public int Starts,Continues;public void Start()=>Starts++;public void Continue()=>Continues++;public void Pause(string reason){PauseReason=reason;}public void Dispose(){}
        public InventorySnapshot Inventory(){if(ReadFailures-->0)throw new IOException("sharing violation",unchecked((int)0x80070020));return Stock;}
        public IProductionPort Production(string key,string batch,IWorkflowStore store){if(key=="iron")IronInstances++;return new ProductionAdapter(this,key);}
        public IGatheringPort Gathering(string route,string batch,IWorkflowStore store)=>Gather;
        public IStorageSupplyPort Storage(WithdrawalPlan plan,string batch,IWorkflowStore store)=>Store;
        public readonly FishingPort Fish=new();public IFishingPort Fishing(string point,string batch,IWorkflowStore store)=>Fish;
        public IMaintenancePort Maintenance(string batch,IWorkflowStore store)=>Maintain;
        private sealed class PurchaseAdapter(Ports host,Func<PurchaseState?> persisted):IPurchasePort
        {
            private string page="scene";private long quantity=7;
            public ShopObservation Observe()=>new(true,page,true,true,host.Purchases>0&&host.PurchaseVisible?10:3,host.Purchases>0&&host.PurchaseVisible?49993:50000,quantity,7);
            public void NavigateToShop()=>page="shop";
            public void PrepareQuantity(long n){quantity=n;page="purchase_confirm";}
            public void ConfirmPurchase(){host.PurchaseWasDurable=persisted()?.Intent is not null;host.Purchases++;if(host.PurchaseVisible)host.Set(5,10);}
            public void ExitShop()=>page="scene";
        }
        public sealed class SurplusAdapter:ISurplusMaintenancePort
        {
            public int Begins,Ticks;public string Begin(){Begins++;return "running";}public string Tick(){Ticks++;return "complete";}
            public string Reconcile()=>"complete";public JsonObject Snapshot()=>new(){["status"]=Ticks>0?"complete":"idle"};
        }
        private sealed class ProductionAdapter(Ports host,string key):IProductionPort
        {
            public bool CanReleaseForeground()=>host.CurrentDevice is null;
            private string Device=>key=="wood"?"wood":"metal";public bool RemoteDevicePreview=>false;
            public ProductionFrame Observe()=>new(host.page,EmptySlots:host.MetalFull&&Device=="metal"?[]:Enumerable.Range(host.occupied.GetValueOrDefault(Device),5-host.occupied.GetValueOrDefault(Device)).ToArray(),SlotCount:5,RecipeKey:host.recipe,CraftAvailable:true,CollectAvailable:host.Ready,ReadySlots:host.Ready?Enumerable.Range(0,host.occupied.GetValueOrDefault(Device)).ToArray():[],AtDevice:host.CurrentDevice==Device);
            public void Navigate(string destination,ProductionRecipe recipe)
            {if(host.FailIronNavigation&&key=="iron")throw new InvalidOperationException("iron target unavailable");host.Navigation.Add(key+":"+destination+":crafts="+host.Crafts+":collects="+host.Collections);if(destination=="leave"){host.Leaves++;host.page="scene";host.CurrentDevice=null;}else{host.CurrentDevice=Device;host.page=destination=="recipe"?"recipe_detail":Device+"_panel";host.recipe=key;}}
            public void Click(string kind,ProductionRecipe recipe)
            {if(kind=="craft"){host.Crafts++;host.occupied[Device]=host.occupied.GetValueOrDefault(Device)+1;}else{host.Collections++;host.occupied[Device]=0;}host.page=Device+"_panel";}
            public void Pause(string reason){}public void Resume(){}
        }
    }
}

