using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using MabiAgent.Desktop.Editors;

namespace MabiAgent.Desktop;

internal static class LanguageUiSelfCheck
{
    public static async Task<string[]> Run(MainWindow window,MainViewModel model,string? auditPath=null)
    {
        RuntimeLocalization.Initialize();
        var language=UiLanguageService.Shared;
        var original=language.Culture;
        var checks=new List<string>();
        try
        {
            language.SetLanguage(UiLanguageService.SimplifiedChinese);
            RuntimeLocalization.RefreshAll();
            window.ShowInTaskbar=false;window.Opacity=0;
            if(!window.IsVisible)window.Show();
            Layout(window);

            model.SelectedPage=0;
            model.AddDialogOpen=true;
            model.OpenCaptureSettingsCommand.Execute(null);
            model.SelectedPage=1;
            Layout(window);
            for(var attempt=0;attempt<40;attempt++)
            {
                await Task.Delay(20);Layout(window);
                if(window.AccountsPanel.GetLogicalDescendants().OfType<DataGrid>().Any(grid=>grid.Columns.Count>=6))break;
            }
            model.SelectedPage=2;
            SettingsPageSections(window.SettingsPage);
            Layout(window);

            if(model.Workbench is null)throw new InvalidOperationException("工作台未初始化");
            var workbench=new AnchorWorkbenchWindow(model.Workbench){ShowInTaskbar=false,Opacity=0};
            if(!workbench.IsVisible)workbench.Show(window);
            model.ChainEditor?.NewDraftCommand.Execute(null);
            model.RuntimeTask="当前任务：铁矿石";
            model.RuntimeProgress="目标 0/1 已完成 · 1 个活动批次 · 1 项待核账 · 当前已确认 120 / 120";
            model.RuntimeHealth="心跳 12:34:56 · 执行阶段 · 计划 42 · 暂停：手动暂停 · 自动重试 1/3";
            Layout(window);Layout(workbench);
            for(var category=0;category<6;category++){model.Workbench.Category=category;Layout(workbench);await Task.Delay(20);}
            model.Workbench.Category=0;Layout(workbench);
            var hadDraft=model.ChainEditor?.HasUnsavedChanges==true;
            var headers=new Control[]{window,window.QueuePage,window.AccountsPanel,window.SettingsPage,workbench}.SelectMany(root=>root.GetSelfAndVisualDescendants().OfType<DataGrid>().Concat(root.GetLogicalDescendants().OfType<DataGrid>()))
                .SelectMany(grid=>grid.Columns).Distinct().Where(column=>column.Header is string).ToDictionary(column=>column,column=>column.Header!.ToString()!);
            var document=model.Workbench.Document;
            var originalDocument=document.ToJson().ToJsonString();
            var simplifiedTexts=Texts(window).Concat(Texts(workbench)).Concat(headers.Values).Distinct().ToArray();

            Check(HasText(window.QueuePage,"我的任务"),"队列简体文案");
            Check(HasText(window.AccountsPanel,"更新物品关联"),"仓库简体文案："+string.Join(" | ",Texts(window.AccountsPanel).Where(x=>x.Contains("物品")||x.Contains("关联"))));
            Check(HasText(window.SettingsPage,"外观与性能"),"设置简体文案");
            Check(HasText(window.AddOverlay,"新增任务"),"新增目标弹窗简体文案");
            Check(HasText(window.CaptureOverlay,"抓包设置"),"抓包设置弹窗简体文案");
            Check(HasText(workbench,"流程工作台"),"工作台简体文案");
            checks.Add("真实窗口简体页面、弹窗与工作台");

            language.SetLanguage(UiLanguageService.TraditionalChinese);
            RuntimeLocalization.RefreshAll();
            Layout(window);Layout(workbench);
            Check(HasText(window.QueuePage,"我的任務"),"队列繁体文案");
            Check(HasText(window.AccountsPanel,"更新物品關聯"),"仓库繁体文案");
            Check(HasText(window.SettingsPage,"外觀與效能"),"设置繁体文案");
            Check(window.SettingsPage.AppearanceSettingsSection.PlaceholderTitle=="外觀與效能","延迟设置占位标题繁体化");
            Check(window.LanguageButton.IsVisible&&HasText(window.LanguageButton,"繁中"),"顶栏语言入口可见");
            Check(HasText(window.AddOverlay,"新增任務"),"新增目标弹窗繁体文案");
            Check(HasText(window.CaptureOverlay,"封包擷取設定"),"抓包设置弹窗繁体文案");
            Check(HasText(workbench,"流程工作台"),"工作台繁体文案");
            Check(HasText(window,"目前任務：铁矿石"),"完整任务状态保留物品名");
            Check(HasText(window,"目標 0/1 已完成 · 1 個活動批次 · 1 項待核帳 · 目前已確認 120 / 120"),"完整进度状态");
            Check(HasText(window,"心跳 12:34:56 · 執行階段 · 計畫 42 · 暫停：手動暫停 · 自動重試 1/3"),"完整健康状态");
            Check(model.ChainEditor?.HasUnsavedChanges==hadDraft,"草稿在语言切换后保持");
            Check(headers.All(pair=>pair.Key.Header?.ToString()==language.TranslateText(pair.Value)),"全页面表格标题随语言切换");
            Check(headers.Keys.Any(c=>c.Header?.ToString()=="已確認")&&headers.Keys.Any(c=>c.Header?.ToString()=="類型"),"队列表头为繁体中文："+string.Join("/",headers.Keys.Select(c=>c.Header)));
            Check(document.ToJson().ToJsonString()==originalDocument,"任务链与参数切换语言后原始数据保持");
            Check(!document.Chains.SelectMany(c=>new[]{c.DisplayName}.Concat(c.Cards.Select(card=>card.DisplayName))).Any(name=>System.Text.RegularExpressions.Regex.IsMatch(name,@"\b(fishing|wool)\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase)),"内置任务链与步骤不显示fishing或wool运行键");
            Check(HasText(window.QueuePage,"任務會依材料、加工時間和目前狀態自動安排。"),"繁体自动安排提示");
            if(auditPath is not null)
                System.IO.File.WriteAllText(auditPath,System.Text.Json.JsonSerializer.Serialize(new{simplified=simplifiedTexts,traditional=Texts(window).Concat(Texts(workbench)).Concat(headers.Keys.Select(c=>c.Header?.ToString()??"")).Distinct().ToArray(),chains=document.Chains.Select(c=>new{c.DisplayName,cards=c.Cards.Select(card=>new{card.DisplayName,card.Summary,card.StageLabel})}),strategies=document.Strategies.Select(s=>new{s.ParameterLabel,s.DescriptionLabel})}));
            checks.Add($"全部{headers.Count}个表格标题、内置任务链步骤、自动安排提示及原始数据保持");
            checks.Add("真实窗口繁体页面、弹窗、工作台、复合状态与草稿");

            language.SetLanguage(UiLanguageService.SimplifiedChinese);
            RuntimeLocalization.RefreshAll();
            Layout(window);Layout(workbench);
            window.Width=1000;window.Height=700;window.UpdateResponsiveLayout(new Size(1000,700));Layout(window,1000,700);
            var linksRight=window.TopLinks.TranslatePoint(new Point(window.TopLinks.Bounds.Width,0),window)?.X??double.MaxValue;
            Check(linksRight<=window.Bounds.Width-150,"窄窗顶栏语言入口不侵占原生窗口按钮区");
            Check(HasText(window.QueuePage,"我的任务"),"队列切回简体");
            Check(HasText(window.SettingsPage,"外观与性能"),"设置切回简体");
            Check(HasText(workbench,"流程工作台"),"工作台切回简体");
            Check(model.ChainEditor?.HasUnsavedChanges==hadDraft,"草稿切回简体后保持");
            language.SetLanguage(UiLanguageService.TraditionalChinese);
            RuntimeLocalization.RefreshAll();Layout(window);Layout(workbench);
            Check(HasText(window,"目前任務：铁矿石")&&model.ChainEditor?.HasUnsavedChanges==hadDraft,"再次切换保留物品名与草稿");
            language.SetLanguage(UiLanguageService.SimplifiedChinese);
            RuntimeLocalization.RefreshAll();Layout(window);Layout(workbench);
            Check(HasText(window.QueuePage,"我的任务")&&model.ChainEditor?.HasUnsavedChanges==hadDraft,"多轮双向切换稳定");
            checks.Add("真实窗口多轮双向切换、物品名与草稿保持");
            return checks.ToArray();
        }
        finally
        {
            language.SetLanguage(original);
            RuntimeLocalization.RefreshAll();
        }
    }

    private static void SettingsPageSections(SettingsPanel settings)
    {
        foreach(var section in new[]
        {
            settings.VipSettingsSection,settings.OperationSettingsSection,settings.AppearanceSettingsSection,
            settings.DataSyncSettingsSection,settings.RepairSettingsSection,settings.PurchaseSettingsSection,
            settings.BackpackSettingsSection,settings.ReserveSettingsSection,settings.WingSettingsSection,
            settings.UpdateSettingsSection
        })section.EnsureBuilt();
    }

    private static void Layout(Control control,double width=1340,double height=920)
    {
        control.Measure(new Size(width,height));
        control.Arrange(new Rect(0,0,width,height));
        control.UpdateLayout();
    }

    private static bool HasText(Control root,string expected)=>Texts(root).Contains(expected,StringComparer.Ordinal);

    private static string[] Texts(Control root)=>root.GetSelfAndVisualDescendants()
        .SelectMany(control=>control switch
        {
            TextBlock text when text.Text is { } value=>new[]{value},
            ContentControl content when content.Content is string value=>new[]{value},
            _=>[]
        }).Distinct(StringComparer.Ordinal).ToArray();

    private static void Check(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException("语言自检失败："+message);
    }
}
