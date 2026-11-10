using Xunit;
namespace MabiAgent.Workflows.Tests;

public sealed class FishingNavigationInputTests
{
    [Theory][InlineData(FishingPlan.CurrentLocation)][InlineData("point_fishing_03")]
    public void ActiveNavigationPreventsCastMovementAndModePreparation(string point)
    {
        var port=new NavigationPort();var store=new MemoryStore();var runner=new IdleFishingRunner(new(point,300),port,store);
        var saved=runner.Snapshot();saved["phase"]="refresh";store.Flush(saved);runner=new(new(point,300),port,store);runner.Resume();
        runner.Tick();port.At=10;runner.Tick();Assert.Empty(port.Actions);
        port.At=179;runner.Tick();Assert.Empty(port.Actions);
        port.Navigation=false;port.At=180;runner.Tick();Assert.Equal(new[]{"prepare_mode"},port.Actions);
    }

    [Theory][InlineData(FishingPlan.CurrentLocation,false)][InlineData("point_fishing_03",false)]
    [InlineData(FishingPlan.CurrentLocation,true)][InlineData("point_fishing_03",true)]
    public void UnexpectedNavigationHasOneBoundedWait(string point,bool unknown)
    {
        var port=new NavigationPort();var runner=new IdleFishingRunner(new(point,300),port,new MemoryStore());
        runner.Tick();if(unknown){port.Stable=false;port.Navigation=null;}
        port.At=181;Assert.Throws<TimeoutException>(()=>runner.Tick());Assert.Empty(port.Actions);
    }

    private sealed class NavigationPort:IFishingPort
    {
        public double At;public bool? Navigation=true;public bool Stable=true;public List<string> Actions=[];
        public bool CanResumeAtPoint=>true;public bool ModePreparationRequired=>true;
        public FishingFrame Observe()=>new(At,At.ToString(),Page:Stable?"scene":"unknown",Stable:Stable,NavigationActive:Navigation,FishingButton:true,Compass:true);
        public void Perform(FishingAction action)=>Actions.Add(action.Kind);
        public void Pause(string reason){}public void Resume(){}
    }
}
