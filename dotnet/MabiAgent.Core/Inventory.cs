using System.Collections.ObjectModel;
namespace MabiAgent.Core;

public sealed record StockObservation(long ItemId, long? Quantity, string Scope, string Source, string Version, bool Confirmed, string? UnknownReason = null)
{
    // Continuous, healthy capture coverage is planning evidence, never a receipt.
    public double? VerifiedThrough { get; init; }
}

public sealed class InventorySnapshot(string scope)
{
    public string Scope { get; } = string.IsNullOrWhiteSpace(scope) ? throw new ArgumentException("库存范围不能为空") : scope;
    private readonly Dictionary<long, StockObservation> rows = [];
    private readonly Dictionary<string, IReadOnlyDictionary<long,long?>> storage = [];
    public IReadOnlyDictionary<long,StockObservation> Observations => new ReadOnlyDictionary<long,StockObservation>(rows);
    public IReadOnlyDictionary<string,IReadOnlyDictionary<long,long?>> Storage => new ReadOnlyDictionary<string,IReadOnlyDictionary<long,long?>>(storage);
    public void Set(StockObservation value)
    {
        if (value.ItemId <= 0 || value.Quantity < 0 || value.Scope != Scope || string.IsNullOrWhiteSpace(value.Source))
            throw new ArgumentException("库存数量或来源范围无效");
        rows[value.ItemId] = value;
    }
    public StockObservation Query(long item) => rows.GetValueOrDefault(item) ?? new(item,null,Scope,"estimate","",false,"未观察");
    public void SetStorage(string owner, IReadOnlyDictionary<long,long?> values)
    {
        if(string.IsNullOrWhiteSpace(owner) || values.Any(p=>p.Key<=0 || p.Value<0)) throw new ArgumentException("仓库范围或数量无效");
        storage[owner]=new ReadOnlyDictionary<long,long?>(new Dictionary<long,long?>(values));
    }
}
