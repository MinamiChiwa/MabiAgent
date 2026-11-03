using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace MabiAgent.Capture;

public sealed partial class AccountItemService
{
    public int? CharacterSlot(string accountId,string characterId)
    {
        lock(gate)
        {
            var metadata=ReadIndex()[accountId];if(metadata is null)return null;
            var slots=ReadCharacterSlots(metadata);
            return slots.Where(p=>p.Value==characterId).Select(p=>(int?)p.Key).SingleOrDefault();
        }
    }
    public CharacterIdentity? CharacterAtSlot(string accountId,int slot)
    {
        ValidateSlot(slot);lock(gate)
        {
            var metadata=ReadIndex()[accountId];if(metadata is null)return null;
            if(!ReadCharacterSlots(metadata).TryGetValue(slot,out var id))return null;
            var names=Characters(metadata);return names.TryGetValue(id,out var name)?new(accountId,id,name):null;
        }
    }
    public void RecordCharacterSlot(string accountId,string characterId,int slot)
    {
        ValidateSlot(slot);lock(gate)
        {
            RequireAccount(accountId);var hash=Fingerprint(IndexPath);var index=ReadIndex();var metadata=index[accountId]!;
            if(!Characters(metadata).ContainsKey(characterId))throw new ArgumentException("角色尚未由该账户抓包记录");
            var slots=ReadCharacterSlots(metadata);
            if(slots.TryGetValue(slot,out var occupied)&&occupied!=characterId||slots.Any(p=>p.Value==characterId&&p.Key!=slot))
                throw new InvalidDataException("账户角色顺序关联冲突，保留原关联");
            if(occupied==characterId)return;
            metadata["character_slots"]??=new JsonObject();metadata["character_slots"]![slot.ToString(CultureInfo.InvariantCulture)]=characterId;
            AtomicWrite(IndexPath,index.ToJsonString(new JsonSerializerOptions{WriteIndented=true}),hash);Interlocked.Increment(ref version);
        }
    }
    private static void ValidateSlot(int slot){if(slot is <1 or >6)throw new ArgumentOutOfRangeException(nameof(slot),"角色顺序必须为1～6");}
    private static Dictionary<int,string> ReadCharacterSlots(JsonNode metadata)
    {
        var result=new Dictionary<int,string>();if(metadata["character_slots"] is null)return result;
        if(metadata["character_slots"] is not JsonObject slots)throw new InvalidDataException("角色顺序关联无效");
        foreach(var p in slots)
        {
            if(!int.TryParse(p.Key,NumberStyles.None,CultureInfo.InvariantCulture,out var slot)||slot is <1 or >6||p.Value is null)throw new InvalidDataException("角色顺序关联无效");
            var id=p.Value.GetValue<string>();if(string.IsNullOrWhiteSpace(id)||!Characters(metadata).ContainsKey(id)||result.ContainsValue(id))throw new InvalidDataException("角色顺序关联重复或缺少抓包身份");
            result.Add(slot,id);
        }
        return result;
    }
}
