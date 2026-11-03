namespace MabiAgent.Core;
public enum SupplyStatus { Unknown, PurchaseRequired, Ready }
public sealed record SupplyPlan(SupplyStatus Status, long ReservedQuantity, long PurchaseQuantity);
public sealed record ConsumablePolicy(string Name, int PerAttempt, int Attempts)
{
    public SupplyPlan Plan(long? available)
    {
        if(string.IsNullOrWhiteSpace(Name) || PerAttempt<=0 || Attempts<=0 || available<0) throw new ArgumentException("采集消耗配置无效");
        long required=checked((long)PerAttempt*Attempts);
        if(available is null) return new(SupplyStatus.Unknown,0,0);
        return available>=required ? new(SupplyStatus.Ready,required,0) : new(SupplyStatus.PurchaseRequired,available.Value,required-available.Value);
    }
}

// The game consumes bottles. This only tracks the confirmed attempt watermark;
// it never overwrites captured inventory or equates attempts with output.
public sealed class GatheringBatch(string route, int attempts, long reservedConsumables)
{
    public string Route { get; }=route;
    public int Attempts { get; }=attempts>0 ? attempts : throw new ArgumentOutOfRangeException(nameof(attempts));
    public int ObservedAttempts { get; private set; }
    public long RemainingConsumables => reservedConsumables-ObservedAttempts;
    public long? OutputQuantity { get; private set; }
    public void ObserveAttempts(int count)
    {
        if(count<ObservedAttempts || count>Attempts || count>reservedConsumables) throw new InvalidOperationException("采集进度倒退或超过预留");
        ObservedAttempts=count;
    }
    public void Complete(long? quantity)
    {
        if(ObservedAttempts!=Attempts || quantity<0) throw new InvalidOperationException("原生采集批尚未结束");
        OutputQuantity=quantity;
    }
}
