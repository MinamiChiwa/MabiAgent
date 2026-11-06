using System.Text.Json.Nodes;
using MabiAgent.Update;
using Xunit;

namespace MabiAgent.Update.Tests;

public sealed class UpdatePreferencesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mabi-update-preferences-" + Guid.NewGuid().ToString("N"));
    private string PreferencesPath => Path.Combine(root, "updates", "preferences.json");

    public UpdatePreferencesTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void MissingPreferencesEnableStartupChecks()
    {
        var preferences = UpdatePreferences.Load(PreferencesPath);

        Assert.True(preferences.CheckOnStart);
        Assert.True(preferences.ShouldCheckOnStartup(["--root", root]));
    }

    [Fact]
    public void ExplicitFalseDisablesStartupChecks()
    {
        Write(new JsonObject { ["check_on_start"] = false });

        var preferences = UpdatePreferences.Load(PreferencesPath);

        Assert.False(preferences.CheckOnStart);
        Assert.False(preferences.ShouldCheckOnStartup(["--root", root]));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"check_on_start\":\"false\"}")]
    public void InvalidPreferencesFallBackToEnabled(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
        File.WriteAllText(PreferencesPath, json);

        Assert.True(UpdatePreferences.Load(PreferencesPath).CheckOnStart);
    }

    [Theory]
    [InlineData("--self-check")]
    [InlineData("--ui-preview")]
    [InlineData("--close-check")]
    public void DiagnosticRunsNeverStartAnUpdateNetworkCheck(string flag)
    {
        var preferences = UpdatePreferences.Load(PreferencesPath);

        Assert.False(preferences.ShouldCheckOnStartup(["--root", root, flag]));
    }

    [Fact]
    public void SavingTheTogglePreservesMirrorAndUnknownKeys()
    {
        Write(new JsonObject
        {
            ["check_on_start"] = true,
            ["mirror"] = "",
            ["future_setting"] = 42
        });

        UpdatePreferences.SaveCheckOnStart(PreferencesPath, false);

        var saved = JsonNode.Parse(File.ReadAllText(PreferencesPath))!.AsObject();
        Assert.False(saved["check_on_start"]!.GetValue<bool>());
        Assert.Equal("", saved["mirror"]!.GetValue<string>());
        Assert.Equal(42, saved["future_setting"]!.GetValue<int>());
    }

    private void Write(JsonObject value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
        File.WriteAllText(PreferencesPath, value.ToJsonString());
    }
}
