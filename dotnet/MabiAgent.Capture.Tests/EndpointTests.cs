using System.Buffers.Binary;
using System.Net;
using MabiAgent.Capture;
using Xunit;
namespace MabiAgent.Capture.Tests;
public class EndpointTests
{
    [Fact] public void OwnerTableUsesExactPidAndReversesToServerDirection()
    {
        var table = new byte[28]; BinaryPrimitives.WriteInt32LittleEndian(table,1);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(4),5);
        new byte[] {127,0,0,1}.CopyTo(table,8); new byte[] {10,0,0,7}.CopyTo(table,16);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(12), 5678); BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(20),1111);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(24),42);
        var endpoints = ProcessEndpointFilter.ParseOwnerTable(table, false, 42);
        Assert.Contains(new TcpEndpoint("10.0.0.7",1111,"127.0.0.1",5678), endpoints);
        Assert.Empty(ProcessEndpointFilter.ParseOwnerTable(table,false,43));
        Assert.Throws<InvalidDataException>(() => ProcessEndpointFilter.ParseOwnerTable(table[..^1],false,42));
    }
    [Fact] public void LegacyCsvKeepsCandidateUntrustedAndLongItemIdsExact()
    {
        var registry = ItemNameRegistry.ReadCsv(new StringReader("item_id,name,association,source,captured\n4294967295,\"矿石,候选\",preset_candidate,catalog,false\n123,鐵礦石,user_confirmed,player_edit,true\n"));
        Assert.Null(registry.ResolveVerified("矿石,候选"));
        Assert.Equal((uint)123,registry.ResolveVerified("铁矿石"));
        Assert.Contains(registry.Snapshot(),r => r.ItemId == uint.MaxValue);
    }
}
