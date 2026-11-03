using System.Text.Json.Nodes;
namespace MabiAgent.Core;
public sealed class CatalogLoader
{
    public string Root {get;}
    public CatalogLoader(string root)=>Root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    public string Resolve(string relative)
    {
        var path=Path.GetFullPath(Path.Combine(Root,relative));
        if(!path.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("配置路径越出项目");
        return path;
    }
    public JsonObject Load(string relative)=>Load(relative,new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    private JsonObject Load(string relative,HashSet<string> ancestors)
    {
        var path=Resolve(relative);if(!ancestors.Add(path))throw new InvalidDataException("配置继承循环");
        var config=JsonNode.Parse(File.ReadAllText(path))?.AsObject()??throw new InvalidDataException("配置不是对象");
        var parent=config["extends"]?.GetValue<string>();config.Remove("extends");
        if(parent is null)return config;
        if(string.IsNullOrWhiteSpace(parent)||Path.IsPathRooted(parent))throw new InvalidDataException("父配置必须为项目相对路径");
        return Merge(Load(parent,ancestors),config);
    }
    public static JsonObject Merge(JsonObject parent,JsonObject child)
    {
        var merged=(JsonObject)parent.DeepClone();
        foreach(var (key,value) in child) merged[key]=merged[key] is JsonObject a && value is JsonObject b?Merge(a,b):value?.DeepClone();
        return merged;
    }
}
