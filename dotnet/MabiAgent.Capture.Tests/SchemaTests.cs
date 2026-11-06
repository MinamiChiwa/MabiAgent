using System.Buffers.Binary;
using System.Text.Json;
using MabiAgent.Capture;
using Xunit;
namespace MabiAgent.Capture.Tests;
public class SchemaTests
{
    [Theory] [InlineData(600000L,9136L,609136L)] [InlineData(-1L,10L,null)]
    public void WalletUsesSequentialFieldsAndNeverInventsMissingBalance(long paid,long free,long? expected)
    {
        var schema=System.Text.Json.Nodes.JsonNode.Parse(Schema().GetRawText())!;
        schema["types"]![ClientInventoryDecoder.RootType]!["fields"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("""{"name":"Wallet","type":"Wallet"}"""));
        schema["types"]!["Wallet"]=System.Text.Json.Nodes.JsonNode.Parse("""{"parent":"object","fields":[{"name":"cash","type":"int64"},{"name":"goldPaid","type":"int64"},{"name":"goldFree","type":"int64"}]}""");
        var body=new byte[28+24];BinaryPrimitives.WriteInt64LittleEndian(body,9007199254740993);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(36),paid);BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(44),free);
        var decoder=new ClientInventoryDecoder(JsonSerializer.SerializeToElement(schema));var result=decoder.Decode(body);
        Assert.Equal(expected,result.Gold);Assert.Equal(3,result.Containers.Count);Assert.Null(decoder.Decode(body[..30]).Gold);
    }
    private static JsonElement Schema() => JsonSerializer.SerializeToElement(new { types = new Dictionary<string,object>
    {
        [ClientInventoryDecoder.RootType] = new { parent = "Identity", fields = new[] { new { name = "Prior", type = "string" }, new { name = "ConsumableInventory", type = "Consumables" } } },
        ["Identity"] = new { parent = "object", fields = new[] { new { name = "CharacterId", type = "int64" } } },
        ["Consumables"] = new { parent = "object", fields = new[] { new { name = "SelectedDefaultFoodConsumableSheetId", type = "uint32" },
            new { name = "ConsumableStacks", type = "System.Collections.Generic.Dictionary`2<Key,Value>" },
            new { name = "ConsumableStacksInAccountStorage", type = "System.Collections.Generic.Dictionary`2<Key,Value>" },
            new { name = "ConsumableStacksInCharacterStorage", type = "System.Collections.Generic.Dictionary`2<Key,Value>" } } }
    } });
    [Fact] public void SequentialSchemaFindsThreeExactContainersIncludingEmpty()
    {
        var body = new byte[8 + 4 + 4 + 4 + 224 + 4 + 4];
        BinaryPrimitives.WriteInt64LittleEndian(body, 9007199254740993);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), 1); ProtocolTests.Record().CopyTo(body, 20);
        var decoder = new ClientInventoryDecoder(Schema());
        var login = decoder.Decode(body);
        Assert.Equal(9007199254740993, login.CharacterCode);
        Assert.Single(login.Containers[InventoryContainer.Backpack]);
        Assert.Empty(login.Containers[InventoryContainer.AccountStorage]);
        Assert.Throws<InvalidDataException>(() => decoder.Decode(body[..^1]));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), 5000);
        Assert.Throws<InvalidDataException>(() => decoder.Decode(body));
    }
    [Fact] public void RepositorySchemaCanBeLoadedWithoutTouchingGame()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "dotnet/MabiAgent.Capture.Tests/Fixtures/client-inventory-schema.historical.json"))) root = root.Parent;
        Assert.NotNull(root);
        var decoder = ClientInventoryDecoder.Load(Path.Combine(root.FullName, "dotnet/MabiAgent.Capture.Tests/Fixtures/client-inventory-schema.historical.json"));
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new byte[8]));
    }
}
