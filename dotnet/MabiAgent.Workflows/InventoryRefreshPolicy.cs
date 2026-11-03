namespace MabiAgent.Workflows;

/// <summary>Call only with the current complete packet backpack quantity.</summary>
public static class InventoryRefreshPolicy
{
    public static bool UseSameCharacterRelogin(long? privateTheaterQuantity) => privateTheaterQuantity == 0;
}
