using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MabiAgent.Core;
namespace MabiAgent.Desktop;
public sealed class Workspace(string root,string? stateDirectory=null)
{
    internal event Action? Changed;
    private readonly object gate=new();
    private JsonObject? catalog;
    public Func<JsonObject>? CatalogProvider {get;set;}
    private JsonObject LoadCatalog()=>CatalogProvider?.Invoke()??new CatalogLoader(Root).Load("config/screen_catalog.1280x960.tw.json");
    private string? queueVersion,settingsVersion;
    public string Root {get;}=Path.GetFullPath(root);
    public string DataPath {get;}=Path.GetFullPath(stateDirectory??Path.Combine(root,"data/agent-run/desktop"));
    private string QueuePath=>Path.Combine(DataPath,"work-queue.json");
    private string SettingsPath=>Path.Combine(DataPath,"settings.json");
    private static string? Version(string path)=>File.Exists(path)?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))):null;
    public JsonObject ReadSettings()
    {
        lock(gate)
        {
            var raw=File.Exists(SettingsPath)?File.ReadAllBytes(SettingsPath):null;
            settingsVersion=raw is null?null:Convert.ToHexString(SHA256.HashData(raw));
            return raw is null ? DefaultSettings() : JsonNode.Parse(raw)!.AsObject();
        }
    }
    private static JsonObject DefaultSettings()=>JsonNode.Parse("""
        {"schema_version":1,"repair":{"enabled":true,"interval_minutes":30,"gold_reserve_min":40000,"auto_buy_bottles":true,"blocked_policy":"dependent_tasks","quote_policy":"retry_then_repair_once","quote_read_attempts":3},"backpack":{"enabled":true,"threshold_percent":90,"preserve_other_selections":true,"sell_surplus_materials":true,"material_reserve_min":300,"material_reserves":{}},"travel":{"auto_use_wings":true,"wing_reserve_min":0},"input":{"scheme":"touch","center_offset":true},"ui":{"theme":"system"}}
        """)!.AsObject();
    public void SaveRepairPreferences(bool repairEnabled,int minutes,long reserve,bool autoBottles)
        =>SaveSettingsPatch(settings=>PatchRepair(settings,repairEnabled,minutes,reserve,autoBottles));
    public void SavePreferences(DesktopPreferences p)
    {
        p.Validate();
        SaveSettingsPatch(settings=>
        {
            Section(settings,"production")["life_vip"]=p.LifeVip;
            PatchRepair(settings,p.RepairEnabled,p.RepairMinutes,p.GoldReserve,p.AutoBuyBottles);
            var bag=Section(settings,"backpack");
            bag["enabled"]=p.BackpackEnabled;bag["threshold_percent"]=p.BackpackThreshold;
            bag["sell_surplus_materials"]=p.SellSurplus;bag["material_reserve_min"]=p.MaterialReserve;
            if(p.MaterialReserves is not null)bag["material_reserves"]=new JsonObject(p.MaterialReserves.Select(x=>new KeyValuePair<string,JsonNode?>(x.Key,JsonValue.Create(x.Value))));
            if(p.Theme is not null)Section(settings,"ui")["theme"]=p.Theme;
            Section(settings,"recognition")["low_cpu"]=p.RecognitionLowCpu;
            var input=Section(settings,"input");input["scheme"]=p.InputScheme;input["center_offset"]=p.TouchCenterOffset;
            var travel=Section(settings,"travel");
            travel["auto_use_wings"]=p.AutoUseWings;travel["wing_reserve_min"]=p.WingReserve;
        });
    }
    // Empty list means every available capture device.
    public void SaveCaptureDevices(IEnumerable<string> devices)
        =>SaveSettingsPatch(settings=>Section(settings,"capture")["devices"]=new JsonArray(devices.Where(d=>!string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.Ordinal).Take(64).Select(d=>(JsonNode?)JsonValue.Create(d)).ToArray()));
    private static JsonObject Section(JsonObject settings,string key)
    {if(settings[key] is not JsonObject)settings[key]=new JsonObject();return settings[key]!.AsObject();}
    private static void PatchRepair(JsonObject settings,bool enabled,int minutes,long reserve,bool bottles)
    {
        if(minutes is <1 or >1440 || reserve is <0 or >FormNumbers.Maximum)throw new ArgumentException("修理间隔或金币留存超出范围");
        var repair=Section(settings,"repair");
        repair["enabled"]=enabled;repair["interval_minutes"]=minutes;
        repair["gold_reserve_min"]=reserve;repair["auto_buy_bottles"]=bottles;
    }
    private void SaveSettingsPatch(Action<JsonObject> patch)
    {
        lock(gate)
        {
            Directory.CreateDirectory(DataPath);
            using var lease=new FileStream(SettingsPath+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose);
            if(settingsVersion!=Version(SettingsPath))throw new IOException("设置已被其他程序修改，请刷新后再编辑");
            var settings=File.Exists(SettingsPath)?JsonNode.Parse(File.ReadAllBytes(SettingsPath))!.AsObject():DefaultSettings();
            patch(settings);
            AtomicJson.Write(SettingsPath,settings);settingsVersion=Version(SettingsPath);
        }
        Changed?.Invoke();
    }
    public void EditQueue(Action<QueueDocument> edit)
    {
        lock(gate)
        {
            using var queue=new QueueStore(QueuePath,queueVersion,true);
            queue.Update(edit);queueVersion=Version(QueuePath);
        }
        Changed?.Invoke();
    }
    public void AddProduction(string recipeKey,long quantity)
    {
        lock(gate)
        {
            var config=catalog??=LoadCatalog();
            var recipe=config["recipes"]?[recipeKey]??throw new ArgumentException("配方不存在，请刷新");
            EditQueue(q=>q.Add("user:"+Guid.NewGuid().ToString("N"),"production",recipe["output_item_id"]!.GetValue<long>(),quantity,recipeKey,[]));
        }
    }
    public void ControlJob(string id,string command)
    {
        lock(gate)
        {
            var config=catalog??=LoadCatalog();var recipes=config["recipes"]!.AsObject();
            EditQueue(q=>
            {
                var selected=q.Jobs.Single(j=>j.Id==id);
                if(command=="cancel"&&id.StartsWith("auto-material:",StringComparison.Ordinal))
                {
                    bool Needs(string? key,HashSet<string> seen)
                    {
                        if(key is null||!seen.Add(key)||recipes[key] is not {} recipe)return false;
                        foreach(var material in recipe["materials"]?.AsArray()??[])
                        {
                            var item=material!["item_id"]!.GetValue<long>();if(item==selected.ItemId)return true;
                            if(recipes.Any(r=>r.Value!["output_item_id"]!.GetValue<long>()==item&&Needs(r.Key,seen)))return true;
                        }
                        return false;
                    }
                    foreach(var root in q.Jobs.Where(j=>!j.Id.StartsWith("auto-material:",StringComparison.Ordinal)&&!j.Cancelled&&j.Kind=="production"&&Needs(j.Recipe,[])))q.Control(root.Id,"pause");
                }
                q.Control(id,command);
            });
        }
    }
    public void DeleteJobs(IEnumerable<string> ids)
    {
        var selected=ids.ToHashSet(StringComparer.Ordinal);
        lock(gate)
        {
            var recipes=(catalog??=LoadCatalog())["recipes"]!.AsObject();
            EditQueue(q=>
            {
                var items=q.Jobs.Where(j=>selected.Contains(j.Id)&&j.Id.StartsWith("auto-material:",StringComparison.Ordinal)).Select(j=>j.ItemId).ToHashSet();
                bool Needs(string? key,HashSet<string> seen)
                {
                    if(key is null||!seen.Add(key)||recipes[key] is not{} recipe)return false;
                    return (recipe["materials"]?.AsArray()??[]).Any(m=>items.Contains(m!["item_id"]!.GetValue<long>())||recipes.Any(r=>r.Value!["output_item_id"]!.GetValue<long>()==m!["item_id"]!.GetValue<long>()&&Needs(r.Key,seen)));
                }
                foreach(var root in q.Jobs.Where(j=>!selected.Contains(j.Id)&&!j.Cancelled&&!j.Id.StartsWith("auto-material:",StringComparison.Ordinal)&&j.Kind=="production"&&Needs(j.Recipe,[])))q.Control(root.Id,"pause");
                q.Delete(selected);
            });
        }
    }
    public void AddGathering(string kind,string routeKey,long quantity)
    {
        if(kind is not ("gathering" or "idle_fishing"))throw new ArgumentException("任务类型无效");
        lock(gate)
        {
            if(quantity<=0)throw new ArgumentException("数量或时长须大于零");
            if(kind=="idle_fishing"&&routeKey==MabiAgent.Workflows.FishingPlan.CurrentLocation)
            {EditQueue(q=>q.Add("user:"+Guid.NewGuid().ToString("N"),kind,1,quantity,routeKey,[]));return;}
            var config=catalog??=LoadCatalog();var route=config["gathering_routes"]?[routeKey]??throw new ArgumentException("采集点不存在，请刷新");var fishing=routeKey.StartsWith("point_fishing_",StringComparison.Ordinal)||route["skill"]?.GetValue<string>() is "fishing" or "釣魚" or "钓鱼";
            if(fishing!=(kind=="idle_fishing"))throw new ArgumentException("采集点类型不匹配");EditQueue(q=>q.Add("user:"+Guid.NewGuid().ToString("N"),kind,route["target_item_id"]!.GetValue<long>(),quantity,routeKey,[]));
        }
    }
    public WorkspaceSnapshot Read(bool refreshCatalog=false)
    {
        lock(gate)
        {
            if(refreshCatalog)catalog=null;
            var config=catalog??=LoadCatalog();
            if(queueVersion is not null&&!File.Exists(QueuePath))throw new IOException("队列文件暂不可用，保留现有任务");
            var raw=File.Exists(QueuePath)?File.ReadAllBytes(QueuePath):null;
            var queue=raw is null?null:new QueueDocument(JsonNode.Parse(raw)!.AsObject());
            queueVersion=raw is null?null:Convert.ToHexString(SHA256.HashData(raw));
            var names=config["item_names"]!.AsObject().ToDictionary(p=>long.Parse(p.Key),p=>p.Value!.GetValue<string>());
            var jobNames=(queue?.Jobs??[]).ToDictionary(j=>j.Id,j=>names.GetValueOrDefault(j.ItemId)??$"未关联道具 {j.ItemId}");
            var dependencies=(queue?.ToJson()["jobs"]?.AsArray()??[]).ToDictionary(j=>j!["job_id"]!.GetValue<string>(),j=>string.Join("、",j!["depends_on"]!.AsArray().Select(d=>jobNames.GetValueOrDefault(d!.GetValue<string>())??d!.GetValue<string>())));
            var recovered=(queue?.ToJson()["jobs"]?.AsArray()??[]).Where(j=>j!["recovery"]?["confirmed_quantity_baseline"] is not null).Select(j=>j!["job_id"]!.GetValue<string>()).ToHashSet();
            var actions=(queue?.ToJson()["jobs"]?.AsArray()??[]).ToDictionary(j=>j!["job_id"]!.GetValue<string>(),j=>j!["pending"]?["action_id"]?.GetValue<string>());
            var recipes=config["recipes"]!.AsObject().Where(p=>config["custom_chain_issues"]?[p.Key] is null).Select(p=>new RecipeRow(p.Key,
                names.GetValueOrDefault(p.Value!["output_item_id"]!.GetValue<long>())??p.Value["name"]?.GetValue<string>()??p.Key,
                DeviceName(p.Value!["device"]!.GetValue<string>()),p.Value!["duration_s"]!.GetFiniteDouble(),p.Value!["output_per_round"]!.GetValue<long>())).ToArray();
            var jobs=(queue?.Jobs??[]).Select((j,index)=>new JobRow(j.Id,j.Kind=="idle_fishing"&&j.Recipe==MabiAgent.Workflows.FishingPlan.CurrentLocation?"当前位置":names.GetValueOrDefault(j.ItemId)??$"未关联道具 {j.ItemId}",
                j.Kind switch{"production"=>"生产","gathering"=>"采集",_=>j.Recipe==MabiAgent.Workflows.FishingPlan.CurrentLocation?"原地钓鱼":"挂机钓鱼"},j.Target,j.Completed,
                (config["custom_chain_issues"]?[j.Recipe??""] is not null?"任务链需更新":j.Kind=="idle_fishing"&&j.Pending?(j.Cancelled?"已取消 · 等待钓鱼轮次结束":j.Paused?"已暂停 · 保留钓鱼进度":"钓鱼进行中"):StatusName(queue!.Status(j.Id)))+(recovered.Contains(j.Id)?" · 历史收据缺失":""),j.Id.StartsWith("auto-material:"),index+1,j.Pending,j.Paused,j.Cancelled,dependencies.GetValueOrDefault(j.Id,""),actions.GetValueOrDefault(j.Id))).ToArray();
            var gathering=(config["gathering_routes"]?.AsObject()??[]).Where(p=>config["custom_chain_issues"]?[p.Key] is null&&p.Value?["alias_of"] is null).Select(p=>new GatheringChoice(p.Key,config["gathering_points"]?[p.Key]?["name"]?.GetValue<string>()??p.Value!["node"]!.GetValue<string>(),p.Key.StartsWith("point_fishing_",StringComparison.Ordinal)||p.Value!["skill"]?.GetValue<string>() is "fishing" or "釣魚" or "钓鱼",p.Value!["target_item_id"]!.GetValue<long>())).ToArray();
            return new(jobs,recipes,gathering);
        }
    }
    public IReadOnlyDictionary<string,string> ReadItemNames()
    {lock(gate){var config=catalog??=LoadCatalog();var names=config["item_names"]!.AsObject().ToDictionary(x=>x.Key,x=>x.Value!.GetValue<string>());
        if(config["decomposition"] is{} decomposition&&decomposition["input_wire_id"] is{} input&&decomposition["input_name"] is{} name)names[input.ToString()]=name.GetValue<string>();return names;}}
    private static string StatusName(string status)=>status switch{"queued"=>"待执行","waiting_dependencies"=>"等待前置任务","reconciliation_required"=>"批次进行中","cancelled"=>"已取消 · 关联记录待结清","complete"=>"已完成","paused"=>"已暂停",_=>"状态待核对"};
    public static string DeviceName(string name)=>name switch{"metal"=>"金属加工","wood"=>"木材加工","fabric"=>"布料加工","leather"=>"皮革加工","chemical"=>"药品加工","food"=>"食材加工",_=>name};
}
public sealed record JobRow(string Id,string Name,string Kind,long Target,long Completed,string Status,bool Automatic,int Order,bool Pending,bool Paused,bool Cancelled,string Dependencies="",string? ActionId=null) : System.ComponentModel.INotifyPropertyChanged
{
    public string Name {get;set;}=Name;
    public string Kind {get;set;}=Kind;
    public long Target {get;set;}=Target;
    public long Completed {get;set;}=Completed;
    public string Status {get;set;}=Status;
    public bool Automatic {get;set;}=Automatic;
    public int Order {get;set;}=Order;
    public bool Pending {get;set;}=Pending;
    public bool Paused {get;set;}=Paused;
    public bool Cancelled {get;set;}=Cancelled;
    public string Dependencies {get;set;}=Dependencies;
    public string? ActionId {get;set;}=ActionId;
    private string queueStatus=Status;
    private string? targetDraft;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string TargetInput {get=>targetDraft??(IsFishing?$"{Target/3600}:{Target%3600/60:00}:{Target%60:00}":Target.ToString());set{targetDraft=value;PropertyChanged?.Invoke(this,new(nameof(TargetInput)));}}
    public bool CanEditTarget=>!Automatic&&!Pending&&!Cancelled;
    public bool HasTargetDraft=>targetDraft is not null;
    public string TargetHint=>IsFishing?"钓鱼时长：时:分:秒，回车或失焦保存":Kind=="采集"?"目标轮数；1轮＝10次采集尝试，不代表材料产量。回车或失焦保存":"目标产物件数，回车或失焦保存";
    public string QuantityUnit=>Kind=="采集"?"轮":IsFishing?"时长":"件";
    public string InputUnit=>IsFishing?"时:分:秒":QuantityUnit;
    public void ResetTargetDraft(){targetDraft=null;PropertyChanged?.Invoke(this,new(nameof(TargetInput)));}
    internal void UpdateFrom(JobRow next)
    {
        Name=next.Name;Kind=next.Kind;Target=next.Target;Completed=next.Completed;Status=next.Status;
        Automatic=next.Automatic;Order=next.Order;Pending=next.Pending;Paused=next.Paused;Cancelled=next.Cancelled;Dependencies=next.Dependencies;ActionId=next.ActionId;queueStatus=next.queueStatus;
        PropertyChanged?.Invoke(this,new(null));
    }
    public void ApplyRuntime(MabiAgent.Workflows.AutomationJobStatus? progress)
    {
        var next=queueStatus;
        if(Pending&&!Cancelled&&!Paused&&progress is not null&&progress.ActionId==ActionId&&!queueStatus.Contains("需更新",StringComparison.Ordinal))
            next=progress.Status=="unknown"?"本批结果待核账":progress.Status=="paused"?"已暂停":progress.PendingConsumption?"动作结果确认中":progress.Phase=="waiting"?"设备加工中":progress.Phase=="leave"?"正在离开设备":progress.Phase is "preview_queue" or "preview_recipe" or "queue" or "navigate"?"正在前往或检查设备":progress.Phase=="recipe"?"正在安排生产":queueStatus;
        if(next!=queueStatus&&queueStatus.EndsWith(" · 历史收据缺失",StringComparison.Ordinal))next+=" · 历史收据缺失";
        if(Status==next)return;Status=next;PropertyChanged?.Invoke(this,new(null));
    }

