using Xunit;
namespace MabiAgent.Windows.Tests;
public sealed class RepairBudgetTests
{
    static QuantityReading Reading(string text)=>new(1,"quote",null,null,new(0,0,8,8),"sample",[new(165,text,.99,null)]);
    [Theory][InlineData("1万",20000)][InlineData("1萬",20000)][InlineData("2.1萬",22000)]
    public void CompactIntegerAndDecimalQuotesRemainUpperBounds(string text,long expected)
    {var result=ScreenMaintenancePort.ReadRepairBudget(Reading(text));Assert.Equal(expected,result.Budget);Assert.True(result.UpperBound);}
    [Fact] public void PrimaryExactQuoteSurvivesCompactOrUnreadableConfirmation()
    {
        Assert.Equal((19913L,false),ScreenMaintenancePort.ResolveRepairBudget(Reading("1万"),19913));
        Assert.Equal((19913L,false),ScreenMaintenancePort.ResolveRepairBudget(Reading("unreadable"),19913));
        Assert.Equal((20000L,true),ScreenMaintenancePort.ResolveRepairBudget(Reading("1万"),29913));
        Assert.Null(ScreenMaintenancePort.ResolveRepairBudget(Reading("unreadable"),null).Budget);
    }
    [Fact] public void CompactQuoteProvidesConservativeBudgetNeverExactPayment()
    {
        var result=ScreenMaintenancePort.ReadRepairBudget(Reading("1.9萬"));
        Assert.Equal(20000,result.Budget);Assert.True(result.UpperBound);
        Assert.Null(ScreenMaintenancePort.ReadRepairBudget(Reading("unreadable")).Budget);
    }
    [Fact] public void ExactSingleObservationNeedsNoPreview()
    {
        var result=ScreenMaintenancePort.ReadRepairBudget(Reading("19,913") with{Value=new(19913)});
        Assert.Equal(19913,result.Budget);Assert.False(result.UpperBound);
    }
}
