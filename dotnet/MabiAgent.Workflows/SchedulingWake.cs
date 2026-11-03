using System.Threading.Channels;

namespace MabiAgent.Workflows;

/// <summary>A bounded notification, never a task queue or a replacement for durable state.</summary>
public sealed class SchedulingWake
{
    private readonly Channel<byte> signals=Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.DropWrite});
    public void Signal()=>signals.Writer.TryWrite(0);
    public async ValueTask WaitAsync(TimeSpan maximumDelay,CancellationToken stop)
    {
        if(maximumDelay<TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(maximumDelay));
        stop.ThrowIfCancellationRequested();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(maximumDelay);
        try{await signals.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);}
        catch(OperationCanceledException)when(!stop.IsCancellationRequested){}
        stop.ThrowIfCancellationRequested();
        while(signals.Reader.TryRead(out _)){}
    }
}

public static class AutomationCadence
{
    public static readonly TimeSpan Active=TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan Health=TimeSpan.FromSeconds(5);
    public static TimeSpan Until(double now,double? due)
        =>due is{} at&&double.IsFinite(at)?TimeSpan.FromSeconds(Math.Clamp(at-now,0,Health.TotalSeconds)):Health;
}
