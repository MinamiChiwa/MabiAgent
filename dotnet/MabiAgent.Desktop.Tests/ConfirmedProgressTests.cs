using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Desktop.Tests;

public sealed class ConfirmedProgressTests
{
    [Fact]
    public void SubmittedProcessingRemainsVisibleAcrossPauseAndUnknownStillNeedsConfirmation()
    {
        var row=new JobRow("wood","木材","生产",100,0,"执行",false,0,true,false,false,ActionId:"batch");
        row.ApplyRuntime(new("batch","paused","waiting",null,false));
        Assert.Equal("设备加工中",row.Status);Assert.False(row.IsWarningState);
        row.ApplyRuntime(new("batch","unknown","waiting","collection_receipt_missing",false));
        Assert.Equal("本批结果待确认",row.Status);Assert.True(row.IsWarningState);
    }
    private static JobRow Fishing()=>new("fish","海滨","挂机钓鱼",120,10,"执行",false,0,true,false,false,ActionId:"cast-batch");

    [Fact]
    public void FishingNavigationTextFollowsExecutionOwnership()
    {
        var row=Fishing();row.ApplyRuntime(new("cast-batch","running","navigate",null,false));Assert.Equal("等待调度",row.Status);
        row.ApplyRuntime(new("cast-batch","running","navigate",null,false){Executing=true});Assert.Equal("正在前往钓鱼点",row.Status);
        row.ApplyRuntime(new("cast-batch","running","navigate",null,false));Assert.Equal("等待调度",row.Status);
    }

    [Fact]
    public void IsolatedTaskShowsBlockedAndRetainsConfirmedProgress()
    {
        var row=Fishing();row.ApplyRuntime(new("cast-batch","blocked","navigate","钓鱼选点导航已到总等待上限",false){Confirmed=23});
        Assert.Equal("任务受阻，请点击继续",row.Status);Assert.True(row.IsWarningState);Assert.False(row.IsActiveState);Assert.Equal(33,row.ConfirmedQuantity);
    }


    [Fact]
    public void CurrentBatchProgressNotifiesTheConfirmedCellWithoutAQueueFileChange()
    {
        var row=Fishing();var changes=new List<string?>();row.PropertyChanged+=(_,e)=>changes.Add(e.PropertyName);
        row.ApplyRuntime(new("cast-batch","running","round",null,false){Confirmed=23});
        Assert.Equal(33,row.ConfirmedQuantity);Assert.Equal(10,row.Completed);
        Assert.Contains(nameof(JobRow.CompletedLabel),changes);Assert.Contains(nameof(JobRow.RemainingLabel),changes);
        changes.Clear();row.ApplyRuntime(new("cast-batch","paused","round",null,false){Confirmed=23});
        Assert.Equal(33,row.ConfirmedQuantity);
        row.ApplyRuntime(new("cast-batch","running","round",null,false){Confirmed=24});Assert.Equal(34,row.ConfirmedQuantity);
    }

    [Fact]
    public void AnotherBatchCannotSupplyProgressToThisRow()
    {
        var row=Fishing();row.ApplyRuntime(new("older-batch","running","round",null,false){Confirmed=90});
        Assert.Equal(10,row.ConfirmedQuantity);
        row.ApplyRuntime(new("cast-batch","running","round",null,false){Confirmed=24});
        row.ApplyRuntime(new("next-batch","running","round",null,false){Confirmed=70});Assert.Equal(10,row.ConfirmedQuantity);
    }

    [Fact]
    public void ReceiptReplacementDoesNotDoubleCountTheFinishedBatch()
    {
        var row=Fishing();row.ApplyRuntime(new("cast-batch","running","round",null,false){Confirmed=115});
        var rows=new QueueRows();rows.ApplySnapshot([row]);
        rows.ApplySnapshot([new("fish","海滨","挂机钓鱼",120,125,"已完成",false,0,false,false,false)]);
        Assert.Same(row,rows[0]);
        row.ApplyRuntime(new("cast-batch","complete","complete",null,false){Confirmed=115});
        Assert.Equal(125,row.ConfirmedQuantity);Assert.Equal(125,row.Completed);Assert.True(row.IsCompleteState);
    }

    [Fact]
    public void UnchangedProgressDoesNotSendRepeatedCellNotifications()
    {
        var row=Fishing();var progress=new AutomationJobStatus("cast-batch","running","round",null,false){Confirmed=23};row.ApplyRuntime(progress);
        var changes=new List<string?>();row.PropertyChanged+=(_,e)=>changes.Add(e.PropertyName);row.ApplyRuntime(progress);
        Assert.Empty(changes);
    }
}
