using Avalonia.Controls;
using Ursa.Controls;
namespace MabiAgent.Desktop;
internal static class UiDialogs
{
    private static readonly HashSet<Window> pending=[];
    public static async Task<bool> Confirm(Window owner,string message)
    {
        if(!owner.IsVisible||!pending.Add(owner))return false;
        try{return await MessageBox.ShowAsync(owner,RuntimeLocalization.Translate(message),RuntimeLocalization.Translate("未保存修改"),MessageBoxIcon.Warning,MessageBoxButton.YesNo)==MessageBoxResult.Yes;}
        finally{pending.Remove(owner);}
    }
    public static async Task RetryDiscard(Control control,Action retry)
    {if(TopLevel.GetTopLevel(control) is Window window&&await Confirm(window,"当前修改尚未保存，放弃并继续？"))retry();}
}
