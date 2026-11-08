using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using MabiAgent.Capture;
using Xunit;

namespace MabiAgent.Capture.Tests;

public class InventoryTests
{
    [Theory][InlineData(1794077099380,true)][InlineData(1791979760147,false)][InlineData(1794077099381,false)]
    public void SharedStackIdUsesExistingBackpackAcquisitionEvidence(long acquired,bool accepted)
    {
        const uint item=332636885;const long stack=565995852121792512;
        var bag=ConsumableCodec.ParseRecord(ProtocolTests.Record(item:item,stackId:stack,quantity:163)) with{AcquiredTimeRaw=1794077099380};
        var warehouse=bag with{Quantity=191,AcquiredTimeRaw=1791979760147};
        var login=Login() with{Containers=new Dictionary<InventoryContainer,IReadOnlyList<ConsumableStack>>{
            [InventoryContainer.Backpack]=[bag],[InventoryContainer.CharacterStorage]=[],[InventoryContainer.AccountStorage]=[warehouse]}};
        var inventory=new StreamingInventory(_=>login,"distinct-acquisition",decodeUpdates:_=>[bag with{Quantity=160,AcquiredTimeRaw=acquired}]);
        inventory.Accept(new(0,GameProtocol.LoginOpcode,0,[1]),"c",1);
        inventory.Accept(new(0,GameProtocol.ProfileOpcode,0,Profile(login.CharacterCode)),"c",2);
        void Update()=>inventory.Accept(new(0,GameProtocol.InventoryOpcode,0,[1]),"c",3);
        if(accepted){Update();Assert.Equal(160,inventory.Snapshot().Quantity(InventoryContainer.Backpack,item));Assert.Equal(191,inventory.Snapshot().Quantity(InventoryContainer.AccountStorage,item));}
        else{Assert.Throws<InvalidDataException>(Update);Assert.Null(inventory.Snapshot().VerifiedThrough);}
    }
    [Fact] public void IdenticalStackInBackpackAndWarehouseRemainsUnknownUntilFreshFullSync()
    {
        const uint item=332636885;const long stack=565995852121792512;
        var row=ConsumableCodec.ParseRecord(ProtocolTests.Record(item:item,stackId:stack,quantity:7));
        var login=Login() with{Containers=new Dictionary<InventoryContainer,IReadOnlyList<ConsumableStack>>{
            [InventoryContainer.Backpack]=[row],[InventoryContainer.CharacterStorage]=[],[InventoryContainer.AccountStorage]=[row]}};
        var inventory=new StreamingInventory(_=>login,"ambiguous");
        inventory.Accept(new(0,GameProtocol.LoginOpcode,0,[1]),"c",1);
        inventory.Accept(new(0,GameProtocol.ProfileOpcode,0,Profile(login.CharacterCode)),"c",2);
        Assert.Throws<InvalidDataException>(()=>inventory.Accept(new(0,GameProtocol.InventoryOpcode,0,ProtocolTests.Record(item:item,stackId:stack,quantity:6)),"c",3));
        Assert.Null(inventory.Snapshot().Quantity(InventoryContainer.Backpack,item));
        Assert.Null(inventory.Snapshot().Quantity(InventoryContainer.AccountStorage,item));
        Assert.Equal(7,inventory.Snapshot().Counts[InventoryContainer.Backpack][item]);
        Assert.Equal(7,inventory.Snapshot().Counts[InventoryContainer.AccountStorage][item]);
        login=login with{Containers=new Dictionary<InventoryContainer,IReadOnlyList<ConsumableStack>>{
            [InventoryContainer.Backpack]=[row with{Quantity=6}],[InventoryContainer.CharacterStorage]=[],[InventoryContainer.AccountStorage]=[]}};
        inventory.Accept(new(0,GameProtocol.LoginOpcode,0,[1]),"c",4);
        Assert.Null(inventory.Snapshot().Error);Assert.Equal(6,inventory.Snapshot().Quantity(InventoryContainer.Backpack,item));
        Assert.Equal(0,inventory.Snapshot().Quantity(InventoryContainer.AccountStorage,item));
    }
    [Fact] public void NewBackpackStackWithSameDescriptorAsWarehouseDoesNotInvalidateAllInventory()
    {
        var baseLogin=Login();var containers=baseLogin.Containers.ToDictionary();
        containers[InventoryContainer.CharacterStorage]=[ConsumableCodec.ParseRecord(ProtocolTests.Record(item:476724970,stackId:566086617766320128,quantity:7))];
        var inventory=new StreamingInventory(_=>baseLogin with{Containers=containers},"capture");
        inventory.Accept(new(0,GameProtocol.LoginOpcode,0,[1]),"c",1);
        inventory.Accept(new(0,GameProtocol.ProfileOpcode,0,Profile(9007199254740993)),"c",2);
        inventory.Accept(new(0,GameProtocol.InventoryOpcode,0,ProtocolTests.Record(item:476724970,stackId:592290542848590848,quantity:2)),"c",3);
        Assert.Null(inventory.Snapshot().Error);Assert.Equal(2,inventory.Snapshot().Quantity(InventoryContainer.Backpack,476724970));
        Assert.Equal(7,inventory.Snapshot().Quantity(InventoryContainer.CharacterStorage,476724970));
        Assert.Throws<InvalidDataException>(()=>inventory.Accept(new(0,GameProtocol.InventoryOpcode,0,ProtocolTests.Record(item:476724970,stackId:566086617766320128,quantity:6)),"c",4));
        Assert.Null(inventory.Snapshot().VerifiedThrough);
    }
    [Fact] public void KnownIdentityBindsOnlyNewMatchingFullSyncWithoutHistoricalCounts()
    {
        var who=LoginIdentity.Join(9007199254740993,Profile(9007199254740993),LoginIdentity.AccountCodec);
        var inventory=new StreamingInventory(_=>Login(quantity:19),"fresh-capture",[who]);
        Assert.Null(inventory.Snapshot().Scope);Assert.Null(inventory.Snapshot().VerifiedThrough);Assert.Empty(inventory.Snapshot().Counts);
        inventory.Accept(new(0,GameProtocol.LoginOpcode,0,[1]),"new-connection",100);
        Assert.Equal(who,inventory.Snapshot().Identity);Assert.Equal(19,inventory.Snapshot().Quantity(InventoryContainer.Backpack,123));
        Assert.Equal(1,inventory.Snapshot().FullSyncRevision);Assert.Equal(100,inventory.Snapshot().FullSyncAt);
        Assert.Equal(3,inventory.Snapshot().Counts.Count);
        inventory.Invalidate("gap");Assert.Null(inventory.Snapshot().VerifiedThrough);
    }
    [Fact] public void KnownOtherCharacterCannotBindNewFullSync()
    {
        var other=LoginIdentity.Join(9007199254740994,Profile(9007199254740994),LoginIdentity.AccountCodec);
        var inventory=new StreamingInventory(_=>Login(),"fresh",[other]);
        inventory.Accept(new(0,GameProtocol.LoginOpcode,0,[1]),"c",100);
        Assert.Null(inventory.Snapshot().Scope);Assert.Null(inventory.Snapshot().VerifiedThrough);Assert.Empty(inventory.Snapshot().Counts);
    }
    [Fact] public void KnownIdentityRejectsInconsistentAccountCode()
    {
        Assert.Throws<ArgumentException>(()=>new StreamingInventory(_=>Login(),"fresh",[new("wrong","9007199254740993","label")]));
    }
    [Fact] public void ItemRevisionDetectsUnobservedRoundtripButNotDuplicateOrOtherItemUpdates()
    {
        var inventory=new StreamingInventory(_=>Login(),"capture");inventory.Accept(new(0,GameProtocol.LoginOpcode,0,[1]),"c",1);inventory.Accept(new(0,GameProtocol.ProfileOpcode,0,Profile(9007199254740993)),"c",2);
        void Update(byte[] body,double at)=>inventory.Accept(new(0,GameProtocol.InventoryOpcode,0,body),"c",at);
        Update(ProtocolTests.Record(quantity:6),3);var changed=inventory.Snapshot().ItemRevisions[123];
        Update(ProtocolTests.Record(quantity:6),4);Assert.Equal(changed,inventory.Snapshot().ItemRevisions[123]);
        Update(ProtocolTests.Record(quantity:7),5);var roundtrip=inventory.Snapshot();Assert.Equal(7,roundtrip.Quantity(InventoryContainer.Backpack,123));Assert.True(roundtrip.ItemRevisions[123]>changed);
        Update(ProtocolTests.Record(item:456,quantity:20),5.5);Assert.Equal(roundtrip.ItemRevisions[123],inventory.Snapshot().ItemRevisions[123]);
        inventory.Accept(new(0,GameProtocol.LoginOpcode,0,[1]),"c",6);Assert.Empty(inventory.Snapshot().ItemRevisions);
    }
    [Fact] public void WalletIsBoundToFullSyncAndInvalidationNeverRefreshesItsAge()
    {
        var inventory=new StreamingInventory(_=>Login() with{Gold=609136},"capture");
        inventory.Accept(new(0,GameProtocol.LoginOpcode,0,[1]),"c",1);Assert.Null(inventory.Snapshot().Gold);
        inventory.Accept(new(0,GameProtocol.ProfileOpcode,0,Profile(9007199254740993)),"c",2);
        inventory.Advance(100,true);Assert.Equal(609136,inventory.Snapshot().Gold);Assert.Equal(1,inventory.Snapshot().GoldAt);
        inventory.Invalidate("gap");Assert.Null(inventory.Snapshot().Gold);Assert.Null(inventory.Snapshot().GoldAt);
    }
    internal static LoginInventory Login(long character = 9007199254740993, long quantity = 7) => new(character, "digest", new Dictionary<InventoryContainer, IReadOnlyList<ConsumableStack>>
    {
        [InventoryContainer.Backpack] = new[] { ConsumableCodec.ParseRecord(ProtocolTests.Record(quantity: quantity)) },
        [InventoryContainer.CharacterStorage] = Array.Empty<ConsumableStack>(),
        [InventoryContainer.AccountStorage] = Array.Empty<ConsumableStack>()
    });
    internal static byte[] Profile(long character)
    {
        var name = Encoding.Unicode.GetBytes("角色甲"); var body = new byte[28 + name.Length];
        BinaryPrimitives.WriteInt64LittleEndian(body, character / 0x100000);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8), character);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(16), character);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(24), name.Length); name.CopyTo(body, 28); return body;
    }
    [Fact] public void IdentityJoinKeepsExactLongAndRejectsAccountMismatch()
    {
        var body = Profile(9007199254740993);
        var identity = LoginIdentity.Join(9007199254740993, body, LoginIdentity.AccountCodec);
        Assert.Equal("9007199254740993", identity.CharacterId);
        body[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => LoginIdentity.Join(9007199254740993, body, LoginIdentity.AccountCodec));
    }
    [Fact] public void FullSyncReplacesAbsoluteAmountsAndInvalidationKeepsUnknown()
    {
        var inventory = new StreamingInventory(_ => Login(), "capture");
        inventory.Accept(new(0, GameProtocol.LoginOpcode, 0, [1]), "connection", 1);
        Assert.Null(inventory.Snapshot().Scope);
        inventory.Accept(new(0, GameProtocol.ProfileOpcode, 0, Profile(9007199254740993)), "connection", 2);
        Assert.Equal(7, inventory.Snapshot().Quantity(InventoryContainer.Backpack, 123));
        var update = new GameFrame(0, GameProtocol.InventoryOpcode, 0, ProtocolTests.Record(quantity: 10));
        inventory.Accept(update, "connection", 3); inventory.Accept(update, "connection", 3);
        Assert.Equal(10, inventory.Snapshot().Quantity(InventoryContainer.Backpack, 123));
        Assert.Equal(1, inventory.Snapshot().FullSyncRevision);
        inventory.Invalidate("lost packet");
        Assert.Null(inventory.Snapshot().Quantity(InventoryContainer.Backpack, 123));
        Assert.False(inventory.Snapshot().ExecutionEligible);
        inventory.Accept(new(0, GameProtocol.LoginOpcode, 0, [1]), "connection", 4);
        Assert.Equal(7, inventory.Snapshot().Quantity(InventoryContainer.Backpack, 123));
        Assert.Equal(2, inventory.Snapshot().FullSyncRevision);
    }
    [Fact] public void MutationBeforeIdentityCannotBeSilentlyLost()
    {
        var inventory = new StreamingInventory(_ => Login(), "capture");
        inventory.Accept(new(0, GameProtocol.LoginOpcode, 0, [1]), "c", 1);
        inventory.Accept(new(0, GameProtocol.InventoryOpcode, 0, ProtocolTests.Record()), "c", 2);
        inventory.Accept(new(0, GameProtocol.ProfileOpcode, 0, Profile(9007199254740993)), "c", 3);
        Assert.Null(inventory.Snapshot().Scope);
        Assert.Null(inventory.Snapshot().VerifiedThrough);
    }
    [Fact] public void SharedStorageOverwritesAcrossCharactersAndUnknownIsNotZero()
    {
        var accounts = new AccountInventory();
        accounts.Record("a", "one", InventoryContainer.AccountStorage, new Dictionary<uint, long> { [123] = 5 }, true, "packet_capture", 1);
        accounts.Record("a", "two", InventoryContainer.AccountStorage, new Dictionary<uint, long> { [123] = 3 }, true, "packet_capture", 2);
        Assert.Equal(3, accounts.Total("a", 123).ObservedQuantity);
        Assert.False(accounts.Total("a", 123).Complete);
        Assert.Null(accounts.Total("a", 999).Quantity);
        foreach (var owner in new[] { "one", "two" })
            foreach (var container in new[] { InventoryContainer.Backpack, InventoryContainer.CharacterStorage })
                accounts.Record("a", owner, container, new Dictionary<uint,long>(), true, "packet_capture", 3);
        Assert.True(accounts.Total("a", 123).Complete);
        Assert.Equal(0, accounts.Total("a", 999).Quantity);
        accounts.Record("a", "two", InventoryContainer.AccountStorage, new Dictionary<uint,long>(), true, "packet_capture", 4);
        Assert.Equal(0, accounts.Total("a", 123).Quantity);
    }
    [Fact] public void CandidateNamesCannotResolveAndConflictsStayAmbiguous()
    {
        var registry = new ItemNameRegistry();
        registry.Set(new(123, "铁矿石", NameAssociation.Candidate, "third-party", false));
        Assert.Null(registry.ResolveVerified("鐵礦石"));
        registry.Set(new(123, "铁矿石", NameAssociation.UserConfirmed, "player", true));
        Assert.Equal((uint)123, registry.ResolveVerified("鐵礦石"));
        registry.Set(new(456, "鐵礦石", NameAssociation.BuiltinVerified, "verified", false));
        Assert.Null(registry.ResolveVerified("铁矿石"));
    }
    [Fact] public void WarehouseKeyUpdatesRequireAnotherFullSync()
    {
        var baseLogin = Login();
        var containers = baseLogin.Containers.ToDictionary();
        containers[InventoryContainer.AccountStorage] = new[] { ConsumableCodec.ParseRecord(ProtocolTests.Record(item: 999)) };
        var inventory = new StreamingInventory(_ => baseLogin with { Containers = containers }, "capture");
        inventory.Accept(new(0, GameProtocol.LoginOpcode, 0, [1]), "c", 1);
        inventory.Accept(new(0, GameProtocol.ProfileOpcode, 0, Profile(9007199254740993)), "c", 2);
        Assert.Throws<InvalidDataException>(() => inventory.Accept(new(0, GameProtocol.InventoryOpcode, 0, ProtocolTests.Record(item:999,quantity:10)), "c", 3));
        Assert.Null(inventory.Snapshot().Quantity(InventoryContainer.AccountStorage,999));
        Assert.Equal(7,inventory.Snapshot().Counts[InventoryContainer.AccountStorage][999]);
    }
    [Fact] public void FullSyncAggregateOverflowInvalidatesInsteadOfPublishing()
    {
        var baseLogin = Login(); var containers = baseLogin.Containers.ToDictionary();
        containers[InventoryContainer.Backpack] = new[] { ConsumableCodec.ParseRecord(ProtocolTests.Record(quantity:long.MaxValue)), ConsumableCodec.ParseRecord(ProtocolTests.Record(quantity:1,text:"other key")) };
        var inventory = new StreamingInventory(_ => baseLogin with { Containers=containers },"capture");
        Assert.Throws<OverflowException>(() => inventory.Accept(new(0,GameProtocol.LoginOpcode,0,[1]),"c",1));
        Assert.Null(inventory.Snapshot().VerifiedThrough);
    }
}

