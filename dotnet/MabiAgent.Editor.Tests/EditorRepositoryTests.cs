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


public sealed class StorageCleanupTests
{
    [Fact] public void KeepsNewestRevisionsAndPurgesExpiredDeletedChains()
    {
        var root=Path.Combine(Path.GetTempPath(),"mabi-cleanup-"+Guid.NewGuid().ToString("N"));
        try
        {
            var revisions=Path.Combine(root,"chains/chain_a/revisions");Directory.CreateDirectory(revisions);
            for(var i=1;i<=StorageCleanup.KeptRevisions+5;i++)File.WriteAllText(Path.Combine(revisions,$"v{i}.json"),"{}");
            File.WriteAllText(Path.Combine(root,"chains/chain_a/chain.json"),"{}");
            var fresh=Path.Combine(root,"deleted/fresh");var old=Path.Combine(root,"deleted/old");var legacy=Path.Combine(root,"deleted/legacy");
            foreach(var folder in new[]{fresh,old,legacy})Directory.CreateDirectory(folder);
            var now=DateTimeOffset.UtcNow;
            StorageCleanup.MarkDeleted(fresh,now.AddDays(-1));StorageCleanup.MarkDeleted(old,now-StorageCleanup.DeletedRetention-TimeSpan.FromHours(1));
            var result=StorageCleanup.Run(root,now);
            Assert.Equal(5,result.Revisions);Assert.Equal(1,result.DeletedChains);
            Assert.False(File.Exists(Path.Combine(revisions,"v5.json")));Assert.True(File.Exists(Path.Combine(revisions,"v6.json")));
            Assert.True(File.Exists(Path.Combine(root,"chains/chain_a/chain.json")));
            Assert.True(Directory.Exists(fresh));Assert.False(Directory.Exists(old));
            Assert.True(File.Exists(Path.Combine(legacy,StorageCleanup.DeletedMarker)));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact] public void AnchorHistoryKeepsPresetAndNewestEntries()
    {
        var history=new JsonArray();for(var i=0;i<StorageCleanup.KeptAnchorHistory+10;i++)history.Add(new JsonObject{["revision"]=i});
        StorageCleanup.TrimAnchorHistory(history);
        Assert.Equal(StorageCleanup.KeptAnchorHistory,history.Count);
        Assert.Equal(0,history[0]!["revision"]!.GetValue<int>());
        Assert.Equal(StorageCleanup.KeptAnchorHistory+9,history[^1]!["revision"]!.GetValue<int>());
        Assert.Equal(11,history[1]!["revision"]!.GetValue<int>());
    }
}
