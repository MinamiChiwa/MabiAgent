using Xunit;
namespace MabiAgent.Workflows.Tests;

public sealed class InventoryRefreshPolicyTests
{
    [Theory]
    [InlineData(null,false)]
    [InlineData(0L,true)]
    [InlineData(1L,false)]
    public void OnlyConfirmedAbsenceUsesExistingRelogin(long? quantity,bool expected)
        => Assert.Equal(expected,InventoryRefreshPolicy.UseSameCharacterRelogin(quantity));
}
