using System.Text.Json;

namespace MabiAgent.Capture;

// Screen quantities, repeated samples and time-based association promotion were
// retired with the inventory rebuild. Packet identity is recorded directly by
// RecordCaptureAssociation; there is no screen-derived recommendation path.
public sealed partial class AccountItemService
{
    private sealed record Candidate(string Name, string Source);
    private readonly Dictionary<uint, Candidate> candidates = [];
    private long version;
    public long Version => Interlocked.Read(ref version);

    private void LoadCandidates(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("候选名称文件过大");
        using var data = JsonDocument.Parse(File.ReadAllText(path));
        var source = data.RootElement.GetProperty("source").GetString() ?? "第三方候选";
        foreach (var row in data.RootElement.GetProperty("names").EnumerateObject())
            if (uint.TryParse(row.Name, out var id) && id > 0 && row.Value.GetString() is { } name && !ItemNameRegistry.IsPlaceholderName(name))
            { _ = ItemNameRegistry.Normalize(name); candidates[id] = new(name, source); }
    }

    public void SetCurrentCapture(InventorySnapshot snapshot)
    {
        if (snapshot.Scope is null || snapshot.Identity is null) return;
        Interlocked.Increment(ref version);
    }

}
