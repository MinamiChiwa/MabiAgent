namespace MabiAgent.Windows;

/// <summary>A superseded screenshot revision stays retired across capture outages.</summary>
public sealed class ScreenStockArbitration
{
    private sealed record Entry(long Revision,string? Epoch,string? PacketVersion,bool Retired);
    private readonly Dictionary<long,Entry> entries=[];
    private readonly Dictionary<long,(string? Epoch,string? Version)> latest=[];
    public bool UseScreen(long itemId,long screenRevision,string? fullSyncEpoch,string? packetVersion)
    {
        var basis=latest.GetValueOrDefault(itemId);
        if(fullSyncEpoch is not null)basis=(fullSyncEpoch,packetVersion);
        latest[itemId]=basis;
        if(!entries.TryGetValue(itemId,out var old)||screenRevision>old.Revision)
            entries[itemId]=old=new(screenRevision,basis.Epoch,basis.Version,false);
        if(screenRevision<old.Revision)return false;
        if(old.Epoch!=basis.Epoch||old.PacketVersion!=basis.Version)entries[itemId]=old=old with{Retired=true};
        return !old.Retired;
    }
}
