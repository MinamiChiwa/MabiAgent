using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Workflows.Tests;
public sealed class MaintenanceTests
{
    [Theory][InlineData(true)][InlineData(false)]
    public void LegacyBlockedBackpackPreservesOnlyObservedCapacityRestriction(bool overweight)
    {
        var store=new MemoryStore();var port=new MaintenancePort();var time=new ManualTime();
        var original=new MaintenanceRunner(port,store,new(RepairEnabled:false),time);
        var saved=original.Snapshot();saved["backpack"]!["status"]="blocked";
        saved["backpack"]!["phase"]="prepare";saved["backpack"]!.AsObject().Remove("capacity_blocked");
        saved["backpack"]!["weight"]=overweight?95:20;saved["backpack"]!["capacity"]=100;
        store.Value=saved;
        var restored=new MaintenanceRunner(port,store,new(RepairEnabled:false),time);
        Assert.Equal(overweight,restored.Blocked("gathering"));Assert.False(restored.Blocked("production"));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void FailedBackpackInspectionReleasesCleanupEvenWhenPageStaysUnknown(bool stable)
    {
        var time=new ManualTime();var port=new MaintenancePort{Frame=new("scene","scene")};
        var runner=new MaintenanceRunner(port,new MemoryStore(),new(RepairEnabled:false,BackpackCheckIntervalSeconds:1),time);
        time.Seconds+=1;runner.Tick();port.Frame=new("missing","unknown",Stable:stable);runner.Tick();
        time.Seconds+=121;runner.Tick();Assert.Equal("backpack_progress_timeout",runner.Snapshot()["reason"]!.GetValue<string>());
        runner.Tick();time.Seconds+=16;runner.Tick();
        Assert.False(runner.StatusSnapshot()["navigation_reserved"]!.GetValue<bool>());
        Assert.False(runner.Snapshot()["global_pause_required"]!.GetValue<bool>());
        Assert.False(runner.Blocked("gathering"));Assert.False(runner.Blocked("production"));
        Assert.Null(runner.Snapshot()["pending"]);Assert.Empty(port.Actions);
    }
    private sealed class WaitingSurplus:ISurplusMaintenancePort
    {
        public int Begins,Ticks;public string Result="running";
        public string Begin(){Begins++;return "running";}public string Tick(){Ticks++;return Result;}public string Reconcile()=>"running";
        public System.Text.Json.Nodes.JsonObject Snapshot()=>new(){["status"]="running"};
    }
    [Fact] public void TidyPrecedesProductionAndSaleAndIsNotRepeatedAfterRelief()
    {
        var time=new ManualTime();var port=new MaintenancePort{Frame=new("1","backpack",Weight:95,Capacity:100)};
        var sale=new WaitingSurplus{Result="complete"};var runner=new MaintenanceRunner(port,new MemoryStore(),new(RepairEnabled:false,ProductionFirst:true),time,surplus:sale);
        runner.Tick();Assert.Equal("prepare",runner.Snapshot()["backpack"]!["phase"]!.GetValue<string>());Assert.Equal(0,sale.Begins);Assert.Equal(1,port.Navigations);
        port.Frame=new("2","tidy",BoldSelected:false,MaterialsSelected:false,TidyCount:3);runner.Tick();
        port.Frame=new("3","tidy_confirm",ConfirmAvailable:true);runner.Tick();Assert.Equal(0,sale.Begins);
        port.Frame=new("4","backpack",Weight:95,Capacity:100,TidyComplete:true);runner.Tick();
        Assert.Equal("production_relief",runner.Snapshot()["backpack"]!["status"]!.GetValue<string>());Assert.Null(runner.Snapshot()["pending"]);
        runner.ProductionReliefExhausted();port.Frame=new("5","backpack",Weight:95,Capacity:100);runner.Tick();Assert.Equal(1,sale.Begins);
        port.Frame=port.Frame with{CaptureId="6"};runner.Tick();port.Frame=port.Frame with{CaptureId="7"};runner.Tick();
        Assert.Equal("backpack_relief_exhausted",runner.Snapshot()["reason"]!.GetValue<string>());
        Assert.Single(port.Actions,a=>a=="confirm_tidy");Assert.Equal(1,sale.Begins);
    }
    [Fact] public void EmptyProtectedTidyIsCheckedBeforeMaterialSale()
    {
        var port=new MaintenancePort{Frame=new("1","backpack",Weight:95,Capacity:100)};var sale=new WaitingSurplus();
        var runner=new MaintenanceRunner(port,new MemoryStore(),new(RepairEnabled:false),new ManualTime(),surplus:sale);
        runner.Tick();Assert.Equal(0,sale.Begins);
        port.Frame=new("2","tidy",BoldSelected:false,MaterialsSelected:false,TidyCount:0);runner.Tick();Assert.Equal(0,sale.Begins);
        runner.ProductionReliefExhausted();port.Frame=new("3","backpack",Weight:95,Capacity:100);runner.Tick();Assert.Equal(1,sale.Begins);Assert.Empty(port.Actions);
    }
    [Fact] public void LegacyUnsentSurplusReturnsToTidyBeforeResumingSale()
    {
        var store=new MemoryStore();var port=new MaintenancePort{Frame=new("1","backpack",Weight:95,Capacity:100)};var sale=new WaitingSurplus();
        var policy=new MaintenancePolicy(RepairEnabled:false);var time=new ManualTime();var seed=new MaintenanceRunner(port,store,policy,time).Snapshot();
        seed["active"]="backpack";seed["backpack"]!["phase"]="surplus";seed["backpack"]!["status"]="running";store.Flush(seed);
        var runner=new MaintenanceRunner(port,store,policy,time,surplus:sale);runner.Resume(true);runner.Tick();
        Assert.Equal("prepare",runner.Snapshot()["backpack"]!["phase"]!.GetValue<string>());Assert.Equal(0,sale.Begins);Assert.Equal(0,sale.Ticks);Assert.Equal(1,port.Navigations);
    }
    [Fact] public void SurplusSearchOutlivesOrdinaryPageWaitButRemainsBounded()
    {
        var time=new ManualTime();var store=new MemoryStore();var port=new MaintenancePort{Frame=new("1","backpack",Weight:95,Capacity:100)};
        var policy=new MaintenancePolicy(RepairEnabled:false);var seed=new MaintenanceRunner(port,store,policy,time).Snapshot();
        seed["active"]="backpack";seed["backpack"]!["phase"]="surplus";seed["backpack"]!["status"]="running";seed["backpack"]!["tidy_checked"]=true;store.Flush(seed);
        var runner=new MaintenanceRunner(port,store,policy,time,surplus:new WaitingSurplus());runner.Resume(true);runner.Tick();
        time.Seconds+=121;runner.Tick();Assert.Equal("running",runner.Snapshot()["backpack"]!["status"]!.GetValue<string>());
        time.Seconds+=210;runner.Tick();Assert.Equal("backpack_progress_timeout",runner.Snapshot()["reason"]!.GetValue<string>());Assert.Null(runner.Snapshot()["pending"]);
    }
    [Theory][InlineData("inspect","scene")][InlineData("prepare","backpack")][InlineData("relief_weight","scene")]
    public void BackpackNavigationFailureIsReportedAndLocallyBlocked(string phase,string page)
    {
        var store=new MemoryStore();var policy=new MaintenancePolicy(RepairEnabled:false);var time=new ManualTime();var seed=new MaintenanceRunner(new MaintenancePort(),store,policy,time).Snapshot();
        seed["active"]="backpack";seed["backpack"]!["status"]="running";seed["backpack"]!["phase"]=phase;seed["backpack"]!["cleanup_after_relief"]=true;seed["backpack"]!["surplus_checked"]=true;store.Flush(seed);
        var port=new MaintenancePort{Frame=new("new",page,Weight:95,Capacity:100),NavigationFailure="navigation_target_unstable:covered"};var runner=new MaintenanceRunner(port,store,policy,time);runner.Resume(true);runner.Tick();
        Assert.Equal("blocked",runner.Snapshot()["backpack"]!["status"]!.GetValue<string>());Assert.Equal(port.NavigationFailure,runner.Snapshot()["reason"]!.GetValue<string>());Assert.False(runner.Snapshot()["global_pause_required"]!.GetValue<bool>());Assert.Null(runner.Snapshot()["pending"]);Assert.Empty(port.Actions);Assert.Equal(1,port.Navigations);
    }
    [Fact] public void TidyPreparationWaitsForDelayedConfirmationWithoutReturningOrRepreparing()
    {
        var port=new MaintenancePort{Frame=new("1","backpack",Weight:95,Capacity:100)};var runner=new MaintenanceRunner(port,new MemoryStore(),new(RepairEnabled:false),new ManualTime());runner.Tick();
        port.Frame=new("2","tidy",BoldSelected:false,MaterialsSelected:false,TidyCount:3);runner.Tick();var actions=port.Actions.Count;
        port.Frame=port.Frame with{CaptureId="3"};runner.Tick();Assert.Equal("confirm",runner.Snapshot()["backpack"]!["phase"]!.GetValue<string>());Assert.Equal(actions,port.Actions.Count);
        port.Frame=new("4","tidy_confirm",ConfirmAvailable:true);runner.Tick();Assert.NotNull(runner.Snapshot()["pending"]);Assert.Equal(actions+1,port.Actions.Count);
    }
    [Fact] public void VerifiedWeightAfterSurplusReleasesBagWithoutEmptyTidyRead()
    {
        var store=new MemoryStore();var seed=new MaintenanceRunner(new MaintenancePort(),store,new(RepairEnabled:false),new ManualTime()).Snapshot();
        seed["active"]="backpack";seed["backpack"]!["phase"]="prepare";seed["backpack"]!["status"]="running";seed["backpack"]!["surplus_checked"]=true;seed["backpack"]!["cleanup_after_relief"]=true;store.Flush(seed);
        var port=new MaintenancePort{Frame=new("new","backpack",Weight:70,Capacity:100)};
        var runner=new MaintenanceRunner(port,store,new(RepairEnabled:false),new ManualTime());runner.Resume(true);runner.Tick();
        Assert.Equal("idle",runner.Snapshot()["backpack"]!["status"]!.GetValue<string>());Assert.Null(runner.Snapshot()["reason"]);Assert.Empty(port.Actions);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void NewRunDefersBackpackInspectionWithoutForgingWeightAndContinueKeepsDeadline(bool savedWeight)
    {
        var time=new ManualTime();var store=new MemoryStore();var port=new MaintenancePort{Frame=new("1","scene")};
        var policy=new MaintenancePolicy(RepairEnabled:false,BackpackCheckIntervalSeconds:120);
        var runner=new MaintenanceRunner(port,store,policy,time);
        if(savedWeight)runner.ObserveBackpack(new("old","backpack",Weight:20,Capacity:100));
        var checkedAt=runner.Snapshot()["backpack"]!["checked_at"]?.ToJsonString();
        time.Seconds=500;runner=new(port,store,policy,time);runner.RestartBackpackSchedule();runner.Tick();
        Assert.Equal(620d,runner.Snapshot()["backpack"]!["check_due_at"]!.GetValue<double>());
        Assert.Equal(checkedAt,runner.Snapshot()["backpack"]!["checked_at"]?.ToJsonString());Assert.Equal(0,port.Navigations);
        time.Seconds=610;runner.Pause();runner.Tick();Assert.Equal("manual",runner.Snapshot()["paused"]!.GetValue<string>());
        runner.Resume(true);runner.Tick();Assert.Equal(620d,runner.Snapshot()["backpack"]!["check_due_at"]!.GetValue<double>());
        time.Seconds=620;runner.Tick();Assert.Equal(1,port.Navigations);Assert.Equal("inspect",runner.Snapshot()["backpack"]!["phase"]!.GetValue<string>());
    }
    [Fact] public void RestartBackpackSchedulePreservesRealFullCleanupAndUnknownSale()
    {
        var time=new ManualTime();var store=new MemoryStore();var port=new MaintenancePort{Frame=new("1","scene",BackpackFull:true)};
        var runner=new MaintenanceRunner(port,store,new(RepairEnabled:false,BackpackCheckIntervalSeconds:120),time);
        runner.Tick(false);runner.RestartBackpackSchedule();Assert.Equal("prepare",runner.Snapshot()["backpack"]!["phase"]!.GetValue<string>());
        port.Frame=new("2","tidy",BoldSelected:false,MaterialsSelected:false,TidyCount:4);runner.Tick();
        port.Frame=new("3","tidy_confirm",ConfirmAvailable:true);runner.Tick();var pending=runner.Snapshot()["pending"]!.ToJsonString();
        time.Seconds+=100;runner=new(port,store,new(RepairEnabled:false,BackpackCheckIntervalSeconds:120),time);runner.RestartBackpackSchedule();runner.Resume(true);runner.Tick();
        Assert.Equal(pending,runner.Snapshot()["pending"]!.ToJsonString());Assert.Single(port.Actions,a=>a=="confirm_tidy");
    }
    [Fact] public void BusyNativeRoundDefersIdleMaintenanceRecognitionButKeepsDueRepair()
    {
        var time=new ManualTime();var port=new MaintenancePort();var runner=new MaintenanceRunner(port,new MemoryStore(),new(IntervalSeconds:1),time);
        time.Seconds+=2;runner.Tick(false,false,deferIdleObservation:true);
        Assert.Equal(0,port.Observations);Assert.Empty(port.Actions);Assert.True(runner.RequiresTurn);
        runner.Tick(true,false,deferIdleObservation:true);Assert.Equal(1,port.Observations);Assert.Single(port.Actions);
        runner.Tick(false,false,deferIdleObservation:true);Assert.Equal(2,port.Observations);Assert.Single(port.Actions);Assert.NotNull(runner.Snapshot()["pending"]);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void NewRunRestartsUnsentRepairIntervalButContinueKeepsIt(bool blocked)
    {
        var time=new ManualTime();var port=new MaintenancePort();var store=new MemoryStore();var policy=new MaintenancePolicy(IntervalSeconds:30,BackpackEnabled:false);
        var runner=new MaintenanceRunner(port,store,policy,time);var state=runner.Snapshot();state["repair"]!["due_at"]=1d;
        if(blocked){state["repair"]!["status"]="blocked";state["repair"]!["phase"]="quote";state["reason"]="repair_quote_unknown";}
        store.Flush(state);time.Seconds=500;runner=new(port,store,policy,time);runner.RestartRepairSchedule();
        Assert.Equal(530d,runner.Snapshot()["repair"]!["due_at"]!.GetValue<double>());Assert.False(runner.RepairRequired);Assert.Null(runner.Snapshot()["reason"]);
        time.Seconds=510;runner.Pause();runner.Resume(true);Assert.Equal(530d,runner.Snapshot()["repair"]!["due_at"]!.GetValue<double>());
    }
    [Fact] public void NewIntervalPreservesUnresolvedPaymentAndNeverReplaysIt()
    {
        var time=new ManualTime();var port=new MaintenancePort();var store=new MemoryStore();var policy=new MaintenancePolicy(IntervalSeconds:1,BackpackEnabled:false);
        var runner=new MaintenanceRunner(port,store,policy,time);time.Seconds+=2;runner.Tick();var pending=runner.Snapshot()["pending"]!.ToJsonString();
        time.Seconds+=100;runner=new(port,store,policy,time);runner.RestartRepairSchedule();runner.Resume(true);runner.Tick();
        Assert.Equal(pending,runner.Snapshot()["pending"]!.ToJsonString());Assert.Single(port.Actions,a=>a=="confirm_repair");
    }
    [Fact] public void DisabledBackpackMaintenanceIgnoresFullToast()
    {
        var port=new MaintenancePort{Frame=new("1","scene",BackpackFull:true)};var runner=new MaintenanceRunner(port,new MemoryStore(),new(RepairEnabled:false,BackpackEnabled:false),new ManualTime());
        runner.Tick();Assert.Null(runner.Snapshot()["reason"]);Assert.Equal("idle",runner.Snapshot()["backpack"]!["status"]!.GetValue<string>());Assert.Equal(0,port.Navigations);
    }
    [Fact] public void SuppressedPeriodicCheckDoesNotOpenBackpackOnStartElapsedIntervalOrResume()
    {
        var time = new ManualTime(); var port = new MaintenancePort { Frame = new("1", "scene") };
        var runner = new MaintenanceRunner(port, new MemoryStore(), new(RepairEnabled: false, BackpackCheckIntervalSeconds: 120), time);
        runner.Tick(allowPeriodicBackpackCheck: false);
        time.Seconds += 241; runner.Tick(allowPeriodicBackpackCheck: false);
        runner.Pause(); runner.Resume(true); runner.Tick(allowPeriodicBackpackCheck: false);
        Assert.False(runner.RequiresTurn); Assert.Equal(0, port.Navigations); Assert.Empty(port.Actions);
        runner.Tick(); Assert.True(runner.RequiresTurn); Assert.Equal(1, port.Navigations);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void SuppressionCancelsSavedInspectionAndOnlyClosesItsOpenedPage(bool opened)
    {
        var time = new ManualTime(); var store = new MemoryStore(); var port = new MaintenancePort { Frame = new("1", "scene") };
        var policy = new MaintenancePolicy(RepairEnabled: false, BackpackCheckIntervalSeconds: 120);
        var runner = new MaintenanceRunner(port, store, policy, time);
        time.Seconds+=120;runner.Tick(opened); var navigations = port.Navigations;
        port.Frame = new("2", opened ? "backpack" : "scene");
        runner = new MaintenanceRunner(port, store, policy, time); runner.Resume(true);
        runner.Tick(allowPeriodicBackpackCheck: false);
        Assert.False(runner.RequiresTurn); Assert.Null(runner.Snapshot()["active"]); Assert.Equal(navigations, port.Navigations);
        Assert.Equal(opened ? 1 : 0, port.Exits); Assert.Empty(port.Actions);
        port.Frame = new("3", "scene"); runner.Tick(allowPeriodicBackpackCheck: false);
        Assert.False(runner.StatusSnapshot()["navigation_reserved"]!.GetValue<bool>());
    }
    [Fact] public void SuppressionReleasesFailedPeriodicInspection()
    {
        var time = new ManualTime(); var port = new MaintenancePort { Frame = new("1", "backpack") };
        var runner = new MaintenanceRunner(port, new MemoryStore(), new(RepairEnabled: false, ReadAttempts: 1, BackpackCheckIntervalSeconds: 120), time);
        time.Seconds+=120;runner.Tick(); Assert.False(runner.Snapshot()["global_pause_required"]!.GetValue<bool>());Assert.Equal("blocked",runner.Snapshot()["backpack"]!["status"]!.GetValue<string>());
        runner.Tick(allowPeriodicBackpackCheck: false);
        Assert.False(runner.RequiresTurn); Assert.False(runner.Snapshot()["global_pause_required"]!.GetValue<bool>());
        Assert.Empty(port.Actions);
    }
    [Fact] public void ActualBackpackFullStillTriggersCleanupWhenPeriodicChecksAreSuppressed()
    {
        var port = new MaintenancePort { Frame = new("1", "scene", BackpackFull: true) };
        var runner = new MaintenanceRunner(port, new MemoryStore(), new(RepairEnabled: false, BackpackCheckIntervalSeconds: 120), new ManualTime());
        runner.Tick(false, false); Assert.True(runner.RequiresTurn); Assert.Equal(0, port.Navigations);
        runner.Tick(allowPeriodicBackpackCheck: false);
        Assert.Equal("prepare", runner.Snapshot()["backpack"]!["phase"]!.GetValue<string>()); Assert.Equal(1, port.Navigations);
    }
    [Fact] public void SuppressionKeepsUnresolvedCleanupAndNeverReplaysSale()
    {
        var port = new MaintenancePort { Frame = new("1", "backpack", Weight: 95, Capacity: 100) };
        var time = new ManualTime();
        var runner = new MaintenanceRunner(port, new MemoryStore(), new(RepairEnabled: false), time);
        runner.Tick(allowPeriodicBackpackCheck: false);
        port.Frame = new("2", "tidy", BoldSelected: false, MaterialsSelected: false, TidyCount: 4); runner.Tick(allowPeriodicBackpackCheck: false);
        port.Frame = new("3", "tidy_confirm", ConfirmAvailable: true); runner.Tick(allowPeriodicBackpackCheck: false);
        var pending = runner.Snapshot()["pending"]!.ToJsonString();
        time.Seconds += 16; port.Frame = new("4", "scene"); runner.Tick(allowPeriodicBackpackCheck: false);
        Assert.True(runner.Snapshot()["global_pause_required"]!.GetValue<bool>());
        Assert.Equal(pending, runner.Snapshot()["pending"]!.ToJsonString()); Assert.Single(port.Actions, a => a == "confirm_tidy");
        port.Frame = new("5", "backpack", Weight: 20, Capacity: 100, TidyComplete: true); runner.Tick(allowPeriodicBackpackCheck: false);
        Assert.Null(runner.Snapshot()["pending"]); Assert.False(runner.RequiresTurn);
    }
    [Fact] public void RetryAfterNoRepairNeededKeepsEquipmentPhaseInsteadOfRepeatingRepairNavigation()
    {
        var time=new ManualTime();var port=new MaintenancePort{Frame=new("1","repair_equipment",RepairNotNeeded:true)};
        var runner=new MaintenanceRunner(port,new MemoryStore(),new(BackpackEnabled:false,IntervalSeconds:1),time);time.Seconds+=2;
        port.NavigationFailure="navigation_result_not_observed:dialog.end";runner.Tick();
        Assert.Equal("blocked",runner.Snapshot()["repair"]!["status"]!.GetValue<string>());
        port.NavigationFailure=null;port.Frame=new("2","scene");runner.Resume(true);runner.Tick();
        Assert.Equal("equip",runner.Snapshot()["repair"]!["phase"]!.GetValue<string>());
        port.Frame=new("3","repair_tools");runner.Tick();Assert.Equal(new[]{"auto_equip"},port.Actions);
        port.Frame=new("4","repair_tools",ToolsEquipped:true);runner.Tick();
        Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("idle",runner.Snapshot()["repair"]!["status"]!.GetValue<string>());
    }
    [Fact] public void DialoguePresentationDoesNotResolvePaymentWithAnUnchangedBalance()
    {
        var time=new ManualTime();var port=new MaintenancePort();var runner=new MaintenanceRunner(port,new MemoryStore(),new(BackpackEnabled:false,IntervalSeconds:1),time);time.Seconds+=2;
        runner.Tick();Assert.NotNull(runner.Snapshot()["pending"]);
        port.Frame=new("2","repair_result",RepairComplete:true);runner.Tick();
        port.Frame=new("3","repair_tools",Gold:100000);runner.Tick();
        Assert.NotNull(runner.Snapshot()["pending"]);Assert.Equal("repair_payment_amount_mismatch",runner.Snapshot()["reason"]!.GetValue<string>());
        Assert.Single(port.Actions,a=>a=="confirm_repair");
    }
    [Fact] public void ExplicitRetryAfterPageRecognitionTimeoutRechecksToolTabWithoutTidying()
    {
        var time=new ManualTime();var port=new MaintenancePort{Frame=new("1","scene")};
        var runner=new MaintenanceRunner(port,new MemoryStore(),new(RepairEnabled:false,BackpackCheckIntervalSeconds:1),time);
        time.Seconds+=1;runner.Tick();port.Frame=new("2","unknown",Stable:false);runner.Tick();time.Seconds+=121;runner.Tick();
        Assert.Equal("backpack_progress_timeout",runner.Snapshot()["reason"]!.GetValue<string>());
        port.Frame=new("3","repair_tools");runner.Resume(true);runner.Tick();
        Assert.False(runner.Snapshot()["global_pause_required"]!.GetValue<bool>());Assert.Empty(port.Actions);
        port.Frame=new("4","repair_tools",Weight:68428,Capacity:81000);runner.Tick();
        Assert.False(runner.RequiresTurn);Assert.Equal("idle",runner.Snapshot()["backpack"]!["status"]!.GetValue<string>());Assert.Empty(port.Actions);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void SimulatedCleanupStillOverweightNeverRepeatsUnconfirmedOrCompletedSale(bool resultSeen)
    {
        var port=new MaintenancePort{Frame=new("1","backpack",Weight:80191,Capacity:81000)};
        var runner=new MaintenanceRunner(port,new MemoryStore(),new(RepairEnabled:false,WeightThresholdPercent:99),new ManualTime());runner.Tick();
        port.Frame=new("2","tidy",BoldSelected:false,MaterialsSelected:false,TidyCount:4);runner.Tick();
        port.Frame=new("3","tidy_confirm",ConfirmAvailable:true);runner.Tick();
        port.Frame=new("4","repair_tools",Weight:80191,Capacity:81000,TidyComplete:resultSeen);runner.Tick();
        for(var i=5;i<9;i++){port.Frame=port.Frame with{CaptureId=i.ToString()};runner.Tick();}
        Assert.Single(port.Actions,a=>a=="confirm_tidy");
        if(resultSeen){Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("production_relief",runner.Snapshot()["backpack"]!["status"]!.GetValue<string>());}
        else Assert.NotNull(runner.Snapshot()["pending"]);
    }
    [Theory][InlineData("backpack",80189L,false)][InlineData("repair_tools",80189L,false)]
    [InlineData("backpack",80190L,false)][InlineData("repair_tools",80190L,false)]
    [InlineData("backpack",80191L,true)][InlineData("repair_tools",80191L,true)]
    public void SimulatedWeightTriggersCleanupAboveConfiguredThresholdOnEveryBackpackTab(string page,long weight,bool over)
    {
        var port=new MaintenancePort{Frame=new("1",page,Weight:weight,Capacity:81000)};
        var time=new ManualTime();var runner=new MaintenanceRunner(port,new MemoryStore(),new(RepairEnabled:false,WeightThresholdPercent:99,BackpackCheckIntervalSeconds:1),time);
        time.Seconds+=1;runner.Tick();Assert.Equal(over?"prepare":"idle",runner.Snapshot()["backpack"]!["phase"]!.GetValue<string>());
        Assert.Equal(0,port.Navigations);Assert.Empty(port.Actions);
        if(!over)return;
        port.Frame=new("2","tidy",BoldSelected:true,MaterialsSelected:true,TidyCount:4);runner.Tick();
        port.Frame=port.Frame with{CaptureId="3",BoldSelected=false};runner.Tick();
        port.Frame=port.Frame with{CaptureId="4",MaterialsSelected=false};runner.Tick();
        port.Frame=new("5","tidy_confirm",ConfirmAvailable:true);runner.Tick();
        Assert.Equal(new[]{"disable_bold","deselect_materials","prepare_tidy","confirm_tidy"},port.Actions);
        Assert.NotNull(runner.Snapshot()["pending"]);
        port.Frame=new("6","tidy_result",TidyComplete:true);runner.Tick();
        port.Frame=new("7",page,Weight:68428,Capacity:81000);runner.Tick();
        Assert.Null(runner.Snapshot()["pending"]);Assert.False(runner.RequiresTurn);
        port.Frame=new("8","scene");runner.Tick();Assert.False(runner.StatusSnapshot()["navigation_reserved"]!.GetValue<bool>());
        Assert.Single(port.Actions,a=>a=="confirm_tidy");
    }
    [Fact] public void ExplicitRetryOfUnknownWeightReopensInspectionWithoutUsingSavedWeightOrTidying()
    {
        var time=new ManualTime();var port=new MaintenancePort{Frame=new("1","backpack")};var runner=new MaintenanceRunner(port,new MemoryStore(),new(RepairEnabled:false,BackpackCheckIntervalSeconds:1),time);time.Seconds+=1;
        for(var i=1;i<=3;i++){port.Frame=port.Frame with{CaptureId=i.ToString()};runner.Tick();}
        Assert.False(runner.Snapshot()["global_pause_required"]!.GetValue<bool>());Assert.Equal("blocked",runner.Snapshot()["backpack"]!["status"]!.GetValue<string>());Assert.False(runner.Blocked("production"));Assert.Empty(port.Actions);
        port.Frame=new("retry","scene");runner.Resume(true);runner.Tick();
        Assert.False(runner.Snapshot()["global_pause_required"]!.GetValue<bool>());Assert.Equal("inspect",runner.Snapshot()["backpack"]!["phase"]!.GetValue<string>());Assert.True(port.Navigations>0);Assert.Empty(port.Actions);
        port.Frame=new("fresh","backpack",Weight:68428,Capacity:81000);runner.Tick();
        Assert.Equal("idle",runner.Snapshot()["backpack"]!["status"]!.GetValue<string>());Assert.Empty(port.Actions);
    }
    [Fact] public void RepairRejectedBeforeInputRechecksQuoteInsteadOfWaitingForPayment()
    {
        var time=new ManualTime();var port=new MaintenancePort{Error=new InputRejectedBeforeDispatchException("changed")};var runner=new MaintenanceRunner(port,new MemoryStore(),new(BackpackEnabled:false,IntervalSeconds:1),time);time.Seconds+=2;
        runner.Tick();Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("quote",runner.Snapshot()["repair"]!["phase"]!.GetValue<string>());
        port.Error=null;port.Frame=port.Frame with{CaptureId="2"};runner.Tick();Assert.Equal("confirm_repair",Assert.Single(port.Actions));Assert.NotNull(runner.Snapshot()["pending"]);
    }
    [Fact] public void GreyToolsEntrySkipsPaymentAndContinuesEquipment()
    {
        var time=new ManualTime();var port=new MaintenancePort{Frame=new("1","repair_equipment",RepairNotNeeded:true)};
        var runner=new MaintenanceRunner(port,new MemoryStore(),new(BackpackEnabled:false,IntervalSeconds:1),time);time.Seconds+=2;
        runner.Tick();Assert.Empty(port.Actions);Assert.Equal("equip",runner.Snapshot()["repair"]!["phase"]!.GetValue<string>());
        port.Frame=new("2","repair_tools");runner.Tick();Assert.Equal(new[]{"auto_equip"},port.Actions);
    }
    [Fact] public void ZeroQuoteSkipsPaymentAndContinuesEquipment()
    {
        var time=new ManualTime();var port=new MaintenancePort{Frame=new("zero","repair_quote",Gold:100000,Quote:0,Currency:"gold",ConfirmAvailable:true)};
        var runner=new MaintenanceRunner(port,new MemoryStore(),new(BackpackEnabled:false,IntervalSeconds:1),time);time.Seconds+=2;runner.Tick();
        Assert.Empty(port.Actions);Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("equip",runner.Snapshot()["repair"]!["phase"]!.GetValue<string>());
    }
    [Fact] public void CurrentQuoteAuthorizesPaymentWithoutPreviewRoundTrip()
    {
        var time=new ManualTime();var port=new MaintenancePort();var runner=new MaintenanceRunner(port,new MemoryStore(),new(BackpackEnabled:false,IntervalSeconds:1),time);time.Seconds+=2;
        port.Frame=new("current","repair_quote",Gold:609136,Quote:1000,Currency:"gold",ConfirmAvailable:true);runner.Tick();
        Assert.Equal("confirm_repair",Assert.Single(port.Actions));Assert.NotNull(runner.Snapshot()["pending"]);Assert.Equal(1,runner.Snapshot()["repair"]!["quote_reads"]!.GetValue<int>());Assert.Equal(0,port.Navigations);
    }
    [Theory][InlineData(19000L,true)][InlineData(20000L,true)][InlineData(20001L,false)][InlineData(0L,false)]
    public void RepairUpperBoundUsesCompletedResultAndActualDebit(long spent,bool settled)
    {
        var time=new ManualTime();var port=new MaintenancePort{Frame=new("quote","repair_quote",Gold:100000,Quote:20000,Currency:"gold",ConfirmAvailable:true,QuoteIsUpperBound:true)};
        var runner=new MaintenanceRunner(port,new MemoryStore(),new(BackpackEnabled:false,IntervalSeconds:1),time);time.Seconds+=2;runner.Tick();
        port.Frame=new("balance","repair_tools",Gold:100000-spent);runner.Tick();Assert.NotNull(runner.Snapshot()["pending"]);
        port.Frame=port.Frame with{CaptureId="complete",RepairComplete=true};runner.Tick();
        Assert.Equal(settled,runner.Snapshot()["pending"] is null);Assert.Single(port.Actions,a=>a=="confirm_repair");
        if(settled)Assert.Equal(spent,runner.Snapshot()["repair"]!["actual_spent"]!.GetValue<long>());
        else Assert.Equal("repair_payment_amount_mismatch",runner.Snapshot()["reason"]!.GetValue<string>());
    }
    [Theory]
    [InlineData(null)]
    [InlineData("silver")]
    public void UnverifiedRepairCurrencyDoesNotAuthorizePaymentOrBlockIndependentWork(string? currency)
    {
        var time=new ManualTime();var port=new MaintenancePort();var store=new MemoryStore();
        var runner=new MaintenanceRunner(port,store,new(BackpackEnabled:false,IntervalSeconds:1),time);
        time.Seconds+=2;
        for(var i=0;i<3;i++)
        {
            port.Frame=new(i.ToString(),"repair_quote",Gold:609136,Quote:1000,Currency:currency,ConfirmAvailable:true);
            runner.Tick();
        }
        Assert.Empty(port.Actions);Assert.Null(runner.Snapshot()["pending"]);
        Assert.Equal("repair_currency_unknown",runner.Snapshot()["reason"]!.GetValue<string>());
        Assert.True(runner.Blocked("idle_fishing",true));Assert.False(runner.Blocked("production"));
    }
    [Fact] public void UnknownGoldSyncWaitsForCleanupAndIsBoundedAcrossRestart()
    {
        var time=new ManualTime();var port=new MaintenancePort();var store=new MemoryStore();
        var runner=new MaintenanceRunner(port,store,new(BackpackEnabled:false,IntervalSeconds:1),time);time.Seconds+=2;
        for(var i=0;i<4;i++){port.Frame=new(i.ToString(),"repair_quote",Quote:1000,Currency:"gold",ConfirmAvailable:true);runner.Tick();}
        Assert.False(runner.TryRequestGoldSync());Assert.Empty(port.Actions);
        port.Frame=new("scene","scene");runner.Tick();Assert.True(runner.TryRequestGoldSync());Assert.False(runner.TryRequestGoldSync());
        runner=new MaintenanceRunner(port,store,new(BackpackEnabled:false,IntervalSeconds:1),time);Assert.False(runner.TryRequestGoldSync());
        runner.Resume(true);port.Frame=new("fresh","repair_quote",Gold:41000,Quote:1000,Currency:"gold",ConfirmAvailable:true);runner.Tick();
        Assert.Equal(new[]{"confirm_repair"},port.Actions);Assert.Equal(40000,runner.Snapshot()["repair"]!["gold_reserve"]!.GetValue<long>());
        Assert.False(runner.TryRequestGoldSync());
    }
    [Fact] public void ConfiguredLowerReserveCannotLowerFishingRepairFloor()
    {
        var time=new ManualTime();var port=new MaintenancePort{Frame=new("1","repair_quote",Gold:40500,Quote:1000,Currency:"gold",ConfirmAvailable:true)};
        var runner=new MaintenanceRunner(port,new MemoryStore(),new(IntervalSeconds:1,GoldReserve:0),time);time.Seconds+=2;runner.Tick();
        Assert.Empty(port.Actions);Assert.Equal("repair_reserve_insufficient",runner.Snapshot()["reason"]!.GetValue<string>());
    }
    [Theory]
    [InlineData(null,1000L,"repair_gold_unknown")]
    [InlineData(100000L,null,"repair_quote_unknown")]
    public void UnknownBalanceOrQuoteNeverAuthorizesPayment(long? gold,long? quote,string reason)
    {
        var port=new MaintenancePort();var time=new ManualTime();var runner=new MaintenanceRunner(port,new MemoryStore(),new(IntervalSeconds:1),time);
        time.Seconds+=2;
        for(var i=0;i<5;i++){port.Frame=new(i.ToString(),"repair_quote",Gold:gold,Quote:quote,Currency:"gold",ConfirmAvailable:true);runner.Tick();}
        Assert.Empty(port.Actions);Assert.Equal(reason,runner.Snapshot()["reason"]!.GetValue<string>());Assert.False(runner.Blocked("production"));
    }
    [Theory]
    [InlineData(41000,1000,true)]
    [InlineData(40999,1000,false)]
    [InlineData(40000,0,true)]
    public void RepairPreservesInclusiveGoldFloor(long gold,long quote,bool allowed)
    {
        var port=new MaintenancePort{Frame=new("1","repair_quote",Gold:gold,Quote:quote,Currency:"gold",ConfirmAvailable:true)};var time=new ManualTime();
        var runner=new MaintenanceRunner(port,new MemoryStore(),new(IntervalSeconds:1),time);time.Seconds+=2;runner.Tick();Assert.Equal(allowed&&quote>0,port.Actions.Contains("confirm_repair"));
        if(allowed&&quote==0){Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("equip",runner.Snapshot()["repair"]!["phase"]!.GetValue<string>());}
    }
    [Fact] public void PeriodicBackpackCheckWaitsForRoundThenClosesBag()
    {
        var time=new ManualTime();var port=new MaintenancePort{Frame=new("1","scene")};var runner=new MaintenanceRunner(port,new MemoryStore(),new(RepairEnabled:false,BackpackCheckIntervalSeconds:120),time);
        runner.Tick(false);Assert.False(runner.RequiresTurn);Assert.Null(runner.Snapshot()["backpack"]!["checked_at"]);
        time.Seconds+=120;runner.Tick(false);Assert.True(runner.RequiresTurn);Assert.Equal(0,port.Navigations);
        runner.Tick();Assert.Equal(1,port.Navigations);
        port.Frame=new("2","backpack",Weight:50,Capacity:100);runner.Tick();Assert.False(runner.RequiresTurn);Assert.True(runner.StatusSnapshot()["navigation_reserved"]!.GetValue<bool>());
        runner.Tick();Assert.Equal(1,port.Exits);port.Frame=new("3","scene");runner.Tick();Assert.False(runner.StatusSnapshot()["navigation_reserved"]!.GetValue<bool>());
        time.Seconds+=119;runner.Tick(false);Assert.False(runner.RequiresTurn);time.Seconds+=2;runner.Tick(false);Assert.True(runner.RequiresTurn);
    }
    [Fact] public void FailedNavigationReturnsToSceneBeforeReleasingOriginalWork()
    {
        var time=new ManualTime();var port=new MaintenancePort{Frame=new("1","region_map"),NavigationFailure="navigation_search_exhausted:repair_quote"};
        var runner=new MaintenanceRunner(port,new MemoryStore(),new(BackpackEnabled:false,IntervalSeconds:1),time);time.Seconds+=2;
        var state=runner.Tick();Assert.True(state["navigation_reserved"]!.GetValue<bool>());Assert.Null(state["pending"]);
        runner.Tick();Assert.Equal(1,port.Exits);Assert.True(runner.StatusSnapshot()["navigation_reserved"]!.GetValue<bool>());
        port.Frame=new("2","scene");runner.Tick();Assert.False(runner.StatusSnapshot()["navigation_reserved"]!.GetValue<bool>());
        runner.Resume(true);Assert.Equal("due",runner.Snapshot()["repair"]!["status"]!.GetValue<string>());
    }
    [Fact] public void PausedRepairAccountsWithoutNavigatingOrEquipping()
    {
        var port=new MaintenancePort();var time=new ManualTime();var runner=new MaintenanceRunner(port,new MemoryStore(),new(IntervalSeconds:1),time);
        time.Seconds+=2;runner.Tick();Assert.Single(port.Actions);runner.Pause();var resumes=port.Resumes;var navigations=port.Navigations;
        port.Frame=new("2","repair_result",RepairComplete:true);runner.ObserveOnly();Assert.NotNull(runner.Snapshot()["pending"]);Assert.Equal(navigations,port.Navigations);
        port.Frame=new("3","repair_tools",Gold:99000);runner.ObserveOnly();runner.ObserveOnly();
        Assert.Null(runner.Snapshot()["pending"]);Assert.Equal("manual",runner.Snapshot()["paused"]!.GetValue<string>());
        Assert.Single(port.Actions);Assert.Equal(resumes,port.Resumes);Assert.Equal(navigations,port.Navigations);
        runner.Resume(true);runner.Tick();Assert.Equal(new[]{"confirm_repair","auto_equip"},port.Actions);
    }
    [Fact] public void PausedTidyRequiresNewResultAndWeightWithoutNavigation()
    {
        var port=new MaintenancePort{Frame=new("1","backpack",Weight:95,Capacity:100)};var time=new ManualTime();var runner=new MaintenanceRunner(port,new MemoryStore(),new(RepairEnabled:false),time);runner.Tick();
        port.Frame=new("2","tidy",BoldSelected:false,MaterialsSelected:false,TidyCount:4);runner.Tick();port.Frame=new("3","tidy_confirm",ConfirmAvailable:true);runner.Tick();runner.Pause();var actions=port.Actions.Count;var navigations=port.Navigations;
        time.Seconds+=121;port.Frame=new("timeout","tidy_result");runner.ObserveOnly();Assert.True(runner.Snapshot()["global_pause_required"]!.GetValue<bool>());
        port.Frame=new("4","tidy_result",TidyComplete:true);runner.ObserveOnly();Assert.NotNull(runner.Snapshot()["pending"]);Assert.Equal(navigations,port.Navigations);
        port.Frame=new("5","backpack",Weight:20,Capacity:100);runner.ObserveOnly();Assert.Null(runner.Snapshot()["pending"]);
        Assert.Equal("manual",runner.Snapshot()["paused"]!.GetValue<string>());Assert.Equal(actions,port.Actions.Count);Assert.Equal(navigations,port.Navigations);
        Assert.False(runner.Snapshot()["global_pause_required"]!.GetValue<bool>());runner.Resume(true);Assert.Null(runner.Snapshot()["paused"]);
    }
    [Fact]
    public void ExplicitResumeWaitsForStableFrameInsteadOfLosingRequest()
    {
        var store = new MemoryStore();
        var port = new MaintenancePort { Frame = new("first", "scene", Stable: false) };
        var runner = new MaintenanceRunner(port, store, new(IntervalSeconds: 1), new ManualTime());
        runner.Pause();
        runner.Resume(true);
        port.Frame = new("second", "scene");
        runner.Tick();
        Assert.Null(runner.Snapshot()["paused"]);
    }

    [Fact]
    public void ResumeTimeoutAndLaterManualPauseCannotReviveMaintenance()
    {
        var port = new MaintenancePort { Frame = new("first", "scene", Stable: false) };
        var time = new ManualTime();
        var runner = new MaintenanceRunner(port, new MemoryStore(), new(IntervalSeconds: 1), time);
        runner.Pause();
        runner.Resume(true);
        time.Seconds += 16;
        runner.Tick();
        Assert.Equal("resume_observation_timeout", runner.Snapshot()["paused"]!.GetValue<string>());
        port.Frame = new("second", "scene");
        runner.Tick();
        Assert.Equal(0, port.Resumes);
        port.Frame = port.Frame with { Stable = false };
        runner.Resume(true);
        runner.Pause();
        port.Frame = new("third", "scene");
        runner.Tick();
        Assert.Equal("manual", runner.Snapshot()["paused"]!.GetValue<string>());
        Assert.Equal(0, port.Resumes);
        Assert.Empty(port.Actions);
    }
    [Fact]
    public void TimedOutTidyAcceptsLateObservedResultWithoutRepeatingSale()
    {
        var time = new ManualTime(); var port = new MaintenancePort { Frame = new("1", "backpack", Weight: 95, Capacity: 100) };
        var runner = new MaintenanceRunner(port, new MemoryStore(), new(RepairEnabled: false), time); runner.Tick();
        port.Frame = new("2", "tidy", BoldSelected: false, MaterialsSelected: false, TidyCount: 4); runner.Tick();
        port.Frame = new("3", "tidy_confirm", ConfirmAvailable: true); runner.Tick();
        time.Seconds = 120; port.Frame = new("4", "backpack", Weight: 20, Capacity: 100); runner.Tick();
        Assert.True(runner.Blocked("production")); Assert.NotNull(runner.Snapshot()["pending"]);
        runner.Resume(true); Assert.NotNull(runner.Snapshot()["pending"]);
        port.Frame = port.Frame with { CaptureId = "5", TidyComplete = true }; runner.Tick();
        Assert.Null(runner.Snapshot()["pending"]); Assert.False(runner.Blocked("production")); Assert.Equal(1, port.Actions.Count(x => x == "confirm_tidy"));
    }
    [Fact]
    public void RepairKeepsPaymentIntentUntilObservedThenEquips()
    {
        var time = new ManualTime(); var store = new MemoryStore(); var port = new MaintenancePort();
        var runner = new MaintenanceRunner(port, store, new(IntervalSeconds: 1), time);
        time.Seconds = 102; runner.Tick(); Assert.Equal("confirm_repair", store.Value!["pending"]!["action"]!.GetValue<string>());
        port.Frame = port.Frame with { CaptureId = "2", Page = "repair_tools", Gold = 99000 }; runner.Tick();
        runner.Tick(); Assert.Equal("auto_equip", store.Value!["pending"]!["action"]!.GetValue<string>());
        port.Frame = port.Frame with { CaptureId = "3", ToolsEquipped = true }; runner.Tick();
        Assert.Null(runner.Snapshot()["pending"]); Assert.Equal("idle", runner.Snapshot()["repair"]!["status"]!.GetValue<string>());
        Assert.Equal(new[] { "confirm_repair", "auto_equip" }, port.Actions);
    }
    [Fact]
    public void RepairReserveAndNativeBatchGateOnlyDependentWork()
    {
        var time = new ManualTime(); var port = new MaintenancePort(); var runner = new MaintenanceRunner(port, new MemoryStore(), new(IntervalSeconds: 1), time);
        time.Seconds = 102; runner.Tick(false); Assert.Empty(port.Actions);
        port.Frame = port.Frame with { Gold = 40999 }; runner.Tick(); Assert.Empty(port.Actions);
        Assert.True(runner.Blocked("gathering", true)); Assert.False(runner.Blocked("production"));
    }
    [Fact]
    public void TidyRequiresProtectiveSelectionAndObservedResult()
    {
        var port = new MaintenancePort { Frame = new("1", "backpack", Weight: 95, Capacity: 100) };
        var runner = new MaintenanceRunner(port, new MemoryStore(), new(RepairEnabled: false), new ManualTime()); runner.Tick();
        port.Frame = new("2", "tidy", BoldSelected: true, MaterialsSelected: true, TidyCount: 4); runner.Tick();
        port.Frame = port.Frame with { CaptureId = "3", BoldSelected = false }; runner.Tick();
        port.Frame = port.Frame with { CaptureId = "4", MaterialsSelected = false }; runner.Tick();
        port.Frame = new("5", "tidy_confirm", ConfirmAvailable: true); runner.Tick();
        Assert.Equal(new[] { "disable_bold", "deselect_materials", "prepare_tidy", "confirm_tidy" }, port.Actions);
        port.Frame = new("6", "backpack", Weight: 20, Capacity: 100); runner.Tick(); Assert.NotNull(runner.Snapshot()["pending"]);
        port.Frame = port.Frame with { CaptureId = "7", TidyComplete = true }; runner.Tick(); Assert.Null(runner.Snapshot()["pending"]);
    }
}
internal sealed class MaintenancePort : IMaintenancePort
{
    public Exception? Error;
    public MaintenanceFrame Frame = new("1", "repair_quote", Gold: 100000, Quote: 1000, Currency: "gold", ConfirmAvailable: true);
    public int Resumes; public string? PauseReason;
    public int Navigations,Exits,Observations;public string? NavigationFailure;public List<string> Actions = []; public MaintenanceFrame Observe() {Observations++;return Frame;} public string? Navigate(string destination) {Navigations++;return NavigationFailure;}
    public void Act(string action) {if(Error is{} e)throw e;Actions.Add(action);} public void Pause(string reason) { PauseReason=reason; } public void Resume() { Resumes++; } public void ResetNavigation() { }
    public bool CanReleaseForeground()=>Frame.Page=="scene";
    public bool ReleaseNavigation(){if(Frame.Page=="scene")return true;Exits++;return false;}
}
