using System.Text.Json.Nodes;
using MabiAgent.Desktop.Editors;
using Xunit;
public sealed class EditorRepositoryTests
{
    [Fact] public void MaterialRenameClearsStaleImagesAndQuantityKeepsThem()
    {
        var chain=JsonNode.Parse("""{"kind":"production","parameters":{"inputs":[{"item_id":1,"name":"原木","quantity":3}]},"nodes":[{"id":"recipe","anchors":[{"key":"recipe_material_1","label":"原木","variants":[{"asset":"old"}]},{"key":"craft","variants":[]}]}]}""")!.AsObject();
        var previous=(JsonArray)chain["parameters"]!["inputs"]!.DeepClone();
        chain["parameters"]!["inputs"]![0]!["quantity"]=5;
        EditorRepository.SynchronizeMaterials(chain,previous);
        Assert.Single(chain["nodes"]![0]!["anchors"]![0]!["variants"]!.AsArray());
        chain["parameters"]!["inputs"]![0]!["name"]="樹液";
        EditorRepository.SynchronizeMaterials(chain,previous);
        Assert.Empty(chain["nodes"]![0]!["anchors"]![0]!["variants"]!.AsArray());
        Assert.Equal("craft",chain["nodes"]![0]!["anchors"]![1]!["key"]!.GetValue<string>());
    }
    [Fact] public void NameIdentityMatchesPythonSha256Contract()
    {
        Assert.Equal(198875762,EditorRepository.ResolveName("铁矿石",new JsonObject{["198875762"]="铁矿石"}));
        Assert.Equal(EditorRepository.ResolveName(" test ",new JsonObject()),EditorRepository.ResolveName("ＴＥＳＴ",new JsonObject()));
        Assert.InRange(EditorRepository.ResolveName("新材料",new JsonObject()),1L<<52,(1L<<53)-1);
    }
    [Fact] public void EditingSharedAnchorUpdatesOtherNodesAndRejectsConflicts()
    {
        var original=JsonNode.Parse("""{"nodes":[{"id":"a","anchors":[{"key":"same","variants":[{"threshold":0.97}]}]},{"id":"b","anchors":[{"key":"same","variants":[{"threshold":0.97}]}]}]}""")!.AsObject();
        var value=(JsonObject)original.DeepClone();value["nodes"]![0]!["anchors"]![0]!["variants"]![0]!["threshold"]=.9;
        EditorRepository.SynchronizeSharedAnchors(value,original);
        Assert.Equal(.9,value["nodes"]![1]!["anchors"]![0]!["variants"]![0]!["threshold"]!.GetValue<double>());
        value["nodes"]![1]!["anchors"]![0]!["variants"]![0]!["threshold"]=.8;
        Assert.Throws<InvalidDataException>(()=>EditorRepository.SynchronizeSharedAnchors(value,original));
    }
    [Theory][InlineData("../escape")][InlineData("C:/escape")][InlineData("bad:name")]
    public void UnsafeIdsAreRejected(string id)=>Assert.Throws<InvalidDataException>(()=>EditorRepository.ValidateId(id));
}


