using MabiAgent.Core;
using Avalonia.Controls;
using MabiAgent.Desktop.Controls;
using Avalonia.VisualTree;
using System.IO;
using System.Text.Json.Nodes;
using MabiAgent.Windows;
namespace MabiAgent.Desktop;
internal static class UiSelfCheck
{
    private static async Task<string[]> CheckSearchablePickers(MainWindow window,MainViewModel model)
    {
        var passed=new List<string>();
        void Need(bool condition,string label){if(!condition)throw new InvalidOperationException(label);passed.Add(label);}
        async Task CheckPicker(AutoCompleteBox picker,object[] choices,Func<object,string> label,string title)
        {
            object[] rows=[];
            void Populated(object? sender,PopulatedEventArgs args)=>rows=args.Data.Cast<object>().ToArray();
            picker.Populated+=Populated;
            try
            {
                foreach(var choice in new[]{choices[0],choices[^1]})
                {
                    picker.IsDropDownOpen=false;picker.SelectedItem=choice;picker.IsDropDownOpen=true;await Task.Delay(80);
                    if(rows.Length!=choices.Length)throw new InvalidOperationException($"{title}：已选名称打开应显示全部{choices.Length}项，实际{rows.Length}项");
                    if(!Equals(picker.SelectedItem,choice))throw new InvalidOperationException(title+"：展开不得清空当前选择");
                    picker.IsDropDownOpen=false;
                }
                var query=label(choices[0]);picker.Text=query;await Task.Delay(80);
                var expected=choices.Count(c=>label(c).Contains(query,StringComparison.CurrentCultureIgnoreCase));
                if(rows.Length!=expected)throw new InvalidOperationException(title+"：键入名称应过滤候选");
                passed.Add(title+"默认选择、改选重开显示全量，键入名称仍可筛选");
            }
            finally{picker.IsDropDownOpen=false;picker.Populated-=Populated;}
        }
        model.OpenAddCommand.Execute(null);
        foreach(var kind in new[]{"采集","挂机钓鱼"})
        {
            model.TaskKind=kind;window.UpdateLayout();await Task.Delay(40);
            var picker=window.FindControl<AutoCompleteBox>("GatheringPicker")!;
            await CheckPicker(picker,model.GatheringChoices.Cast<object>().ToArray(),o=>((GatheringChoice)o).Name,kind+"地点下拉");
        }
        try
        {
            model.TaskKind="生产";window.UpdateLayout();await Task.Delay(40);
            var productionCategories=model.GoalCategories.ToArray();
            var productionLabels=productionCategories.Select(category=>category.Label).ToArray();
            Need(model.GoalCategoryLabel=="生产设施"&&model.ShowGoalCategory&&productionCategories.Length>0&&productionCategories[0].Label=="全部设施","生产目标显示设施分类并以“全部设施”恢复全量");
            var woodCategory=Array.IndexOf(productionLabels,"木材加工");var metalCategory=Array.IndexOf(productionLabels,"金属加工");var foodCategory=Array.IndexOf(productionLabels,"食材加工");
            Need(woodCategory>0&&metalCategory>woodCategory&&foodCategory>metalCategory,"生产设施分类包含已知的木材、金属和食材组并保持排序");

            model.SelectedGoalCategory=productionCategories.Single(category=>category.Label=="金属加工");window.UpdateLayout();await Task.Delay(40);
            var metalRecipes=model.RecipeChoices.Cast<RecipeRow>().ToArray();
            var iron=metalRecipes.Single(recipe=>recipe.Id=="iron_ingot_from_iron_ore");var steel=metalRecipes.Single(recipe=>recipe.Id=="metal_steel");
            Need(metalRecipes.Length>=2&&metalRecipes.All(recipe=>recipe.Device=="金属加工"),"选择金属设施后配方视图只保留金属加工配方");
            Need(Array.IndexOf(metalRecipes,steel)<Array.IndexOf(metalRecipes,iron),"金属加工组内钢锭排在铁锭之前，已知配方排序生效");

            model.SelectedRecipe=iron;model.RecipeSearch=iron.Name;window.UpdateLayout();await Task.Delay(40);
            var recipePicker=window.FindControl<ComboBox>("RecipePicker")!;
            Need(model.RecipeChoices.Contains(iron)&&model.SelectedRecipe?.Id==iron.Id&&Equals(recipePicker.SelectedItem,iron),"配方名称精确搜索保留当前选择");
            model.RecipeSearch="";model.SelectedGoalCategory=productionCategories[0];window.UpdateLayout();await Task.Delay(40);
            Need(model.RecipeChoices.Count==model.Recipes.Count&&model.RecipeChoices.Cast<RecipeRow>().Any(recipe=>recipe.Device=="木材加工")&&model.RecipeChoices.Cast<RecipeRow>().Contains(iron),"切换“全部设施”恢复木质与金属全部配方");

            model.TaskKind="采集";window.UpdateLayout();await Task.Delay(40);
            var allHarvest=model.GatheringChoices.Cast<GatheringChoice>().ToArray();
            var skillCategories=model.GoalCategories.ToArray();var skillLabels=skillCategories.Select(category=>category.Label).ToArray();
            Need(model.GoalCategoryLabel=="采集技能"&&model.ShowGoalCategory&&skillCategories.Length>0&&skillCategories[0].Label=="全部技能","采集目标显示技能分类并以“全部技能”恢复全量");
            var loggingSkill=Array.IndexOf(skillLabels,"伐木");var miningSkill=Array.IndexOf(skillLabels,"采矿");var herbSkill=Array.IndexOf(skillLabels,"采药草");
            Need(loggingSkill>0&&miningSkill>loggingSkill&&herbSkill>miningSkill,"采集技能分类包含伐木、采矿和采药草并保持已知顺序："+string.Join("、",skillLabels));

            var miningCategory=skillCategories.Single(category=>category.Label=="采矿");model.SelectedGoalCategory=miningCategory;window.UpdateLayout();await Task.Delay(40);
            var mining=model.GatheringChoices.Cast<GatheringChoice>().ToArray();
            var ironRoute=mining.Single(route=>route.Id=="iron_ore_nearby");var coalRoute=mining.Single(route=>route.Id=="point_mining_03");
            Need(mining.All(route=>!route.Fishing)&&mining.Any(route=>route.Id=="iron_ore_nearby")&&!mining.Any(route=>route.Name=="樹木"),"采矿技能只保留非钓鱼采矿点并包含铁矿脉");
            Need(Array.IndexOf(mining,coalRoute)<Array.IndexOf(mining,ironRoute),"采矿组内煤炭矿脉排在铁矿脉之前，地点排序生效");

            var gatheringPicker=window.FindControl<AutoCompleteBox>("GatheringPicker")!;gatheringPicker.SelectedItem=ironRoute;model.SelectedGathering=ironRoute;gatheringPicker.Text=ironRoute.Name;gatheringPicker.IsDropDownOpen=true;await Task.Delay(80);
            Need(Equals(gatheringPicker.SelectedItem,ironRoute)&&model.SelectedGathering?.Id==ironRoute.Id,"采集地点精确搜索保留当前选择");
            gatheringPicker.IsDropDownOpen=false;
            model.SelectedGoalCategory=skillCategories[0];window.UpdateLayout();await Task.Delay(40);
            Need(model.GatheringChoices.Select(route=>route.Id).SequenceEqual(allHarvest.Select(route=>route.Id)),"切换“全部技能”恢复全部非钓鱼采集点");

            model.TaskKind="挂机钓鱼";window.UpdateLayout();await Task.Delay(40);
            Need(model.GoalCategoryLabel=="采集技能"&&model.ShowGoalCategory&&model.GoalCategories.Count>0&&model.GoalCategories[0].Label=="全部技能"&&model.GatheringChoices.Count>0&&model.GatheringChoices.All(route=>route.Fishing),"挂机钓鱼保留技能分类并只显示钓鱼场");
            model.TaskKind="原地钓鱼";window.UpdateLayout();await Task.Delay(40);
            Need(!model.ShowGoalCategory&&model.GatheringChoices.Count==0&&model.SelectedGathering is null,"原地钓鱼保留无需地点和技能分类的模式");
        }
        finally
        {
            // Later interaction checks consume the gathering picker immediately after this helper.
            // Leave an explicit, non-empty gathering fixture instead of inheriting production state.
            model.TaskKind="采集";
            model.SelectedGoalCategory=model.GoalCategories.FirstOrDefault(category=>category.Key=="all");
            model.SelectedGathering=model.GatheringChoices.FirstOrDefault();
            window.UpdateLayout();await Task.Delay(40);
        }
        model.CloseAddCommand.Execute(null);
        var previousPage=model.SelectedPage;
        ScrollViewer? settingsViewer=null;
        Avalonia.Vector previousOffset=default;
        try
        {
            model.SelectedPage=2;window.UpdateLayout();await Task.Delay(40);
            var settings=window.FindControl<SettingsPanel>("SettingsPage")!;
            var settingsColumns=settings.FindControl<Grid>("SettingsColumns")
                ?? throw new InvalidOperationException("设置页缺少 SettingsColumns 网格");
            settingsViewer=settingsColumns.GetVisualAncestors().OfType<ScrollViewer>().First();
            previousOffset=settingsViewer.Offset;
            var reserveSection=settings.FindControl<DeferredSettingsSection>("ReserveSettingsSection")
                ?? throw new InvalidOperationException("设置页缺少延迟材料留存卡");
            for(var attempt=0;attempt<50&&!reserveSection.IsBuilt;attempt++){reserveSection.BringIntoView();window.UpdateLayout();await Task.Delay(20);}
            if(!reserveSection.IsBuilt&&!window.IsVisible){reserveSection.EnsureBuilt();window.UpdateLayout();}
            if(!reserveSection.IsBuilt)throw new InvalidOperationException("滚到材料留存卡后未实现其设置模板");
            reserveSection.BringIntoView();window.UpdateLayout();await Task.Delay(40);
            var reserve=reserveSection.GetVisualDescendants().OfType<AutoCompleteBox>().Single(c=>c.Name=="ReservePicker");
            for(var attempt=0;attempt<20&&!reserve.IsEffectivelyVisible;attempt++)
            {
                reserve.BringIntoView();
                window.UpdateLayout();
                await Task.Delay(20);
            }
            window.UpdateLayout();
            if(window.IsActive&&!reserve.IsEffectivelyVisible)throw new InvalidOperationException("背包卡实现后材料留存下拉不在设置视口内");
            await CheckPicker(reserve,model.ReserveChoices.Cast<object>().ToArray(),o=>((ReserveChoice)o).Name,"材料留存下拉");
        }
        finally
        {
            if(settingsViewer is not null)settingsViewer.Offset=previousOffset;window.UpdateLayout();await Task.Delay(40);
            model.SelectedPage=previousPage;window.UpdateLayout();await Task.Delay(40);
        }
        return passed.ToArray();
    }
    // Independent state directory: no game input and no writes to player data.
    public static async Task<string[]> Run(string root)
    {
        var directory=Path.Combine(Path.GetTempPath(),"mabi-ui-check-"+Guid.NewGuid().ToString("N"));
        var w=new Workspace(root,directory);var vm=new MainViewModel(w,new GameWindowSelection(()=>[]));
        var passed=new List<string>();
        void Check(bool condition,string label){if(!condition)throw new InvalidOperationException(label);passed.Add(label);}
        try
        {
            await vm.RefreshAsync();Check(vm.Error is null,"读取空状态不落盘");
            Check(!Directory.Exists(directory),"读取不创建用户文件");
            passed.AddRange(await StartupNoticeSelfCheck.Run());
            var refreshWorkspace=new Workspace(root,Path.Combine(directory,"refresh-pause-check"));
            await using(var refreshService=new AutomationService(refreshWorkspace,new GameWindowSelection(()=>[])))
            {
                var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                var refreshing=typeof(AutomationService).GetField("refreshingInventory",flags)!;
                var autoResume=typeof(AutomationService).GetField("resumeAfterRecovery",flags)!;
                var pauseRevision=typeof(AutomationService).GetField("commandRevision",flags)!;
                var serviceGate=(SemaphoreSlim)typeof(AutomationService).GetField("gate",flags)!.GetValue(refreshService)!;
                var pauseModel=new MainViewModel(refreshWorkspace,new GameWindowSelection(()=>[]));
                typeof(MainViewModel).GetField("automation",flags)!.SetValue(pauseModel,refreshService);
                refreshing.SetValue(refreshService,true);autoResume.SetValue(refreshService,true);
                Check(!refreshService.Status.Running&&refreshService.IsRunning&&pauseModel.CanPause,"快速重进刷新属于活动执行且暂停入口可用");
                await serviceGate.WaitAsync();Task pause;
                try
                {
                    pause=refreshService.PauseAsync();
                    Check((int)pauseRevision.GetValue(refreshService)! == 1&&!pause.IsCompleted,"暂停在等待调度锁前撤销旧自动恢复命令");
                }
                finally{serviceGate.Release();}
                await pause.WaitAsync(TimeSpan.FromSeconds(3));
                Check(!refreshService.IsRunning&&!(bool)autoResume.GetValue(refreshService)!&&!pauseModel.CanPause,"刷新暂停完成后清除执行与自动恢复状态");
                refreshWorkspace.EditQueue(q=>q.Add("stop-check","production",1156816717,3,"iron_ingot_from_iron_ore",[]));
                File.WriteAllText(Path.Combine(refreshWorkspace.DataPath,"native-input-intents.json"),"{}");
                refreshing.SetValue(refreshService,false);autoResume.SetValue(refreshService,true);
                Task stop,start,workbench;await serviceGate.WaitAsync();
                try
                {
                    start=refreshService.StartAsync();workbench=refreshService.RunWorkbenchTestAsync(new(new JsonObject()),new(new JsonObject()),null,false,new Progress<Editors.WorkbenchRunRow>(),CancellationToken.None);refreshing.SetValue(refreshService,true);stop=refreshService.StopAllAsync();
                    static async Task<bool> Cancelled(Task task){try{await task.WaitAsync(TimeSpan.FromSeconds(3));return false;}catch(OperationCanceledException){return true;}}
                    Check(await Cancelled(start)&&await Cancelled(workbench)&&!stop.IsCompleted,"停止全部先取消共享及工作台等待，再等待调度锁");
                }
                finally{serviceGate.Release();}
                await stop.WaitAsync(TimeSpan.FromSeconds(3));
                Check(!refreshService.IsRunning&&!(bool)autoResume.GetValue(refreshService)!&&refreshService.Status.ReconciliationCount==0,"停止全部终止刷新和自动恢复并清空核账状态");
                Check(!File.Exists(Path.Combine(refreshWorkspace.DataPath,"native-input-intents.json"))&&refreshWorkspace.Read().Jobs.Single().Target==3,"停止全部清除消费意图并保留用户目标");
                File.WriteAllText(Path.Combine(refreshWorkspace.DataPath,"stop-reset.json"),"{}");
                static async Task<string> Failure(Func<Task> action){try{await action().WaitAsync(TimeSpan.FromSeconds(3));return "";}catch(Exception error){return error.Message;}}
                var startReset=await Failure(refreshService.StartAsync);var reloginReset=await Failure(refreshService.ReloginAsync);var refreshReset=await Failure(refreshService.RefreshInventoryAsync);
                var workbenchReset=await Failure(()=>refreshService.RunWorkbenchTestAsync(new(new JsonObject()),new(new JsonObject()),null,false,new Progress<Editors.WorkbenchRunRow>(),CancellationToken.None));
                Check(new[]{startReset,reloginReset,refreshReset,workbenchReset}.All(message=>message.Contains("停止重置未完成")),"停止重置标记在会话构建和输入前阻断开始、重登、刷新及输入测试");
                var readOnlyReset=await Failure(()=>refreshService.RunWorkbenchTestAsync(new(new JsonObject()),new(new JsonObject()),null,true,new Progress<Editors.WorkbenchRunRow>(),CancellationToken.None));
                Check(!readOnlyReset.Contains("停止重置未完成"),"停止重置期间仅允许不激活窗口且不发送输入的识别测试");
                await refreshService.StopAllAsync().WaitAsync(TimeSpan.FromSeconds(3));pauseModel.PrepareClose();
            }
            var batchRow=new JobRow("old","铁锭","生产",20,0,"批次进行中",false,1,true,false,false,ActionId:"batch-old");
            Check(!batchRow.IsWarningState,"已分配批次不冒充结果待确认");
            batchRow.ApplyRuntime(new("batch-old","unknown","confirm_craft","consuming_result_not_visible",true));
            Check(batchRow.IsWarningState&&batchRow.Status=="本批结果待核账","真正未知消费显示待核账");
            var nextRow=new JobRow("next","胶水","生产",20,0,"批次进行中",false,2,true,false,false,ActionId:"batch-next");
            var boundRows=new QueueRows();boundRows.ApplySnapshot([batchRow,nextRow]);boundRows.ApplySnapshot([nextRow]);
            boundRows[0].ApplyRuntime(new("batch-old","unknown","confirm_craft",null,true));
            Check(boundRows[0].Id=="next"&&!boundRows[0].IsWarningState,"删除与改序不把旧动作待核账转到另一行");
            nextRow.ApplyRuntime(new("batch-next","running","queue",null,false));Check(!nextRow.IsWarningState&&nextRow.IsActiveState,"前往设备属于活动批次");
            nextRow.ApplyRuntime(new("batch-next","waiting","waiting",null,false));Check(nextRow.Status=="设备加工中"&&!nextRow.IsWarningState,"已确认委托的加工等待不显示待核账");
            nextRow.UpdateFrom(new("next","胶水","生产",20,0,"批次进行中",false,1,true,false,false,ActionId:"replacement"));
            nextRow.ApplyRuntime(new("batch-next","unknown","confirm_craft",null,true));Check(!nextRow.IsWarningState,"同一任务新批次拒绝旧动作状态");
            vm.RecipeSearch="不存在的配方";
            Check(vm.SelectedRecipe is null&&!vm.AddProductionCommand.CanExecute(null),"无匹配配方不能提交隐藏选择");
            vm.RecipeSearch="";vm.SelectedRecipe=vm.Recipes.First();
            vm.TaskKind="生产";vm.SelectedRecipe=vm.Recipes.First();vm.TargetQuantity="1";vm.OpenAddCommand.Execute(null);
            Check(vm.AddDialogOpen&&vm.CanAddGoal&&vm.AddGoalCommand.CanExecute(null),"已选配方与合法数量可通过新增目标提交");
            vm.CloseAddCommand.Execute(null);
            vm.RecipeSearch="不存在的配方";Check(vm.SelectedRecipe is null&&!vm.AddGoalCommand.CanExecute(null),"配方筛选隐藏选择后禁用新增目标");
            vm.RecipeSearch="";vm.SelectedRecipe=vm.Recipes.First();            vm.OpenAddCommand.Execute(null);vm.TargetQuantity="错字";
            vm.TaskKind="采集";Check(vm.GoalQuantityLabel=="采集轮数"&&vm.GoalQuantityUnit=="轮","新增采集目标显示轮数单位");vm.TaskKind="生产";
            Check(vm.GoalQuantityLabel=="目标产物数量"&&vm.GoalQuantityUnit=="件","新增生产目标显示产物件数单位");
            Check(!vm.AddProductionCommand.CanExecute(null),"非法数量不能提交旧值");
            await vm.AddProductionCommand.ExecuteAsync(null);Check(vm.Jobs.Count==0,"绕过按钮也不能提交非法数量");
            vm.TargetQuantity="12";await vm.AddProductionCommand.ExecuteAsync(null);
            Check(vm.Jobs.Count==1&&!vm.AddDialogOpen,"新增成功关闭弹窗");
            vm.SelectedJob=vm.Jobs[0];vm.EditedQuantity="3.5";
            Check(!vm.SaveQuantityCommand.CanExecute(null),"编辑数量小数被阻止");
            vm.EditedQuantity="15";await vm.SaveQuantityCommand.ExecuteAsync(null);
            Check(vm.Jobs[0].Target==15,"修改数量写入原格式队列");
            await vm.ControlCommand.ExecuteAsync("pause");
            Check(!vm.ControlCommand.CanExecute("pause")&&vm.ControlCommand.CanExecute("continue"),"操作按钮随目标状态变化");
            vm.Search="不存在的目标";Check(vm.SelectedJob is null&&!vm.MoveCommand.CanExecute("up"),"过滤隐藏后清除操作目标");vm.Search="";
            vm.BackpackThreshold="101";Check(!vm.SaveSettings(),"拒绝超范围阈值");
            await vm.RefreshAsync();Check(vm.BackpackThreshold=="101"&&vm.SettingsDirty,"刷新保留未保存输入");
            vm.BackpackThreshold="85";Check(vm.SaveSettings(),"修正后可保存");
            var path=Path.Combine(directory,"settings.json");var settings=JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            settings["repair"]!["gold_reserve_min"]=45678;File.WriteAllText(path,settings.ToJsonString());
            vm.GoldReserve="98765";Check(!vm.SaveSettings(),"外部设置冲突不会覆盖");
            await vm.RefreshAsync();Check(vm.GoldReserve=="98765"&&vm.SettingsDirty,"冲突刷新仍保留输入");
            vm.ReloadSettingsCommand.Execute(null);
            Check(vm.GoldReserve=="45678"&&!vm.SettingsDirty,"显式放弃后重读磁盘");
            passed.AddRange(Accounts.AccountItemsSelfCheck.Run());
            var repository=new Editors.EditorRepository(root,Path.Combine(directory,"editor-check"));
            vm.InitializeEditorSelfCheck(repository,new MabiAgent.Capture.AccountItemService(Path.Combine(directory,"account-check")));
            var gatheringSkills=GoalChoices.LoadGatheringSkills(root,()=>Editors.PublishedCatalogCompiler.CompileAvailable(repository));
            Check(gatheringSkills.GetValueOrDefault("iron_ore_nearby")=="采矿","发布目录中的铁矿别名继承采矿分类："+gatheringSkills.GetValueOrDefault("iron_ore_nearby","未映射"));
            passed.AddRange(await Editors.AnchorWorkbenchSelfCheck.Run(vm,new Editors.EditorRepository(root,Path.Combine(directory,"workbench-check"))));
            var gatheringChoices=new Workspace(root,Path.Combine(directory,"gathering-choices")).Read().Gathering!;
            Check(gatheringChoices.Single(c=>c.Id=="iron_ore_nearby").Name=="鐵礦脈"&&!gatheringChoices.Any(c=>c.Id=="point_mining_01"),"铁矿采集显示游戏名称并隐藏重复新建入口");
            Check(vm.ChainEditor is not null&&vm.AnchorEditor is not null&&vm.AccountItems is not null,"新页面模型无需抓包或游戏输入");
            _=new Editors.ChainEditorPanel{DataContext=vm.ChainEditor};_=new Editors.AnchorEditorPanel{DataContext=vm.AnchorEditor};
            Check(vm.ChainEditor!.Templates.Count>0&&vm.AnchorEditor!.Anchors.Count>0,"任务链与锚点页面加载安装配置："+vm.AnchorEditor?.Status);
            vm.ChainEditor.SelectedTemplate=vm.ChainEditor.Templates.First(t=>Editors.EditorRepository.Text(t.Document,"template_key")=="production:iron_ingot_from_iron_ore");vm.ChainEditor.NewDraftCommand.Execute(null);vm.ChainEditor.Name="自检任务链";vm.ChainEditor.Hours="1";vm.ChainEditor.Minutes="2";vm.ChainEditor.Seconds="3";vm.ChainEditor.SaveCommand.Execute(null);
            Check(repository.List().Count==1&&repository.List()[0]["parameters"]!["duration_s"]!.GetFiniteDouble()==3723,"编辑任务链小时分秒并保存草稿");
            vm.ChainEditor.PublishCommand.Execute(null);Check(repository.List()[0]["status"]!.GetValue<string>()=="published","发布任务链先校验后落盘");await vm.RefreshAsync();Check(vm.Recipes.Any(r=>r.Duration==3723),"发布后重新加载下拉配方");
            var previewWindow=new MainWindow(vm){Width=1120,Height=800};var content=previewWindow;vm.SelectedPage=2;content.Measure(new Avalonia.Size(1120,800));content.Arrange(new Avalonia.Rect(0,0,1120,800));content.UpdateLayout();Check(Avalonia.Application.Current!.Styles.Count>=3,"Semi 与 Ursa 主题资源已加载");using var preview=new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1120,800));preview.Render(content);Check(preview.PixelSize.Width==1120,"设置页离屏布局可渲染");

