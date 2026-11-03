using System.Text.Json.Nodes;
using MabiAgent.Core;
using MabiAgent.Desktop.Editors;
using MabiAgent.Windows;
using MabiAgent.Workflows;
using Xunit;

public sealed class SharedWorkflowAuditTests
{
    static string Root=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
    [Fact] public void EveryInstalledTemplatePublishesThroughSharedRuntimeConfiguration()
    {
        var repository=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"mabi-audit-"+Guid.NewGuid().ToString("N")));
        var fishing=repository.Templates.Where(t=>EditorRepository.Text(t,"runtime_key").StartsWith("point_fishing_",StringComparison.Ordinal)).ToArray();
        Assert.Equal(6,fishing.Length);foreach(var t in fishing)Assert.Throws<InvalidDataException>(()=>repository.ValidateChain(t,true));
        var documents=repository.Templates.Except(fishing).Select(t=>{var copy=t.DeepClone().AsObject();copy["id"]=EditorRepository.PresetId(EditorRepository.Text(copy,"template_key"));copy["status"]="published";return copy;}).ToArray();
        var invalid=new List<string>();foreach(var document in documents){try{repository.ValidateChain(document,true);}catch(Exception e){invalid.Add(EditorRepository.Text(document,"template_key")+": "+e.Message);}}
        Assert.True(invalid.Count==0,string.Join("\n",invalid));
        var compiled=PublishedCatalogCompiler.Compile(repository,documents,includeWorkbench:false);
        var catalog=new RuntimeCatalog(compiled);var routes=ScreenRoutes.FromConfiguration(Root,compiled,repository.UserAssetRoot);
        Assert.Equal(repository.Catalog["recipes"]!.AsObject().Count,catalog.Recipes.Count);Assert.Equal(repository.Catalog["devices"]!.AsObject().Count,routes.Devices.Count);
        foreach(var (key,recipe) in catalog.Production)
        {Assert.True(routes.Recipes.ContainsKey(key),key);Assert.True(routes.Devices.ContainsKey(recipe.Device),key);Assert.NotEmpty(routes.Devices[recipe.Device].QueuePage);}
        foreach(var (key,route) in catalog.Gathering)
        {Assert.True(routes.Configuration["gathering_routes"]?[key]?["navigation"] is JsonObject,key);Assert.Equal(10,route.AttemptsPerBatch);}
    }
    [Fact] public void LaterPublishedProductionAndGatheringReuseTheSameRuntimeContracts()
    {
        var repository=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"mabi-future-audit-"+Guid.NewGuid().ToString("N")));
        var production=repository.CreateDraft("production:catalog_wood_04");production["id"]="future_wood";production["runtime_key"]="custom_future_wood";production["status"]="published";production["parameters"]!["duration_s"]=25201;
        var gathering=repository.CreateDraft("gathering:iron_ore_nearby");gathering["id"]="future_gather";gathering["runtime_key"]="custom_future_gather";gathering["status"]="published";
        var compiled=PublishedCatalogCompiler.Compile(repository,[production,gathering],includeWorkbench:false);var catalog=new RuntimeCatalog(compiled);var routes=ScreenRoutes.FromConfiguration(Root,compiled,repository.UserAssetRoot);
        Assert.Equal("wood",catalog.Production["custom_future_wood"].Device);Assert.Equal(25201,catalog.Production["custom_future_wood"].DurationSeconds);
        Assert.Equal(routes.Recipes["catalog_wood_04"].Device,routes.Recipes["custom_future_wood"].Device);
        Assert.Equal(10,catalog.Gathering["custom_future_gather"].AttemptsPerBatch);Assert.NotNull(compiled["gathering_observer"]!["routes"]!["custom_future_gather"]);
    }
    [Fact] public void GatheringDraftShowsOnlyThreeTargetDifferencesAndRetainsSharedNavigation()
    {
        var repository=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"mabi-draft-audit-"+Guid.NewGuid().ToString("N")));
        var draft=repository.CreateDraft("gathering:iron_ore_nearby");var before=draft["nodes"]!.ToJsonString();
        var differences=EditorRepository.GatheringDifferenceNodeIds(draft);
        Assert.Equal(3,differences.Count);Assert.Contains("start",differences);Assert.Contains("observation",differences);
        Assert.Equal(before,draft["nodes"]!.ToJsonString());Assert.Equal("鐵礦脈",draft["parameters"]!["node"]!.GetValue<string>());
        Assert.Contains(draft["nodes"]!.AsArray(),n=>EditorRepository.Text(n!,"id")=="step_0"&&!differences.Contains("step_0"));
        Assert.Equal("iron_ore_nearby",repository.Catalog["gathering_routes"]!["point_mining_01"]!["alias_of"]!.GetValue<string>());
        Assert.Equal(repository.Catalog["gathering_routes"]!["iron_ore_nearby"]!["navigation"]!["detail_page"]!.GetValue<string>(),repository.Catalog["gathering_routes"]!["point_mining_01"]!["navigation"]!["detail_page"]!.GetValue<string>());
    }
}
