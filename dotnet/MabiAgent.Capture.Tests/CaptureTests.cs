using System.Buffers.Binary;
using System.Net;
using MabiAgent.Capture;
using Xunit;
namespace MabiAgent.Capture.Tests;
public class CaptureTests
{
    [Fact] public async Task AckKeepAliveDoesNotInvalidateAnActiveInventoryStream()
    {
        var identity=LoginIdentity.Join(9007199254740993,InventoryTests.Profile(9007199254740993),LoginIdentity.AccountCodec);
        var inventory=new StreamingInventory(_=>InventoryTests.Login(),"keepalive",[identity]);
        var source=new PartialSource((packet,gap)=>
        {
            var endpoint=new TcpEndpoint("127.0.0.1",1111,"127.0.0.1",5678);
            var login=ProtocolTests.Frame(GameProtocol.LoginOpcode,[42]);
            packet(new(endpoint,100,login,false,false,false,1){Acknowledgement=true});
            var next=100u+(uint)login.Length;
            packet(new(endpoint,next-1,[0],false,false,false,2){Acknowledgement=true});
            Assert.Equal(1,inventory.Snapshot().FullSyncRevision);
            Assert.NotNull(inventory.Snapshot().VerifiedThrough);
            packet(new(endpoint,next,ProtocolTests.Frame(GameProtocol.InventoryOpcode,ProtocolTests.Record(quantity:10)),false,false,false,3){Acknowledgement=true});
            Assert.Equal(10,inventory.Snapshot().Quantity(InventoryContainer.Backpack,123));
            Assert.Equal(3,inventory.Snapshot().VerifiedThrough);
        });
        using var service=new PassiveCaptureService(inventory,()=>source,_=>true);
        await service.RunAsync();
        Assert.Null(service.LastException);
        Assert.Equal(0,service.MetricsSnapshot().Invalidations);
        Assert.Equal(2,service.MetricsSnapshot().DecodedFrames);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ConflictPacketCanSupplyFreshValidatedMapSync(bool fragmented)
    {
        var character=9007199254740993L;var identity=new CharacterIdentity((character/0x100000).ToString(),character.ToString(),"known");
        var inventory=new StreamingInventory(_=>InventoryTests.Login(),"recovery",[identity]);
        var source=new PartialSource((packet,gap)=>
        {
            var endpoint=new TcpEndpoint("127.0.0.1",1111,"127.0.0.1",5678);var login=ProtocolTests.Frame(GameProtocol.LoginOpcode,[1]);
            packet(new(endpoint,100,login,false,false,false,1));Assert.Equal(1,inventory.Snapshot().FullSyncRevision);
            var next=100u+(uint)login.Length;
            packet(new(endpoint,next+5,new byte[20],false,false,false,2));
            var fresh=ProtocolTests.Frame(GameProtocol.LoginOpcode,[2]);
            packet(new(endpoint,next+5,fragmented?fresh[..9]:fresh,false,false,false,3));
            if(fragmented){Assert.Null(inventory.Snapshot().VerifiedThrough);packet(new(endpoint,next+14,fresh[9..],false,false,false,4));}
            Assert.Equal(2,inventory.Snapshot().FullSyncRevision);Assert.NotNull(inventory.Snapshot().VerifiedThrough);
            Assert.Equal(7,inventory.Snapshot().Quantity(InventoryContainer.Backpack,123));
        });
        using var service=new PassiveCaptureService(inventory,()=>source,_=>true);await service.RunAsync();
        Assert.Equal("Conflicting TCP reorder",service.LastError);
    }
    [Fact] public async Task FrameObserverReceivesDecodedInventoryEventWithConnectionAndReceiveTime()
    {
        var inventory=new StreamingInventory(_=>InventoryTests.Login(),"test");var seen=new List<(GameFrame Frame,string Connection,double At)>();
        var source=new PartialSource((packet,gap)=>
        {
            var endpoint=new TcpEndpoint("127.0.0.1",1111,"127.0.0.1",5678);
            var login=ProtocolTests.Frame(GameProtocol.LoginOpcode,[1]);
            packet(new(endpoint,100,login,false,false,false,8));
            packet(new(endpoint,100+(uint)login.Length,ProtocolTests.Frame(GameProtocol.InventoryOpcode,ProtocolTests.Record(item:639212127,quantity:57)),false,false,false,9));
        });
        using var service=new PassiveCaptureService(inventory,()=>source,_=>true,(frame,connection,at)=>seen.Add((frame,connection,at)));
        await service.RunAsync();var received=Assert.Single(seen,s=>s.Frame.Opcode==GameProtocol.InventoryOpcode);Assert.Equal(9,received.At);Assert.NotEmpty(received.Connection);
        Assert.Equal(639212127u,Assert.Single(ConsumableCodec.ParseUpdates(GameProtocol.DecodeBody(received.Frame.Flag,received.Frame.Body))).ItemId);
        Assert.Null(inventory.Snapshot().VerifiedThrough); // A diagnostic event does not fabricate identity or a full sync.
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteTailKeepsDecodedBaselineAndOnlyExtendsAfterCompletion(bool reorder)
    {
        var character=9007199254740993L;
        var identity=new CharacterIdentity((character/0x100000).ToString(),character.ToString(),"known");
        var inventory=new StreamingInventory(_=>InventoryTests.Login(),"test",[identity]);
        var source=new PartialSource((packet,gap)=>
        {
            var endpoint=new TcpEndpoint("127.0.0.1",1111,"127.0.0.1",5678);
            var login=ProtocolTests.Frame(GameProtocol.LoginOpcode,[1]);
            var tail=ProtocolTests.Frame(1234,new byte[30]);
            packet(new(endpoint,100,login.Concat(tail[..10]).ToArray(),false,false,false,1));
            Assert.Equal(1,inventory.Snapshot().FullSyncRevision);
            Assert.Equal(1,inventory.Snapshot().VerifiedThrough);
            Assert.Equal(7,inventory.Snapshot().Quantity(InventoryContainer.Backpack,123));
            var next=100u+(uint)login.Length+10;
            if(reorder)
            {
                packet(new(endpoint,next+5,tail[15..],false,false,false,2));
                Assert.Equal(1,inventory.Snapshot().VerifiedThrough);
                packet(new(endpoint,next,tail[10..15],false,false,false,3));
            }
            else packet(new(endpoint,next,tail[10..],false,false,false,3));
            Assert.Equal(3,inventory.Snapshot().VerifiedThrough);
            gap("capture loss");
            Assert.Null(inventory.Snapshot().VerifiedThrough);
            Assert.Null(inventory.Snapshot().Quantity(InventoryContainer.Backpack,123));
            packet(new(endpoint,500,tail,false,false,false,4));
            Assert.Null(inventory.Snapshot().VerifiedThrough);
        });
        using var service=new PassiveCaptureService(inventory,()=>source,_=>true);
        await service.RunAsync();
        Assert.Null(service.LastException);
    }
    private sealed class PartialSource(Action<Action<TcpPacket>,Action<string>> run):IPassivePacketSource
    {
        public void Run(Action<TcpPacket> packet,Action<string> gap,CancellationToken token)=>run(packet,gap);
        public void Dispose(){}
    }
    [Fact] public async Task AdapterStreamsDoNotMergeSameEndpointWithDifferentSequences()
    {
        var inventory=new StreamingInventory(_=>InventoryTests.Login(),"test");var source=new AdapterSource(inventory);
        using var service=new PassiveCaptureService(inventory,()=>source,_=>true);await service.RunAsync();
        Assert.True(source.VerifiedDuringRun);Assert.Null(service.LastException);
    }
    private sealed class AdapterSource(StreamingInventory inventory):IPassivePacketSource
    {
        public bool VerifiedDuringRun;
        public void Run(Action<TcpPacket> onPacket,Action<string> gap,CancellationToken token)
        {
            var endpoint=new TcpEndpoint("127.0.0.1",1111,"127.0.0.1",5678);
            onPacket(new(endpoint,100,[9,8,7,6],false,false,false,0,"virtual"));
            onPacket(new(endpoint,100,ProtocolTests.Frame(GameProtocol.LoginOpcode,[1]),false,false,false,1,"physical"));
            onPacket(new(endpoint,110,ProtocolTests.Frame(GameProtocol.ProfileOpcode,InventoryTests.Profile(9007199254740993)),false,false,false,2,"physical"));
            onPacket(new(endpoint,104,[5,4,3,2],false,false,false,3,"virtual"));
            VerifiedDuringRun=inventory.Snapshot().Quantity(InventoryContainer.Backpack,123)==7;
            Assert.Equal(2,inventory.Snapshot().VerifiedThrough); // unrelated adapter traffic is not coverage
        }
        public void Dispose(){}
    }
    [Fact] public void EthernetIpv4TcpDecoderRejectsFragmentsAndTruncation()
    {
        var frame = new byte[57]; frame[12] = 8; frame[14] = 0x45; frame[23] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), 43);
        new byte[] { 127, 0, 0, 1 }.CopyTo(frame, 26); new byte[] { 127, 0, 0, 1 }.CopyTo(frame, 30);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34), 1111); BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36), 5678);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(38), 0x12345678); frame[46] = 0x50; frame[47] = 0x18; frame[54] = 42;
        var packet = TcpPacketDecoder.Decode(1, frame, 1);
        Assert.NotNull(packet); Assert.Equal(0x12345678u, packet.Sequence); Assert.Equal(1111, packet.Endpoint.SourcePort); Assert.Equal(3, packet.Payload.Length); Assert.True(packet.Acknowledgement);
        Assert.Null(TcpPacketDecoder.Decode(1, frame[..^1], 1));
        frame[20] = 0x20; Assert.Null(TcpPacketDecoder.Decode(1, frame, 1));
    }
    [Fact] public async Task ServiceStopsAndDisposesSourceWithoutArchivingOrInput()
    {
        var inventory = new StreamingInventory(_ => InventoryTests.Login(), "test");
        var source = new Source();
        using var service = new PassiveCaptureService(inventory, () => source, _ => true);
        await service.RunAsync();
        Assert.True(source.Disposed); Assert.False(service.IsRunning);
        Assert.Equal(0, service.BufferedBytes); Assert.Null(inventory.Snapshot().VerifiedThrough);
        Assert.Equal(2, service.CapturedPackets);
        var metrics=service.MetricsSnapshot();Assert.Equal(2,metrics.Packets);Assert.Equal(2,metrics.DecodedFrames);Assert.True(metrics.PayloadBytes>0);
        Assert.Equal(0,metrics.Connections);Assert.Equal(0,metrics.BufferedBytes);
    }
    [Fact] public async Task CancellationStopsWorkerBeforeItCanRestart()
    {
        var inventory = new StreamingInventory(_ => InventoryTests.Login(), "test");
        var source = new WaitingSource();
        using var service = new PassiveCaptureService(inventory, () => source, _ => true);
        var running = service.RunAsync();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Throws<InvalidOperationException>(() => { _ = service.RunAsync(); });
        service.Stop(); await running.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(source.Disposed); Assert.False(service.IsRunning); Assert.Equal(0,service.BufferedBytes);
    }
    [Fact] public async Task OtherProcessOwnedTrafficDoesNotBlockCurrentInventory()
    {
        var inventory = new StreamingInventory(_ => InventoryTests.Login(), "test");
        var source = new MixedSource(inventory);
        using var service = new PassiveCaptureService(inventory, () => source, _ => true);
        await service.RunAsync();
        Assert.True(source.VerifiedDuringRun);
    }
    private sealed class MixedSource(StreamingInventory inventory) : IPassivePacketSource
    {
        public bool VerifiedDuringRun;
        public void Run(Action<TcpPacket> onPacket, Action<string> onGap, CancellationToken token)
        {
            var game = new TcpEndpoint("127.0.0.1",1111,"127.0.0.1",5678); var other = game with { SourcePort = 1112 };
            onPacket(new(other, 50, [1,2,3,4], false, false, false, 0));
            onPacket(new(game, 100, ProtocolTests.Frame(GameProtocol.LoginOpcode,[1]), false,false,false,1));
            onPacket(new(game, 110, ProtocolTests.Frame(GameProtocol.ProfileOpcode, InventoryTests.Profile(9007199254740993)),false,false,false,2));
            onPacket(new(other, 54, [], false, true, false, 3));
            VerifiedDuringRun = inventory.Snapshot().Quantity(InventoryContainer.Backpack,123) == 7;
        }
        public void Dispose() { }
    }
    private sealed class WaitingSource : IPassivePacketSource
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        public void Run(Action<TcpPacket> onPacket, Action<string> onGap, CancellationToken token) { Started.TrySetResult(); token.WaitHandle.WaitOne(); }
        public void Dispose() => Disposed = true;
    }
    private sealed class Source : IPassivePacketSource
    {
        public bool Disposed;
        public void Run(Action<TcpPacket> onPacket, Action<string> onGap, CancellationToken cancellationToken)
        {
            var endpoint = new TcpEndpoint("127.0.0.1",1111,"127.0.0.1",5678);
            onPacket(new(endpoint, 100, ProtocolTests.Frame(GameProtocol.LoginOpcode,[1]), false,false,false,1));
            onPacket(new(endpoint, 110, ProtocolTests.Frame(GameProtocol.ProfileOpcode, InventoryTests.Profile(9007199254740993)),false,false,false,2));
        }
        public void Dispose() => Disposed = true;
    }
}



