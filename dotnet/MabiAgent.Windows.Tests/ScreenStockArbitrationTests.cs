using Xunit;
namespace MabiAgent.Windows.Tests;
public sealed class ScreenStockArbitrationTests
{
    [Fact] public void MissingCaptureAndSameCharacterReconnectDoNotChangeOwnerButRealChangeSticks()
    {var tracker=new ScreenOwnerTracker();Assert.False(tracker.Observe(null,null));Assert.False(tracker.Observe("account","one"));Assert.False(tracker.Observe(null,null));Assert.False(tracker.Observe("account","one"));Assert.True(tracker.Observe("account","two"));Assert.True(tracker.Observe(null,null));Assert.True(tracker.Observe("account","one"));}
    [Fact] public void PacketChangePermanentlyRetiresScreenshotEvenWhenCaptureDisappears()
    {var a=new ScreenStockArbitration();Assert.True(a.UseScreen(1,1,"sync1","v1"));Assert.False(a.UseScreen(1,1,"sync1","v2"));Assert.False(a.UseScreen(1,1,null,null));Assert.False(a.UseScreen(1,1,"sync1","v1"));Assert.True(a.UseScreen(1,2,"sync1","v1"));}
    [Fact] public void FirstFullSyncAndReconnectRetireOldScreenWithoutTreatingMissingPacketsAsZero()
    {var a=new ScreenStockArbitration();Assert.True(a.UseScreen(1,1,null,null));Assert.False(a.UseScreen(1,1,"sync1",null));Assert.False(a.UseScreen(1,1,null,null));Assert.True(a.UseScreen(1,2,"sync1",null));Assert.False(a.UseScreen(1,2,"sync2",null));}
    [Fact] public void UnchangedEpochOutageDoesNotRetireFreshScreenAndItemsRemainIndependent()
    {var a=new ScreenStockArbitration();Assert.True(a.UseScreen(1,3,"sync1","v1"));Assert.True(a.UseScreen(1,3,null,null));Assert.True(a.UseScreen(2,1,"sync1","v9"));Assert.False(a.UseScreen(1,2,"sync1","v1"));Assert.False(a.UseScreen(2,1,"sync1",""));}
}
