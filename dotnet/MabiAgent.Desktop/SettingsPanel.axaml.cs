using Avalonia;
using Avalonia.Controls;
namespace MabiAgent.Desktop;
public partial class SettingsPanel:UserControl
{
    public SettingsPanel(){InitializeComponent();SizeChanged+=(_,_)=>UpdateColumns();Controls.SearchablePicker.OpenOnFocus<ReserveChoice>(ReservePicker,g=>g.Name);}
    // Two columns when each can hold a label plus a 200px control; otherwise stack them.
    private void UpdateColumns()
    {
        var single=Bounds.Width<820;
        SettingsColumns.ColumnDefinitions[1].Width=new GridLength(single?0:16);
        SettingsColumns.ColumnDefinitions[2].Width=single?new GridLength(0):new GridLength(1,GridUnitType.Star);
        Grid.SetColumnSpan(RunColumn,single?3:1);
        Grid.SetRow(MaintenanceColumn,single?1:0);Grid.SetColumn(MaintenanceColumn,single?0:2);Grid.SetColumnSpan(MaintenanceColumn,single?3:1);
        MaintenanceColumn.Margin=new Thickness(0,single?16:0,0,0);
    }
}
