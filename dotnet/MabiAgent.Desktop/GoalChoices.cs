using System.Collections;
using System.Text.Json.Nodes;
using MabiAgent.Core;
namespace MabiAgent.Desktop;

public sealed record GoalCategoryChoice(string Key,string Label);

internal static class GoalChoices
{
    private const string AllKey="all";
    public static GoalCategoryChoice AllProduction {get;}=new(AllKey,"全部设施");
    public static GoalCategoryChoice AllGathering {get;}=new(AllKey,"全部技能");
    public static IComparer RecipeComparer {get;}=new RecipeOrderComparer();

    public static GoalCategoryChoice[] ProductionCategories(IEnumerable<RecipeRow> recipes)
        => Categories(AllProduction,recipes.Select(recipe=>recipe.Device),"device:");

    public static GoalCategoryChoice[] GatheringCategories(IEnumerable<GatheringChoice> routes,IReadOnlyDictionary<string,string> skills)
        => Categories(AllGathering,routes.Select(route=>Skill(route,skills)),"skill:");

    public static bool Matches(RecipeRow recipe,GoalCategoryChoice? category)
        => category is null||category.Key==AllKey||category.Key=="device:"+recipe.Device;

    public static IEnumerable<GatheringChoice> FilterAndSort(IEnumerable<GatheringChoice> routes,GoalCategoryChoice? category,IReadOnlyDictionary<string,string> skills)
        => routes.Where(route=>category is null||category.Key==AllKey||category.Key=="skill:"+Skill(route,skills))
            .OrderBy(route=>Skill(route,skills),StringComparer.OrdinalIgnoreCase)
            .ThenBy(route=>route.Name,StringComparer.OrdinalIgnoreCase)
            .ThenBy(route=>route.Id,StringComparer.Ordinal);

    public static IReadOnlyDictionary<string,string> LoadGatheringSkills(string root,Func<JsonObject>? provider)
    {
        var skills=MapGatheringSkills(new CatalogLoader(root).Load("config/screen_catalog.1280x960.tw.json"));
        if(provider is not null)
            foreach(var (key,skill) in MapGatheringSkills(provider()))skills[key]=skill;
        return skills;
    }

    private static Dictionary<string,string> MapGatheringSkills(JsonObject config)
    {
        var skills=new Dictionary<string,string>(StringComparer.Ordinal);
        var aliasSkills=new Dictionary<string,string>(StringComparer.Ordinal);
        var conflictingAliases=new HashSet<string>(StringComparer.Ordinal);
        foreach(var route in config["gathering_routes"]?.AsObject()??[])
        {
            var skill=route.Value?["skill"]?.GetValue<string>();
            if(string.IsNullOrWhiteSpace(skill))continue;
            skill=Workspace.SkillName(skill);
            skills[route.Key]=skill;
            var alias=route.Value?["alias_of"]?.GetValue<string>();
            if(string.IsNullOrWhiteSpace(alias))continue;
            if(aliasSkills.TryGetValue(alias,out var existing)&&!string.Equals(existing,skill,StringComparison.OrdinalIgnoreCase))
            {
                aliasSkills.Remove(alias);
                conflictingAliases.Add(alias);
            }
            else if(!conflictingAliases.Contains(alias))aliasSkills[alias]=skill;
        }
        foreach(var (alias,skill) in aliasSkills)
            if(!skills.ContainsKey(alias)&&!conflictingAliases.Contains(alias))skills[alias]=skill;
        return skills;
    }

    private static string Skill(GatheringChoice route,IReadOnlyDictionary<string,string> skills)
        => skills.GetValueOrDefault(route.Id)??"未分类";

    private static GoalCategoryChoice[] Categories(GoalCategoryChoice all,IEnumerable<string> values,string prefix)
        => new[]{all}.Concat(values.Where(value=>!string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value=>value,StringComparer.OrdinalIgnoreCase)
            .Select(value=>new GoalCategoryChoice(prefix+value,value))).ToArray();

    private sealed class RecipeOrderComparer:IComparer
    {
        public int Compare(object? x,object? y)
        {
            if(x is not RecipeRow first||y is not RecipeRow second)return 0;
            var result=StringComparer.OrdinalIgnoreCase.Compare(first.Device,second.Device);
            if(result!=0)return result;
            result=StringComparer.OrdinalIgnoreCase.Compare(first.Name,second.Name);
            return result!=0?result:StringComparer.Ordinal.Compare(first.Id,second.Id);
        }
    }
}
