using System.IO;
using System.Text.Json.Nodes;
using MabiAgent.Core;
namespace MabiAgent.Desktop.Editors;
public sealed record AnchorReference(JsonArray Path,string Label);
public sealed record AnchorEntry(string Id,string Name,string Namespace,JsonArray Path,JsonObject Spec,IReadOnlyList<AnchorReference> References)
{ public string Label=>$"{(References.Count>1?"通用":"独立")} · {DisplayName} · {References.Count} 处引用"; public string DisplayName=>Id.StartsWith("reading:",StringComparison.Ordinal)?"数量读取":Namespace is "quantity" or "maintenance"?"数量或维护图片":Name.Contains("material",StringComparison.Ordinal)?"材料名称":Name.Contains("name",StringComparison.Ordinal)?"目标名称":Name.Contains("craft",StringComparison.Ordinal)?"加工按钮":Name.Contains("collect",StringComparison.Ordinal)?"领取按钮":Name.Contains("title",StringComparison.Ordinal)?"页面标题":"页面或操作锚点"; }
public sealed partial class EditorRepository
{
    public JsonObject DebugState()=>File.Exists(DebugPath)?Read(DebugPath):new JsonObject{["schema_version"]=1,["revision"]=0,["history"]=new JsonArray(new JsonObject{["revision"]=0,["changes"]=new JsonArray(),["label"]="安装预设",["saved_at"]=null})};
    public void ApplyDebug(JsonObject routes){var state=DebugState();foreach(var c in state["history"]!.AsArray().Last()!["changes"]!.AsArray())SetPath(routes,c!["path"]!.AsArray(),c["value"]);}
    public JsonObject EffectiveRoutes()
    {var config=PublishedCatalogCompiler.Compile(this);var copy=new JsonObject{["ui_profile"]=config["ui"]!.DeepClone()};foreach(var key in new[]{"devices","recipes","gathering_observer","quantity","maintenance"})copy[key]=config[key]!.DeepClone();var navigation=new JsonObject();foreach(var(k,v)in config["gathering_routes"]?.AsObject()??[])navigation[k]=v?["navigation"]?.DeepClone();copy["gathering_navigation"]=navigation;return copy;}
    static void Walk(JsonNode? node,JsonArray path,Action<JsonNode?,JsonArray> visit)
    {visit(node,path);if(node is JsonObject o)foreach(var(k,v)in o){var p=(JsonArray)path.DeepClone();p.Add(k);Walk(v,p,visit);}else if(node is JsonArray a)for(int i=0;i<a.Count;i++){var p=(JsonArray)path.DeepClone();p.Add(i);Walk(a[i],p,visit);}}
    public IReadOnlyList<AnchorEntry> Anchors()
    {
        var routes=EffectiveRoutes();var result=new List<AnchorEntry>();var allReferences=new Dictionary<string,List<AnchorReference>>(); Walk(routes,new JsonArray(),(v,p)=>{if(v is JsonValue j&&j.TryGetValue<string>(out var s)&&!(p.Count>1&&p[1]?.ToString()=="templates")){if(!allReferences.TryGetValue(s,out var list))allReferences[s]=list=[];list.Add(new((JsonArray)p.DeepClone(),string.Join(" / ",p.Select(x=>x!.ToString()))));}});
        foreach(var space in new[]{"ui_profile","gathering_observer"})foreach(var(name,n)in routes[space]?["templates"]?.AsObject()??[])
        {if(n is not JsonObject spec||spec["path"] is null)continue;var references=allReferences.GetValueOrDefault(name)??[];

        result.Add(new(space+":"+name,name,space,new JsonArray(space,"templates",name),(JsonObject)spec.DeepClone(),references));}
        foreach(var space in new[]{"quantity","maintenance"})Walk(routes[space],new JsonArray(space),(node,path)=>
        {
            if(node is not JsonObject spec)return;
            var quantity=spec["region"] is JsonArray&&spec["item_id"] is not null&&spec["scope"] is not null;
            var image=spec["path"] is JsonValue&&(spec["sha256"] is not null||spec["label_threshold"] is not null);
            if(quantity||image){var id=(quantity?"reading:":"inline:")+path.ToJsonString();result.Add(new(id,string.Join(" / ",path.Select(x=>x!.ToString())),space,(JsonArray)path.DeepClone(),(JsonObject)spec.DeepClone(),[new((JsonArray)path.DeepClone(),"内联配置")]));}
            if(spec["label_template"] is JsonValue legacy&&spec["label_templates"] is null){var full=ResolveImagePath(legacy.GetValue<string>());var inline=new JsonObject{["path"]=legacy.DeepClone(),["sha256"]=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full))),["label_threshold"]=spec["label_threshold"]?.DeepClone()??JsonValue.Create(90),["label_score"]=spec["label_score"]?.DeepClone()??JsonValue.Create(.97)};result.Add(new("legacy:"+path.ToJsonString(),string.Join(" / ",path.Select(x=>x!.ToString())),space,(JsonArray)path.DeepClone(),inline,[new((JsonArray)path.DeepClone(),"旧版内联图片")]));}
        });
        return result;
    }
    public static JsonNode? GetPath(JsonNode root,JsonArray path)
    {JsonNode? node=root;foreach(var part in path)node=part is JsonValue p&&p.TryGetValue<int>(out var i)?node?[i]:node?[part!.GetValue<string>()];return node;}
    static void SetPath(JsonNode root,JsonArray path,JsonNode? value)
    {
        if(path.Count<2||!new[]{"ui_profile","devices","recipes","gathering_navigation","gathering_observer","quantity","maintenance"}.Contains(path[0]!.GetValue<string>()))throw new InvalidDataException("锚点路径无效");
        JsonNode current=root;for(int i=0;i<path.Count-1;i++){var part=path[i]!;if(part is JsonValue p&&p.TryGetValue<int>(out var index))current=current[index]??throw new InvalidDataException("引用不存在");else{var key=part.GetValue<string>();current[key]??=new JsonObject();current=current[key]!;}}
        if(path.Last() is JsonValue last&&last.TryGetValue<int>(out var at)){var a=current.AsArray();if(at<0||at>=a.Count)throw new InvalidDataException("引用不存在");a[at]=value?.DeepClone();}else current[path.Last()!.GetValue<string>()]=value?.DeepClone();
    }
    public int SaveAnchor(int revision,AnchorEntry entry,JsonObject spec,bool independent,AnchorReference? reference)
    {
        lock(gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DebugPath)!);using var lease=new FileStream(DebugPath+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose);var state=DebugState();if(state["revision"]!.GetValue<int>()!=revision)throw new IOException("锚点已更新，请刷新");var current=Anchors().SingleOrDefault(a=>a.Id==entry.Id)??throw new InvalidDataException("锚点不存在");
            var next=CatalogLoader.Merge(current.Spec,spec);if(!next.ContainsKey("region")&&(current.Id.StartsWith("reading:",StringComparison.Ordinal)))throw new InvalidDataException("数量区域缺失");foreach(var key in new[]{"box","search","crop_native","region"})if(next[key] is {} box)ValidateBox(box,Catalog["client_size"]!.AsArray());
            if(next["box"] is JsonArray b&&next["search"] is JsonArray search&&(search[0]!.GetValue<int>()>b[0]!.GetValue<int>()||search[1]!.GetValue<int>()>b[1]!.GetValue<int>()||search[2]!.GetValue<int>()<b[2]!.GetValue<int>()||search[3]!.GetValue<int>()<b[3]!.GetValue<int>()))throw new InvalidDataException("搜索范围必须包含锚点区域");if(!Modes.Contains(Text(next,"match_mode","grayscale")))throw new InvalidDataException("识别方式无效");var threshold=next["threshold"]?.GetFiniteDouble()??.97;if(!double.IsFinite(threshold)||threshold is <.7 or >1)throw new InvalidDataException("阈值须为0.7至1");
            if(current.Id.StartsWith("reading:",StringComparison.Ordinal)){ValidateShape(current.Spec,next);if(independent)throw new InvalidDataException("内联数量读取不能拆分");var readings=(JsonArray)state["history"]!.AsArray().Last()!["changes"]!.DeepClone();AddChange(readings,current.Path,next);return CommitDebug(state,readings,"修改数量读取："+entry.Name);}
            var path=ResolveImagePath(Text(next,"path"));if(!File.Exists(path))throw new InvalidDataException("图片不存在");var digest=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));if(Text(next,"sha256")!=digest)throw new InvalidDataException("图片校验失败");
            var changes=(JsonArray)state["history"]!.AsArray().Last()!["changes"]!.DeepClone();
            if(independent&&(current.Id.StartsWith("inline:",StringComparison.Ordinal)||current.Id.StartsWith("legacy:",StringComparison.Ordinal)))throw new InvalidDataException("内联图片不能拆分");
            if(independent)
            {if(reference is null||!current.References.Any(r=>JsonNode.DeepEquals(r.Path,reference.Path)))throw new InvalidDataException("请选一个实际引用拆分");var name="debug_"+Guid.NewGuid().ToString("N")[..20];AddChange(changes,new JsonArray(entry.Namespace,"templates",name),next);AddChange(changes,reference.Path,JsonValue.Create(name));var variants=EffectiveRoutes()[entry.Namespace]?["anchor_variants"]?[entry.Name];if(variants is not null)AddChange(changes,new JsonArray(entry.Namespace,"anchor_variants",name),variants);}
            else if(current.Id.StartsWith("legacy:",StringComparison.Ordinal)){var p=(JsonArray)current.Path.DeepClone();p.Add("label_templates");AddChange(changes,p,new JsonArray(next.DeepClone()));}else AddChange(changes,current.Path,next);
            return CommitDebug(state,changes,"修改锚点："+entry.Name);
        }
    }
    public string ResolveImagePath(string source)
    {var installed=SafePath(Root,"assets/templates");var full=Path.GetFullPath(Path.Combine(Root,source));if(full.StartsWith(installed+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return SafePath(installed,Path.GetRelativePath(installed,full));var assetRoot=SafePath(ChainsDirectory,"assets");return SafePath(assetRoot,Path.GetRelativePath(assetRoot,full));}
    static void AddChange(JsonArray changes,JsonArray path,JsonNode? value)
    {foreach(var previous in changes.Where(c=>JsonNode.DeepEquals(c!["path"],path)).ToArray())changes.Remove(previous);changes.Add(new JsonObject{["path"]=path.DeepClone(),["value"]=value?.DeepClone()});}
    int CommitDebug(JsonObject state,JsonArray changes,string label)
    {var revision=state["revision"]!.GetValue<int>()+1;state["revision"]=revision;state["history"]!.AsArray().Add(new JsonObject{["revision"]=revision,["changes"]=changes,["label"]=label,["saved_at"]=DateTimeOffset.UtcNow.ToString("O")});Directory.CreateDirectory(Path.GetDirectoryName(DebugPath)!);AtomicJson.Write(DebugPath,state);CatalogChanged?.Invoke();return revision;}
    public int RestoreAnchors(int currentRevision,int targetRevision)
    {lock(gate){var state=DebugState();if(state["revision"]!.GetValue<int>()!=currentRevision)throw new IOException("锚点已更新，请刷新");var target=state["history"]!.AsArray().Single(h=>h!["revision"]!.GetValue<int>()==targetRevision)!;return CommitDebug(state,(JsonArray)target["changes"]!.DeepClone(),"恢复到版本 "+targetRevision);}}
}








