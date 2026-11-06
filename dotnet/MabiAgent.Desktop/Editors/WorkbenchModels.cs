using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MabiAgent.Core;

namespace MabiAgent.Desktop.Editors;

public abstract class WorkbenchModel : INotifyPropertyChanged
{
    public JsonObject Data { get; }
    protected WorkbenchModel(JsonObject data) => Data = data;
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this,new(name));
        if(name==nameof(Name))PropertyChanged?.Invoke(this,new(nameof(DisplayName)));
        if(this is WorkbenchCard&&name!=nameof(WorkbenchCard.Summary))PropertyChanged?.Invoke(this,new(nameof(WorkbenchCard.Summary)));
        if(this is WorkbenchCard&&name==nameof(WorkbenchCard.Action))PropertyChanged?.Invoke(this,new(nameof(WorkbenchCard.IsSemantic)));
        if(this is WorkbenchAnchorState&&name==nameof(WorkbenchAnchorState.Mode)){PropertyChanged?.Invoke(this,new(nameof(WorkbenchAnchorState.ModeLabel)));PropertyChanged?.Invoke(this,new(nameof(WorkbenchAnchorState.IsImage)));PropertyChanged?.Invoke(this,new(nameof(WorkbenchAnchorState.IsCoordinate)));}
        if(this is WorkbenchAnchorState&&name==nameof(WorkbenchAnchorState.Shape))PropertyChanged?.Invoke(this,new(nameof(WorkbenchAnchorState.ShapeLabel)));
        if(this is WorkbenchChain&&name==nameof(WorkbenchChain.Kind))PropertyChanged?.Invoke(this,new(nameof(WorkbenchChain.KindLabel)));
        if(this is WorkbenchStrategy&&name==nameof(WorkbenchStrategy.Value))PropertyChanged?.Invoke(this,new(nameof(WorkbenchStrategy.DisplayValue)));
    }
    protected string Text(string key, string fallback = "") => Data[key]?.GetValue<string>() ?? fallback;
    protected bool Flag(string key, bool fallback = false) => Data[key]?.GetValue<bool>() ?? fallback;
    protected int Number(string key, int fallback = 0) => Data[key]?.GetValue<int>() ?? fallback;
    protected void Put(string key, string value, [CallerMemberName] string? property = null) { value??="";if(Text(key)==value)return;Data[key] = value; Notify(property); }
    protected void Put(string key, bool value, [CallerMemberName] string? property = null) { Data[key] = value; Notify(property); }
    protected void Put(string key, int value, [CallerMemberName] string? property = null) { Data[key] = value; Notify(property); }
    protected static ObservableCollection<T> Rows<T>(JsonObject data, string key, Func<JsonObject,T> create) => new((data[key] as JsonArray ?? []).OfType<JsonObject>().Select(create));
    protected static JsonArray Array<T>(IEnumerable<T> rows) where T : WorkbenchModel => new(rows.Select(row => (JsonNode)row.ToJson()).ToArray());
    public virtual JsonObject ToJson() => (JsonObject)Data.DeepClone();
    public string Id { get => Text("id"); set => Put("id", value); }
    public string Name { get => Text("name"); set => Put("name", value); }
    public virtual string DisplayName {get=>WorkbenchLabels.Localize(WorkbenchLabels.Friendly(Name));set{if(value==DisplayName)return;Name=value;}}
    public void RefreshDisplay()
    {
        foreach(var name in new[]{nameof(DisplayName),"KindLabel","Summary","StageLabel","ModeLabel","ShapeLabel","CategoryLabel","ParameterLabel","DescriptionLabel","DisplayValue"})
            PropertyChanged?.Invoke(this,new(name));
    }
    public static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N")[..16];
    public static string[] SplitIds(string text) => text.Split([',','，',';','；','\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
}

public sealed class WorkbenchDocument : WorkbenchModel
{
    public WorkbenchDocument(JsonObject data) : base(data)
    {
        Chains = Rows(data,"chains",x=>new WorkbenchChain(x)); Anchors = Rows(data,"anchors",x=>new WorkbenchAnchor(x));
        Pages = Rows(data,"pages",x=>new WorkbenchPage(x)); CardLibrary = Rows(data,"card_library",x=>new WorkbenchCard(x));
        Strategies = Rows(data,"strategies",x=>new WorkbenchStrategy(x));
    }
    public int Revision { get => Number("revision"); set => Put("revision",value); }
    public int Width { get => Number("width",1280); set => Put("width",value); }
    public int Height { get => Number("height",960); set => Put("height",value); }
    public int Dpi { get => Number("dpi",96); set => Put("dpi",value); }
    public ObservableCollection<WorkbenchChain> Chains { get; }
    public ObservableCollection<WorkbenchAnchor> Anchors { get; }
    public ObservableCollection<WorkbenchPage> Pages { get; }
    public ObservableCollection<WorkbenchCard> CardLibrary { get; }
    public ObservableCollection<WorkbenchStrategy> Strategies { get; }
    public WorkbenchAnchor? FindAnchor(string id) => Anchors.FirstOrDefault(a=>a.Id==id);
    public IReadOnlyList<WorkbenchCard> AnchorReferences(string id) => Chains.SelectMany(c=>c.Cards).Concat(CardLibrary).Where(c=>c.AnchorId==id||SplitIds(c.ConditionAnchorIds).Contains(id)||SplitIds(c.ExpectedAnchorIds).Contains(id)).ToArray();
    public WorkbenchAnchorState? ResolveState(WorkbenchCard card) => FindAnchor(card.AnchorId)?.States.FirstOrDefault(s=>s.Id==card.StateId) ?? FindAnchor(card.AnchorId)?.States.FirstOrDefault();
    public override JsonObject ToJson() { var j=base.ToJson();j["schema_version"]=1;j["chains"]=Array(Chains);j["anchors"]=Array(Anchors);j["pages"]=Array(Pages);j["card_library"]=Array(CardLibrary);j["strategies"]=Array(Strategies);return j; }
}

public sealed class WorkbenchChain : WorkbenchModel
{
    public WorkbenchChain(JsonObject data) : base(data) => Cards=Rows(data,"cards",x=>new WorkbenchCard(x));
    public ObservableCollection<WorkbenchCard> Cards { get; }
    public string Kind { get => Text("kind","navigation"); set=>Put("kind",value); }
    public string KindLabel=>WorkbenchLabels.Localize(WorkbenchLabels.Category(Kind));
    public override string DisplayName
    {
        get => HasOriginalBuiltInName()?WorkbenchLabels.Localize(WorkbenchLabels.ChainDisplayName(Kind,Name)):Name;
        set { if(value==DisplayName)return; Name=value; }
    }
    bool HasOriginalBuiltInName()
    {
        if(Id.StartsWith("installed:",StringComparison.Ordinal)&&Data["source"]?["name"] is JsonValue original&&original.TryGetValue<string>(out var sourceName)&&Name==sourceName)return true;
        if(Id.StartsWith("route:",StringComparison.Ordinal)&&Data["source_path"] is JsonArray path&&path.Count>0)
        {
            var page=path[^1]!.ToString();
            return Name==WorkbenchLabels.RouteChainName(Kind,page)||Name==WorkbenchLabels.Category(Kind)+" · "+WorkbenchLabels.Friendly(page);
        }
        return false;
    }
    public string RuntimeKey { get => Text("runtime_key"); set=>Put("runtime_key",value); }
    public bool Enabled { get => Flag("enabled"); set=>Put("enabled",value); }
    public string Description { get => Text("description"); set=>Put("description",value); }
    public WorkbenchCard AddCard(WorkbenchCard? source=null, int index=-1)
    {var card=source is null?new WorkbenchCard(new JsonObject{["id"]=NewId("card"),["name"]="新步骤",["action"]="observe"}):source.Copy();if(index<0||index>Cards.Count)index=Cards.Count;Cards.Insert(index,card);return card;}
    public void RemoveCards(IEnumerable<string> ids)
    {var selected=ids.ToHashSet(StringComparer.Ordinal);foreach(var card in Cards.Where(c=>selected.Contains(c.Id)).ToArray())Cards.Remove(card);foreach(var card in Cards){if(selected.Contains(card.SuccessId))card.SuccessId="";if(selected.Contains(card.FailureId))card.FailureId="";}}
    public void MoveCards(IEnumerable<string> ids,int insertionIndex)
    {var selected=ids.ToHashSet(StringComparer.Ordinal);var moving=Cards.Where(c=>selected.Contains(c.Id)).ToArray();var target=Math.Clamp(insertionIndex,0,Cards.Count);target-=Cards.Take(target).Count(c=>selected.Contains(c.Id));foreach(var card in moving)Cards.Remove(card);foreach(var card in moving)Cards.Insert(target++,card);}
    public override JsonObject ToJson(){var j=base.ToJson();j["cards"]=Array(Cards);return j;}
}

public sealed class WorkbenchCard : WorkbenchModel
{
    public WorkbenchCard(JsonObject data) : base(data) { }
    public string Action {get=>Text("action","observe");set=>Put("action",value);}
    public bool IsSemantic=>Action=="semantic";
    public string PageId {get=>Text("page_id");set=>Put("page_id",value);}
    public string ExpectedPageId {get=>Text("expected_page_id");set=>Put("expected_page_id",value);}
    public string Target {get=>Text("target");set=>Put("target",value);}
    public string TransitionId {get=>Text("transition_id");set=>Put("transition_id",value);}
    public string AnchorId {get=>Text("anchor_id");set=>Put("anchor_id",value);}
    public string StateId {get=>Text("state_id");set=>Put("state_id",value);}
    public string ConditionOperation {get=>Text("condition_operation","all");set=>Put("condition_operation",value);}
    public string ConditionAnchorIds {get=>Text("condition_anchor_ids");set=>Put("condition_anchor_ids",value);}
    public string ExpectedAnchorIds {get=>Text("expected_anchor_ids");set=>Put("expected_anchor_ids",value);}
    public string SuccessId {get=>Text("success_id");set=>Put("success_id",value);}
    public string FailureId {get=>Text("failure_id");set=>Put("failure_id",value);}
    public int RetryLimit {get=>Number("retry_limit");set=>Put("retry_limit",value);}
    public int TimeoutSeconds {get=>Data["timeout_seconds"] is null?checked((int)Math.Ceiling(Data["source"]?["timeout_s"]?.GetFiniteDouble()??15)):Number("timeout_seconds",15);set=>Put("timeout_seconds",value);}
    public int DelayMilliseconds {get=>Number("delay_ms",200);set=>Put("delay_ms",value);}
    public int KeyCode {get=>Number("key_code",32);set=>Put("key_code",value);}
    public int KeyDurationMilliseconds {get=>Number("key_duration_ms",80);set=>Put("key_duration_ms",value);}
    public string InputText {get=>Text("input_text");set=>Put("input_text",value);}
    public int Backspaces {get=>Number("backspaces");set=>Put("backspaces",value);}
    public int ScrollSteps {get=>Data["source"]?["steps"]?.GetValue<int>()??3;set{Data["source"]??=new JsonObject();Data["source"]!["steps"]=value;Notify();}}
    public string Destination {get=>Data["source"]?["destination"] is JsonArray array?string.Join(", ",array.Select(x=>x!.ToString())):Data["source"]?["destination"]?.ToString()??"";set{var p=value.Split([',','，',' '],StringSplitOptions.RemoveEmptyEntries);Data["source"]??=new JsonObject();Data["source"]!["destination"]=p.Length==2&&p.All(x=>int.TryParse(x,out _))?new JsonArray(p.Select(x=>(JsonNode?)JsonValue.Create(int.Parse(x))).ToArray()):JsonValue.Create(value);Notify();}}
    public bool RandomClick {get=>Flag("random_click");set{Put("random_click",value);Notify(nameof(RandomMode));}}
    public string RandomMode
    {
        get=>Data["random_click"] is null?"inherit":RandomClick?"on":"off";
        set{if(value is not("inherit" or "on" or "off"))throw new ArgumentException("点击方式无效");if(value==RandomMode)return;if(value=="inherit")Data.Remove("random_click");else Data["random_click"]=value=="on";Notify();Notify(nameof(RandomClick));}
    }
    public string StageLabel=>WorkbenchLabels.Localize(Text("stage") is {Length:>0} stage?"阶段 · "+WorkbenchLabels.Page(stage)+(Text("stage_collection")=="search"?" · 搜索":" · 操作"):"");
    public bool HasStage=>Text("stage").Length>0;
    public bool Enabled {get=>Flag("enabled",true);set=>Put("enabled",value);}
    public bool ConsumesResources {get=>Flag("consumes_resources");set=>Put("consumes_resources",value);}
    public string Role {get=>Text("role","navigation");set=>Put("role",value);}
    public string Summary => WorkbenchLabels.Localize(Action switch {"click"=>"点击 · "+WorkbenchLabels.Page(PageId),"right"=>"右键 · "+WorkbenchLabels.Page(PageId),"scroll"=>"滚动 · "+ScrollSteps+" 格","zoom_max"=>"放大到最大","drag"=>"拖动 · "+WorkbenchLabels.Page(PageId),"branch"=>"条件分支", "wait"=>"等待页面或锚点", "delay"=>"等待 "+DelayMilliseconds+" 毫秒","semantic"=>"任务动作 · "+WorkbenchLabels.Role(Role),_=>"识别 · "+WorkbenchLabels.Page(PageId)});
    public WorkbenchCard Copy(){var j=ToJson();j["id"]=NewId("card");j["success_id"]="";j["failure_id"]="";return new(j);}
}

public sealed class WorkbenchAnchor : WorkbenchModel
{
    public WorkbenchAnchor(JsonObject data):base(data)=>States=Rows(data,"states",x=>new WorkbenchAnchorState(x));
    public ObservableCollection<WorkbenchAnchorState> States {get;}
    public string PageId {get=>Text("page_id");set=>Put("page_id",value);}
    public bool InLibrary {get=>Flag("in_library");set=>Put("in_library",value);}
    public string Description {get=>Text("description");set=>Put("description",value);}
    public string DescriptionLabel=>WorkbenchLabels.Localize(WorkbenchLabels.Friendly(Description));
    public bool HasDescription=>!string.IsNullOrWhiteSpace(Description);
    public override JsonObject ToJson(){var j=base.ToJson();j["states"]=Array(States);return j;}
}

public sealed class WorkbenchAnchorState : WorkbenchModel
{
    public WorkbenchAnchorState(JsonObject data):base(data){}
    public string Mode {get=>Text("mode","image");set=>Put("mode",value);}
    public string ModeLabel=>Mode=="coordinate"?"固定坐标":"图片匹配";
    public bool IsImage=>Mode=="image";
    public bool IsCoordinate=>Mode=="coordinate";
    public string Shape {get=>Text("shape","rectangle");set=>Put("shape",value);}
    public string ShapeLabel=>Shape switch {"circle"=>"圆形","ellipse"=>"椭圆",_=>"矩形"};
    public string MatchMode {get=>Text("match_mode","grayscale");set=>Put("match_mode",value);}
    public string Path {get=>Text("path");set=>Put("path",value);}
    public string Sha256 {get=>Text("sha256");set=>Put("sha256",value);}
    public string Threshold {get=>Data["threshold"]?.ToString()??"0.97";set{Data["threshold"]=double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var d)&&double.IsFinite(d)?JsonValue.Create(d):JsonValue.Create(value);Notify();}}
    public string Box {get=>Region("box");set{PutRegion("box",value);Notify();}}
    public string Search {get=>Region("search");set{PutRegion("search",value);Notify();}}
    public string TemplateKey {get=>Text("template_key");set=>Put("template_key",value);}
    public bool RandomClick {get=>Flag("random_click");set=>Put("random_click",value);}
    string Region(string key)=>Data[key] is JsonArray array?string.Join(", ",array.Select(x=>x!.ToString())):Data[key]?.ToString()??"";
    void PutRegion(string key,string value){var p=value.Split([',','，',' '],StringSplitOptions.RemoveEmptyEntries);Data[key]=p.Length==4&&p.All(x=>int.TryParse(x,out _))?new JsonArray(p.Select(x=>(JsonNode?)JsonValue.Create(int.Parse(x))).ToArray()):JsonValue.Create(value);}
}

