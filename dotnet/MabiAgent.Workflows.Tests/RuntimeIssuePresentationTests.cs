using Xunit;
namespace MabiAgent.Workflows.Tests;
public sealed class RuntimeIssuePresentationTests
{
    [Theory][InlineData("restart")][InlineData("device_batch_not_ready")][InlineData("materials_unknown")][InlineData("购买前仓库库存未知，等待完整抓包")]
    public void AutomaticWaitDoesNotRemainInPlayerInbox(string reason)=>Assert.False(RuntimeIssuePresentation.RequiresPlayerAction(reason));
    [Theory][InlineData("complete")][InlineData("running")][InlineData("waiting")]
    public void RecoveredJobHidesOldReason(string status)=>Assert.False(RuntimeIssuePresentation.RequiresPlayerAction("old_fault",new("a",status,"waiting",null,false)));
    [Fact] public void UnknownConsumptionAndManualFailuresRemainVisible()
    {Assert.True(RuntimeIssuePresentation.RequiresPlayerAction("restart",new("a","unknown","result","restart",true)));Assert.True(RuntimeIssuePresentation.RequiresPlayerAction("backpack_player_action_required"));Assert.True(RuntimeIssuePresentation.RequiresPlayerAction("no_source"));}
}
