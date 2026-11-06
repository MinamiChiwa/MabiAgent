using System.Text.Json.Nodes;
using MabiAgent.Desktop.Editors;
using Xunit;

public sealed class WorkbenchTests
{
    static string Root=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
    [Fact] public void InstalledRoutesImportWithoutWritingUserDataAndKeepConsumptionSemantic()
    {
        var temp=Path.Combine(Path.GetTempPath(),"MabiAgent-workbench-"+Guid.NewGuid().ToString("N"));
        var editor=new EditorRepository(Root,temp);var store=new WorkbenchRepository(editor);var document=store.Load();
        WorkbenchValidation.Validate(document);
        Assert.False(File.Exists(store.FilePath));Assert.NotEmpty(document.Pages);Assert.NotEmpty(document.Anchors);Assert.NotEmpty(document.Strategies);
        Assert.All(document.Chains,c=>Assert.False(c.Enabled));
        Assert.All(document.Chains.Where(c=>c.Kind=="gathering"),c=>Assert.Contains(c.Cards,n=>n.Id=="start"&&n.Action=="semantic"&&n.ConsumesResources));
        Assert.Contains(document.Chains,c=>c.RuntimeKey=="iron_ingot_from_iron_ore");
    }
    [Fact] public void InstalledImportCanSaveAndCompileWithoutRemovingTargetsOrChangingCounters()
    {
        var temp=Path.Combine(Path.GetTempPath(),"MabiAgent-workbench-"+Guid.NewGuid().ToString("N"));
        try
        {
            var editor=new EditorRepository(Root,temp);var before=PublishedCatalogCompiler.Compile(editor);var store=new WorkbenchRepository(editor);var imported=store.Load();store.Save(imported);var after=PublishedCatalogCompiler.Compile(editor);
            Assert.Empty(editor.List());Assert.Empty(after["workbench_chains"]!.AsObject());
            Assert.True(JsonNode.DeepEquals(before["recipes"],after["recipes"]));Assert.True(JsonNode.DeepEquals(before["quantity"],after["quantity"]));
            foreach(var(key,value)in before["ui"]!["pages"]!.AsObject())
            {foreach(var(target,anchor)in value?["targets"]?.AsObject()??[])Assert.True(JsonNode.DeepEquals(anchor,after["ui"]!["pages"]![key]!["targets"]?[target]),key+"/"+target);}
            Assert.Contains(imported.Chains,c=>c.Data["staged_route"]?.GetValue<bool>()==true);
        }
        finally{if(Directory.Exists(temp))Directory.Delete(temp,true);}
    }
    [Fact] public void MultiMovePreservesRelativeOrderAndDeletionRemovesDanglingBranches()
    {
        var chain=new WorkbenchChain(new JsonObject{["id"]="chain",["name"]="测试"});
        foreach(var id in new[]{"a","b","c","d","e"})chain.Cards.Add(new(new JsonObject{["id"]=id,["name"]=id}));
        chain.Cards[0].SuccessId="c";chain.MoveCards(["b","d"],5);
        Assert.Equal(new[]{"a","c","e","b","d"},chain.Cards.Select(c=>c.Id));
        chain.RemoveCards(["c","e"]);Assert.Equal("",chain.Cards[0].SuccessId);Assert.Equal(new[]{"a","b","d"},chain.Cards.Select(c=>c.Id));
        var copy=chain.AddCard(chain.Cards[0]);copy.Name="独立副本";Assert.NotEqual(chain.Cards[0].Id,copy.Id);Assert.Equal("a",chain.Cards[0].Name);
    }
    [Fact] public void SharedCoordinateAnchorCompilesShapeAndRandomReferenceToEveryCard()
    {
        var editor=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"));var document=SmallDocument();
        var chain=document.Chains.Single();chain.Enabled=true;
        var second=chain.AddCard(chain.Cards[0]);second.PageId="skills";document.Pages.Add(new(new JsonObject{["id"]="skills",["name"]="技能",["anchor_ids"]="scene_identity"}));
        var config=PublishedCatalogCompiler.Compile(editor);WorkbenchCompiler.Apply(editor,document,config,allowStandaloneTests:true);
        foreach(var card in config["workbench_chains"]!["test"]!["cards"]!.AsArray())
        {var area=config["ui"]!["pages"]![card!["page_id"]!.GetValue<string>()]!["coordinate_targets"]![card["target"]!.GetValue<string>()]!;Assert.Equal("ellipse",area["shape"]!.GetValue<string>());Assert.True(area["random"]!.GetValue<bool>());Assert.Equal(40,area["box"]![2]!.GetValue<int>());}
        document.Anchors.Single(a=>a.Id=="shared").States.Single().Box="20,20,60,60";
        WorkbenchCompiler.Apply(editor,document,config,allowStandaloneTests:true);var target=config["workbench_chains"]!["test"]!["cards"]![0]!["target"]!.GetValue<string>();Assert.Equal(60,config["ui"]!["pages"]!["scene"]!["coordinate_targets"]![target]!["box"]![2]!.GetValue<int>());
        Assert.Equal(2,config["workbench_chains"]!["test"]!["cards"]!.AsArray().Count);
    }
    [Fact] public void CardsSharingPageAndTargetKeepIndependentStatesAndRandomPolicies()
    {
        var editor=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"));var document=SmallDocument();var chain=document.Chains.Single();chain.Enabled=true;
        var anchor=document.Anchors.Single(a=>a.Id=="shared");anchor.States[0].RandomClick=true;
        anchor.States.Add(new(new JsonObject{["id"]="other",["name"]="另一区域",["mode"]="coordinate",["shape"]="rectangle",["box"]=new JsonArray(50,50,90,80)}));
        var second=chain.AddCard(chain.Cards[0]);second.StateId="other";second.RandomClick=false;
        var config=PublishedCatalogCompiler.Compile(editor);WorkbenchCompiler.Apply(editor,document,config,allowStandaloneTests:true);
        var compiled=config["workbench_chains"]!["test"]!["cards"]!.AsArray();var firstTarget=compiled[0]!["target"]!.GetValue<string>();var secondTarget=compiled[1]!["target"]!.GetValue<string>();Assert.NotEqual(firstTarget,secondTarget);
        var targets=config["ui"]!["pages"]!["scene"]!["coordinate_targets"]!;
        Assert.True(targets[firstTarget]!["random"]!.GetValue<bool>());Assert.Equal("ellipse",targets[firstTarget]!["shape"]!.GetValue<string>());Assert.Equal(10,targets[firstTarget]!["box"]![0]!.GetValue<int>());
        Assert.False(targets[secondTarget]!["random"]!.GetValue<bool>());Assert.Equal("rectangle",targets[secondTarget]!["shape"]!.GetValue<string>());Assert.Equal(50,targets[secondTarget]!["box"]![0]!.GetValue<int>());
        chain.MoveCards([second.Id],0);WorkbenchCompiler.Apply(editor,document,config,allowStandaloneTests:true);
        targets=config["ui"]!["pages"]!["scene"]!["coordinate_targets"]!;
        Assert.True(targets[firstTarget]!["random"]!.GetValue<bool>());Assert.False(targets[secondTarget]!["random"]!.GetValue<bool>());
    }
    [Fact] public void ImageCardsKeepIndependentRandomPoliciesAcrossPageVariantsAndTransitions()
    {
        var editor=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"));var document=SmallDocument();var chain=document.Chains.Single();chain.Enabled=true;
        var anchor=document.Anchors.Single(a=>a.Id=="shared");var state=anchor.States[0];state.Mode="image";state.Path="sample.png";state.Search="0,0,1280,960";state.Threshold="0.8";state.RandomClick=true;
        var identity=new WorkbenchAnchor(new JsonObject{["id"]="identity",["name"]="备用页面身份"});identity.States.Add(new(state.ToJson()));document.Anchors.Add(identity);
        var page=document.Pages.Single();page.AnchorIds="shared, identity";page.MatchRule="any";page.Transitions.Add(new(new JsonObject{["id"]="link",["name"]="页面关系",["target"]="fixed",["anchor_id"]="shared",["destination_page_id"]="skills"}));document.Pages.Add(new(new JsonObject{["id"]="skills",["name"]="技能",["anchor_ids"]="scene_identity"}));
        var second=chain.AddCard(chain.Cards[0]);second.RandomClick=false;
        var config=PublishedCatalogCompiler.Compile(editor);WorkbenchCompiler.Apply(editor,document,config,allowStandaloneTests:true);
        var compiled=config["workbench_chains"]!["test"]!["cards"]!.AsArray();var firstTarget=compiled[0]!["target"]!.GetValue<string>();var secondTarget=compiled[1]!["target"]!.GetValue<string>();Assert.Equal("skills",compiled[0]!["expected_page_id"]!.GetValue<string>());
        var variants=config["ui"]!["page_variants"]!.AsObject().Select(p=>p.Value).OfType<JsonObject>().Where(v=>v["page"]?.GetValue<string>()=="scene").ToArray();Assert.NotEmpty(variants);
        foreach(var runtimePage in variants.Append(config["ui"]!["pages"]!["scene"]!.AsObject()))
        {Assert.NotNull(runtimePage["targets"]?[firstTarget]);Assert.NotNull(runtimePage["targets"]?[secondTarget]);Assert.NotNull(runtimePage["random_targets"]?[firstTarget]);Assert.Null(runtimePage["random_targets"]?[secondTarget]);}
    }
    [Fact] public void CardRandomPolicyInheritsSharedDefaultUntilExplicitlyOverridden()
    {
        var editor=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"));var document=SmallDocument();var chain=document.Chains.Single();chain.Enabled=true;var card=chain.Cards[0];var state=document.Anchors[0].States[0];state.RandomClick=true;card.RandomMode="inherit";
        Assert.False(card.Data.ContainsKey("random_click"));var config=PublishedCatalogCompiler.Compile(editor);
        bool CompiledRandom(){WorkbenchCompiler.Apply(editor,document,config,allowStandaloneTests:true);var target=config["workbench_chains"]!["test"]!["cards"]![0]!["target"]!.GetValue<string>();return config["ui"]!["pages"]!["scene"]!["coordinate_targets"]![target]!["random"]!.GetValue<bool>();}
        Assert.True(CompiledRandom());card.RandomMode="off";Assert.False(CompiledRandom());state.RandomClick=false;card.RandomMode="on";Assert.True(CompiledRandom());card.RandomMode="inherit";Assert.False(CompiledRandom());
    }
    [Fact] public void LinkedTransitionFollowsSharedAnchorActionAndDestinationEdits()
    {
        var editor=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"));var document=SmallDocument();var chain=document.Chains.Single();chain.Enabled=true;var card=chain.Cards[0];card.TransitionId="link";
        document.Pages.Add(new(new JsonObject{["id"]="skills",["name"]="技能",["anchor_ids"]="scene_identity"}));
        var link=new WorkbenchTransition(new JsonObject{["id"]="link",["name"]="打开技能",["target"]="workbench.transition.link",["anchor_id"]="shared",["destination_page_id"]="skills",["action"]="right"});document.Pages[0].Transitions.Add(link);
        var other=new WorkbenchAnchor(new JsonObject{["id"]="other",["name"]="另一个点击区域"});other.States.Add(new(new JsonObject{["id"]="default",["name"]="坐标",["mode"]="coordinate",["box"]=new JsonArray(60,60,90,90)}));document.Anchors.Add(other);
        var config=PublishedCatalogCompiler.Compile(editor);WorkbenchCompiler.Apply(editor,document,config,allowStandaloneTests:true);
        Assert.Equal("right",config["workbench_chains"]!["test"]!["cards"]![0]!["action"]!.GetValue<string>());Assert.Equal("skills",config["workbench_chains"]!["test"]!["cards"]![0]!["expected_page_id"]!.GetValue<string>());
        link.AnchorId="other";WorkbenchCompiler.Apply(editor,document,config,allowStandaloneTests:true);var target=config["workbench_chains"]!["test"]!["cards"]![0]!["target"]!.GetValue<string>();Assert.Equal(60,config["ui"]!["pages"]!["scene"]!["coordinate_targets"]![target]!["box"]![0]!.GetValue<int>());
        link.DestinationPageId="";Assert.Throws<InvalidDataException>(()=>WorkbenchValidation.Validate(document));link.DestinationPageId="skills";card.TransitionId="missing";Assert.Throws<InvalidDataException>(()=>WorkbenchValidation.Validate(document));
    }
    [Fact] public void CoordinateStrategiesKeepNestedRectsAndSignedOffsetsTogether()
    {
        var editor=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"));
        editor.Catalog["maintenance"]!["workbench_coordinate_fixture"]=new JsonObject{["named_rectangles"]=new JsonObject{["custom"]=new JsonArray(20,30,60,70)},["slot_boxes"]=new JsonArray(new JsonArray(10,20,40,50)),["offset"]=new JsonArray(2,-4,112,21),["grid"]=new JsonObject{["step"]=new JsonArray(103,124),["origin"]=new JsonArray(720,185),["icon"]=new JsonArray(4,0,74,55)},["thresholds"]=new JsonArray(150,180),["client_size"]=new JsonArray(1280,960)};
        var document=new WorkbenchRepository(editor).Import();WorkbenchValidation.Validate(document);
        WorkbenchStrategy Find(string suffix)=>document.Strategies.Single(s=>s.Description.EndsWith(suffix,StringComparison.Ordinal));
        Assert.Equal("region",Find("workbench_coordinate_fixture / named_rectangles / custom").ValueKind);Assert.Equal("region",Find("workbench_coordinate_fixture / slot_boxes / 0").ValueKind);
        Assert.Equal("relativeregion",Find("workbench_coordinate_fixture / offset").ValueKind);Assert.Equal("relativepoint",Find("workbench_coordinate_fixture / grid / step").ValueKind);Assert.Equal("point",Find("workbench_coordinate_fixture / grid / origin").ValueKind);Assert.Equal("relativeregion",Find("workbench_coordinate_fixture / grid / icon").ValueKind);
        Assert.Contains(document.Strategies,s=>s.Description.Contains("equipped_tool_slots / 0")&&s.ValueKind=="region");Assert.Contains(document.Strategies,s=>s.Description.Contains("slot_boxes / 0")&&s.Category=="devices"&&s.ValueKind=="region");
        Assert.DoesNotContain(document.Strategies,s=>s.IsCoordinate&&(s.Description.Contains("thresholds")||s.Description.Contains("client_size")));
        var relative=Find("workbench_coordinate_fixture / offset");relative.Value="-20, -30, 50, 60";WorkbenchValidation.Validate(document);Assert.True(relative.IsRelative);relative.Value="5, 0, -1, 8";Assert.Throws<InvalidDataException>(()=>WorkbenchValidation.Validate(document));
    }
    [Fact] public void SaveRejectsEnabledChainsWithNoRuntimeBindingBeforeWriting()
    {
        var temp=Path.Combine(Path.GetTempPath(),"MabiAgent-workbench-"+Guid.NewGuid().ToString("N"));
        try
        {
            var editor=new EditorRepository(Root,temp);var store=new WorkbenchRepository(editor);var document=SmallDocument();document.Chains[0].Enabled=true;
            Assert.Throws<InvalidDataException>(()=>store.Save(document));Assert.False(File.Exists(store.FilePath));Assert.Equal(0,document.Revision);
            document.Chains[0].Enabled=false;store.Save(document);Assert.True(File.Exists(store.FilePath));Assert.Equal(1,document.Revision);
        }
        finally{if(Directory.Exists(temp))Directory.Delete(temp,true);}
    }
    [Theory]
    [InlineData("repair.confirm","")]
    [InlineData("custom","maint_repair_confirm")]
    [InlineData("custom","craft_processing")]
    public void KnownConsumptionTargetsCannotBecomeOrdinaryClicks(string target,string template)
    {
        var document=SmallDocument();var card=document.Chains[0].Cards[0];card.Target=target;document.Anchors[0].States[0].TemplateKey=template;
        Assert.Throws<InvalidDataException>(()=>WorkbenchValidation.Validate(document));card.Action="semantic";card.ConsumesResources=true;document.Chains[0].Kind="production";card.Role="production";WorkbenchValidation.Validate(document);
    }
    [Theory]
    [InlineData(false,"wait")]
    [InlineData(false,"delay")]
    [InlineData(true,"wait")]
    [InlineData(true,"semantic")]
    public void ImportedNonStagedRoutesRejectActionsTheirNativeExecutorCannotRun(bool single,string action)
    {
        var editor=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"));var document=SmallDocument();var chain=document.Chains[0];chain.Enabled=true;chain.Data["source_path"]=new JsonArray("maintenance","navigation","fixture");chain.Data["single_route"]=single;chain.Cards[0].Action=action;
        if(action=="semantic"){chain.Kind="production";chain.Cards[0].Role="production";}
        var configuration=PublishedCatalogCompiler.Compile(editor,includeWorkbench:false);configuration["maintenance"]!["navigation"]!["fixture"]=new JsonObject{["target"]="fixed",["steps"]=new JsonArray()};configuration["ui"]!["pages"]!["scene"]!["targets"]!["fixed"]="scene_bag";
        var error=Assert.Throws<InvalidDataException>(()=>WorkbenchCompiler.Apply(editor,document,configuration));Assert.Contains("原生导航路线仅支持",error.Message);
    }
    [Fact] public void MapNavigationImportsConcreteDestinationsAndKeepsEditsAfterRuntimeLoading()
    {
        var editor=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"));var document=new WorkbenchRepository(editor).Import();
        Assert.DoesNotContain(document.Chains,c=>c.Data["source_path"] is JsonArray path&&path.Last()?.ToString()=="map_navigation");
        foreach(var destination in new[]{"repair_quote","purchase_shop","storage"})
        {
            var chain=Assert.Single(document.Chains,c=>c.Data["source_path"] is JsonArray path&&string.Join("/",path.Select(n=>n!.ToString()))=="maintenance/navigation/"+destination+"/region_map");
            Assert.DoesNotContain(chain.Cards,c=>c.ToJson().ToJsonString().Contains("$shop",StringComparison.Ordinal)||c.ToJson().ToJsonString().Contains("$npc",StringComparison.Ordinal)||c.ToJson().ToJsonString().Contains("$travel",StringComparison.Ordinal));
            chain.Enabled=true;var first=chain.Cards.First(c=>c.Action=="zoom_max");first.Action="scroll";first.ScrollSteps=2;
        }
        var configuration=PublishedCatalogCompiler.Compile(editor,includeWorkbench:false);
        var regionTargets=configuration["ui"]!["pages"]!["region_map"]!["targets"]!.AsObject();regionTargets.Remove("map.storage_bank");regionTargets.Remove("map.storage_npc");regionTargets["map.inn"]="maint_tir_town";regionTargets["map.storage_box"]="storage_bank_npc";
        WorkbenchCompiler.Apply(editor,document,configuration);
        foreach(var destination in new[]{"repair_quote","purchase_shop","storage"})Assert.True(configuration["maintenance"]!["navigation"]![destination]!["region_map"]!["workbench_override"]!.GetValue<bool>());
        var storageRoute=configuration["maintenance"]!["navigation"]!["storage"]!["region_map"]!;
        Assert.Contains("map.inn",storageRoute.ToJsonString());Assert.Contains("map.storage_box",storageRoute.ToJsonString());Assert.Contains("storage_travel",storageRoute.ToJsonString());
        Assert.DoesNotContain("map.storage_bank",storageRoute.ToJsonString());
        Assert.NotNull(configuration["ui"]!["pages"]!["storage_travel"]);Assert.NotNull(configuration["ui"]!["pages"]!["storage_dialog"]);
    }
    [Fact] public void InstalledPageCannotSilentlyKeepItsOldIdentityAfterAllAnchorsAreRemoved()
    {
        var editor=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"));var document=new WorkbenchRepository(editor).Import();var page=document.Pages.First(p=>p.Data["source"]?["anchors"] is JsonArray {Count:>0});page.AnchorIds="";
        var error=Assert.Throws<InvalidDataException>(()=>WorkbenchValidation.Validate(document));Assert.Contains("保留至少一个图片身份锚点",error.Message);
    }
    [Fact] public void NativeRouteStepTimeoutUsesTheDisplayedSettingAndRejectsIgnoredFlowLogic()
    {
        var editor=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"));var document=SmallDocument();var chain=document.Chains[0];chain.Enabled=true;chain.Data["source_path"]=new JsonArray("maintenance","navigation","fixture");chain.Data["single_route"]=true;
        var card=chain.Cards[0];card.Data["source"]=new JsonObject{["timeout_s"]=42.5};Assert.Equal(43,card.TimeoutSeconds);card.TimeoutSeconds=27;
        var configuration=PublishedCatalogCompiler.Compile(editor,includeWorkbench:false);configuration["maintenance"]!["navigation"]!["fixture"]=new JsonObject{["target"]="fixed"};configuration["ui"]!["pages"]!["scene"]!["targets"]!["fixed"]="scene_bag";WorkbenchCompiler.Apply(editor,document,configuration);Assert.Equal(27,configuration["maintenance"]!["navigation"]!["fixture"]!["timeout_s"]!.GetValue<int>());
        card.RetryLimit=1;Assert.Contains("原生导航使用",Assert.Throws<InvalidDataException>(()=>WorkbenchCompiler.Apply(editor,document,configuration)).Message);
    }
    [Theory]
    [InlineData("production","production",true)]
    [InlineData("production","result",true)]
    [InlineData("gathering","gathering_start",true)]
    [InlineData("gathering","observation",true)]
    [InlineData("production","navigation",false)]
    [InlineData("gathering","result",false)]
    [InlineData("navigation","production",false)]
    public void SemanticRolesMustBeSupportedByTheNormalTaskRunner(string kind,string role,bool valid)
    {
        var document=SmallDocument();var chain=document.Chains[0];chain.Kind=kind;var card=chain.Cards[0];card.Action="semantic";card.Role=role;Assert.True(card.IsSemantic);
        if(valid)WorkbenchValidation.Validate(document);else Assert.Contains("当前任务支持",Assert.Throws<InvalidDataException>(()=>WorkbenchValidation.Validate(document)).Message);
    }
    [Fact] public void SaveUsesRevisionLockAndPreservesUnknownFieldsWithoutTouchingOldChains()
    {
        var temp=Path.Combine(Path.GetTempPath(),"MabiAgent-workbench-"+Guid.NewGuid().ToString("N"));
        try
        {
            var editor=new EditorRepository(Root,temp);var store=new WorkbenchRepository(editor);var document=SmallDocument();document.Data["future"]=new JsonObject{["version"]=8};
            var changes=0;editor.CatalogChanged+=()=>changes++;var saved=store.Save(document);var stale=new WorkbenchDocument(saved.ToJson());
            saved.Chains.Single().Name="更名";store.Save(saved);Assert.Throws<IOException>(()=>store.Save(stale));
            Assert.Equal(2,changes);Assert.Equal(8,store.Load().Data["future"]!["version"]!.GetValue<int>());Assert.Empty(editor.List());
            Assert.Equal(2,store.Load().Revision);
        }
        finally{if(Directory.Exists(temp))Directory.Delete(temp,true);}
    }
    [Fact] public void RetiredLibraryAnchorMissingAssetDoesNotBlockSaveAndRemainsInDocument()
    {
        var temp=Path.Combine(Path.GetTempPath(),"MabiAgent-workbench-"+Guid.NewGuid().ToString("N"));
        try
        {
            var editor=new EditorRepository(Root,temp);var document=SmallDocument();var state=(JsonObject)PublishedCatalogCompiler.Compile(editor,includeWorkbench:false)["ui"]!["templates"]!["scene_menu"]!.DeepClone();
            state["id"]="default";state["name"]="退役图片";state["mode"]="image";state["template_key"]="retired_public_template";state["path"]="assets/templates/retired-public-missing.png";state["sha256"]=new string('0',64);
            var retired=new WorkbenchAnchor(new JsonObject{["id"]="retired_library_anchor",["name"]="退役公共锚点",["in_library"]=true,["source_namespace"]="ui_profile",["source_path"]=new JsonArray("ui_profile","templates","retired_public_template")});retired.States.Add(new(state));document.Anchors.Add(retired);
            var store=new WorkbenchRepository(editor);store.Save(document);Assert.NotNull(store.Load()!.FindAnchor("retired_library_anchor"));
        }
        finally{if(Directory.Exists(temp))Directory.Delete(temp,true);}
    }
    [Fact] public void MissingAssetsForCustomOrCurrentImageAnchorsAreStillRejected()
    {
        var temp=Path.Combine(Path.GetTempPath(),"MabiAgent-workbench-"+Guid.NewGuid().ToString("N"));
        try
        {
            var editor=new EditorRepository(Root,temp);var configuration=PublishedCatalogCompiler.Compile(editor,includeWorkbench:false);var store=new WorkbenchRepository(editor);
            var customDocument=SmallDocument();var state=(JsonObject)configuration["ui"]!["templates"]!["scene_skills"]!.DeepClone();state["id"]="default";state["name"]="用户图片";state["mode"]="image";state["template_key"]="user_missing_template";state["path"]="assets/templates/user-missing.png";state["sha256"]=new string('0',64);
            var custom=new WorkbenchAnchor(new JsonObject{["id"]="user_missing_anchor",["name"]="用户缺失图片"});custom.States.Add(new(state));customDocument.Anchors.Add(custom);
            var customError=Assert.Throws<InvalidDataException>(()=>store.Save(customDocument));Assert.Contains("图片不存在",customError.Message);
            var currentDocument=SmallDocument();currentDocument.FindAnchor("scene_identity")!.States[0].Path="assets/templates/current-public-missing.png";
            var currentError=Assert.Throws<InvalidDataException>(()=>store.Save(currentDocument));Assert.Contains("图片不存在",currentError.Message);
        }
        finally{if(Directory.Exists(temp))Directory.Delete(temp,true);}
    }
    [Fact] public void InvalidReferencesAndConsumptionBypassAreRejectedBeforeSave()
    {
        var document=SmallDocument();var card=document.Chains[0].Cards[0];card.SuccessId="missing";
        Assert.Throws<InvalidDataException>(()=>WorkbenchValidation.Validate(document));card.SuccessId="";card.ConsumesResources=true;
        Assert.Throws<InvalidDataException>(()=>WorkbenchValidation.Validate(document));card.ConsumesResources=false;
        document.Anchors[0].States[0].Shape="circle";document.Anchors[0].States[0].Box="10,10,40,50";
        Assert.Throws<InvalidDataException>(()=>WorkbenchValidation.Validate(document));
    }
    [Fact] public void BooleanStrategyTogglePreservesItsType()
    {
        var editor=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"));var document=SmallDocument();
        var strategy=new WorkbenchStrategy(new JsonObject{["id"]="s",["name"]="策略",["path"]=new JsonArray("enabled"),["original"]=true,["value"]=true});document.Strategies.Add(strategy);strategy.Value="false";
        var config=PublishedCatalogCompiler.Compile(editor);config["enabled"]=true;WorkbenchCompiler.Apply(editor,document,config);Assert.False(config["enabled"]!.GetValue<bool>());
        strategy.Value="非法";Assert.Throws<InvalidDataException>(()=>WorkbenchValidation.Validate(document));
    }
    [Fact] public void ExternalAnchorsSurviveRuntimeProfileLoadingAndImportsHaveStableIds()
    {
        var editor=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"));var store=new WorkbenchRepository(editor);var document=store.Import();
        Assert.Equal(document.ToJson().ToJsonString(),store.Import().ToJson().ToJsonString());
        var anchor=document.Anchors.Single(a=>a.Id=="ui_profile:purchase.map_store_label");anchor.States[0].Threshold="0.91";
        var config=PublishedCatalogCompiler.Compile(editor);WorkbenchCompiler.Apply(editor,document,config);
        Assert.Equal(.91,config["ui"]!["templates"]!["purchase.map_store_label"]!["threshold"]!.GetValue<double>());
        Assert.Equal(.91,config["purchase"]!["templates"]!["map_store_label"]!["threshold"]!.GetValue<double>());
        Assert.Contains(document.Strategies,s=>s.ValueKind=="region"&&s.Data["value"] is JsonArray);
        Assert.DoesNotContain(document.Strategies,s=>s.Data["path"] is JsonArray p&&p.Count>1&&p[^2]?.ToString()=="region"&&p[^1] is JsonValue v&&v.TryGetValue<int>(out _));
    }
    [Fact] public void RemovingWorkbenchStateRemovesOnlyItsVariantAndPreservesInstalledAlternatives()
    {
        var editor=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"));var document=SmallDocument();var anchor=document.FindAnchor("scene_identity")!;var added=new WorkbenchAnchorState(anchor.States[0].ToJson()){Id="custom",TemplateKey=""};anchor.States.Add(added);
        var configuration=PublishedCatalogCompiler.Compile(editor,includeWorkbench:false);var primary=anchor.States[0].TemplateKey;configuration["ui"]!["anchor_variants"]![primary]=new JsonArray("installed_alternative");
        WorkbenchCompiler.Apply(editor,document,configuration);var ownKey=WorkbenchRepository.RuntimeAnchorName(anchor.Id,added.Id);Assert.Contains(ownKey,configuration["ui"]!["anchor_variants"]![primary]!.AsArray().Select(n=>n!.GetValue<string>()));
        anchor.States.Remove(added);WorkbenchCompiler.Apply(editor,document,configuration);Assert.Equal(new[]{"installed_alternative"},configuration["ui"]!["anchor_variants"]![primary]!.AsArray().Select(n=>n!.GetValue<string>()));
    }
    [Fact] public void StalePublicSourcesCannotResurrectReplacedFlowsAndUserChainSurvives()
    {
        var editor=new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"));var configuration=PublishedCatalogCompiler.Compile(editor,includeWorkbench:false);var profile=configuration["ui"]!.AsObject();var document=SmallDocument();var custom=document.Chains.Single();custom.Enabled=true;
        var imported=new WorkbenchRepository(editor).Import();var sourceAnchor=imported.Anchors.Single(a=>a.States[0].TemplateKey=="scene_menu");var staleAnchor=new WorkbenchAnchor(sourceAnchor.ToJson()){Id="stale_public_anchor"};document.Anchors.Add(staleAnchor);
        profile["templates"]!.AsObject().Remove("scene_menu");
        var stalePage=new WorkbenchPage(new JsonObject{["id"]="retired_public_page",["name"]="已移除公共页",["in_library"]=true,["source_section"]="pages",["source_key"]="retired_public_page",["source_path"]=new JsonArray("pages","retired_public_page"),["source"]=new JsonObject{["targets"]=new JsonObject{["retired.target"]="scene_menu"}}});document.Pages.Add(stalePage);
        var staleChain=new WorkbenchChain(new JsonObject{["id"]="stale_chain",["name"]="已替换公共链",["runtime_key"]="stale_public_flow",["kind"]="navigation",["enabled"]=true,["source_path"]=new JsonArray("maintenance","navigation","backpack_tidy","scene")});staleChain.Cards.Add(new(new JsonObject{["id"]="stale_step",["name"]="旧入口",["page_id"]="scene",["target"]="scene.open_storage",["action"]="click",["role"]="navigation",["enabled"]=true}));document.Chains.Add(staleChain);
        document.Strategies.Add(new(new JsonObject{["id"]="stale_strategy",["name"]="旧参数",["path"]=new JsonArray("maintenance","navigation","removed_route","timeout_s"),["original"]=15,["value"]=30}));
        profile["storage_supply"]!.AsObject()["shortcut"]=new JsonObject{["key"]="x"};((JsonObject)profile["pages"]!["scene"]!)["surfaces"]!.AsObject()["scene.open_storage"]=new JsonArray(1,1,2,2);
        WorkbenchCompiler.Apply(editor,document,configuration,allowStandaloneTests:true);
        Assert.Null(profile["storage_supply"]?["shortcut"]);Assert.Null(profile["pages"]!["scene"]!["surfaces"]?["scene.open_storage"]);Assert.Null(profile["pages"]!["retired_public_page"]);Assert.Null(profile["templates"]!["scene_menu"]);Assert.Null(configuration["workbench_chains"]!["stale_public_flow"]);Assert.NotNull(configuration["workbench_chains"]!["test"]);Assert.Null(configuration["maintenance"]!["navigation"]!["backpack_tidy"]!["scene"]!["workbench_override"]);
        Assert.NotNull(profile["pages"]!["scene"]!["coordinate_targets"]!.AsObject().FirstOrDefault(p=>p.Key.StartsWith("workbench.target.",StringComparison.Ordinal)).Value);
    }
    static WorkbenchDocument SmallDocument()
    {
        var d=new WorkbenchDocument(new JsonObject());d.Pages.Add(new(new JsonObject{["id"]="scene",["name"]="场景",["anchor_ids"]="scene_identity"}));
        var anchor=new WorkbenchAnchor(new JsonObject{["id"]="shared",["name"]="共用按钮"});anchor.States.Add(new(new JsonObject{["id"]="default",["name"]="位置",["mode"]="coordinate",["shape"]="ellipse",["box"]=new JsonArray(10,10,40,40)}));d.Anchors.Add(anchor);
        var installed=new WorkbenchRepository(new EditorRepository(Root,Path.Combine(Path.GetTempPath(),"unused-workbench"))).Import().Anchors.First(a=>a.States.Any(state=>state.TemplateKey=="scene_bag"));var identity=new WorkbenchAnchor(installed.ToJson()){Id="scene_identity"};d.Anchors.Add(identity);
        var chain=new WorkbenchChain(new JsonObject{["id"]="chain",["name"]="测试链",["runtime_key"]="test"});chain.Cards.Add(new(new JsonObject{["id"]="a",["name"]="点击",["page_id"]="scene",["anchor_id"]="shared",["state_id"]="default",["action"]="click",["target"]="fixed",["random_click"]=true}));d.Chains.Add(chain);return d;
    }
}
