using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Workflows.Tests;
public sealed class RuntimeIntegrationTests
{
    [Theory][InlineData("point_fishing_lake")][InlineData(FishingPlan.CurrentLocation)]
    public void FishingPeriodicBackpackCheckWaitsForTwentyRoundsAndTenMinutes(string point)
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-fish-sparse-"+Guid.NewGuid().ToString("N"));
        try
        {
            var config=Catalog().Configuration;config["gathering_routes"]![point]=JsonNode.Parse("""{"target_item_id":1,"skill":"fishing","node":"lake","attempts_per_batch":1}""");
            var queue=new RuntimeQueue(dir);queue.Edit(q=>q.Add("fish","idle_fishing",1,1000,point,[]));
            var ports=new RuntimePorts();var time=new ManualTime();
            using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackCheckIntervalSeconds:120),time);
            host.Start();host.Tick();host.Tick();
            if(point!=FishingPlan.CurrentLocation)
            {ports.Fish.Frame=new(1,"travel",NavigationActive:true);host.Tick();ports.Fish.Frame=new(2,"arrived",NavigationActive:false);host.Tick();}
            ports.Fish.Frame=new(3,"first","fishing",FishingButton:true);host.Tick();
            Assert.Equal(AutomationCadence.Fishing,host.NextCheckDelay());time.Seconds+=121;
            for(var round=0;round<20;round++)
            {
                ports.Fish.Frame=new(4+round*3,"hook-"+round,"fishing",RoundActive:true,LeisureMode:false);host.Tick();
                Assert.Equal(AutomationCadence.Fishing,host.NextCheckDelay());
                ports.Fish.Frame=new(5+round*3,"end-"+round,"fishing",FishingButton:true,Compass:true);host.Tick();
                Assert.Equal(0,ports.Maintain.Navigations);
            }
            Assert.Equal(0,ports.Maintain.Navigations);time.Seconds+=480;
            for(var i=0;i<5&&ports.Maintain.Navigations==0;i++)
            {ports.Fish.Frame=new(65+i,"maintenance-"+i,Compass:true);host.Tick();}
            Assert.True(ports.Maintain.Navigations>0);Assert.Contains(ports.Fish.Actions,a=>a.Kind=="leave");
            Assert.True(host.Status.Running);Assert.Null(host.Status.Fault);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Theory][InlineData("inventory_unknown")][InlineData("no_source")][InlineData("recipe_cycle")]
    public void SufficientMaterialWithdrawsUnsentSupplyDespiteIndependentBlockedMaterial(string issue)
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-local-supply-"+Guid.NewGuid().ToString("N"));
        try
        {
            var config=Catalog().Configuration;
            if(issue=="recipe_cycle")config["recipes"]!["log_cycle"]=JsonNode.Parse("""{"output_item_id":2,"output_per_round":1,"device":"wood","duration_s":60,"materials":[{"item_id":2,"quantity":1}]}""");
            var queue=new RuntimeQueue(dir);queue.Edit(q=>{q.Add("iron","production",3,1,"iron",[]);q.Add("wood","production",4,1,"wood",[]);q.Add("auto-material:ore","gathering",1,5,"ore",[]);});
            var ports=new RuntimePorts();ports.Stock.Set(new(1,10,ports.Scope,"packet_capture","ore-v1",true));
            if(issue!="inventory_unknown")ports.Stock.Set(new(2,0,ports.Scope,"packet_capture","log-v1",true));
            using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();host.Tick();
            Assert.DoesNotContain(queue.Read().Jobs,j=>j.Id=="auto-material:ore"&&!j.Cancelled);Assert.Equal(0,ports.Gather.Starts);Assert.Null(host.Status.Fault);
            Assert.Contains(queue.Read().Jobs,j=>j.Id=="wood"&&!j.Cancelled);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void FishingMaterialUsesConfirmedSourceAfterWarehouseAndNeverInventsYield(bool warehouseAvailable)
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-fishing-source-"+Guid.NewGuid().ToString("N"));
        try
        {
            var config=Catalog().Configuration;
            config["gathering_routes"]!["east_wall"]=JsonNode.Parse("""{"target_item_id":9,"skill":"fishing","node":"east","attempts_per_batch":10}""");
            config["material_sources"]=JsonNode.Parse("""{"2":{"kind":"fishing","point_key":"east_wall"}}""");
            var queue=new RuntimeQueue(dir);queue.Edit(q=>q.Add("wood","production",4,1,"wood",[]));var ports=new RuntimePorts();
            ports.Stock.Set(new(2,0,ports.Scope,"packet_capture","v1",true));
            if(warehouseAvailable)ports.Stock.SetStorage("shared",new Dictionary<long,long?>{[2]=10});
            using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();host.Tick();
            var automatic=queue.Read().Jobs.Where(j=>j.Kind=="idle_fishing").ToArray();
            if(warehouseAvailable)Assert.Empty(automatic);
            else{var fish=Assert.Single(automatic);Assert.Equal(2,fish.ItemId);Assert.Equal("east_wall",fish.Recipe);Assert.Equal(60,fish.Target);Assert.Equal(0,fish.Completed);}
            Assert.Equal(0,ports.Stock.Query(2).Quantity);Assert.Null(host.Status.Fault);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void CompletedFishingSupplyRejectsVersionAndInstanceChangeWithoutBatchEndCoverage()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-fishing-packet-"+Guid.NewGuid().ToString("N"));
        try
        {
            var config=Catalog().Configuration;config["gathering_routes"]!["east_wall"]=JsonNode.Parse("""{"target_item_id":9,"skill":"fishing","node":"east","attempts_per_batch":10}""");
            config["material_sources"]=JsonNode.Parse("""{"2":{"kind":"fishing","point_key":"east_wall"}}""");
            var queue=new RuntimeQueue(dir);queue.Edit(q=>q.Add("wood","production",4,1,"wood",[]));var ports=new RuntimePorts();var time=new ManualTime();
            ports.Stock.Set(new(2,0,ports.Scope,"packet_capture","v1",true){VerifiedThrough=99});
            using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time)){host.Start();host.Tick();}
            var job=Assert.Single(queue.Read().Jobs,j=>j.Kind=="idle_fishing");
            var claimed=queue.Read().ToJson()["jobs"]!.AsArray().First(j=>j!["job_id"]!.GetValue<string>()==job.Id)!;
            var folder=queue.BatchDirectory(ports.Scope,job.Id,claimed["pending"]!["action_id"]!.GetValue<string>());
            var stateStore=new JsonWorkflowStore(Path.Combine(folder,"idle-fishing.json"));var ended=stateStore.Read()!;
            ended["status"]="complete";ended["phase"]="complete";ended["submitted"]=1;ended["elapsed_s"]=60d;stateStore.Flush(ended);
            using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time))
            {
                host.Start();host.Tick();host.Tick();Assert.Null(host.Status.Fault);Assert.DoesNotContain(host.Status.Issues.Values,v=>v.Contains("恢复失败"));
                Assert.False(queue.Read().Jobs.Single(j=>j.Id==job.Id).Pending,host.Status.Message+" "+string.Join(";",host.Status.Issues.Values));
            }
            Assert.Equal(99d,new JsonWorkflowStore(Path.Combine(folder,"batch.json")).Read()!["batch_end_through"]!.GetFiniteDouble());
            using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time)){host.Start();for(var i=0;i<4;i++)host.Tick();}
            Assert.Equal(60,queue.Read().Jobs.Single(j=>j.Id==job.Id).Target);Assert.Equal(0,ports.Stock.Query(2).Quantity);
            ports.Stock.Set(new(2,1,ports.Scope,"packet_capture","rebuilt-instance:v2",true){VerifiedThrough=99});
            using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time)){host.Start();host.Tick();}
            Assert.Equal(60,queue.Read().Jobs.Single(j=>j.Id==job.Id).Target);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void CompletedFishingSupplyExtendsOnlyAfterContinuousCoverageWithSameVersionAndQuantity()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-fishing-continuous-"+Guid.NewGuid().ToString("N"));
        try
        {
            var config=Catalog().Configuration;config["gathering_routes"]!["east_wall"]=JsonNode.Parse("""{"target_item_id":9,"skill":"fishing","node":"east","attempts_per_batch":10}""");
            config["material_sources"]=JsonNode.Parse("""{"2":{"kind":"fishing","point_key":"east_wall"}}""");
            var queue=new RuntimeQueue(dir);queue.Edit(q=>q.Add("wood","production",4,1,"wood",[]));var ports=new RuntimePorts();var time=new ManualTime();
            ports.Stock.Set(new(2,0,ports.Scope,"packet_capture","v1",true){VerifiedThrough=99});
            using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time)){host.Start();host.Tick();}
            var job=Assert.Single(queue.Read().Jobs,j=>j.Kind=="idle_fishing");
            var claimed=queue.Read().ToJson()["jobs"]!.AsArray().First(j=>j!["job_id"]!.GetValue<string>()==job.Id)!;
            var folder=queue.BatchDirectory(ports.Scope,job.Id,claimed["pending"]!["action_id"]!.GetValue<string>());
            var stateStore=new JsonWorkflowStore(Path.Combine(folder,"idle-fishing.json"));var ended=stateStore.Read()!;
            ended["status"]="complete";ended["phase"]="complete";ended["submitted"]=1;ended["elapsed_s"]=60d;stateStore.Flush(ended);
            using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time))
            {
                host.Start();host.Tick();host.Tick();Assert.Null(host.Status.Fault);Assert.DoesNotContain(host.Status.Issues.Values,v=>v.Contains("恢复失败"));
                Assert.False(queue.Read().Jobs.Single(j=>j.Id==job.Id).Pending,host.Status.Message+" "+string.Join(";",host.Status.Issues.Values));
            }
            Assert.Equal(99d,new JsonWorkflowStore(Path.Combine(folder,"batch.json")).Read()!["batch_end_through"]!.GetFiniteDouble());
            using var continued=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time);continued.Start();for(var i=0;i<4;i++)continued.Tick();
            Assert.Equal(60,queue.Read().Jobs.Single(j=>j.Id==job.Id).Target);
            ports.Stock.Set(ports.Stock.Query(2) with{VerifiedThrough=99.5});continued.Tick();
            Assert.Equal(60,queue.Read().Jobs.Single(j=>j.Id==job.Id).Target);
            time.Seconds=101;ports.Stock.Set(ports.Stock.Query(2) with{VerifiedThrough=101});continued.Tick();
            Assert.Equal(120,queue.Read().Jobs.Single(j=>j.Id==job.Id).Target);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void ManualGatheringCountsNativeRoundsWithoutScreenYield()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-manual-rounds-"+Guid.NewGuid().ToString("N"));
        try
        {
            var queue=new RuntimeQueue(dir);queue.Edit(q=>q.Add("manual","gathering",1,2,"ore",[]));var ports=new RuntimePorts();var time=new ManualTime();
            using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time);host.Start();
            for(var round=1;round<=2;round++)
            {
                ports.Gather.Frame=new("scene",RouteKey:"ore",StartAvailable:true);
                for(var i=0;i<12&&ports.Gather.Starts<round;i++)host.Tick();Assert.Equal(round,ports.Gather.Starts);
                ports.Gather.Frame=new("scene",RouteKey:"ore",TaskId:JsonValue.Create(round),TaskStatus:"complete",Evidence:new JsonObject{["navigation_idle_confirmed"]=true});
                for(var i=0;i<4;i++)host.Tick();Assert.Equal(round,queue.Read().Jobs.Single(j=>j.Id=="manual").Completed);
            }
            Assert.Equal("complete",queue.Read().Status("manual"));Assert.Null(host.Status.Fault);Assert.Equal(2,ports.Gather.Starts);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void LegacyBatchRefreshPolicyRetiresButUnknownConsumptionAndExtensionsRemain()
    {
        var store=new MemoryStore();store.Flush(new JsonObject{["debits"]=new JsonObject{["craft:1"]=new JsonArray(new JsonObject{["item"]=1L,["quantity"]=7L,["scope"]="character",["version"]="v1",["source"]="packet_capture"})},["refreshes"]=new JsonObject{["old-batch:inventory"]=new JsonObject{["ItemId"]=1L}},["seen"]=new JsonObject{["craft:1"]=true,["old-batch:inventory"]=true},["extension"]="keep"});
        var ledger=new RuntimeInventory(store);var stock=new InventorySnapshot("character");stock.Set(new(1,20,stock.Scope,"packet_capture","v1",true));
        Assert.Equal(13,ledger.Apply(stock).Query(1).Quantity);Assert.Equal(7,ledger.ReservedQuantity(1,stock.Scope));Assert.False(ledger.HasPendingRefresh(1,stock.Scope));
        Assert.Equal("keep",store.Read()!["extension"]!.GetValue<string>());Assert.Equal(2,store.Read()!["scheduler_version"]!.GetValue<int>());Assert.NotNull(store.Read()!["seen"]!["craft:1"]);Assert.Null(store.Read()!["seen"]!["old-batch:inventory"]);
    }
    [Fact] public void RuntimeInventoryExposesReservationsWithoutChangingObservedStock()
    {
        var store=new MemoryStore();var ledger=new RuntimeInventory(store);var stock=new InventorySnapshot("character");
        stock.Set(new(1,20,"character","packet_capture","v1",true));
        ledger.Submitted("craft",1,new("r",2,1,new(){[1]=7},"metal"),stock);
        Assert.Equal(7,ledger.ReservedQuantity(1,"character"));
        Assert.False(ledger.HasPendingRefresh(1,"character"));
        Assert.Equal(13,ledger.Apply(stock).Query(1).Quantity);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void FishingDoesNotRequestBackpackOrInventoryVerificationOnStartLoopAndResume(bool otherTaskQueued)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mabi-fish-no-inspection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = Catalog().Configuration;
            config["gathering_routes"]!["point_fishing_lake"] = JsonNode.Parse("""{"target_item_id":1,"skill":"fishing","node":"lake","attempts_per_batch":1}""");
            var queue = new RuntimeQueue(dir); queue.Edit(q => q.Add("fish", "idle_fishing", 1, 3600, "point_fishing_lake", []));
            if (otherTaskQueued) queue.Edit(q => { q.Add("iron", "production", 3, 5, "iron", []); q.Control("iron", "pause"); });
            var ports = new RuntimePorts(); var time = new ManualTime();
            using var host = new AutomationCoordinator(new(config), queue, ports, () => new(RepairEnabled: false, BackpackCheckIntervalSeconds: 120), time);
            host.Start(); host.Tick(); host.Tick();
            ports.Fish.Frame = new(1, "2", NavigationActive: true); host.Tick();
            ports.Fish.Frame = new(2, "3", NavigationActive: false); host.Tick();
            ports.Fish.Frame = new(3, "4", "fishing", FishingButton: true); host.Tick();
            for (var i = 1; i <= 4; i++)
            {
                time.Seconds += 121; ports.Fish.Frame = new(3 + time.Seconds, "round-" + i, "fishing", RoundActive: true, LeisureMode: false); host.Tick();
                if (i == 2) { host.Pause(); host.Continue(); host.Tick(); }
            }
            Assert.Equal(0, ports.Maintain.Navigations); Assert.Empty(ports.Maintain.Actions);
            Assert.Equal(0, ports.RefreshRequests);
            Assert.DoesNotContain(ports.Fish.Actions, a => a.Kind == "leave");
            Assert.True(host.Status.Running); Assert.Null(host.Status.Fault);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact] public void FishingPausesItsClockForRepairAndReturnsToSamePoint()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-fish-repair-"+Guid.NewGuid().ToString("N"));
        try
        {
            var config=Catalog().Configuration;config["gathering_routes"]!["point_fishing_lake"]=JsonNode.Parse("""{"target_item_id":1,"skill":"fishing","node":"lake","attempts_per_batch":1}""");
            var queue=new RuntimeQueue(dir);queue.Edit(q=>q.Add("fish","idle_fishing",1,60,"point_fishing_lake",[]));var ports=new RuntimePorts();var time=new ManualTime();
            using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(IntervalSeconds:10,BackpackCheckIntervalSeconds:120),time);
            host.Start();host.Tick();host.Tick();ports.Fish.Frame=new(1,"2",NavigationActive:true);host.Tick();ports.Fish.Frame=new(2,"3",NavigationActive:false);host.Tick();
            ports.Fish.Frame=new(3,"4","fishing",FishingButton:true);host.Tick();ports.Fish.Frame=new(4,"5","fishing",RoundActive:true,LeisureMode:false);host.Tick();
            time.Seconds+=11;ports.Fish.Frame=new(8,"6","fishing",RoundActive:true,LeisureMode:false);host.Tick();
            Assert.True(host.Status.Jobs["fish"].Confirmed>0);Assert.Equal(host.Status.ActiveConfirmed,host.Status.Jobs["fish"].Confirmed);
            Assert.Empty(ports.Maintain.Actions);Assert.Equal(0,ports.Maintain.Navigations);Assert.Equal("leave",ports.Fish.Actions.Last().Kind);
            ports.Fish.Frame=new(9,"7",Compass:true);host.Tick();Assert.Equal("leave",ports.Fish.Actions.Last().Kind);
            ports.Fish.CanResumeAtPoint=false; // Maintenance navigation invalidates the prior fishing-point proof.
            ports.Maintain.Frame=new("quote","repair_quote",Gold:100000,Quote:1000,Currency:"gold",ConfirmAvailable:true);host.Tick();
            Assert.Equal(new[]{"confirm_repair"},ports.Maintain.Actions);
            ports.Maintain.Frame=new("paid","repair_tools",Gold:99000,RepairComplete:true);host.Tick();host.Tick();
            Assert.Equal(new[]{"confirm_repair","auto_equip"},ports.Maintain.Actions);
            ports.Maintain.Frame=new("equipped","scene",Gold:99000,ToolsEquipped:true);ports.Fish.Frame=new(10,"8",Compass:true);host.Tick();host.Tick();host.Tick();
            Assert.Equal(2,ports.Fish.Actions.Count(a=>a.Kind=="navigate"));Assert.All(ports.Fish.Actions.Where(a=>a.Kind=="navigate"),a=>Assert.Equal("point_fishing_lake",a.PointKey));
            Assert.Equal(1,ports.Fish.Actions.Count(a=>a.Kind=="start"));Assert.Null(host.Status.Fault);Assert.True(host.Status.Running);
            var state=JsonNode.Parse(File.ReadAllText(Directory.GetFiles(dir,"idle-fishing.json",SearchOption.AllDirectories).Single()))!;
            Assert.Equal(5,state["elapsed_s"]!.GetValue<double>());Assert.Single(queue.Read().Jobs);Assert.Equal(0,ports.RefreshRequests);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void CancellingWhilePausedReleasesUnstartedFishingWithoutInput()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-cancel-"+Guid.NewGuid().ToString("N"));
        try
        {
            var config=Catalog().Configuration;config["gathering_routes"]!["point_fishing_lake"]=JsonNode.Parse("""{"target_item_id":1,"skill":"fishing","node":"lake","attempts_per_batch":1}""");
            var queue=new RuntimeQueue(dir);queue.Edit(q=>q.Add("fish","idle_fishing",1,60,"point_fishing_lake",[]));var ports=new RuntimePorts();
            using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());
            host.Start();host.Tick();host.Pause();var inputs=ports.Fish.Actions.Count;queue.Edit(q=>q.Control("fish","cancel"));
            host.ObservePausedWork();Assert.Empty(queue.Read().Jobs);Assert.Equal(inputs,ports.Fish.Actions.Count);Assert.False(host.Status.Running);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void PausedFishingConfirmsAndClosesCancelledRoundWithoutInputOrInventoryInspection()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-fish-"+Guid.NewGuid().ToString("N"));
        try
        {
            var config=Catalog().Configuration;config["gathering_routes"]!["point_fishing_lake"]=JsonNode.Parse("{\"target_item_id\":1,\"skill\":\"fishing\",\"node\":\"lake\",\"attempts_per_batch\":1}");
            var queue=new RuntimeQueue(dir);queue.Edit(q=>q.Add("fish","idle_fishing",1,60,"point_fishing_lake",[]));var ports=new RuntimePorts();var time=new ManualTime();
            using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time))
            {
                host.Start();host.Tick();host.Tick();ports.Fish.Frame=new(1,"2",NavigationActive:true);host.Tick();ports.Fish.Frame=new(2,"3",NavigationActive:false);host.Tick();
                ports.Fish.Frame=new(3,"4","fishing",FishingButton:true);host.Tick();Assert.Equal(1,ports.Fish.Actions.Count(a=>a.Kind=="start"));
                host.Pause();ports.Fish.Frame=new(4,"5","fishing",RoundActive:true,LeisureMode:false);host.ObservePausedFishing();Assert.Equal(0,host.Status.ReconciliationCount);
            }
            queue.Edit(q=>q.Control("fish","cancel"));
            using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time))
            {
                Assert.False(host.SafeToSwitch);host.Start();host.Pause();ports.Fish.Frame=new(8,"6",Compass:true);host.ObservePausedFishing();
                Assert.False(host.Status.Running);Assert.DoesNotContain(queue.Read().Jobs,j=>j.Pending);Assert.True(host.SafeToSwitch);
            }
            Assert.Equal(0,ports.RefreshRequests);Assert.Equal(2,ports.Fish.Actions.Count);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void IntegerFishingDurationStartsAndRestoresTheSameBatch()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-runtime-"+Guid.NewGuid().ToString("N"));
        try
        {
            var config=Catalog().Configuration;config["gathering_routes"]!["point_fishing_lake"]=JsonNode.Parse("""{"target_item_id":1,"skill":"fishing","node":"lake","attempts_per_batch":1}""");
            var queue=new RuntimeQueue(dir);queue.Edit(q=>q.Add("fish","idle_fishing",1,3665,"point_fishing_lake",[]));
            var ports=new RuntimePorts();
            using(var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime()))
            {host.Start();host.Tick();Assert.Null(host.Status.Fault);Assert.True(host.Status.Running);Assert.Equal("fish",host.Status.ActiveJob);}
            var before=queue.Read().ToJson()["jobs"]![0]!["pending"]!.DeepClone();
            Assert.Equal(0,ports.RefreshRequests);
            var batch=Directory.GetFiles(dir,"batch.json",SearchOption.AllDirectories).Single();
            Assert.Equal(3665,JsonNode.Parse(File.ReadAllText(batch))!["duration_s"]!.GetFiniteDouble());
            using(var host=new AutomationCoordinator(new(config),queue,new RuntimePorts(),()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime()))
            {host.Start();host.Tick();Assert.Null(host.Status.Fault);Assert.DoesNotContain(host.Status.Issues.Values,v=>v.Contains("恢复失败"));}
            Assert.True(JsonNode.DeepEquals(before,queue.Read().ToJson()["jobs"]![0]!["pending"]));
            Assert.Single(Directory.GetFiles(dir,"batch.json",SearchOption.AllDirectories));
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void KnownStockStartsReadyProductionWithoutInventoryPatrol()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-runtime-"+Guid.NewGuid().ToString("N"));
        try
        {
            var queue=new RuntimeQueue(dir);queue.Edit(q=>{q.Add("iron","production",3,5,"iron",[]);q.Add("wood","production",4,5,"wood",[]);});
            var ports=new RuntimePorts();ports.Stock.Set(new(1,0,"character","packet","v1",true));ports.Stock.Set(new(2,50,"character","packet","v1",true));
            using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());
            host.Start();host.Tick();Assert.Equal("wood",host.Status.ActiveJob);Assert.Equal(0,ports.RefreshRequests);
            var q=queue.Read();Assert.True(q.Jobs.Single(j=>j.Id=="wood").Pending);
            Assert.Single(q.Jobs,j=>j.Id.StartsWith("auto-material:"));
            for(var i=0;i<4;i++)host.Tick();Assert.Equal(0,ports.RefreshRequests);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void UnknownInventoryIsBoundedAndNeverCreatesGatheringAsZero()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-runtime-"+Guid.NewGuid().ToString("N"));
        try
        {
            var queue=new RuntimeQueue(dir);queue.Edit(q=>q.Add("iron","production",3,5,"iron",[]));var ports=new RuntimePorts();
            using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());
            host.Start();for(var i=0;i<10;i++)host.Tick();
            Assert.Equal(1,ports.RefreshRequests);Assert.Single(queue.Read().Jobs);Assert.False(queue.Read().Jobs[0].Pending);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void FailedInventoryRefreshDoesNotRetryOnUnknownVersionOrQueueChangeAndExplicitContinueAllowsRetry()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-runtime-"+Guid.NewGuid().ToString("N"));
        try
        {
            var queue=new RuntimeQueue(dir);queue.Edit(q=>q.Add("iron","production",3,5,"iron",[]));var ports=new RuntimePorts();var time=new ManualTime();
            using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time);
            host.Start();host.Tick();Assert.Equal(1,ports.RefreshRequests);
            host.SuspendForInventorySync();host.FinishInventorySyncWithoutCoverage("map_loaded timeout");
            for(var i=0;i<4;i++){ports.Stock.Set(new(1,null,"character","packet_capture","unknown-"+i,false));host.Tick();}
            queue.Edit(q=>q.Add("wood","production",4,1,"wood",[]));host.SynchronizeQueue();ports.Stock.Set(new(2,10,"character","packet_capture","wood-ready",true));
            host.Tick();Assert.Equal("wood",host.Status.ActiveJob);Assert.Equal(1,ports.RefreshRequests);Assert.Contains("inventory_sync",host.Status.Issues.Keys);
            queue.Edit(q=>q.Delete(["wood"]));host.SynchronizeQueue();host.Pause();for(var i=0;i<3;i++)host.Tick();Assert.False(host.Status.Running);Assert.Equal(1,ports.RefreshRequests);
            host.Continue();host.Tick();Assert.Equal(2,ports.RefreshRequests);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void UnknownInventoryPreservesExistingAutomaticProgress()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-runtime-"+Guid.NewGuid().ToString("N"));
        try
        {
            var queue=new RuntimeQueue(dir);queue.Edit(q=>{q.Add("iron","production",3,5,"iron",[]);q.Add("auto-material:ore","gathering",1,50,"ore",[]);q.Begin("auto-material:ore","a","gathering");q.Confirm("auto-material:ore","a","r",3,"screen");});
            using var host=new AutomationCoordinator(Catalog(),queue,new RuntimePorts(),()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());
            host.Start();host.Tick();var automatic=queue.Read().Jobs.Single(j=>j.Id=="auto-material:ore");
            Assert.False(automatic.Cancelled);Assert.Equal(3,automatic.Completed);Assert.Equal(50,automatic.Target);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void GatheringRefreshPreservesNewStockAndIndependentMaterials()
    {
        var store=new MemoryStore();var inventory=new RuntimeInventory(store);var stock=new InventorySnapshot("character");
        var basis=new StockObservation(1,0,stock.Scope,"packet","v1",true);stock.Set(basis);stock.Set(new(2,50,stock.Scope,"packet","v1",true));
        inventory.RequireFresh("a",basis);inventory.Compact(new HashSet<string>());
        Assert.Null(new RuntimeInventory(store).Apply(stock).Query(1).Quantity);Assert.Equal(50,inventory.Apply(stock).Query(2).Quantity);
        var wrongScope=new InventorySnapshot("other");wrongScope.Set(basis with{Scope="other",Version="v2",Quantity=10});inventory.Apply(wrongScope);
        Assert.Null(inventory.Apply(stock).Query(1).Quantity);
        stock.Set(basis with{Quantity=7,Version="v2"});Assert.Equal(7,inventory.Apply(stock).Query(1).Quantity);
        inventory.RequireFresh("a",basis);Assert.Equal(7,inventory.Apply(stock).Query(1).Quantity);
        inventory.RequireFresh("b",stock.Query(1));stock.Set(basis with{Quantity=8,Source="screen_bag",Version="screen:1"});
        Assert.Equal(8,inventory.Apply(stock).Query(1).Quantity);
    }
    [Fact] public void ContinuousCaptureResolvesBatchRefreshWithoutChangingQuantityOrClearingCraftDebit()
    {
        var store=new MemoryStore();var ledger=new RuntimeInventory(store);var stock=new InventorySnapshot("c");
        var basis=new StockObservation(1,50,"c","packet_capture","v1",true){VerifiedThrough=99};stock.Set(basis);
        ledger.Submitted("craft",1,new("r",3,1,new(){[1]=10},"metal"),stock);ledger.RequireFresh("gather",basis,100);
        Assert.Null(ledger.Apply(stock).Query(1).Quantity);
        stock.Set(basis with{VerifiedThrough=99.9});Assert.Null(ledger.Apply(stock).Query(1).Quantity);
        stock.Set(basis with{VerifiedThrough=100.1});ledger=new RuntimeInventory(store);
        Assert.Equal(40,ledger.Apply(stock).Query(1).Quantity);Assert.Single(store.Read()!["debits"]!.AsObject());Assert.Empty(store.Read()!["refreshes"]!.AsObject());
    }
    [Fact] public void CaptureWatermarkDoesNotReplanOrWakeDeferredWorkByItself()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-watermark-"+Guid.NewGuid().ToString("N"));
        try
        {
            var ports=new RuntimePorts();ports.Stock.Set(new(1,10,"character","packet_capture","v1",true){VerifiedThrough=99});
            using var host=new AutomationCoordinator(Catalog(),new RuntimeQueue(dir),ports,()=>new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());host.Start();host.Tick();var revision=host.Status.PlanRevision;
            ports.Stock.Set(ports.Stock.Query(1) with{VerifiedThrough=101});host.Tick();Assert.Equal(revision,host.Status.PlanRevision);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Theory][InlineData(0)][InlineData(50)]
    public void EndedAutomaticGatheringUsesContinuousStockAndNeverInspectsHistoricalYield(long currentQuantity)
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-live-planning-"+Guid.NewGuid().ToString("N"));
        try
        {
            var queue=new RuntimeQueue(dir);queue.Edit(q=>q.Add("iron","production",3,1,"iron",[]));var ports=new RuntimePorts();var time=new ManualTime();
            ports.Stock.Set(new(1,0,"character","packet_capture","v1",true){VerifiedThrough=99});
            using var host=new AutomationCoordinator(Catalog(),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time);host.Start();host.Tick();host.Tick();
            ports.Gather.Frame=new("scene",RouteKey:"ore",TaskId:JsonValue.Create("batch"),TaskStatus:"active");host.Tick();
            ports.Gather.Frame=ports.Gather.Frame with{TaskStatus="complete",Evidence=new JsonObject{["navigation_idle_confirmed"]=true}};host.Tick();host.Tick();
            Assert.Equal(1,ports.RefreshRequests);
            time.Seconds+=1;ports.Stock.Set(ports.Stock.Query(1) with{Quantity=currentQuantity,VerifiedThrough=101});
            if(currentQuantity==0)ports.Gather.Frame=new("scene",RouteKey:"ore",StartAvailable:true);
            host.Tick();host.Tick();Assert.Equal(1,ports.RefreshRequests);Assert.Null(host.Status.Fault);
            if(currentQuantity>0)Assert.Equal("iron",host.Status.ActiveJob);
            else Assert.Equal(2,ports.Gather.Starts); // confirmed zero starts a new needed batch, never replays the old intent
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void EstimatedGatheringGroupRunsAllRoundsBeforeOneRefresh()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-round-group-"+Guid.NewGuid().ToString("N"));
        try
        {
            var queue=new RuntimeQueue(dir);queue.Edit(q=>q.Add("iron","production",3,3,"iron",[]));var ports=new RuntimePorts();var time=new ManualTime();
            ports.Stock.Set(new(1,0,"character","packet_capture","v1",true){VerifiedThrough=99});
            var config=Catalog().Configuration;config["gathering_routes"]!["ore"]!["reference_yield_per_batch"]=10;
            using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time);host.Start();
            for(var round=1;round<=3;round++)
            {
                ports.Gather.Frame=new("scene",RouteKey:"ore",StartAvailable:true);host.Tick();host.Tick();
                Assert.Equal(round,ports.Gather.Starts);
                ports.Gather.Frame=new("scene",RouteKey:"ore",TaskId:JsonValue.Create("batch"+round),TaskStatus:"active");host.Tick();
                ports.Gather.Frame=ports.Gather.Frame with{TaskStatus="complete",Evidence=new JsonObject{["navigation_idle_confirmed"]=true}};host.Tick();host.Tick();
                Assert.Equal(round==3?1:0,ports.RefreshRequests);
            }
            for(var i=0;i<5;i++)host.Tick();
            Assert.Equal(3,ports.Gather.Starts);Assert.Equal(1,ports.RefreshRequests);Assert.Null(host.Status.Fault);
            Assert.Equal(0,ports.Stock.Query(1).Quantity);
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void NewPacketStockCancelsRemainingEstimatedRoundsAcrossSharedGoals()
    {
        var dir=Path.Combine(Path.GetTempPath(),"mabi-round-cancel-"+Guid.NewGuid().ToString("N"));
        try
        {
            var queue=new RuntimeQueue(dir);queue.Edit(q=>{q.Add("a","production",3,1,"iron",[]);q.Add("b","production",3,2,"iron",[]);});
            var ports=new RuntimePorts();var time=new ManualTime();ports.Stock.Set(new(1,0,"character","packet_capture","v1",true){VerifiedThrough=99});
            var config=Catalog().Configuration;config["gathering_routes"]!["ore"]!["reference_yield_per_batch"]=10;
            using var host=new AutomationCoordinator(new(config),queue,ports,()=>new(RepairEnabled:false,BackpackEnabled:false),time);host.Start();host.Tick();host.Tick();
            Assert.Equal(3,Assert.Single(queue.Read().Jobs,j=>j.Id.StartsWith("auto-material:")).Target);
            ports.Gather.Frame=new("scene",RouteKey:"ore",TaskId:JsonValue.Create("batch"),TaskStatus:"active");host.Tick();
            ports.Stock.Set(new(1,30,"character","packet_capture","v2",true){VerifiedThrough=101});
            ports.Gather.Frame=ports.Gather.Frame with{TaskStatus="complete",Evidence=new JsonObject{["navigation_idle_confirmed"]=true}};
            for(var i=0;i<5;i++)host.Tick();
            Assert.Equal(1,ports.Gather.Starts);Assert.Equal(0,ports.RefreshRequests);Assert.DoesNotContain(queue.Read().Jobs,j=>j.Id.StartsWith("auto-material:")&&!j.Cancelled&&j.Completed<j.Target);Assert.Null(host.Status.Fault);
        }finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    [Fact] public void CraftReservationIsDurableBeforeWorkflowIntent()
    {
        var stock=new InventorySnapshot("c");stock.Set(new(1,50,"c","packet","v1",true));
        var ledger=new RuntimeInventory(new MemoryStore());var inner=new MemoryStore();
        var store=new SubmissionStore(inner,ledger,"a",new("iron",3,1,new(){[1]=10},"metal"),()=>stock);
        store.Flush(new JsonObject{["submitted"]=0,["pending"]=new JsonObject{["kind"]="craft"}});
        Assert.NotNull(inner.Read()!["pending"]);Assert.Equal(40,ledger.Apply(stock).Query(1).Quantity);
    }
    [Fact] public void SubmittedMaterialIsReservedUntilSameMaterialVersionChanges()
    {
        var store=new MemoryStore();var ledger=new RuntimeInventory(store);var bag=new InventorySnapshot("character");
        bag.Set(new(1,50,bag.Scope,"packet","v1",true));bag.Set(new(2,20,bag.Scope,"packet","v1",true));
        var recipe=new RecipeDefinition("r",3,1,new(){[1]=10},"metal");
        ledger.Submitted("a",1,recipe,bag);ledger.Submitted("a",1,recipe,bag);
        Assert.Equal(40,ledger.Apply(bag).Query(1).Quantity);
        bag.Set(new(2,30,bag.Scope,"packet","v2",true));
        Assert.Equal(40,new RuntimeInventory(store).Apply(bag).Query(1).Quantity);
        bag.Set(new(1,40,bag.Scope,"packet","v2",true));Assert.Equal(40,ledger.Apply(bag).Query(1).Quantity);
        ledger.Submitted("a",1,recipe,bag);Assert.Equal(40,new RuntimeInventory(store).Apply(bag).Query(1).Quantity);
    }
    [Fact] public void RuntimeCatalogUsesAllMaterialRowsAndExactLongIds()
    {
        var catalog=new RuntimeCatalog(JsonNode.Parse("""
        {"item_names":{"4560776048983186":"绳索"},"recipes":{"rope":{"output_item_id":4560776048983186,"output_per_round":3,"device":"fabric","duration_s":90,"materials":[{"item_id":1,"quantity":2},{"item_id":2,"quantity":5}]}},"gathering_routes":{"fiber":{"target_item_id":1,"skill":"gathering","node":"fiber","attempts_per_batch":10}}}
        """)!.AsObject());
        Assert.Equal(4560776048983186,catalog.Recipes["rope"].OutputItemId);Assert.Equal(2,catalog.Recipes["rope"].Inputs.Count);
        Assert.Equal(10,catalog.Gathering["fiber"].AttemptsPerBatch);
        var edited=catalog.Configuration;
        edited["gathering_routes"]!["fiber"]!["reference_yield_per_batch"]=4;
        edited["recipes"]!["rope"]!["output_per_round"]=3;
        var rebuilt=new RuntimeCatalog(edited);
        Assert.Equal(4L,rebuilt.Gathering["fiber"].ReferenceYieldPerBatch);
        Assert.Equal(4560776048983186,rebuilt.Recipes["rope"].OutputItemId);
    }

    [Fact]
    public void PlannerSharesProductionRoundsAcrossGoalsAndUsesConfirmedStockFirst()
    {
        var planner = new MaterialPlanner(
            [new RecipeDefinition("ingot", 2, 3, new() { [1] = 10 }, "forge")],
            gatheringSources: new Dictionary<long, string> { [1] = "ore" },
            routes: new Dictionary<string, long> { ["ore"] = 1 });
        var stock = new InventorySnapshot("character");
        stock.Set(new(1, 10, stock.Scope, "packet_capture", "v1", true));

        var plan = planner.Plan(
            [new MaterialGoal("a", 2, 3), new MaterialGoal("b", 2, 3)], stock);

        var task = Assert.Single(plan.Tasks, t => t.Kind == "production");
        Assert.Equal(2, task.Rounds);
        Assert.Equal(1, task.ReadyRounds);
        Assert.Equal(6, task.OutputQuantity);
        Assert.Equal(2, plan.Goals.Length);
        Assert.Equal(10, plan.Allocations.Where(a => a.SourceKind == "inventory").Sum(a => a.Quantity));
    }

    [Fact]
    public void RuntimeInventoryDoesNotDoubleApplyUnknownConsumptionAfterRefresh()
    {
        var store = new MemoryStore();
        var ledger = new RuntimeInventory(store);
        var basis = new InventorySnapshot("character");
        basis.Set(new(1, 20, basis.Scope, "packet_capture", "v1", true));
        var recipe = new RecipeDefinition("craft", 2, 1, new() { [1] = 10 }, "forge");

        ledger.Submitted("batch", 1, recipe, basis);
        Assert.Equal(10, ledger.Apply(basis).Query(1).Quantity);

        basis.Set(new(1, 10, basis.Scope, "packet_capture", "v2", true));
        Assert.Equal(10, ledger.Apply(basis).Query(1).Quantity);
        Assert.Empty(store.Read()!["debits"]!.AsObject());
    }
    private static RuntimeCatalog Catalog()=>new(JsonNode.Parse("""
    {"item_names":{"1":"矿石","2":"原木","3":"铁锭","4":"木材"},"recipes":{"iron":{"output_item_id":3,"output_per_round":1,"device":"metal","duration_s":60,"materials":[{"item_id":1,"quantity":10}]},"wood":{"output_item_id":4,"output_per_round":1,"device":"wood","duration_s":60,"materials":[{"item_id":2,"quantity":10}]}},"gathering_routes":{"ore":{"target_item_id":1,"skill":"mining","node":"ore","attempts_per_batch":10}}}
    """)!.AsObject());
}
internal sealed class RuntimePorts:IAutomationPorts
{
    public string Scope=>"character";public string? CurrentDevice=>null;public InventorySnapshot Stock=new("character");public int RefreshRequests;
    public bool PreferBulkInventorySync=>true;public void RequestInventorySync(){RefreshRequests++;BeginQuickReentry(DateTimeOffset.UtcNow);}
    public void BeginQuickReentry(DateTimeOffset now){}
    public void Start(){}public void Continue(){}public void Pause(string reason){}public void Dispose(){}
    public InventorySnapshot Inventory()=>Stock;
    public IProductionPort Production(string recipe,string batch,IWorkflowStore store)=>new ProductionPort{Frame=new("scene")};
    public readonly GatheringPort Gather=new();
    public IGatheringPort Gathering(string route,string batch,IWorkflowStore store)=>Gather;
    public IStorageSupplyPort Storage(WithdrawalPlan plan,string batch,IWorkflowStore store)=>new StoragePort();
    public readonly FishingPort Fish=new();
    public IFishingPort Fishing(string point,string batch,IWorkflowStore store)=>Fish;
    public readonly MaintenancePort Maintain=new(){Frame=new("1","scene")};
    public IMaintenancePort Maintenance(string batch,IWorkflowStore store)=>Maintain;
}



