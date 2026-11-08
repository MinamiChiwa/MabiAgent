namespace MabiAgent.Capture;

/// <summary>仓库可信名称沿用已有目录键；目录外道具使用实际类型编号。</summary>
public static class LinkedItemCatalog
{
    public static IReadOnlyDictionary<long,string> Names(ItemNameRegistry registry,IReadOnlyDictionary<long,string> catalog)
    {
        var result=new Dictionary<long,string>();
        foreach(var row in registry.Snapshot().Where(r=>r.Captured&&r.Association is NameAssociation.UserConfirmed or NameAssociation.BuiltinVerified))
        {
            if(string.IsNullOrWhiteSpace(row.Name)||registry.ResolveVerified(row.Name)!=row.ItemId)continue;
            var matches=catalog.Where(p=>ItemNameRegistry.Normalize(p.Value)==ItemNameRegistry.Normalize(row.Name)).ToArray();
            if(matches.Length>1)continue;
            var key=matches.Length==1?matches[0].Key:row.ItemId;
            if(catalog.TryGetValue(key,out var existing)&&ItemNameRegistry.Normalize(existing)!=ItemNameRegistry.Normalize(row.Name))continue;
            result[key]=row.Name;
        }
        return result;
    }
}
