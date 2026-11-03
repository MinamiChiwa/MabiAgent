using System.Globalization;
using System.Text.Json;
using MabiAgent.Capture;
using Xunit;
namespace MabiAgent.Capture.Tests;
public class PythonParityTests
{
    [Fact] public void NativeDecoderMatchesOriginalPythonSchemaAndStackRecords()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "config/client-inventory-schema.disk.json"))) root = root.Parent;
        Assert.NotNull(root);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/python-parity.json")));
        Assert.True(json.RootElement.GetProperty("synthetic").GetBoolean());
        var login = ClientInventoryDecoder.Load(Path.Combine(root.FullName, "config/client-inventory-schema.disk.json")).Decode(Convert.FromBase64String(json.RootElement.GetProperty("body_base64").GetString()!));
        Assert.Equal(json.RootElement.GetProperty("character_id").GetString(), login.CharacterCode.ToString(CultureInfo.InvariantCulture));
        var expected = json.RootElement.GetProperty("records");
        foreach (var pair in new[] { ("backpack",InventoryContainer.Backpack), ("account_storage",InventoryContainer.AccountStorage), ("character_storage",InventoryContainer.CharacterStorage) })
        {
            var rows = expected.GetProperty(pair.Item1); var actual = login.Containers[pair.Item2];
            Assert.Equal(rows.GetArrayLength(), actual.Count);
            for (var i=0; i<actual.Count; i++)
            {
                Assert.Equal(rows[i].GetProperty("item_id").GetUInt32(), actual[i].ItemId);
                Assert.Equal(rows[i].GetProperty("quantity").GetInt64(), actual[i].Quantity);
                Assert.Equal(rows[i].GetProperty("stackable_id").GetInt64(), actual[i].StackableId);
                Assert.Equal(rows[i].GetProperty("stack_key_hex").GetString(), actual[i].StackKeyHex);
                Assert.Equal(rows[i].GetProperty("descriptor_text").GetString(), actual[i].DescriptorText);
                Assert.Equal(rows[i].GetProperty("byte_length").GetInt32(), actual[i].ByteLength);
            }
        }
    }
}
