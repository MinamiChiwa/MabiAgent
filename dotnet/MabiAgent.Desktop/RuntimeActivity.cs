using System.Diagnostics;
using MabiAgent.Core;
namespace MabiAgent.Desktop;

public sealed class RuntimeActivity(string directory)
{
    public sealed record Stage(string Name,long Started);
    private Stage current=new("空闲",Stopwatch.GetTimestamp());
    private readonly object logGate=new();
    private string? lastProgress;
    private readonly RuntimeDiagnostics diagnostics=new(directory);
    private readonly string trace=Guid.NewGuid().ToString("N");
    private string? progressWriteError;
    public string? DiagnosticWriteError=>diagnostics.WriteError??progressWriteError;
    public void Report(Exception error,[System.Runtime.CompilerServices.CallerMemberName]string operation="")=>Fault(error,operation,scope:"ui");
    public void Fault(Exception error,string operation,string? session=null,string? job=null,string? phase=null,string? page=null,string scope="global",bool pending=false)
    {diagnostics.Write(diagnostics.Create(trace,"error","desktop",phase??Current.Name,scope,session,job,null,page,"explicit_continue",operation,error,pending,operation+" > "+Current.Name));}
    public void Warning(string module,string message,string? session=null,string? job=null,string? page=null,string scope="dependency")
    {diagnostics.Write(diagnostics.Create(trace,"warning",module,Current.Name,scope,session,job,null,page,"review",message,trigger:module+" > "+Current.Name));}
    public Stage Current=>Volatile.Read(ref current);
    public string Description { get { var stage=Current;return stage.Name+$" {Stopwatch.GetElapsedTime(stage.Started).TotalSeconds:F1}s"; } }
    public IDisposable Enter(string name,bool command=false)
    {
        var next=new Stage(name,Stopwatch.GetTimestamp());var prior=Interlocked.Exchange(ref current,next);
        if(command)Log("开始 "+name);
        return new Scope(()=>{var elapsed=Stopwatch.GetElapsedTime(next.Started);Interlocked.CompareExchange(ref current,prior,next);if(command||elapsed.TotalSeconds>=3)Log($"结束 {name} {elapsed.TotalSeconds:F2}s");});
    }
    public async Task AcquireAsync(SemaphoreSlim gate,string command,CancellationToken token,TimeSpan? timeout=null)
    {
        if(await gate.WaitAsync(timeout??TimeSpan.FromSeconds(10),token).ConfigureAwait(false))return;
        var error=new TimeoutException(command+"等待调度超时；当前阶段："+Description);Fault(error,command);Log(error.ToString());throw error;
    }
    public void Progress(string message)
    {if(message==lastProgress)return;lastProgress=message;Log(message);}
    public void Log(string message)
    {
        try{lock(logGate){Directory.CreateDirectory(directory);var path=Path.Combine(directory,"runtime-progress.log");
            if(File.Exists(path)&&new FileInfo(path).Length>262144)File.WriteAllLines(path,File.ReadLines(path).TakeLast(100).ToArray());
            File.AppendAllText(path,DateTimeOffset.Now.ToString("O")+" "+message[..Math.Min(message.Length,8192)]+Environment.NewLine);progressWriteError=null;}}catch(Exception error){progressWriteError=error.ToString();}
    }
    private sealed class Scope(Action complete):IDisposable {public void Dispose()=>complete();}
}
