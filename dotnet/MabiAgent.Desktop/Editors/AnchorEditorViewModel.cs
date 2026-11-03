using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using Avalonia.Collections;
namespace MabiAgent.Desktop.Editors;
public sealed partial class AnchorEditorViewModel:ObservableObject
{
    public EditorRepository Repository{get;}public IEditorCaptureService? CaptureService{get;}public IAnchorTextDetector? TextDetector{get;}
    public IAnchorTestService TestService{get;}
    public ObservableCollection<LogicEntry> Logic{get;}=[];
    private LogicEntry? selectedLogic;
    private string specBaseline="",logicBaseline="";
    public LogicEntry? SelectedLogic
    {
        get=>selectedLogic;
        set{if(ReferenceEquals(value,selectedLogic))return;if(LogicJson!=logicBaseline&&!AllowDiscard(()=>SelectedLogic=value)){OnPropertyChanged();return;}if(SetProperty(ref selectedLogic,value))OnSelectedLogicChanged(value);}
    }
    [ObservableProperty] private string logicJson="";
    [ObservableProperty] private string specJson="";
    public ObservableCollection<AnchorEntry> Anchors{get;}=[];public ObservableCollection<AnchorReference> References{get;}=[];
    private AnchorEntry? selected;
    private string baseline="";
    public Func<bool> ConfirmDiscard{get;set;}=()=>false;
    public Action<Action>? RequestDiscard{get;set;}
    bool AllowDiscard(Action retry){if(ConfirmDiscard())return true;RequestDiscard?.Invoke(retry);return false;}
    public bool HasUnsavedChanges=>(Fields is not null&&Fields.Data.ToJsonString()!=baseline)||SpecJson!=specBaseline||LogicJson!=logicBaseline;
    public AnchorEntry? Selected
    {
        get=>selected;
        set{if(ReferenceEquals(value,selected))return;if(HasUnsavedChanges&&!AllowDiscard(()=>Selected=value)){OnPropertyChanged();return;}if(SetProperty(ref selected,value))OnSelectedChanged(value);}
    }
    public DataGridCollectionView AnchorView{get;}
    [ObservableProperty] private string search="";
    partial void OnSearchChanged(string? oldValue,string newValue)
    {
        AnchorView.Refresh();
        if(Selected is {} anchor&&!AnchorView.Contains(anchor))
        {
            if(HasUnsavedChanges&&!AllowDiscard(()=>Search=newValue)){Search=oldValue??"";Status="当前修改尚未保存，请先保存或确认放弃后筛选。";return;}
            Selected=null;
        }
    }
    [ObservableProperty] private AnchorReference? selectedReference;
    [ObservableProperty] private VariantRow? fields;
    [ObservableProperty] private bool independent;
    [ObservableProperty] private string status="";
    public int Revision{get;private set;}
    public void ReportError(Exception error,string operation="锚点操作"){Repository.ReportFault(error,operation,Selected?.Id);Status=error.Message;}
    public AnchorEditorViewModel(EditorRepository repository,IEditorCaptureService? captureService=null,IAnchorTextDetector? textDetector=null){Repository=repository;CaptureService=captureService;TextDetector=textDetector;TestService=new NativeAnchorTestService(repository);AnchorView=new DataGridCollectionView(Anchors);AnchorView.Filter=x=>x is AnchorEntry a&&(a.Label.Contains(Search,StringComparison.OrdinalIgnoreCase)||a.Id.Contains(Search,StringComparison.OrdinalIgnoreCase));Reload();}
    private void OnSelectedChanged(AnchorEntry? value){References.Clear();if(value is null){Fields=null;baseline="";SpecJson=specBaseline="";return;}Fields=new((JsonObject)value.Spec.DeepClone());baseline=Fields.Data.ToJsonString();SpecJson=value.Spec.ToJsonString(new System.Text.Json.JsonSerializerOptions{WriteIndented=true});specBaseline=SpecJson;foreach(var r in value.References)References.Add(r);SelectedReference=References.FirstOrDefault();Independent=false;}
    [RelayCommand] void Reload(){try{if(HasUnsavedChanges&&!AllowDiscard(Reload))return;baseline=Fields?.Data.ToJsonString()??"";specBaseline=SpecJson;logicBaseline=LogicJson;var logicPath=SelectedLogic?.Path;var id=Selected?.Id;Revision=Repository.DebugState()["revision"]!.GetValue<int>();Logic.Clear();foreach(var l in Repository.LogicEntries())Logic.Add(l);SelectedLogic=Logic.FirstOrDefault(l=>l.Path==logicPath)??Logic.FirstOrDefault();Anchors.Clear();foreach(var a in Repository.Anchors())Anchors.Add(a);Selected=Anchors.FirstOrDefault(a=>a.Id==id)??Anchors.FirstOrDefault();}catch(Exception e){ReportError(e);}}
    [RelayCommand] void Save(){try{if(Selected is null||Fields is null)return;Revision=Repository.SaveAnchor(Revision,Selected,Fields.Data,Independent,SelectedReference);baseline=Fields.Data.ToJsonString();specBaseline=SpecJson;Reload();Status="锚点已保存。";}catch(Exception e){ReportError(e);}}
    [RelayCommand] void RestorePreset(){try{if(HasUnsavedChanges&&!AllowDiscard(RestorePreset))return;baseline=Fields?.Data.ToJsonString()??"";specBaseline=SpecJson;logicBaseline=LogicJson;Revision=Repository.RestoreAnchors(Revision,0);Reload();Status="已恢复预设。";}catch(Exception e){ReportError(e);}}
    private void OnSelectedLogicChanged(LogicEntry? value){LogicJson=value?.Value?.ToJsonString(new System.Text.Json.JsonSerializerOptions{WriteIndented=true})??"";logicBaseline=LogicJson;}
    [RelayCommand] void SaveLogic(){try{if(SelectedLogic is null)return;Revision=Repository.SaveLogic(Revision,SelectedLogic.Path,JsonNode.Parse(LogicJson));logicBaseline=LogicJson;Reload();Status="已保存逻辑版本。";}catch(Exception e){ReportError(e);}}
    [RelayCommand] void ApplySpec(){try{var spec=JsonNode.Parse(SpecJson)!.AsObject();Fields=new(spec);Status="完整参数已载入表单；保存锚点后生效。";}catch(Exception e){ReportError(e);}}
    public void SetImage(JsonObject asset,int[] box)
    {if(Fields is null)return;Fields.Data["path"]=Repository.AssetPath(EditorRepository.Text(asset,"id"));Fields.Data["sha256"]=asset["sha256"]!.DeepClone();Fields.Data["bytes"]=asset["bytes"]!.DeepClone();Fields.Box=string.Join(",",box);Fields.Search=Fields.Box;Status="图片已载入，保存后应用所选范围。";}
}

