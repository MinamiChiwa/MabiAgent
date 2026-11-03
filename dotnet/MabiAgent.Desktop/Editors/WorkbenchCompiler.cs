using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MabiAgent.Core;

namespace MabiAgent.Desktop.Editors;

public static class WorkbenchCompiler
{
    public static void Apply(EditorRepository repository,JsonObject configuration)
    {var saved=new WorkbenchRepository(repository).LoadSaved();if(saved is not null)Apply(repository,saved,configuration);}
    public static void Apply(EditorRepository repository,WorkbenchDocument document,JsonObject configuration,bool allowStandaloneTests=false)
    {
        WorkbenchValidation.Validate(document);
        WorkbenchValidation.ValidateConsumptionTargets(document,configuration);
        var profile=configuration["ui"]?.AsObject()??throw new InvalidDataException("缺少屏幕页面配置");
        foreach(var(section,file)in new[]{("purchase","screen_purchase.1280x960.tw.json"),("travel_wings","screen_travel.1280x960.tw.json"),("relogin","screen_relogin.1280x960.tw.json"),("notifications","session_notifications.1280x960.tw.json")})
        {var relative="config/"+file;if(configuration[section] is null&&File.Exists(EditorRepository.SafePath(repository.Root,relative)))configuration[section]=new CatalogLoader(repository.Root).Load(relative);}
        profile["templates"]??=new JsonObject();profile["anchor_variants"]??=new JsonObject();profile["pages"]??=new JsonObject();profile["page_variants"]??=new JsonObject();
        foreach(var anchor in document.Anchors)
        {
            if(anchor.Data["source_namespace"]?.GetValue<string>() is "quantity" or "maintenance")
            {ApplyInline(configuration,anchor);var referenced=document.Chains.SelectMany(c=>c.Cards).Any(c=>c.AnchorId==anchor.Id||WorkbenchModel.SplitIds(c.ConditionAnchorIds).Contains(anchor.Id)||WorkbenchModel.SplitIds(c.ExpectedAnchorIds).Contains(anchor.Id))||document.Pages.Any(p=>WorkbenchModel.SplitIds(p.AnchorIds).Contains(anchor.Id)||p.Transitions.Any(t=>t.AnchorId==anchor.Id));if(!referenced)continue;}
            var states=anchor.States.Where(s=>s.Mode=="image").ToArray();
            foreach(var state in states)
            {
                var spec=state.ToJson();foreach(var field in new[]{"id","name","mode","shape","template_key","random_click","source_unlocated"})spec.Remove(field);
                var key=StateKey(anchor,state);profile["templates"]![key]=spec;
                foreach(var(prefix,section)in new[]{("purchase.","purchase"),("travel.","travel_wings"),("relogin.","relogin"),("notification.","notifications")})
                    if(key.StartsWith(prefix,StringComparison.Ordinal)&&configuration[section]?["templates"] is JsonObject externalTemplates)externalTemplates[key[prefix.Length..]]=spec.DeepClone();
                if(state.TemplateKey.Length>0&&state.TemplateKey!=key&&profile["templates"]?[state.TemplateKey] is not null)profile["templates"]![state.TemplateKey]=spec.DeepClone();
            }
            if(states.Length>0)
            {
                profile["workbench_owned_variants"]??=new JsonObject();var previous=(profile["workbench_owned_variants"]?[anchor.Id] as JsonArray??[]).Select(n=>n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                var primary=StateKey(anchor,states[0]);var installed=(profile["anchor_variants"]?[primary] as JsonArray??[]).Select(n=>n!.GetValue<string>()).Where(key=>!previous.Contains(key)).ToArray();var current=states.Skip(1).Select(s=>StateKey(anchor,s)).ToArray();
                var variants=installed.Concat(current).Where(key=>key!=primary).Distinct(StringComparer.Ordinal).ToArray();
                if(variants.Length>0)profile["anchor_variants"]![primary]=new JsonArray(variants.Select(s=>(JsonNode?)JsonValue.Create(s)).ToArray());else profile["anchor_variants"]!.AsObject().Remove(primary);
                profile["workbench_owned_variants"]![anchor.Id]=new JsonArray(current.Except(installed,StringComparer.Ordinal).Select(s=>(JsonNode?)JsonValue.Create(s)).ToArray());
            }
        }
        foreach(var page in document.Pages)
        {
            var section=EditorRepository.Text(page.Data,"source_section","pages");var key=EditorRepository.Text(page.Data,"source_key",page.Id);
            var source=profile[section]?[key]?.DeepClone()?.AsObject()??page.Data["source"]?.DeepClone()?.AsObject()??new JsonObject{["targets"]=new JsonObject()};
            var imageAnchors=WorkbenchModel.SplitIds(page.AnchorIds).Select(document.FindAnchor).Where(a=>a is not null).Cast<WorkbenchAnchor>().Select(a=>(Anchor:a,State:a.States.FirstOrDefault(s=>s.Mode=="image"))).Where(x=>x.State is not null).ToArray();
            if(imageAnchors.Length>0)source["anchors"]=new JsonArray(imageAnchors.Select(x=>(JsonNode?)JsonValue.Create(StateKey(x.Anchor,x.State!))).ToArray());
            else source.Remove("anchors");
            foreach(var link in page.Transitions)
            {
                if(link.Target.Length==0||document.FindAnchor(link.AnchorId) is not {} anchor||anchor.States.FirstOrDefault() is not {} state)continue;
                PutTarget(source,link.Target,anchor,state,state.RandomClick);
            }
            if(page.MatchRule=="any"&&imageAnchors.Length>1)
            {
                // Existing recognizer requires all listed identities. Alternatives are explicit variants of the same page.
                source["anchors"]=new JsonArray(StateKey(imageAnchors[0].Anchor,imageAnchors[0].State!));
                foreach(var (pair,i)in imageAnchors.Skip(1).Select((x,i)=>(x,i))){var variant=(JsonObject)source.DeepClone();variant["page"]=EditorRepository.Text(page.Data,"runtime_page",page.Id);variant["anchors"]=new JsonArray(StateKey(pair.Anchor,pair.State!));profile["page_variants"]!["workbench_any_"+key+"_"+i]=variant;}
            }
            profile[section]??=new JsonObject();profile[section]![key]=source;
        }
        var flows=new JsonObject();
        WorkbenchRepository.BindMapNavigation(configuration);
        foreach(var chain in document.Chains.Where(c=>c.Enabled))
        {
            if(chain.RuntimeKey.Length==0)throw new InvalidDataException(chain.Name+"：请关联运行任务");
            if(!allowStandaloneTests&&chain.Data["source_path"] is null&&configuration["recipes"]?[chain.RuntimeKey] is null&&configuration["gathering_routes"]?[chain.RuntimeKey] is null)throw new InvalidDataException(chain.Name+"：请将运行任务绑定到现有生产或采集任务；独立流程可使用测试按钮");
            if(chain.Data["source_path"] is not null)
                foreach(var card in chain.Cards.Where(c=>c.Enabled))
                {
                    if(card.Action is not("click" or "right" or "scroll" or "zoom_max" or "drag"))throw new InvalidDataException(card.Name+"：原生导航路线仅支持点击、右键、滚动、缩放和拖拽；页面等待由原导航流程确认");
                    if(card.ConditionOperation!="all"||card.ConditionAnchorIds.Length>0||card.ExpectedAnchorIds.Length>0||card.SuccessId.Length>0||card.FailureId.Length>0||card.RetryLimit>0)throw new InvalidDataException(card.Name+"：原生导航使用原页面与阶段判断，不支持独立条件、分支、完成锚点和重试");
                }
            if(flows.ContainsKey(chain.RuntimeKey))throw new InvalidDataException(chain.Name+"：已有其他启用任务链使用相同运行任务");
            var flow=chain.ToJson();var cards=flow["cards"]!.AsArray();
            foreach(var card in chain.Cards)
            {
                var compiled=cards.OfType<JsonObject>().Single(c=>EditorRepository.Text(c,"id")==card.Id);
                if(chain.Data["source_path"] is not null)compiled["timeout_seconds"]=card.TimeoutSeconds;
                var currentPage=document.Pages.FirstOrDefault(p=>p.Id==card.PageId);
                var transition=card.TransitionId.Length>0?currentPage?.Transitions.FirstOrDefault(t=>t.Id==card.TransitionId):card.Target.Length==0?null:currentPage?.Transitions.FirstOrDefault(t=>t.Target==card.Target);
                var linked=card.TransitionId.Length>0&&transition is not null;
                var anchor=document.FindAnchor(linked?transition!.AnchorId:card.AnchorId);
                var state=linked?anchor?.States.FirstOrDefault():document.ResolveState(card);
                if(anchor is not null&&state is not null)
                {
                    if(state.Mode=="coordinate")
                    {compiled["fixed_box"]=state.Data["box"]!.DeepClone();compiled["fixed_geometry"]=new JsonArray(document.Width,document.Height,document.Dpi);}
                    // A card owns its runtime target: shared names must not let another card overwrite its state or click policy.
                    var target="workbench.target."+Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(chain.Id+"\n"+card.Id)));compiled["target"]=target;
                    var page=document.Pages.FirstOrDefault(p=>p.Id==card.PageId);var section=page is null?"pages":EditorRepository.Text(page.Data,"source_section","pages");var pageKey=page is null?card.PageId:EditorRepository.Text(page.Data,"source_key",page.Id);
                    if(profile[section]?[pageKey] is JsonObject spec)
                    {
                        var random=card.RandomMode=="inherit"?state.RandomClick:card.RandomClick;
                        PutTarget(spec,target,anchor,state,random);
                        if(section=="pages")foreach(var variant in profile["page_variants"]!.AsObject().Select(p=>p.Value).OfType<JsonObject>().Where(v=>EditorRepository.Text(v,"page")==RuntimePage(document,card.PageId)))PutTarget(variant,target,anchor,state,random);
                    }
                }
                compiled["page_id"]=RuntimePage(document,card.PageId);compiled["expected_page_id"]=RuntimePage(document,card.ExpectedPageId);
                if(transition is not null&&transition.DestinationPageId.Length>0)
                {if(card.ExpectedPageId.Length==0)compiled["expected_page_id"]=RuntimePage(document,transition.DestinationPageId);if((linked||!card.Data.ContainsKey("action"))&&transition.Action is "click" or "right")compiled["action"]=transition.Action;}
                compiled["condition_anchors"]=new JsonArray(WorkbenchModel.SplitIds(card.ConditionAnchorIds).Select(id=>document.FindAnchor(id)).Where(a=>a is not null).Cast<WorkbenchAnchor>().Where(a=>a.States.Any(s=>s.Mode=="image")).Select(a=>(JsonNode?)JsonValue.Create(StateKey(a,a.States.First(s=>s.Mode=="image")))).ToArray());
                compiled["expected_anchors"]=new JsonArray(WorkbenchModel.SplitIds(card.ExpectedAnchorIds).Select(id=>document.FindAnchor(id)!).Select(a=>(JsonNode?)JsonValue.Create(StateKey(a,a.States.First(s=>s.Mode=="image")))).ToArray());
            }
            flows[chain.RuntimeKey]=flow;
            if(chain.Data["source_path"] is JsonArray sourcePath&&EditorRepository.GetPath(configuration,sourcePath) is JsonObject route)
            {
                route["workbench_override"]=true;
                if(chain.Data["staged_route"]?.GetValue<bool>()==true)
                {
                    string? previousStage=null,previousCollection=null;
                    foreach(var compiled in cards.OfType<JsonObject>())
                    {
                        var stage=EditorRepository.Text(compiled,"stage");var collection=EditorRepository.Text(compiled,"stage_collection");
                        if(stage.Length==0){stage=previousStage??EditorRepository.Text(route,"start");collection=previousCollection??"choices";compiled["stage"]=stage;compiled["stage_collection"]=collection;}
                        if(route["stages"]?[stage] is null||collection is not("choices" or "search"))throw new InvalidDataException(chain.Name+"：步骤所属导航阶段无效");
                        if(EditorRepository.Text(compiled,"action") is not("click" or "right" or "scroll" or "zoom_max" or "drag"))throw new InvalidDataException(EditorRepository.Text(compiled,"name")+"：地图导航阶段请使用点击、拖拽或缩放；条件与等待由页面状态和原导航阶段确认");
                        previousStage=stage;previousCollection=collection;
                    }
                    foreach(var(stageName,stage)in route["stages"]!.AsObject())foreach(var collection in new[]{"choices","search"})
                    {
                        var matches=cards.OfType<JsonObject>().Where(c=>c["enabled"]?.GetValue<bool>()!=false&&EditorRepository.Text(c,"stage")==stageName&&EditorRepository.Text(c,"stage_collection")==collection).ToArray();if(stage?[collection] is null&&matches.Length==0)continue;
                        stage![collection]=new JsonArray(matches.Select(c=>(JsonNode)CompileRouteStep(c)).ToArray());
                    }
                }
                else if(chain.Data["single_route"]?.GetValue<bool>()==true)
                {var active=cards.OfType<JsonObject>().Where(c=>c["enabled"]?.GetValue<bool>()!=false).ToArray();if(active.Length!=1)throw new InvalidDataException(chain.Name+"：当前路线需要一个可执行步骤");var updated=CompileRouteStep(active[0]);foreach(var(k,v)in updated)route[k]=v?.DeepClone();}
                else route["steps"]=new JsonArray(cards.OfType<JsonObject>().Where(c=>c["enabled"]?.GetValue<bool>()!=false&&EditorRepository.Text(c,"role","navigation")=="navigation").Select(c=>(JsonNode)CompileRouteStep(c)).ToArray());
            }
        }
        configuration["workbench_chains"]=flows;
        if(configuration["purchase"]?["pages"] is JsonObject purchasePages)
            foreach(var name in purchasePages.Select(p=>p.Key).ToArray())if(profile["pages"]?[name] is{} edited)purchasePages[name]=edited.DeepClone();
        foreach(var strategy in document.Strategies)
        {
            if(JsonNode.DeepEquals(strategy.Data["value"],strategy.Data["original"]))continue;
            var path=strategy.Data["path"]!.AsArray();var old=EditorRepository.GetPath(configuration,path);if(old is null||!WorkbenchValidation.SameType(old,strategy.Data["original"]!))throw new InvalidDataException(strategy.Name+"：原参数已变化，请重新载入");
            JsonNode current=configuration;foreach(var part in path.Take(path.Count-1))current=part is JsonValue v&&v.TryGetValue<int>(out var i)?current[i]!:current[part!.GetValue<string>()]!;
            if(path.Last() is JsonValue last&&last.TryGetValue<int>(out var index))current[index]=strategy.Data["value"]?.DeepClone();else current[path.Last()!.GetValue<string>()]=strategy.Data["value"]?.DeepClone();
        }
    }
    static string RuntimePage(WorkbenchDocument document,string id)=>document.Pages.FirstOrDefault(p=>p.Id==id) is {} page?EditorRepository.Text(page.Data,"runtime_page",id):id;
    static JsonObject CompileRouteStep(JsonObject card)
    {var step=card["source"]?.DeepClone()?.AsObject()??new JsonObject();step["page"]=card["page_id"]?.DeepClone();step["target"]=card["target"]?.DeepClone();step["expected_page"]=card["expected_page_id"]?.DeepClone();step["kind"]=card["action"]?.DeepClone();if(card["timeout_seconds"] is{} timeout)step["timeout_s"]=timeout.DeepClone();return step;}
    static void ApplyInline(JsonObject configuration,WorkbenchAnchor anchor)
    {
        if(anchor.Data["source_path"] is not JsonArray path||anchor.Data["source_spec"] is not JsonObject original||anchor.States.FirstOrDefault() is not {} state)return;
        if(JsonNode.DeepEquals(anchor.Data["imported_state"],state.ToJson()))return;
        var target=EditorRepository.GetPath(configuration,path)?.AsObject();if(target is null)return;
        if(anchor.Id.StartsWith("reading:",StringComparison.Ordinal)){target["region"]=state.Data["box"]?.DeepClone();return;}
        if(anchor.Id.StartsWith("legacy:",StringComparison.Ordinal))
        {target["label_template"]=state.Path;target["label_sha256"]=state.Sha256;target["label_crop_native"]=state.Data["box"]?.DeepClone();target["label_score"]=state.Data["threshold"]?.DeepClone();return;}
        foreach(var field in original.Select(x=>x.Key))if(state.Data[field] is{} value)target[field]=value.DeepClone();
        if(original.ContainsKey("label_score"))target["label_score"]=state.Data["threshold"]?.DeepClone();
    }
    static string StateKey(WorkbenchAnchor anchor,WorkbenchAnchorState state)=>state.TemplateKey.Length>0?state.TemplateKey:WorkbenchRepository.RuntimeAnchorName(anchor.Id,state.Id);
    static void PutTarget(JsonObject page,string target,WorkbenchAnchor anchor,WorkbenchAnchorState state,bool random)
    {
        page["random_targets"]?.AsObject().Remove(target);
        if(state.Mode=="coordinate")
        {page["coordinate_targets"]??=new JsonObject();page["coordinate_targets"]![target]=new JsonObject{["box"]=state.Data["box"]!.DeepClone(),["shape"]=state.Shape,["random"]=random};page["targets"]?.AsObject().Remove(target);}
        else
        {page["targets"]??=new JsonObject();page["targets"]![target]=StateKey(anchor,state);page["coordinate_targets"]?.AsObject().Remove(target);if(random){page["random_targets"]??=new JsonObject();page["random_targets"]![target]=new JsonObject{["shape"]=state.Shape};}}
    }
    public static void ValidateAssets(EditorRepository repository,WorkbenchDocument document)
    {
        foreach(var anchor in document.Anchors)foreach(var state in anchor.States.Where(s=>s.Mode=="image"))
        {
            var path=repository.ResolveImagePath(state.Path);if(!File.Exists(path))throw new InvalidDataException(anchor.Name+"：图片不存在");
            if(new FileInfo(path).Length>8*1024*1024)throw new InvalidDataException(anchor.Name+"：图片超过 8 MiB");
            if(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))!=state.Sha256)throw new InvalidDataException(anchor.Name+"：图片校验失败，请重新框选");
        }
    }
}
