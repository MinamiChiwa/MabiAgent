using System.Text.Json;
using System.Text.Json.Nodes;
namespace MabiAgent.Core;

public static class JsonNumbers
{
    // JsonValue<T> keeps its CLR type until serialized; JSON numbers must mean the same in both forms.
    public static double GetFiniteDouble(this JsonNode node)
    {
        if(node is not JsonValue value||value.GetValueKind()!=JsonValueKind.Number)throw new InvalidDataException("需要有限 JSON 数字");
        double number;
        if(value.TryGetValue<double>(out var d))number=d;
        else if(value.TryGetValue<long>(out var l))number=l;
        else if(value.TryGetValue<int>(out var i))number=i;
        else if(value.TryGetValue<float>(out var f))number=f;
        else if(value.TryGetValue<decimal>(out var m))number=(double)m;
        else if(value.TryGetValue<uint>(out var ui))number=ui;
        else if(value.TryGetValue<ulong>(out var ul))number=ul;
        else if(value.TryGetValue<short>(out var s))number=s;
        else if(value.TryGetValue<ushort>(out var us))number=us;
        else if(value.TryGetValue<byte>(out var b))number=b;
        else if(value.TryGetValue<sbyte>(out var sb))number=sb;
        else throw new InvalidDataException("不支持的 JSON 数字类型");
        return double.IsFinite(number)?number:throw new InvalidDataException("JSON 数字必须有限");
    }
}
