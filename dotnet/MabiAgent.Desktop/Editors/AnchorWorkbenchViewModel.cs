using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json.Nodes;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MabiAgent.Desktop.Editors;

public sealed record WorkbenchChoice(string Key,string Label);
public sealed record WorkbenchRunRow(string Time,string Card,string Result,string Details)
{
    public bool IsError=>Result.Contains("失败")||Result.Contains("错误")||Result.Contains("异常");
    public bool IsWarning=>!IsError&&(Result.Contains("等待")||Result.Contains("暂停")||Result.Contains("停止")||Result.Contains("跳过"));
    public bool IsSuccess=>!IsError&&!IsWarning&&(Result.Contains("成功")||Result.Contains("完成")||Result.Contains("通过"));
    public bool IsInfo=>!IsError&&!IsWarning&&!IsSuccess;
}
public delegate Task WorkbenchRun(WorkbenchDocument document,WorkbenchChain chain,IReadOnlyCollection<string>? selected,bool recognizeOnly,IProgress<WorkbenchRunRow> progress,CancellationToken cancellation);

public sealed partial class AnchorWorkbenchViewModel:ObservableObject,IDisposable
{
    public EditorRepository Editor {get;}
    readonly WorkbenchRepository repository;
    readonly WorkbenchRun? run;
    readonly List<string> undo=[],redo=[];
    WorkbenchCard[] clipboard=[];
    string baseline="",editStart="";
    bool restoring;
    CancellationTokenSource? testCancellation;
    public MainViewModel Host {get;}
    public IEditorCaptureService? Capture {get;}
    public IAnchorTextDetector? TextDetector {get;}
    [ObservableProperty] WorkbenchDocument document;
    [ObservableProperty] int category;
    [ObservableProperty] string search="";
    [ObservableProperty] string status="选择任务链或页面开始编辑。";
    public bool StatusIsError=>new[]{"失败","错误","异常","无效","不存在","越出","不支持"}.Any(Status.Contains);
    public bool StatusIsWarning=>!StatusIsError&&new[]{"请先","等待","暂停","停止","未保存"}.Any(Status.Contains);
    public bool StatusIsSuccess=>!StatusIsError&&!StatusIsWarning&&new[]{"已保存","已完成","成功","已定位","已更新","已加入","已关联"}.Any(Status.Contains);
    public bool StatusIsInfo=>!StatusIsError&&!StatusIsWarning&&!StatusIsSuccess;
    [ObservableProperty] WorkbenchChain? selectedChain;
    [ObservableProperty] WorkbenchCard? selectedCard;
    [ObservableProperty] WorkbenchAnchor? selectedAnchor;
    [ObservableProperty] WorkbenchAnchorState? selectedState;
    [ObservableProperty] WorkbenchPage? selectedPage;
    [ObservableProperty] WorkbenchCard? selectedLibraryCard;
    [ObservableProperty] bool testing;
    [ObservableProperty] bool recognizeOnly=true;
    [ObservableProperty] bool libraryAnchorsOnly;
    public ObservableCollection<WorkbenchCard> SelectedCards {get;}=[];
    public ObservableCollection<WorkbenchRunRow> Results {get;}=[];
    public bool HasResults=>Results.Count>0;
    public event Action? CardsReordered;
    public DataGridCollectionView ChainView {get;private set;}=null!;
    public DataGridCollectionView AnchorView {get;private set;}=null!;
    public DataGridCollectionView PageView {get;private set;}=null!;
    public DataGridCollectionView LibraryView {get;private set;}=null!;
    public DataGridCollectionView StrategyView {get;private set;}=null!;
    public bool IsChains=>Category==0;
    public bool IsPages=>Category==1;
    public bool IsAnchors=>Category==2;
    public bool IsLibrary=>Category==3;
    public bool IsStrategies=>Category==4;
    public bool IsResults=>Category==5;
    public bool HasUnsavedChanges=>Document.ToJson().ToJsonString()!=baseline;
    internal string BaselineJson=>baseline;
    public string SelectionText=>$"已选 {(SelectedCards.Count>0?SelectedCards.Count:SelectedCard is null?0:1)} 步 · Ctrl / Shift 多选";
    public string AnchorReferences=>SelectedAnchor is null?"":$"{Document.AnchorReferences(SelectedAnchor.Id).Count} 张卡片引用 · 保存后同步";
    public static WorkbenchChoice[] Actions {get;}=[new("observe","识别页面"),new("click","点击目标"),new("right","右键目标"),new("scroll","滚动区域"),new("zoom_max","放大到最大"),new("drag","拖动区域"),new("key","按键"),new("text","输入文字"),new("wait","等待状态"),new("branch","条件分支"),new("delay","短暂等待"),new("semantic","任务动作")];
    public static WorkbenchChoice[] Conditions {get;}=[new("all","全部满足"),new("any","任一满足"),new("not","均未出现"),new("none","全部不存在"),new("page","当前页面匹配"),new("exists","指定锚点存在")];
    public static WorkbenchChoice[] PageRules {get;}=[new("all","全部锚点匹配"),new("any","任一锚点匹配")];
    public static WorkbenchChoice[] Modes {get;}=[new("image","图片匹配"),new("coordinate","页面相对坐标")];
    public static WorkbenchChoice[] Shapes {get;}=[new("rectangle","矩形"),new("circle","圆形"),new("ellipse","椭圆")];
    public static WorkbenchChoice[] RandomModes {get;}=[new("inherit","沿用锚点设置"),new("on","中心范围随机偏移"),new("off","固定在中心")];
    public IReadOnlyList<WorkbenchChoice> ActionChoices=>IsImportedNavigation?Actions.Where(x=>x.Key is "click" or "right" or "scroll" or "zoom_max" or "drag").ToArray():Actions.Where(x=>x.Key!="semantic"||SemanticRoles.Count>0).ToArray();
    public IReadOnlyList<WorkbenchChoice> SemanticRoles=>SelectedChain?.Kind switch
    {"production"=>[new("production","加工委托"),new("result","领取并核实产物")],"gathering"=>[new("gathering_start","开始原生轮次"),new("observation","等待轮次完成")],_=>[]};
    public bool IsImportedNavigation=>SelectedChain?.Data["source_path"] is not null;
    public bool CanConfigureFlowLogic=>!IsImportedNavigation;
    public bool IsSemanticCard=>SelectedCard?.IsSemantic==true;
    public bool IsStagedChain=>SelectedChain?.Data["staged_route"]?.GetValue<bool>()==true;
    public string ChainHint=>IsStagedChain?"地图导航按阶段执行，可在同一阶段内拖动排序；新增步骤放在当前阶段。页面确认与最大缩放沿用导航流程。":IsImportedNavigation?"这条导航复用原任务流程。卡片设置点击、滚动、拖拽与缩放；到达条件和阶段等待沿用原导航策略。":"步骤按卡片顺序执行，可设置条件、分支和完成状态。";
    public IReadOnlyList<WorkbenchChoice> TransitionChoices=>new[]{new WorkbenchChoice("","独立设置")}.Concat(Document.Pages.FirstOrDefault(p=>p.Id==SelectedCard?.PageId)?.Transitions.Select(t=>new WorkbenchChoice(t.Id,t.DisplayName))??[]).ToArray();
    public bool CanLinkTransition=>SelectedCard is {ConsumesResources:false,Action:"click" or "right"};
    public IReadOnlyList<WorkbenchChoice> RuntimeChoices=>Document.Chains.Where(c=>c.Data["source"] is not null&&c.Kind is "production" or "gathering").GroupBy(c=>c.RuntimeKey).Select(g=>new WorkbenchChoice(g.Key,g.First().DisplayName)).ToArray();
    public AnchorWorkbenchViewModel(MainViewModel host,EditorRepository editor,IEditorCaptureService? capture=null,IAnchorTextDetector? detector=null,WorkbenchRun? run=null)
    {
        Host=host;Editor=editor;repository=new(editor);Capture=capture;TextDetector=detector;this.run=run;
        document=repository.Load();baseline=Document.ToJson().ToJsonString();ResetViews();SelectDefaults();
        SelectedCards.CollectionChanged+=(_,_)=>OnPropertyChanged(nameof(SelectionText));
        Results.CollectionChanged+=(_,_)=>OnPropertyChanged(nameof(HasResults));
        UiLanguageService.Shared.LanguageChanged+=LanguageChanged;
    }
    void LanguageChanged(object? sender,EventArgs e)
    {
        var models=Document.Chains.Cast<WorkbenchModel>().Concat(Document.Chains.SelectMany(c=>c.Cards))
            .Concat(Document.Anchors).Concat(Document.Anchors.SelectMany(a=>a.States))
            .Concat(Document.Pages).Concat(Document.Pages.SelectMany(p=>p.Transitions))
            .Concat(Document.CardLibrary).Concat(Document.Strategies);
        foreach(var model in models)model.RefreshDisplay();
        foreach(var name in new[]{nameof(RuntimeChoices),nameof(TransitionChoices),nameof(ActionChoices),nameof(SemanticRoles),nameof(ChainHint)})OnPropertyChanged(name);
    }
    void SelectDefaults(){SelectedChain=Document.Chains.FirstOrDefault();SelectedPage=Document.Pages.FirstOrDefault();SelectedAnchor=Document.Anchors.FirstOrDefault();}
    void ResetViews()
    {
        DataGridCollectionView View<T>(IEnumerable<T> source,Func<T,string> text){var view=new DataGridCollectionView(source);view.Filter=x=>x is T row&&(string.IsNullOrWhiteSpace(Search)||text(row).Contains(Search,StringComparison.OrdinalIgnoreCase));return view;}
        ChainView=View(Document.Chains,x=>x.DisplayName+" "+x.Name+" "+x.Kind+" "+x.RuntimeKey);
        AnchorView=View(Document.Anchors,x=>(!LibraryAnchorsOnly||x.InLibrary)?x.Name+" "+x.Description+" "+x.Id:"");
        AnchorView.Filter=x=>x is WorkbenchAnchor a&&(!LibraryAnchorsOnly||a.InLibrary)&&(string.IsNullOrWhiteSpace(Search)||(a.DisplayName+" "+a.Name+" "+a.Description+" "+a.Id).Contains(Search,StringComparison.OrdinalIgnoreCase));
        PageView=View(Document.Pages,x=>x.DisplayName+" "+x.Name+" "+x.Id);
        LibraryView=View(Document.CardLibrary,x=>x.DisplayName+" "+x.Name+" "+x.Summary);
        StrategyView=View(Document.Strategies,x=>x.ParameterLabel+" "+x.Name+" "+x.CategoryLabel+" "+x.Category+" "+x.DescriptionLabel+" "+x.Description);
        foreach(var name in new[]{nameof(ChainView),nameof(AnchorView),nameof(PageView),nameof(LibraryView),nameof(StrategyView)})OnPropertyChanged(name);
    }
    void RefreshViews()
    {
        var chain=SelectedChain;var card=SelectedCard;var selected=SelectedCards.ToArray();var anchor=SelectedAnchor;var state=SelectedState;var page=SelectedPage;var library=SelectedLibraryCard;
        ChainView?.Refresh();AnchorView?.Refresh();PageView?.Refresh();LibraryView?.Refresh();StrategyView?.Refresh();
        if(chain is not null&&ChainView?.Cast<WorkbenchChain>().Contains(chain)==true)
        {SelectedChain=chain;if(card is not null&&chain.Cards.Contains(card))SelectedCard=card;SelectedCards.Clear();foreach(var row in selected.Where(chain.Cards.Contains))SelectedCards.Add(row);CardsReordered?.Invoke();}
        if(anchor is not null&&AnchorView?.Cast<WorkbenchAnchor>().Contains(anchor)==true){SelectedAnchor=anchor;if(state is not null&&anchor.States.Contains(state))SelectedState=state;}
        if(page is not null&&PageView?.Cast<WorkbenchPage>().Contains(page)==true)SelectedPage=page;
        if(library is not null&&LibraryView?.Cast<WorkbenchCard>().Contains(library)==true)SelectedLibraryCard=library;
    }
    partial void OnSearchChanged(string value)
    {
        RefreshViews();
        if(SelectedChain is {} chain&&!ChainView.Cast<WorkbenchChain>().Contains(chain))SelectedChain=null;
        if(SelectedAnchor is {} anchor&&!AnchorView.Cast<WorkbenchAnchor>().Contains(anchor))SelectedAnchor=null;
        if(SelectedPage is {} page&&!PageView.Cast<WorkbenchPage>().Contains(page))SelectedPage=null;
        if(SelectedLibraryCard is {} card&&!LibraryView.Cast<WorkbenchCard>().Contains(card))SelectedLibraryCard=null;
    }
    partial void OnCategoryChanged(int value){foreach(var name in new[]{nameof(IsChains),nameof(IsPages),nameof(IsAnchors),nameof(IsLibrary),nameof(IsStrategies),nameof(IsResults)})OnPropertyChanged(name);}
    partial void OnStatusChanged(string value){foreach(var name in new[]{nameof(StatusIsError),nameof(StatusIsWarning),nameof(StatusIsSuccess),nameof(StatusIsInfo)})OnPropertyChanged(name);}
    partial void OnSelectedChainChanged(WorkbenchChain? value){SelectedCards.Clear();SelectedCard=value?.Cards.FirstOrDefault();RefreshChainChoices();}
    void RefreshChainChoices(){foreach(var name in new[]{nameof(ActionChoices),nameof(SemanticRoles),nameof(IsStagedChain),nameof(IsImportedNavigation),nameof(CanConfigureFlowLogic),nameof(ChainHint)})OnPropertyChanged(name);}
    public void ApplyRuntimeChoice()
    {if(SelectedChain is not {} chain||IsImportedNavigation)return;var source=Document.Chains.FirstOrDefault(c=>c.Data["source"] is not null&&c.RuntimeKey==chain.RuntimeKey);if(source is not null)chain.Kind=source.Kind;RefreshChainChoices();}
    partial void OnSelectedCardChanging(WorkbenchCard? value){if(SelectedCard is {} old)old.PropertyChanged-=CardChanged;}
    partial void OnSelectedCardChanged(WorkbenchCard? value){if(value is not null)value.PropertyChanged+=CardChanged;RefreshCardChoices();OnPropertyChanged(nameof(SelectionText));}
    void CardChanged(object? sender,PropertyChangedEventArgs e){if(e.PropertyName is nameof(WorkbenchCard.PageId) or nameof(WorkbenchCard.Action))RefreshCardChoices();}
    partial void OnLibraryAnchorsOnlyChanged(bool value)=>OnSearchChanged(Search);
    partial void OnSelectedAnchorChanged(WorkbenchAnchor? value){SelectedState=value?.States.FirstOrDefault();OnPropertyChanged(nameof(AnchorReferences));}
    void Guard(Action action){try{action();OnPropertyChanged(nameof(HasUnsavedChanges));}catch(Exception error){Editor.ReportFault(error,"锚点工作台",SelectedChain?.Id);Status=error.Message;}}
    public void BeginEdit(){if(!restoring)editStart=Document.ToJson().ToJsonString();}
    public void EndEdit(){if(restoring||editStart.Length==0)return;var after=Document.ToJson().ToJsonString();if(editStart!=after){PushUndo(editStart);redo.Clear();}editStart="";OnPropertyChanged(nameof(HasUnsavedChanges));}
    void PushUndo(string json){if(undo.LastOrDefault()!=json)undo.Add(json);if(undo.Count>30)undo.RemoveAt(0);}
    void Change(Action mutation)=>Guard(()=>{if(Testing)throw new InvalidOperationException("请先停止测试再编辑。");EndEdit();var before=Document.ToJson().ToJsonString();mutation();if(before!=Document.ToJson().ToJsonString()){PushUndo(before);redo.Clear();}RefreshViews();OnPropertyChanged(nameof(AnchorReferences));});
    public void ReportError(Exception error)=>Guard(()=>throw error);
    public bool SaveNow(){try{EndEdit();repository.Save(Document);baseline=Document.ToJson().ToJsonString();OnPropertyChanged(nameof(HasUnsavedChanges));Status="工作台已保存，下次开始时使用新配置。";return true;}catch(Exception error){ReportError(error);return false;}}
    [RelayCommand] void Save()=>SaveNow();
    public void ReloadNow(){Document=repository.Load();baseline=Document.ToJson().ToJsonString();undo.Clear();redo.Clear();ResetViews();SelectDefaults();Status="已重新读取配置。";OnPropertyChanged(nameof(HasUnsavedChanges));}
    void Restore(string json){restoring=true;try{var chainId=SelectedChain?.Id;Document=new(JsonNode.Parse(json)!.AsObject());ResetViews();SelectedChain=Document.Chains.FirstOrDefault(x=>x.Id==chainId)??Document.Chains.FirstOrDefault();SelectedAnchor=Document.Anchors.FirstOrDefault();SelectedPage=Document.Pages.FirstOrDefault();}finally{restoring=false;}OnPropertyChanged(nameof(HasUnsavedChanges));}
    [RelayCommand] void Undo(){EndEdit();if(undo.Count==0||Testing)return;redo.Add(Document.ToJson().ToJsonString());var value=undo[^1];undo.RemoveAt(undo.Count-1);Restore(value);Status="已撤销。";}
    [RelayCommand] void Redo(){EndEdit();if(redo.Count==0||Testing)return;PushUndo(Document.ToJson().ToJsonString());var value=redo[^1];redo.RemoveAt(redo.Count-1);Restore(value);Status="已重做。";}
    void FollowStage(WorkbenchCard card,WorkbenchCard? preceding)
    {if(!IsStagedChain||preceding is null)return;foreach(var key in new[]{"stage","stage_collection"})if(preceding.Data[key] is{} value)card.Data[key]=value.DeepClone();card.PageId=preceding.PageId;}
    [RelayCommand] void AddCard()=>Change(()=>{if(SelectedChain is null)return;var preceding=SelectedCard;var added=SelectedChain.AddCard(index:preceding is null?-1:SelectedChain.Cards.IndexOf(preceding)+1);FollowStage(added,preceding);if(IsStagedChain)added.Action="click";SelectedCard=added;});
    WorkbenchCard[] Selection()
    {
        var ids=(SelectedCards.Count>0?SelectedCards.Select(c=>c.Id):SelectedCard is {} card?[card.Id]:[]).ToHashSet(StringComparer.Ordinal);
        return SelectedChain?.Cards.Where(c=>ids.Contains(c.Id)).ToArray()??[];
    }
    [RelayCommand] void DeleteCards()=>Change(()=>{SelectedChain?.RemoveCards(Selection().Select(x=>x.Id));SelectedCards.Clear();SelectedCard=SelectedChain?.Cards.FirstOrDefault();});
    [RelayCommand] void DuplicateCards()=>Change(()=>{if(SelectedChain is null)return;var cards=Selection();var index=cards.Length==0?SelectedChain.Cards.Count:cards.Max(x=>SelectedChain.Cards.IndexOf(x))+1;foreach(var card in cards)SelectedCard=SelectedChain.AddCard(card,index++);});
    [RelayCommand] void CopyCards(){clipboard=Selection().Select(x=>x.Copy()).ToArray();Status=$"已复制 {clipboard.Length} 步。";}
    [RelayCommand] void CutCards(){CopyCards();DeleteCards();}
    [RelayCommand] void PasteCards()=>Change(()=>{if(SelectedChain is null||clipboard.Length==0)return;if(IsStagedChain&&clipboard.Any(c=>!ActionChoices.Any(a=>a.Key==c.Action)))throw new InvalidOperationException("地图导航阶段只能加入点击、拖拽、滚动或缩放步骤。");var preceding=SelectedCard;var index=preceding is null?SelectedChain.Cards.Count:SelectedChain.Cards.IndexOf(preceding)+1;foreach(var card in clipboard){var added=SelectedChain.AddCard(card,index++);FollowStage(added,preceding);SelectedCard=added;}Status=$"已粘贴 {clipboard.Length} 步。";});
    [RelayCommand] void StoreCards()=>Change(()=>{foreach(var card in Selection())Document.CardLibrary.Add(card.Copy());Status=$"已将 {Selection().Length} 步存入公共卡片库。";});
    [RelayCommand] void InsertLibrary()=>Change(()=>{if(SelectedChain is null||SelectedLibraryCard is null)throw new InvalidOperationException("请先选择目标任务链和库中卡片。");if(IsStagedChain&&!ActionChoices.Any(a=>a.Key==SelectedLibraryCard.Action))throw new InvalidOperationException("此卡片不适用于地图导航阶段。");var preceding=SelectedCard;var added=SelectedChain.AddCard(SelectedLibraryCard,preceding is null?-1:SelectedChain.Cards.IndexOf(preceding)+1);FollowStage(added,preceding);SelectedCard=added;Category=0;Status="卡片已加入当前任务链。";});
    [RelayCommand] void DeleteLibrary()=>Change(()=>{if(SelectedLibraryCard is null)return;Document.CardLibrary.Remove(SelectedLibraryCard);SelectedLibraryCard=Document.CardLibrary.FirstOrDefault();});
    public void MoveSelection(int insertionIndex)=>Change(()=>
    {
        if(SelectedChain is null)return;
        var cards=Selection();
        if(IsStagedChain&&cards.Length>0)
        {
            string Stage(WorkbenchCard card)=>card.Data["stage"]+"/"+card.Data["stage_collection"];
            var stage=Stage(cards[0]);var same=SelectedChain.Cards.Where(c=>Stage(c)==stage).ToArray();
            if(cards.Any(c=>Stage(c)!=stage)||insertionIndex<SelectedChain.Cards.IndexOf(same[0])||insertionIndex>SelectedChain.Cards.IndexOf(same[^1])+1)throw new InvalidOperationException("请在同一导航阶段内调整步骤顺序。");
        }
        var active=SelectedCard;SelectedChain.MoveCards(cards.Select(x=>x.Id),insertionIndex);
        SelectedCard=active;SelectedCards.Clear();foreach(var card in cards)SelectedCards.Add(card);CardsReordered?.Invoke();
    });
    public void MoveBy(int delta){if(SelectedChain is null)return;var chosen=Selection();if(chosen.Length==0)return;var at=delta<0?chosen.Min(x=>SelectedChain.Cards.IndexOf(x))-1:chosen.Max(x=>SelectedChain.Cards.IndexOf(x))+2;MoveSelection(Math.Clamp(at,0,SelectedChain.Cards.Count));}
    [RelayCommand] void AddChain()=>Change(()=>{var chain=new WorkbenchChain(new JsonObject{["id"]=WorkbenchModel.NewId("chain"),["name"]="新任务链",["runtime_key"]=WorkbenchModel.NewId("flow"),["kind"]="navigation"});Document.Chains.Add(chain);SelectedChain=chain;chain.AddCard();});
    [RelayCommand] void AddAnchor()=>Change(()=>{var anchor=new WorkbenchAnchor(new JsonObject{["id"]=WorkbenchModel.NewId("anchor"),["name"]="新锚点",["in_library"]=true});Document.Anchors.Add(anchor);SelectedAnchor=anchor;AddStateCore();Category=2;});
    void AddStateCore(){if(SelectedAnchor is null)return;var state=new WorkbenchAnchorState(new JsonObject{["id"]=WorkbenchModel.NewId("state"),["name"]="新状态",["mode"]="coordinate",["box"]=new JsonArray(0,0,1,1),["search"]=new JsonArray(0,0,Document.Width,Document.Height)});SelectedAnchor.States.Add(state);SelectedState=state;}
    [RelayCommand] void AddState()=>Change(AddStateCore);
    [RelayCommand] void DeleteAnchor()=>Change(()=>{if(SelectedAnchor is null)return;var refs=Document.AnchorReferences(SelectedAnchor.Id);if(refs.Count>0||Document.Pages.Any(p=>WorkbenchModel.SplitIds(p.AnchorIds).Contains(SelectedAnchor.Id)||p.Transitions.Any(t=>t.AnchorId==SelectedAnchor.Id)))throw new InvalidOperationException("该锚点仍被卡片或页面引用，请先更换引用。");Document.Anchors.Remove(SelectedAnchor);SelectedAnchor=Document.Anchors.FirstOrDefault();});
    [RelayCommand] void DeleteState()=>Change(()=>{if(SelectedAnchor is null||SelectedState is null)return;if(Document.AnchorReferences(SelectedAnchor.Id).Any(c=>c.StateId==SelectedState.Id))throw new InvalidOperationException("该状态仍被任务卡片引用。");SelectedAnchor.States.Remove(SelectedState);SelectedState=SelectedAnchor.States.FirstOrDefault();});
    [RelayCommand] void AddPage()=>Change(()=>{var page=new WorkbenchPage(new JsonObject{["id"]=WorkbenchModel.NewId("page"),["name"]="新页面"});Document.Pages.Add(page);SelectedPage=page;Category=1;});
    [RelayCommand] void AddTransition()=>Change(()=>{var id=WorkbenchModel.NewId("link");SelectedPage?.Transitions.Add(new(new JsonObject{["id"]=id,["name"]="页面跳转",["target"]="workbench.transition."+id,["action"]="click"}));OnPropertyChanged(nameof(TransitionChoices));});
    public void DeleteTransition(WorkbenchTransition transition)=>Change(()=>SelectedPage?.Transitions.Remove(transition));
    public void RefreshCardChoices(){OnPropertyChanged(nameof(TransitionChoices));OnPropertyChanged(nameof(CanLinkTransition));OnPropertyChanged(nameof(IsSemanticCard));}
    public void ApplyTransitionChoice()
    {
        var card=SelectedCard;if(card is null)return;
        if(!CanLinkTransition){card.TransitionId="";throw new InvalidOperationException("请在普通点击步骤中选择页面跳转。");}
        var link=Document.Pages.FirstOrDefault(p=>p.Id==card.PageId)?.Transitions.FirstOrDefault(t=>t.Id==card.TransitionId);
        if(link is null)return;
        card.AnchorId=link.AnchorId;card.StateId=Document.FindAnchor(link.AnchorId)?.States.FirstOrDefault()?.Id??"";card.Target=link.Target;card.ExpectedPageId="";card.Action=link.Action;
    }
    public void SetStateRegion(string region,string shape,bool searchOnly)=>Change(()=>{if(SelectedState is null)return;if(searchOnly)SelectedState.Search=region;else{SelectedState.Box=region;SelectedState.Shape=shape;if(SelectedState.Mode=="coordinate")SelectedState.Search=region;}});
    public void SetStateImage(JsonObject asset,int[] box)=>Change(()=>{if(SelectedState is null)return;SelectedState.Mode="image";SelectedState.Shape="rectangle";SelectedState.Path=Editor.AssetPath(EditorRepository.Text(asset,"id"));SelectedState.Sha256=EditorRepository.Text(asset,"sha256");SelectedState.Data["bytes"]=asset["bytes"]?.DeepClone();SelectedState.Box=string.Join(", ",box);SelectedState.Search=SelectedState.Box;Status="已选取图片，可调整搜索范围并实时测试。";});
    [RelayCommand] void LinkSelectedAnchor()=>Change(()=>{if(SelectedCard is null||SelectedAnchor is null)return;SelectedCard.AnchorId=SelectedAnchor.Id;SelectedCard.StateId=SelectedState?.Id??"";Status="已关联通用锚点。";});
    public async Task RunTestAsync(bool selected)
    {
        if(Testing)return;
        try
        {
            EndEdit();if(run is null)throw new InvalidOperationException("运行服务尚未就绪。");if(SelectedChain is null)throw new InvalidOperationException("请选择任务链。");
            var ids=selected?Selection().Select(x=>x.Id).ToArray():null;if(selected&&ids!.Length==0)throw new InvalidOperationException("请先选择要测试的步骤。");
            Results.Clear();Testing=true;testCancellation=new();Category=5;Status=RecognizeOnly?"正在验证识别与流程配置…":"正在测试流程；后台模式不切换焦点，暂停可随时停止后续输入。";
            await run(new(Document.ToJson()),new(SelectedChain.ToJson()),ids,RecognizeOnly,new Progress<WorkbenchRunRow>(row=>{Results.Add(row);Status=row.Card+" · "+row.Result;}),testCancellation.Token);
            Status="测试结束，请查看逐步结果。";
        }
        catch(OperationCanceledException){Status="测试已停止。";}
        catch(Exception error){ReportError(error);Results.Add(new(DateTimeOffset.Now.ToString("HH:mm:ss"),SelectedChain?.Name??"流程","失败",error.Message));}
        finally{Testing=false;testCancellation?.Dispose();testCancellation=null;}
    }
    [RelayCommand] Task TestAllAsync()=>RunTestAsync(false);
    [RelayCommand] Task TestSelectedAsync()=>RunTestAsync(true);
    [RelayCommand] void StopTest()=>testCancellation?.Cancel();
    public void Dispose(){UiLanguageService.Shared.LanguageChanged-=LanguageChanged;testCancellation?.Cancel();testCancellation?.Dispose();}
}
