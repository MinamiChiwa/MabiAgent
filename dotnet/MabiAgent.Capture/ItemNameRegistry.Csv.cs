using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MabiAgent.Capture;

public sealed partial class ItemNameRegistry
{
    /// <summary>Reads existing account associations without modifying or migrating the file.</summary>
    public static ItemNameRegistry ReadCsv(TextReader input)
    {
        var rows = CsvRows(input).GetEnumerator(); var registry = new ItemNameRegistry();
        if (!rows.MoveNext()) return registry;
        var headers = rows.Current.Select((s,i) => (Name:s.TrimStart((char)0xfeff), Index:i)).ToDictionary(p => p.Name,p => p.Index,StringComparer.Ordinal);
        if (!headers.ContainsKey("item_id") || !headers.ContainsKey("name")) throw new InvalidDataException("Missing item CSV headers");
        var count = 0; var ids = new HashSet<uint>();
        while (rows.MoveNext())
        {
            if (++count > 100000) throw new InvalidDataException("Association row limit exceeded");
            var row = rows.Current;
            string Field(string name) => headers.TryGetValue(name,out var index) && index < row.Count ? row[index] : "";
            if (row.Count == 1 && row[0].Length == 0) continue;
            if (!uint.TryParse(Field("item_id"),NumberStyles.None,CultureInfo.InvariantCulture,out var item) || item == 0 || !ids.Add(item)) throw new InvalidDataException("Invalid or duplicate item CSV ID");
            var association = Field("association") switch
            {
                "user_confirmed" => NameAssociation.UserConfirmed, "builtin_verified" => NameAssociation.BuiltinVerified,
                "preset_candidate" => NameAssociation.Candidate, "user_cleared" => NameAssociation.UserCleared, _ => NameAssociation.Unknown
            };
            var name = Field("name"); registry.Set(new(item,string.IsNullOrEmpty(name)?null:name,association,Field("source"),Field("captured") == "true"));
        }
        return registry;
    }
    public void LoadVerifiedCatalog(JsonElement catalog)
    {
        if (!catalog.TryGetProperty("items",out var items)) throw new InvalidDataException("Missing verified catalog items");
        foreach (var pair in items.EnumerateObject())
        {
            if (pair.Value.GetProperty("status").GetString() != "verified") continue;
            if (!uint.TryParse(pair.Name,NumberStyles.None,CultureInfo.InvariantCulture,out var item) || item == 0) throw new InvalidDataException("Invalid verified catalog item");
            lock (gate)
            {
                rows.TryGetValue(item,out var existing);
                if (existing?.Association is NameAssociation.UserConfirmed or NameAssociation.BuiltinVerified or NameAssociation.UserCleared) continue;
                var userConfirmed = pair.Value.TryGetProperty("association", out var association) && association.GetString() == "user_confirmed";
                var source = userConfirmed && pair.Value.TryGetProperty("source", out var declaredSource) && !string.IsNullOrWhiteSpace(declaredSource.GetString())
                    ? declaredSource.GetString()! : userConfirmed ? "user_confirmed" : "builtin_verified";
                Set(new(item,pair.Value.GetProperty("name").GetString(),userConfirmed ? NameAssociation.UserConfirmed : NameAssociation.BuiltinVerified,source,existing?.Captured ?? false));
            }
        }
    }
    internal static IEnumerable<IReadOnlyList<string>> CsvRows(TextReader input)
    {
        var row = new List<string>(); var field = new StringBuilder(); var quoted = false; var afterQuote = false; var total = 0;
        int ch;
        while ((ch = input.Read()) >= 0)
        {
            if (++total > 16*1024*1024 || field.Length > 65536 || row.Count > 128) throw new InvalidDataException("CSV budget exceeded");
            var c = (char)ch;
            if (quoted)
            {
                if (c == '"') { if (input.Peek() == '"') { input.Read(); field.Append('"'); } else { quoted = false; afterQuote = true; } }
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0 && !afterQuote) { quoted = true; continue; }
            if (c == ',' || c == 13 || c == 10)
            {
                row.Add(field.ToString()); field.Clear(); afterQuote = false;
                if (c == ',') continue;
                if (c == 13 && input.Peek() == 10) input.Read();
                yield return row; row = []; continue;
            }
            if (afterQuote || c == '"') throw new InvalidDataException("Malformed CSV quoting");
            field.Append(c);
        }
        if (quoted) throw new InvalidDataException("Unclosed CSV field");
        if (field.Length > 0 || row.Count > 0 || afterQuote) { row.Add(field.ToString()); yield return row; }
    }
}

