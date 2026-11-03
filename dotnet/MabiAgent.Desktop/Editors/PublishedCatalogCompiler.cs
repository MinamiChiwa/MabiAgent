using System.IO;
using System.Text.Json.Nodes;
namespace MabiAgent.Desktop.Editors;
public static class PublishedCatalogCompiler
{
    public static JsonObject CompileAvailable(EditorRepository repository,IEnumerable<JsonObject>? documents=null,bool includeWorkbench=true)
    {
        var accepted=new List<JsonObject>();var blocked=new JsonObject();var names=new JsonObject();
        foreach(var chain in documents??repository.List())
        {
            if(EditorRepository.Text(chain,"status")!="published")continue;
            try{_=Compile(repository,accepted.Append(chain),includeWorkbench);accepted.Add(chain);}
            catch(Exception error)when(error is InvalidDataException or IOException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                var key=EditorRepository.Text(chain,"runtime_key");if(key.Length==0)throw;
                blocked[key]=EditorRepository.Text(chain,"name")+"："+error.Message;
                repository.ReportFault(error,"编译隔离任务链",key);
                var parameters=chain["parameters"];if(parameters?["target_item_id"] is{} id&&!string.IsNullOrWhiteSpace(EditorRepository.Text(parameters,"target_item_name")))names[id.ToString()]=EditorRepository.Text(parameters,"target_item_name");
            }
        }
        var config=Compile(repository,accepted,includeWorkbench);config["custom_chain_issues"]=blocked;
        // Installed navigation defaults remain; RuntimeCatalog excludes blocked actions.
        foreach(var(id,name)in names)if(config["item_names"]?[id] is null)config["item_names"]![id]=name?.DeepClone();
        return config;
    }
    public static JsonObject Compile(EditorRepository repository,IEnumerable<JsonObject>? documents=null,bool includeWorkbench=true)
    {
        var config=(JsonObject)repository.Catalog.DeepClone();var routes=(JsonObject)repository.Routes.DeepClone();var profile=routes["ui_profile"]!.AsObject();
        profile["page_variants"]??=new JsonObject();profile["anchor_variants"]??=new JsonObject();config["custom_navigation_nodes"]=new JsonObject();
        var overrides=new HashSet<string>();
        foreach(var chain in documents??repository.List())
        {
            if(EditorRepository.Text(chain,"status")!="published")continue;repository.ValidateChain(chain,true);
            var template=repository.Templates.Single(t=>EditorRepository.Text(t,"template_key")==EditorRepository.Text(chain,"template_key"));var originalKey=EditorRepository.Text(template,"runtime_key");var runtime=EditorRepository.Text(chain,"runtime_key");var canonical=runtime==originalKey;
            if(!overrides.Add(runtime))throw new InvalidDataException("同一运行配方不能有多份覆盖");
            var map=new Dictionary<string,string>();var originals=template["nodes"]!.AsArray().ToDictionary(n=>EditorRepository.Text(n!,"id"));
            foreach(var node in chain["nodes"]!.AsArray())foreach(var anchor in node!["anchors"]!.AsArray())
            {var key=EditorRepository.Text(anchor!,"key");var original=originals[EditorRepository.Text(node,"id")]!["anchors"]!.AsArray().FirstOrDefault(a=>EditorRepository.Text(a!,"key")==key);if(original is not null&&JsonNode.DeepEquals(original["variants"],anchor!["variants"])){map[key]=key;continue;}var name=runtime+"__"+key;map[key]=name;var alternates=new JsonArray();int i=0;
             foreach(var variant in anchor!["variants"]!.AsArray()){var n=i==0?name:name+"__"+i;if(i++>0)alternates.Add(n);var spec=Variant(repository,variant!);profile["templates"]![n]=spec; if(EditorRepository.Text(node,"role")=="observation"){var copy=(JsonObject)spec.DeepClone();copy["source_sha256"]=spec["sha256"]!.DeepClone();routes["gathering_observer"]!["templates"]![n]=copy;}}
             profile["anchor_variants"]![name]=alternates;if(EditorRepository.Text(node,"role")=="observation")routes["gathering_observer"]!["templates"]![name]!["variants"]=alternates.DeepClone();}
            string M(string name)=>map.GetValueOrDefault(name,name);
            foreach(var node in chain["nodes"]!.AsArray().Where(n=>EditorRepository.Text(n!,"role")=="navigation"))
            {var page=EditorRepository.Text(node!,"page");if(profile["pages"]?[page] is not JsonObject old)continue;var modified=(JsonObject)old.DeepClone();MapPage(modified,M);if(canonical)profile["pages"]![page]=modified;else
             {
                 // User routes get their own action names below. Their replacement button
                 // must not relocate a shared action in every other route on this page.
                 if(old["targets"] is {} commonTargets)modified["targets"]=commonTargets.DeepClone();
                 if(!JsonNode.DeepEquals(modified,old)){modified["page"]=page;profile["page_variants"]![runtime+"__"+page]=modified;}
             }}
            var p=chain["parameters"]!;var target=p["target_item_id"]!;if(!string.IsNullOrWhiteSpace(EditorRepository.Text(p,"target_item_name")))config["item_names"]![target.ToString()]=EditorRepository.Text(p,"target_item_name");
            if(EditorRepository.Text(chain,"kind")=="production")
            {
                var old=routes["recipes"]![originalKey]!;var recipe=(JsonObject)old.DeepClone();var device=routes["devices"]![EditorRepository.Text(p,"device")]!;var recipeNode=chain["nodes"]!.AsArray().Single(n=>EditorRepository.Text(n!,"id")=="recipe")!;
                var confirm=recipeNode["anchors"]!.AsArray().Select(a=>EditorRepository.Text(a!,"key")).Where(k=>k!=EditorRepository.Text(old,"craft_anchor")).Select(M).ToArray();
                if(!JsonNode.DeepEquals(target,old["output_item_id"])&&confirm.SequenceEqual(old["confirm_anchors"]!.AsArray().Select(x=>x!.GetValue<string>())))throw new InvalidDataException("新产物需要替换配方身份图，不能只改名称");
                var open=chain["nodes"]!.AsArray().Single(n=>EditorRepository.Text(n!,"id")=="open_recipe")!;var oldPage=repository.Routes["ui_profile"]!["pages"]![EditorRepository.Text(open,"page")]!;var openAnchor=oldPage["targets"]![EditorRepository.Text(old,"open_target")]!.GetValue<string>();var openTarget=runtime+".open_recipe";
                profile["pages"]![EditorRepository.Text(device,"queue_page")]!["targets"]![openTarget]=M(openAnchor);
                recipe["name"]=chain["name"]!.DeepClone();recipe["device"]=p["device"]!.DeepClone();recipe["duration_s"]=p["duration_s"]!.DeepClone();recipe["output_item_id"]=target.DeepClone();recipe["output_per_round"]=p["output_per_round"]!.DeepClone();recipe["craft_button_box"]=p["craft_button_box"]!.DeepClone();recipe["materials"]=p["inputs"]!.DeepClone();recipe["confirm_anchors"]=Strings(confirm);recipe["craft_anchor"]=M(EditorRepository.Text(old,"craft_anchor"));recipe["open_target"]=openTarget;routes["recipes"]![runtime]=recipe;
                profile["page_variants"]![runtime]=new JsonObject{["page"]="recipe_detail",["device"]=p["device"]!.DeepClone(),["anchors"]=Strings(confirm),["targets"]=new JsonObject{["recipe_detail.close"]="screen_overlay_back"}};
                if(canonical)foreach(var section in new[]{"pages","page_variants"})foreach(var(_,page)in profile[section]!.AsObject()){var before=page!["anchors"]?.AsArray().Select(x=>x!.GetValue<string>()).ToArray()??[];if(old["confirm_anchors"]!.AsArray().Select(x=>x!.GetValue<string>()).All(before.Contains))page["anchors"]=Strings(confirm);MapPage(page.AsObject(),M);}
                if(EditorRepository.Text(old,"device")==EditorRepository.Text(p,"device"))foreach(var f in new[]{"arrival_anchors","collect_anchors"})device[f]=Strings(device[f]!.AsArray().Select(x=>M(x!.GetValue<string>())));
                config["custom_navigation_nodes"]![runtime]=Strings(chain["nodes"]!.AsArray().Where(n=>EditorRepository.Text(n!,"role")=="navigation").Select(n=>EditorRepository.Text(n!,"id")));
            }
            else
            {
                var original=config["gathering_routes"]![originalKey]!;var gather=(JsonObject)original.DeepClone();var navigation=(JsonObject)gather["navigation"]!.DeepClone();var retained=chain["nodes"]!.AsArray().Select(n=>EditorRepository.Text(n!,"id")).ToHashSet();var oldSteps=navigation["steps"]!.AsArray();navigation["omitted_pages"]=Strings(oldSteps.Select((s,i)=>(s,i)).Where(x=>!retained.Contains("step_"+x.i)).Select(x=>EditorRepository.Text(x.s!,"page")));navigation["steps"]=new JsonArray(oldSteps.Select((s,i)=>(s,i)).Where(x=>retained.Contains("step_"+x.i)).Select(x=>x.s!.DeepClone()).ToArray());
                int i=0;foreach(var step in navigation["steps"]!.AsArray()){var page=PageFor(profile,runtime,EditorRepository.Text(step!,"page"));var name=page["targets"]![EditorRepository.Text(step!,"target")]!.GetValue<string>();var action=runtime+".step_"+i++;page["targets"]![action]=M(name);step!["target"]=action;}
                var detail=PageFor(profile,runtime,EditorRepository.Text(navigation,"detail_page"));var start=runtime+".start";detail["targets"]![start]=M(detail["targets"]![EditorRepository.Text(navigation,"start_target")]!.GetValue<string>());navigation["start_target"]=start;
                foreach(var f in new[]{"target_item_id","skill","node","attempts_per_batch"})gather[f]=p[f]!.DeepClone();gather["navigation"]=navigation;gather["point_only"]=false;config["gathering_routes"]![runtime]=gather;routes["gathering_navigation"]![runtime]=navigation.DeepClone();
                var observer=routes["gathering_observer"]!;var observation=(JsonObject)observer["routes"]![originalKey]!.DeepClone();
                if((!JsonNode.DeepEquals(p["node"],original["node"])||!JsonNode.DeepEquals(p["skill"],original["skill"]))&&observation["task_anchors"]!.AsArray().All(n=>M(n!.GetValue<string>())==n.GetValue<string>()))throw new InvalidDataException("改采集点须替换任务身份图");
                observation["total"]=p["attempts_per_batch"]!.DeepClone();observation["task_label"]=chain["name"]!.DeepClone();observation["task_anchors"]=Strings(observation["task_anchors"]!.AsArray().Select(n=>M(n!.GetValue<string>())));observer["routes"]![runtime]=observation;
                // Inventory quantities are supplied exclusively by CapturePlanningAdapter.
                // Do not compile chain metadata into screen bag-search readers.
            }
        }
        repository.ApplyDebug(routes);
        config["ui"]=routes["ui_profile"]!.DeepClone();config.Remove("template_sources");foreach(var f in new[]{"recipes","devices","gathering_observer","quantity","maintenance"})config[f]=routes[f]!.DeepClone();foreach(var(k,v)in routes["gathering_navigation"]!.AsObject())if(config["gathering_routes"]?[k] is {} g)g["navigation"]=v!.DeepClone();if(includeWorkbench)WorkbenchCompiler.Apply(repository,config);return config;
    }
    static JsonObject PageFor(JsonObject profile,string runtime,string page)=>(profile["page_variants"]?[runtime+"__"+page]??profile["pages"]![page])!.AsObject();
    static JsonArray Strings(IEnumerable<string> values)=>new(values.Select(v=>(JsonNode?)JsonValue.Create(v)).ToArray());
    static void MapPage(JsonObject page,Func<string,string> map){if(page["anchors"] is JsonArray names)page["anchors"]=Strings(names.Select(n=>map(n!.GetValue<string>())));if(page["targets"] is JsonObject targets)foreach(var(k,v)in targets.ToArray())targets[k]=map(v!.GetValue<string>());}
    static JsonObject Variant(EditorRepository repository,JsonNode variant){var path=repository.AssetPath(EditorRepository.Text(variant["asset"]!,"id"));var relative=Path.GetRelativePath(repository.Root,path);return new(){["path"]=relative.StartsWith("..",StringComparison.Ordinal)?path:relative,["sha256"]=variant["asset"]!["sha256"]!.DeepClone(),["bytes"]=new FileInfo(path).Length,["box"]=variant["box"]!.DeepClone(),["search"]=variant["search"]!.DeepClone(),["threshold"]=variant["threshold"]!.DeepClone(),["match_mode"]=variant["match_mode"]!.DeepClone()};}
}

