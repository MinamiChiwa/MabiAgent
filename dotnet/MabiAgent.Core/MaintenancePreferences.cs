using System.Text.Json.Nodes;
namespace MabiAgent.Core;
public sealed record MaintenancePreferences(long GoldReserve=40000,bool AutoBuyBottles=true)
{
    public static MaintenancePreferences FromJson(JsonObject settings)
    {
        var repair=settings["repair"]?.AsObject()??throw new InvalidDataException("缺少修理设置");
        var reserve=repair["gold_reserve_min"]?.GetValue<long>()??40000;
        if(reserve<0||reserve>9007199254740991)throw new InvalidDataException("金币留存无效");
        return new(reserve,repair["auto_buy_bottles"]?.GetValue<bool>()??true);
    }
    public bool CanSpend(long gold,long price)=>price>=0 && gold>=price && gold-price>=GoldReserve;
}
