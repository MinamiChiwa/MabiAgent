using System.Text.Json.Nodes;
namespace MabiAgent.Windows;

/// <summary>Ordinary navigation cannot stand in for a journalled semantic action.</summary>
internal sealed class NavigationConsumptionPolicy
{
    readonly HashSet<string> targets=new(StringComparer.Ordinal){"production.craft","production.collect","gathering.start","repair.confirm","tidy.confirm","bag.auto_equip","purchase.confirm","bag.sale_confirm","travel.use_wings","fishing.start","fishing.reel","fishing.switch_mode","fishing.confirm_mode","storage.prepare","storage.confirm","storage.prepare_transfer","storage.confirm_transfer","relogin.menu","relogin.exit","relogin.character_select","relogin.enter"};
    readonly HashSet<string> templates=new(StringComparer.Ordinal){"craft_processing","maint_tidy_confirm_action","maint_repair_confirm","maint_auto_equip","storage_take_button","storage_transfer_confirm","purchase.purchase_label","surplus_sale_confirm","travel.wing_button","scene_menu","relogin.menu_exit","relogin.select_character","relogin.start_game"};
    readonly HashSet<string> hashes=new(StringComparer.OrdinalIgnoreCase),paths=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,List<PixelBox>> regions=new(StringComparer.Ordinal);
    readonly NavigationProfile profile;
    public NavigationConsumptionPolicy(ScreenRoutes routes)
    {
        profile=routes.Profile;
        void Target(JsonNode? node){if(node is JsonValue v&&v.TryGetValue<string>(out var text)&&text.Length>0)targets.Add(text);}
        Target(routes.Configuration["purchase"]?["confirm_target"]);
        if(routes.Configuration["travel_wings"]?["use_box"] is{} wingBox)Add("scene",NavigationProfile.ReadBox(wingBox));
        Target(profile.Definition["storage_supply"]?["selection"]?["prepare_target"]);Target(profile.Definition["storage_supply"]?["transfer"]?["confirm_target"]);
        foreach(var row in routes.Configuration["gathering_routes"]?.AsObject().Select(p=>p.Value)??[])Target(row?["navigation"]?["start_target"]);
        foreach(var recipe in routes.Recipes.Values){templates.Add(recipe.CraftAnchor);Add(recipe.Page,recipe.CraftButtonBox);}
        foreach(var device in routes.Devices.Values)foreach(var name in device.CollectAnchors){templates.Add(name);if(profile.Templates.TryGetValue(name,out var spec))Add(device.QueuePage,spec.ControlBox??spec.Box);}
        var pages=new[]{"pages","page_variants"}.SelectMany(section=>profile.Definition[section]?.AsObject().Select(p=>(Key:p.Key,Spec:p.Value!))??[]).ToArray();
        foreach(var (_,page) in pages)foreach(var(target,node)in page["targets"]?.AsObject()??[])if(targets.Contains(target)&&node is not null)templates.Add(node.GetValue<string>());
        foreach(var name in templates)if(profile.Templates.TryGetValue(name,out var spec)){hashes.Add(spec.Sha256);paths.Add(spec.Path);}
        foreach(var(page,name)in new[]{("scene","scene_menu"),("scene","travel.wing_button"),("relogin_menu","relogin.menu_exit"),("relogin_exit","relogin.select_character"),("relogin_characters","relogin.start_game")})
            if(profile.Templates.TryGetValue(name,out var spec))Add(page,spec.ControlBox??spec.Box);
        foreach(var (key,page) in pages)
        {
            var runtime=page["page"]?.GetValue<string>()??key;
            foreach(var(target,node)in page["coordinate_targets"]?.AsObject()??[])if(targets.Contains(target)&&node is not null)Add(runtime,NavigationProfile.ReadBox(node));
            foreach(var(target,node)in page["targets"]?.AsObject()??[])if(node is not null&&profile.Templates.TryGetValue(node.GetValue<string>(),out var spec)&&(targets.Contains(target)||Protected(spec)))Add(runtime,spec.ControlBox??spec.Box);
        }
    }
    void Add(string page,PixelBox box){if(!regions.TryGetValue(page,out var boxes))regions[page]=boxes=[];boxes.Add(box);}
    bool Protected(TemplateDefinition spec)=>templates.Contains(spec.Name)||hashes.Contains(spec.Sha256)||paths.Contains(spec.Path);
    public void Verify(string target,UiObservation fresh,Func<PixelBox,PixelBox> scale)
    {
        if(targets.Contains(target))Block();
        if(!fresh.View.Targets.TryGetValue(target,out var point))throw new InputBlockedException("当前画面未找到目标");
        var click=fresh.View.TargetRegions is{} targetRegions&&targetRegions.TryGetValue(target,out var known)?known:new PixelBox(point.X,point.Y,point.X+1,point.Y+1);
        bool Overlaps(PixelBox box)=>click.Left<box.Right&&click.Right>box.Left&&click.Top<box.Bottom&&click.Bottom>box.Top;
        foreach(var hit in fresh.View.Hits)if(profile.Templates.TryGetValue(hit.Name,out var spec)&&Protected(spec)&&Overlaps(hit.Box))Block();
        foreach(var section in new[]{"pages","page_variants"})foreach(var(key,page)in profile.Definition[section]?.AsObject()??[])
            if((page?["page"]?.GetValue<string>()??key)==fresh.View.Page&&page?["targets"]?[target] is{} name&&profile.Templates.TryGetValue(name.GetValue<string>(),out var spec)&&Protected(spec))Block();
        if(regions.TryGetValue(fresh.View.Page,out var boxes)&&boxes.Any(box=>Overlaps(scale(box))))Block();
        if(fresh.View.TargetRegions is{} allRegions)foreach(var name in targets)if(allRegions.TryGetValue(name,out var box)&&fresh.View.Targets.ContainsKey(name)&&Overlaps(box))Block();
    }
    static void Block()=>throw new InputBlockedException("该目标须由原任务核验并记录消费意图");
}
