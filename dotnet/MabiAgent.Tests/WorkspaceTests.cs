using System.Text.Json.Nodes;
using MabiAgent.Core;
using MabiAgent.Desktop;
using Xunit;
namespace MabiAgent.Tests;
public sealed class WorkspaceTests:IDisposable
{
    [Fact] public void DefaultFloorDoesNotAuthorizeUnrelatedMaterialSales()
    {
        var preferences=DesktopPreferences.FromJson(JsonNode.Parse("""{"backpack":{"material_reserve_min":300,"material_reserves":{"985189850":300}}}""")!.AsObject());
        Assert.True(preferences.IsExplicitSurplusMaterial(985189850));Assert.False(preferences.IsExplicitSurplusMaterial(123));
        Assert.False((preferences with{MaterialReserves=new Dictionary<string,long>()}).IsExplicitSurplusMaterial(985189850));
    }
    [Theory][InlineData("采集","轮")][InlineData("生产","件")]
    public void QueueLabelsKeepUnitsWhileEditableValuesStayNumeric(string kind,string unit)
    {var row=new JobRow("job","目标",kind,5,2,"等待",false,1,false,false,false);Assert.Equal("5"+unit,row.TargetLabel);Assert.Equal("2"+unit,row.CompletedLabel);Assert.Equal("3"+unit,row.RemainingLabel);Assert.Equal("5",row.TargetInput);Assert.Equal(unit,row.InputUnit);if(kind=="采集")Assert.Contains("1轮＝10次",row.TargetHint);}
    [Theory][InlineData("采集",37,36,"1轮")][InlineData("生产",13,9,"4件")][InlineData("采集",1,2,"0轮")][InlineData("挂机钓鱼",65,5,"0时 1分 0秒")]
    public void AutomaticSupplyDisplaysRemainingRatherThanLifetimeTarget(string kind,long target,long completed,string expected)
    {var row=new JobRow("auto-material:test","材料",kind,target,completed,"等待",true,1,false,false,false);Assert.Equal(expected,row.RemainingLabel);}
    [Theory][InlineData(0,300)][InlineData(120,300)][InlineData(500,500)]
    public void UnconfiguredMaterialUsesDefaultFloorAndQueueRequirement(long required,long expected)
    {
        var preferences=DesktopPreferences.FromJson(JsonNode.Parse("""{"backpack":{"material_reserve_min":300,"material_reserves":{"99":50}}}""")!.AsObject());
        Assert.Equal(expected,preferences.RequiredMaterialReserve(123,required));Assert.Null(preferences.RequiredMaterialReserve(123,null));
    }
    [Theory][InlineData(0,0,0)][InlineData(50,20,50)][InlineData(50,120,120)][InlineData(500,20,500)]
    public void IndividualMaterialFloorOverridesDefaultButCannotLowerQueueRequirement(long configured,long required,long expected)
    {
        var preferences=DesktopPreferences.FromJson(new JsonObject{["backpack"]=new JsonObject{["material_reserve_min"]=300L,["material_reserves"]=new JsonObject{["123"]=configured}}});
        Assert.Equal(expected,preferences.RequiredMaterialReserve(123,required));Assert.Equal(300,preferences.RequiredMaterialReserve(124,0));
        Assert.Equal(300,(preferences with{MaterialReserves=new Dictionary<string,long>()}).RequiredMaterialReserve(123,required));
    }
    private readonly string root=Path.Combine(Path.GetTempPath(),"mabi-workspace-"+Guid.NewGuid().ToString("N"));
    private string Data=>Path.Combine(root,"selected-data");
    public WorkspaceTests()
    {
        Directory.CreateDirectory(Path.Combine(root,"config"));
        File.WriteAllText(Path.Combine(root,"config/screen_catalog.1280x960.tw.json"),"""
        {"recipes":{"iron":{"name":"铁锭","output_item_id":1156816717,"device":"metal","duration_s":60,"output_per_round":3}},"item_names":{"1156816717":"铁锭"}}
        """);
    }
    public void Dispose()=>Directory.Delete(root,true);
    [Fact] public void LifeVipDefaultsOffAndRoundTripsWithoutChangingRecipeStandardDuration()
    {
        var workspace=new Workspace(root,Data);var original=DesktopPreferences.FromJson(workspace.ReadSettings());Assert.False(original.LifeVip);
        workspace.SavePreferences(original with{LifeVip=true});Assert.True(DesktopPreferences.FromJson(workspace.ReadSettings()).LifeVip);
        var row=new RecipeRow("custom","Custom","wood",25201,1,true);Assert.Equal(12600.5,row.EffectiveDuration);Assert.Equal(25201,row.Duration);
        workspace.SavePreferences(original);Assert.False(DesktopPreferences.FromJson(workspace.ReadSettings()).LifeVip);
    }
    [Fact] public void GlobalInputDefaultsMigrateAndRoundTripWithoutDroppingOtherSettings()
    {
        var w=new Workspace(root,Data);var defaults=DesktopPreferences.FromJson(w.ReadSettings());Assert.Equal("touch",defaults.InputScheme);Assert.True(defaults.TouchCenterOffset);
        Directory.CreateDirectory(Data);var path=Path.Combine(Data,"settings.json");File.WriteAllText(path,"""{"custom":{"keep":7},"repair":{"interval_minutes":37}}""");
        var old=DesktopPreferences.FromJson(w.ReadSettings());Assert.Equal("touch",old.InputScheme);Assert.True(old.TouchCenterOffset);Assert.Equal(37,old.RepairMinutes);
        w.SavePreferences(old with{InputScheme="mouse",TouchCenterOffset=false});var mouse=DesktopPreferences.FromJson(w.ReadSettings());Assert.Equal("mouse",mouse.InputScheme);Assert.False(mouse.TouchCenterOffset);Assert.Equal(7,w.ReadSettings()["custom"]!["keep"]!.GetValue<int>());
        w.SavePreferences(mouse with{InputScheme="touch",TouchCenterOffset=true});var touch=DesktopPreferences.FromJson(w.ReadSettings());Assert.Equal("touch",touch.InputScheme);Assert.True(touch.TouchCenterOffset);Assert.Equal(37,touch.RepairMinutes);
        Assert.Throws<ArgumentException>(()=>w.SavePreferences(touch with{InputScheme="unsupported"}));
    }
    [Fact] public void UserChangesWakeAfterCommitButReadsAndFailedWritesDoNotWake()
    {
        var workspace=new Workspace(root,Data);var changes=0;workspace.Changed+=()=>changes++;
        workspace.ReadSettings();workspace.Read();Assert.Equal(0,changes);
        workspace.SaveRepairPreferences(false,40,40000,true);Assert.Equal(1,changes);
        workspace.AddProduction("iron",3);Assert.Equal(2,changes);
        workspace.ReadSettings();workspace.Read();Assert.Equal(2,changes);
        var other=new Workspace(root,Data);other.Read();other.AddProduction("iron",3);
        Assert.Throws<IOException>(()=>workspace.EditQueue(q=>q.Add("stale","production",1156816717,3,"iron",[])));
        Assert.Equal(2,changes);
    }
    [Fact] public void CancellingAutomaticMaterialPausesOnlyItsConsumers()
    {
        var config=JsonNode.Parse("""{"item_names":{"1":"矿石","2":"铁锭","3":"成品","4":"木材"},"recipes":{"iron":{"output_item_id":2,"device":"metal","duration_s":60,"output_per_round":1,"materials":[{"item_id":1,"quantity":1}]},"final":{"output_item_id":3,"device":"metal","duration_s":60,"output_per_round":1,"materials":[{"item_id":2,"quantity":1}]},"wood":{"output_item_id":4,"device":"wood","duration_s":60,"output_per_round":1,"materials":[]}}}""")!.AsObject();
        var w=new Workspace(root,Data){CatalogProvider=()=>config};w.Read();w.EditQueue(q=>{q.Add("final","production",3,10,"final",[]);q.Add("wood","production",4,10,"wood",[]);q.Add("auto-material:ore","gathering",1,10,"mine",[]);});w.Read();
        w.ControlJob("auto-material:ore","cancel");var rows=w.Read().Jobs;Assert.DoesNotContain(rows,j=>j.Automatic);Assert.True(rows.Single(j=>j.Id=="final").Paused);Assert.False(rows.Single(j=>j.Id=="wood").Paused);
    }
    [Fact] public void FishingPendingIsProgressNotMaterialReconciliation()
    {
        var w=new Workspace(root,Data);w.Read();w.EditQueue(q=>{q.Add("fishing","idle_fishing",1,60,"lake",[]);q.Begin("fishing","batch","character:idle_fishing");});
        var row=w.Read().Jobs.Single();Assert.True(row.Pending);Assert.Equal("钓鱼进行中",row.Status);
        w.EditQueue(q=>q.Control("fishing","pause"));Assert.Equal("已暂停 · 保留钓鱼进度",w.Read().Jobs.Single().Status);
    }
    [Fact] public void MissingOrInvalidKnownQueueIsNeverAcceptedAsEmpty()
    {
        var w=new Workspace(root,Data);w.Read();w.AddProduction("iron",9);var before=w.Read().Jobs.Single();
        var path=Path.Combine(Data,"work-queue.json");var raw=File.ReadAllBytes(path);File.Delete(path);
        Assert.Throws<IOException>(()=>w.Read());File.WriteAllText(path,"{invalid");Assert.ThrowsAny<System.Text.Json.JsonException>(()=>w.Read());
        File.WriteAllBytes(path,raw);Assert.Equal(before,w.Read().Jobs.Single());
    }
    [Fact] public void ReadingDoesNotCreateStateOrChooseReleaseDataImplicitly()
    {
        var workspace=new Workspace(root);
        Assert.Equal(Path.Combine(root,"data/agent-run/desktop").Replace('/',Path.DirectorySeparatorChar),workspace.DataPath);
        Assert.Empty(workspace.Read().Jobs);Assert.True(MaintenancePreferences.FromJson(workspace.ReadSettings()).AutoBuyBottles);
        Assert.False(Directory.Exists(workspace.DataPath));
    }
    [Fact] public void EditingPersistsExactIdsAndIsVisibleAfterReload()
    {
        var w=new Workspace(root,Data);w.Read();w.AddProduction("iron",9);
        var job=w.Read().Jobs.Single();Assert.Equal("铁锭",job.Name);Assert.Equal(9,job.Target);
        w.EditQueue(q=>q.SetTargetQuantity(job.Id,12));w.EditQueue(q=>q.Control(job.Id,"pause"));
        var reloaded=new Workspace(root,Data).Read().Jobs.Single();Assert.Equal(12,reloaded.Target);Assert.Equal("已暂停",reloaded.Status);
        w.EditQueue(q=>q.Control(job.Id,"cancel"));Assert.Empty(new Workspace(root,Data).Read().Jobs);
    }
    [Fact] public void ExternalQueueUpdatePreventsStaleEdit()
    {
        var a=new Workspace(root,Data);a.Read();a.AddProduction("iron",3);var id=a.Read().Jobs[0].Id;
        var b=new Workspace(root,Data);b.Read();b.AddProduction("iron",6);
        Assert.Throws<IOException>(()=>a.EditQueue(q=>q.Control(id,"cancel")));
        Assert.Equal(2,new Workspace(root,Data).Read().Jobs.Length);
    }
    [Fact] public void SettingsShareReserveAndPreserveUnrelatedFields()
    {
        var w=new Workspace(root,Data);var settings=w.ReadSettings();
        settings["backpack"]!["threshold_percent"]=87;settings["extension"]="preserve";
        AtomicJson.Write(Path.Combine(Data,"settings.json"),settings);w.ReadSettings();
        w.SaveRepairPreferences(false,42,54321,false);
        var result=w.ReadSettings();var p=MaintenancePreferences.FromJson(result);
        Assert.Equal(54321,p.GoldReserve);Assert.False(p.AutoBuyBottles);
        Assert.Equal(87,result["backpack"]!["threshold_percent"]!.GetValue<int>());Assert.Equal("preserve",result["extension"]!.GetValue<string>());
        File.WriteAllText(Path.Combine(Data,"settings.json"),settings.ToJsonString());
        Assert.Throws<IOException>(()=>w.SaveRepairPreferences(true,30,40000,true));
        Assert.Empty(Directory.GetFiles(Data,"*.tmp"));Assert.Empty(Directory.GetFiles(Data,"*.lock"));
    }
    [Fact] public void FullMaintenanceEditPreservesOverridesAndUnknownFields()
    {
        var w=new Workspace(root,Data);var original=w.ReadSettings();
        original["backpack"]!["material_reserves"]!["123"]=725;
        original["travel"]!["extension"]="keep";
        AtomicJson.Write(Path.Combine(Data,"settings.json"),original);w.ReadSettings();
        w.SavePreferences(new(true,45,50000,false,true,85,false,400,false,25));
        var saved=new Workspace(root,Data).ReadSettings();
        Assert.Equal(85,saved["backpack"]!["threshold_percent"]!.GetValue<int>());
        Assert.Equal(400,saved["backpack"]!["material_reserve_min"]!.GetValue<long>());
        Assert.Equal(725,saved["backpack"]!["material_reserves"]!["123"]!.GetValue<int>());
        Assert.False(saved["repair"]!["auto_buy_bottles"]!.GetValue<bool>());
        Assert.Equal(25,saved["travel"]!["wing_reserve_min"]!.GetValue<long>());
        Assert.Equal("keep",saved["travel"]!["extension"]!.GetValue<string>());
        Assert.True(saved["backpack"]!["preserve_other_selections"]!.GetValue<bool>());
    }
    [Fact] public void WingReserveSettingsAcceptIntLongAndParsedJson()
    {
        foreach(var node in new JsonNode[]{JsonValue.Create((int)20)!,JsonValue.Create(20L)!,JsonNode.Parse("20")!})
        {
            var settings=JsonNode.Parse("""{"travel":{"auto_use_wings":true}}""")!.AsObject();
            settings["travel"]!["wing_reserve_min"]=node;
            Assert.Equal(20,DesktopPreferences.FromJson(settings).WingReserve);
        }
        var malformed=JsonNode.Parse("""{"travel":{"wing_reserve_min":"20"}}""")!.AsObject();
        Assert.Throws<InvalidDataException>(()=>DesktopPreferences.FromJson(malformed));
    }
    [Fact] public void MaterialOverridesAndThemeEditRoundTripWithoutDroppingExtensions()
    {
        var w=new Workspace(root,Data);var original=w.ReadSettings();original["ui"]!["future"]=true;original["extension"]="keep";AtomicJson.Write(Path.Combine(Data,"settings.json"),original);w.ReadSettings();
        var p=DesktopPreferences.FromJson(original) with {MaterialReserves=new Dictionary<string,long>{{"123",0},{"456",725}},Theme="light"};w.SavePreferences(p);
        var saved=w.ReadSettings();var parsed=DesktopPreferences.FromJson(saved);Assert.Equal(0,parsed.MaterialReserves!["123"]);Assert.Equal(725,parsed.MaterialReserves["456"]);Assert.Equal("light",parsed.Theme);Assert.True(saved["ui"]!["future"]!.GetValue<bool>());Assert.Equal("keep",saved["extension"]!.GetValue<string>());
        w.SavePreferences(parsed with {MaterialReserves=new Dictionary<string,long>()});Assert.Empty(DesktopPreferences.FromJson(w.ReadSettings()).MaterialReserves!);
    }
    [Fact] public void LowCpuRecognitionCanBeToggledWithoutDroppingRecognitionExtensions()
    {
        var workspace=new Workspace(root,Data);var original=workspace.ReadSettings();
        Assert.False(DesktopPreferences.FromJson(original).RecognitionLowCpu);
        original["recognition"]=new JsonObject{["future"]="keep"};
        AtomicJson.Write(Path.Combine(Data,"settings.json"),original);workspace.ReadSettings();
        workspace.SavePreferences(DesktopPreferences.FromJson(original) with{RecognitionLowCpu=true});
        var saved=workspace.ReadSettings();Assert.True(DesktopPreferences.FromJson(saved).RecognitionLowCpu);
        Assert.Equal("keep",saved["recognition"]!["future"]!.GetValue<string>());
        workspace.SavePreferences(DesktopPreferences.FromJson(saved) with{RecognitionLowCpu=false});
        Assert.False(DesktopPreferences.FromJson(workspace.ReadSettings()).RecognitionLowCpu);
    }
    [Fact] public void FishingDurationAndDependencyLabelsUseQueueValues()
    {
        var w=new Workspace(root,Data);w.Read();w.EditQueue(q=>{q.Add("fish","idle_fishing",1156816717,3665,"point_fishing_test",[]);q.Add("craft","production",1156816717,3,"iron",["fish"]);});
        var jobs=w.Read().Jobs;Assert.Equal("1时 1分 5秒",jobs[0].TargetLabel);Assert.Equal("铁锭",jobs[1].Dependencies);Assert.Equal("等待前置任务",jobs[1].Status);
    }    [Theory]
    [InlineData("")] [InlineData("1.5")] [InlineData("-1")] [InlineData("9007199254740992")] [InlineData("abc")]
    public void InvalidNumericInputNeverFallsBackToPreviousValue(string text)
    {
        Assert.False(FormNumbers.TryWhole(text,1,9007199254740991,out _));
    }
    [Fact] public void InvalidPreferencesDoNotCreateOrModifyFiles()
    {
        var w=new Workspace(root,Data);w.ReadSettings();
        Assert.Throws<ArgumentException>(()=>w.SavePreferences(new(true,30,40000,true,true,101,true,300,true,0)));
        Assert.False(Directory.Exists(Data));
        w.SavePreferences(new(true,30,40000,true,true,90,true,300,true,0));
        var path=Path.Combine(Data,"settings.json");var before=File.ReadAllBytes(path);
        Assert.Throws<ArgumentException>(()=>w.SavePreferences(new(true,30,40000,true,true,90,true,-1,true,0)));
        Assert.Equal(before,File.ReadAllBytes(path));
    }
}

