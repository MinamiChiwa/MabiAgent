using MabiAgent.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace MabiAgent.Desktop.Editors;
public sealed record ChainChoice(JsonObject Document)
{public string Name=>EditorRepository.Text(Document,"name");public string Label=>$"{Name} · v{Document["revision"]} · {(EditorRepository.Text(Document,"status")=="published"?"已发布":"草稿")}";}
public sealed class MaterialRow(JsonObject data):ObservableObject
{
    public JsonObject Data {get;}=data;
    public string Name {get=>EditorRepository.Text(Data,"name");set{Data["name"]=value;OnPropertyChanged();}}
    public string Quantity {get=>Data["quantity"]?.ToString()??"1";set{if(long.TryParse(value,out var n)&&n>0)Data["quantity"]=n;else Data["quantity"]=value;OnPropertyChanged();}}
}
public sealed class NodeRow(JsonObject data,int index):ObservableObject
{
    public JsonObject Data {get;}=data;public string Caption=>$"{index+1}. {Label}";
    public string Label {get=>EditorRepository.Text(Data,"label");set{Data["label"]=value;OnPropertyChanged();OnPropertyChanged(nameof(Caption));}}
    public string Page=>EditorRepository.Text(Data,"page");
    public ObservableCollection<ChainAnchorRow> Anchors {get;}=new(data["anchors"]!.AsArray().Select(a=>new ChainAnchorRow(a!.AsObject())));
}
public sealed class ChainAnchorRow(JsonObject data):ObservableObject
{
    public JsonObject Data {get;}=data;public string Label=>EditorRepository.Text(Data,"label");public string Key=>EditorRepository.Text(Data,"key");
    public ObservableCollection<VariantRow> Variants {get;}=new(data["variants"]!.AsArray().Select(v=>new VariantRow(v!.AsObject())));
    public void Refresh(){Variants.Clear();foreach(var v in Data["variants"]!.AsArray())Variants.Add(new(v!.AsObject()));OnPropertyChanged(nameof(Label));}
}
public sealed class VariantRow(JsonObject data):ObservableObject
{
    public JsonObject Data {get;}=data;
    public string Label=>$"{EditorRepository.ModeChoices.FirstOrDefault(x=>x.Key==Mode)?.Label??Mode} · {Box}";
    public string Mode{get=>EditorRepository.Text(Data,"match_mode","grayscale");set{Data["match_mode"]=value;OnPropertyChanged();}}
    public string Threshold{get=>Data["threshold"]?.ToString()??"0.97";set{Data["threshold"]=double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var d)?JsonValue.Create(d):JsonValue.Create(value);OnPropertyChanged();}}
    public string Box{get=>string.Join(", ",Data["box"]?.AsArray().Select(x=>x!.ToString())??[]);set{SetBox("box",value);OnPropertyChanged();}}
    public string Search{get=>string.Join(", ",Data["search"]?.AsArray().Select(x=>x!.ToString())??[]);set{SetBox("search",value);OnPropertyChanged();}}
    void SetBox(string key,string text){var parts=text.Split([',','，',' '],StringSplitOptions.RemoveEmptyEntries);if(parts.Length==4&&parts.All(x=>int.TryParse(x,out _)))Data[key]=new JsonArray(parts.Select(x=>(JsonNode?)JsonValue.Create(int.Parse(x))).ToArray());else Data[key]=JsonValue.Create(text);}
}
public sealed partial class ChainEditorViewModel:ObservableObject
{
    public EditorRepository Repository{get;}public IEditorCaptureService? CaptureService{get;}public IAnchorTextDetector? TextDetector{get;}
    public ObservableCollection<ChainChoice> Chains {get;}=[];public ObservableCollection<ChainChoice> Templates {get;}=[];
    public ObservableCollection<ChainChoice> Versions {get;}=[];public ObservableCollection<MaterialRow> Materials {get;}=[];public ObservableCollection<NodeRow> Nodes {get;}=[];
    [ObservableProperty] private ChainChoice? selectedChain;
    [ObservableProperty] private ChainChoice? selectedTemplate;
    [ObservableProperty] private ChainChoice? selectedVersion;
    [ObservableProperty] private NodeRow? selectedNode;
    [ObservableProperty] private ChainAnchorRow? selectedAnchor;
    [ObservableProperty] private VariantRow? selectedVariant;
    [ObservableProperty] private string status="";
    [ObservableProperty] private bool advancedEditing;
    public IEnumerable<NodeRow> VisibleNodes=>IsGathering&&!AdvancedEditing?Nodes.Where(n=>EditorRepository.GatheringDifferenceNodeIds(current!).Contains(EditorRepository.Text(n.Data,"id"))):Nodes;
    partial void OnAdvancedEditingChanged(bool value){OnPropertyChanged(nameof(VisibleNodes));SelectedNode=VisibleNodes.FirstOrDefault();}
    public void UseGatheringTemplates(string? runtimeKey=null)
    {
        Templates.Clear();foreach(var t in Repository.Templates.Where(t=>EditorRepository.Text(t,"kind")=="gathering"&&Repository.Catalog["gathering_routes"]?[EditorRepository.Text(t,"runtime_key")]?["alias_of"] is null&&!(EditorRepository.Text(t["parameters"]!,"skill") is "fishing" or "釣魚" or "钓鱼")))Templates.Add(new(t));
        SelectedTemplate=Templates.FirstOrDefault(t=>EditorRepository.Text(t.Document,"runtime_key")==runtimeKey)??Templates.FirstOrDefault();
        NewDraftCommand.Execute(null);Status="已复用公共导航和原生轮次。只改目标名称、列表目标、详情身份和任务身份图；已有采集点可直接使用原模板发布。";
    }
    JsonObject? current;JsonArray previousMaterials=new();string baseline="";bool restoringSelection;
    public Func<bool>? ConfirmDiscard {get;set;}
    public Action<Action>? RequestDiscard {get;set;}
    bool AllowDiscard(Action retry){if(ConfirmDiscard?.Invoke()==true)return true;RequestDiscard?.Invoke(retry);return false;}
    string EditSignature()=>current?.ToJsonString()+"|"+Hours+"|"+Minutes+"|"+Seconds;
    public bool HasUnsavedChanges=>current is not null&&EditSignature()!=baseline;
    public bool HasCurrent=>current is not null;public bool IsProduction=>current is not null&&EditorRepository.Text(current,"kind")=="production";
    public bool IsGathering=>current is not null&&!IsProduction;
    public JsonObject? Current=>current;
    public string Name {get=>current is null?"":EditorRepository.Text(current,"name");set{if(current is not null)current["name"]=value;OnPropertyChanged();}}
    public string Product {get=>Param("target_item_name");set{SetParam("target_item_name",value);if(current?["quantity"] is JsonObject q){q["name"]=value;q["identity"]=null;}OnPropertyChanged();}}
    public string Output {get=>Param("output_per_round","1");set{SetNumber("output_per_round",value);OnPropertyChanged();}}
    public string Attempts {get=>Param("attempts_per_batch","10");set{SetNumber("attempts_per_batch",value);OnPropertyChanged();}}
    public string Skill {get=>Param("skill");set{SetParam("skill",value);OnPropertyChanged();}}
    public string Point {get=>Param("node");set{SetParam("node",value);OnPropertyChanged();}}
    public string Device=>Param("device");
    [ObservableProperty] private string hours="0";
    [ObservableProperty] private string minutes="0";
    [ObservableProperty] private string seconds="0";
    string Param(string key,string fallback="")=>current?["parameters"]?[key]?.ToString()??fallback;
    void SetParam(string key,string value){if(current is not null)current["parameters"]![key]=value;}
    void SetNumber(string key,string value){if(current is not null)current["parameters"]![key]=long.TryParse(value,out var n)?JsonValue.Create(n):JsonValue.Create(value);}
    public ChainEditorViewModel(EditorRepository repository,IEditorCaptureService? captureService=null,IAnchorTextDetector? textDetector=null)
    {Repository=repository;CaptureService=captureService;TextDetector=textDetector;foreach(var t in repository.Templates)Templates.Add(new(t));SelectedTemplate=Templates.FirstOrDefault();RefreshList();}
    void RefreshList(){Chains.Clear();foreach(var c in Repository.List())Chains.Add(new(c));}
    partial void OnSelectedChainChanged(ChainChoice? oldValue,ChainChoice? newValue){if(restoringSelection||newValue is null)return;if(HasUnsavedChanges&&!AllowDiscard(()=>SelectedChain=newValue)){restoringSelection=true;SelectedChain=oldValue;restoringSelection=false;Status="尚有未保存修改，请先保存或确认放弃。";return;}Load(newValue.Document);}
    partial void OnSelectedNodeChanged(NodeRow? value){SelectedAnchor=value?.Anchors.FirstOrDefault();}
    partial void OnSelectedAnchorChanged(ChainAnchorRow? value){SelectedVariant=value?.Variants.FirstOrDefault();}
    public void Load(JsonObject document)
    {current=(JsonObject)document.DeepClone();Materials.Clear();foreach(var m in current["parameters"]?["inputs"]?.AsArray()??[]){var r=new MaterialRow(m!.AsObject());r.PropertyChanged+=MaterialChanged;Materials.Add(r);}previousMaterials=(JsonArray)(current["parameters"]?["inputs"]?.DeepClone()??new JsonArray());
    var duration=current["parameters"]?["duration_s"]?.GetFiniteDouble()??0;Hours=((int)duration/3600).ToString();Minutes=((int)duration%3600/60).ToString();Seconds=(duration%60).ToString(CultureInfo.InvariantCulture);RefreshNodes();Versions.Clear();var id=EditorRepository.Text(current,"id");if(id.Length>0)foreach(var h in Repository.History(id))Versions.Add(new(h));foreach(var name in new[]{nameof(HasCurrent),nameof(Current),nameof(Name),nameof(Product),nameof(Output),nameof(Attempts),nameof(Skill),nameof(Point),nameof(Device),nameof(IsProduction),nameof(IsGathering)})OnPropertyChanged(name);baseline=EditSignature();}
    void RefreshNodes(){var id=SelectedNode is null?"":EditorRepository.Text(SelectedNode.Data,"id");Nodes.Clear();if(current is not null)foreach(var(n,i)in current["nodes"]!.AsArray().Select((n,i)=>(n,i)))Nodes.Add(new(n!.AsObject(),i));OnPropertyChanged(nameof(VisibleNodes));SelectedNode=VisibleNodes.FirstOrDefault(n=>EditorRepository.Text(n.Data,"id")==id)??VisibleNodes.FirstOrDefault();}
    void MaterialChanged(object? sender,PropertyChangedEventArgs e){if(current is null)return;EditorRepository.SynchronizeMaterials(current,previousMaterials);previousMaterials=(JsonArray)current["parameters"]!["inputs"]!.DeepClone();RefreshNodes();}
    public void ReportError(Exception error,string operation="任务链操作"){Repository.ReportFault(error,operation,current is null?null:EditorRepository.Text(current,"id"));Status=error.Message;}
    void Run(Action action){try{action();}catch(Exception e)when(e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or FormatException or OverflowException){ReportError(e);}}
    [RelayCommand] void NewDraft()=>Run(()=>{if(SelectedTemplate is not null){if(HasUnsavedChanges&&!AllowDiscard(NewDraft)){Status="请先保存当前修改。";return;}Load(Repository.CreateDraft(EditorRepository.Text(SelectedTemplate.Document,"template_key")));baseline="";Status="已创建草稿";}});
    [RelayCommand] void Duplicate()=>Run(()=>{if(current is null)return;var c=(JsonObject)current.DeepClone();c["id"]="";c["revision"]=0;c["name"]=Name+" 副本";Load(c);baseline="";});
    [RelayCommand] void Save()=>Persist(false);
    [RelayCommand] void Publish()=>Persist(true);
    void Persist(bool publish)=>Run(()=>{if(current is null)return;if(IsProduction){if(!int.TryParse(Hours,out var h)||h<0||!int.TryParse(Minutes,out var m)||m is <0 or >59||!double.TryParse(Seconds,NumberStyles.Float,CultureInfo.InvariantCulture,out var s)||s<0||s>=60)throw new InvalidDataException("耗时请填写小时、0–59分和0–59秒");current["parameters"]!["duration_s"]=h*3600d+m*60d+s;}Load(Repository.Save(current,publish));RefreshList();Status=publish?"已发布到配方目录。":"草稿已保存。";});
    [RelayCommand] void Delete()=>Run(()=>{if(current is null)return;Repository.Delete(current);current=null;Nodes.Clear();Materials.Clear();RefreshList();OnPropertyChanged(nameof(HasCurrent));Status="任务链已删除。";});
    [RelayCommand] void AddMaterial()=>Run(()=>{if(current is null||!IsProduction)return;var row=new JsonObject{["item_id"]=0,["name"]="",["quantity"]=1};current["parameters"]!["inputs"]!.AsArray().Add(row);var vm=new MaterialRow(row);vm.PropertyChanged+=MaterialChanged;Materials.Add(vm);MaterialChanged(this,new("Name"));});
    [RelayCommand] void RemoveMaterial(MaterialRow? row)=>Run(()=>{if(current is null||row is null)return;current["parameters"]!["inputs"]!.AsArray().Remove(row.Data);Materials.Remove(row);MaterialChanged(this,new("Name"));});
    [RelayCommand] void RemoveNode()=>Run(()=>{if(current is null||SelectedNode is null)return;current["nodes"]!.AsArray().Remove(SelectedNode.Data);RefreshNodes();Status="已从草稿删除节点，发布时核验必需步骤。";});
    [RelayCommand] void RestoreNodes()=>Run(()=>{if(current is null)return;var original=Repository.CreateDraft(EditorRepository.Text(current,"template_key"));var live=current["nodes"]!.AsArray().ToDictionary(n=>EditorRepository.Text(n!,"id"));current["nodes"]=new JsonArray(original["nodes"]!.AsArray().Select(n=>live.GetValueOrDefault(EditorRepository.Text(n!,"id"),n)!.DeepClone()).ToArray());RefreshNodes();});
    [RelayCommand] void RestorePreset()=>Run(()=>{if(current is not null){if(HasUnsavedChanges&&!AllowDiscard(RestorePreset))return;Load(Repository.RestorePreset(current));baseline="";Status="已载入出厂预设，保存后生效。";}});
    [RelayCommand] void RestoreVersion()=>Run(()=>{if(current is null||SelectedVersion is null)return;if(HasUnsavedChanges&&!AllowDiscard(RestoreVersion))return;var old=(JsonObject)SelectedVersion.Document.DeepClone();old["revision"]=current["revision"]?.DeepClone();Load(old);baseline="";Status="已载入历史版本，保存后生效。";});
    [RelayCommand] void RemoveVariant()=>Run(()=>{if(SelectedAnchor is null||SelectedVariant is null)return;SelectedAnchor.Data["variants"]!.AsArray().Remove(SelectedVariant.Data);SelectedAnchor.Refresh();SelectedVariant=SelectedAnchor.Variants.FirstOrDefault();});
    public void SetVariant(JsonObject asset,int[] box,bool append)
    {if(SelectedAnchor is null)return;var variant=SelectedVariant is null||append?new JsonObject{["match_mode"]="light_text",["threshold"]=.97}:SelectedVariant.Data;variant["asset"]=asset.DeepClone();variant["box"]=new JsonArray(box.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray());variant["search"]=variant["box"]!.DeepClone();if(append||SelectedVariant is null)SelectedAnchor.Data["variants"]!.AsArray().Add(variant);SelectedAnchor.Refresh();SelectedVariant=SelectedAnchor.Variants.LastOrDefault();Status="已更新原生裁图，请核对搜索区域。";}
}



