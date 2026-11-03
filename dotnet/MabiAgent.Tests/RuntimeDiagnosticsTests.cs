using System.Text.Json;
using MabiAgent.Core;
using Xunit;
namespace MabiAgent.Tests;
public sealed class RuntimeDiagnosticsTests:IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"mabi-diagnostics-"+Guid.NewGuid().ToString("N"));
    [Fact] public void StructuredFaultPreservesTraceAndMultilineException()
    {
        var log=new RuntimeDiagnostics(directory);Exception error;try{throw new InvalidOperationException("错误\n第二行");}catch(Exception e){error=e;}
        Assert.True(log.Write(log.Create("trace","error","production","craft","foreground","session","job","batch","recipe_detail","observe_only","未确认",error,true,"tick > craft")));
        var lines=File.ReadAllLines(Path.Combine(directory,"runtime-events.jsonl"));var value=JsonSerializer.Deserialize<RuntimeFaultEvent>(Assert.Single(lines))!;
        Assert.Equal("trace",value.TraceId);Assert.Equal("batch",value.Action);Assert.True(value.Pending);Assert.Contains(nameof(StructuredFaultPreservesTraceAndMultilineException),value.Exception);Assert.Equal(TimeSpan.Zero,value.At.Offset);
    }
    [Fact] public void RotationRetainsTwoBoundedValidFiles()
    {
        var log=new RuntimeDiagnostics(directory);for(var n=0;n<350;n++)Assert.True(log.Write(log.Create("trace","warning","test","wait","task",null,null,null,null,"review",new string('a',3000))));
        Assert.Equal(2,Directory.GetFiles(directory).Length);foreach(var file in Directory.GetFiles(directory)){Assert.InRange(new FileInfo(file).Length,1,262144);foreach(var line in File.ReadLines(file))Assert.NotNull(JsonSerializer.Deserialize<RuntimeFaultEvent>(line));}
    }
    [Fact] public void WriteFailureIsVisibleWithoutThrowing()
    {
        Directory.CreateDirectory(directory);var blocker=Path.Combine(directory,"file");File.WriteAllText(blocker,"x");var log=new RuntimeDiagnostics(blocker);
        Assert.False(log.Write(log.Create("t","error","test","write","task",null,null,null,null,"review","error")));Assert.NotNull(log.WriteError);
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
