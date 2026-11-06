using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
namespace MabiAgent.Desktop;
public partial class SponsorWindow:Window
{
    public const string AfdianUrl="https://afdian.com/a/minamichiwa";
    public const string PatreonUrl="https://patreon.com/chiwaminami";
    public SponsorWindow(){InitializeComponent();Opened+=(_,_)=>Controls.WindowSizing.FitToWorkingArea(this,Owner as Window);}
    private async void Open(string url){try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch(Exception e){await Ursa.Controls.MessageBox.ShowAsync(this,RuntimeLocalization.Translate("无法打开浏览器："+e.Message),RuntimeLocalization.Translate("赞助"));}}
    private void OpenAfdian(object sender,RoutedEventArgs e)=>Open(AfdianUrl);
    private void OpenPatreon(object sender,RoutedEventArgs e)=>Open(PatreonUrl);
    private void CloseDialog(object sender,RoutedEventArgs e)=>Close();
}
