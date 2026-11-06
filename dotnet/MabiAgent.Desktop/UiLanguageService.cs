using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MabiAgent.Desktop;

public sealed class UiLanguageService
{
    public const string SimplifiedChinese = "zh-CN";
    public const string TraditionalChinese = "zh-TW";
    private static readonly Regex Placeholder = new(@"\{(?<index>\d+)(?:[^}]*)\}", RegexOptions.Compiled);
    public static UiLanguageService Shared { get; } = new();
    private const int MaximumCachedTranslations=512;
    private const int MaximumDynamicTextLength=512;
    private readonly IReadOnlyDictionary<string, string> simplified;
    private readonly IReadOnlyDictionary<string, string> traditional;
    private readonly IReadOnlyDictionary<string, TextPattern[]> patternsByFirstCharacter;
    private readonly TextPattern[] genericPatterns;
    private readonly ConcurrentDictionary<string,string> translationCache=new(StringComparer.Ordinal);
    private readonly object gate = new();

    public UiLanguageService() : this(ReadEmbedded(SimplifiedChinese), ReadEmbedded(TraditionalChinese)) { }

    internal UiLanguageService(
        IReadOnlyDictionary<string, string> simplified,
        IReadOnlyDictionary<string, string> traditional)
    {
        this.simplified = simplified;
        this.traditional = traditional;
        if (!simplified.Keys.Order(StringComparer.Ordinal).SequenceEqual(traditional.Keys.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("简繁语言资源键不一致。");
        foreach(var pair in traditional)
        {
            if(!Placeholder.IsMatch(pair.Key))continue;
            var sourceIndexes=Placeholder.Matches(pair.Key).Select(x=>int.Parse(x.Groups["index"].Value,CultureInfo.InvariantCulture)).ToHashSet();
            var targetIndexes=Placeholder.Matches(pair.Value).Select(x=>int.Parse(x.Groups["index"].Value,CultureInfo.InvariantCulture)).ToHashSet();
            if(!sourceIndexes.SetEquals(targetIndexes))
                throw new InvalidDataException($"语言资源占位符不一致：{pair.Key}");
        }
        var patterns=traditional
            .Where(x => Placeholder.IsMatch(x.Key) && x.Value.Contains('{'))
            .Select(x => TextPattern.Create(x.Key, x.Value))
            .OrderByDescending(x => x.Source.Length)
            .ToArray();
        genericPatterns=patterns.Where(x=>x.Prefix.Length==0).ToArray();
        patternsByFirstCharacter=patterns
            .Where(x=>x.Prefix.Length>0)
            .GroupBy(x=>x.Prefix[..1],StringComparer.Ordinal)
            .ToDictionary(x=>x.Key,x=>x.ToArray(),StringComparer.Ordinal);
    }

    public event EventHandler? LanguageChanged;
    public string Culture { get; private set; } = SimplifiedChinese;
    public bool IsTraditionalChinese => Culture == TraditionalChinese;
    public string LanguageName => IsTraditionalChinese ? "繁體中文" : "简体中文";
    public int ResourceCount => traditional.Count;
    public int CachedTranslationCount => translationCache.Count;

    public string Text(string source) => TranslateText(source);

    public string WorkbenchText(string source)
        => TranslateText(Regex.Replace(source,@"[^ ·/（）]+",match=>TranslateText(match.Value)));

    public string Format(string source, params object?[] arguments)
        => TranslatePattern(source, arguments);

    public bool SetLanguage(string? culture)
    {
        var next = Normalize(culture);
        if (next is null || next == Culture) return next is not null;
        lock (gate){Culture=next;translationCache.Clear();}
        LanguageChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public string TranslateText(string source)
    {
        if (string.IsNullOrEmpty(source) || !IsTraditionalChinese) return source;
        if(traditional.TryGetValue(source,out var exact)) return exact;
        if(translationCache.TryGetValue(source,out var cached)) return cached;
        if(source.Length>MaximumDynamicTextLength) return source;
        var translated=TranslatePatternFromText(source);
        if(translationCache.Count<MaximumCachedTranslations) translationCache.TryAdd(source,translated);
        return translated;
    }

    public string TranslatePattern(string source, params object?[] arguments)
    {
        var culture = CultureInfo.GetCultureInfo(IsTraditionalChinese ? "zh-TW" : "zh-CN");
        if (!IsTraditionalChinese) return string.Format(culture, source, arguments);
        return traditional.TryGetValue(source, out var target)
            ? string.Format(culture, target, arguments)
            : string.Format(culture, source, arguments);
    }

    public static string? Normalize(string? culture)
    {
        var value = culture?.Trim();
        if (string.Equals(value, SimplifiedChinese, StringComparison.OrdinalIgnoreCase)) return SimplifiedChinese;
        if (string.Equals(value, TraditionalChinese, StringComparison.OrdinalIgnoreCase)) return TraditionalChinese;
        return null;
    }

    private string TranslatePatternFromText(string source)
    {
        if(source.Length>0&&patternsByFirstCharacter.TryGetValue(source[..1],out var patterns))
        {
            var translated=TranslateWith(source,patterns);
            if(translated is not null)return translated;
        }
        var matched=TranslateWith(source,genericPatterns);
        if(matched is not null)return matched;
        // Compose authored labels without converting user names or arbitrary text.
        foreach(var separator in new[]{" · ","、","\n"," / "})
            if(source.Contains(separator,StringComparison.Ordinal))
                return string.Join(separator,source.Split(separator).Select(TranslateText));
        var trimmed=source.Trim();
        if(trimmed!=source&&trimmed.Length>0)
            return source[..source.IndexOf(trimmed,StringComparison.Ordinal)]+TranslateText(trimmed)+source[(source.IndexOf(trimmed,StringComparison.Ordinal)+trimmed.Length)..];
        return source;
    }

    private string? TranslateWith(string source,IEnumerable<TextPattern> patterns)
    {
        foreach(var pattern in patterns)
        {
            var values=pattern.Match(source);if(values is null)continue;
            for(var index=0;index<values.Length;index++)
                if(values[index] is string value)values[index]=TranslateText(value);
            return string.Format(CultureInfo.GetCultureInfo("zh-TW"),pattern.Target,values);
        }
        return null;
    }

    private static IReadOnlyDictionary<string, string> ReadEmbedded(string culture)
    {
        var file = $"strings.{culture}.json";
        var output = Path.Combine(AppContext.BaseDirectory,"Localization",file);
        if(File.Exists(output))
            using(var stream=File.OpenRead(output))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)??throw new InvalidDataException($"语言资源为空：{output}");
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
        {
            var source=Path.Combine(directory.FullName,"MabiAgent.Desktop","Localization",file);
            if(File.Exists(source))
                using(var stream=File.OpenRead(source))
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)??throw new InvalidDataException($"语言资源为空：{source}");
        }
        throw new InvalidDataException($"缺少语言资源：{file}");
    }

    private sealed record TextPattern(string Source,string Prefix,string Target,IReadOnlyList<string> Literals,int[] PlaceholderIndexes)
    {
        public static TextPattern Create(string source,string target)
        {
            var literals=new List<string>();
            var indexes=new List<int>();
            var cursor=0;
            foreach(Match match in Placeholder.Matches(source))
            {
                literals.Add(source[cursor..match.Index]);
                indexes.Add(int.Parse(match.Groups["index"].Value,CultureInfo.InvariantCulture));
                cursor=match.Index+match.Length;
            }
            literals.Add(source[cursor..]);
            return new(source,literals[0],target,literals,indexes.ToArray());
        }

        public object?[]? Match(string source)
        {
            if(!source.StartsWith(Literals[0],StringComparison.Ordinal))return null;
            var values=new object?[PlaceholderIndexes.Length==0?0:PlaceholderIndexes.Max()+1];
            var cursor=Literals[0].Length;
            for(var occurrence=0;occurrence<PlaceholderIndexes.Length;occurrence++)
            {
                var next=Literals[occurrence+1];
                string value;
                if(next.Length==0&&occurrence==PlaceholderIndexes.Length-1){value=source[cursor..];cursor=source.Length;}
                else
                {
                    var boundary=source.IndexOf(next,cursor,StringComparison.Ordinal);
                    if(boundary<0)return null;
                    value=source[cursor..boundary];cursor=boundary+next.Length;
                }
                values[PlaceholderIndexes[occurrence]]=value;
            }
            return cursor==source.Length?values:null;
        }
    }
}