public sealed class WorkbenchPage : WorkbenchModel
{
    public WorkbenchPage(JsonObject data):base(data)=>Transitions=Rows(data,"transitions",x=>new WorkbenchTransition(x));
    public string AnchorIds {get=>Text("anchor_ids");set=>Put("anchor_ids",value);}
    public string MatchRule {get=>Text("match_rule","all");set=>Put("match_rule",value);}
    public ObservableCollection<WorkbenchTransition> Transitions {get;}
    public override JsonObject ToJson(){var j=base.ToJson();j["transitions"]=Array(Transitions);return j;}
}
public sealed class WorkbenchTransition : WorkbenchModel
{
    public WorkbenchTransition(JsonObject data):base(data){}
    public string DestinationPageId {get=>Text("destination_page_id");set=>Put("destination_page_id",value);}
    public string AnchorId {get=>Text("anchor_id");set=>Put("anchor_id",value);}
    public string Action {get=>Text("action","click");set=>Put("action",value);}
    public string Target {get=>Text("target");set=>Put("target",value);}
}
public sealed class WorkbenchStrategy : WorkbenchModel
{
    public WorkbenchStrategy(JsonObject data):base(data){}
    public string Category {get=>Text("category");set=>Put("category",value);}
    public string CategoryLabel=>WorkbenchLabels.Localize(WorkbenchLabels.Category(Category));
    public string ParameterLabel=>WorkbenchLabels.Localize(WorkbenchLabels.Parameter(Name));
    public string DescriptionLabel=>WorkbenchLabels.Localize(WorkbenchLabels.Friendly(Description));
    private System.Text.Json.JsonValueKind ParameterType=>(Data["original"]??Data["value"])?.GetValueKind()??System.Text.Json.JsonValueKind.Undefined;
    public bool IsBoolean=>ParameterType is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False;
    public bool BooleanValue {get=>IsBoolean&&Data["value"] is JsonValue current&&current.TryGetValue<bool>(out var enabled)&&enabled;set{if(!IsBoolean)return;Put("value",value,nameof(BooleanValue));Notify(nameof(Value));}}
    public string Value {get=>Data["value"] is JsonArray array?string.Join(", ",array.Select(x=>x!.ToString())):Data["value"]?.ToString()??"";set{if(IsCoordinate){var p=value.Split([',','，',' '],StringSplitOptions.RemoveEmptyEntries);Data["value"]=p.Length==(ValueKind is "point" or "relativepoint"?2:4)&&p.All(x=>int.TryParse(x,out _))?new JsonArray(p.Select(x=>(JsonNode?)JsonValue.Create(int.Parse(x))).ToArray()):JsonValue.Create(value);}else Data["value"]=ParameterType switch {System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False=>bool.TryParse(value,out var b)?JsonValue.Create(b):JsonValue.Create(value),System.Text.Json.JsonValueKind.Number=>double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var d)&&double.IsFinite(d)?JsonValue.Create(d):JsonValue.Create(value),_=>JsonValue.Create(value)};Notify();}}
    public string ValueKind=>Text("value_kind","string");
    public string DisplayValue
    {
        get=>Data["path"] is JsonArray path&&path.LastOrDefault()?.ToString()=="skill"?WorkbenchLabels.Localize(WorkbenchLabels.Friendly(Value)):Value;
        set
        {
            if(value==DisplayValue)return;
            if(Data["path"] is JsonArray path&&path.LastOrDefault()?.ToString()=="skill")
                value=new[]{"daily","logging","mining","harvest","herb","wool","fishing","hoeing","insect"}.FirstOrDefault(key=>value==WorkbenchLabels.Localize(WorkbenchLabels.Friendly(key)))??value;
            Value=value;
        }
    }
    public bool IsCoordinate=>ValueKind is "point" or "region" or "relativepoint" or "relativeregion";
    public bool IsRelative=>ValueKind is "relativepoint" or "relativeregion";
    public string Description {get=>Text("description");set=>Put("description",value);}
}

