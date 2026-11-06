using Xunit;
namespace MabiAgent.Windows.Tests;
public sealed class ScreenOwnerTrackerTests
{
    [Fact] public void MissingCaptureAndSameCharacterReconnectDoNotChangeOwnerButRealChangeSticks()
    {var tracker=new ScreenOwnerTracker();Assert.False(tracker.Observe(null,null));Assert.False(tracker.Observe("account","one"));Assert.False(tracker.Observe(null,null));Assert.False(tracker.Observe("account","one"));Assert.True(tracker.Observe("account","two"));Assert.True(tracker.Observe(null,null));Assert.True(tracker.Observe("account","one"));}
}
