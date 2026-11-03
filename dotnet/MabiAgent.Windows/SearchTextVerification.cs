namespace MabiAgent.Windows;
/// <summary>Only fresh, separated exact text readings authorize applying a search.</summary>
public sealed class SearchTextVerification
{
    private double sentAt,lastAt;private double? candidate;
    public void Begin(double at){sentAt=lastAt=at;candidate=null;}
    public bool Observe(bool exact,double at)
    {
        if(!double.IsFinite(at)||at<=sentAt||at<=lastAt)return false;lastAt=at;
        if(!exact){candidate=null;return false;}
        if(candidate is {} previous&&at-previous>=.2)return true;candidate??=at;return false;
    }
}
