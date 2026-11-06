using System.Text.Json;
using System.Text.Json.Nodes;

namespace MabiAgent.Update;

/// <summary>
/// User preferences for the optional update channel. A missing file or a missing/invalid
/// <c>check_on_start</c> value enables startup checks; an explicit <c>false</c> stays disabled.
/// </summary>
public sealed record UpdatePreferences(bool CheckOnStart, bool HasMirror, string? Mirror)
{
    public const bool DefaultCheckOnStart = true;

    public static UpdatePreferences Load(string path)
    {
        if (!File.Exists(path)) return new(DefaultCheckOnStart, false, null);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject node)
                return new(DefaultCheckOnStart, false, null);

            var checkOnStart = node["check_on_start"] is JsonValue check
                && check.TryGetValue<bool>(out var configured)
                ? configured
                : DefaultCheckOnStart;
            var hasMirror = node.TryGetPropertyValue("mirror", out var mirrorNode);
            var mirror = hasMirror && mirrorNode is JsonValue mirrorValue
                && mirrorValue.TryGetValue<string>(out var configuredMirror)
                ? configuredMirror
                : hasMirror ? "" : null;
            return new(checkOnStart, hasMirror, mirror);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(DefaultCheckOnStart, false, null);
        }
    }

    /// <summary>Diagnostics and acceptance runs must never start an update network request.</summary>
    public bool ShouldCheckOnStartup(IEnumerable<string> arguments) =>
        CheckOnStart && !UpdateEntry.IsAutomatedRun(arguments.ToArray());

    /// <summary>Rewrites only <c>check_on_start</c>; the mirror and unknown keys survive.</summary>
    public static void SaveCheckOnStart(string path, bool checkOnStart)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("更新偏好缺少目录");
        Directory.CreateDirectory(directory);
        JsonObject node;
        if (File.Exists(path))
            node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("更新偏好不是 JSON 对象");
        else
            node = new JsonObject();
        node["check_on_start"] = checkOnStart;
        File.WriteAllText(path, node.ToJsonString());
    }
}
