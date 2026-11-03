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
