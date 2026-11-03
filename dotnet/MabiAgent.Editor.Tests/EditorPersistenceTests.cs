using System.Text.Json.Nodes;
using MabiAgent.Desktop.Editors;
using Xunit;
public sealed class EditorPersistenceTests
{
    [Fact] public void GatheringBatchCountUsesConfigurationWithoutDynamicCounterTemplates()
    {
        var repo=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-editor"));
        var template=repo.Templates.First(t=>EditorRepository.Text(t,"kind")=="gathering");
        var draft=repo.CreateDraft(EditorRepository.Text(template,"template_key"));
        draft["id"]="chain_fixed_batch";draft["runtime_key"]="custom_fixed_batch";draft["status"]="published";draft["parameters"]!["attempts_per_batch"]=12;
        var observationNode=draft["nodes"]!.AsArray().Single(n=>EditorRepository.Text(n!,"role")=="observation")!;
        var originalKey=EditorRepository.Text(template,"runtime_key");
        Assert.Equal(repo.Catalog["gathering_observer"]!["routes"]![originalKey]!["task_anchors"]!.AsArray().Select(n=>n!.GetValue<string>()),observationNode["anchors"]!.AsArray().Select(n=>EditorRepository.Text(n!,"key")));
        var config=PublishedCatalogCompiler.Compile(repo,[draft],includeWorkbench:false);
        Assert.Equal(12,config["gathering_routes"]!["custom_fixed_batch"]!["attempts_per_batch"]!.GetValue<int>());
        var observer=config["gathering_observer"]!["routes"]!["custom_fixed_batch"]!;
        Assert.Equal(12,observer["total"]!.GetValue<int>());
        Assert.Null(observer["total_template"]);Assert.Null(observer["stop_template"]);Assert.Null(observer["idle_template"]);
    }
    [Fact] public void CustomNavigationReplacementKeepsSharedButtonAliases()
    {
        var temporary=Path.Combine(Path.GetTempPath(),"MabiAgent-editor-"+Guid.NewGuid().ToString("N"));try
        {
            var repo=new EditorRepository(Root,temporary);var template=repo.Templates.First(t=>EditorRepository.Text(t,"kind")=="gathering");
            var draft=repo.CreateDraft(EditorRepository.Text(template,"template_key"));draft["id"]="chain_isolated";draft["runtime_key"]="custom_chain_isolated";draft["status"]="published";
            var node=draft["nodes"]!.AsArray().First(n=>EditorRepository.Text(n!,"role")=="navigation"&&n!["anchors"]!.AsArray().Count>0)!;
            var anchor=node["anchors"]![0]!;anchor["variants"]![0]!["threshold"]=.98;
            var page=EditorRepository.Text(node,"page");var original=repo.Routes["ui_profile"]!["pages"]![page]!["targets"]!.DeepClone();
            var config=PublishedCatalogCompiler.Compile(repo,[draft],includeWorkbench:false);
            foreach(var(name,value)in original.AsObject())Assert.True(JsonNode.DeepEquals(value,config["ui"]!["pages"]![page]!["targets"]![name]));
            foreach(var v in config["ui"]!["page_variants"]!.AsObject().Select(p=>p.Value!).Where(v=>EditorRepository.Text(v,"page")==page))
                foreach(var(name,value)in original.AsObject())Assert.True(JsonNode.DeepEquals(value,v["targets"]![name]));
            Assert.Contains(config["gathering_routes"]!["custom_chain_isolated"]!["navigation"]!["steps"]!.AsArray(),s=>EditorRepository.Text(s!,"target").StartsWith("custom_chain_isolated.",StringComparison.Ordinal));
        }finally{if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
    }
    [Fact] public void NavigationUpgradeKeepsRecipeIdentityAndParameters()
    {
        var repo=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-editor"));
        var chain=repo.CreateDraft("production:iron_ingot_from_iron_ore");
        chain["parameters"]!["duration_s"]=725;
        chain["nodes"]![0]!["anchors"]!.AsArray().Clear();
        var before=chain.ToJsonString();
        var upgraded=repo.UpgradeNavigation(chain);
        Assert.Equal(before,chain.ToJsonString());
        Assert.True(JsonNode.DeepEquals(chain["parameters"],upgraded["parameters"]));
        Assert.True(JsonNode.DeepEquals(chain["nodes"]![4],upgraded["nodes"]![4]));
        repo.ValidateChain(upgraded,true);
        Assert.True(JsonNode.DeepEquals(upgraded,repo.UpgradeNavigation(upgraded)));
    }
    [Fact] public void NavigationUpgradeCannotInventNewProductIdentity()
    {
        var repo=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-editor"));
        var chain=repo.CreateDraft("production:iron_ingot_from_iron_ore");
        chain["parameters"]!["target_item_id"]=123;chain["status"]="published";
        Assert.Throws<InvalidDataException>(()=>PublishedCatalogCompiler.Compile(repo,[repo.UpgradeNavigation(chain)]));
    }
    [Fact] public void KnownProductionTargetRenameUpgradesButUnknownActionIsRejected()
    {
        var repo=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-editor"));
        var chain=repo.CreateDraft("production:iron_ingot_from_iron_ore");
        chain["nodes"]![3]!["target"]="metal.open_iron_recipe";
        repo.ValidateChain(repo.UpgradeNavigation(chain),true);
        chain["nodes"]![3]!["target"]="unreviewed.action";
        Assert.Throws<InvalidDataException>(()=>repo.UpgradeNavigation(chain));
    }
    static string Root=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
    [Fact] public void RestorePresetResetsKnownFieldsButKeepsExtensions()
    {
        var repo=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-editor"));var draft=repo.CreateDraft("production:iron_ingot_from_iron_ore");draft["id"]="chain_test";draft["revision"]=4;draft["future"]=true;draft["parameters"]!["duration_s"]=123;draft["parameters"]!["future"]=42;draft["nodes"]![0]!["future"]="keep";
        var restored=repo.RestorePreset(draft);Assert.Equal("chain_test",restored["id"]!.GetValue<string>());Assert.Equal(4,restored["revision"]!.GetValue<int>());Assert.True(restored["future"]!.GetValue<bool>());Assert.Equal(42,restored["parameters"]!["future"]!.GetValue<int>());Assert.Equal("keep",restored["nodes"]![0]!["future"]!.GetValue<string>());Assert.NotEqual(123,restored["parameters"]!["duration_s"]!.GetValue<double>());
    }    [Fact] public void PublishedCompilerCarriesRecipeDurationWithoutScreenMaterialCounters()
    {
        var repo=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-editor"));var draft=repo.CreateDraft("production:iron_ingot_from_iron_ore");draft["id"]="chain_test";draft["runtime_key"]="custom_chain_test";draft["status"]="published";draft["parameters"]!["duration_s"]=725;
        var config=PublishedCatalogCompiler.Compile(repo,[draft]);var recipe=config["recipes"]!["custom_chain_test"]!;
        Assert.Equal(725,recipe["duration_s"]!.GetValue<int>());Assert.Equal("custom_chain_test.open_recipe",recipe["open_target"]!.GetValue<string>());
        Assert.NotNull(config["ui"]!["pages"]!["metal_panel"]!["targets"]!["custom_chain_test.open_recipe"]);
        Assert.Null(config["quantity"]?["materials"]);
    }
    [Fact] public void CompilerRejectsNewOutputWithOldIdentityPictures()
    {
        var repo=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-editor"));var draft=repo.CreateDraft("production:iron_ingot_from_iron_ore");draft["id"]="chain_test";draft["runtime_key"]="custom_chain_test";draft["status"]="published";draft["parameters"]!["target_item_id"]=123;
        Assert.Throws<InvalidDataException>(()=>PublishedCatalogCompiler.Compile(repo,[draft]));
    }
    [Fact] public void InlineCountersAreListedAndLogicCannotChangePermissions()
    {
        var repo=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-editor"));
        Assert.Contains(repo.Anchors(),a=>a.Id.StartsWith("reading:"));Assert.Contains(repo.Anchors(),a=>a.Namespace=="maintenance"||a.Namespace=="quantity");
        Assert.Throws<InvalidDataException>(()=>repo.SaveLogic(0,new JsonArray("ui_profile","templates"),new JsonObject()));
    }
    [Fact] public void IndependentAnchorRebindsOnlySelectedReference()
    {
        var temporary=Path.Combine(Path.GetTempPath(),"MabiAgent-editor-"+Guid.NewGuid().ToString("N"));try
        {
            var repo=new EditorRepository(Root,temporary);var a=repo.Anchors().First(a=>a.Namespace=="ui_profile"&&a.References.Count>1&&a.Spec["box"] is not null);var chosen=a.References[0];var untouched=a.References[1];
            repo.SaveAnchor(0,a,a.Spec,true,chosen);var routes=repo.EffectiveRoutes();Assert.StartsWith("debug_",EditorRepository.GetPath(routes,chosen.Path)!.GetValue<string>());Assert.Equal(a.Name,EditorRepository.GetPath(routes,untouched.Path)!.GetValue<string>());
        }finally{if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
    }
    [Fact] public void DraftRoundTripPreservesUnknownFieldsAndRejectsStaleRevision()
    {
        var temporary=Path.Combine(Path.GetTempPath(),"MabiAgent-editor-"+Guid.NewGuid().ToString("N"));try
        {
            var repo=new EditorRepository(Root,temporary);var draft=repo.CreateDraft("production:iron_ingot_from_iron_ore");draft["future_extension"]=new JsonObject{["value"]=7};draft["parameters"]!["future_parameter"]=true;
            var saved=repo.Save(draft,false);var second=(JsonObject)saved.DeepClone();second["name"]="test";repo.Save(second,false);
            Assert.Equal(7,repo.List().Single()["future_extension"]!["value"]!.GetValue<int>());
            Assert.True(repo.List().Single()["parameters"]!["future_parameter"]!.GetValue<bool>());
            Assert.Throws<IOException>(()=>repo.Save(saved,false));Assert.Equal(2,repo.History(EditorRepository.Text(saved,"id")).Count);
        }finally{if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
    }
    [Fact] public void PublishedChainCannotLoseCraftIdentityOrReorderNodes()
    {
        var repo=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-editor"));var draft=repo.CreateDraft("production:iron_ingot_from_iron_ore");var recipe=draft["nodes"]!.AsArray().Single(n=>EditorRepository.Text(n!,"id")=="recipe")!;
        recipe["anchors"]!.AsArray().Clear();Assert.Throws<InvalidDataException>(()=>repo.ValidateChain(draft,true));
    }
    [Fact] public void SharedAnchorUpdatePreservesReferencesAndUnknownStateFields()
    {
        var temporary=Path.Combine(Path.GetTempPath(),"MabiAgent-editor-"+Guid.NewGuid().ToString("N"));try
        {
            var repo=new EditorRepository(Root,temporary);var a=repo.Anchors().First(a=>a.References.Count>1&&a.Spec["box"] is not null);var before=a.References.Select(r=>r.Path.ToJsonString()).ToArray();var spec=(JsonObject)a.Spec.DeepClone();spec["threshold"]=.95;
            var rev=repo.SaveAnchor(0,a,spec,false,null);Assert.Equal(1,rev);var current=repo.Anchors().Single(n=>n.Id==a.Id);Assert.Equal(before,current.References.Select(r=>r.Path.ToJsonString()));Assert.Equal(.95,current.Spec["threshold"]!.GetValue<double>());
            repo.RestoreAnchors(rev,0);Assert.Equal(a.Spec["threshold"]?.ToJsonString(),repo.Anchors().Single(n=>n.Id==a.Id).Spec["threshold"]?.ToJsonString());
        }finally{if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
    }
    [Fact] public void IncompatibleLegacyChainDoesNotHideIndependentCatalogOrRewriteSource()
    {
        var repo=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-editor"));
        var invalid=repo.CreateDraft("production:iron_ingot_from_iron_ore");invalid["id"]="legacy_iron";invalid["status"]="published";invalid["runtime_key"]="iron_ingot_from_iron_ore";
        invalid["nodes"]![0]!["anchors"]!.AsArray().Clear();var before=invalid.ToJsonString();
        var valid=repo.CreateDraft("production:wood_first");valid["id"]="valid_wood";valid["status"]="published";
        var config=PublishedCatalogCompiler.CompileAvailable(repo,[invalid,valid]);
        Assert.NotNull(config["recipes"]!["wood_first"]);Assert.NotNull(config["recipes"]!["iron_ingot_from_iron_ore"]);
        Assert.NotNull(config["custom_chain_issues"]!["iron_ingot_from_iron_ore"]);Assert.Equal(before,invalid.ToJsonString());
        Assert.Throws<InvalidDataException>(()=>PublishedCatalogCompiler.Compile(repo,[invalid]));
    }
}



