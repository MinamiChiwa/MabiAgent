using System.Text.Json.Nodes;
using MabiAgent.Desktop.Editors;
using Xunit;

public sealed class WorkbenchPresentationTests
{
    [Fact] public void TranslatedNameRoundTripPreservesConfigurationUntilUserChangesText()
    {
        var model=new WorkbenchAnchor(new JsonObject{["id"]="ui_profile:bag_open",["name"]="bag_open"});
        var before=model.ToJson().ToJsonString();
        Assert.Equal("背包 打开",model.DisplayName);
        model.DisplayName=model.DisplayName;
        Assert.Equal(before,model.ToJson().ToJsonString());
        model.DisplayName="背包入口";
        Assert.Equal("背包入口",model.Name);
        Assert.Equal("ui_profile:bag_open",model.Id);
    }

    [Theory]
    [InlineData("production","鐵錠（鐵礦石）","生产 · 鐵錠（鐵礦石）")]
    [InlineData("gathering","鐵礦石","采集 · 鐵礦石")]
    [InlineData("maintenance","维护 · 打开背包","维护 · 打开背包")]
    public void BuiltInTaskChainsShowCategoryAndTargetWithoutChangingStoredName(string kind,string name,string expected)
    {
        var data=new JsonObject{["id"]="installed:test",["name"]=name,["kind"]=kind,["runtime_key"]="stable_key",["source"]=new JsonObject{["name"]=name}};
        var chain=new WorkbenchChain(data);var before=chain.ToJson().ToJsonString();
        Assert.Equal(expected,chain.DisplayName);
        chain.DisplayName=chain.DisplayName;
        Assert.Equal(before,chain.ToJson().ToJsonString());
        Assert.Equal("stable_key",chain.RuntimeKey);
    }

    [Fact]
    public void RouteAndUserTaskChainNamesKeepStableIdentifiersAndCustomNames()
    {
        var routePath=new JsonArray("maintenance","navigation","backpack_tidy","scene");
        var route=new WorkbenchChain(new JsonObject{["id"]="route:test",["name"]=WorkbenchLabels.RouteChainName("maintenance","scene"),["kind"]="maintenance",["runtime_key"]="maintenance/navigation/backpack_tidy/scene",["source_path"]=routePath.DeepClone()});
        Assert.Equal("维护 · 游戏主界面",route.DisplayName);
        route.Name="我的背包路线";
        Assert.Equal("我的背包路线",route.DisplayName);
        Assert.Equal("maintenance/navigation/backpack_tidy/scene",route.RuntimeKey);
        Assert.True(JsonNode.DeepEquals(routePath,route.Data["source_path"]));

        var custom=new WorkbenchChain(new JsonObject{["id"]="chain:test",["name"]="我的加工",["kind"]="production",["runtime_key"]="custom",["source"]=new JsonObject{["name"]="我的加工"}});
        Assert.Equal("我的加工",custom.DisplayName);
    }

    [Fact] public void UnknownParametersKeepDistinctKeysAndUserChineseNamesStayUnchanged()
    {
        Assert.Contains("customalpha",WorkbenchLabels.Parameter("customalpha"));
        Assert.NotEqual(WorkbenchLabels.Parameter("customalpha"),WorkbenchLabels.Parameter("custombeta"));
        Assert.Equal("道具與材料",WorkbenchLabels.Friendly("道具與材料"));
    }

    [Fact] public void BooleanEditorPreservesNumberAndStringTypesDuringHiddenBindings()
    {
        var number=new WorkbenchStrategy(new JsonObject{["value"]=15});
        Assert.False(number.IsBoolean);Assert.False(number.BooleanValue);
        number.BooleanValue=true;
        Assert.Equal(15,number.Data["value"]!.GetValue<int>());
        var text=new WorkbenchStrategy(new JsonObject{["value"]="scene"});
        Assert.False(text.BooleanValue);text.BooleanValue=false;
        Assert.Equal("scene",text.Value);
        var toggle=new WorkbenchStrategy(new JsonObject{["value"]=false});
        Assert.True(toggle.IsBoolean);toggle.BooleanValue=true;
        Assert.True(toggle.Data["value"]!.GetValue<bool>());
    }

    [Fact] public void NumericStrategyRecoversOriginalTypeAfterClearedOrInvalidDraft()
    {
        var number=new WorkbenchStrategy(new JsonObject{["id"]="numeric",["name"]="间隔",["value_kind"]="number",["original"]=30,["value"]=30});
        var document=new WorkbenchDocument(new JsonObject{["width"]=1280,["height"]=960,["dpi"]=96});
        document.Strategies.Add(number);
        number.Value="";
        Assert.Throws<InvalidDataException>(()=>WorkbenchValidation.Validate(document));
        number.Value="60";
        Assert.Equal(System.Text.Json.JsonValueKind.Number,number.Data["value"]!.GetValueKind());
        Assert.Equal(60,number.Data["value"]!.GetValue<double>());
        WorkbenchValidation.Validate(document);
        number.Value="不是数字";
        Assert.Throws<InvalidDataException>(()=>WorkbenchValidation.Validate(document));
        number.Value="45.5";
        Assert.Equal(45.5,number.Data["value"]!.GetValue<double>());
        WorkbenchValidation.Validate(document);
        var text=new WorkbenchStrategy(new JsonObject{["original"]="30",["value"]="30"});
        text.Value="";text.Value="60";
        Assert.Equal(System.Text.Json.JsonValueKind.String,text.Data["value"]!.GetValueKind());
    }
}
