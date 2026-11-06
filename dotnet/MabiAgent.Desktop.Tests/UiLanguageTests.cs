using Avalonia.Controls;
using Avalonia.Controls.Documents;
using MabiAgent.Desktop.Editors;
using MabiAgent.Desktop;
using MabiAgent.Windows;
using Xunit;

namespace MabiAgent.Desktop.Tests;

[CollectionDefinition("UI language", DisableParallelization = true)]
public sealed class UiLanguageCollection;

[Collection("UI language")]
public sealed class UiLanguageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mabi-language-ui-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResourcesSwitchWholePhrasesFormatAndPreserveUnknownValues()
    {
        var language = new UiLanguageService();
        var changes = 0;
        language.LanguageChanged += (_, _) => changes++;
        Assert.True(language.ResourceCount >= 500);
        Assert.Equal("简体中文", language.LanguageName);
        Assert.True(language.SetLanguage(UiLanguageService.TraditionalChinese));
        Assert.Equal("繁體中文", language.LanguageName);
        Assert.Equal("倉庫", language.Text("仓库"));
        Assert.Equal("已選 3 項", language.TranslateText("已选 3 条"));
        Assert.Equal("已選 5 項", language.Format("已选 {0} 条", 5));
        Assert.Equal("目標 1/2 已完成 · 3 個活動批次 · 1 項待核帳 · 目前已確認 11時 1分 20秒 / 120時 0分 0秒",
            language.TranslateText("目标 1/2 已完成 · 3 个活动批次 · 1 项待核账 · 当前已确认 11时 1分 20秒 / 120时 0分 0秒"));
        Assert.Equal("心跳 12:34:56 · 執行階段 · 計畫 42 · 暫停：手動暫停 · 自動重試 1/3",
            language.TranslateText("心跳 12:34:56 · 执行阶段 · 计划 42 · 暂停：手动暂停 · 自动重试 1/3"));
        Assert.Equal("右位 乙 · 左位 甲", language.TranslateText("左位 甲 · 右位 乙"));
        for(var index=0;index<800;index++)language.TranslateText($"未登记动态状态 {index}");
        Assert.InRange(language.CachedTranslationCount,0,512);
        Assert.Equal("铁矿石", language.Text("铁矿石"));
        Assert.Equal(1, changes);
        Assert.False(language.SetLanguage("en-US"));
    }

    [Fact]
    public void ColumnHeadersInlineTextAndWorkbenchNamesSwitchWithoutChangingData()
    {
        RuntimeLocalization.Initialize();
        var header=new DataGridTextColumn{Header="已确认"};
        var inline=new Run{Text="自动补充 · "};
        var chain=new WorkbenchChain(new System.Text.Json.Nodes.JsonObject{
            ["id"]="installed:test",["kind"]="fishing",["name"]="fishing · wool",
            ["source"]=new System.Text.Json.Nodes.JsonObject{["name"]="fishing · wool"}});
        var before=chain.ToJson().ToJsonString();
        UiLanguageService.Shared.SetLanguage(UiLanguageService.TraditionalChinese);RuntimeLocalization.RefreshAll();
        Assert.Equal("已確認",header.Header);
        Assert.Equal("自動補充 · ",inline.Text);
        Assert.Equal("釣魚 · 羊毛",chain.DisplayName);
        chain.DisplayName=chain.DisplayName;
        Assert.Equal(before,chain.ToJson().ToJsonString());
        chain.DisplayName="我的 fishing 路线";
        Assert.Equal("我的 fishing 路线",chain.DisplayName);
        UiLanguageService.Shared.SetLanguage(UiLanguageService.SimplifiedChinese);RuntimeLocalization.RefreshAll();
        Assert.Equal("已确认",header.Header);Assert.Equal("自动补充 · ",inline.Text);
        Assert.Equal("我的 fishing 路线",chain.Name);
    }

    [Theory]
    [InlineData("gathering","採集","轮","5轮","2轮")]
    [InlineData("production","製作","件","5件","2件")]
    [InlineData("idle_fishing","掛機釣魚","时长","0时 0分 5秒","0时 0分 2秒")]
    public void QueueUnitsUseTaskKindAcrossLanguageChanges(string kind,string label,string unit,string target,string completed)
    {
        var row=new JobRow("test","道具",label,5,2,"等待",false,1,false,false,false,TaskKind:kind);
        Assert.Equal(unit,row.QuantityUnit);Assert.Equal(target,row.TargetLabel);Assert.Equal(completed,row.CompletedLabel);
        row.Kind="显示名称";
        Assert.Equal(unit,row.QuantityUnit);Assert.Equal(target,row.TargetLabel);
    }

    [Fact]
    public void GatheringPlaceholdersAndSkillParametersDisplayNamesWithoutChangingRuntimeKeys()
    {
        Assert.Equal("钓鱼 · 小釣魚場",Workspace.DisplayItemName("采集点:fishing:小釣魚場"));
        Assert.Equal("剪羊毛 · 羊",Workspace.DisplayItemName("采集点:wool:羊"));
        Assert.Equal("羊毛",Workspace.DisplayItemName("羊毛"));
        var data=System.Text.Json.Nodes.JsonNode.Parse("""{"path":["gathering","skill"],"value":"fishing","value_kind":"string"}""")!.AsObject();
        RuntimeLocalization.Initialize();
        var model=new WorkbenchStrategy(data);
        UiLanguageService.Shared.SetLanguage(UiLanguageService.TraditionalChinese);
        Assert.Equal("釣魚",model.DisplayValue);model.DisplayValue=model.DisplayValue;Assert.Equal("fishing",model.Value);
        UiLanguageService.Shared.SetLanguage(UiLanguageService.SimplifiedChinese);
        Assert.Equal("钓鱼",model.DisplayValue);Assert.Equal("fishing",model.Value);
    }

    [Fact]
    public void ControlsCreatedInTraditionalRestoreTheirOriginalSimplifiedText()
    {
        RuntimeLocalization.Initialize();
        UiLanguageService.Shared.SetLanguage(UiLanguageService.TraditionalChinese);
        var label=new TextBlock{Text="钓鱼时长"};
        var button=new Button();
        TextBlock? child=null;
        button.PropertyChanged+=(_,change)=>
        {
            if(change.Property==ContentControl.ContentProperty&&change.NewValue is string)
                button.Content=child=new TextBlock{Text="采集技能"};
        };
        button.Content="更新物品关联";
        var header=new DataGridTextColumn{Header="已确认"};
        Assert.Equal("釣魚時間",label.Text);
        Assert.Equal("採集技能",child!.Text);
        UiLanguageService.Shared.SetLanguage(UiLanguageService.SimplifiedChinese);
        RuntimeLocalization.RefreshAll();
        Assert.Equal("钓鱼时长",label.Text);
        Assert.Equal("采集技能",child.Text);
        Assert.Equal("已确认",header.Header);
    }

    [Fact]
    public void RuntimeControlsCoverPagesDialogsDynamicStatesAndItemNames()
    {
        RuntimeLocalization.Initialize();
        UiLanguageService.Shared.SetLanguage(UiLanguageService.SimplifiedChinese);
        var page = new TextBlock { Text = "任务队列" };
        var dialog = new TextBlock { Text = "停止全部任务？" };
        var state = new TextBlock { Text = "等待启动" };
        var count = new TextBlock { Text = "3 条" };
        var item = new TextBlock { Text = "铁矿石" };

        UiLanguageService.Shared.SetLanguage(UiLanguageService.TraditionalChinese);
        RuntimeLocalization.RefreshAll();
        Assert.Equal("任務佇列", page.Text);
        Assert.Equal("停止全部任務？", dialog.Text);
        Assert.Equal("等待啟動", state.Text);
        Assert.Equal("3 項", count.Text);
        Assert.Equal("铁矿石", item.Text);

        state.Text = "已自动保存";
        Assert.Equal("已自動儲存", state.Text);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            UiLanguageService.Shared.SetLanguage(cycle % 2 == 0 ? UiLanguageService.SimplifiedChinese : UiLanguageService.TraditionalChinese);
            RuntimeLocalization.RefreshAll();
            Assert.Equal("铁矿石", item.Text);
        }
        UiLanguageService.Shared.SetLanguage(UiLanguageService.SimplifiedChinese);
        RuntimeLocalization.RefreshAll();
        Assert.Equal("任务队列", page.Text);
        Assert.Equal("停止全部任务？", dialog.Text);
        Assert.Equal("3 条", count.Text);
        Assert.Equal("已自动保存", state.Text);
        Assert.Equal("铁矿石", item.Text);
    }

    [Fact]
    public void LanguagePreferencePersistsThroughViewModelAndWorkspace()
    {
        Directory.CreateDirectory(directory);
        var workspace = new Workspace(directory, Path.Combine(directory, "state"));
        var language = new UiLanguageService();
        var model = new MainViewModel(workspace, new GameWindowSelection(() => []), language);
        model.ChangeLanguage(UiLanguageService.TraditionalChinese);

        var saved = DesktopPreferences.FromJson(workspace.ReadSettings());
        Assert.Equal(UiLanguageService.TraditionalChinese, saved.Language);
        Assert.Equal("繁中", model.LanguageButtonLabel);
        Assert.Equal("任務佇列", language.TranslateText(model.PageTitle));
        Assert.Contains("## 安裝", UserGuideDocument.Read(language.Culture));
        model.PrepareClose();
    }

    [Fact]
    public void EmbeddedGuidesContainBothLanguagesAndResolveDocumentLinks()
    {
        Assert.Contains("## 安装", UserGuideDocument.Read("zh-CN"));
        Assert.Contains("## 安裝", UserGuideDocument.Read("zh-TW"));
        Assert.Contains("| --- |", UserGuideDocument.Read("zh-CN"));
        Assert.NotEmpty(UserGuideDocument.Read("zh-CN", "development"));
        Assert.Contains("PolyForm", UserGuideDocument.Read("zh-CN", "license"));
        Assert.Equal("zh-TW", UserGuideDocument.RelativeDocument("README.zh-TW.md"));
        Assert.Equal("development", UserGuideDocument.RelativeDocument("docs/DEVELOPMENT.md#build"));
        Assert.Equal("license", UserGuideDocument.RelativeDocument("../LICENSE"));
        Assert.Null(UserGuideDocument.RelativeDocument("https://example.org"));
    }

    public void Dispose()
    {
        UiLanguageService.Shared.SetLanguage(UiLanguageService.SimplifiedChinese);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
