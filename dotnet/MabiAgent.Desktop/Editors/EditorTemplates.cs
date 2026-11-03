using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
namespace MabiAgent.Desktop.Editors;
public sealed partial class EditorRepository
{
    public static HashSet<string> GatheringDifferenceNodeIds(JsonObject chain)
    {
        var nodes=chain["nodes"]!.AsArray();var lastNavigation=nodes.LastOrDefault(n=>Text(n!,"id").StartsWith("step_",StringComparison.Ordinal));
        return new(new[]{lastNavigation is null?"":Text(lastNavigation,"id"),"start","observation"},StringComparer.Ordinal);
    }
    static string AnchorLabel(string key)
    {
        if(key.Contains("material",StringComparison.Ordinal)||key.StartsWith("detail_ore_",StringComparison.Ordinal))return "材料名称";
        if(key.Contains("craft",StringComparison.Ordinal))return "加工按钮";
        if(key.Contains("collect",StringComparison.Ordinal))return "领取按钮";
        if(key.Contains("name",StringComparison.Ordinal))return "目标名称";
        if(key.Contains("equipment",StringComparison.Ordinal)||key.Contains("arriv",StringComparison.Ordinal))return "设备身份";
        if(key.Contains("close",StringComparison.Ordinal)||key.Contains("back",StringComparison.Ordinal))return "返回或关闭";
        if(key.Contains("title",StringComparison.Ordinal)||key.Contains("header",StringComparison.Ordinal))return "页面标题";
        if(key.Contains("total",StringComparison.Ordinal))return "原生批次总数";
        if(key.Contains("progress",StringComparison.Ordinal))return "原生任务进度";
        if(key.Contains("stop",StringComparison.Ordinal))return "停止状态";
        if(key.Contains("idle",StringComparison.Ordinal))return "空闲状态";
        return "页面身份或导航目标";
    }
    JsonObject Anchor(string name,string space="ui_profile")
    {
        var variants=new JsonArray();var profile=Routes[space]!;var names=new[]{name}.Concat(profile["anchor_variants"]?[name]?.AsArray().Select(n=>n!.GetValue<string>())??[]);
        foreach(var key in names.Distinct())if(profile["templates"]?[key] is JsonObject spec&&spec["path"] is not null)
        {var box=(spec["box"]??spec["crop_native"]??spec["search"])!;var sha=Text(spec,"sha256");var path=SafePath(Root,Text(spec,"path"));if(sha.Length==0&&File.Exists(path))sha=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));var id="template_"+sha;installedAssets[id]=path;variants.Add(new JsonObject{["asset"]=new JsonObject{["id"]=id,["sha256"]=sha,["width"]=box[2]!.GetValue<int>()-box[0]!.GetValue<int>(),["height"]=box[3]!.GetValue<int>()-box[1]!.GetValue<int>(),["source"]="installed_template"},["box"]=box.DeepClone(),["search"]=(spec["search"]??box).DeepClone(),["threshold"]=spec["threshold"]?.DeepClone()??JsonValue.Create(.97),["match_mode"]=Text(spec,"match_mode",space=="ui_profile"?"grayscale":"light_text")});}
        return new JsonObject{["key"]=name,["label"]=AnchorLabel(name),["variants"]=variants};
    }
    JsonObject Node(string id,string label,string page,IEnumerable<string> names,string role="navigation",string? target=null,string? expected=null,string space="ui_profile")=>new(){["id"]=id,["label"]=label,["page"]=page,["target"]=target,["expected_page"]=expected,["role"]=role,["anchors"]=new JsonArray(names.Distinct().Select(n=>(JsonNode)Anchor(n,space)).ToArray())};
    JsonObject Page(string id,string page,string? target=null,string? expected=null)
    {var p=Routes["ui_profile"]!["pages"]?[page];var names=p?["anchors"]?.AsArray().Select(n=>n!.GetValue<string>()).ToList()??[];if(target is not null&&p?["targets"]?[target] is {} a)names.Add(a.GetValue<string>());return Node(id,page switch{"scene"=>"主界面","skills"=>"技能菜单","category_page"=>"加工分类","character_menu"=>"角色菜单","character_life_skills"=>"生活技能",_=>page.EndsWith("_panel",StringComparison.Ordinal)?"设备面板":page.Contains("detail",StringComparison.Ordinal)?"目标详情":"采集点列表"},page,names,target:target,expected:expected);}
    IReadOnlyList<JsonObject> BuildTemplates()
    {
        var result=new List<JsonObject>();foreach(var(key,v)in Catalog["recipes"]!.AsObject())
        {var e=v!;var d=Catalog["devices"]![Text(e,"device")]!;var p=new JsonObject{["target_item_id"]=e["output_item_id"]!.DeepClone(),["target_item_name"]=Catalog["item_names"]?[e["output_item_id"]!.ToString()]?.DeepClone()??e["name"]?.DeepClone()??JsonValue.Create(key),["output_per_round"]=e["output_per_round"]!.DeepClone(),["duration_s"]=e["duration_s"]!.DeepClone(),["device"]=e["device"]!.DeepClone(),["craft_button_box"]=e["craft_button_box"]!.DeepClone(),["inputs"]=e["materials"]?.DeepClone()??JsonNode.Parse("[{\"item_id\":198875762,\"quantity\":10,\"name\":\"鐵礦石\"}]")};
        foreach(var m in p["inputs"]!.AsArray())m!["name"]??=Catalog["item_names"]?[m["item_id"]!.ToString()]?.DeepClone()??JsonValue.Create("");
        var nodes=new JsonArray(Page("scene","scene","scene.open_skills","skills"),Page("skills","skills","skills.open_processing","category_page"),Page("category","category_page",Text(d,"category_target"),Text(d,"queue_page")),Page("open_recipe",Text(d,"queue_page"),Text(e,"open_target"),"recipe_detail"),Node("recipe","配方身份与加工","recipe_detail",e["confirm_anchors"]!.AsArray().Select(n=>n!.GetValue<string>()).Append(Text(e,"craft_anchor")),"production"),Node("result","设备到达与领取",Text(d,"queue_page"),d["arrival_anchors"]!.AsArray().Concat(d["collect_anchors"]!.AsArray()).Select(n=>n!.GetValue<string>()),"result"));
        result.Add(Template("production",key,Text(e,"name",Text(p,"target_item_name")),p,nodes));}
        foreach(var(key,v)in Catalog["gathering_routes"]?.AsObject()??[])
        {var e=v!;var nav=e["navigation"]!;var nodes=new JsonArray();int i=0;foreach(var step in nav["steps"]!.AsArray())nodes.Add(Page("step_"+i++,Text(step!,"page"),Text(step!,"target"),Text(step!,"expected_page")));nodes.Add(Page("start",Text(nav,"detail_page"),Text(nav,"start_target"),"scene"));
        var observer=Catalog["gathering_observer"]?["routes"]?[key];var names=observer?["task_anchors"]?.AsArray().Select(x=>x!.GetValue<string>()).ToList()??[];nodes.Add(Node("observation","原生采集任务与完成判断","scene",names,"observation",space:"gathering_observer"));
        var p=new JsonObject{["target_item_id"]=e["target_item_id"]!.DeepClone(),["target_item_name"]=Catalog["item_names"]?[e["target_item_id"]!.ToString()]?.DeepClone()??JsonValue.Create(""),["attempts_per_batch"]=e["attempts_per_batch"]?.DeepClone()??JsonValue.Create(10),["skill"]=e["skill"]!.DeepClone(),["node"]=e["node"]!.DeepClone()};var t=Template("gathering",key,Catalog["gathering_points"]?[key]?["name"]?.GetValue<string>()??Text(e,"node"),p,nodes);t["quantity"]=new JsonObject{["name"]=Text(p,"target_item_name"),["identity"]=null};result.Add(t);}
        return result;
    }
    JsonObject Template(string kind,string key,string name,JsonObject parameters,JsonArray nodes)=>new(){["schema_version"]=1,["id"]="",["name"]=name,["template_key"]=kind+":"+key,["kind"]=kind,["status"]="draft",["revision"]=0,["verification"]="menu_captured_untested",["runtime_key"]=key,["client_size"]=Catalog["client_size"]!.DeepClone(),["dpi"]=Catalog["dpi"]!.DeepClone(),["parameters"]=parameters,["nodes"]=nodes};
}


