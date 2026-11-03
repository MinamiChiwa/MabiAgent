using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MabiAgent.Core;
namespace MabiAgent.Desktop.Editors;
public sealed partial class EditorRepository
{
    readonly object gate=new(); readonly Func<string,long?>? resolveVerifiedName;
    public string Root {get;}
    public string StateDirectory {get;}
    public void ReportFault(Exception error,string operation,string? item=null)
    {var log=new RuntimeDiagnostics(StateDirectory);log.Write(log.Create(Guid.NewGuid().ToString("N"),"error","editor",operation,"editor",null,item,null,null,"correct_then_retry",error.Message,error,trigger:operation));}
    public JsonObject Catalog {get;}
    public JsonObject Routes {get;}
    public IReadOnlyList<JsonObject> Templates {get;}
    public event Action? CatalogChanged;
    string ChainsDirectory=>SafePath(StateDirectory,"task-chains");
    string DebugPath=>SafePath(StateDirectory,"anchor-debug/versions.json");
    public EditorRepository(string root,string stateDirectory,Func<string,long?>? resolveVerifiedName=null)
    {
        Root=Path.GetFullPath(root);StateDirectory=Path.GetFullPath(stateDirectory);this.resolveVerifiedName=resolveVerifiedName;
        var loader=new CatalogLoader(Root);Catalog=loader.Load("config/screen_catalog.1280x960.tw.json");
        var ui=loader.Load(Text(Catalog,"navigation_profile"));
        if(Catalog["ui"] is JsonObject extra)foreach(var(k,v)in extra)
        {if(v is JsonObject o){ui[k]??=new JsonObject();foreach(var(a,b)in o)ui[k]![a]=b?.DeepClone();}else ui[k]=v?.DeepClone();}
        if(Catalog["template_sources"] is JsonObject sources)foreach(var(k,v)in sources)
        {JsonNode spec=loader.Load(Text(v!,"profile"));foreach(var part in v!["select"]!.AsArray())spec=part is JsonValue x&&x.TryGetValue<int>(out var i)?spec[i]!:spec[part!.GetValue<string>()]!;var s=(JsonObject)spec.DeepClone();if(v["overrides"] is JsonObject over)foreach(var(a,b)in over)s[a]=b?.DeepClone();s["search"]??=s["box"]?.DeepClone();s["threshold"]??=JsonValue.Create(.97);ui["templates"]![k]=s;}
        if(Catalog["ui"]?["templates"] is JsonObject replacements)foreach(var(k,v)in replacements)ui["templates"]![k]=v?.DeepClone();
        if(Catalog["ui_remove"] is JsonObject remove)foreach(var(k,v)in remove)foreach(var name in v!.AsArray())ui[k]?.AsObject().Remove(name!.GetValue<string>());
        Routes=new JsonObject{["ui_profile"]=ui};
        foreach(var key in new[]{"devices","recipes","gathering_observer","quantity","maintenance"})Routes[key]=Catalog[key]?.DeepClone()??new JsonObject();
        var nav=new JsonObject();foreach(var(k,v)in Catalog["gathering_routes"]?.AsObject()??[])nav[k]=v?["navigation"]?.DeepClone();Routes["gathering_navigation"]=nav;
        Templates=BuildTemplates();
    }
    public static string Text(JsonNode node,string key,string fallback="")=>node[key]?.GetValue<string>()??fallback;
    public static string SafePath(string root,string relative)
    {
        var fullRoot=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));var path=Path.GetFullPath(Path.Combine(fullRoot,relative));
        if(!path.StartsWith(fullRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("路径越出允许目录");
        for(var at=path;at.Length>=fullRoot.Length;at=Path.GetDirectoryName(at)??"")if((File.Exists(at)||Directory.Exists(at))&&(File.GetAttributes(at)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("路径不得经过链接或目录联接");
        return path;
    }
    public static void ValidateId(string id){if(!Regex.IsMatch(id,@"\A[a-zA-Z0-9_-]{1,80}\z"))throw new InvalidDataException("任务链或图片标识无效");}
    static JsonObject Read(string path)=>JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    public IReadOnlyList<JsonObject> List()
    {var dir=SafePath(ChainsDirectory,"chains");return !Directory.Exists(dir)?[]:Directory.EnumerateDirectories(dir).Select(d=>SafePath(ChainsDirectory,"chains/"+Path.GetFileName(d)+"/chain.json")).Where(File.Exists).Select(Read).ToArray();}
    public IReadOnlyList<JsonObject> History(string id)
    {ValidateId(id);var dir=SafePath(ChainsDirectory,$"chains/{id}/revisions");return !Directory.Exists(dir)?[]:Directory.EnumerateFiles(dir,"v*.json").Select(p=>Read(SafePath(dir,Path.GetFileName(p)))).OrderByDescending(j=>j["revision"]!.GetValue<int>()).ToArray();}
    public JsonObject CreateDraft(string templateKey)
    {var j=(JsonObject)Templates.Single(t=>Text(t,"template_key")==templateKey).DeepClone();j["id"]="";j["revision"]=0;j["status"]="draft";return j;}
    public JsonObject RestorePreset(JsonObject current)
    {var j=CreateDraft(Text(current,"template_key"));PreserveExtensions(j,current);j["id"]=current["id"]?.DeepClone();j["revision"]=current["revision"]?.DeepClone();return j;}
    static void PreserveExtensions(JsonNode target,JsonNode source)
    {
        if(target is JsonObject to&&source is JsonObject from)foreach(var(k,v)in from)
        {if(!to.ContainsKey(k))to[k]=v?.DeepClone();else if(to[k] is {} t&&v is not null)PreserveExtensions(t,v);}
        else if(target is JsonArray ta&&source is JsonArray sa)
        {for(var i=0;i<ta.Count;i++){var t=ta[i];if(t is null)continue;JsonNode? original=null;if(t is JsonObject o){var key=o.ContainsKey("id")?"id":o.ContainsKey("key")?"key":o.ContainsKey("item_id")?"item_id":null;if(key is not null){original=sa.FirstOrDefault(n=>n is JsonObject&&JsonNode.DeepEquals(n[key],o[key]));if(original is null)continue;}}original??=i<sa.Count?sa[i]:null;if(original is not null)PreserveExtensions(t,original);}}
    }
    public JsonObject Save(JsonObject document,bool publish)
    {
        lock(gate)
        {
            var value=(JsonObject)document.DeepClone();var id=Text(value,"id");if(id.Length==0)id="chain_"+Guid.NewGuid().ToString("N")[..16];ValidateId(id);
            var folder=SafePath(ChainsDirectory,$"chains/{id}");Directory.CreateDirectory(folder);
            using var lease=new FileStream(SafePath(folder,"editor.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose);
            var path=SafePath(folder,"chain.json");var previous=File.Exists(path)?Read(path):null;
            if(previous is not null&&previous["revision"]!.GetValue<int>()!=(value["revision"]?.GetValue<int>()??0))throw new IOException("任务链已更新，请重新打开");
            var p=value["parameters"]!.AsObject();var known=Catalog["item_names"]?.AsObject()??new JsonObject();
            if(!string.IsNullOrWhiteSpace(Text(p,"target_item_name")))p["target_item_id"]=ResolveItem(Text(p,"target_item_name"),known,publish);
            foreach(var row in p["inputs"]?.AsArray()??[])if(!string.IsNullOrWhiteSpace(Text(row!,"name")))row!["item_id"]=ResolveItem(Text(row,"name"),known,publish);
            var originalTemplate=Templates.Single(t=>Text(t,"template_key")==Text(value,"template_key"));SynchronizeSharedAnchors(value,previous??originalTemplate);ValidateChain(value,publish);
            value["id"]=id;value["revision"]=(previous?["revision"]?.GetValue<int>()??0)+1;value["status"]=publish?"published":"draft";value["verification"]="user_defined_untested";
            var template=Templates.Single(t=>Text(t,"template_key")==Text(value,"template_key"));var same=JsonNode.DeepEquals(p,template["parameters"])&&JsonNode.DeepEquals(value["nodes"],template["nodes"]);
            value["runtime_key"]=id==PresetId(Text(value,"template_key"))||same?Text(template,"runtime_key"):"custom_"+id;
            if(publish)PublishedCatalogCompiler.Compile(this,List().Where(c=>Text(c,"id")!=id).Append(value));
            Directory.CreateDirectory(SafePath(folder,"revisions"));AtomicJson.Write(SafePath(folder,$"revisions/v{value["revision"]}.json"),value);AtomicJson.Write(path,value);StorageCleanup.PruneRevisions(folder);
            CatalogChanged?.Invoke();return value;
        }
    }
    public void Delete(JsonObject value)
    {
        var id=Text(value,"id");ValidateId(id);lock(gate)
        {var source=SafePath(ChainsDirectory,$"chains/{id}");var current=Read(SafePath(source,"chain.json"));if(!JsonNode.DeepEquals(current["revision"],value["revision"]))throw new IOException("任务链已更新，请重新打开");
        var target=SafePath(ChainsDirectory,$"deleted/{id}_{Guid.NewGuid():N}");Directory.CreateDirectory(Path.GetDirectoryName(target)!);Directory.Move(source,target);StorageCleanup.MarkDeleted(target,DateTimeOffset.UtcNow);CatalogChanged?.Invoke();}
    }
    public StorageCleanup.Result CleanupStorage()
    {
        lock(gate)return StorageCleanup.Run(ChainsDirectory,DateTimeOffset.UtcNow);
    }
    public void ValidateChain(JsonObject value,bool publish)
    {
        var template=Templates.SingleOrDefault(t=>Text(t,"template_key")==Text(value,"template_key"))??throw new InvalidDataException("请选择已安装预设");
        if(string.IsNullOrWhiteSpace(Text(value,"name")))throw new InvalidDataException("请填写任务名称");
        foreach(var f in new[]{"schema_version","kind","client_size","dpi"})if(!JsonNode.DeepEquals(value[f],template[f]))throw new InvalidDataException("任务类型与校准布局不能改变");
        var originals=template["nodes"]!.AsArray().Select(n=>n!.AsObject()).ToArray();var nodes=value["nodes"]!.AsArray();var ids=nodes.Select(n=>Text(n!,"id")).ToArray();
        if(ids.Distinct().Count()!=ids.Length||!ids.SequenceEqual(originals.Select(n=>Text(n,"id")).Where(ids.Contains)))throw new InvalidDataException("节点须保持预设操作顺序");
        var required=Text(value,"kind")=="production"?new[]{"open_recipe","recipe","result"}:new[]{"start","observation"};
        if(publish&&required.Except(ids).Any())throw new InvalidDataException("请补回执行与结果确认节点后发布");
        var size=value["client_size"]!.AsArray();
        foreach(var n in nodes)
        {var original=originals.Single(o=>Text(o,"id")==Text(n!,"id"));foreach(var f in new[]{"id","page","target","expected_page","role"})if(!JsonNode.DeepEquals(n![f],original[f]))throw new InvalidDataException("不能插入未知动作");
        var currentAnchors=n!["anchors"]!.AsArray();var anchorKeys=currentAnchors.Select(a=>Text(a!,"key")).ToArray();var originalAnchors=original["anchors"]!.AsArray();var requiredAnchors=originalAnchors.Where(a=>!(Text(n,"id")=="recipe"&&Material(a!))).Select(a=>Text(a!,"key"));if(anchorKeys.Distinct().Count()!=anchorKeys.Length||requiredAnchors.Except(anchorKeys).Any())throw new InvalidDataException("原有按钮与身份锚点须保留");foreach(var extra in anchorKeys.Except(originalAnchors.Select(a=>Text(a!,"key"))))if(Text(n,"id")!="recipe"||!Regex.IsMatch(extra,@"\Arecipe_material_[1-9]\d{0,2}\z"))throw new InvalidDataException("仅配方节点可增加材料锚点");foreach(var a in currentAnchors)
        {var variants=a!["variants"]!.AsArray();if(variants.Count>12||publish&&variants.Count==0)throw new InvalidDataException("每个锚点需要1至12种状态图片");
        foreach(var v in variants){ValidateBox(v!["box"]!,size);ValidateBox(v["search"]!,size);var b=v["box"]!.AsArray().Select(x=>x!.GetValue<int>()).ToArray();var search=v["search"]!.AsArray().Select(x=>x!.GetValue<int>()).ToArray();if(search[0]>b[0]||search[1]>b[1]||search[2]<b[2]||search[3]<b[3])throw new InvalidDataException("搜索范围须包含锚点区域");if(v["asset"]?["width"]?.GetValue<int>()!=b[2]-b[0]||v["asset"]?["height"]?.GetValue<int>()!=b[3]-b[1])throw new InvalidDataException("锚点尺寸与原生区域不一致");var threshold=v["threshold"]!.GetFiniteDouble();if(!double.IsFinite(threshold)||threshold<.7||threshold>1)throw new InvalidDataException("阈值须为0.7至1");if(!Modes.Contains(Text(v,"match_mode")))throw new InvalidDataException("识别方式无效");
        if(publish){var asset=v["asset"]!;var path=AssetPath(Text(asset,"id"));if(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))!=Text(asset,"sha256"))throw new InvalidDataException("图片校验失败");}}}}
        if(publish){var p=value["parameters"]!;if(Text(value,"kind")=="gathering"&&Catalog["gathering_points"]?[Text(template,"runtime_key")]?["execution"]?.GetValue<string>()=="navigation_only")throw new InvalidDataException("该采集预设仅完成导航校准，不能发布为普通采集");if(Text(value,"kind")=="gathering"&&(p["attempts_per_batch"]?.GetValue<int>() is not >0))throw new InvalidDataException("采集次数须大于零");if(string.IsNullOrWhiteSpace(Text(p,"target_item_name")))throw new InvalidDataException("请填写产物名称");if(Text(value,"kind")=="production"){if(double.Parse(p["duration_s"]!.ToJsonString(),System.Globalization.CultureInfo.InvariantCulture)<=0||p["output_per_round"]!.GetValue<long>()<=0)throw new InvalidDataException("耗时与产量须大于零");foreach(var row in p["inputs"]!.AsArray())if(string.IsNullOrWhiteSpace(Text(row!,"name"))||row!["quantity"]!.GetValue<long>()<=0)throw new InvalidDataException("请填写材料名称和数量");}}
    }
    public string UserAssetRoot=>SafePath(ChainsDirectory,"assets");
    public static IReadOnlyList<RecognitionModeChoice> ModeChoices {get;}=[new("grayscale","灰度图像"),new("portrait_core","头像内脸（抗锯齿）"),new("light_text","亮色文字字形"),new("bright_text","文字字形（兼容变色）"),new("color_glyph","彩色图案"),new("red_glyph","红色图案"),new("yellow_text","黄色文字"),new("gold_text","金色文字"),new("gray","灰度兼容")];
    public static string[] Modes {get;}=ModeChoices.Select(choice=>choice.Key).ToArray();
    public static void ValidateBox(JsonNode node,JsonArray size)
    {var b=node.AsArray().Select(v=>v!.GetValue<int>()).ToArray();if(b.Length!=4||b[0]<0||b[1]<0||b[2]<=b[0]||b[3]<=b[1]||b[2]>size[0]!.GetValue<int>()||b[3]>size[1]!.GetValue<int>())throw new InvalidDataException("区域须位于原生客户区内");}
    public static void SynchronizeSharedAnchors(JsonObject value,JsonObject original)
    {
        var changed=new Dictionary<string,JsonNode?>();var originals=original["nodes"]!.AsArray().ToDictionary(n=>Text(n!,"id"));
        foreach(var node in value["nodes"]!.AsArray())
        {
            var old=originals.GetValueOrDefault(Text(node!,"id"));if(old is null)continue;
            foreach(var anchor in node!["anchors"]!.AsArray())
            {var key=Text(anchor!,"key");var prior=old["anchors"]!.AsArray().FirstOrDefault(a=>Text(a!,"key")==key);if(prior is not null&&JsonNode.DeepEquals(prior["variants"],anchor!["variants"]))continue;
             if(changed.TryGetValue(key,out var previous)&&!JsonNode.DeepEquals(previous,anchor!["variants"]))throw new InvalidDataException("同一通用锚点有不同修改，请统一后保存");changed[key]=anchor!["variants"]?.DeepClone();}
        }
        foreach(var node in value["nodes"]!.AsArray())foreach(var anchor in node!["anchors"]!.AsArray())if(changed.TryGetValue(Text(anchor!,"key"),out var variants))anchor!["variants"]=variants?.DeepClone();
    }
    public static string PresetId(string key)=>"preset_"+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    long ResolveItem(string name,JsonObject known,bool publish) { var normalized=NormalizeItemName(name);var candidates=known.Where(p=>NormalizeItemName(p.Value!.GetValue<string>())==normalized).Select(p=>long.Parse(p.Key)).Distinct().ToArray();if(candidates.Length>1)throw new InvalidDataException("道具名称对应多个编号，请在账户道具表明确关联："+name);if(candidates.Length==1)return candidates[0];if(resolveVerifiedName is not null){var verified=resolveVerifiedName(name);if(verified is >0)return verified.Value;}if(publish)throw new InvalidDataException("道具尚未与已验证名称关联："+name);return ResolveName(name,known); }
    static string NormalizeItemName(string name)=>name.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
    public static long ResolveName(string name,JsonObject known)
    {
        var normalized=name.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();if(normalized.Length==0)throw new InvalidDataException("道具名不能为空");

        var matches=known.Where(p=>NormalizeItemName(p.Value!.GetValue<string>())==normalized).Select(p=>long.Parse(p.Key)).Distinct().ToArray();if(matches.Length>1)throw new InvalidDataException("道具名称存在歧义");if(matches.Length==1)return matches[0];
        long hash=0;foreach(var b in SHA256.HashData(Encoding.UTF8.GetBytes(normalized)).Take(6))hash=(hash<<8)|b;return (1L<<52)+hash;
    }
    static bool Material(JsonNode a)=>Regex.IsMatch(Text(a,"key"),"material|^detail_ore_");
    public static void SynchronizeMaterials(JsonObject chain,JsonArray? previous=null)
    {
        if(Text(chain,"kind")!="production")return;var node=chain["nodes"]?.AsArray().FirstOrDefault(n=>Text(n!,"id")=="recipe");if(node is null)return;
        var anchors=node["anchors"]!.AsArray();var old=anchors.Where(a=>Material(a!)&&Text(a!,"key")!="detail_ore_ten").Select(a=>a!.AsObject()).ToList();var used=new HashSet<JsonObject>();var next=new List<JsonObject>();
        var rows=chain["parameters"]!["inputs"]!.AsArray();for(int i=0;i<rows.Count;i++)
        {var row=rows[i]!;var name=Text(row,"name").Trim();var match=old.FirstOrDefault(a=>!used.Contains(a)&&Text(a,"label")==name);
        if(match is null&&previous is not null){var indexes=previous.Select((p,n)=>(p,n)).Where(x=>JsonNode.DeepEquals(x.p?["item_id"],row["item_id"])).Select(x=>x.n).ToArray();if(indexes.Length==1&&indexes[0]<old.Count&&!used.Contains(old[indexes[0]])){match=old[indexes[0]];if(Text(previous[indexes[0]]!,"name").Trim()!=name)match["variants"]=new JsonArray();}}
        if(match is null&&previous is null&&i<old.Count&&!used.Contains(old[i]))match=old[i];
        if(match is null){int number=1;while(old.Any(a=>Text(a,"key")==$"recipe_material_{number}"))number++;match=new JsonObject{["key"]=$"recipe_material_{number}",["variants"]=new JsonArray()};old.Add(match);}
        used.Add(match);match["label"]=name.Length>0?name:$"材料 {i+1}";next.Add((JsonObject)match.DeepClone());}
        var insert=anchors.Select((a,i)=>(a,i)).FirstOrDefault(x=>Material(x.a!)).i;var kept=anchors.Where(a=>!Material(a!)).Select(a=>a!.DeepClone()).ToList();kept.InsertRange(Math.Min(insert,kept.Count),next);node["anchors"]=new JsonArray(kept.ToArray());
    }
    readonly Dictionary<string,string> installedAssets=new();
    public string AssetPath(string id)
    {ValidateId(id);if(installedAssets.TryGetValue(id,out var path))return SafePath(Root,Path.GetRelativePath(Root,path));if(!id.StartsWith("image_")||id.Length!=70)throw new InvalidDataException("图片不属于任务链资源");return SafePath(ChainsDirectory,$"assets/{id}/original.bin");}
    public JsonObject ImportAsset(byte[] png,int width,int height,int[] box,byte[]? previewJpeg=null)
    {if(png.Length<24||!png.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})||System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16,4))!=width||System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20,4))!=height)throw new InvalidDataException("需要尺寸一致的PNG图片");if(previewJpeg is {Length: >1048576})throw new InvalidDataException("预览不得超过1 MiB");ValidateBox(new JsonArray(box.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),Catalog["client_size"]!.AsArray());if(box[2]-box[0]!=width||box[3]-box[1]!=height)throw new InvalidDataException("图片和原生区域尺寸不一致");if(png.Length>8*1024*1024||width<=0||height<=0||(long)width*height>16_000_000)throw new InvalidDataException("图片超出上限");var sha=Convert.ToHexStringLower(SHA256.HashData(png));var id="image_"+sha;var folder=SafePath(ChainsDirectory,$"assets/{id}");Directory.CreateDirectory(folder);var asset=new JsonObject{["id"]=id,["sha256"]=sha,["width"]=width,["height"]=height,["bytes"]=png.Length,["source"]="selected_game_window_crop",["filename"]="anchor.png",["preview_url"]="chain-assets/"+id+"/preview",["coordinates_for_input"]=false,["box"]=new JsonArray(box.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray())};if(!File.Exists(SafePath(folder,"original.bin")))File.WriteAllBytes(SafePath(folder,"original.bin"),png);if(previewJpeg is not null){File.WriteAllBytes(SafePath(folder,"preview.jpg"),previewJpeg);asset["preview_bytes"]=previewJpeg.Length;}var manifest=SafePath(folder,"manifest.json");if(File.Exists(manifest))asset=CatalogLoader.Merge(Read(manifest),asset);AtomicJson.Write(manifest,asset);return asset;}
}









public sealed record RecognitionModeChoice(string Key,string Label);


