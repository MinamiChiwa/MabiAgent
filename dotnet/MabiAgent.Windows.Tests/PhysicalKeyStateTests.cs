using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;
public sealed class PhysicalKeyStateTests
{
    [Fact] public void UnmappedLegacyOemStateIsNotAPhysicalKey()
    {
        Assert.False(NativeWindowsOps.IsPhysicalKeyDown(0xF2, _ => unchecked((short)0x8000), _ => 0));
    }
    [Theory][InlineData(0x66)][InlineData(0x69)][InlineData(0x41)][InlineData(0xA1)][InlineData(0xF2)]
    public void MappedHeldKeysAlwaysInterrupt(int key)
    {
        Assert.True(NativeWindowsOps.IsPhysicalKeyDown(key, _ => unchecked((short)0x8000), _ => 0x49));
    }
    [Fact] public void UnmappedOrdinaryKeysAreNotSilentlyIgnored()
    {
        Assert.True(NativeWindowsOps.IsPhysicalKeyDown(0x41, _ => unchecked((short)0x8000), _ => 0));
        Assert.False(NativeWindowsOps.IsPhysicalKeyDown(0x66, _ => 0, _ => 0x4D));
    }
}
