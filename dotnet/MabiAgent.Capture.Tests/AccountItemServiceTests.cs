using System.Security.Cryptography;
using System.Text;
using MabiAgent.Capture;
using Xunit;

namespace MabiAgent.Capture.Tests;

public sealed class AccountItemServiceTests : IDisposable
{
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)]
    public void CharacterSlotsPersistWithoutChangingInventory(int slot)
    {
        var service=new AccountItemService(directory);service.Record(Snapshot("one",3,20));var path=service.Files("account").Inventory;var before=File.ReadAllBytes(path);
        service.RecordCharacterSlot("account","one",slot);service=new(directory);
        Assert.Equal(slot,service.CharacterSlot("account","one"));Assert.Equal("角色one",service.CharacterAtSlot("account",slot)!.Label);Assert.Equal(before,File.ReadAllBytes(path));
        Assert.Null(service.CharacterAtSlot("account",slot==6?1:6));Assert.Null(service.CharacterSlot("missing","one"));
    }
    [Fact] public void SameSlotAndNameStayIsolatedByAccountAndCharacterId()
    {
        var service=new AccountItemService(directory);service.Record(Snapshot("one",3,20) with{Identity=new("account","one","同名")});
        service.Record(Snapshot("two",9,70) with{Scope=new("other","two","other-session"),Identity=new("other","two","同名")});
        service.RecordCharacterSlot("account","one",1);service.RecordCharacterSlot("other","two",1);
        Assert.Equal("one",service.CharacterAtSlot("account",1)!.CharacterId);Assert.Equal("two",service.CharacterAtSlot("other",1)!.CharacterId);
        Assert.Equal(3,service.View("account").Items.Single(x=>x.ItemId==123).Characters["one"].Backpack);
        Assert.Equal(70,service.View("other").Items.Single(x=>x.ItemId==123).SharedStorage);
        Assert.Null(service.CharacterSlot("account","two"));Assert.Throws<ArgumentException>(()=>service.RecordCharacterSlot("account","two",2));
    }
    [Fact] public void SlotConflictsAndInvalidSlotsPreserveMappings()
    {
        var service=new AccountItemService(directory);service.Record(Snapshot("one",3,20));service.Record(Snapshot("two",7,20));service.RecordCharacterSlot("account","one",1);
        var before=File.ReadAllBytes(Path.Combine(directory,"accounts.json"));
        Assert.Throws<InvalidDataException>(()=>service.RecordCharacterSlot("account","two",1));Assert.Throws<InvalidDataException>(()=>service.RecordCharacterSlot("account","one",2));
        Assert.Throws<ArgumentOutOfRangeException>(()=>service.RecordCharacterSlot("account","two",7));Assert.Throws<ArgumentOutOfRangeException>(()=>service.CharacterAtSlot("account",0));Assert.Equal(before,File.ReadAllBytes(Path.Combine(directory,"accounts.json")));
        service.Record(Snapshot("one",4,21) with{Identity=new("account","one","改名")});Assert.Equal("改名",service.CharacterAtSlot("account",1)!.Label);
    }
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mabi-capture-test-" + Guid.NewGuid().ToString("N"));
    private string Folder => Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("account"))))[..(directory.Length + 1 + 24)];
    private static InventorySnapshot Snapshot(string character, long bag, long shared) => new(new("account", character, "session-" + character), new("account", character, "角色" + character),
        new Dictionary<InventoryContainer, IReadOnlyDictionary<uint, long>>
        { [InventoryContainer.Backpack] = new Dictionary<uint,long> { [123] = bag }, [InventoryContainer.CharacterStorage] = new Dictionary<uint,long>(), [InventoryContainer.AccountStorage] = new Dictionary<uint,long> { [123] = shared, [456] = 0 } }, 1, 1, 1, 2, null);
    [Fact] public void SharedStorageIsReplacedAndCharacterColumnsSurviveRestart()
    {
        var service = new AccountItemService(directory); service.Record(Snapshot("one", 3, 20)); service.Record(Snapshot("two", 7, 10));
        service = new(directory); var view = service.View("account"); var row = view.Items.Single(r => r.ItemId == 123);
        Assert.Equal(20, row.AccountTotal); Assert.Equal(10, row.SharedStorage); Assert.Equal(3, row.Characters["one"].Total); Assert.Equal(7, row.Characters["two"].Total);
        Assert.True(view.Complete); Assert.True(view.Historical); Assert.False(view.ExecutionEligible);
        using var output = new StringWriter(); service.ExportCsv("account", output);
        Assert.Contains("角色one [one]", output.ToString()); Assert.Equal(3, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
    [Fact] public void MissingCoverageIsUnknownAndReadingDoesNotCreateFiles()
    {
        var service = new AccountItemService(directory); Assert.Empty(service.Accounts()); Assert.False(Directory.Exists(directory));
        service.Record(Snapshot("one", 3, 20));
        var path = Path.Combine(Folder, "inventory.csv");
        File.WriteAllLines(path, File.ReadAllLines(path).Where(l => !l.Contains("full_coverage", StringComparison.Ordinal)), new UTF8Encoding(true));
        var view = service.View("account"); Assert.False(view.Complete); Assert.Null(view.Items[0].AccountTotal); Assert.Equal(23, view.Items[0].ObservedTotal);
        Assert.Null(view.Items[0].Characters["one"].Total);
    }
    [Fact] public void BatchAssociationsValidateBeforeWritingAndPreserveUnknownColumns()
    {
        var service = new AccountItemService(directory); service.Record(Snapshot("one", 3, 20));
        var path = Path.Combine(Folder, "associations.csv");
        File.WriteAllText(path, "item_id,name,association,source,captured,capture_source,extra\n123,旧名,preset_candidate,catalog,true,original,keep\n456,,unknown,capture,true,,other\n");
        var before = File.ReadAllBytes(path);
        Assert.Throws<ArgumentException>(() => service.UpdateNames("account", [new(123, "同名"), new(456, "同名")], NameEditOperation.Confirm));
        Assert.Equal(before, File.ReadAllBytes(path));
        service.UpdateNames("account", [new(123, "铁矿石"), new(456, "逗号,名称")], NameEditOperation.Confirm);
        Assert.Equal(123u, service.ResolveVerified("account", "鐵礦石"));
        Assert.Contains("original,keep", File.ReadAllText(path));
        service.UpdateNames("account", [new(123, null)], NameEditOperation.Clear); Assert.Null(service.ResolveVerified("account", "铁矿石"));
        Assert.Equal(NameAssociation.UserCleared, service.View("account").Items.Single(r => r.ItemId == 123).Association);
    }
    [Fact] public void InvalidCurrentScopeCannotOverwriteHistoricalRows()
    {
        var service = new AccountItemService(directory); service.Record(Snapshot("one", 3, 20));
        var path = Path.Combine(Folder, "inventory.csv"); var before = File.ReadAllBytes(path);
        Assert.Throws<ArgumentException>(() => service.Record(Snapshot("one", 9, 1) with { VerifiedThrough = null }));
        Assert.Throws<ArgumentException>(() => service.Record(Snapshot("one", 9, 1) with { Identity = new("other", "one", "wrong") }));
        Assert.Equal(before, File.ReadAllBytes(path));
    }
    [Fact] public async Task ConcurrentUpdateNamesWithSameBaselineAllowsOnlyOneWriter()
    {
        var service=new AccountItemService(directory);service.Record(Snapshot("one",3,20));
        var path=service.Files("account").Associations;
        var expected=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var first=new AccountItemService(directory);var second=new AccountItemService(directory);using var barrier=new Barrier(2);
        Task<IOException?> Save(AccountItemService writer,string name)=>Task.Run(()=>
        {
            barrier.SignalAndWait();try{writer.UpdateNames("account",[new(123,name)],NameEditOperation.Confirm,expected);return null;}catch(IOException error){return error;}
        });
        var results=await Task.WhenAll(Save(first,"第一"),Save(second,"第二"));
        Assert.Single(results,result=>result is null);Assert.Single(results,result=>result is not null);
        var saved=service.ReadNames("account").Snapshot().Single(row=>row.ItemId==123).Name;
        Assert.True(saved is "第一" or "第二");
    }
    [Theory][InlineData(4,3,20)][InlineData(9,2,21)][InlineData(5,3,19)]
    public void OlderSessionGenerationsCannotOverwriteNewerHistory(long revision,long full,double verified)
    {
        var service=new AccountItemService(directory);var at=DateTimeOffset.UtcNow.AddMinutes(-1);
        var current=Snapshot("one",3,20) with{Revision=5,FullSyncRevision=3,VerifiedThrough=20,ObservedAt=at,FrameSha256="new"};
        service.Record(current);var path=service.Files("account").Inventory;var before=File.ReadAllBytes(path);
        service.Record(Snapshot("one",99,1) with{Revision=revision,FullSyncRevision=full,VerifiedThrough=verified,ObservedAt=at.AddSeconds(1),FrameSha256="old"});
        Assert.Equal(before,File.ReadAllBytes(path));Assert.Equal(3,service.View("account").Items.Single(row=>row.ItemId==123).Characters["one"].Backpack);
    }
    [Fact] public void DifferentSessionsRequireNewerObservationAndSharedStorageKeepsAccountOwnership()
    {
        var service=new AccountItemService(directory);var at=DateTimeOffset.UtcNow.AddMinutes(-1);
        service.Record(Snapshot("one",3,20) with{ObservedAt=at});
        service.Record(Snapshot("one",99,1) with{Scope=new("account","one","older-session"),Revision=99,FullSyncRevision=99,VerifiedThrough=99,ObservedAt=at.AddSeconds(-1)});
        Assert.Equal(3,service.View("account").Items.Single(row=>row.ItemId==123).Characters["one"].Backpack);
        service.Record(Snapshot("one",98,2) with{Scope=new("account","one","equal-time-session"),ObservedAt=at});
        Assert.Equal(3,service.View("account").Items.Single(row=>row.ItemId==123).Characters["one"].Backpack);
        service.Record(Snapshot("two",7,99) with{ObservedAt=at.AddSeconds(-1)});
        var row=service.View("account").Items.Single(item=>item.ItemId==123);
        Assert.Equal(20,row.SharedStorage);Assert.Equal(7,row.Characters["two"].Backpack);
        service.Record(Snapshot("one",9,30) with{Scope=new("account","one","newer-session"),Revision=1,FullSyncRevision=1,VerifiedThrough=1,ObservedAt=at.AddSeconds(1)});
        row=service.View("account").Items.Single(item=>item.ItemId==123);Assert.Equal(9,row.Characters["one"].Backpack);Assert.Equal(30,row.SharedStorage);
    }
    [Fact] public void OldSessionHealthAdvanceCannotOverwriteNewFullSync()
    {
        static StreamingInventory Sync(string capture,long quantity,string hash)
        {
            var stream=new StreamingInventory(_=>InventoryTests.Login(quantity:quantity) with{BodySha256=hash},capture);
            stream.Accept(new(0,GameProtocol.LoginOpcode,0,[1]),"connection",1);
            stream.Accept(new(0,GameProtocol.ProfileOpcode,0,InventoryTests.Profile(9007199254740993)),"connection",2);
            return stream;
        }
        var service=new AccountItemService(directory);var old=Sync("old",7,"old-full");var oldFull=old.Snapshot();service.Record(oldFull);
        while(DateTimeOffset.UtcNow<=oldFull.FullSyncObservedAt!.Value)Thread.Sleep(1);
        var current=Sync("new",19,"new-full");var currentFull=current.Snapshot();service.Record(currentFull);
        Assert.Equal(19,service.View(currentFull.Identity!.AccountId).Items.Single(row=>row.ItemId==123).Characters[currentFull.Identity.CharacterId].Backpack);
        while(DateTimeOffset.UtcNow<=currentFull.ObservedAt!.Value)Thread.Sleep(1);
        old.Accept(new(0,GameProtocol.InventoryOpcode,0,ProtocolTests.Record(quantity:99)),"connection",3);old.Advance(100,true);
        var delayed=old.Snapshot();
        Assert.True(delayed.Revision>currentFull.Revision);Assert.True(delayed.ObservedAt!.Value>currentFull.ObservedAt.Value);
        Assert.True(delayed.FullSyncObservedAt!.Value<currentFull.FullSyncObservedAt!.Value);
        service.Record(delayed);
        var account=currentFull.Identity.AccountId;var row=service.View(account).Items.Single(item=>item.ItemId==123);
        Assert.Equal(19,row.Characters[currentFull.Identity.CharacterId].Backpack);
        var csv=File.ReadAllText(service.Files(account).Inventory);Assert.Contains("new-full",csv);Assert.DoesNotContain("old-full",csv);
    }
    [Fact] public void FullSyncHashAndObservationTimeReachInventoryCsv()
    {
        var stream=new StreamingInventory(_=>InventoryTests.Login(),"capture");
        stream.Accept(new(0,GameProtocol.LoginOpcode,0,[1]),"connection",1);
        stream.Accept(new(0,GameProtocol.ProfileOpcode,0,InventoryTests.Profile(9007199254740993)),"connection",2);
        var snapshot=stream.Snapshot();Assert.Equal("digest",snapshot.FrameSha256);Assert.NotNull(snapshot.ObservedAt);
        var service=new AccountItemService(directory);service.Record(snapshot);
        var lines=File.ReadAllLines(service.Files(snapshot.Identity!.AccountId).Inventory).Select(line=>line.Split(',')).ToArray();
        var headers=lines[0];var hash=Array.IndexOf(headers,"frame_sha256");var observed=Array.IndexOf(headers,"observed_at");
        Assert.True(hash>=0&&observed>=0);Assert.All(lines.Skip(1),row=>Assert.Equal("digest",row[hash]));
        Assert.All(lines.Skip(1),row=>Assert.Equal(snapshot.ObservedAt.Value,DateTimeOffset.Parse(row[observed])));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
