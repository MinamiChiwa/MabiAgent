using System.Text.Json.Nodes;
using Xunit;
namespace MabiAgent.Windows.Tests;

public sealed class StorageTransferQuantityTests
{
    [Theory][InlineData(650,"744.93/810","0.10",650L)][InlineData(1,"744.93/810","0.10",null)][InlineData(650,null,"0.10",null)][InlineData(650,"744.93/810","0",null)]
    public void CapacityLimitRequiresMatchingWeightEvidence(long selected,string? bag,string unit,long? expected)
        =>Assert.Equal(expected,ScreenStorageSupplyPort.LimitedQuantity(selected,bag,unit));
    [Fact] public void QuantityReadRegionCoversMultiDigitInputSurface()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());
        var storage=routes.Profile.Definition["storage_supply"]!;
        var region=storage["transfer"]!["quantity"]!["region"]!.AsArray().Select(v=>v!.GetValue<int>()).ToArray();
        var surface=routes.Profile.Definition["pages"]!["storage_transfer"]!["surfaces"]!["storage.quantity"]!.AsArray().Select(v=>v!.GetValue<int>()).ToArray();
        Assert.Equal(surface,region);
        Assert.True(region[2]-region[0]>=70);
        Assert.True(region[3]-region[1]>=30);
    }
    [Fact] public void TransferNameSearchCoversKnownShiftedDetailTitles()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var transfer=routes.Profile.Definition["storage_supply"]!["transfer"]!;
        var fixedRegion=transfer["name_region"]!.AsArray().Select(v=>v!.GetValue<int>()).ToArray();
        var search=transfer["name_search_region"]!.AsArray().Select(v=>v!.GetValue<int>()).ToArray();
        Assert.True(search[0]<=fixedRegion[0]&&search[1]<=fixedRegion[1]&&search[2]>=fixedRegion[2]&&search[3]>=423);
    }
}
