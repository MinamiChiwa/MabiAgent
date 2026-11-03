using MabiAgent.Workflows;
using Xunit;

namespace MabiAgent.Workflows.Tests;

public sealed class WorkbenchExtensionTests
{
    [Fact] public void ProductionExtensionRunsBeforeNormalActionsAndAnExceptionStaysLocal()
    {
        var port=new ExtendedProduction();var runner=new ProductionRunner(port,new MemoryStore(),new("iron","furnace",30),time:new ManualTime());
        runner.Resume(true);runner.Tick();Assert.Equal(1,port.ExtensionTicks);Assert.Equal(0,port.Clicks);
        port.Throw=true;runner.Tick();Assert.Equal("module_fault",runner.Snapshot()["pause_reason"]!.GetValue<string>());Assert.Equal(0,port.Clicks);Assert.Null(port.Paused);
    }
    [Fact] public void GatheringExtensionHoldsNewStartAndAllowsOriginalSemanticsWhenReady()
    {
        var port=new ExtendedGathering();var runner=new GatheringRunner(port,new MemoryStore(),new("ore",1,"mining","point"),"a",new ManualTime());
        runner.Resume(true);runner.Tick();Assert.Equal(1,port.ExtensionTicks);Assert.Equal(0,port.Starts);
        port.Hold=false;runner.Tick();Assert.Equal(1,port.Starts);Assert.NotNull(runner.Snapshot()["pending"]);
        var ticks=port.ExtensionTicks;runner.Tick();Assert.Equal(ticks,port.ExtensionTicks);Assert.Equal(1,port.Starts);
    }
    sealed class ExtendedProduction:IProductionPort
    {
        public bool RemoteDevicePreview=>false;public bool HasExtensionPending=>true;public int ExtensionTicks,Clicks;public bool Throw;public string? Paused;
        public bool TickExtension(){ExtensionTicks++;if(Throw)throw new InvalidOperationException("测试步骤错误");return true;}
        public ProductionFrame Observe()=>new("furnace_panel",EmptySlots:[0],SlotCount:1);
        public void Navigate(string destination,ProductionRecipe recipe){}public void Click(string kind,ProductionRecipe recipe)=>Clicks++;
        public void Pause(string reason)=>Paused=reason;public void Resume(){}
    }
    sealed class ExtendedGathering:IGatheringPort
    {
        public bool HasExtensionPending=>true;public bool Hold=true;public int ExtensionTicks,Starts;
        public bool TickExtension(){ExtensionTicks++;return Hold;}
        public GatheringFrame Observe(GatheringRoute route,string actionId)=>new("scene",RouteKey:route.Key,StartAvailable:true);
        public void Navigate(GatheringRoute route){}public void Start(GatheringRoute route,string actionId)=>Starts++;public void Pause(string reason){}public void Resume(){}
    }
}