    public bool IsFishing=>Kind is "挂机钓鱼" or "原地钓鱼";
    public bool IsCompleteState=>!Cancelled&&!IsWarningState&&Completed>=Target;
    public bool IsWarningState=>!Cancelled&&(Paused||Status.Contains("待确认",StringComparison.Ordinal)||Status.Contains("待核账",StringComparison.Ordinal)||Status=="已暂停"||Status.Contains("需更新",StringComparison.Ordinal)||Status.Contains("缺失",StringComparison.Ordinal)||Status.Contains("待核对",StringComparison.Ordinal));
    public bool IsActiveState=>!Cancelled&&!Paused&&!IsWarningState&&!IsCompleteState&&Pending;
    public string TargetLabel=>IsFishing?Duration(Target):$"{Target}{QuantityUnit}";
    public string CompletedLabel=>IsFishing?Duration(Completed):$"{Completed}{QuantityUnit}";
    public string RemainingLabel=>IsFishing?Duration(Math.Max(0,Target-Completed)):$"{Math.Max(0,Target-Completed)}"+(Kind=="采集"?"轮":"件");
    public string DependencyLabel=>string.IsNullOrEmpty(Dependencies)?"前置任务：无":"前置任务："+Dependencies;
    public static string Duration(long s)=>$"{s/3600}时 {s%3600/60}分 {s%60}秒";
}
public sealed record RecipeRow(string Id,string Name,string Device,double Duration,long Output,bool LifeVip=false)
{public string Label=>$"{Device} · {Name}";public double EffectiveDuration=>MabiAgent.Workflows.ProductionTiming.Duration(Duration,LifeVip);public string DurationLabel=>$"{(long)EffectiveDuration/3600:00}:{(long)EffectiveDuration%3600/60:00}:{EffectiveDuration%60:00.#}"+(LifeVip?"（VIP）":"");}
public sealed record GatheringChoice(string Id,string Name,bool Fishing,long ItemId);
public sealed record WorkspaceSnapshot(JobRow[] Jobs,RecipeRow[] Recipes,GatheringChoice[]? Gathering=null);
public sealed record DesktopPreferences(bool RepairEnabled,int RepairMinutes,long GoldReserve,bool AutoBuyBottles,
    bool BackpackEnabled,int BackpackThreshold,bool SellSurplus,long MaterialReserve,bool AutoUseWings,long WingReserve,IReadOnlyDictionary<string,long>? MaterialReserves=null,string? Theme=null,bool RecognitionLowCpu=false,string InputScheme="touch",bool TouchCenterOffset=true,bool LifeVip=false)
{
    public bool IsExplicitSurplusMaterial(long item)=>MaterialReserves?.ContainsKey(item.ToString(System.Globalization.CultureInfo.InvariantCulture))==true;
    public long? RequiredMaterialReserve(long item,long? queueRequired)
    {
        if(queueRequired is null or <0)return null;
        var configured=MaterialReserves is not null&&MaterialReserves.TryGetValue(item.ToString(System.Globalization.CultureInfo.InvariantCulture),out var individual)?individual:MaterialReserve;
        return configured<0?null:Math.Max(queueRequired.Value,configured);
    }
    public void Validate()
    {
        if(RepairMinutes is <1 or >1440||BackpackThreshold is <1 or >100||GoldReserve is <0 or >FormNumbers.Maximum
            ||MaterialReserve is <0 or >FormNumbers.Maximum||WingReserve is <0 or >FormNumbers.Maximum)
            throw new ArgumentException("请填写有效的间隔、容量及留存数量");
        if(MaterialReserves?.Any(x=>!long.TryParse(x.Key,out var id)||id<=0||x.Value<0||x.Value>FormNumbers.Maximum)==true)throw new ArgumentException("材料留存须使用有效道具与非负整数");
        if(InputScheme is not("touch" or "mouse"))throw new ArgumentException("操作方案无效");
        if(Theme is not null and not ("system" or "light" or "dark"))throw new ArgumentException("主题选项无效");
    }
    public static DesktopPreferences FromJson(JsonObject s)=>new(
        s["repair"]?["enabled"]?.GetValue<bool>()??true,s["repair"]?["interval_minutes"]?.GetValue<int>()??30,
        s["repair"]?["gold_reserve_min"]?.GetValue<long>()??40000,s["repair"]?["auto_buy_bottles"]?.GetValue<bool>()??true,
        s["backpack"]?["enabled"]?.GetValue<bool>()??true,s["backpack"]?["threshold_percent"]?.GetValue<int>()??90,
        s["backpack"]?["sell_surplus_materials"]?.GetValue<bool>()??true,s["backpack"]?["material_reserve_min"]?.GetValue<long>()??300,
        s["travel"]?["auto_use_wings"]?.GetValue<bool>()??true,s["travel"]?["wing_reserve_min"]?.GetValue<long>()??0,
        (s["backpack"]?["material_reserves"]?.AsObject()??[]).ToDictionary(x=>x.Key,x=>x.Value!.GetValue<long>()),s["ui"]?["theme"]?.GetValue<string>()??"system",s["recognition"]?["low_cpu"]?.GetValue<bool>()??false,s["input"]?["scheme"]?.GetValue<string>()??"touch",s["input"]?["center_offset"]?.GetValue<bool>()??true,s["production"]?["life_vip"]?.GetValue<bool>()??false);
}
public static class FormNumbers
{
    public const long Maximum=9007199254740991;
    public static bool TryWhole(string? text,long min,long max,out long value)
        =>long.TryParse(text?.Trim(),System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out value)&&value>=min&&value<=max;
}


