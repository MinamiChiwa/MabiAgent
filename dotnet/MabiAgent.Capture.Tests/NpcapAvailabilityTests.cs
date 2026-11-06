using MabiAgent.Capture;
using Xunit;

namespace MabiAgent.Capture.Tests;

public sealed class NpcapAvailabilityTests
{
    [Fact] public void AvailableAdapterSuppressesInstallationReminder()
    {
        var calls = 0;
        var result = NpcapAvailability.Check(() => { calls++; return [new("adapter", "Ethernet")]; });
        Assert.True(result.Available); Assert.Null(result.Detail); Assert.Equal(1, calls);
    }

    [Fact] public void LoadedLibraryWithNoAdaptersDoesNotClaimToBeUsable()
    {
        var result = NpcapAvailability.Check(() => []);
        Assert.False(result.Available); Assert.Contains("网卡", result.Detail);
    }

    [Theory]
    [InlineData("missing")][InlineData("wrong_architecture")][InlineData("missing_export")]
    [InlineData("permissions")][InlineData("discovery")]
    public void DetectionFailureProvidesReminderRatherThanCrashingStartup(string failure)
    {
        var result = NpcapAvailability.Check(() => throw (failure switch
        {
            "missing" => new DllNotFoundException(),
            "wrong_architecture" => new BadImageFormatException(),
            "missing_export" => new EntryPointNotFoundException(),
            "permissions" => new UnauthorizedAccessException("permission denied"),
            _ => new IOException("discovery failed")
        }));
        Assert.False(result.Available); Assert.False(string.IsNullOrWhiteSpace(result.Detail));
        Assert.Equal("npcap.com", new Uri(NpcapAvailability.OfficialDownloadUrl).Host);
        Assert.Equal("https", new Uri(NpcapAvailability.OfficialDownloadUrl).Scheme);
    }
}
