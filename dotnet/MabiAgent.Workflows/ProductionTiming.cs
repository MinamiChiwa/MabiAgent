namespace MabiAgent.Workflows;
public static class ProductionTiming
{
    public static double Duration(double standardSeconds, bool lifeVip)
    {
        if (!double.IsFinite(standardSeconds) || standardSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(standardSeconds));
        return lifeVip ? standardSeconds / 2 : standardSeconds;
    }
}
