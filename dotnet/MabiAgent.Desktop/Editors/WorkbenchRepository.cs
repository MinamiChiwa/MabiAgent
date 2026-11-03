using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MabiAgent.Core;

namespace MabiAgent.Desktop.Editors;

public sealed class WorkbenchRepository(EditorRepository repository)
{
    public EditorRepository Editor {get;}=repository;
    public string FilePath=>EditorRepository.SafePath(Editor.StateDirectory,"anchor-debug/workbench.json");
    public WorkbenchDocument? LoadSaved()
    {
        if(!File.Exists(FilePath))return null;
        var data=JsonNode.Parse(File.ReadAllText(FilePath))?.AsObject()??throw new InvalidDataException("调试工作台配置为空");
        if(data["schema_version"]?.GetValue<int>()!=1)throw new InvalidDataException("调试工作台配置版本不支持");
        return new(data);
    }
    public WorkbenchDocument Load()=>LoadSaved()??Import();
    public WorkbenchDocument Save(WorkbenchDocument document)
    {
        WorkbenchValidation.Validate(document);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var lease=new FileStream(FilePath+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose);
        var current=LoadSaved();if((current?.Revision??0)!=document.Revision)throw new IOException("调试配置已更新，请重新打开后保存");
        var saved=new WorkbenchDocument(document.ToJson()){Revision=document.Revision+1};
        saved.Data["saved_at"]=DateTimeOffset.UtcNow.ToString("O");
        WorkbenchCompiler.ValidateAssets(Editor,saved);
        var candidate=PublishedCatalogCompiler.CompileAvailable(Editor,includeWorkbench:false);WorkbenchCompiler.Apply(Editor,saved,candidate);
        AtomicJson.Write(FilePath,saved.ToJson());Editor.NotifyWorkbenchChanged();document.Revision=saved.Revision;return saved;
    }
    public WorkbenchDocument Import()
    {
        var size=Editor.Catalog["client_size"]!.AsArray();
        var document=new WorkbenchDocument(new JsonObject{["width"]=size[0]!.DeepClone(),["height"]=size[1]!.DeepClone(),["dpi"]=Editor.Catalog["dpi"]!.DeepClone()});
        var routes=Editor.EffectiveRoutes();
        var extraProfiles=new List<(string Prefix,JsonObject Profile)>();
        foreach(var(prefix,file)in new[]{("purchase.","screen_purchase.1280x960.tw.json"),("travel.","screen_travel.1280x960.tw.json"),("relogin.","screen_relogin.1280x960.tw.json"),("notification.","session_notifications.1280x960.tw.json")})
        {
            var relative="config/"+file;if(!File.Exists(EditorRepository.SafePath(Editor.Root,relative)))continue;var extra=new CatalogLoader(Editor.Root).Load(relative);extraProfiles.Add((prefix,extra));
            foreach(var(key,spec)in extra["templates"]?.AsObject()??[])
            {var name=prefix+key;if(routes["ui_profile"]!["templates"]?[name] is not null)continue;routes["ui_profile"]!["templates"]![name]=spec?.DeepClone();}
            foreach(var(key,page)in extra["pages"]?.AsObject()??[])if(routes["ui_profile"]!["pages"]?[key] is null)routes["ui_profile"]!["pages"]![key]=page?.DeepClone();
        }
        foreach(var entry in Editor.Anchors())
        {
            var anchor=new WorkbenchAnchor(new JsonObject{["id"]=entry.Id,["name"]=entry.Name,["in_library"]=true,["source_path"]=entry.Path.DeepClone(),["source_namespace"]=entry.Namespace,["source_spec"]=entry.Spec.DeepClone()});
            var spec=(JsonObject)entry.Spec.DeepClone();spec["id"]="default";spec["name"]="默认状态";spec["mode"]=spec["path"] is null?"coordinate":"image";spec["box"]??=spec["region"]?.DeepClone()??spec["crop_native"]?.DeepClone()??spec["search"]?.DeepClone();
            if(spec["box"] is null&&spec["path"] is not null)
            {
                var original=EditorRepository.GetPath(routes,entry.Path);var box=original?["label_crop_native"];
                var bytes=File.ReadAllBytes(Editor.ResolveImagePath(EditorRepository.Text(spec,"path")));if(bytes.Length<24||!bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new InvalidDataException(entry.Name+"：图片格式不支持");
                var width=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16,4));var height=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20,4));
                spec["box"]=box is JsonArray region&&region[2]!.GetValue<int>()-region[0]!.GetValue<int>()==width&&region[3]!.GetValue<int>()-region[1]!.GetValue<int>()==height?box.DeepClone():new JsonArray(0,0,width,height);
                spec["source_unlocated"]=box is null;
            }
            spec["search"]??=spec["box"]?.DeepClone();spec["threshold"]??=spec["label_score"]?.DeepClone()??JsonValue.Create(.97);spec["template_key"]=entry.Namespace=="ui_profile"?entry.Name:entry.Namespace=="gathering_observer"?"gathering_observer."+entry.Name:"";anchor.States.Add(new(spec));anchor.Data["imported_state"]=spec.DeepClone();document.Anchors.Add(anchor);
        }
        foreach(var(prefix,extra)in extraProfiles)foreach(var(key,value)in extra["templates"]?.AsObject()??[])
        {
            var name=prefix+key;var id="ui_profile:"+name;if(document.FindAnchor(id) is not null)continue;
            var spec=value!.DeepClone().AsObject();spec["id"]="default";spec["name"]="默认状态";spec["mode"]="image";spec["template_key"]=name;spec["box"]??=spec["crop_native"]?.DeepClone();spec["search"]??=spec["box"]?.DeepClone();spec["threshold"]??=JsonValue.Create(.97);
            var anchor=new WorkbenchAnchor(new JsonObject{["id"]=id,["name"]=name,["in_library"]=true,["source_namespace"]="ui_profile"});anchor.States.Add(new(spec));document.Anchors.Add(anchor);
        }
        foreach(var(section,node)in routes["ui_profile"]!.AsObject().Where(p=>p.Key is "pages" or "page_variants"))foreach(var(key,page)in node!.AsObject())
        {
            var id=section=="pages"?key:"variant:"+key;var source=page!.AsObject();
            var row=new WorkbenchPage(new JsonObject{["id"]=id,["name"]=EditorRepository.Text(source,"label",EditorRepository.Text(source,"name",WorkbenchLabels.Page(key))),["runtime_page"]=EditorRepository.Text(source,"page",key),["source_section"]=section,["source_key"]=key,["source"]=source.DeepClone(),["anchor_ids"]=string.Join(", ",(source["anchors"] as JsonArray??[]).Select(a=>"ui_profile:"+a!.GetValue<string>()))});
            foreach(var(target,value)in source["targets"]?.AsObject()??[])row.Transitions.Add(new(new JsonObject{["id"]="link_"+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id+"\n"+target)))[..20],["name"]=target,["anchor_id"]="ui_profile:"+value!.GetValue<string>(),["target"]=target,["imported"]=true}));
            foreach(var collection in new[]{"coordinate_targets","fixed_targets"})foreach(var(target,area)in source[collection]?.AsObject()??[])
            {if(area is null)continue;var anchor=ImportCoordinateAnchor(document,id,target,area);if(row.Transitions.Any(t=>t.Target==target))continue;row.Transitions.Add(new(new JsonObject{["id"]="link_"+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id+"\n"+target)))[..20],["name"]=target,["anchor_id"]=anchor.Id,["target"]=target,["imported"]=true}));}
            document.Pages.Add(row);
        }
        var users=Editor.List();var userTemplates=users.Select(x=>EditorRepository.Text(x,"template_key")).ToHashSet();
        foreach(var source in users.Concat(Editor.Templates.Where(t=>!userTemplates.Contains(EditorRepository.Text(t,"template_key")))))
        {
            var id=EditorRepository.Text(source,"id");if(id.Length==0)id="installed:"+EditorRepository.Text(source,"template_key");
            var chain=new WorkbenchChain(new JsonObject{["id"]=id,["name"]=EditorRepository.Text(source,"name"),["runtime_key"]=EditorRepository.Text(source,"runtime_key"),["kind"]=EditorRepository.Text(source,"kind"),["source"]=source.DeepClone(),["enabled"]=false,["resume_at_current_page"]=true});
            foreach(var node in source["nodes"]!.AsArray().OfType<JsonObject>())
            {
                var role=EditorRepository.Text(node,"role");var page=EditorRepository.Text(node,"page");var target=EditorRepository.Text(node,"target");
                if(chain.Kind=="gathering"&&EditorRepository.Text(node,"id")=="start")role="gathering_start";
                var pageSpec=routes["ui_profile"]?["pages"]?[page];var targetName=pageSpec?["targets"]?[target]?.GetValue<string>();
                var card=new WorkbenchCard(new JsonObject{["id"]=EditorRepository.Text(node,"id"),["name"]=EditorRepository.Text(node,"label"),["page_id"]=page,["expected_page_id"]=EditorRepository.Text(node,"expected_page"),["target"]=target,["role"]=role,["action"]=role=="navigation"?"click":"semantic",["consumes_resources"]=role is "production" or "result" or "gathering_start",["source"]=node.DeepClone(),["anchor_id"]=targetName is null?"":"ui_profile:"+targetName,["state_id"]="default"});
                if(role!="navigation")card.TimeoutSeconds=role=="result"?Math.Clamp((int)Math.Ceiling(source["parameters"]?["duration_s"]?.GetFiniteDouble()??0)+180,180,86400):role=="observation"?780:180;
                if(targetName is null&&pageSpec?["coordinate_targets"]?[target] is {} fixedArea)
                {
                    card.AnchorId=ImportCoordinateAnchor(document,page,target,fixedArea).Id;
                }
                if(pageSpec?["random_targets"]?[target] is not null)card.RandomClick=true;
                chain.Cards.Add(card);
            }
            document.Chains.Add(chain);
        }
        var strategySources=(JsonObject)Editor.Catalog.DeepClone();strategySources["ui"]=routes["ui_profile"]!.DeepClone();strategySources["devices"]=routes["devices"]?.DeepClone();
        foreach(var(prefix,extra)in extraProfiles)
        {var section=prefix switch{"purchase."=>"purchase","travel."=>"travel_wings","relogin."=>"relogin",_=>"notifications"};strategySources[section]=strategySources[section] is JsonObject original?CatalogLoader.Merge(extra,original):extra.DeepClone();}
        BindMapNavigation(strategySources);
        foreach(var(root,value)in strategySources.Where(x=>x.Key is "maintenance" or "quantity" or "fishing" or "travel_wings" or "relogin" or "purchase" or "notifications" or "scheduler" or "runtime" or "gathering_policy" or "devices" or "ui" or "gathering_observer" or "gathering_routes"))
        {
            ImportStrategies(document,value,new JsonArray(root),root);
            if(root is not("ui" or "devices" or "gathering_observer" or "gathering_routes"))ImportRoutes(document,value,new JsonArray(root),root);
        }
        return document;
    }
    static WorkbenchAnchor ImportCoordinateAnchor(WorkbenchDocument document,string page,string target,JsonNode area)
    {
        var id="coordinate:"+page+":"+target;if(document.FindAnchor(id) is {} existing)return existing;
        var box=area is JsonObject obj?obj["box"]:area;if(box is not JsonArray)throw new InvalidDataException(target+"：固定坐标区域格式无效");
        var anchor=new WorkbenchAnchor(new JsonObject{["id"]=id,["name"]=target,["page_id"]=page});anchor.States.Add(new(new JsonObject{["id"]="default",["name"]="点击区域",["mode"]="coordinate",["box"]=box.DeepClone(),["search"]=box.DeepClone(),["shape"]=area is JsonObject shape?shape["shape"]?.DeepClone()??JsonValue.Create("rectangle"):JsonValue.Create("rectangle"),["random_click"]=area is JsonObject random?random["random"]?.DeepClone()??JsonValue.Create(false):JsonValue.Create(false)}));document.Anchors.Add(anchor);return anchor;
    }
    static void ImportRoutes(WorkbenchDocument document,JsonNode? value,JsonArray path,string category)
    {
        if(path.Last()?.ToString()=="map_navigation")return;
        if(value is JsonObject obj)
        {
            if(obj["stages"] is JsonObject stages)
            {
                var key=string.Join("/",path.Select(x=>x!.ToString()));var chain=new WorkbenchChain(new JsonObject{["id"]="route:"+key,["name"]=WorkbenchLabels.Category(category)+" · "+WorkbenchLabels.Page(path.Last()!.ToString()),["description"]=key,["kind"]=category,["runtime_key"]=key,["source_path"]=path.DeepClone(),["staged_route"]=true,["enabled"]=false});
                foreach(var(stageName,stage)in stages)foreach(var collection in new[]{"choices","search"})foreach(var(step,i)in(stage?[collection] as JsonArray??[]).OfType<JsonObject>().Select((s,i)=>(s,i)))
                {
                    var target=EditorRepository.Text(step,"target");var card=new WorkbenchCard(new JsonObject{["id"]=stageName+"_"+collection+"_"+i,["name"]=target,["action"]=EditorRepository.Text(step,"kind","click"),["page_id"]=path.Last()!.ToString(),["target"]=target,["source"]=step.DeepClone(),["stage"]=stageName,["stage_collection"]=collection,["role"]="navigation"});
                    chain.Cards.Add(card);
                }
                document.Chains.Add(chain);return;
            }
            if(obj["steps"] is JsonArray steps&&steps.Any(x=>x?["target"] is not null))
            {
                var key=string.Join("/",path.Select(x=>x!.ToString()));var chain=new WorkbenchChain(new JsonObject{["id"]="route:"+key,["name"]=WorkbenchLabels.Category(category)+" · "+WorkbenchLabels.Page(path.Last()!.ToString()),["description"]=key,["kind"]=category,["runtime_key"]=key,["source_path"]=path.DeepClone(),["enabled"]=false});
                foreach(var(step,i)in steps.OfType<JsonObject>().Select((s,i)=>(s,i)))chain.Cards.Add(new(new JsonObject{["id"]="step_"+i,["name"]=EditorRepository.Text(step,"target","步骤 "+(i+1)),["action"]=EditorRepository.Text(step,"kind","click"),["page_id"]=EditorRepository.Text(step,"page"),["expected_page_id"]=EditorRepository.Text(step,"expected_page"),["target"]=EditorRepository.Text(step,"target"),["source"]=step.DeepClone()}));
                document.Chains.Add(chain);
            }
            else if(obj["target"] is JsonValue&&path.Count>=3&&path.Any(p=>p?.ToString() is "navigation" or "exit_navigation"))
            {
                var key=string.Join("/",path.Select(x=>x!.ToString()));var chain=new WorkbenchChain(new JsonObject{["id"]="route:"+key,["name"]=WorkbenchLabels.Category(category)+" · "+WorkbenchLabels.Page(path.Last()!.ToString()),["description"]=key,["kind"]=category,["runtime_key"]=key,["source_path"]=path.DeepClone(),["single_route"]=true,["enabled"]=false});
                chain.Cards.Add(new(new JsonObject{["id"]="step",["name"]=EditorRepository.Text(obj,"target"),["action"]=EditorRepository.Text(obj,"kind","click"),["page_id"]=path.Last()!.ToString(),["target"]=EditorRepository.Text(obj,"target"),["source"]=obj.DeepClone()}));document.Chains.Add(chain);return;
            }
            foreach(var(k,v)in obj){if(k is "templates" or "steps")continue;var p=(JsonArray)path.DeepClone();p.Add(k);ImportRoutes(document,v,p,category);}
        }
        else if(value is JsonArray array)for(int i=0;i<array.Count;i++){var p=(JsonArray)path.DeepClone();p.Add(i);ImportRoutes(document,array[i],p,category);}
    }
    internal static void BindMapNavigation(JsonObject configuration)
    {
        if(configuration["maintenance"]?["map_navigation"] is not {} template)return;
        JsonNode Bind(JsonNode node,string shop,string npc,string travel)=>node switch
        {JsonObject obj=>new JsonObject(obj.Select(p=>KeyValuePair.Create(p.Key,p.Value is null?null:Bind(p.Value,shop,npc,travel)))),JsonArray array=>new JsonArray(array.Select(v=>v is null?null:Bind(v,shop,npc,travel)).ToArray()),JsonValue value when value.TryGetValue<string>(out var name)=>JsonValue.Create(name switch{"$shop"=>shop,"$npc"=>npc,"$travel"=>travel,_=>name})!,_=>node.DeepClone()};
        configuration["maintenance"]!["navigation"]??=new JsonObject();
        foreach(var(destination,shop,npc,travel)in new[]{("repair_quote","map.blacksmith","map.repair_npc","repair_travel"),("purchase_shop","map.general_store","map.shop_npc","shop_travel")})
        {configuration["maintenance"]!["navigation"]![destination]??=new JsonObject();configuration["maintenance"]!["navigation"]![destination]!["region_map"]=Bind(template,shop,npc,travel);}
    }
    static readonly HashSet<string> Protected=["templates","path","sha256","bytes","label_template","trusted_asset_roots","contract","source","root","coordinates_for_input"];
    static void ImportStrategies(WorkbenchDocument document,JsonNode? value,JsonArray path,string category)
    {
        if(value is JsonObject obj){foreach(var(k,v)in obj){if(Protected.Contains(k))continue;var p=(JsonArray)path.DeepClone();p.Add(k);ImportStrategies(document,v,p,category);}}
        else if(value is JsonArray array)
        {
            var names=path.OfType<JsonValue>().Where(v=>v.TryGetValue<string>(out _)).Select(v=>v.GetValue<string>()).ToArray();var last=names.LastOrDefault()??"";
            var numeric=array.Count is 2 or 4&&array.All(x=>x is JsonValue v&&v.TryGetValue<int>(out _));
            var relative=last is "offset" or "open_click_offset" or "step"||names.Contains("grid")&&last is "icon" or "quantity" or "label";
            var nonCoordinate=last is "thresholds" or "scale" or "client_size" or "source_size" or "output_size" or "verified_client_size";
            var point=last is "destination" or "origin" or "point" or "position"||names.Any(n=>n is "coordinate_targets" or "fixed_targets" or "points");
            // Preserve each geometric leaf as one setting, including named dictionaries and nested slot arrays.
            // Four-number leaves describe rectangles in the runtime contracts; sizes and OCR thresholds do not.
            if(numeric&&!nonCoordinate&&(array.Count==4||point||relative))
            {var key=string.Join(" / ",path.Select(x=>x!.ToString()));document.Strategies.Add(new(new JsonObject{["id"]="strategy:"+key,["name"]=WorkbenchLabels.Parameter(last)+(path.Last() is JsonValue index&&index.TryGetValue<int>(out var slot)?" "+(slot+1):""),["description"]=key,["category"]=category,["path"]=path.DeepClone(),["value"]=array.DeepClone(),["value_kind"]=(relative?"relative":"")+(array.Count==2?"point":"region"),["original"]=array.DeepClone()}));return;}
            for(int i=0;i<array.Count;i++){var p=(JsonArray)path.DeepClone();p.Add(i);ImportStrategies(document,array[i],p,category);}
        }
        else if(value is JsonValue scalar){var key=string.Join(" / ",path.Select(x=>x!.ToString()));document.Strategies.Add(new(new JsonObject{["id"]="strategy:"+key,["name"]=WorkbenchLabels.Parameter(path.Last()!.ToString()),["description"]=key,["category"]=category,["path"]=path.DeepClone(),["value"]=scalar.DeepClone(),["value_kind"]=scalar.GetValueKind().ToString().ToLowerInvariant(),["original"]=scalar.DeepClone()}));}
    }
    public static string RuntimeAnchorName(string anchorId,string stateId)=>"workbench_"+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(anchorId+"\n"+stateId)))[..24];
}

public sealed partial class EditorRepository
{
    internal void NotifyWorkbenchChanged()=>CatalogChanged?.Invoke();
}

