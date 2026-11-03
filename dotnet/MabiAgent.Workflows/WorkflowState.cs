using MabiAgent.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace MabiAgent.Workflows;

/// <summary>Flush must durably finish before returning. No consuming input is allowed without it.</summary>
public interface IWorkflowStore
{
    JsonObject? Read();
    void Flush(JsonObject state);
}
public sealed class JsonWorkflowStore(string path) : IWorkflowStore
{
    public JsonObject? Read() => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject
        ?? throw new InvalidDataException("流程记录损坏，保留待核对") : null;
    public void Flush(JsonObject state)
    {
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state); stream.Flush(true);
            }
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
/// <summary>Only a preflight check that proves no native input was attempted may raise this.</summary>
public sealed class InputRejectedBeforeDispatchException(string reason) : InputNotDispatchedException(reason);
public interface IForegroundWorkflowPort
{
    void Pause(string reason);
    void Resume();
    void PauseLocal(string reason)=>Pause(reason);
    // Opt-in proof from a fresh screen: no pending navigation or native character activity.
    bool CanReleaseForeground()=>false;
}
public abstract class PersistentWorkflow
{
    protected readonly object Gate = new();
    protected readonly IWorkflowStore Store;
    protected readonly TimeProvider Time;
    protected JsonObject State;
    protected double Now => (Time.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds;
    protected PersistentWorkflow(IWorkflowStore store, TimeProvider? time, JsonObject initial)
    { Store = store; Time = time ?? TimeProvider.System; State = initial; }
    public JsonObject Snapshot() { lock (Gate) return (JsonObject)State.DeepClone(); }
    protected void Save() => Store.Flush(Snapshot());
    protected void RememberFault(Exception error,string operation)
    {
        State["last_fault"]=new JsonObject{["id"]=Guid.NewGuid().ToString("N"),["at"]=Time.GetUtcNow().ToString("O"),
            ["module"]=GetType().Name,["operation"]=operation,["phase"]=S("phase"),["pending"]=Pending is not null,["exception"]=error.ToString()};
    }
    // In-memory adapters may produce CLR int/long nodes for JSON numbers. Normalize once
    // on load so legacy integer timestamps have the same behavior as file-loaded JSON.
    protected JsonObject? Load() => Store.Read() is { } value ? JsonNode.Parse(value.ToJsonString())!.AsObject() : null;
    protected string? S(string key) => State[key]?.GetValue<string>();
    protected int I(string key) => State[key]?.GetValue<int>() ?? 0;
    protected double D(string key) => State[key]?.GetFiniteDouble() ?? 0;
    protected bool B(string key) => State[key]?.GetValue<bool>() ?? false;
    protected JsonObject? Pending => State["pending"] as JsonObject;
    protected void Set(params (string Key, object? Value)[] changes)
    {
        foreach (var (key, value) in changes) State[key] = value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value);
    }
    protected static bool Ready(string page, bool foreground, bool stable) => foreground && stable && !string.IsNullOrWhiteSpace(page) && page is not ("unknown" or "restart_required");
    private double? pageWaitAt;
    protected bool PageReady(string page, bool foreground, bool stable, Action<string> pause, bool pauseOnUnrecognized = true)
    {
        if (Ready(page, foreground, stable)) { pageWaitAt = null; return true; }
        pageWaitAt ??= Now;
        if (pauseOnUnrecognized && S("status") != "paused" && Now - pageWaitAt >= 45) pause("unrecognized_page");
        return false;
    }
    protected void ResetPageWait() => pageWaitAt = null;
    private string? progressKey;private double progressAt;
    protected void ResetProgressWait(){progressKey=null;}
    protected void CheckProgress(string key,double seconds=120)
    {
        if(key!=progressKey){progressKey=key;progressAt=Now;}
        else if(Now-progressAt>=seconds)throw new TimeoutException(GetType().Name+"阶段无进展："+key);
    }
    protected static HashSet<int> Slots(JsonNode? node) => node is JsonArray array ? array.Select(x => x!.GetValue<int>()).ToHashSet() : [];
    protected static JsonArray Array(IEnumerable<int> values) => new(values.Order().Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    protected static void ValidatePositive(double value, string name)
    { if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name); }
}