            vm.TaskKind="采集";Check(vm.GatheringChoices.Count>0,"新增目标支持采集点");vm.TargetQuantity="2";await vm.AddGoalCommand.ExecuteAsync(null);Check(vm.Jobs.Any(j=>j.Kind=="采集"),"采集目标写入原队列");
            vm.TaskKind="挂机钓鱼";vm.FishingHours="0";vm.FishingMinutes="1";vm.FishingSeconds="5";Check(vm.GatheringChoices.Count>0&&vm.CanAddGoal,"钓鱼点与时分秒有效");await vm.AddGoalCommand.ExecuteAsync(null);Check(vm.Jobs.Any(j=>j.Kind=="挂机钓鱼"&&j.Target==65),"钓鱼时分秒写入秒数目标");
            vm.SelectedJob=vm.Jobs.First(j=>j.Kind=="挂机钓鱼");vm.EditedHours="1";vm.EditedMinutes="2";vm.EditedSeconds="3";await vm.SaveQuantityCommand.ExecuteAsync(null);Check(vm.SelectedJob!.Target==3723&&vm.SelectedJob.TargetLabel=="1时 2分 3秒","队列钓鱼时长可按时分秒修改");
            var globalRepair=vm.RepairEnabled;vm.TaskKind="原地钓鱼";
            Check(vm.IsFishingGoal&&vm.IsInPlaceFishing&&!vm.NeedsGatheringPoint&&vm.SelectedGathering is null&&vm.CanAddGoal,"原地钓鱼无需选点且采用时长");
            vm.FishingMinutes="2";vm.FishingSeconds="3";await vm.AddGoalCommand.ExecuteAsync(null);
            var inPlace=vm.Jobs.Single(j=>j.Kind=="原地钓鱼");Check(inPlace.IsFishing&&inPlace.Target==123&&inPlace.Name=="当前位置","原地钓鱼写入同一钓鱼队列并可重读");
            vm.SelectedJob=inPlace;vm.EditedHours="0";vm.EditedMinutes="3";vm.EditedSeconds="4";await vm.SaveQuantityCommand.ExecuteAsync(null);
            Check(vm.SelectedJob!.Target==184&&vm.SelectedJob.TargetLabel=="0时 3分 4秒"&&vm.RepairEnabled==globalRepair,"原地钓鱼时长可编辑且不改全局修理");
            vm.SelectedReserveItem=vm.ReserveChoices.First();vm.ReserveQuantity="0";vm.SetMaterialReserveCommand.Execute(null);vm.ThemeKey="light";Check(vm.SaveSettings(),"材料留存与主题保存");vm.ReloadSettingsCommand.Execute(null);Check(vm.MaterialReserves.Single().Quantity==0&&vm.ThemeKey=="light","零留存与浅色主题可重读");vm.SelectedReserve=vm.MaterialReserves.Single();vm.RemoveMaterialReserveCommand.Execute(null);Check(vm.SaveSettings()&&vm.MaterialReserves.Count==0,"删除个别留存后使用默认值");vm.ThemeKey="dark";vm.SaveSettings();
            vm.ChainEditor.Name="未保存内容";vm.ChainEditor.ConfirmDiscard=()=>false;vm.ChainEditor.SelectedChain=vm.ChainEditor.Chains.First();Check(vm.ChainEditor.Name=="未保存内容","切换任务链保护未保存修改");vm.ChainEditor.RestorePresetCommand.Execute(null);Check(vm.ChainEditor.Name=="未保存内容","恢复任务链预设前保护草稿");
            var anchor=vm.AnchorEditor!.Selected;vm.AnchorEditor.Fields!.Threshold="0.91";vm.AnchorEditor.ConfirmDiscard=()=>false;vm.AnchorEditor.Selected=vm.AnchorEditor.Anchors.First(a=>a.Id!=anchor!.Id);
            Check(vm.AnchorEditor.Selected==anchor&&vm.AnchorEditor.HasUnsavedChanges,"切换锚点保护未保存修改");var revision=vm.AnchorEditor.Revision;vm.AnchorEditor.RestorePresetCommand.Execute(null);Check(vm.AnchorEditor.Revision==revision&&vm.AnchorEditor.HasUnsavedChanges,"恢复预设前保护未保存锚点");vm.AnchorEditor.ConfirmDiscard=()=>true;vm.AnchorEditor.ReloadCommand.Execute(null);
            vm.AnchorEditor.SpecJson+=" ";vm.AnchorEditor.ConfirmDiscard=()=>false;vm.AnchorEditor.ReloadCommand.Execute(null);Check(vm.AnchorEditor.HasUnsavedChanges,"完整参数草稿受刷新保护");vm.AnchorEditor.ConfirmDiscard=()=>true;vm.AnchorEditor.ReloadCommand.Execute(null);
            vm.AnchorEditor.Search="不存在的锚点";Check(vm.AnchorEditor.AnchorView.IsEmpty&&vm.AnchorEditor.Selected is null&&vm.AnchorEditor.Fields is null,"锚点筛选清除隐藏选择及表单");vm.AnchorEditor.Search="";
            vm.AnchorEditor.Selected=vm.AnchorEditor.Anchors.First();vm.AnchorEditor.Fields!.Threshold="0.92";vm.AnchorEditor.ConfirmDiscard=()=>false;vm.AnchorEditor.Search="不存在的锚点";Check(vm.AnchorEditor.Search==""&&vm.AnchorEditor.HasUnsavedChanges,"锚点筛选先保护未保存修改");vm.AnchorEditor.ConfirmDiscard=()=>true;vm.AnchorEditor.ReloadCommand.Execute(null);
            foreach(var page in Enumerable.Range(0,3)){vm.SelectedPage=page;content.Measure(new Avalonia.Size(1120,800));content.Arrange(new Avalonia.Rect(0,0,1120,800));content.UpdateLayout();Check(content.Bounds.Width>0&&vm.PageDescription.Length>0,$"页面 {page+1} 可布局且有操作说明");}
            _=new SponsorWindow();Check(SponsorWindow.AfdianUrl.EndsWith("/minamichiwa")&&SponsorWindow.PatreonUrl.EndsWith("/chiwaminami"),"赞助沿用原作者页面且无支付动作");
            Check(Avalonia.Application.Current!.TryGetResource("CardBorder",Avalonia.Styling.ThemeVariant.Dark,out var card)&&card is Avalonia.Styling.ControlTheme,"Semi 卡片主题可解析");
            var native=Editors.CropSelectionWindow.NativeSelection(new Avalonia.Point(100,125),new Avalonia.Point(300,225),new Avalonia.Size(1000,800),new Avalonia.PixelSize(1280,960));
            Check(native==new Avalonia.PixelRect(128,128,256,128),"框选移除留白后转换原生像素");
            using(var image=Editors.ImagePixels.FromRgb(200,40,new byte[200*40*3]))
            {using var crop=Editors.ImagePixels.Crop(image,new Avalonia.PixelRect(10,5,80,20));Check(crop.PixelSize==new Avalonia.PixelSize(80,20)&&Editors.ImagePixels.Rgb(crop).Length==4800&&Editors.ImagePixels.Preview(crop).Length<=1048576,"图像裁剪与压缩保留原生尺寸");}
            vm.Search="";vm.SelectJob(vm.Jobs.First());vm.EditedQuantity="12345";
            var selectedQueueId=vm.SelectedJob!.Id;
            for(var tick=0;tick<6;tick++)
            {
                w.EditQueue(q=>q.Control(vm.Jobs.Last().Id,tick%2==0?"pause":"continue"));await vm.RefreshLiveQueueAsync();
                Check(vm.QueueView.Cast<JobRow>().Select(j=>j.Id).SequenceEqual(vm.Jobs.Where(j=>!j.Automatic).Select(j=>j.Id)),"连续进度刷新后人工视图完整 "+tick);
                Check(vm.AutomaticView.Cast<JobRow>().Count()==vm.Jobs.Count(j=>j.Automatic),"连续进度刷新后自动视图完整 "+tick);
            }
            Check(vm.SelectedJob?.Id==selectedQueueId&&vm.EditedQuantity=="12345","连续刷新保留任务选择与草稿");
            var queueFile=Path.Combine(directory,"work-queue.json");var validQueue=File.ReadAllBytes(queueFile);var visibleBefore=vm.QueueView.Cast<JobRow>().Select(j=>j.Id).ToArray();
            File.WriteAllText(queueFile,"{invalid");await vm.RefreshLiveQueueAsync();
            Check(vm.Error?.StartsWith("队列刷新失败")==true&&vm.QueueView.Cast<JobRow>().Select(j=>j.Id).SequenceEqual(visibleBefore),"读取失败保留已有队列显示");
            File.WriteAllBytes(queueFile,validQueue);await vm.RefreshLiveQueueAsync();
            File.Delete(queueFile);await vm.RefreshLiveQueueAsync();
            Check(vm.Error?.StartsWith("队列刷新失败")==true&&vm.QueueView.Cast<JobRow>().Select(j=>j.Id).SequenceEqual(visibleBefore),"队列文件暂缺仍保留任务");
            File.WriteAllBytes(queueFile,validQueue);await vm.RefreshLiveQueueAsync();Check(vm.Error is null,"队列恢复后刷新错误解除");
            w.EditQueue(q=>q.Control(vm.Jobs.Last().Id,"pause"));await vm.RefreshLiveQueueAsync();
            Check(vm.EditedQuantity=="12345"&&!vm.IsBusy,"后台进度刷新保留编辑草稿且不禁用界面");
            var dragged=vm.Jobs.First();var target=vm.Jobs.Last();await vm.ReorderAsync(dragged,target);
            Check(w.Read().Jobs.Last().Id==dragged.Id,"拖拽复用同组排序并持久保存");
            vm.QueueView.SortDescriptions.Add(Avalonia.Collections.DataGridSortDescription.FromPath(nameof(JobRow.Name)));
            var order=w.Read().Jobs.Select(j=>j.Id).ToArray();await vm.ReorderAsync(vm.Jobs.Last(),vm.Jobs.First());
            Check(w.Read().Jobs.Select(j=>j.Id).SequenceEqual(order),"表头排序时拒绝拖拽改执行顺序");vm.ResetSortCommand.Execute(null);
            passed.AddRange(await CheckSearchablePickers(previewWindow,vm));
            var gathering=vm.GatheringChoices.First();
            w.EditQueue(q=>q.Add("auto-material:self-check","gathering",gathering.ItemId,10,gathering.Id,[]));await vm.RefreshLiveQueueAsync();
            vm.Search="";vm.SelectedPage=0;previewWindow.Show();await Task.Delay(80);previewWindow.UpdateLayout();
            Check(previewWindow.Title==ProductInfo.Title,"标题包含产品名及版本");
            Check(previewWindow.FindControl<TextBlock>("AppName")!.Text==ProductInfo.Title,"顶栏响应式布局保持产品名及版本");
            async Task<T> WaitForDeferredControl<T>(string sectionName,string controlName) where T:Control
            {
                vm.SelectedPage=2;
                var section=previewWindow.FindControl<SettingsPanel>("SettingsPage")!.FindControl<DeferredSettingsSection>(sectionName)
                    ??throw new InvalidOperationException("设置页缺少延迟卡："+sectionName);
                for(var attempt=0;attempt<100;attempt++)
                {
                    section.BringIntoView();previewWindow.UpdateLayout();
                    var control=section.GetVisualDescendants().OfType<T>().FirstOrDefault(candidate=>candidate.Name==controlName&&candidate.IsEffectivelyVisible);
                    if(control is not null)return control;
                    await Task.Delay(20);
                }
                throw new InvalidOperationException($"设置卡 {sectionName} 未实现可见控件 {controlName}");
            }
            var newVip=previewWindow.FindControl<ToggleSwitch>("NewTaskVip")!;
            var settingsPage=previewWindow.FindControl<SettingsPanel>("SettingsPage")!;
            var settingsVip=await WaitForDeferredControl<CheckBox>("VipSettingsSection","MaintenanceVip");
            Check(!vm.LifeVip,"生活VIP默认关闭");
            newVip.IsChecked=true;await Task.Delay(40);
            Check(vm.LifeVip&&settingsVip.IsChecked==true&&vm.Recipes.All(r=>r.LifeVip),"新建VIP同步维护与配方时长");
            Check(vm.SaveSettings()&&DesktopPreferences.FromJson(w.ReadSettings()).LifeVip,"生活VIP持久保存");
            if(Environment.GetEnvironmentVariable("MABI_WORKBENCH_PREVIEW_DIR") is{} previewPath)
            {
                Directory.CreateDirectory(previewPath);
                vm.TaskKind="生产";vm.OpenAddCommand.Execute(null);await Task.Delay(300);previewWindow.UpdateLayout();
                using(var shot=new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)previewWindow.Bounds.Width,(int)previewWindow.Bounds.Height))){shot.Render(previewWindow);shot.Save(Path.Combine(previewPath,"vip-new-task.png"),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);}
                vm.CloseAddCommand.Execute(null);vm.SelectedPage=2;
                foreach(var scroll in previewWindow.FindControl<SettingsPanel>("SettingsPage")!.GetVisualDescendants().OfType<ScrollViewer>())scroll.Offset=default;
                await Task.Delay(300);previewWindow.UpdateLayout();
                using(var shot=new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)previewWindow.Bounds.Width,(int)previewWindow.Bounds.Height))){shot.Render(previewWindow);shot.Save(Path.Combine(previewPath,"vip-settings.png"),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);}
            }
            settingsVip.IsChecked=false;await Task.Delay(40);
            Check(!vm.LifeVip&&newVip.IsChecked==false&&vm.Recipes.All(r=>!r.LifeVip),"维护VIP关闭同步新建与配方时长");
            Check(vm.SaveSettings(),"生活VIP恢复默认");
            vm.SelectedPage=2;
            var lowCpuToggle=await WaitForDeferredControl<CheckBox>("AppearanceSettingsSection","LowCpuRecognitionToggle");
            passed.Add("设置页完成布局并显示识别低占用开关");
            Check(lowCpuToggle.IsVisible&&lowCpuToggle.GetVisualAncestors().OfType<SettingsPanel>().Single().IsVisible&&lowCpuToggle.IsEnabled&&lowCpuToggle.IsChecked==vm.RecognitionLowCpu,"识别低占用开关可用且显示当前设置");
            lowCpuToggle.SetCurrentValue(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty,true);await Task.Delay(20);
            Check(vm.RecognitionLowCpu&&vm.SettingsDirty,"开启低占用模式经界面绑定标记待保存");
            Check(vm.SaveSettings()&&JsonNode.Parse(File.ReadAllText(path))!["recognition"]!["low_cpu"]!.GetValue<bool>(),"低占用模式开启写入隔离设置文件");
            vm.RecognitionLowCpu=false;await Task.Delay(20);
            Check(lowCpuToggle.IsChecked==false&&vm.SettingsDirty,"低占用模型修改同步到开关");
            vm.ReloadSettingsCommand.Execute(null);await Task.Delay(20);
            Check(vm.RecognitionLowCpu&&lowCpuToggle.IsChecked==true&&!vm.SettingsDirty,"放弃低占用草稿后恢复已保存开关状态");
            lowCpuToggle.SetCurrentValue(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty,false);await Task.Delay(20);
            Check(!vm.RecognitionLowCpu&&vm.SettingsDirty&&vm.SaveSettings()&&!JsonNode.Parse(File.ReadAllText(path))!["recognition"]!["low_cpu"]!.GetValue<bool>(),"低占用模式关闭经界面绑定并保存");
            vm.ReloadSettingsCommand.Execute(null);await Task.Delay(20);
            Check(!vm.RecognitionLowCpu&&lowCpuToggle.IsChecked==false&&!vm.SettingsDirty,"关闭低占用模式重读后保持关闭");
            var schemePicker=await WaitForDeferredControl<StackPanel>("OperationSettingsSection","GlobalInputSchemePicker");
            var mouseInputChoice=schemePicker.GetVisualDescendants().OfType<RadioButton>().Single(c=>c.Name=="MouseInputChoice");
            var touchInputChoice=schemePicker.GetVisualDescendants().OfType<RadioButton>().Single(c=>c.Name=="TouchInputChoice");
            var centerToggle=await WaitForDeferredControl<CheckBox>("OperationSettingsSection","GlobalTouchCenterToggle");
            Check(vm.InputScheme=="touch"&&vm.TouchCenterOffset&&centerToggle.IsChecked==true&&touchInputChoice.IsChecked==true,"旧设置默认后台触控及全局中心偏移");
            mouseInputChoice.SetCurrentValue(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty,true);await Task.Delay(20);
            Check(vm.InputScheme=="mouse"&&!vm.IsTouchInput&&vm.SettingsDirty&&vm.SaveSettings(),"切换前台鼠标通过实际控件保存");
            Check(JsonNode.Parse(File.ReadAllText(path))!["input"]!["scheme"]!.GetValue<string>()=="mouse","前台方案写入隔离设置");
            touchInputChoice.SetCurrentValue(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty,true);await Task.Delay(20);centerToggle.SetCurrentValue(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty,false);await Task.Delay(20);
            Check(vm.IsTouchInput&&!vm.TouchCenterOffset&&vm.SaveSettings(),"切回触控并保存中心偏移关闭");
            centerToggle.SetCurrentValue(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty,true);await Task.Delay(20);Check(vm.TouchCenterOffset&&vm.SaveSettings(),"中心偏移开启写入全局设置");
            vm.ReloadSettingsCommand.Execute(null);await Task.Delay(20);
            Check(vm.InputScheme=="touch"&&vm.TouchCenterOffset&&centerToggle.IsChecked==true&&!vm.SettingsDirty,"方案及全局中心偏移重读一致");
            settingsPage.ShowUpdateSection();await Task.Delay(80);previewWindow.UpdateLayout();
            var updateSection=settingsPage.FindControl<DeferredSettingsSection>("UpdateSettingsSection")!;
            Check(updateSection.IsBuilt&&updateSection.IsEffectivelyVisible&&updateSection.GetVisualDescendants().OfType<Border>().Any(c=>c.IsEffectivelyVisible&&c.Name=="UpdateCard"),"标题更新操作可靠实现并滚动到设置更新卡");
            vm.SelectedPage=0;content.UpdateLayout();
            var queuePanel=previewWindow.GetVisualDescendants().OfType<QueuePanel>().Single();
            foreach(var expander in queuePanel.GetVisualDescendants().OfType<Expander>())expander.IsExpanded=true;
            previewWindow.UpdateLayout();await Task.Delay(40);
            var manualGrid=previewWindow.GetVisualDescendants().OfType<DataGrid>().Single(g=>g.Name=="ManualGrid");
            var automaticGrid=previewWindow.GetVisualDescendants().OfType<DataGrid>().Single(g=>g.Name=="AutomaticGrid");
            var snapshotRows=vm.Jobs.Select(j=>j with{}).ToArray();var snapshotSelected=snapshotRows.First(j=>!j.Automatic);
            vm.SelectJob(snapshotSelected);vm.EditedQuantity="12345";
            vm.QueueView.SortDescriptions.Add(Avalonia.Collections.DataGridSortDescription.FromPath(nameof(JobRow.Name)));
            for(var tick=0;tick<16;tick++)
            {
                vm.QueueView.MoveCurrentToLast();vm.AutomaticView.MoveCurrentToLast();
                vm.SelectJob(vm.Jobs.First(j=>j.Id==snapshotSelected.Id));vm.EditedQuantity="12345";
                var changed=snapshotRows.Reverse().Select((j,i)=>j with{Status="刷新 "+tick,Order=i+1,Automatic=j.Id==snapshotSelected.Id?false:(i+tick)%2==0}).ToList();
                if(tick%2==0)changed.Add(snapshotSelected with{Id="transient:test",Name="临时任务",Automatic=false});
                if(tick%3==0)changed.RemoveAll(j=>j.Id==snapshotRows.Last().Id&&j.Id!=snapshotSelected.Id);
                vm.Apply(new(changed.ToArray(),vm.Recipes.ToArray(),w.Read().Gathering),true);
                previewWindow.UpdateLayout();await Task.Delay(10);
                Check(vm.QueueView.Cast<JobRow>().Count()==changed.Count(j=>!j.Automatic)&&vm.AutomaticView.Cast<JobRow>().Count()==changed.Count(j=>j.Automatic),"选中及排序时原子增删重排分类完整 "+tick);
                Check(vm.SelectedJob?.Id==snapshotSelected.Id&&manualGrid.SelectedItem is JobRow row&&row.Id==snapshotSelected.Id&&vm.EditedQuantity=="12345","真实表格原子刷新保留选择和草稿 "+tick);
            }
            vm.Apply(new(snapshotRows,vm.Recipes.ToArray(),w.Read().Gathering),true);vm.ResetSortCommand.Execute(null);
            var recoveryBase=new MabiAgent.Workflows.AutomationStatus(false,"旧状态",null,new Dictionary<string,string>(),1);
            var failedRecovery=AutomationService.RecoveryStatus(recoveryBase,System.Text.Json.Nodes.JsonNode.Parse("{\"status\":\"blocked\",\"phase\":\"verify_owner\",\"reason\":\"确认当前角色超时\"}")!.AsObject(),"relogin_blocked");
            Check(!failedRecovery.Running&&failedRecovery.Message=="确认当前角色超时"&&failedRecovery.PauseReason=="relogin_blocked","恢复失败准确呈现终止原因");
            var runningRecovery=AutomationService.RecoveryStatus(recoveryBase,System.Text.Json.Nodes.JsonNode.Parse("{\"status\":\"running\",\"phase\":\"sync\"}")!.AsObject(),null);
            Check(runningRecovery.Message.Contains("等待完整库存同步")&&runningRecovery.PauseReason is null,"恢复运行显示阶段而非旧暂停原因");
            w.EditQueue(q=>q.Add("auto-material:self-check","gathering",gathering.ItemId,10,gathering.Id,[]));await vm.RefreshLiveQueueAsync();
            vm.SelectJob(vm.Jobs.First(j=>!j.Automatic));vm.EditedQuantity="12345";
            for(var tick=0;tick<6;tick++)
            {
                var last=vm.Jobs.Last(j=>!j.Automatic);w.EditQueue(q=>q.Control(last.Id,tick%2==0?"pause":"continue"));await vm.RefreshLiveQueueAsync();
                previewWindow.UpdateLayout();await Task.Delay(30);
                Check(manualGrid.ItemsSource.Cast<JobRow>().Count()==vm.ManualCount&&manualGrid.GetVisualDescendants().OfType<DataGridRow>().Any(r=>r.IsEffectivelyVisible&&r.Bounds.Height>0),"实际任务表格持续刷新仍有可见行 "+tick);
                Check(automaticGrid.ItemsSource.Cast<JobRow>().Count()==vm.AutomaticCount&&automaticGrid.GetVisualDescendants().OfType<DataGridRow>().Any(r=>r.IsEffectivelyVisible&&r.Bounds.Height>0),"实际自动任务表格与源一致且可见 "+tick);
                Check(vm.EditedQuantity=="12345","实际表格刷新不改草稿 "+tick);
            }
            vm.QueueView.SortDescriptions.Add(Avalonia.Collections.DataGridSortDescription.FromPath(nameof(JobRow.Name)));
            vm.Search="已暂停";await vm.RefreshLiveQueueAsync();previewWindow.UpdateLayout();
            Check(!vm.QueueView.IsEmpty&&vm.QueueView.Cast<JobRow>().All(j=>j.Status.Contains("已暂停"))&&vm.QueueView.SortDescriptions.Count==1,"实际任务表格排序筛选结果一致："+vm.QueueView.Count+" / "+vm.QueueView.SortDescriptions.Count+" / "+string.Join(",",vm.QueueView.Cast<JobRow>().Select(j=>j.Status)));vm.Search="";vm.ResetSortCommand.Execute(null);
            foreach(var size in new[]{new Avalonia.Size(1000,700),new Avalonia.Size(1340,920)})
            {
                previewWindow.Width=size.Width;previewWindow.Height=size.Height;vm.SelectJob(vm.Jobs.First(j=>!j.Automatic));
                previewWindow.UpdateLayout();await Task.Delay(30);
                Check(manualGrid.Bounds.Height>=160&&manualGrid.GetVisualDescendants().OfType<DataGridRow>().Any(r=>r.Bounds.Height>0),$"窗口 {size.Width}×{size.Height} 显示批量操作及自动任务时主列表保留高度");
            }

            passed.AddRange(await PresentationSelfCheck.Run(previewWindow,vm));
            passed.AddRange(await ScrollUiSelfCheck.Run(root));
            passed.AddRange(await MemoryUiSelfCheck.Run(root));
            var originalTheme=vm.ThemeKey;vm.ThemeKey="dark";vm.SaveSettings();await Task.Delay(40);
            var themeButton=previewWindow.FindControl<Button>("ThemeToggle")!;
            themeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));await Task.Delay(80);
            Check(vm.ThemeKey=="light"&&DesktopPreferences.FromJson(w.ReadSettings()).Theme=="light","主界面一键亮色主题同步维护设置并保存");
            themeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));await Task.Delay(500);
            Check(vm.ThemeKey=="dark"&&DesktopPreferences.FromJson(w.ReadSettings()).Theme=="dark","主界面一键暗色主题支持连续切换并完成旋转");
            vm.ThemeKey=originalTheme;vm.SaveSettings();
            vm.RuntimeIssues="铁锭：任务异常，等待显式继续\n维护：报价待核实";previewWindow.UpdateLayout();
            var problem=previewWindow.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e=>e.Header?.ToString()=="待处理问题");
            Check(problem is{IsVisible:true},"隔离问题在运行状态中可展开查看");problem!.IsExpanded=true;previewWindow.UpdateLayout();
            Check(previewWindow.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.IsEffectivelyVisible&&t.Text==vm.RuntimeIssues),"隔离任务与依赖问题完整显示");vm.RuntimeIssues="";
            vm.PrepareClose();Check(!vm.StartCommand.CanExecute(null),"关闭准备后禁止重新开始");
            return passed.ToArray();
        }
        finally{if(vm.ChainEditor?.Current is {} current)vm.ChainEditor.Load(current);if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}






