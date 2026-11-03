using System.IO;
using System.Text.Json.Nodes;
namespace MabiAgent.Desktop.Editors;
public sealed partial class EditorRepository
{
    public JsonObject UpgradeNavigation(JsonObject document)
    {
        var value=(JsonObject)document.DeepClone();
        var template=Templates.Single(t=>Text(t,"template_key")==Text(value,"template_key"));
        foreach(var node in value["nodes"]!.AsArray())
        {
            var original=template["nodes"]!.AsArray().Single(n=>Text(n!,"id")==Text(node!,"id"))!;
            var runtimeKey=Text(template,"runtime_key");
            if(Text(node!,"id")=="open_recipe"&&
                (runtimeKey=="fabric_first"&&Text(node!,"target")=="fabric.open_first_recipe"||runtimeKey=="iron_ingot_from_iron_ore"&&Text(node!,"target")=="metal.open_iron_recipe"))
                node!["target"]=original["target"]!.DeepClone();
            foreach(var field in new[]{"page","target","expected_page","role"})
                if(!JsonNode.DeepEquals(node![field],original[field]))throw new InvalidDataException("旧链动作已改变，须人工核对");
            var aliases=new Dictionary<string,string>();var key=Text(template,"runtime_key");var id=Text(node!,"id");
            if(id=="start"&&key=="iron_ore_nearby")
            {aliases["iron_vein_detail_name"]="preset_catalog_point_mining_01_name";aliases["find_nearby"]="preset_catalog_point_mining_01_find";}
            if(key is "fabric_first" or "iron_ingot_from_iron_ore")
            {
                var prefix="preset_catalog_"+key;
                if(id=="open_recipe")aliases[key=="fabric_first"?"preset_fabric_first":"metal_iron_ore_tile"]=prefix+"_tile";
                if(id=="recipe")
                {aliases[key=="fabric_first"?"preset_fabric_detail_name":"detail_iron_name"]=prefix+"_name";aliases[key=="fabric_first"?"preset_fabric_detail_equipment":"detail_equipment"]=prefix+"_equipment";aliases[key=="fabric_first"?"preset_fabric_detail_material":"detail_ore_name"]=prefix+"_material_1";}
            }
            if(id=="recipe"&&JsonNode.DeepEquals(node,original))continue;
            UpgradeAnchors(node!,original,aliases);
        }
        return value;
    }
    static void UpgradeAnchors(JsonNode node,JsonNode original,Dictionary<string,string> aliases)
    {
        var old=node["anchors"]!.AsArray();var next=new JsonArray();
        foreach(var anchor in original["anchors"]!.AsArray())
        {
            var key=Text(anchor!,"key");var prior=old.FirstOrDefault(a=>Text(a!,"key")==key)
                ??old.FirstOrDefault(a=>aliases.GetValueOrDefault(Text(a!,"key"))==key);
            var replacement=(JsonObject)anchor!.DeepClone();
            // Refresh installed assets only; preserve user captures, thresholds and ranges.
            if(prior is not null&&prior["variants"]!.AsArray().Any(v=>Text(v!["asset"]!,"source")!="installed_template"))
            {replacement=(JsonObject)prior.DeepClone();replacement["key"]=key;}
            next.Add(replacement);
        }
        foreach(var extra in old.Where(a=>Text(node,"id")=="recipe"&&Text(a!,"key").StartsWith("recipe_material_",StringComparison.Ordinal)))next.Add(extra!.DeepClone());
        foreach(var unused in old.Where(a=>!next.Any(n=>Text(n!,"key")==Text(a!,"key"))&&!aliases.ContainsKey(Text(a!,"key"))))
            if(unused!["variants"]!.AsArray().Any(v=>Text(v!["asset"]!,"source")!="installed_template"))
            {node["legacy_navigation_anchors"]??=new JsonArray();var archive=node["legacy_navigation_anchors"]!.AsArray();if(!archive.Any(a=>JsonNode.DeepEquals(a,unused)))archive.Add(unused.DeepClone());}
        node["anchors"]=next;
    }
    public IReadOnlyDictionary<string,string> UpgradePublishedChains()
    {
        lock(gate)
        {
            var results=new Dictionary<string,string>();
            foreach(var current in List().Where(c=>Text(c,"status")=="published"))
            {
                var id=Text(current,"id");
                try
                {
                    var upgraded=UpgradeNavigation(current);
                    ValidateChain(upgraded,true);_=PublishedCatalogCompiler.Compile(this,[upgraded]);
                    if(JsonNode.DeepEquals(current,upgraded)){results[id]="无需更新";continue;}
                    var folder=SafePath(ChainsDirectory,"chains/"+id);
                    using var lease=new FileStream(SafePath(folder,"editor.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose);
                    if(!JsonNode.DeepEquals(Read(SafePath(folder,"chain.json")),current))throw new IOException("任务链已被其他窗口更新");
                    var revision=current["revision"]!.GetValue<int>();
                    var oldPath=SafePath(folder,$"revisions/v{revision}.json");if(!File.Exists(oldPath))MabiAgent.Core.AtomicJson.Write(oldPath,current);
                    upgraded["revision"]=revision+1;
                    MabiAgent.Core.AtomicJson.Write(SafePath(folder,$"revisions/v{revision+1}.json"),upgraded);
                    MabiAgent.Core.AtomicJson.Write(SafePath(folder,"chain.json"),upgraded);results[id]="已更新布局，保留身份图片及参数";
                }
                catch(Exception e)when(e is InvalidDataException or IOException or InvalidOperationException or ArgumentException or KeyNotFoundException){results[id]="保留原链："+e.Message;}
            }
            return results;
        }
    }
}
