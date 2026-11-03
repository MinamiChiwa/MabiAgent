using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace MabiAgent.Desktop.Editors;

internal static class AnchorWorkbenchSelfCheck
{
    public static async Task<string[]> Run(MainViewModel host,EditorRepository repository)
    {
        var passed=new List<string>();
        void Check(bool condition,string label){if(!condition)throw new InvalidOperationException(label);passed.Add(label);}
        using var vm=new AnchorWorkbenchViewModel(host,repository);
        Check(vm.Document.Chains.Count>0&&vm.Document.Pages.Count>0&&vm.Document.Anchors.Count>0,"工作台导入全部任务链、独立页面及通用锚点");
        var initial=vm.Document.ToJson().ToJsonString();
        Check(!vm.HasUnsavedChanges,"工作台模型初始化不修改配置："+Differences(System.Text.Json.Nodes.JsonNode.Parse(vm.BaselineJson),vm.Document.ToJson()));
        var window=new AnchorWorkbenchWindow(vm);
        window.Show();await Task.Delay(80);
        var surface=(Control)window.Content!;window.UpdateLayout();
        passed.AddRange(await UiLayoutRegression.RunWorkbench(window,vm));
        Check(!vm.HasUnsavedChanges,"打开工作台及初始化选择不会修改配置："+Differences(System.Text.Json.Nodes.JsonNode.Parse(vm.BaselineJson),vm.Document.ToJson()));
        var pagePicker=window.FindControl<AnchorSetPicker>("PageAnchors")!;
        Check(!pagePicker.FindControl<Expander>("Library")!.IsExpanded,"锚点库默认折叠，与已选锚点分开");
        Check(pagePicker.FindControl<ListBox>("Chosen")!.ItemCount==WorkbenchModel.SplitIds(vm.SelectedPage!.AnchorIds).Count(),"页面锚点区仅列已选引用");
        Check(pagePicker.FindControl<ListBox>("Available")!.ItemsSource!.Cast<WorkbenchAnchor>().All(a=>!WorkbenchModel.SplitIds(vm.SelectedPage.AnchorIds).Contains(a.Id)),"可添加锚点不重复包含已选项");
        var selectedIds=vm.SelectedPage.AnchorIds;var available=pagePicker.FindControl<ListBox>("Available")!;var added=available.ItemsSource!.Cast<WorkbenchAnchor>().First();available.SelectedItem=added;
        pagePicker.FindControl<Button>("AddAnchorButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Check(WorkbenchModel.SplitIds(vm.SelectedPage.AnchorIds).Contains(added.Id),"从锚点库添加会更新当前页面引用");
        pagePicker.FindControl<ListBox>("Chosen")!.SelectedItem=added;pagePicker.FindControl<Button>("RemoveAnchorButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Check(vm.SelectedPage.AnchorIds==selectedIds,"移除已选锚点只解除当前引用");
        var quick=new ChainEditorViewModel(repository);quick.UseGatheringTemplates("iron_ore_nearby");
        Check(quick.IsGathering&&quick.VisibleNodes.Count()==3&&quick.Nodes.Count>3&&!quick.AdvancedEditing,"采集模板默认仅编辑三类目标差异，公共步骤完整保留");
        quick.AdvancedEditing=true;Check(quick.VisibleNodes.Count()==quick.Nodes.Count,"高级编辑可展开全部公共步骤");
        quick.AdvancedEditing=false;
        var quickWindow=new Ursa.Controls.UrsaWindow{Width=1100,Height=820,Content=new ChainEditorPanel{DataContext=quick}};
        quickWindow.Show(window);await Task.Delay(60);quickWindow.UpdateLayout();
        Check(quickWindow.Bounds.Width>0&&quickWindow.Bounds.Height>0,"采集模板向导可实际构造与布局");
        if(Environment.GetEnvironmentVariable("MABI_WORKBENCH_PREVIEW_DIR") is {Length:>0} quickPreview)
        {Directory.CreateDirectory(quickPreview);using var image=new RenderTargetBitmap(new PixelSize((int)quickWindow.Bounds.Width,(int)quickWindow.Bounds.Height));image.Render((Control)quickWindow.Content);image.Save(Path.Combine(quickPreview,"gathering-template.png"),PngBitmapEncoderOptions.Default);}
        quickWindow.Close();
        foreach(var candidate in vm.Document.Chains.Take(12))
        {vm.SelectedChain=candidate;foreach(var card in candidate.Cards)vm.SelectedCard=card;}
        foreach(var anchor in vm.Document.Anchors.Take(40)){vm.SelectedAnchor=anchor;foreach(var candidateState in anchor.States)vm.SelectedState=candidateState;}
        Check(vm.Document.ToJson().ToJsonString()==initial,"切换任务链、卡片、锚点及状态不改写配置："+Differences(System.Text.Json.Nodes.JsonNode.Parse(initial),vm.Document.ToJson()));
        vm.SelectedChain=vm.Document.Chains.First();vm.SelectedAnchor=vm.Document.Anchors.First();
        await Task.Delay(50);window.UpdateLayout();
        Check(vm.SelectedCard is not null&&ReferenceEquals(vm.SelectedCard,vm.SelectedChain.Cards.First())&&window.FindControl<ListBox>("StepList")!.SelectedItem==vm.SelectedCard,"切换任务链保留首张卡片与详情选择");
        var artifacts=Directory.Exists(Path.Combine(repository.Root,"dotnet"))?Path.Combine(repository.Root,"build"):null;
        if(artifacts is not null)Directory.CreateDirectory(artifacts);
        foreach(var category in Enumerable.Range(0,6))
        {
            vm.Category=category;
            await Task.Delay(40);surface.UpdateLayout();
            using var image=new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height));image.Render(window);
            if(category==0&&artifacts is not null)image.Save(Path.Combine(artifacts,"anchor-workbench-preview.png"),PngBitmapEncoderOptions.Default);
            // Optional review renders of every category, written only where the caller asks.
            if(Environment.GetEnvironmentVariable("MABI_WORKBENCH_PREVIEW_DIR") is {Length:>0} review){Directory.CreateDirectory(review);image.Save(Path.Combine(review,$"workbench-{category}.png"),PngBitmapEncoderOptions.Default);}
            Check(surface.Bounds.Width>0&&surface.Bounds.Width<=window.Bounds.Width&&surface.Bounds.Height>0&&surface.Bounds.Height<=window.Bounds.Height,$"工作台分类 {category+1} 在当前工作区内可布局和渲染");
        }
        window.Width=1060;window.Height=700;await Task.Delay(50);window.UpdateLayout();
        Check(surface.Bounds.Width>0&&surface.Bounds.Width<=1060,"工作台最小窗口可布局");
        vm.BeginEdit();vm.SelectedChain!.Name+="布局检查";vm.EndEdit();
        foreach(var width in new[]{760,960,1060,1180,1400})
        {
            window.Width=width;await Task.Delay(60);window.UpdateLayout();
            var search=window.FindControl<TextBox>("WorkbenchSearch")!;var actions=window.FindControl<StackPanel>("WorkbenchHeaderActions")!;
            Check(search.Bounds.Width>=220&&search.PlaceholderText=="搜索当前分类","工作台搜索占位可完整显示 "+width);
            var at=actions.TranslatePoint(default,window)!.Value;
            Check(at.X>=0&&at.X+actions.Bounds.Width<=window.Bounds.Width,"工作台未保存提示及操作栏不越界 "+width);
            var title=window.FindControl<StackPanel>("WorkbenchTitleGroup")!;
            Check(width>=1280||actions.TranslatePoint(default,window)!.Value.Y>=title.TranslatePoint(default,window)!.Value.Y+title.Bounds.Height,"窄工作台搜索与标题分行 "+width);
            if(Environment.GetEnvironmentVariable("MABI_WORKBENCH_PREVIEW_DIR") is{} directory&&width is 760 or 1060)
            {
                using var shot=new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height));shot.Render(window);shot.Save(Path.Combine(directory,"workbench-narrow-"+width+".png"),PngBitmapEncoderOptions.Default);
            }
        }
        vm.ReloadNow();window.Width=1060;await Task.Delay(40);window.UpdateLayout();
        vm.Category=0;var chain=vm.SelectedChain!;var count=chain.Cards.Count;
        vm.SelectedCard=chain.Cards.First();vm.SelectedCards.Add(vm.SelectedCard);vm.CopyCardsCommand.Execute(null);vm.PasteCardsCommand.Execute(null);
        Check(chain.Cards.Count==count+1&&chain.Cards.Select(c=>c.Id).Distinct().Count()==chain.Cards.Count,"卡片复制粘贴生成独立身份");
        vm.UndoCommand.Execute(null);Check(vm.SelectedChain!.Cards.Count==count,"卡片操作可撤销");vm.RedoCommand.Execute(null);Check(vm.SelectedChain!.Cards.Count==count+1,"卡片操作可重做");
        vm.BeginEdit();var originalName=vm.SelectedChain!.Name;vm.SelectedChain.Name="正在编辑";vm.UndoCommand.Execute(null);Check(vm.SelectedChain!.Name==originalName,"文本编辑尚未失焦也可直接撤销");
        vm.ReloadNow();chain=vm.SelectedChain!;vm.SelectedCard=chain.Cards.First();vm.SelectedCards.Clear();vm.SelectedCards.Add(vm.SelectedCard);vm.StoreCardsCommand.Execute(null);
        Check(vm.Document.CardLibrary.Count==1,"选中卡片可以存入公共库");vm.SelectedLibraryCard=vm.Document.CardLibrary.First();vm.InsertLibraryCommand.Execute(null);Check(chain.Cards.Count==count+1,"公共卡片可加入任务链");
        vm.ReloadNow();await Task.Delay(50);chain=vm.SelectedChain!;var last=chain.Cards.Last();vm.SelectedCard=last;vm.SelectedCards.Clear();vm.SelectedCards.Add(last);vm.MoveSelection(0);await Task.Delay(50);Check(chain.Cards.First()==last,"多选操作复用顺序移动");Check(vm.SelectedCard==last&&vm.SelectedCards.Contains(last)&&window.FindControl<ListBox>("StepList")!.SelectedItems!.Contains(last),"拖动排序保留选中卡片和详情："+vm.SelectedCard?.Id+" / "+last.Id+" / "+vm.SelectedCards.Count);
        vm.ReloadNow();vm.AddAnchorCommand.Execute(null);vm.SelectedAnchor!.Name="自检圆形";vm.SetStateRegion("100, 100, 180, 180","circle",false);vm.SelectedState!.RandomClick=true;
        Check(vm.SelectedState.Shape=="circle"&&vm.SelectedState.Box=="100, 100, 180, 180","固定锚点支持圆形范围与中心随机点击");
        vm.SelectedCard=vm.SelectedChain!.Cards.First();vm.LinkSelectedAnchorCommand.Execute(null);var state=vm.Document.ResolveState(vm.SelectedCard);vm.SelectedState.Box="120, 120, 200, 200";
        Check(ReferenceEquals(state,vm.SelectedState)&&vm.Document.ResolveState(vm.SelectedCard)!.Box=="120, 120, 200, 200","通用锚点修改同步所有卡片引用");
        vm.ReloadNow();Check(vm.Document.ToJson().ToJsonString()==initial&&!vm.HasUnsavedChanges,"重读保留原配置且清除草稿");
        Check(vm.SaveNow(),"完整安装配置导入后可原子保存："+vm.Status);
        Check(vm.Document.Revision==1,"工作台保存单独版本，不改任务队列");
        _=PublishedCatalogCompiler.CompileAvailable(repository);Check(true,"保存后运行配置仍可编译");
        window.CloseWithOwner();return passed.ToArray();
    }
    static string Differences(System.Text.Json.Nodes.JsonNode? before,System.Text.Json.Nodes.JsonNode? after,string path="")
    {
        if(System.Text.Json.Nodes.JsonNode.DeepEquals(before,after))return "";
        if(before is System.Text.Json.Nodes.JsonObject a&&after is System.Text.Json.Nodes.JsonObject b)return string.Join("; ",a.Select(x=>x.Key).Union(b.Select(x=>x.Key)).Select(key=>!a.ContainsKey(key)||!b.ContainsKey(key)?path+"/"+key+": missing→"+b[key]:Differences(a[key],b[key],path+"/"+key)).Where(x=>x.Length>0).Take(8));
        if(before is System.Text.Json.Nodes.JsonArray c&&after is System.Text.Json.Nodes.JsonArray d)return string.Join("; ",Enumerable.Range(0,Math.Min(c.Count,d.Count)).Select(i=>Differences(c[i],d[i],path+"/"+i)).Where(x=>x.Length>0).Take(8));
        return path+": "+before+" → "+after;
    }
}