public static partial class WorkbenchLabels
{
    public static string Role(string key)=>key switch {"production"=>"加工委托","result"=>"到达与领取","gathering_start"=>"开始采集","gathering_result"=>"采集结果","navigation"=>"页面导航",_=>Friendly(key)};
    public static string ChainDisplayName(string kind,string name)
    {
        var category=Category(kind);var value=Friendly(name);var prefix=category+" · ";
        if(value==category)return category;
        if(value.StartsWith(prefix,StringComparison.Ordinal))return value;
        if(value.StartsWith(category,StringComparison.Ordinal))value=value[category.Length..].TrimStart(' ','·','：','-');
        return value.Length==0?category:prefix+value;
    }
    public static string RouteChainName(string kind,string page)=>Category(kind)+" · "+Page(page);
    public static string Category(string key)=>key switch {"production"=>"生产", "gathering" or "gathering_policy"=>"采集", "maintenance"=>"维护", "quantity"=>"核账", "fishing"=>"钓鱼", "travel_wings"=>"移动", "relogin"=>"重新登录", "purchase"=>"采购", "scheduler"=>"调度", "runtime"=>"运行", "navigation"=>"导航", _=>Friendly(key)};
    public static string Page(string key)=>key switch {"scene"=>"游戏主界面","skills"=>"技能菜单","category_page"=>"加工分类","recipe_detail"=>"配方详情","bag"=>"背包","bag_search"=>"背包搜索","world_map"=>"世界地图","region_map"=>"地区地图","repair_dialog"=>"维修对话","repair_quote"=>"维修报价","repair_equipment"=>"维修装备","repair_tools"=>"工具维修","repair_result"=>"维修结果","repair_travel"=>"维修导航","shop"=>"杂货店","shop_travel"=>"商店导航","storage"=>"仓库","storage_detail"=>"仓库详情","storage_transfer"=>"仓库提取","character_menu"=>"角色菜单","character_life_skills"=>"生活技能","tidy"=>"整理背包","tidy_result"=>"整理结果","metal_panel"=>"金属加工设备","wood_panel"=>"木材加工设备","fabric_panel"=>"纺织设备","food_panel"=>"烹饪设备","leather_panel"=>"皮革加工设备","chemical_panel"=>"化学加工设备",_=>Friendly(key)};
    public static string Parameter(string key)=>key switch {"enabled"=>"启用","interval_s" or "interval_seconds"=>"执行间隔（秒）","timeout_s" or "timeout_seconds"=>"超时时间（秒）","settle_s"=>"操作等待（秒）","retry_limit" or "max_retries"=>"最大重试次数","completion_idle_observations"=>"完成确认次数","completion_idle_seconds"=>"完成等待（秒）","duration_s"=>"持续时间（秒）","threshold" or "label_score"=>"匹配阈值","label_threshold"=>"文字亮度阈值","max_rounds"=>"最大轮数","minimum_equipped_tools"=>"最少装备工具数","gold_reserve"=>"金币保留量","material_reserve"=>"材料保留量","wing_reserve"=>"羽毛保留量","repair_minutes"=>"维修间隔（分钟）","backpack_threshold"=>"背包整理阈值","use_wings"=>"使用移动羽毛","steps"=>"滚动格数","target"=>"操作目标","page"=>"当前页面","expected_page"=>"预期页面","kind"=>"操作类型","name"=>"名称","attempts_per_batch"=>"每批次数","batch_timeout_s"=>"批次超时（秒）","cooldown_s"=>"冷却时间（秒）","count"=>"数量","reserve"=>"保留量","left"=>"左边界","top"=>"上边界","right"=>"右边界","bottom"=>"下边界",_=>Friendly(key)};
}
