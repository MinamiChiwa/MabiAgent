using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MabiAgent.Core;
using OpenCvSharp;
namespace MabiAgent.Windows;

public sealed record TemplateDefinition(string Name,string Path,string Sha256,PixelBox Box,PixelBox Search,double Threshold,string? Mode,int LightThreshold,bool RelativeContrast,int? RelativeContrastThreshold,double? GlyphThreshold,PixelBox? ControlBox,RgbImage Image);
public sealed class NavigationProfile
{
    public int Width{get;}public int Height{get;}public int Dpi{get;}public JsonObject Definition{get;}public string Revision{get;}
    public IReadOnlyDictionary<string,TemplateDefinition> Templates{get;}
    public static NavigationProfile Load(string root,string relative)=>FromDefinition(root,new CatalogLoader(root).Load(relative));
    private NavigationProfile(int width,int height,int dpi,JsonObject definition,IReadOnlyDictionary<string,TemplateDefinition> templates)
    {
        Width=width;Height=height;Dpi=dpi;Definition=definition;
        Templates=new System.Collections.ObjectModel.ReadOnlyDictionary<string,TemplateDefinition>(templates.ToDictionary());
        Revision=Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(definition.ToJsonString())));
    }
    internal NavigationProfile Snapshot()=>new(Width,Height,Dpi,(JsonObject)Definition.DeepClone(),Templates);
    public static NavigationProfile FromDefinition(string root,JsonObject definition,string? userAssetRoot=null)
    {
        var loader=new CatalogLoader(root);var size=definition["client_size"]!.AsArray();int width=size[0]!.GetValue<int>(),height=size[1]!.GetValue<int>(),dpi=definition["dpi"]!.GetValue<int>();
        var layout=ReviewedLayout.For(width,height,dpi);if(layout.ReferenceWidth!=width||layout.ReferenceHeight!=height)throw new InvalidDataException("模板必须来自原校准尺寸");
        var templates=new Dictionary<string,TemplateDefinition>();var images=new Dictionary<string,RgbImage>(StringComparer.OrdinalIgnoreCase);
        foreach(var (name,node) in definition["templates"]!.AsObject())
        {
            var spec=node!.AsObject();var relative=spec["path"]!.GetValue<string>();var path=ResolveTemplatePath(root,relative,userAssetRoot);

            var bytes=File.ReadAllBytes(path);var sha=spec["sha256"]!.GetValue<string>();
            if(bytes.Length>8*1024*1024||spec["bytes"] is {} declaredBytes&&bytes.LongLength!=declaredBytes.GetValue<long>()||Convert.ToHexStringLower(SHA256.HashData(bytes))!=sha)throw new InvalidDataException("模板摘要或长度不符："+name);
            var box=ReadBox(spec["box"]!);var search=ReadBox(spec["search"]??spec["box"]!);var threshold=spec["threshold"]?.GetFiniteDouble()??.97;
            if(!box.IsInside(width,height)||!search.IsInside(width,height)||!double.IsFinite(threshold)||threshold is <=0 or >1)throw new InvalidDataException("模板区域或阈值无效："+name);
            if(!images.TryGetValue(sha,out var image)){using var encoded=Cv2.ImDecode(bytes,ImreadModes.Color);if(encoded.Empty())throw new InvalidDataException("图片解码失败");using var rgb=new Mat();Cv2.CvtColor(encoded,rgb,ColorConversionCodes.BGR2RGB);var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);image=new(rgb.Width,rgb.Height,pixels);images[sha]=image;}
            if(image.Width!=box.Width||image.Height!=box.Height)throw new InvalidDataException("模板原生尺寸不符："+name);
            templates[name]=new(name,relative,sha,box,search,threshold,spec["match_mode"]?.GetValue<string>(),spec["light_threshold"]?.GetValue<int>()??100,spec["relative_contrast"]?.GetValue<bool>()??false,spec["relative_contrast_threshold"]?.GetValue<int>(),spec["glyph_threshold"]?.GetFiniteDouble(),spec["control_box"] is {} control?ReadBox(control):null,image);
        }
        var reviewed=(JsonObject)definition.DeepClone();
        ApplyCoordinateControls(reviewed,templates,width,height);
        return new(width,height,dpi,reviewed,templates);
    }
    private static void ApplyCoordinateControls(JsonObject definition,IReadOnlyDictionary<string,TemplateDefinition> templates,int width,int height)
    {
        var controls=Names(definition["coordinate_control_targets"]).ToHashSet(StringComparer.Ordinal);
        foreach(var spec in new[]{"pages","page_variants"}.SelectMany(section=>definition[section]?.AsObject().Select(p=>p.Value!.AsObject())??[]))
        {
            var moved=new HashSet<string>();var targets=spec["targets"] as JsonObject;
            foreach(var (target,value) in targets?.ToArray()??[])
            {
                if(!controls.Contains(target)&&spec["fixed_targets"]?[target] is null)continue;
                var anchor=value!.GetValue<string>();
                if(!templates.TryGetValue(anchor,out var template))continue;
                var box=spec["coordinate_targets"]?[target]??spec["fixed_targets"]?[target];
                if(box is null){var b=template.ControlBox??template.Box;box=new JsonArray(b.Left,b.Top,b.Right,b.Bottom);}
                if(!ReadBox(box).IsInside(width,height))throw new InvalidDataException("固定点击区域越界："+target);
                spec["coordinate_targets"]??=new JsonObject();spec["coordinate_targets"]![target]=box.DeepClone();
                targets!.Remove(target);spec["fixed_targets"]?.AsObject().Remove(target);moved.Add(anchor);
            }
            // Keep page identity and state evidence; a fixed button needs no second glyph match.
            var anchors=Names(spec["anchors"]);var identity=anchors.Where(a=>!moved.Contains(a)).ToArray();
            if(identity.Length>0)spec["anchors"]=new JsonArray(identity.Select(a=>(JsonNode?)JsonValue.Create(a)).ToArray());
            if(spec["coordinate_targets"] is JsonObject coordinates)foreach(var (target,node) in coordinates)
            {
                if(node is null||!ReadBox(node).IsInside(width,height))throw new InvalidDataException("固定点击区域越界："+target);
                ReadClickShape(node);
            }
        }
    }
    public static string ResolveTemplatePath(string root,string source,string? userAssetRoot=null)
    {
        var path=Path.GetFullPath(Path.Combine(root,source));var installed=Path.GetFullPath(Path.Combine(root,"assets/templates"));
        var user=userAssetRoot is null?null:Path.TrimEndingDirectorySeparator(Path.GetFullPath(userAssetRoot));
        var allowed=path.StartsWith(installed+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)?installed:user is not null&&path.StartsWith(user+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)?user:null;
        if(allowed is null)throw new InvalidDataException("模板必须来自安装资产或任务链资产目录");
        for(var at=path;at.Length>=allowed.Length;at=Path.GetDirectoryName(at)??"")if((File.Exists(at)||Directory.Exists(at))&&(File.GetAttributes(at)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("模板路径不得经过链接");
        if(allowed==user&&(Path.GetFileName(path)!="original.bin"||!(Path.GetFileName(Path.GetDirectoryName(path))??"").StartsWith("image_",StringComparison.Ordinal)))throw new InvalidDataException("任务链资产标识无效");
        return path;
    }
    internal static PixelBox ReadBox(JsonNode node){if(node is JsonObject)node=node["box"]??throw new InvalidDataException("点击区域缺少 box");var a=node.AsArray();if(a.Count!=4)throw new InvalidDataException("区域必须有四个整数");return new(a[0]!.GetValue<int>(),a[1]!.GetValue<int>(),a[2]!.GetValue<int>(),a[3]!.GetValue<int>());}
    internal static ClickRegionShape ReadClickShape(JsonNode node)
    {
        var shape=node is JsonObject?node["shape"]?.GetValue<string>():null;
        if(shape=="circle"&&node is JsonObject obj&&obj["box"] is not null&&ReadBox(node) is var box&&box.Width!=box.Height)throw new InvalidDataException("圆形点击区域的宽高必须相同");
        return shape switch{null or "rectangle"=>ClickRegionShape.Rectangle,"ellipse" or "circle"=>ClickRegionShape.Ellipse,_=>throw new InvalidDataException("未知点击区域形状："+shape)};
    }
    internal static string[] Names(JsonNode? node)=>node?.AsArray().Select(v=>v!.GetValue<string>()).ToArray()??[];
}
public sealed record DeviceRoute(string Key,string QueuePage,string CategoryTarget,string GotoTarget,IReadOnlyList<PixelBox> Slots,IReadOnlyList<string> CollectAnchors,IReadOnlyList<string> ArrivalAnchors,string RecipeCloseTarget);
public sealed record RecipeRoute(string Key,string Device,double DurationSeconds,string OpenTarget,string Page,IReadOnlyList<string> ConfirmAnchors,string CraftAnchor,PixelBox CraftButtonBox,long OutputItemId,int OutputPerRound,PixelPoint OpenClickOffset);
public sealed class ScreenRoutes
{
    public JsonObject Configuration{get;}public NavigationProfile Profile{get;}public IReadOnlyDictionary<string,DeviceRoute> Devices{get;}public IReadOnlyDictionary<string,RecipeRoute> Recipes{get;}public string Revision{get;}
    public string DefaultRecipe{get;}
    private ScreenRoutes(JsonObject config,NavigationProfile profile,Dictionary<string,DeviceRoute> devices,Dictionary<string,RecipeRoute> recipes){Configuration=config;Profile=profile;Devices=devices;Recipes=recipes;DefaultRecipe=config["default_recipe"]!.GetValue<string>();if(!recipes.ContainsKey(DefaultRecipe))throw new InvalidDataException("默认配方不存在");Revision=Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(profile.Revision+"\n"+config.ToJsonString())));}
    internal ScreenRoutes Snapshot()=>new((JsonObject)Configuration.DeepClone(),Profile.Snapshot(),Devices.ToDictionary(),Recipes.ToDictionary());
    public static ScreenRoutes Load(string root,string relative="config/screen_catalog.1280x960.tw.json")=>FromConfiguration(root,new CatalogLoader(root).Load(relative));
    public static ScreenRoutes FromConfiguration(string root,JsonObject configuration,string? userAssetRoot=null)
    {
        var loader=new CatalogLoader(root);var config=(JsonObject)configuration.DeepClone();if(config["schema_version"]!.GetValue<int>()!=1)throw new InvalidDataException("不支持的屏幕目录版本");
        var profile=loader.Load(config["navigation_profile"]!.GetValue<string>());if(!JsonNode.DeepEquals(profile["client_size"],config["client_size"])||!JsonNode.DeepEquals(profile["dpi"],config["dpi"]))throw new InvalidDataException("导航与路由尺寸不同");
        if(config["ui"] is JsonObject ui)foreach(var (field,value) in ui){if(value is JsonObject extra){if(profile[field] is not JsonObject){profile[field]=new JsonObject();}foreach(var (key,v) in extra)profile[field]![key]=v?.DeepClone();}else profile[field]=value?.DeepClone();}
        if(config["template_sources"] is JsonObject sources)foreach(var (name,node) in sources){var source=node!;JsonNode selected=loader.Load(source["profile"]!.GetValue<string>());foreach(var part in source["select"]!.AsArray()){if(part is JsonValue value&&value.TryGetValue<int>(out var index))selected=selected[index]!;else selected=selected[part!.GetValue<string>()]!;}var spec=(JsonObject)selected.DeepClone();if(source["overrides"] is JsonObject overrides)foreach(var (key,v) in overrides)spec[key]=v?.DeepClone();spec["search"]??=spec["box"]!.DeepClone();spec["threshold"]??=JsonValue.Create(.97);profile["templates"]![name]=spec;}
        if(config["ui"]?["templates"] is JsonObject replacements)foreach(var (key,v) in replacements)profile["templates"]![key]=v?.DeepClone();
        if(config["ui_remove"] is JsonObject remove)foreach(var (section,names) in remove)foreach(var name in NavigationProfile.Names(names))profile[section]?.AsObject().Remove(name);
        // Arrival variants identify the same recipe list. Keep recipe targets
        // available there without copying the catalogue into every variant.
        if(profile["page_variants"] is JsonObject pageVariants&&config["recipes"] is JsonObject configuredRecipes)
        foreach(var variant in pageVariants.Select(p=>p.Value).OfType<JsonObject>())
        {
            var page=variant["page"]?.GetValue<string>();if(page is null||!page.EndsWith("_panel",StringComparison.Ordinal))continue;
            foreach(var recipeNode in configuredRecipes.Select(p=>p.Value).OfType<JsonObject>())
            {
                var target=recipeNode["open_target"]?.GetValue<string>();if(target is null||profile["pages"]?[page]?["targets"]?[target] is not{} anchor)continue;
                variant["targets"]??=new JsonObject();variant["targets"]![target]??=anchor.DeepClone();
            }
        }
        if(config["gathering_observer"]?["templates"] is JsonObject gatheringTemplates)foreach(var (name,node) in gatheringTemplates)
        {
            var spec=(JsonObject)node!.DeepClone();spec["box"]??=spec["crop_native"]?.DeepClone();
            if(spec["match_mode"]?.GetValue<string>()=="gold_text")spec["match_mode"]="yellow_text";
            profile["templates"]!["gathering_observer."+name]=spec;
            if(spec["variants"] is JsonArray variants){profile["anchor_variants"]??=new JsonObject();profile["anchor_variants"]!["gathering_observer."+name]=new JsonArray(variants.Select(v=>(JsonNode?)JsonValue.Create("gathering_observer."+v!.GetValue<string>())).ToArray());}
        }
        var overlayPath=loader.Resolve("config/screen_overlays.1280x960.tw.json");
        if(File.Exists(overlayPath))
        {
            var overlays=loader.Load("config/screen_overlays.1280x960.tw.json");if(JsonNode.DeepEquals(overlays["client_size"],config["client_size"]))
            {
                foreach(var (name,node) in overlays["templates"]!.AsObject())profile["templates"]!["overlay."+name]=node!.DeepClone();
                profile["pages"]!["production_complete"]=new JsonObject{["anchors"]=new JsonArray("overlay.production_complete","overlay.confirm"),["targets"]=new JsonObject{["completion.confirm"]="overlay.confirm"}};
            }
        }
        foreach(var extra in new[]{("travel_wings","screen_travel.1280x960.tw.json","travel."),("relogin","screen_relogin.1280x960.tw.json","relogin."),("notifications","session_notifications.1280x960.tw.json","notification.")})
        {
            var relative="config/"+extra.Item2;if(!File.Exists(loader.Resolve(relative)))continue;
            var source=config[extra.Item1] as JsonObject??loader.Load(relative);config[extra.Item1]=source.DeepClone();
            foreach(var(name,node)in source["templates"]?.AsObject()??[]){var spec=(JsonObject)node!.DeepClone();spec["box"]??=spec["crop_native"]?.DeepClone();spec["search"]??=spec["box"]?.DeepClone();profile["templates"]![extra.Item3+name]=spec;}
        }
        var purchasePath="config/screen_purchase.1280x960.tw.json";
        if(File.Exists(loader.Resolve(purchasePath)))
        {
            var purchase=config["purchase"] as JsonObject??loader.Load(purchasePath);config["purchase"]=purchase.DeepClone();
            foreach(var(name,node)in purchase["templates"]?.AsObject()??[])profile["templates"]!["purchase."+name]=node!.DeepClone();
            foreach(var(name,node)in purchase["pages"]?.AsObject()??[])profile["pages"]![name]=node!.DeepClone();
            // Alternate region header uses the same map targets and state machine.
            if(profile["pages"]?["region_map"] is {} region&&profile["templates"]?["purchase.map_region"] is not null)
            {
                var variant=region.DeepClone();variant["page"]="region_map";variant["anchors"]=new JsonArray("purchase.map_region");
                profile["page_variants"]??=new JsonObject();profile["page_variants"]!["region_header_layout"]=variant;
            }
            config["maintenance"]??=new JsonObject();config["maintenance"]!["navigation"]??=new JsonObject();
            var existingPurchaseNavigation=config["maintenance"]!["navigation"]?["purchase_shop"]?.DeepClone();
            config["maintenance"]!["navigation"]!["purchase_shop"]=purchase["navigation_pages"]?.DeepClone()??new JsonObject();
            config["maintenance"]!["navigation"]!["purchase_exit"]=purchase["exit_navigation"]?.DeepClone()??new JsonObject();
            config["maintenance"]!["exit_navigation"]??=new JsonObject();
            foreach(var(page,exit)in purchase["exit_navigation"]?.AsObject()??[])
                if(config["maintenance"]!["exit_navigation"]![page] is null)config["maintenance"]!["exit_navigation"]![page]=exit?.DeepClone();
            foreach(var(page,route)in existingPurchaseNavigation?.AsObject()??[])if(route?["workbench_override"]?.GetValue<bool>()==true)config["maintenance"]!["navigation"]!["purchase_shop"]![page]=route.DeepClone();
        }
        if(config["maintenance"]?["map_navigation"] is {} mapRoute)
        {
            JsonNode Bind(JsonNode node,string shop,string npc,string travelPage)
            {
                if(node is JsonObject obj)return new JsonObject(obj.Select(p=>KeyValuePair.Create(p.Key,p.Value is null?null:Bind(p.Value,shop,npc,travelPage))));
                if(node is JsonArray array)return new JsonArray(array.Select(v=>v is null?null:Bind(v,shop,npc,travelPage)).ToArray());
                if(node is JsonValue value&&value.TryGetValue<string>(out var name))return JsonValue.Create(name switch{"$shop"=>shop,"$npc"=>npc,"$travel"=>travelPage,_=>name})!;
                return node.DeepClone();
            }
            foreach(var (destination,shop,npc,travel) in new[]{("repair_quote","map.blacksmith","map.repair_npc","repair_travel"),("purchase_shop","map.general_store","map.shop_npc","shop_travel")})
                if(config["maintenance"]?["navigation"]?[destination]?["region_map"]?["workbench_override"]?.GetValue<bool>()!=true)config["maintenance"]!["navigation"]![destination]!["region_map"]=Bind(mapRoute,shop,npc,travel);
        }
        var result=NavigationProfile.FromDefinition(root,profile,userAssetRoot);var devices=new Dictionary<string,DeviceRoute>();
        foreach(var (key,node) in config["devices"]!.AsObject()){var e=node!;var page=e["queue_page"]!.GetValue<string>();var slots=e["slot_boxes"]!.AsArray().Select(x=>NavigationProfile.ReadBox(x!)).ToArray();if(page!=key+"_panel"||slots.Length==0||slots.Any(b=>!b.IsInside(result.Width,result.Height)))throw new InvalidDataException("设备页面或槽位无效");devices[key]=new(key,page,e["category_target"]!.GetValue<string>(),e["goto_target"]!.GetValue<string>(),slots,NavigationProfile.Names(e["collect_anchors"]),NavigationProfile.Names(e["arrival_anchors"]),e["recipe_close_target"]?.GetValue<string>()??"recipe_detail.close");}
        var recipes=new Dictionary<string,RecipeRoute>();foreach(var (key,node) in config["recipes"]!.AsObject()){var e=node!;var device=e["device"]!.GetValue<string>();var duration=e["duration_s"]!.GetFiniteDouble();var page=e["page"]?.GetValue<string>()??"recipe_detail";var output=e["output_item_id"]!.GetValue<long>();var count=e["output_per_round"]!.GetValue<int>();var offset=e["open_click_offset"]?.AsArray();var point=offset is null?new PixelPoint(0,0):new(offset[0]!.GetValue<int>(),offset[1]!.GetValue<int>());var box=NavigationProfile.ReadBox(e["craft_button_box"]!);if(!devices.ContainsKey(device)||!double.IsFinite(duration)||duration<=0||output<=0||count<=0||page!="recipe_detail"||Math.Abs((long)point.X)>120||Math.Abs((long)point.Y)>120||!box.IsInside(result.Width,result.Height))throw new InvalidDataException("配方设备、耗时或产出无效："+key);recipes[key]=new(key,device,duration,e["open_target"]!.GetValue<string>(),page,NavigationProfile.Names(e["confirm_anchors"]),e["craft_anchor"]!.GetValue<string>(),box,output,count,point);}
        return new(config,result,devices,recipes);
    }
}




