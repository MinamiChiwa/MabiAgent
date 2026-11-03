using System.IO;
using System.Text.Json.Nodes;
namespace MabiAgent.Desktop.Editors;
public sealed record LogicEntry(JsonArray Path,string Label,JsonNode? Value);
public sealed partial class EditorRepository
{
    static readonly HashSet<string> ProtectedLogicFields=["templates","root","source","contract","enabled","capabilities","coordinates_for_input","path","sha256","bytes","label_template","trusted_asset_roots"];
    public IReadOnlyList<LogicEntry> LogicEntries()
    {var routes=EffectiveRoutes();var result=new List<LogicEntry>();foreach(var(root,value)in routes)if(value is JsonObject section)foreach(var(key,node)in section)if(!ProtectedLogicFields.Contains(key))result.Add(new(new JsonArray(root,key),root+" / "+key,node?.DeepClone()));return result;}
    public int SaveLogic(int revision,JsonArray path,JsonNode? value)
    {
        lock(gate)
        {
            if(path.Count<2||path.Count>16||path.Any(p=>p is JsonValue v&&v.TryGetValue<string>(out var s)&&ProtectedLogicFields.Contains(s)))throw new InvalidDataException("只能修改现有逻辑数据");
            if(value?.ToJsonString().Length>65536)throw new InvalidDataException("逻辑节点过大");var state=DebugState();if(state["revision"]!.GetValue<int>()!=revision)throw new IOException("配置已更新，请刷新");var routes=EffectiveRoutes();var old=GetPath(routes,path)??throw new InvalidDataException("逻辑节点不存在");ValidateShape(old,value);
            var anchors=Anchors().Select(a=>a.Name).ToHashSet();Walk(old,new JsonArray(),(v,p)=>{if(v is JsonValue s&&s.TryGetValue<string>(out var name)&&anchors.Contains(name)){var next=GetPath(value!,p)?.GetValue<string>();if(next is null||!anchors.Contains(next))throw new InvalidDataException("锚点引用必须指向已存在锚点");}});
            Walk(value,path,(v,p)=>{if(p.LastOrDefault()?.ToString() is "box" or "search" or "region" or "control_box" or "craft_button_box")ValidateBox(v!,Catalog["client_size"]!.AsArray());});
            var changes=(JsonArray)state["history"]!.AsArray().Last()!["changes"]!.DeepClone();AddChange(changes,path,value);return CommitDebug(state,changes,"修改逻辑："+string.Join(" / ",path));
        }
    }
    static void ValidateShape(JsonNode? old,JsonNode? value)
    {
        if(old is JsonObject o){if(value is not JsonObject n||!o.Select(x=>x.Key).Order().SequenceEqual(n.Select(x=>x.Key).Order()))throw new InvalidDataException("逻辑字段须保持完整");foreach(var(k,v)in o){if(ProtectedLogicFields.Contains(k)&&!JsonNode.DeepEquals(v,n[k]))throw new InvalidDataException("图片来源与权限字段不可通过逻辑修改");ValidateShape(v,n[k]);}}
        else if(old is JsonArray a){if(value is not JsonArray b||a.Count>0&&b.Count==0||a.Count==0&&b.Count>0)throw new InvalidDataException("逻辑列表结构无效");for(int i=0;i<b.Count;i++)ValidateShape(a[Math.Min(i,a.Count-1)],b[i]);}
        else if(old is JsonValue v){if(value is not JsonValue x||v.GetValueKind()!=x.GetValueKind())throw new InvalidDataException("逻辑参数类型不可改变");if(x.GetValueKind()==System.Text.Json.JsonValueKind.String&&string.IsNullOrWhiteSpace(x.GetValue<string>()))throw new InvalidDataException("文字不能为空");}
        else if(value is not null)throw new InvalidDataException("该空字段不可改变");
    }
}
