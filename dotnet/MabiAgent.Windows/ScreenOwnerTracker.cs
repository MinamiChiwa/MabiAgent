namespace MabiAgent.Windows;

/// <summary>Connection changes are stock epochs, never character changes.</summary>
public sealed class ScreenOwnerTracker
{
    private (string Account,string Character)? owner;
    private bool changed;
    public bool Observe(string? account,string? character)
    {
        if(changed)return true;
        if(string.IsNullOrWhiteSpace(account)||string.IsNullOrWhiteSpace(character))return false;
        var current=(account,character);
        if(owner is{} known&&known!=current)return changed=true;
        owner=current;return false;
    }
}
