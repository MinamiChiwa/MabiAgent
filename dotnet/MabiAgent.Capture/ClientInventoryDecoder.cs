using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MabiAgent.Capture;

/// <summary>Sequential parser using the existing disk schema. It never scans for item IDs or infers inventory from a partial vector.</summary>
public sealed class ClientInventoryDecoder
{
    public const string RootType = "Shared.Protocols.Replications.GroupReplicatedDatas.CharacterPersonalReplicatedData";
    private readonly JsonElement types;
    public ClientInventoryDecoder(JsonElement schema)
    {
        if (!schema.TryGetProperty("types", out var source) || source.ValueKind != JsonValueKind.Object || source.EnumerateObject().Count() > 4096)
            throw new InvalidDataException("Invalid client schema type budget");
        types = source.Clone();
    }
    public static ClientInventoryDecoder Load(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 16 * 1024 * 1024) throw new InvalidDataException("Schema byte limit exceeded");
        using var json = JsonDocument.Parse(input); return new(json.RootElement);
    }
    public LoginInventory Decode(byte[] body)
    {
        if (body.Length < 8 || body.Length > GameProtocol.MaxBody) throw new InvalidDataException("Invalid login body size");
        try
        {
            var reader = new Reader(body, types);
            var root = types.GetProperty(RootType); reader.Value(root.GetProperty("parent").GetString());
            foreach (var field in root.GetProperty("fields").EnumerateArray())
            {
                if (field.GetProperty("name").GetString() != "ConsumableInventory") { reader.Value(field.GetProperty("type").GetString()); continue; }
                var type = types.GetProperty(field.GetProperty("type").GetString()!);
                reader.Value(type.GetProperty("parent").GetString());
                var containers = new Dictionary<InventoryContainer, IReadOnlyList<ConsumableStack>>();
                foreach (var member in type.GetProperty("fields").EnumerateArray())
                {
                    var name = member.GetProperty("name").GetString();
                    var container = name switch
                    {
                        "ConsumableStacks" => InventoryContainer.Backpack,
                        "ConsumableStacksInAccountStorage" => InventoryContainer.AccountStorage,
                        "ConsumableStacksInCharacterStorage" => InventoryContainer.CharacterStorage,
                        _ => (InventoryContainer?)null
                    };
                    if (!container.HasValue) { reader.Value(member.GetProperty("type").GetString()); continue; }
                    var wireType = member.GetProperty("type").GetString()!;
                    if (!wireType.Contains("Dictionary`2<", StringComparison.Ordinal)) throw new InvalidDataException("Consumable field is not dictionary");
                    var count = reader.Count(); var records = new List<ConsumableStack>(count); var seen = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < count; i++)
                    {
                        var row = ConsumableCodec.ParseRecord(body, reader.Offset);
                        if (!seen.Add(row.StackKeyHex)) throw new InvalidDataException("Duplicate container stack key");
                        records.Add(row); reader.Read(row.ByteLength);
                    }
                    containers.Add(container.Value, records.AsReadOnly());
                }
                if (containers.Count != 3 || containers.Values.Sum(x => x.Count) > 10000) throw new InvalidDataException("Incomplete consumable containers");
                var character = BinaryPrimitives.ReadInt64LittleEndian(body);
                if (character <= 0) throw new InvalidDataException("Unknown character code");
                long? gold=null;
                // Wallet is later in this same sequential login schema. Its optional
                // failure must not discard a valid consumable inventory.
                try
                {
                    var afterInventory=false;
                    foreach(var later in root.GetProperty("fields").EnumerateArray())
                    {
                        var name=later.GetProperty("name").GetString();
                        if(!afterInventory){afterInventory=name=="ConsumableInventory";continue;}
                        if(name!="Wallet"){reader.Value(later.GetProperty("type").GetString());continue;}
                        var wallet=types.GetProperty(later.GetProperty("type").GetString()!);reader.Value(wallet.GetProperty("parent").GetString());
                        long? paid=null,free=null;
                        foreach(var part in wallet.GetProperty("fields").EnumerateArray())
                        {
                            var key=part.GetProperty("name").GetString();
                            if(key is "goldPaid" or "goldFree")
                            {if(part.GetProperty("type").GetString()!="int64")throw new InvalidDataException("Unsupported wallet gold type");var amount=BinaryPrimitives.ReadInt64LittleEndian(reader.Read(8));if(amount<0)throw new InvalidDataException("Negative wallet gold");if(key=="goldPaid")paid=amount;else free=amount;}
                            else reader.Value(part.GetProperty("type").GetString());
                            if(paid is not null&&free is not null){gold=checked(paid.Value+free.Value);break;}
                        }
                        break;
                    }
                }
                catch(Exception error)when(error is InvalidDataException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException){gold=null;}
                return new(character, Convert.ToHexStringLower(SHA256.HashData(body)), containers,gold);
            }
            throw new InvalidDataException("Consumable inventory field missing");
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
        { throw new InvalidDataException("Unsupported or malformed login schema/body", ex); }
    }
    private sealed class Reader(byte[] data, JsonElement schema)
    {
        public int Offset { get; private set; }
        private int elements;
        public ReadOnlySpan<byte> Read(int size)
        {
            if (size < 0 || size > data.Length - Offset) throw new InvalidDataException($"Truncated schema value at {Offset}");
            var value = data.AsSpan(Offset, size); Offset += size; return value;
        }
        public int Count()
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(Read(4));
            if (count < 0 || count > 4096 || elements + count > 65536) throw new InvalidDataException("Schema collection budget exceeded");
            elements += count; return count;
        }
        public void Value(string? name, int depth = 0)
        {
            if (depth > 48) throw new InvalidDataException("Schema recursion budget exceeded");
            var size = name switch { "int8" or "uint8" => 1, "int16" or "uint16" or "char" => 2, "int32" or "uint32" or "float32" => 4, "int64" or "uint64" or "float64" => 8, "System.Decimal" => 16, _ => 0 };
            if (size > 0) { Read(size); return; }
            if (name == "bool") { if (Read(1)[0] > 1) throw new InvalidDataException("Invalid schema boolean"); return; }
            if (name == "string")
            {
                var count = BinaryPrimitives.ReadInt32LittleEndian(Read(4));
                if (count < 0 || count > 65536 || count % 2 != 0) throw new InvalidDataException("Invalid schema UTF16 length");
                try { _ = ConsumableCodec.StrictUtf16.GetCharCount(Read(count)); }
                catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid schema UTF16", error); }
                return;
            }
            if (name is null or "object" or "System.Object" or "System.ValueType" or "System.Enum") return;
            var arguments = GenericArguments(name); var prefix = name.Split('<')[0];
            var dictionary = (prefix.EndsWith("Dictionary`2", StringComparison.Ordinal) || prefix.EndsWith("SortedDictionary`2", StringComparison.Ordinal)) && arguments.Count == 2;
            var sequence = arguments.Count == 1 && (prefix.EndsWith("List`1", StringComparison.Ordinal) || prefix.EndsWith("MMList`1", StringComparison.Ordinal) || prefix.EndsWith("HashSet`1", StringComparison.Ordinal));
            if (name.EndsWith("[]", StringComparison.Ordinal)) { arguments = [name[..^2]]; sequence = true; }
            if (dictionary || sequence)
            {
                var count = Count(); var keys = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    var start = Offset; Value(arguments[0], depth + 1);
                    if (dictionary)
                    {
                        // Wire keys are the identity contract used by the inventory codec.
                        var key = Convert.ToHexStringLower(data.AsSpan(start, Offset - start));
                        if (!keys.Add(key)) throw new InvalidDataException("Duplicate schema dictionary key");
                        Value(arguments[1], depth + 1);
                    }
                }
                return;
            }
            if (!schema.TryGetProperty(name, out var record)) throw new InvalidDataException("Unsupported schema type: " + name);
            var fields = record.GetProperty("fields");
            if (fields.GetArrayLength() > 512) throw new InvalidDataException("Schema field budget exceeded");
            var parent = record.GetProperty("parent").GetString();
            if (parent == "System.Enum")
            { Value(fields.EnumerateArray().Single(f => f.GetProperty("name").GetString() == "value__").GetProperty("type").GetString(), depth + 1); return; }
            Value(parent, depth + 1);
            foreach (var field in fields.EnumerateArray()) Value(field.GetProperty("type").GetString(), depth + 1);
        }
        private static List<string> GenericArguments(string name)
        {
            var first = name.IndexOf('<'); if (first < 0 || !name.EndsWith('>')) return [];
            var text = name[(first + 1)..^1]; var result = new List<string>(); var depth = 0; var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '<') depth++; else if (text[i] == '>') depth--;
                else if (text[i] == ',' && depth == 0) { result.Add(text[start..i]); start = i + 1; }
            }
            result.Add(text[start..]); return result;
        }
    }
}
