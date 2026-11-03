using System.ComponentModel;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MabiAgent.Desktop.Controls;
using Ursa.Controls;
using NumericUpDown=Avalonia.Controls.NumericUpDown;

namespace MabiAgent.Desktop.Editors;

public partial class AnchorWorkbenchWindow:UrsaWindow
{
    static readonly (string Title,string Purpose)[] Categories=
    [
        ("任务链","按顺序排列的步骤卡片；选中步骤后在右侧设置目标与完成条件。"),
        ("页面识别","用稳定图片确认当前处于哪个页面，并记录点击后会到达的页面。"),
        ("锚点库","可被步骤引用的点击目标或识别图片；修改一次，所有引用同步。"),
        ("公共卡片","收藏可复用的步骤，随时插入到任意任务链。"),
        ("调度参数","常用调度设置与全部策略参数；保存后在下次运行生效。"),
        ("测试记录","全流程或选中步骤测试的逐步结果。"),
    ];
    readonly AnchorWorkbenchViewModel vm;
    bool syncing,closingWithOwner,compactHeight;WorkbenchCard? watchedCard;
    public AnchorWorkbenchWindow(AnchorWorkbenchViewModel model)
    {
        InitializeComponent();vm=model;AnchorImageCache.Attach(model.Editor.ResolveImagePath);
        syncing=true;DataContext=model;syncing=false;
        SizeChanged+=(_,_)=>UpdateResponsiveLayout();
        StateEditor.SizeChanged+=(_,_)=>UpdateStateEditor();
        Opened+=(_,_)=>FitWorkingArea();
        vm.PropertyChanged+=Changed;
        vm.CardsReordered+=RestoreCardSelection;
        _=new RowDragReorder(StepList,v=>v.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault(),
            v=>v.GetSelfAndVisualAncestors().All(x=>x is not (TextBox or ComboBox or Button)),
            item=>!vm.Testing&&item is WorkbenchCard,
            ()=>vm.SelectedChain?.Cards.Cast<object>().ToArray()??[],item=>StepList.ScrollIntoView(item),DropCard);
        AddHandler(GotFocusEvent,(_,e)=>{if(e.Source is TextBox or ComboBox or NumericUpDown)vm.BeginEdit();},RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent,(_,e)=>{if(e.Source is TextBox or ComboBox or NumericUpDown)vm.EndEdit();},RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent,(_,_)=>{vm.EndEdit();vm.BeginEdit();},RoutingStrategies.Tunnel,true);
        StepList.AddHandler(PointerPressedEvent,CardPressed,RoutingStrategies.Tunnel,true);
        // Step numbers follow the container index, which also tracks reorders.
        StepList.ContainerPrepared+=(_,e)=>e.Container.Tag=(e.Index+1).ToString();
        StepList.ContainerIndexChanged+=(_,e)=>e.Container.Tag=(e.NewIndex+1).ToString();
        Closed+=(_,_)=>{vm.StopTestCommand.Execute(null);vm.PropertyChanged-=Changed;vm.CardsReordered-=RestoreCardSelection;WatchCard(null);};
        UpdateHeader();WatchCard(vm.SelectedCard);UpdateInspector();SyncSelectionEditors();
    }
    void FitWorkingArea()
    {
        WindowSizing.FitToWorkingArea(this,Owner as Window);
        UpdateResponsiveLayout();
    }
    internal void UpdateResponsiveLayout(Size? viewport=null)
    {
        var size=viewport??Bounds.Size;
        var compact=size.Width<1180;
        WorkbenchShell.ColumnDefinitions[0].Width=new GridLength(compact?0:200);
        WorkbenchSidebar.IsVisible=!compact;CompactCategory.IsVisible=compact;
        WorkbenchHeading.IsVisible=!compact||size.Width>=960;
        WorkbenchSearch.Width=compact?168:240;
        WorkbenchBody.Margin=compact?new Thickness(16,0,16,12):new Thickness(24,0,24,14);
        ChainLayout.ColumnDefinitions[0].Width=new GridLength(compact?0:232);
        ChainLayout.ColumnDefinitions[1].Width=new GridLength(compact?0:16);
        ChainLayout.ColumnDefinitions[3].Width=new GridLength(compact?12:16);
        ChainLayout.ColumnDefinitions[4].Width=new GridLength(compact?300:340);
        ChainSidebar.IsVisible=!compact;CompactChainRow.IsVisible=compact;
        PageLayout.ColumnDefinitions[0].Width=new GridLength(compact?200:232);
        AnchorLayout.ColumnDefinitions[0].Width=new GridLength(compact?220:272);
        var shortWindow=size.Height<650;
        if(shortWindow!=compactHeight){compactHeight=shortWindow;ChainHintText.IsVisible=!shortWindow;}
        UpdateStateEditor();
    }
    // The state editor shows preview and fields side by side when there is room, stacked otherwise.
    void UpdateStateEditor()
    {
        var stacked=StateEditor.Bounds.Width is >0 and <640;
        StateEditor.ColumnDefinitions[1].Width=new GridLength(stacked?0:16);
        StateEditor.ColumnDefinitions[2].Width=stacked?new GridLength(0):new GridLength(300);
        if(StateEditor.RowDefinitions.Count<2){StateEditor.RowDefinitions.Add(new RowDefinition(GridLength.Auto));StateEditor.RowDefinitions.Add(new RowDefinition(GridLength.Auto));}
        Grid.SetColumn(StateFieldsColumn,stacked?0:2);Grid.SetRow(StateFieldsColumn,stacked?1:0);
        StateFieldsColumn.Margin=new Thickness(0,stacked?12:0,0,0);
    }
    public void FocusSearch(){WorkbenchSearch.Focus();WorkbenchSearch.SelectAll();}
    public void CloseWithOwner(){closingWithOwner=true;Close();}
    protected override async Task<bool> CanClose()
    {
        if(closingWithOwner)return true;
        vm.EndEdit();
        if(vm.Testing){vm.StopTestCommand.Execute(null);vm.Status="正在停止测试，请稍后关闭。";return false;}
        if(!vm.HasUnsavedChanges)return true;
        var answer=await MessageBox.ShowAsync(this,"保存工作台修改后关闭？","未保存修改",MessageBoxIcon.Warning,MessageBoxButton.YesNoCancel);
        if(answer==MessageBoxResult.Cancel)return false;
        if(answer==MessageBoxResult.Yes)return vm.SaveNow();
        if(answer==MessageBoxResult.No){vm.ReloadNow();return true;}
        return false;
    }
    void Changed(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(vm.SelectedChain) or nameof(vm.Document))
        {
            var chain=vm.SelectedChain;
            Avalonia.Threading.Dispatcher.UIThread.Post(()=>
            {if(ReferenceEquals(chain,vm.SelectedChain)){StepList.SelectedItem=vm.SelectedCard;SyncSelectionEditors();}});
        }
        if(e.PropertyName is nameof(vm.SelectedCard) or nameof(vm.Document)){WatchCard(vm.SelectedCard);UpdateInspector();}
        if(e.PropertyName is nameof(vm.SelectedCard) or nameof(vm.SelectedAnchor) or nameof(vm.SelectedPage) or nameof(vm.Document))SyncSelectionEditors();
        if(e.PropertyName is nameof(vm.SelectedState) or nameof(vm.Document))AnchorTestText.Text="";
        if(e.PropertyName==nameof(vm.Category)){UpdateHeader();Avalonia.Threading.Dispatcher.UIThread.Post(()=>{if(CurrentLayout() is {} layout)Motion.FadeIn(layout);},Avalonia.Threading.DispatcherPriority.Loaded);}
    }
    Control? CurrentLayout()=>vm.Category switch{0=>ChainLayout,1=>PageLayout,2=>AnchorLayout,_=>null};
    void UpdateHeader()
    {
        var (title,purpose)=Categories[Math.Clamp(vm.Category,0,Categories.Length-1)];
        CategoryTitle.Text=title;CategoryPurpose.Text=purpose;ToolTip.SetTip(CategoryPurpose,purpose);
    }
    void WatchCard(WorkbenchCard? card)
    {
        if(ReferenceEquals(card,watchedCard))return;
        if(watchedCard is not null)watchedCard.PropertyChanged-=CardChanged;
        watchedCard=card;if(card is not null)card.PropertyChanged+=CardChanged;
    }
    void CardChanged(object? sender,PropertyChangedEventArgs e){if(e.PropertyName is nameof(WorkbenchCard.Action) or null or "")UpdateInspector();}
    // Only the sections that apply to the selected action are shown.
    void UpdateInspector()
    {
        var card=vm.SelectedCard;var action=card?.Action;
        InspectorEmpty.IsVisible=card is null;InspectorBody.IsVisible=card is not null;
        TargetSection.IsVisible=action is not ("key" or "text" or "delay");
        KeySection.IsVisible=action is "key" or "text";
        ScrollSection.IsVisible=action is "scroll" or "drag";
    }
    void SyncSelectionEditors()
    {
        if(syncing)return;syncing=true;
        try
        {
            CardStatePicker.ItemsSource=vm.Document.FindAnchor(vm.SelectedCard?.AnchorId??"")?.States;
            CardStatePicker.SelectedValue=vm.SelectedCard?.StateId;



        }
        finally{syncing=false;}
    }
    void CardsSelected(object? sender,SelectionChangedEventArgs e)
    {if(syncing||DataContext is not AnchorWorkbenchViewModel model)return;if(StepList.SelectedItem is WorkbenchCard card&&model.SelectedChain?.Cards.Contains(card)==true)model.SelectedCard=card;model.SelectedCards.Clear();foreach(var c in StepList.SelectedItems?.OfType<WorkbenchCard>()??[])model.SelectedCards.Add(c);SyncSelectionEditors();}
    void RestoreCardSelection()
    {
        syncing=true;
        try{StepList.SelectedItems?.Clear();foreach(var card in vm.SelectedCards)StepList.SelectedItems?.Add(card);}
        finally{syncing=false;}
        SyncSelectionEditors();
    }
    void KeyChoiceChanged(object? sender,SelectionChangedEventArgs e)
    {
        if(syncing||DataContext is not AnchorWorkbenchViewModel||sender is not ComboBox picker||picker.Tag is not string path||picker.SelectedValue is not string value)return;
        var parts=path.Split('.');
        var model=parts.Length==1?picker.DataContext as WorkbenchModel:parts[0] switch
        {nameof(vm.SelectedCard)=>vm.SelectedCard,nameof(vm.SelectedChain)=>vm.SelectedChain,nameof(vm.SelectedAnchor)=>vm.SelectedAnchor,nameof(vm.SelectedState)=>vm.SelectedState,nameof(vm.SelectedPage)=>vm.SelectedPage,_=>null};
        var property=model?.GetType().GetProperty(parts[^1]);
        if(property?.CanWrite!=true||property.PropertyType!=typeof(string)||Equals(property.GetValue(model),value))return;
        vm.BeginEdit();try{property.SetValue(model,value);
        if(ReferenceEquals(model,vm.SelectedChain)&&parts[^1]==nameof(WorkbenchChain.RuntimeKey))vm.ApplyRuntimeChoice();
        if(ReferenceEquals(model,vm.SelectedCard)&&parts[^1]==nameof(WorkbenchCard.PageId))vm.RefreshCardChoices();
        if(ReferenceEquals(model,vm.SelectedCard)&&parts[^1]==nameof(WorkbenchCard.Action))UpdateInspector();
        if(ReferenceEquals(model,vm.SelectedCard)&&parts[^1]==nameof(WorkbenchCard.TransitionId)){vm.ApplyTransitionChoice();SyncSelectionEditors();}
        if(ReferenceEquals(model,vm.SelectedCard)&&parts[^1]==nameof(WorkbenchCard.AnchorId))
        {
            var states=vm.Document.FindAnchor(value)?.States;
            if(states?.Any(s=>s.Id==vm.SelectedCard!.StateId)!=true)vm.SelectedCard!.StateId=states?.FirstOrDefault()?.Id??"";
            SyncSelectionEditors();
        }
        vm.EndEdit();}catch(Exception error){vm.EndEdit();vm.ReportError(error.InnerException??error);}
    }
    // One-way so swapping the anchor (and its state list) never writes null back into SelectedState.
    void StateChipSelected(object? sender,SelectionChangedEventArgs e)
    {if(StateChips.SelectedItem is WorkbenchAnchorState state&&!ReferenceEquals(state,vm.SelectedState))vm.SelectedState=state;}
    void ShowLibrary(object? sender,RoutedEventArgs e)=>vm.Category=3;
    async void NewGatheringFromTemplate(object? sender,RoutedEventArgs e)
    {
        var editor=new ChainEditorViewModel(vm.Editor,vm.Capture,vm.TextDetector);editor.UseGatheringTemplates(vm.SelectedChain?.RuntimeKey);
        var dialog=new UrsaWindow{Title="从模板新建采集流程",Width=1100,Height=820,MinWidth=760,MinHeight=560,Content=new ChainEditorPanel{DataContext=editor}};
        var closing=false;
        dialog.Closing+=async (_,args)=>
        {
            if(closing||!editor.HasUnsavedChanges)return;args.Cancel=true;
            if(await UiDialogs.Confirm(dialog,"采集草稿尚未保存，放弃修改并关闭？")){closing=true;dialog.Close();}
        };
        await dialog.ShowDialog(this);vm.Status="采集模板编辑已结束；已发布的流程可在新增采集目标中选择。";
    }
    void ShowChains(object? sender,RoutedEventArgs e)=>vm.Category=0;
    void OpenCardAnchor(object? sender,RoutedEventArgs e)
    {
        if(vm.Document.FindAnchor(vm.SelectedCard?.AnchorId??"") is not {} anchor){vm.Status="请先为步骤选择目标锚点。";return;}
        vm.Category=2;vm.SelectedAnchor=anchor;vm.SelectedState=anchor.States.FirstOrDefault(s=>s.Id==vm.SelectedCard!.StateId)??anchor.States.FirstOrDefault();
    }
    void MoveUp(object? sender,RoutedEventArgs e)=>vm.MoveBy(-1);
    void MoveDown(object? sender,RoutedEventArgs e)=>vm.MoveBy(1);
    void DeleteTransition(object? sender,RoutedEventArgs e){if((sender as Control)?.DataContext is WorkbenchTransition row)vm.DeleteTransition(row);}
    static ListBoxItem? Item(object? source)=>(source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
    void CardPressed(object? sender,PointerPressedEventArgs e)
    {
        if(Item(e.Source)?.DataContext is not WorkbenchCard card)return;
        if(e.GetCurrentPoint(this).Properties.IsRightButtonPressed&&StepList.SelectedItems?.Contains(card)!=true)StepList.SelectedItem=card;
    }
    // Dragging one card moves the whole selection when the card is part of it.
    Task DropCard(object dragged,object over,bool after)
    {
        if(vm.SelectedChain is not {} chain||dragged is not WorkbenchCard card||over is not WorkbenchCard target)return Task.CompletedTask;
        if(!vm.SelectedCards.Contains(card)){vm.SelectedCards.Clear();vm.SelectedCards.Add(card);vm.SelectedCard=card;}
        var index=chain.Cards.IndexOf(target);if(after)index++;
        vm.MoveSelection(index);return Task.CompletedTask;
    }
    void HandleKeys(object? sender,KeyEventArgs e)
    {
        var control=e.KeyModifiers.HasFlag(KeyModifiers.Control);var alt=e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if(e.Key==Key.Escape&&vm.Testing){vm.StopTestCommand.Execute(null);e.Handled=true;return;}
        if(control&&e.Key==Key.S){vm.SaveCommand.Execute(null);e.Handled=true;return;}
        if(control&&e.Key==Key.F){FocusSearch();e.Handled=true;return;}
        if(e.Key==Key.F5){_=vm.RunTestAsync(false);e.Handled=true;return;}
        if(e.Key==Key.F6){_=vm.RunTestAsync(true);e.Handled=true;return;}
        if((e.Source as Visual)?.GetSelfAndVisualAncestors().Any(v=>v is TextBox or NumericUpDown)==true)return;
        if(control&&e.Key==Key.Z)vm.UndoCommand.Execute(null);
        else if(control&&e.Key==Key.Y)vm.RedoCommand.Execute(null);
        else if(vm.Category!=0)return;
        else if(control&&e.Key==Key.A)StepList.SelectAll();
        else if(control&&e.Key==Key.D)vm.DuplicateCardsCommand.Execute(null);
        else if(control&&e.Key==Key.C)vm.CopyCardsCommand.Execute(null);
        else if(control&&e.Key==Key.X)vm.CutCardsCommand.Execute(null);
        else if(control&&e.Key==Key.V)vm.PasteCardsCommand.Execute(null);
        else if(e.Key==Key.Delete)vm.DeleteCardsCommand.Execute(null);
        else if(e.Key==Key.Insert)vm.AddCardCommand.Execute(null);
        else if(alt&&e.Key==Key.Up)vm.MoveBy(-1);
        else if(alt&&e.Key==Key.Down)vm.MoveBy(1);
        else return;e.Handled=true;
    }
    async void PickRegion(object? sender,RoutedEventArgs e)=>await PickGeometry(false,false);
    async void PickPoint(object? sender,RoutedEventArgs e)=>await PickGeometry(false,true);
    async void PickSearch(object? sender,RoutedEventArgs e)=>await PickGeometry(true,false);
    async void PickDragEnd(object? sender,RoutedEventArgs e)
    {
        if(vm.SelectedCard is null)return;
        try{using var result=await LiveTargetPicker.PickAsync(this,vm.Capture,TargetSelectionKind.Point);if(result is null)return;var point=EditorImagePicker.ToReference(result.Region,result.Capture.PixelSize,new(vm.Document.Width,vm.Document.Height));vm.BeginEdit();vm.SelectedCard.Destination=string.Join(", ",point.X,point.Y);vm.EndEdit();vm.Status="拖拽终点已定位。";}catch(Exception error){vm.ReportError(error);}
    }
    async void LocateStrategy(object? sender,RoutedEventArgs e)
    {
        if((sender as Control)?.DataContext is not WorkbenchStrategy strategy)return;
        try
        {
            var point=strategy.ValueKind is "point" or "relativepoint";
            PixelRect? origin=null;
            if(strategy.IsRelative)
            {
                vm.Status="先点击该参数的参考原点，再选择目标位置。";
                using var reference=await LiveTargetPicker.PickAsync(this,vm.Capture,TargetSelectionKind.Point,hint:"点击参考原点 · Esc 取消");if(reference is null)return;
                origin=EditorImagePicker.ToReference(reference.Region,reference.Capture.PixelSize,new(vm.Document.Width,vm.Document.Height));
            }
            using var result=await LiveTargetPicker.PickAsync(this,vm.Capture,point?TargetSelectionKind.Point:TargetSelectionKind.Region);if(result is null)return;
            var region=EditorImagePicker.ToReference(result.Region,result.Capture.PixelSize,new(vm.Document.Width,vm.Document.Height));
            var x=origin?.X??0;var y=origin?.Y??0;
            vm.BeginEdit();strategy.Value=point?string.Join(", ",region.X-x,region.Y-y):string.Join(", ",region.X-x,region.Y-y,region.Right-x,region.Bottom-y);vm.EndEdit();vm.Status="策略坐标已定位。";
        }
        catch(Exception error){vm.ReportError(error);}
    }
    async Task PickGeometry(bool search,bool point)
    {
        if(vm.SelectedState is null)return;
        try
        {
            if(!search&&!point&&vm.SelectedState.Mode=="image"){await PickImage(true);return;}
            var shape=vm.SelectedState.Shape;var kind=point?TargetSelectionKind.Point:search?TargetSelectionKind.Region:shape=="circle"?TargetSelectionKind.Circle:shape=="ellipse"?TargetSelectionKind.Ellipse:TargetSelectionKind.Region;
            using var selected=await LiveTargetPicker.PickAsync(this,vm.Capture,kind);if(selected is null)return;
            var region=EditorImagePicker.ToReference(selected.Region,selected.Capture.PixelSize,new(vm.Document.Width,vm.Document.Height));
            if(selected.Shape=="circle"){var side=Math.Min(region.Width,region.Height);region=new(region.X,region.Y,side,side);}
            vm.SetStateRegion(string.Join(", ",region.X,region.Y,region.Right,region.Bottom),selected.Shape,search);
            vm.Status=search?"搜索范围已更新。":"点击范围已更新，随机点击会避开边缘。";
        }
        catch(Exception error){vm.ReportError(error);}
    }
    async void CaptureAnchor(object? sender,RoutedEventArgs e){try{await PickImage(true);}catch(Exception error){vm.ReportError(error);}}
    async void BrowseAnchor(object? sender,RoutedEventArgs e){try{await PickImage(false);}catch(Exception error){vm.ReportError(error);}}
    // game=true frames a region on the live window; false opens a screenshot file and frames it there.
    async Task PickImage(bool game)
    {
        if(vm.SelectedState is null){vm.Status="请先选择或添加锚点状态。";return;}
        var result=await EditorImagePicker.Pick(this,vm.Editor,vm.Capture,vm.TextDetector,game);
        if(result is not {} image)return;
        using(image.Preview){vm.SetStateImage(image.Asset,image.Box);}
        vm.Status=game?"图片锚点已从游戏画面截取并更新预览。":"图片锚点已从本地截图导入并更新预览；点击范围按截图坐标记录。";
    }
    async void TestAnchor(object? sender,RoutedEventArgs e)
    {
        if(vm.SelectedState is null||vm.SelectedAnchor is null)return;
        try
        {
            if(vm.Capture is null)throw new InvalidOperationException("请先选择游戏窗口。");
            using var capture=await vm.Capture.CaptureAsync();var state=vm.SelectedState;
            if(state.Mode=="coordinate")
            {
                AnchorTestText.Text="坐标锚点 · "+state.Box+" · "+state.ShapeLabel+" · 中央60%点击范围";vm.Status=AnchorTestText.Text;
                var a=state.Data["box"]!.AsArray();var fixedBox=EditorImagePicker.ToReference(new(a[0]!.GetValue<int>(),a[1]!.GetValue<int>(),a[2]!.GetValue<int>()-a[0]!.GetValue<int>(),a[3]!.GetValue<int>()-a[1]!.GetValue<int>()),new(vm.Document.Width,vm.Document.Height),capture.PixelSize);
                var visual=new CropSelectionWindow(capture,fixedBox,selectionShape:state.Shape){Title="点击范围 · "+state.ShapeLabel+" · 拖动可调整"};
                if(await visual.ShowDialog<bool>(this)&&visual.Selection is {} area){var normalized=EditorImagePicker.ToReference(area,capture.PixelSize,new(vm.Document.Width,vm.Document.Height));if(state.Shape=="circle"){var side=Math.Min(normalized.Width,normalized.Height);normalized=new(normalized.X,normalized.Y,side,side);}vm.SetStateRegion(string.Join(", ",normalized.X,normalized.Y,normalized.Right,normalized.Bottom),state.Shape,false);}return;
            }
            var entry=new AnchorEntry("workbench",vm.SelectedAnchor.Name,"ui_profile",new JsonArray(),state.ToJson(),[]);
            var result=new NativeAnchorTestService(vm.Editor).Test(entry,state.ToJson(),capture);AnchorTestText.Text=result.Message;vm.Status=result.Message;
            var array=state.Data["search"]?.AsArray();PixelRect? roi=null;if(array?.Count==4){var r=EditorImagePicker.ToReference(new(array[0]!.GetValue<int>(),array[1]!.GetValue<int>(),array[2]!.GetValue<int>()-array[0]!.GetValue<int>(),array[3]!.GetValue<int>()-array[1]!.GetValue<int>()),new(vm.Document.Width,vm.Document.Height),capture.PixelSize);roi=r;}
            var bounds=result.Bounds is {} box?new PixelRect(box.Left,box.Top,box.Width,box.Height):(PixelRect?)null;
            var preview=new CropSelectionWindow(capture,roi,bounds){Title=result.Message+" · 蓝框为搜索范围，拖动可调整"};
            if(await preview.ShowDialog<bool>(this)&&preview.Selection is {} selection){var normalized=EditorImagePicker.ToReference(selection,capture.PixelSize,new(vm.Document.Width,vm.Document.Height));vm.SetStateRegion(string.Join(", ",normalized.X,normalized.Y,normalized.Right,normalized.Bottom),state.Shape,true);}
        }
        catch(Exception error){vm.ReportError(error);}
    }
}
