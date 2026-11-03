using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Tests;
public sealed class ConfigurationTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"mabi-config-"+Guid.NewGuid().ToString("N"));
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    [Fact]public void MergePreservesNestedFieldsAndReplacesLists()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"base.json"),"""{"ui":{"a":1,"b":[1,2]},"schema_version":1}""");
        File.WriteAllText(Path.Combine(root,"child.json"),"""{"extends":"base.json","ui":{"b":[3]}}""");
        var actual=new CatalogLoader(root).Load("child.json");
        Assert.True(JsonNode.DeepEquals(actual,JsonNode.Parse("""{"ui":{"a":1,"b":[3]},"schema_version":1}""")));
    }
    [Fact] public void CyclesAndTraversalAreRejected()
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"a.json"),"""{"extends":"a.json"}""");
        var loader=new CatalogLoader(root);
        Assert.Throws<InvalidDataException>(()=>loader.Load("a.json"));
        Assert.Throws<InvalidDataException>(()=>loader.Load("../outside.json"));
    }
}
