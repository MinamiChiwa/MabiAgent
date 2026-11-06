namespace MabiAgent.Capture;

/// <summary>Resolves files from the selected process; no installation path is shipped or persisted.</summary>
public sealed class GameClientLayoutResolver(Func<uint,(long CreatedUtcTicks,string Executable)> readProcessIdentity)
{
    private ClientCaptureLayout? cached;
    private sealed record Failed(CaptureTarget Target,string? Directory,DateTimeOffset RetryAt,string Message);
    private Failed? failed;
    public void ResetFailure()=>Volatile.Write(ref failed,null);
    public string Status { get; private set; } = "选择游戏窗口后自动识别客户端版本";
    public bool NeedsDirectory { get; private set; }
    public string? FallbackDirectory { get; set; }
    public ClientCaptureLayout Resolve(CaptureTarget target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var prior=Volatile.Read(ref failed);
        if(prior is not null && prior.Target==target && prior.Directory==FallbackDirectory && DateTimeOffset.UtcNow<prior.RetryAt) throw new InvalidDataException(prior.Message);
        try
        {
            var identity = readProcessIdentity(checked((uint)target.ProcessId));
            if(identity.CreatedUtcTicks != target.StartedUtc.ToUniversalTime().Ticks) throw new InvalidDataException("游戏进程已变化，请重新绑定窗口");
            var directory = Path.GetDirectoryName(identity.Executable);
            if(string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("无法读取游戏程序所在目录");
            Status = "正在读取游戏文件，适配当前版本";
            ClientCaptureLayout result;
            try { result = ClientLayoutReader.Read(directory,cancellation,cached); }
            catch(Exception error) when(error is not OperationCanceledException and not OutOfMemoryException
                && !string.IsNullOrWhiteSpace(FallbackDirectory)
                && !Path.GetFullPath(directory).Equals(Path.GetFullPath(FallbackDirectory),StringComparison.OrdinalIgnoreCase))
            {
                // A launcher directory, unreadable metadata or unsupported files can all need an explicit directory.
                result = ClientLayoutReader.Read(FallbackDirectory!,cancellation,cached);
            }
            var after = readProcessIdentity(checked((uint)target.ProcessId));
            if(after != identity) throw new InvalidDataException("解析期间游戏进程已变化，请重新绑定窗口");
            cached = result; ResetFailure(); NeedsDirectory = false; Status = "当前客户端已解析 · " + result.AssemblyHash[..12]; return result;
        }
        catch(OperationCanceledException) { throw; }
        catch(Exception error) when(error is not OutOfMemoryException)
        {
            NeedsDirectory = true; Status = "客户端自动解析失败：" + error.Message;
            var message=Status + "。请在抓包设置中选择游戏程序所在目录后重试。";
            Volatile.Write(ref failed,new(target,FallbackDirectory,DateTimeOffset.UtcNow.AddSeconds(15),message));
            throw new InvalidDataException(message,error);
        }
    }
}
