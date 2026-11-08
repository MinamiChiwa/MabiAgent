using System.Globalization;
using System.Text.Json.Nodes;
using MabiAgent.Core;
using MabiAgent.Workflows;
namespace MabiAgent.Windows;

// One bounded branch conversion. Actual balances and output come only from packets.
public sealed class ScreenDecompositionPort:ScreenWorkflowPort,IDecompositionPort
{
    private readonly Func<InventorySnapshot> inventory;private readonly JsonNode cfg;private JsonObject state;
    private readonly ScreenMaintenancePort exit;private double editedAt;private int edits;private readonly ScreenBagItemSearch search;
    private static double Now=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d;
    private long InputId=>cfg["input_wire_id"]!.GetValue<long>();
    private long OutputId=>cfg["output_item_id"]!.GetValue<long>();
    public ScreenDecompositionPort(ScreenWorkflowSession session,string id,IWorkflowStore store,Func<InventorySnapshot> stock):base(session,id,store)
    {search=new(session);inventory=stock;cfg=session.Routes.Configuration["decomposition"]??throw new InvalidDataException("缺少分解配置");state=store.Read()??new JsonObject{["status"]="idle"};if(state["search_dirty"]?.GetValue<bool>()==true)search.RestoreFilter();if(state["pending"] is not null)state["status"]="unknown";else if(S(state,"status")=="running")state["status"]="blocked";exit=session.CreateMaintenancePort(id+":exit",new NavigationStore());}
    private sealed class NavigationStore:IWorkflowStore{private JsonObject? value;public JsonObject? Read()=>(JsonObject?)value?.DeepClone();public void Flush(JsonObject next)=>value=(JsonObject)next.DeepClone();}
    public JsonObject Snapshot()=>(JsonObject)state.DeepClone();
    private void Save()=>Store.Flush(state);
    public bool Request(long inputQuantity)
    {
        if(inputQuantity<=0||state["pending"] is not null||S(state,"status")=="running")return false;
        var snapshot=inventory();var input=snapshot.Query(InputId);var output=snapshot.Query(OutputId);
        if(!input.Confirmed||!output.Confirmed||input.Source!="packet_capture"||output.Source!="packet_capture"||input.Scope!=snapshot.Scope||output.Scope!=input.Scope||input.Quantity is null||input.Quantity<inputQuantity||output.Quantity is null)return false;
        state=new JsonObject{["status"]="running",["phase"]="select",["amount"]=inputQuantity,["input_before"]=input.Quantity,["output_before"]=output.Quantity,["input_version"]=input.Version,["output_version"]=output.Version,["scope"]=input.Scope,["deadline"]=Now+180};
        edits=0;search.Reset();Save();return true;
    }
    public void ObserveOnly()
    {
        if(state["pending"] is null)return;var input=inventory().Query(InputId);var output=inventory().Query(OutputId);
        if(!input.Confirmed||!output.Confirmed||input.Quantity is null||output.Quantity is null||input.Source!="packet_capture"||output.Source!="packet_capture"||input.Scope!=S(state,"scope")||output.Scope!=S(state,"scope"))return;
        if(input.Version==S(state,"input_version")||output.Version==S(state,"output_version")||input.Quantity!=state["input_before"]!.GetValue<long>()-state["amount"]!.GetValue<long>()||output.Quantity<=state["output_before"]!.GetValue<long>())return;
        state["output_received"]=output.Quantity-state["output_before"]!.GetValue<long>();state["input_after"]=input.Quantity;state["output_after"]=output.Quantity;state["pending"]=null;state["status"]="complete";state["phase"]="leave";Save();
    }
    private string Block(string reason){state["status"]=state["pending"] is null?"blocked":"unknown";if(state["pending"] is null)state["phase"]="leave";state["reason"]=reason;Save();return S(state,"status")!;}
    public bool ReleaseNavigation(){var observation=Read();if(!search.Clear(observation))return false;if(!search.Used&&state["search_dirty"]?.GetValue<bool>()==true){state["search_dirty"]=false;Save();}if(!exit.ReleaseNavigation())return false;if(S(state,"phase")=="leave"){state["phase"]="idle";Save();}return true;}
    public string Tick()
    {
        try{return TickCore();}
        catch(InputBlockedException error)when(!Session.Guard.Paused)
        {if(error.TargetChangedBeforeDispatch)return S(state,"status")??"running";return Block(error.Message);}
    }
    private string TickCore()
    {
        ObserveOnly();if(S(state,"status")!="running")return S(state,"status")!;
        if(Now>state["deadline"]!.GetFiniteDouble())return Block("decomposition_timeout");
        var o=Read();if(!o.Stable||!Session.InputAvailable)return "running";var page=o.View.Page;var phase=S(state,"phase");
        if(state["pending"] is not null){if(page=="decomposition_result")Session.NavigateTarget("decomposition.result_close");return "running";}
        if(phase=="select")
        {
            state["search_dirty"]=true;Save();var result=search.Tick(o,cfg["input_name"]?.GetValue<string>()??"樹枝");
            if(result.Item is{} item){state["phase"]="detail";Save();Session.Guard.ClickAt(item);}
            else if(result.Status=="missing")return Block("branch_not_visible");
        }
        else if(phase=="detail"&&page=="bag_item_detail")
        {if(!Session.BagItemNameMatches("樹枝"))return Block("branch_identity_unknown");Session.NavigateTarget("bag.decompose_open");state["phase"]="quantity";Save();}
        else if(phase=="quantity"&&page=="bag_decompose")
        {Edit(o);state["phase"]="verify";Save();}
        else if(phase=="verify"&&page=="bag_decompose")
        {
            if(Now-editedAt<1)return "running";var amount=state["amount"]!.GetValue<long>();
            if(Session.ReadQuantity(cfg["quantity"])?.Value?.Quantity!=amount){if(Now-editedAt<2)return "running";if(edits<4){Edit(o);return "running";}return Block("decomposition_quantity_unverified");}
            Session.NavigateTarget("bag.decompose_confirm");state["phase"]="preview";Save();
        }
        else if(phase=="preview"&&page=="decomposition_preview")
        {
            var amount=state["amount"]!.GetValue<long>();var input=inventory().Query(InputId);var output=inventory().Query(OutputId);
            if(!input.Confirmed||!output.Confirmed||input.Source!="packet_capture"||output.Source!="packet_capture"||input.Scope!=S(state,"scope")||output.Scope!=input.Scope||input.Quantity is null||input.Quantity<amount||output.Quantity is null)return Block("packet_stock_unknown");
            state["input_before"]=input.Quantity;state["output_before"]=output.Quantity;state["input_version"]=input.Version;state["output_version"]=output.Version;state["pending"]=new JsonObject{["kind"]="decompose",["amount"]=amount,["at"]=Now};state["phase"]="result";Save();
            try{Consume("decomposition.confirm","decompose",f=>f.View.Page=="decomposition_preview");}
            catch(InputRejectedBeforeDispatchException){state["pending"]=null;state["phase"]="preview";Save();}
            catch{Block("decomposition_result_unknown");throw;}
        }
        return "running";
    }
    private void Edit(UiObservation o){Session.Guard.EditQuantity(Session.Guard.ObserveCurrentTarget("bag.decompose_quantity",o),state["amount"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture));editedAt=Now;edits++;}
}
