using System.Text.Json;
using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Workflows.Tests;

public sealed class HostLifecycleTests : IDisposable
{
    [Theory][InlineData(false)][InlineData(true)]
    public void UnrestoredBatchRequestsInventoryOnceBeforeReturningToIndependentWork(bool activeFishing)
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));var ports=new Ports();ports.Set(1,1000);
        using(var original=Host(queue,ports,new ManualTime())){original.Start();for(var n=0;n<12&&ports.Crafts==0;n++)original.Tick();Assert.Equal(1,ports.Crafts);}
        queue.Edit(q=>q.Add("fish","idle_fishing",1,600,FishingPlan.CurrentLocation,[]));
        ports.RuntimeScope="process-without-owner";ports.Stock=new(ports.Scope);
        if(activeFishing)
        {
            using var fishing=Host(queue,ports,new ManualTime());fishing.Start();fishing.Tick();
            ports.Fish.Frame=new(1,"button","fishing",FishingButton:true);fishing.Tick();
            ports.Fish.Frame=new(2,"cast","fishing",FishingButton:true);fishing.Tick();
            ports.Fish.Frame=new(3,"hook","fishing",RoundActive:true,LeisureMode:false,Gauge:.6);fishing.Tick();
            Assert.False(fishing.SafeToSwitch);Assert.Equal(0,ports.SyncRequests);
        }
        ports.BulkSync=true;ports.Fish.Frame=ports.Fish.Frame with{CaptureId="resume"};
        using var host=Host(queue,ports,new ManualTime());host.Start();host.Tick();
        for(var n=0;activeFishing&&n<3&&ports.SyncRequests==0;n++)
        {ports.Fish.Frame=ports.Fish.Frame with{At=4+n,CaptureId="boundary-"+n};host.Tick();}
        Assert.True(ports.SyncRequests==1,JsonSerializer.Serialize(host.Status));Assert.DoesNotContain("iron",host.Status.Jobs.Keys);Assert.Equal(1,ports.Crafts);
        host.SuspendForInventorySync();host.FinishInventorySyncWithoutCoverage("waiting for packet");
        for(var n=0;n<5;n++)host.Tick();Assert.Equal(1,ports.SyncRequests);Assert.Equal(1,ports.Crafts);
        ports.RuntimeScope="character";ports.Stock=new(ports.Scope);ports.Set(1,1000);ports.Set(3,0);host.ContinueAfterInventorySync();
        Assert.Contains("iron",host.Status.Jobs.Keys);Assert.Equal(1,ports.Crafts);Assert.Equal(1,ports.SyncRequests);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void CaptureIdentityArrivingAfterHostConstructionRestoresPendingBatch(bool automatic)
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));var ports=new Ports();ports.Set(1,10);
        using(var original=Host(queue,ports,new ManualTime())){original.Start();for(var n=0;n<12&&ports.Crafts==0;n++)original.Tick();Assert.Equal(1,ports.Crafts);}
        ports.RuntimeScope="process-without-owner";
        using var host=Host(queue,ports,new ManualTime());Assert.Empty(host.Status.Jobs);
        ports.RuntimeScope="character";
        if(automatic)host.ContinueAfterInventorySync();else host.Continue();
        Assert.Contains("iron",host.Status.Jobs.Keys);Assert.False(host.SafeToSwitch);
        Assert.DoesNotContain("上次任务的操作结果待确认",host.Status.Issues.GetValueOrDefault("iron")??"");Assert.Equal(1,ports.Crafts);
        host.ContinueAfterInventorySync();Assert.Single(host.Status.Jobs);Assert.Equal(1,ports.Crafts);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void IndependentMissingSourceDoesNotBlockAuthorizedSurplusReserve(bool unknown)
    {
        var config=Catalog().Configuration;config["recipes"]!["iron"]!["materials"]=JsonNode.Parse("""[{"item_id":1,"quantity":10},{"item_id":9,"quantity":1}]""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));
        var ports=new Ports{Sale=new()};ports.ExplicitSurplus.UnionWith([1,88]);ports.Set(1,30);ports.Set(88,50);
        if(unknown)ports.Stock.Set(new(9,null,ports.Scope,"unknown","",false));else ports.Set(9,0);
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false),new ManualTime());
        host.Start();host.Tick();Assert.NotNull(ports.Reserve);
        Assert.Equal(10,ports.Reserve!(1));Assert.Equal(0,ports.Reserve(88));Assert.Null(ports.Reserve(9));
        ports.ExplicitSurplus.Add(9);if(unknown)Assert.Null(ports.Reserve(9));
    }
    [Theory][InlineData(FishingPlan.CurrentLocation)][InlineData("lake")]
    public void CompletedFishingAutomaticallyStopsAndKeepsTheResult(string point)
    {
        var config=Catalog().Configuration;config["gathering_routes"]!["lake"]=JsonNode.Parse("""{"target_item_id":1,"skill":"fishing","node":"lake","attempts_per_batch":1}""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("fish","idle_fishing",1,2,point,[]));
        var ports=new Ports();var time=new ManualTime();using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time);
        host.Start();host.Tick();host.Tick();
        if(point!="current_location")
        {ports.Fish.Frame=new(1,"travel",NavigationActive:true);host.Tick();ports.Fish.Frame=new(2,"arrived",NavigationActive:false);host.Tick();}
        ports.Fish.Frame=new(3,"cast","fishing",FishingButton:true);host.Tick();
        ports.Fish.Frame=new(4,"hook","fishing",RoundActive:true,LeisureMode:false,Gauge:.6);host.Tick();
        ports.Fish.Frame=new(6,"finished","fishing",RoundActive:true,LeisureMode:false,Gauge:.4);host.Tick();
        Assert.False(host.Status.Running,host.Status.ToString());Assert.Contains("已自动停止",host.Status.Message);
        Assert.Equal("queue_completed",ports.PauseReason);Assert.Equal(2,queue.Read().Jobs[0].Completed);Assert.False(queue.Read().Jobs[0].Pending);
    }
    [Fact] public void CancelledQueueStopsButPausedIncompleteQueueKeepsWaiting()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,1,"iron",[]);q.Control("iron","pause");});
        var ports=new Ports();using var host=Host(queue,ports,new ManualTime());host.Start();host.Tick();Assert.True(host.Status.Running);
        queue.Edit(q=>q.Control("iron","cancel"));host.Tick();Assert.False(host.Status.Running,host.Status+"; "+queue.Read().ToJson()+"; "+File.ReadAllText(Path.Combine(directory,"maintenance.json")));Assert.Contains("已自动停止",host.Status.Message);
    }
    [Fact] public void CompletedQueueWaitsForUnresolvedMaintenanceConsumption()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,1,"iron",[]);q.Control("iron","cancel");});
        var ports=new Ports();var time=new ManualTime();var policy=new MaintenancePolicy();var store=new JsonWorkflowStore(Path.Combine(directory,"maintenance.json"));
        var state=new MaintenanceRunner(ports.Maintain,store,policy,time).Snapshot();
        state["pending"]=new JsonObject{["kind"]="repair",["capture_id"]="before",["deadline"]=200d};state["active"]="repair";store.Flush(state);
        using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>policy,time);host.Start();host.Tick();
        Assert.True(host.Status.Running);Assert.NotNull(store.Read()!["pending"]);
    }
    [Fact] public void ReadyProductionWaitsForBackpackInspectionAndItsExit()
    {
        var queue=new RuntimeQueue(directory);var ports=new Ports();var time=new ManualTime();
        using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>new(RepairEnabled:false,BackpackCheckIntervalSeconds:120),time);
        host.Start();time.Seconds+=121;host.Tick();
        ports.Set(1,10);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));
        for(var i=0;i<4;i++)host.Tick();
        Assert.Empty(ports.Navigation);Assert.Equal(0,ports.Crafts);
        ports.Maintain.Frame=new("bag","backpack",Weight:50,Capacity:100);host.Tick();host.Tick();
        Assert.Empty(ports.Navigation);Assert.Equal(0,ports.Crafts);
        ports.Maintain.Frame=new("closed","scene");
        for(var i=0;i<12&&ports.Crafts==0;i++)host.Tick();
        Assert.Equal(1,ports.Crafts);Assert.Null(host.Status.Fault);
    }
    [Fact] public void BackpackRecognitionAndExitFailureDoNotStarveIndependentProduction()
    {
        var queue=new RuntimeQueue(directory);var ports=new Ports();var time=new ManualTime();
        using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>new(RepairEnabled:false,BackpackCheckIntervalSeconds:120),time);
        host.Start();time.Seconds+=121;host.Tick();
        ports.Maintain.Frame=new("missing","unknown",Stable:false);host.Tick();time.Seconds+=121;host.Tick();
        host.Tick();time.Seconds+=16;host.Tick();
        ports.Set(1,10);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));
        for(var i=0;i<12&&ports.Crafts==0;i++)host.Tick();
        Assert.True(host.Status.Running,host.Status.Fault);Assert.True(ports.Crafts==1,host.Status.ToString());
        Assert.Empty(ports.Maintain.Actions);
    }
    [Fact] public void ResumingBlockedStorageDoesNotDeadlockProductionAlreadyUsingCharacter()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("wood","production",4,1,"wood",[]));
        var ports=new Ports();ports.Set(2,0);ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[2]=10});
        ports.Store.Frame=new(100,"locate-1","storage",Scope:"shared",LocateAvailable:true,LocateLimit:1);
        var time=new ManualTime();using var host=Host(queue,ports,time);host.Start();host.Tick();host.Tick();
        ports.Store.Frame=ports.Store.Frame with{At=101,CaptureId="locate-2"};time.Seconds=101;host.Tick();
        var storage=new JsonWorkflowStore(Path.Combine(directory,"storage-supply.json"));
        Assert.Equal("blocked",storage.Read()!["status"]!.GetValue<string>());Assert.Null(storage.Read()!["pending"]);
        ports.Set(1,10);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));
        for(var i=0;i<10&&ports.CurrentDevice!="metal";i++)host.Tick();
        Assert.Equal("metal",ports.CurrentDevice);Assert.Equal(0,ports.Crafts);
        host.Pause();host.Continue();var actions=ports.Store.Actions.Count;
        host.Tick();Assert.Equal(actions,ports.Store.Actions.Count);
        for(var i=0;i<6&&ports.Crafts==0;i++)host.Tick();
        Assert.Equal(1,ports.Crafts);Assert.Null(host.Status.Fault);
    }
    [Fact] public void UnverifiedStorageExitStopsParkingUnrelatedProduction()
    {
        // Live evidence: the storage panel close never confirmed, so the kept transfer
        // intent left SafeToSwitch false and every other task stopped with it.
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));
        var ports=new Ports();ports.Set(1,10);ports.Store.ReleaseSucceeds=false;ports.Store.LeaveAllowed=true;
        ports.Store.Frame=ports.Store.Frame with{At=200,CaptureId="after-restart"};
        var store=new JsonWorkflowStore(Path.Combine(directory,"storage-supply.json"));
        var seedPort=new StoragePort{Frame=new(100,"dialog","storage",Scope:"shared",ItemId:1,Name:"ore",TransferDialog:true,DialogQuantity:3,ConfirmAvailable:true)};
        new StorageSupplyRunner([new(1,"ore",3,"shared")],seedPort,store).Tick();
        var state=store.Read()!;
        Assert.Equal("confirm_transfer",state["pending"]!["kind"]!.GetValue<string>());
        state["runtime_scope"]="character";state["runtime_action"]="withdrawal:seed";state["pending"]!["deadline"]=101d;store.Flush(state);
        var time=new ManualTime();using var host=Host(queue,ports,time);host.Start();
        for(var i=0;i<12&&ports.Crafts==0;i++){time.Seconds+=2;host.Tick();}
        Assert.True(ports.Crafts>0,string.Join(";",ports.Navigation));
        Assert.Null(host.Status.Fault);
        // The exit was tried a bounded number of times, not once per tick forever.
        Assert.True(ports.Store.Releases<=StorageSupplyRunner.MaximumReleaseAttempts+1,ports.Store.Releases.ToString());
        Assert.Equal("release_attempts_exhausted",store.Read()!["release_reason"]!.GetValue<string>());
    }
    [Theory][InlineData(420)][InlineData(4200)]
    public void FinishedUnknownSlotsAreCollectedBeforeIndependentGatheringNavigates(double checkAt)
    {
        var queue=new RuntimeQueue(directory);
        queue.Edit(q=>{q.Add("iron","production",3,1,"iron",[]);q.Add("ore","gathering",1,1,"ore",[]);});
        var ports=new Ports{PlayerReady=true};ports.Set(1,10);var time=new ManualTime();
        using var host=Host(queue,ports,time);host.Start();
        for(var n=0;n<12&&ports.Crafts==0;n++)host.Tick();
        Assert.Equal(1,ports.Crafts);
        // Leave the device first: the 300s trip interval starts there, not at the
        // moment this test decides to move the clock.
        for(var n=0;n<4;n++)host.Tick();
        // Come due while only the player's finished slot is ready. The gathering job
        // is not at its node yet, so it has to navigate once it gets the turn.
        ports.Set(1,0);ports.Gather.Frame=ports.Gather.Frame with{RouteKey="scene"};time.Seconds=checkAt;
        for(var n=0;n<8;n++)host.Tick();
        var file=Directory.GetFiles(directory,"production.json",SearchOption.AllDirectories).Single();
        var state=JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.Null(state["reason"]);Assert.Equal(1,ports.Collections);
        Assert.Equal(1,queue.Read().Jobs.Single(j=>j.Id=="iron").Completed);
        var ironMoves=ports.Navigation.Count(x=>x.StartsWith("iron:",StringComparison.Ordinal));
        for(var n=0;n<10&&ports.Gather.Navigations==0;n++)host.Tick();
        Assert.True(ports.Gather.Navigations>0,string.Join(";",ports.Navigation));
        ports.Gather.Frame=ports.Gather.Frame with{RouteKey="ore"};
        for(var n=0;n<10&&ports.Gather.Starts==0;n++)host.Tick();
        Assert.True(ports.Gather.Starts>0,string.Join(";",ports.Navigation));
        Assert.Equal(ironMoves,ports.Navigation.Count(x=>x.StartsWith("iron:",StringComparison.Ordinal)));
        Assert.True(host.Status.Running);Assert.Null(host.Status.Fault);
    }
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
    [Fact] public void InventoryRefreshCompletionCannotOverrideManualPause()
    {
        var queue=new RuntimeQueue(directory);var ports=new Ports();using var host=Host(queue,ports,new ManualTime());
        host.Start();host.SuspendForInventorySync();host.Pause();
        host.ContinueAfterInventorySync();
        Assert.False(host.Status.Running);Assert.Equal("manual",host.Status.PauseReason);
        host.FinishInventorySyncWithoutCoverage("refresh failed");
        Assert.False(host.Status.Running);Assert.Equal("manual",host.Status.PauseReason);
        host.Continue();Assert.True(host.Status.Running);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void DecompositionRunsOnlyAfterWarehouseAllocationAndHoldsIndependentInput(bool inStorage)
    {
        var config=Catalog().Configuration;config["material_sources"]=new JsonObject{["2"]=new JsonObject{["kind"]="decomposition"}};
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("wood","production",4,1,"wood",[]));
        var ports=new Ports{Conversion=new()};ports.Set(2,0);if(inStorage)ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[2]=10});
        else{ports.Stock.SetStorage("character",new Dictionary<long,long?>{[2]=0});ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[2]=0});}
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
        using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>policy,time);host.Start();time.Seconds=121;host.Tick();
        for(var i=0;i<20&&host.Status.Running;i++)
        {
            ports.Maintain.Frame=ports.Maintain.Frame with{CaptureId=(i+2).ToString(),Page=i<3?"tidy":"backpack",BoldSelected=false,MaterialsSelected=false,TidyCount=0};
            host.Tick();
        }
        Assert.False(host.Status.Running);Assert.Equal("backpack_player_action_required",host.Status.PauseReason);
        Assert.Contains("请处理背包",host.Status.Message);Assert.Empty(ports.Store.Actions);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void NewProductionMaterialsReplaceStaleReliefExhaustion(bool exhausted)
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));
        var ports=new Ports();ports.Set(1,10);var time=new ManualTime();
        var policy=new MaintenancePolicy(RepairEnabled:false,BackpackCheckIntervalSeconds:120);
        var store=new JsonWorkflowStore(Path.Combine(directory,"maintenance.json"));
        var state=new MaintenanceRunner(ports.Maintain,store,policy,time).Snapshot();
        state["backpack"]!["status"]=exhausted?"blocked":"due";state["backpack"]!["phase"]="prepare";
        state["backpack"]!["capacity_blocked"]=true;state["backpack"]!["tidy_checked"]=true;
        state["backpack"]!["cleanup_after_relief"]=true;state["backpack"]!["surplus_checked"]=true;
        state["reason"]=exhausted?"backpack_relief_exhausted":"backpack_cleanup_after_production";store.Flush(state);
        using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>policy,time);host.Start();
        for(var n=0;n<12&&ports.Crafts==0;n++)host.Tick();
        Assert.True(host.Status.Running,host.Status.ToString());Assert.Equal(1,ports.Crafts);
        Assert.NotEqual("backpack_relief_exhausted",store.Read()!["reason"]?.GetValue<string>());
    }
    [Fact] public void ReliefWeightCheckDoesNotFreezeAnUnfinishedProductionBatch()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,3,"iron",[]));
        var ports=new Ports();ports.Set(1,30);var time=new ManualTime();var policy=new MaintenancePolicy(RepairEnabled:false);
        var store=new JsonWorkflowStore(Path.Combine(directory,"maintenance.json"));
        var seed=new MaintenanceRunner(ports.Maintain,store,policy,time).Snapshot();
        seed["backpack"]!["phase"]="relief";seed["backpack"]!["status"]="production_relief";store.Flush(seed);
        using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>policy,time);host.Start();
        for(var n=0;n<35&&ports.Maintain.Navigations==0;n++)host.Tick();
        Assert.Equal(3,ports.Crafts);Assert.True(ports.Maintain.Navigations>0);
        Assert.True(host.Status.Running);Assert.Null(host.Status.Fault);
    }
    [Theory][InlineData(false,false,false)][InlineData(true,false,false)][InlineData(false,true,false)][InlineData(false,true,true)]
    public void FullProductionSlotsWaitForCompletionDuringReliefAndThenCollect(bool inventoryLost,bool exhausted,bool resume)
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,100,"iron",[]));
        var ports=new Ports();ports.Set(1,1000);var time=new ManualTime();
        using(var host=Host(queue,ports,time))
        {host.Start();for(var n=0;n<40;n++)host.Tick();Assert.Equal(5,ports.Crafts);}
        var store=new JsonWorkflowStore(Path.Combine(directory,"maintenance.json"));var saved=store.Read()!;
        saved["backpack"]!["phase"]="relief";saved["backpack"]!["status"]="production_relief";store.Flush(saved);
        if(exhausted)
        {
            saved["reason"]="backpack_relief_exhausted";saved["backpack"]!["phase"]="prepare";saved["backpack"]!["status"]="blocked";
            saved["backpack"]!["tidy_checked"]=true;saved["backpack"]!["surplus_checked"]=true;saved["backpack"]!["cleanup_after_relief"]=true;store.Flush(saved);
        }
        using var restored=new AutomationCoordinator(Catalog(),queue,ports,()=>new(RepairEnabled:false),time);
        if(resume)restored.Continue();else restored.Start();for(var n=0;n<8;n++)restored.Tick();
        Assert.True(restored.Status.Running,restored.Status.ToString());Assert.Contains("等待到期",restored.Status.Message);
        Assert.DoesNotContain("iron",restored.Status.Issues.Keys);
        Assert.Equal(5,ports.Crafts);Assert.Equal(0,ports.Collections);
        if(inventoryLost)
        {
            ports.BulkSync=true;ports.Stock.Set(new(1,null,ports.Scope,"unknown","ambiguous",false,"container ambiguous"));
            restored.Tick();Assert.Equal(1,ports.SyncRequests);Assert.True(restored.Status.Running);
            for(var n=0;n<4;n++)restored.Tick();Assert.Equal(1,ports.SyncRequests);
            ports.Set(1,950);restored.SuspendForInventorySync();restored.ContinueAfterInventorySync();
        }
        ports.Ready=true;time.Seconds=1000;
        for(var n=0;n<15&&ports.Collections==0;n++)restored.Tick();
        Assert.Equal(1,ports.Collections);Assert.True(restored.Status.Running);Assert.Null(restored.Status.Fault);
    }
    [Theory][InlineData(400,"idle_fishing")][InlineData(400,"gathering")]
    public void BusyProductionUsesMaterialMinimumAndAllowsNewIndependentWork(long ore,string addedKind)
    {
        var config=Catalog().Configuration;config["gathering_routes"]!["lake"]=JsonNode.Parse("""{"target_item_id":2,"skill":"fishing","node":"lake","attempts_per_batch":1}""");
        config["gathering_routes"]!["log"]=JsonNode.Parse("""{"target_item_id":2,"skill":"logging","node":"log","attempts_per_batch":10}""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1000,"iron",[]));
        var ports=new Ports();ports.Set(1,ore);var time=new ManualTime();
        using(var original=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time))
        {original.Start();for(var n=0;n<40;n++)original.Tick();Assert.Equal(5,ports.Crafts);Assert.Equal(0,ports.Gather.Starts);}
        var store=new JsonWorkflowStore(Path.Combine(directory,"maintenance.json"));var saved=store.Read()!;
        saved["backpack"]!["phase"]="relief";saved["backpack"]!["status"]="production_relief";
        saved["backpack"]!["weight"]=77764;saved["backpack"]!["capacity"]=81000;saved["backpack"]!["capacity_blocked"]=true;
        store.Flush(saved);
        using var restored=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,WeightThresholdPercent:95),time);
        restored.Start();
        queue.Edit(q=>q.Add("new-task",addedKind,2,60,addedKind=="idle_fishing"?"lake":"log",[]));
        for(var n=0;n<25&&(restored.Status.ActiveJob is null or "iron");n++)restored.Tick();
        Assert.True(restored.Status.Running,restored.Status.ToString());Assert.Null(restored.Status.Fault);
        Assert.Equal("new-task",restored.Status.ActiveJob);Assert.Equal(0,ports.Reserve!(1));
    }
    [Theory][InlineData("gathering")][InlineData("idle_fishing")]
    public void ProtectedTidyHandsOffToSurplusBeforeIndependentWork(string kind)
    {
        var config=Catalog().Configuration;
        config["gathering_routes"]!["lake"]=JsonNode.Parse("""{"target_item_id":2,"skill":"fishing","node":"lake","attempts_per_batch":1}""");
        config["gathering_routes"]!["log"]=JsonNode.Parse("""{"target_item_id":2,"skill":"logging","node":"log","attempts_per_batch":10}""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("independent",kind,2,60,kind=="gathering"?"log":"lake",[]));
        var ports=new Ports{Sale=new()};ports.Set(1,100);ports.Set(2,100);var time=new ManualTime();
        var policy=new MaintenancePolicy(RepairEnabled:false,WeightThresholdPercent:95);
        var store=new JsonWorkflowStore(Path.Combine(directory,"maintenance.json"));
        var seed=new MaintenanceRunner(ports.Maintain,store,policy,time).Snapshot();
        seed["backpack"]!["phase"]="relief";seed["backpack"]!["status"]="production_relief";
        seed["backpack"]!["tidy_checked"]=true;seed["backpack"]!["weight"]=80730;seed["backpack"]!["capacity"]=81000;store.Flush(seed);
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>policy,time);host.Start();
        for(var n=0;n<10&&ports.Sale.Begins==0;n++)host.Tick();
        Assert.Equal(1,ports.Sale.Begins);Assert.Equal(0,ports.Gather.Starts);Assert.True(host.Status.Running);
    }
    [Theory][InlineData(300,349,true)][InlineData(300,350,false)][InlineData(400,400,true)][InlineData(100,400,false)]
    public void BusyProductionReplenishesOnlyBelowConfiguredMaterialMinimum(long minimum,long ore,bool gathers)
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1000,"iron",[]));
        var ports=new Ports{MaterialMinimum=minimum};ports.Set(1,ore);
        using var host=Host(queue,ports,new ManualTime());host.Start();for(var n=0;n<40;n++)host.Tick();
        Assert.Equal(5,ports.Crafts);Assert.Equal(gathers,ports.Gather.Starts>0);Assert.True(host.Status.Running);Assert.Null(host.Status.Fault);
    }
    [Fact] public void UnknownInventoryRefreshesBeforeDeclaringBackpackReliefExhausted()
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));
        var ports=new Ports{BulkSync=true};var time=new ManualTime();var policy=new MaintenancePolicy(RepairEnabled:false);
        var store=new JsonWorkflowStore(Path.Combine(directory,"maintenance.json"));
        var state=new MaintenanceRunner(ports.Maintain,store,policy,time).Snapshot();
        state["backpack"]!["status"]="production_relief";state["backpack"]!["phase"]="relief";
        state["backpack"]!["capacity_blocked"]=true;store.Flush(state);
        using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>policy,time);host.Start();host.Tick();
        Assert.True(host.Status.Running);Assert.Equal(1,ports.SyncRequests);
        Assert.Equal("production_relief",store.Read()!["backpack"]!["status"]!.GetValue<string>());
        ports.Set(1,10);host.SuspendForInventorySync();host.ContinueAfterInventorySync();
        for(var n=0;n<12&&ports.Crafts==0;n++)host.Tick();
        Assert.Equal(1,ports.Crafts);Assert.True(host.Status.Running);
    }
    [Fact] public void WaitingRootBatchLeavesReadyMaterialsForSameRecipeSupplyJob()
    {
        var config=Catalog().Configuration;config["gathering_routes"]=new JsonObject();
        config["recipes"]!["wood"]!["materials"]=JsonNode.Parse("""[{"item_id":3,"quantity":10}]""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,100,"iron",[]);q.Add("wood","production",4,100,"wood",[]);});
        var ports=new Ports();ports.Set(1,20);ports.Set(3,0);var time=new ManualTime();
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time);host.Start();
        for(var n=0;n<35;n++)host.Tick();Assert.Equal(2,ports.Crafts);
        ports.Set(1,100);
        for(var n=0;n<45&&ports.Crafts<5;n++)host.Tick();
        Assert.True(ports.Crafts==5,host.Status+"; "+queue.Read().ToJson()+"; "+string.Join(";",ports.Navigation));Assert.Equal(0,ports.Collections);
        Assert.True(queue.Read().Jobs.Single(j=>j.Id=="iron").Pending);
        Assert.Contains(queue.Read().Jobs,j=>j.Id.StartsWith("auto-material:")&&j.Recipe=="iron"&&j.Pending);
        Assert.True(host.Status.Running,host.Status.ToString());
    }
    [Fact] public void LongTermSharedMaterialAllocationDoesNotHideReadyIndependentRecipe()
    {
        var config=Catalog().Configuration;
        config["recipes"]!["wood"]!["materials"]=JsonNode.Parse("""[{"item_id":1,"quantity":10},{"item_id":2,"quantity":10}]""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,100,"iron",[]);q.Add("wood","production",4,1,"wood",[]);q.Control("iron","pause");});
        var ports=new Ports();ports.Set(1,20);ports.Set(2,10);var time=new ManualTime();
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time);
        host.Start();queue.Edit(q=>q.Control("iron","continue"));ports.MetalFull=true;
        for(var n=0;n<30&&ports.Crafts==0;n++)host.Tick();
        Assert.Equal(1,ports.Crafts);Assert.Contains(ports.Navigation,n=>n.StartsWith("wood:recipe",StringComparison.Ordinal));
        Assert.True(host.Status.Running);Assert.Null(host.Status.Fault);
    }
    [Fact] public void ManualWellResumesWhenLaterMapPacketSuppliesBottlesWithoutRestart()
    {
        var config=Catalog().Configuration;config["item_names"]!["5"]="空瓶";config["item_names"]!["6"]="水";
        config["gathering_routes"]!["water"]=JsonNode.Parse("""{"target_item_id":6,"skill":"daily","node":"水井","attempts_per_batch":10}""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("water","gathering",6,1,"water",[]));var ports=new Ports{BulkSync=true};
        ports.Gather.Frame=ports.Gather.Frame with{RouteKey="water"};ports.Consumables=new(new Dictionary<string,GatheringConsumableRule>{["water"]=new(5,"空瓶")},()=>ports.Stock,()=>new());
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();
        for(var i=0;i<5;i++)host.Tick();Assert.Equal(0,ports.Gather.Starts);Assert.Equal(1,ports.SyncRequests);
        host.SuspendForInventorySync();host.FinishInventorySyncWithoutCoverage("waiting for packet");ports.Set(5,10);
        for(var i=0;i<10&&ports.Gather.Starts==0;i++)host.Tick();Assert.Equal(1,ports.Gather.Starts);Assert.Equal(1,ports.SyncRequests);Assert.True(host.Status.Running);
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
    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void UnknownWithdrawalLeavesRequestsOneRefreshThenAccountsWithoutAnotherTransfer(bool relief,bool requestedBeforeRestart)
    {
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));
        var ports=new Ports{BulkSync=true};ports.Set(1,0);ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[1]=10});var time=new ManualTime();
        using(var host=Host(queue,ports,time)){host.Start();host.Tick();host.Tick();Assert.Single(ports.Store.Actions);}
        var path=Path.Combine(directory,"storage-supply.json");var store=new JsonWorkflowStore(path);var saved=store.Read()!;
        // Restore the exact final-transfer boundary with unavailable packet balances.
        saved["pending"]!["kind"]="confirm_transfer";saved["pending"]!["deadline"]=101d;
        if(requestedBeforeRestart)
        {saved["reconciliation_navigation_started"]=true;saved["reconciliation_navigation_released"]=true;saved["reconciliation_refresh_requested"]=true;}
        store.Flush(saved);
        if(relief)
        {
            var maintenance=new JsonWorkflowStore(Path.Combine(directory,"maintenance.json"));var state=maintenance.Read()!;
            state["backpack"]!["phase"]="relief";state["backpack"]!["status"]="production_relief";maintenance.Flush(state);
        }
        using var restored=new AutomationCoordinator(Catalog(),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:relief),time);ports.Store.LeaveAllowed=true;ports.Store.Frame=ports.Store.Frame with{At=116,CaptureId="late",Available=null,BagQuantity=null};restored.Start();
        for(var n=0;n<5;n++)restored.Tick();
        Assert.True(restored.SafeToSwitch);Assert.True(restored.CanPrepareInventoryRefresh);Assert.Equal(1,ports.SyncRequests);Assert.Single(ports.Store.Actions);Assert.NotNull(store.Read()!["pending"]);
        for(var n=0;n<3;n++)restored.Tick();Assert.Equal(1,ports.SyncRequests);
        ports.Store.Frame=ports.Store.Frame with{At=117,CaptureId="fresh",Available=0,BagQuantity=10,BalanceAt=117};ports.Set(1,10);ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[1]=0});
        restored.ContinueAfterInventorySync();for(var n=0;n<15;n++)Assert.Null(restored.Tick().Fault);
        Assert.True(store.Read()!["status"]!.GetValue<string>()=="complete",store.Read()!.ToJsonString());Assert.Null(store.Read()!["pending"]);Assert.Single(ports.Store.Actions);Assert.True(ports.Crafts>0);
    }
    [Fact] public void FailedDecompositionRefreshRetriesOnlyAfterExplicitContinueWithoutReplayingConsumption()
    {
        var config=Catalog().Configuration;config["material_sources"]=new JsonObject{["2"]=new JsonObject{["kind"]="decomposition"}};
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("wood","production",4,1,"wood",[]));var ports=new Ports{BulkSync=true,Conversion=new()};ports.Set(2,0);
        ports.Stock.SetStorage("character",new Dictionary<long,long?>{[2]=0});ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[2]=0});
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();host.Tick();Assert.Equal(1,ports.Conversion.Requests);
        ports.Conversion.State=new JsonObject{["status"]="unknown",["pending"]=new JsonObject{["kind"]="decompose"}};host.Tick();
        Assert.Equal(1,ports.SyncRequests);Assert.Equal(1,ports.Conversion.Requests);
        host.SuspendForInventorySync();host.FinishInventorySyncWithoutCoverage("refresh failed");for(var i=0;i<3;i++)host.Tick();
        Assert.Equal(1,ports.SyncRequests);Assert.Equal(1,ports.Conversion.Requests);
        host.Continue();host.Tick();Assert.Equal(2,ports.SyncRequests);Assert.Equal(1,ports.Conversion.Requests);
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
            queue.Edit(q=>{q.Delete(["fish"]);q.Add("iron","production",3,1,"iron",[]);});host.SynchronizeQueue();
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
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("water","gathering",6,2,"water",[]));var ports=new Ports{Coverage=1};ports.Set(5,0);ports.Gather.Frame=ports.Gather.Frame with{RouteKey="water"};
        ports.Stock.SetStorage("character",new Dictionary<long,long?>{[5]=0});ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[5]=0});
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
    [Fact] public void TwoWaterRoundsWithdrawTwentyBottlesInOnePlan()
    {
        var config=Catalog().Configuration;config["item_names"]!["5"]="空瓶";config["item_names"]!["6"]="水";
        config["gathering_routes"]!["water"]=JsonNode.Parse("""{"target_item_id":6,"skill":"daily","node":"水井","attempts_per_batch":10}""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("water","gathering",6,2,"water",[]));var ports=new Ports();ports.Set(5,0);ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[5]=20});
        ports.Gather.Frame=ports.Gather.Frame with{RouteKey="water"};ports.Consumables=new(new Dictionary<string,GatheringConsumableRule>{["water"]=new(5,"空瓶")},()=>ports.Stock,()=>new(AutoBuyBottles:false));
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();
        for(var i=0;i<12&&!File.Exists(Path.Combine(directory,"storage-supply.json"));i++)Assert.Null(host.Tick().Fault);
        var state=new JsonWorkflowStore(Path.Combine(directory,"storage-supply.json")).Read()!;
        Assert.Equal(20,state["plans"]![0]!["quantity"]!.GetValue<long>());Assert.Equal(0,ports.Purchases);
    }
    [Theory][InlineData(false,false)][InlineData(true,false)][InlineData(true,true)]
    public void ExistingWithdrawalSurvivesUnknownDependencyButExplicitPauseRetiresIt(bool unknownParent,bool independentKnownDemand)
    {
        var config=Catalog().Configuration;
        if(unknownParent)
        {
            config["recipes"]!["iron"]!["materials"]=JsonNode.Parse("""[{"item_id":4,"quantity":1}]""");
        }
        var material=unknownParent?2L:1L;var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));
        var ports=new Ports();ports.Set(1,0);ports.Set(2,0);ports.Set(4,0);ports.Store.Frame=ports.Store.Frame with{Stable=false};
        ports.Stock.SetStorage("character",new Dictionary<long,long?>{[1]=0,[2]=0,[4]=0});
        ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[1]=20,[2]=20,[4]=0});
        var path=Path.Combine(directory,"storage-supply.json");using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();
        for(var i=0;i<12&&!File.Exists(path);i++)host.Tick();
        var original=new JsonWorkflowStore(path).Read()!;Assert.Equal(material,original["plans"]![0]!["item_id"]!.GetValue<long>());
        var action=original["runtime_action"]!.GetValue<string>();
        if(independentKnownDemand){queue.Edit(q=>q.Add("wood","production",4,1,"wood",[]));ports.Set(material,10);}
        var unknown=unknownParent?4L:material;ports.Stock.Set(new(unknown,null,ports.Scope,"packet","gap",false,"capture_invalidated"));
        for(var i=0;i<3;i++)Assert.Null(host.Tick().Fault);
        var retained=new JsonWorkflowStore(path).Read()!;Assert.Equal(action,retained["runtime_action"]!.GetValue<string>());
        Assert.NotEqual("complete",retained["status"]!.GetValue<string>());Assert.Empty(ports.Store.Actions);
        queue.Edit(q=>q.Control("iron","pause"));host.SynchronizeQueue();for(var i=0;i<4&&File.Exists(path);i++)host.Tick();
        Assert.False(File.Exists(path));Assert.Empty(ports.Store.Actions);
    }
    [Fact] public void PausedWaterRetiresUnsentBottleWithdrawalAndCanPlanAgain()
    {
        var config=Catalog().Configuration;config["item_names"]!["5"]="空瓶";config["item_names"]!["6"]="水";
        config["gathering_routes"]!["water"]=JsonNode.Parse("""{"target_item_id":6,"skill":"daily","node":"水井","attempts_per_batch":10}""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("water","gathering",6,2,"water",[]));var ports=new Ports();ports.Set(5,0);ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[5]=20});
        ports.Gather.Frame=ports.Gather.Frame with{RouteKey="water"};ports.Consumables=new(new Dictionary<string,GatheringConsumableRule>{["water"]=new(5,"空瓶")},()=>ports.Stock,()=>new(AutoBuyBottles:false));
        var path=Path.Combine(directory,"storage-supply.json");using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();
        for(var i=0;i<12&&!File.Exists(path);i++)host.Tick();var first=new JsonWorkflowStore(path).Read()!["runtime_action"]!.GetValue<string>();Assert.Empty(ports.Store.Actions);
        queue.Edit(q=>q.Control("water","pause"));host.SynchronizeQueue();for(var i=0;i<4&&File.Exists(path);i++)host.Tick();
        Assert.False(File.Exists(path));Assert.Empty(ports.Store.Actions);
        queue.Edit(q=>q.Control("water","continue"));host.SynchronizeQueue();for(var i=0;i<12&&!File.Exists(path);i++)host.Tick();
        Assert.NotEqual(first,new JsonWorkflowStore(path).Read()!["runtime_action"]!.GetValue<string>());Assert.Empty(ports.Store.Actions);
    }
    [Fact] public void AbandonedStorageExitStillDeliversConfirmedBottlesToWaterGathering()
    {
        var config=Catalog().Configuration;config["item_names"]!["5"]="空瓶";config["item_names"]!["6"]="水";
        config["gathering_routes"]!["water"]=JsonNode.Parse("""{"target_item_id":6,"skill":"daily","node":"水井","attempts_per_batch":10}""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("water","gathering",6,1,"water",[]));var ports=new Ports();ports.Set(5,20);
        ports.Store.ReleaseSucceeds=false;ports.Gather.Frame=ports.Gather.Frame with{RouteKey="water"};
        ports.Consumables=new(new Dictionary<string,GatheringConsumableRule>{["water"]=new(5,"空瓶")},()=>ports.Stock,()=>new(AutoBuyBottles:false));
        // A withdrawal whose transfer was confirmed but whose exit was given up: the
        // game may still be in the warehouse. The bag change is what the water batch
        // needs, so giving up the exit must not also drop that wake.
        new JsonWorkflowStore(Path.Combine(directory,"storage-supply.json")).Flush(new JsonObject{
            ["schema_version"]=1,["runtime_scope"]="character",["runtime_action"]="withdrawal:x",
            ["plans"]=new JsonArray(new JsonObject{["item_id"]=5L,["name"]="空瓶",["quantity"]=10L,["scope"]="shared",["goal_ids"]=new JsonArray()}),
            ["index"]=1,["status"]="running",["phase"]="navigate",["pending"]=null,["completed"]=new JsonArray(),
            ["pause_reason"]=null,["reason"]="storage_exit_unverified",["wait"]=null,["search"]=new JsonObject(),
            ["release_attempts"]=3,["release_reason"]="release_attempts_exhausted",["exit_page_unverified"]=true});
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();
        Assert.True(File.Exists(Path.Combine(directory,"storage-supply.json")),"restored record was dropped before the first tick");
        for(var i=0;i<12&&ports.Gather.Starts==0;i++)Assert.Null(host.Tick().Fault);
        Assert.True(ports.Gather.Starts>0);
        for(var i=0;i<3;i++)Assert.Null(host.Tick().Fault);
        // The page is reported as never verified instead of assumed to be the scene,
        // and the confirmed transfer is never replayed to make the exit succeed.
        var record=new JsonWorkflowStore(Path.Combine(directory,"storage-supply.json")).Read();
        Assert.True(record?["exit_page_unverified"]?.GetValue<bool>()==true,record?.ToJsonString()??"<missing>");
        Assert.Empty(ports.Store.Actions);
    }
    [Fact] public void WarehouseAppearingBeforePaymentCancelsPurchaseAndCreatesWithdrawal()
    {
        var config=Catalog().Configuration;config["item_names"]!["5"]="空瓶";config["item_names"]!["6"]="水";
        config["gathering_routes"]!["water"]=JsonNode.Parse("""{"target_item_id":6,"skill":"daily","node":"水井","attempts_per_batch":10}""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("water","gathering",6,1,"water",[]));var ports=new Ports();ports.Set(5,0);
        ports.Stock.SetStorage("character",new Dictionary<long,long?>{[5]=0});ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[5]=0});
        ports.Gather.Frame=ports.Gather.Frame with{RouteKey="water"};ports.Consumables=new(new Dictionary<string,GatheringConsumableRule>{["water"]=new(5,"空瓶")},()=>ports.Stock,()=>new());
        var purchasePath=Path.Combine(directory,"bottle-purchase.json");using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();
        for(var i=0;i<12&&!File.Exists(purchasePath);i++)host.Tick();Assert.Equal(PurchaseStage.Navigate,JsonSerializer.Deserialize<PurchaseState>(File.ReadAllText(purchasePath))!.Stage);Assert.Equal(0,ports.Purchases);
        ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[5]=10});host.Tick();
        var purchaseState=JsonSerializer.Deserialize<PurchaseState>(File.ReadAllText(purchasePath))!;Assert.Equal(PurchaseStage.Complete,purchaseState.Stage);Assert.Null(purchaseState.Intent);Assert.Equal(0,ports.Purchases);
        var storage=new JsonWorkflowStore(Path.Combine(directory,"storage-supply.json")).Read()!;Assert.Equal(10,storage["plans"]![0]!["quantity"]!.GetValue<long>());
    }
    [Fact] public void MissingManualPurchaseMaterialDoesNotBuyOrBlockIndependentProduction()
    {
        var config=Catalog().Configuration;config["item_names"]!["1"]="蜂蠟";config["gathering_routes"]!.AsObject().Clear();
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("iron","production",3,1,"iron",[]);q.Add("wood","production",4,1,"wood",[]);});
        var ports=new Ports();ports.Set(1,0);ports.Set(2,10);using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();
        for(var i=0;i<15;i++)Assert.Null(host.Tick().Fault);
        Assert.Equal(0,ports.Purchases);Assert.True(ports.Crafts>0);Assert.Contains(host.Status.Issues.Values,s=>s.Contains("蜂蠟材料不足"));
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void UnknownStorageBlocksPurchaseAndDecompositionAndMergesRecoverableRefresh(bool absentStorage)
    {
        var config=Catalog().Configuration;config["item_names"]!["5"]="紙";config["recipes"]!["iron"]!["materials"]=JsonNode.Parse("""[{"item_id":5,"quantity":10}]""");
        config["material_sources"]=new JsonObject{["2"]=new JsonObject{["kind"]="decomposition"}};
        var queue=new RuntimeQueue(directory);queue.Edit(q=>{q.Add("paper","production",3,1,"iron",[]);q.Add("wood","production",4,1,"wood",[]);});
        var ports=new Ports{BulkSync=true,Conversion=new()};ports.Set(5,0);ports.Set(2,0);
        if(!absentStorage)
        {
            ports.Stock.SetStorage("character",new Dictionary<long,long?>{[2]=null,[5]=null});
            ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[2]=null,[5]=null});
        }
        using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();for(var i=0;i<3;i++)host.Tick();
        Assert.Equal(0,ports.Purchases);Assert.Equal(0,ports.Conversion.Requests);Assert.Equal(1,ports.SyncRequests);
        host.SuspendForInventorySync();host.FinishInventorySyncWithoutCoverage("refresh failed");for(var i=0;i<3;i++)host.Tick();
        Assert.Equal(1,ports.SyncRequests);Assert.Equal(0,ports.Purchases);Assert.Equal(0,ports.Conversion.Requests);
        host.Continue();host.Tick();Assert.Equal(2,ports.SyncRequests);Assert.Equal(0,ports.Purchases);Assert.Equal(0,ports.Conversion.Requests);
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
        ports.Stock.SetStorage("character",new Dictionary<long,long?>{[5]=0});ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[5]=0});
        ports.Consumables=new(new Dictionary<string,GatheringConsumableRule>{["water"]=new(5,"空瓶")},()=>ports.Stock,()=>new());
        using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime()))
        {host.Start();for(var i=0;i<10&&ports.Purchases==0;i++)host.Tick();Assert.Equal(1,ports.Purchases);}
        using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime()))
        {host.Start();for(var i=0;i<4;i++)host.Tick();Assert.Equal(1,ports.Purchases);Assert.Equal(0,ports.Gather.Starts);Assert.False(host.SafeToSwitch);}
    }
    [Fact] public void CompletedPurchaseSurvivesRestartUntilFreshStockCanBeReservedWithoutRepayment()
    {
        var config=Catalog().Configuration;config["item_names"]!["5"]="空瓶";config["item_names"]!["6"]="水";
        config["gathering_routes"]!["water"]=JsonNode.Parse("""{"target_item_id":6,"skill":"daily","node":"水","attempts_per_batch":10}""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("water","gathering",6,2,"water",[]));var ports=new Ports{Coverage=1};ports.Set(5,0);ports.Gather.Frame=ports.Gather.Frame with{RouteKey="water"};
        ports.Stock.SetStorage("character",new Dictionary<long,long?>{[5]=0});ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[5]=0});
        ports.Consumables=new(new Dictionary<string,GatheringConsumableRule>{["water"]=new(5,"空瓶")},()=>ports.Stock,()=>new());
        var path=Path.Combine(directory,"bottle-purchase.json");bool Complete()=>File.Exists(path)&&JsonSerializer.Deserialize<PurchaseState>(File.ReadAllText(path))?.Stage==PurchaseStage.Complete;
        using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime()))
        {host.Start();for(var i=0;i<30&&!Complete();i++)host.Tick();Assert.True(Complete());Assert.Equal(1d,JsonSerializer.Deserialize<PurchaseState>(File.ReadAllText(path))!.CompletedThrough!.Value);Assert.Equal(1,ports.Purchases);}
        ports.PurchaseVisible=false;
        Assert.Equal(20,ports.Stock.Query(5).Quantity);
        ports.Stock.Set(new(5,20,ports.Scope,"packet","rebuilt-old-instance",true){VerifiedThrough=0});
        using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime()))
        {
            // Sufficient stock may run the independent batch, but cannot clear Complete or authorize repayment.
            host.Start();for(var i=0;i<8;i++)host.Tick();Assert.Equal(1,ports.Purchases);Assert.Equal(1,ports.Gather.Starts);Assert.True(Complete());
            Assert.True(File.Exists(Path.Combine(directory,"bottle-purchase-context.json")));
            ports.Stock.Set(new(5,10,ports.Scope,"packet","new-version-old-quantity",true){VerifiedThrough=2});host.Tick();
            Assert.True(Complete());Assert.Equal(1,ports.Gather.Starts);Assert.Equal(1,ports.Purchases);
            ports.Stock.Set(new(5,20,ports.Scope,"packet","covered-arrival",true){VerifiedThrough=3});
            for(var i=0;i<12&&ports.Gather.Starts==0;i++)host.Tick();Assert.Equal(1,ports.Purchases);Assert.Equal(1,ports.Gather.Starts);
        }
    }
    [Fact] public void CompletedMaterialPurchaseRequiresTargetQuantityAndPostCompletionCoverage()
    {
        var config=Catalog().Configuration;config["item_names"]!["5"]="紙";
        config["recipes"]!["iron"]!["materials"]=JsonNode.Parse("""[{"item_id":5,"quantity":10}]""");
        var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("paper-product","production",3,1,"iron",[]));var ports=new Ports{Coverage=1};ports.Set(5,3);
        ports.Stock.SetStorage("character",new Dictionary<long,long?>{[5]=0});ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[5]=0});
        var path=Path.Combine(directory,"bottle-purchase.json");bool Complete()=>File.Exists(path)&&JsonSerializer.Deserialize<PurchaseState>(File.ReadAllText(path))?.Stage==PurchaseStage.Complete;
        using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime()))
        {host.Start();for(var i=0;i<30&&!Complete();i++)host.Tick();Assert.True(Complete());Assert.Equal(1d,JsonSerializer.Deserialize<PurchaseState>(File.ReadAllText(path))!.CompletedThrough!.Value);Assert.Equal(1,ports.Purchases);}
        ports.PurchaseVisible=false;ports.Stock.Set(new(5,13,ports.Scope,"packet","rebuilt-old-instance",true){VerifiedThrough=0});
        using var continued=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());continued.Start();for(var i=0;i<4;i++)continued.Tick();
        // Existing sufficient stock may craft; the completion record still awaits valid arrival coverage.
        Assert.True(Complete());Assert.Equal(1,ports.Purchases);Assert.Equal(1,ports.Crafts);
        ports.Stock.Set(new(5,3,ports.Scope,"packet","new-version-old-quantity",true){VerifiedThrough=2});continued.Tick();
        Assert.True(Complete());Assert.Equal(1,ports.Purchases);Assert.Equal(1,ports.Crafts);
        ports.Stock.Set(new(5,13,ports.Scope,"packet","covered-arrival",true){VerifiedThrough=3});
        for(var i=0;i<12&&ports.Crafts==0;i++)continued.Tick();Assert.True(ports.Crafts>0);Assert.Equal(1,ports.Purchases);
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
        var rules=new Dictionary<string,GatheringConsumableRule>{["water"]=new(5,"空瓶")};var queue=new RuntimeQueue(directory);queue.Edit(q=>q.Add("water","gathering",6,1,"water",[]));
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
    [Fact] public void ProtectedTidyPrecedesProductionReliefAndSurplus()
    {
        var queue=new RuntimeQueue(directory);var ports=new Ports{Sale=new()};ports.Maintain.Frame=new("1","backpack",Weight:95,Capacity:100);
        using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:true),new ManualTime());host.Start();
        for(var i=0;i<5;i++)host.Tick();Assert.Equal(0,ports.Sale.Begins);
        ports.Maintain.Frame=new("2","tidy",BoldSelected:true,MaterialsSelected:true,TidyCount:2);host.Tick();Assert.Contains("disable_bold",ports.Maintain.Actions);
        ports.Maintain.Frame=new("3","tidy",BoldSelected:false,MaterialsSelected:true,TidyCount:2);host.Tick();Assert.Contains("deselect_materials",ports.Maintain.Actions);
        ports.Maintain.Frame=new("4","tidy",BoldSelected:false,MaterialsSelected:false,TidyCount:0);host.Tick();Assert.Equal(0,ports.Sale.Begins);
        ports.Maintain.Frame=new("5","backpack",Weight:95,Capacity:100);for(var i=0;i<8;i++)host.Tick();
        Assert.Equal(1,ports.Sale.Begins);Assert.True(ports.Sale.Ticks>0);
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
        public string RuntimeScope="character";public string Scope=>RuntimeScope;public string? CurrentDevice{get;private set;}public InventorySnapshot Stock=new("character");public int Crafts,Leaves,IronInstances,Purchases,AuxiliaryCalls,Collections;public bool Ready,PlayerReady,MetalFull,PurchaseWasDurable,PurchaseVisible=true;
        public GatheringConsumables? Consumables;public IGatheringConsumables? GatheringConsumables=>Consumables;public Func<long,long?>? Reserve;
        public readonly MaintenancePort Maintain=new(){Frame=new("1","scene")};
        public readonly List<string> Navigation=[];
        public SurplusAdapter? Sale;
        public HashSet<long> ExplicitSurplus=[];public bool IsExplicitSurplusMaterial(long item)=>ExplicitSurplus.Contains(item);
        public long MaterialMinimum=300;public long MaterialReserveMinimum(long item)=>MaterialMinimum;
        public DecompositionAdapter? Conversion;public IDecompositionPort? Decomposition(string batch,IWorkflowStore store)=>Conversion;
        public sealed class DecompositionAdapter:IDecompositionPort
        {
            public int Requests,Ticks;private bool running;public JsonObject? State;
            public JsonObject Snapshot()=>State?.DeepClone().AsObject()??new(){["status"]=running?"running":"idle"};
            public bool Request(long quantity){Assert.Equal(1,quantity);Requests++;running=true;return true;}
            public string Tick(){Ticks++;return "running";}public void ObserveOnly(){}public bool ReleaseNavigation()=>true;
        }
        public ISurplusMaintenancePort? Surplus(string batch,IWorkflowStore store,Func<long,long?> reserve){Reserve=reserve;return Sale;}
        public bool FailBeforeTick,FailIronNavigation;
        public bool NavigationOffer;public int NavigationAuxiliaryCalls;
        public bool BeforeNavigationTick(){NavigationAuxiliaryCalls++;return NavigationOffer;}
        public bool BeforeTick(bool safe){if(FailBeforeTick)throw new IOException("sharing after boundary",unchecked((int)0x80070020));Assert.True(safe);AuxiliaryCalls++;return false;}
        public IPurchasePort? Purchase(GatheringSupplyRequest request,string batch,Func<PurchaseState?> persisted)=>new PurchaseAdapter(this,persisted,request.TargetStock);
        public readonly GatheringPort Gather=new();public readonly StoragePort Store=new(){Frame=new(100,"1","storage",Scope:"shared",ItemId:1,Name:"ore",Available:10,BagQuantity:0,BagScope:"character",TransferAvailable:true,BalanceSource:"packet_capture",BalanceAt:100)};
        private int version;private string page="scene",recipe="";private readonly Dictionary<string,int> occupied=[];public double? Coverage;
        public void Set(long item,long quantity)=>Stock.Set(new(item,quantity,Scope,"packet","v"+(++version),true){VerifiedThrough=Coverage});
        public int ReadFailures;public string? PauseReason;
        public int Starts,Continues;public void Start()=>Starts++;public void Continue()=>Continues++;public void Pause(string reason){PauseReason=reason;}public void Dispose(){}
        public InventorySnapshot Inventory(){if(ReadFailures-->0)throw new IOException("sharing violation",unchecked((int)0x80070020));return Stock;}
        public IProductionPort Production(string key,string batch,IWorkflowStore store){if(key=="iron")IronInstances++;return new ProductionAdapter(this,key);}
        public IGatheringPort Gathering(string route,string batch,IWorkflowStore store)=>Gather;
        public IStorageSupplyPort Storage(WithdrawalPlan plan,string batch,IWorkflowStore store)=>Store;
        public readonly FishingPort Fish=new();public IFishingPort Fishing(string point,string batch,IWorkflowStore store)=>Fish;
        public IMaintenancePort Maintenance(string batch,IWorkflowStore store)=>Maintain;
        private sealed class PurchaseAdapter(Ports host,Func<PurchaseState?> persisted,long target):IPurchasePort
        {
            private string page="scene";private long quantity=7;
            public ShopObservation Observe()=>new(true,page,true,true,host.Purchases>0&&host.PurchaseVisible?target:3,host.Purchases>0&&host.PurchaseVisible?49993:50000,quantity,7);
            public void NavigateToShop()=>page="shop";
            public void PrepareQuantity(long n){quantity=n;page="purchase_confirm";}
            public void ConfirmPurchase(){host.PurchaseWasDurable=persisted()?.Intent is not null;host.Purchases++;if(host.PurchaseVisible)host.Set(5,target);}
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
            public ProductionFrame Observe()
            {
                var occupied=host.occupied.GetValueOrDefault(Device);
                var empty=host.MetalFull&&Device=="metal"?new int[0]
                    :Enumerable.Range(occupied,5-occupied).Where(i=>!(host.PlayerReady&&i==4)).ToArray();
                // Slot 4 stands for a finished player-native job: it is ready on screen
                // but belongs to no batch, so one click would clear it too.
                var ready=host.Ready||host.PlayerReady
                    ?Enumerable.Range(0,occupied).Concat(host.PlayerReady?new[]{4}:new int[0]).ToArray()
                    :new int[0];
                return new(host.page,EmptySlots:empty,SlotCount:5,RecipeKey:host.recipe,CraftAvailable:true,
                    CollectAvailable:host.Ready||host.PlayerReady,ReadySlots:ready,AtDevice:host.CurrentDevice==Device);
            }
            public void Navigate(string destination,ProductionRecipe recipe)
            {if(host.FailIronNavigation&&key=="iron")throw new InvalidOperationException("iron target unavailable");host.Navigation.Add(key+":"+destination+":crafts="+host.Crafts+":collects="+host.Collections);if(destination=="leave"){host.Leaves++;host.page="scene";host.CurrentDevice=null;}else{host.CurrentDevice=Device;host.page=destination=="recipe"?"recipe_detail":Device+"_panel";host.recipe=key;}}
            public void Click(string kind,ProductionRecipe recipe)
            {if(kind=="craft"){host.Crafts++;host.occupied[Device]=host.occupied.GetValueOrDefault(Device)+1;}else{host.Collections++;host.occupied[Device]=0;host.PlayerReady=false;}host.page=Device+"_panel";}
            public void Pause(string reason){}public void Resume(){}
        }
    }
}

