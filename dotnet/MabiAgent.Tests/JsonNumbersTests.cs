using System.Text.Json.Nodes;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Tests;

public sealed class JsonNumbersTests
{
    [Fact] public void MemoryAndFileNumbersShareFiniteDoubleSemantics()
    {
        JsonNode[] values=[JsonValue.Create(60)!,JsonValue.Create(60L)!,JsonValue.Create(60f)!,JsonValue.Create(60d)!,JsonValue.Create(60m)!,JsonValue.Create(60u)!,JsonValue.Create(60UL)!,JsonNode.Parse("60")!,JsonNode.Parse("6e1")!];
        foreach(var value in values){Assert.Equal(60,value.GetFiniteDouble());Assert.Equal(60,value.DeepClone().GetFiniteDouble());Assert.Equal(60,JsonNode.Parse(value.ToJsonString())!.GetFiniteDouble());}
        Assert.Equal(.125,JsonValue.Create(.125m)!.GetFiniteDouble());
        Assert.Equal(0,JsonValue.Create(0L)!.GetFiniteDouble());
        Assert.Equal(-1,JsonValue.Create(-1L)!.GetFiniteDouble());
    }
    [Fact] public void NonNumbersAndNonFiniteValuesAreRejected()
    {
        foreach(var node in new JsonNode[]{JsonValue.Create("60")!,JsonValue.Create(true)!,new JsonObject(),new JsonArray(),JsonNode.Parse("1e400")!})Assert.Throws<InvalidDataException>(()=>node.GetFiniteDouble());
        foreach(var number in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity})Assert.Throws<InvalidDataException>(()=>JsonValue.Create(number)!.GetFiniteDouble());
    }
}
