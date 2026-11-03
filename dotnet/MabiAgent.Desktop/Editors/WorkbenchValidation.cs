using System.IO;
using System.Text.Json.Nodes;
using MabiAgent.Core;

namespace MabiAgent.Desktop.Editors;

public static class WorkbenchValidation
{
    static readonly HashSet<string> ConsumptionTargets=["production.craft","production.collect","gathering.start","repair.confirm","tidy.confirm","bag.auto_equip","purchase.confirm","bag.sale_confirm","travel.use_wings","fishing.start","fishing.reel","fishing.switch_mode","fishing.confirm_mode","storage.prepare","storage.confirm","storage.prepare_transfer","storage.confirm_transfer","relogin.menu","relogin.exit","relogin.character_select","relogin.enter"];
    public static void ValidateConsumptionTargets(WorkbenchDocument document,JsonObject? configuration=null)
    {
        var targets=new HashSet<string>(ConsumptionTargets,StringComparer.Ordinal);var templates=new HashSet<string>(ConsumptionTargets,StringComparer.Ordinal);
        templates.UnionWith(["craft_processing","maint_tidy_confirm_action","maint_repair_confirm","maint_auto_equip","storage_take_button","storage_transfer_confirm","purchase.purchase_label","surplus_sale_confirm","travel.wing_button","scene_menu","relogin.menu_exit","relogin.select_character","relogin.start_game"]);
        void Add(JsonNode? value){if(value is JsonValue v&&v.TryGetValue<string>(out var name)&&name.Length>0)targets.Add(name);}
        if(configuration is not null)
        {
            Add(configuration["purchase"]?["confirm_target"]);Add(configuration["ui"]?["storage_supply"]?["selection"]?["prepare_target"]);Add(configuration["ui"]?["storage_supply"]?["transfer"]?["confirm_target"]);
            foreach(var route in configuration["gathering_routes"]?.AsObject().Select(p=>p.Value)??[])Add(route?["navigation"]?["start_target"]);
            foreach(var recipe in configuration["recipes"]?.AsObject().Select(p=>p.Value)??[])if(recipe?["craft_anchor"] is JsonValue craft&&craft.TryGetValue<string>(out var key))templates.Add(key);
            foreach(var device in configuration["devices"]?.AsObject().Select(p=>p.Value)??[])foreach(var name in device?["collect_anchors"] as JsonArray??[])if(name is JsonValue label&&label.TryGetValue<string>(out var text))templates.Add(text);
            foreach(var section in new[]{"pages","page_variants"})foreach(var page in configuration["ui"]?[section]?.AsObject().Select(p=>p.Value)??[])foreach(var(target,value)in page?["targets"]?.AsObject()??[])if(targets.Contains(target)&&value is JsonValue template&&template.TryGetValue<string>(out var name))templates.Add(name);
        }
        var specs=configuration?["ui"]?["templates"]?.AsObject();var protectedImages=document.Anchors.SelectMany(a=>a.States).Where(s=>templates.Contains(s.TemplateKey)).Select(s=>s.Sha256).Where(s=>s.Length>0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var key in templates)if(specs?[key]?["sha256"] is JsonValue sha&&sha.TryGetValue<string>(out var hash)&&hash.Length>0)protectedImages.Add(hash);
        foreach(var card in document.Chains.SelectMany(c=>c.Cards).Concat(document.CardLibrary).Where(c=>c.Action is "click" or "right" or "drag"))
        {
            var link=document.Pages.FirstOrDefault(p=>p.Id==card.PageId)?.Transitions.FirstOrDefault(t=>t.Id==card.TransitionId);var anchor=document.FindAnchor(link?.AnchorId??card.AnchorId);
            if(targets.Contains(card.Target)||link is not null&&targets.Contains(link.Target)||anchor?.States.Any(s=>templates.Contains(s.TemplateKey)||s.Sha256.Length>0&&protectedImages.Contains(s.Sha256))==true)throw new InvalidDataException(card.Name+"：该目标须使用原任务动作核验并记录消费意图");
        }
    }
    public static readonly string[] Actions=["click","right","scroll","zoom_max","drag","key","text","observe","wait","delay","branch","semantic"];
    public static readonly string[] Conditions=["all","any","not","none","page","exists"];
    public static void Validate(WorkbenchDocument document)
    {
        ValidateConsumptionTargets(document);
        if(document.Width<=0||document.Height<=0||document.Dpi<=0)throw new InvalidDataException("客户区尺寸和 DPI 须大于零");
        Unique(document.Chains,"任务链");Unique(document.Anchors,"锚点");Unique(document.Pages,"页面");Unique(document.CardLibrary,"公共卡片");
        var anchors=document.Anchors.Select(a=>a.Id).ToHashSet(StringComparer.Ordinal);var pages=document.Pages.Select(a=>a.Id).ToHashSet(StringComparer.Ordinal);
        foreach(var anchor in document.Anchors)
        {
            Unique(anchor.States,"锚点状态");if(anchor.States.Count==0)throw new InvalidDataException(anchor.Name+"：请至少添加一个状态");
            if(anchor.PageId.Length>0&&!pages.Contains(anchor.PageId))throw new InvalidDataException(anchor.Name+"：关联页面不存在");
            foreach(var state in anchor.States)
            {
                if(state.Mode is not("image" or "coordinate"))throw new InvalidDataException(anchor.Name+"：请选择图片或固定坐标");
                if(state.Shape is not("rectangle" or "ellipse" or "circle"))throw new InvalidDataException(anchor.Name+"：区域形状无效");
                if(state.Data["box"] is not JsonArray box)throw new InvalidDataException(anchor.Name+"：请设置有效区域");
                var size=new JsonArray(document.Width,document.Height);EditorRepository.ValidateBox(box,size);
                if(state.Shape=="circle"&&box[2]!.GetValue<int>()-box[0]!.GetValue<int>()!=box[3]!.GetValue<int>()-box[1]!.GetValue<int>())throw new InvalidDataException(anchor.Name+"：圆形区域的宽高须一致");
                if(state.Mode=="image")
                {
                    if(state.Path.Length==0)throw new InvalidDataException(anchor.Name+"：请框选图片");
                    if(!EditorRepository.Modes.Contains(state.MatchMode))throw new InvalidDataException(anchor.Name+"：图片识别方式无效");
                    if(!double.TryParse(state.Threshold,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var threshold)||!double.IsFinite(threshold)||threshold<=0||threshold>1)throw new InvalidDataException(anchor.Name+"：阈值须大于 0 且不大于 1");
                    if(state.Data["search"] is not JsonArray search)throw new InvalidDataException(anchor.Name+"：请设置搜索范围");EditorRepository.ValidateBox(search,size);
                    if(search[2]!.GetValue<int>()-search[0]!.GetValue<int>()<box[2]!.GetValue<int>()-box[0]!.GetValue<int>()||search[3]!.GetValue<int>()-search[1]!.GetValue<int>()<box[3]!.GetValue<int>()-box[1]!.GetValue<int>())throw new InvalidDataException(anchor.Name+"：搜索范围不能小于图片尺寸");
                }
            }
        }
        foreach(var page in document.Pages)
        {
            Unique(page.Transitions,"页面跳转");
            if(page.MatchRule is not("all" or "any"))throw new InvalidDataException(page.Name+"：页面识别规则无效");
            if(page.Data["source"]?["anchors"] is JsonArray {Count:>0}&&!WorkbenchModel.SplitIds(page.AnchorIds).Select(document.FindAnchor).Any(a=>a?.States.Any(s=>s.Mode=="image")==true))throw new InvalidDataException(page.Name+"：已有页面须保留至少一个图片身份锚点");
            foreach(var id in WorkbenchModel.SplitIds(page.AnchorIds))if(!anchors.Contains(id))throw new InvalidDataException(page.Name+"：引用的锚点不存在（"+id+"）");
            foreach(var link in page.Transitions){if(link.Target.StartsWith("workbench.transition.",StringComparison.Ordinal)&&(link.AnchorId.Length==0||link.DestinationPageId.Length==0))throw new InvalidDataException(page.Name+"：请完善新跳转的点击锚点和到达页面");if(link.Action is not("click" or "right"))throw new InvalidDataException(page.Name+"：页面跳转仅支持左键或右键点击");if(link.DestinationPageId.Length>0&&!pages.Contains(link.DestinationPageId))throw new InvalidDataException(page.Name+"：跳转页面不存在");if(link.AnchorId.Length>0&&!anchors.Contains(link.AnchorId))throw new InvalidDataException(page.Name+"：跳转锚点不存在（"+link.AnchorId+"）");}
        }
        foreach(var chain in document.Chains)
        {
            Unique(chain.Cards,"任务步骤");var ids=chain.Cards.Select(c=>c.Id).ToHashSet(StringComparer.Ordinal);
            foreach(var card in chain.Cards)
            {
                if(card.TransitionId.Length>0)
                {if(card.Action is not("click" or "right")||card.ConsumesResources)throw new InvalidDataException(card.Name+"：页面跳转仅关联普通点击步骤");var link=document.Pages.FirstOrDefault(p=>p.Id==card.PageId)?.Transitions.FirstOrDefault(t=>t.Id==card.TransitionId);if(link is null)throw new InvalidDataException(card.Name+"：所选页面跳转不存在");if(link.AnchorId.Length==0||link.Target.Length==0||link.DestinationPageId.Length==0)throw new InvalidDataException(card.Name+"：请完善页面跳转的点击锚点和到达页面");}
                if(!Actions.Contains(card.Action))throw new InvalidDataException(card.Name+"：步骤类型无效");
                if(card.Action=="key"&&(card.KeyCode is not(27 or 32 or 65 or 68 or 73 or 75 or 87 or 83 or 88)||card.KeyDurationMilliseconds is <1 or >2000))throw new InvalidDataException(card.Name+"：按键仅支持ESC、空格、WASD、I、K、X，按住1–2000毫秒");
                if(card.Action=="text"&&(card.InputText.Length>128||card.InputText.Any(char.IsControl)||card.Backspaces is <0 or >128))throw new InvalidDataException(card.Name+"：文字及退格最多128字，不包含控制字符");
                if(card.IsSemantic&&!(chain.Kind=="production"&&card.Role is "production" or "result"||chain.Kind=="gathering"&&card.Role is "gathering_start" or "observation"))throw new InvalidDataException(card.Name+"：请选择当前任务支持的任务动作；生产使用加工或结果确认，采集使用开始或完成观察");
                if(!Conditions.Contains(card.ConditionOperation))throw new InvalidDataException(card.Name+"：条件关系无效");
                if(card.TimeoutSeconds<1||card.TimeoutSeconds>(card.Action=="semantic"?86400:600)||card.RetryLimit is <0 or >10||card.DelayMilliseconds is <0 or >600000)throw new InvalidDataException(card.Name+"：导航超时须为 1–600 秒，任务动作最长一天，重试为 0–10 次");
                foreach(var id in new[]{card.SuccessId,card.FailureId}.Where(x=>x.Length>0))if(!ids.Contains(id))throw new InvalidDataException(card.Name+"：分支指向已删除步骤");
                foreach(var id in WorkbenchModel.SplitIds(card.ConditionAnchorIds).Concat(WorkbenchModel.SplitIds(card.ExpectedAnchorIds)).Append(card.AnchorId).Where(x=>x.Length>0))if(!anchors.Contains(id))throw new InvalidDataException(card.Name+"：通用锚点不存在（"+id+"）");
                foreach(var id in WorkbenchModel.SplitIds(card.ConditionAnchorIds).Concat(WorkbenchModel.SplitIds(card.ExpectedAnchorIds)))if(!document.FindAnchor(id)!.States.Any(s=>s.Mode=="image"))throw new InvalidDataException(card.Name+"：识别判断须引用图片锚点，固定坐标不作为识别证据");
                if(card.StateId.Length>0&&card.AnchorId.Length>0&&!document.FindAnchor(card.AnchorId)!.States.Any(s=>s.Id==card.StateId))throw new InvalidDataException(card.Name+"：所选锚点状态不存在");
                if(card.PageId.Length>0&&!pages.Contains(card.PageId)&&chain.Data["source_path"] is null)throw new InvalidDataException(card.Name+"：目标页面不存在（"+card.PageId+"）");
                if(card.Action is "click" or "right"&&card.Target.Length==0&&card.AnchorId.Length==0&&card.TransitionId.Length==0)throw new InvalidDataException(card.Name+"：请选择点击锚点或目标");
                if(card.Action=="branch"&&card.ConditionAnchorIds.Length==0&&card.PageId.Length==0)throw new InvalidDataException(card.Name+"：请设置分支条件");
                if(card.ConsumesResources&&card.Action!="semantic")throw new InvalidDataException(card.Name+"：消费步骤须使用原任务动作");
                if(card.Action=="scroll"&&(card.ScrollSteps==0||Math.Abs((long)card.ScrollSteps)>12))throw new InvalidDataException(card.Name+"：滚动格数须为 -12 至 12 且不为零");
                if(card.Action=="drag")
                {if(card.Data["source"]?["destination"] is not JsonArray destination||destination.Count!=2||destination[0]!.GetValue<int>()<0||destination[1]!.GetValue<int>()<0||destination[0]!.GetValue<int>()>=document.Width||destination[1]!.GetValue<int>()>=document.Height)throw new InvalidDataException(card.Name+"：请在目标窗口选取有效拖拽终点");}
            }
            if(chain.Enabled&&chain.Kind=="production"&&chain.Data["source"] is JsonObject original)
                foreach(var role in new[]{"production","result"})if(!chain.Cards.Any(c=>c.Enabled&&c.Role==role&&c.Action=="semantic"&&c.Data["source"] is not null)&&original["nodes"]!.AsArray().Any(n=>EditorRepository.Text(n!,"role")==role))throw new InvalidDataException(chain.Name+"：必须保留配方与结果确认步骤");
        }
        foreach(var strategy in document.Strategies)
        {
            var current=strategy.Data["value"];var original=strategy.Data["original"];if(current is null||original is null||!SameType(current,original))throw new InvalidDataException(strategy.Name+"：参数类型不匹配");if(current.GetValueKind()==System.Text.Json.JsonValueKind.Number&&!double.IsFinite(current.GetFiniteDouble()))throw new InvalidDataException(strategy.Name+"：数值无效");
            if(strategy.ValueKind=="region")EditorRepository.ValidateBox(current,new JsonArray(document.Width,document.Height));
            if(strategy.ValueKind=="point"){if(current is not JsonArray point||point.Count!=2||point[0]!.GetValue<int>()<0||point[1]!.GetValue<int>()<0||point[0]!.GetValue<int>()>=document.Width||point[1]!.GetValue<int>()>=document.Height)throw new InvalidDataException(strategy.Name+"：请选择客户区内坐标");}
            if(strategy.IsRelative)
            {
                var count=strategy.ValueKind=="relativepoint"?2:4;
                if(current is not JsonArray relative||relative.Count!=count||relative.Any(n=>n is not JsonValue v||!v.TryGetValue<int>(out _)))throw new InvalidDataException(strategy.Name+"：请设置有效的相对坐标");
                if(count==4&&((long)relative[2]!.GetValue<int>()-relative[0]!.GetValue<int>()<=0||(long)relative[3]!.GetValue<int>()-relative[1]!.GetValue<int>()<=0))throw new InvalidDataException(strategy.Name+"：相对区域的宽高须大于零");
            }
        }
    }
    public static bool SameType(JsonNode first,JsonNode second)
    {var a=first.GetValueKind();var b=second.GetValueKind();return a==b||a is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False&&b is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False;}
    static void Unique<T>(IEnumerable<T> rows,string name) where T:WorkbenchModel
    {var all=rows.ToArray();if(all.Any(x=>string.IsNullOrWhiteSpace(x.Id)||string.IsNullOrWhiteSpace(x.Name)))throw new InvalidDataException(name+"名称和标识不能为空");if(all.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=all.Length)throw new InvalidDataException(name+"标识重复");}
}
