using System.Text;
using System.Text.Json;
namespace MabiAgent.Core;

public sealed record RuntimeFaultEvent(DateTimeOffset At,string EventId,string TraceId,string Severity,string Module,string Stage,
    string Scope,string? Session,string? Job,string? Action,string? Page,string Recovery,string Message,string? Exception,
    bool Pending=false,string? Trigger=null);

/// <summary>Bounded JSONL; failure is returned to the host, never hidden or thrown into input dispatch.</summary>
public sealed class RuntimeDiagnostics(string directory,TimeProvider? time=null)
{
    private static readonly object FileGate=new();
    private const int Limit=262144;
    private readonly TimeProvider clock=time??TimeProvider.System;
    public string? WriteError{get;private set;}
    public RuntimeFaultEvent Create(string trace,string severity,string module,string stage,string scope,string? session,string? job,string? action,
        string? page,string recovery,string message,Exception? error=null,bool pending=false,string? trigger=null)=>
        new(clock.GetUtcNow(),Guid.NewGuid().ToString("N"),trace,severity,module,stage,scope,session,job,action,page,recovery,message,error?.ToString(),pending,trigger);
    public bool Write(RuntimeFaultEvent value)
    {
        try
        {
            var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)+Environment.NewLine);
            if(bytes.Length>Limit)throw new IOException("单条诊断超出日志容量，原异常仍保留在运行状态");
            lock(FileGate)
            {
                Directory.CreateDirectory(directory);var path=Path.Combine(directory,"runtime-events.jsonl");
                if(File.Exists(path)&&new FileInfo(path).Length+bytes.Length>Limit)File.Move(path,path+".previous",true);
                using var stream=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read);stream.Write(bytes);
            }
            WriteError=null;return true;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {WriteError=error.ToString();return false;}
    }
}
