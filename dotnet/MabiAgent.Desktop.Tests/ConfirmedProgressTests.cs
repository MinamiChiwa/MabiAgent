using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Desktop.Tests;

public sealed class ConfirmedProgressTests
{
    private static JobRow Fishing()=>new("fish","海滨","挂机钓鱼",120,10,"执行",false,0,true,false,false,ActionId:"cast-batch");

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
