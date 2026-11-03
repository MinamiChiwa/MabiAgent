using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
namespace MabiAgent.Desktop.Controls;

public sealed class HelpButton:Button
{
    internal static readonly TimeSpan MaximumOpenTime=TimeSpan.FromSeconds(6);
    private readonly DispatcherTimer closeTimer=new(){Interval=MaximumOpenTime};
    private TopLevel? owner;
    private Visual[] ancestors=[];
    public static readonly StyledProperty<string?> MessageProperty=AvaloniaProperty.Register<HelpButton,string?>(nameof(Message));
    public string? Message{get=>GetValue(MessageProperty);set=>SetValue(MessageProperty,value);}
    protected override Type StyleKeyOverride=>typeof(Button);
    public HelpButton()
    {
        Classes.Add("help");Content=UiIcon.Create("help");
        AutomationProperties.SetName(this,"查看说明");ToolTip.SetShowDelay(this,600);ToolTip.SetBetweenShowDelay(this,-1);
        GotFocus+=(_,e)=>{if(e.NavigationMethod!=NavigationMethod.Pointer)OpenHelp();};
        LostFocus+=(_,_)=>CloseHelp();PointerExited+=(_,_)=>CloseHelp();
        closeTimer.Tick+=(_,_)=>CloseHelp();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);owner=TopLevel.GetTopLevel(this);
        ancestors=this.GetVisualAncestors().ToArray();
        foreach(var ancestor in ancestors)ancestor.PropertyChanged+=AncestorPropertyChanged;
        owner?.AddHandler(PointerPressedEvent,OwnerPointerPressed,RoutingStrategies.Tunnel);
        owner?.AddHandler(KeyDownEvent,OwnerKeyDown,RoutingStrategies.Tunnel);
        if(owner is Window window)window.Deactivated+=OwnerDeactivated;
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CloseHelp();
        foreach(var ancestor in ancestors)ancestor.PropertyChanged-=AncestorPropertyChanged;
        ancestors=[];
        owner?.RemoveHandler(PointerPressedEvent,OwnerPointerPressed);
        owner?.RemoveHandler(KeyDownEvent,OwnerKeyDown);
        if(owner is Window window)window.Deactivated-=OwnerDeactivated;
        owner=null;base.OnDetachedFromVisualTree(e);
    }
    private void OwnerPointerPressed(object? sender,PointerPressedEventArgs e)
    {if(e.Source is not Visual source||(!ReferenceEquals(source,this)&&!source.GetVisualAncestors().Contains(this)))CloseHelp();}
    private void OwnerKeyDown(object? sender,KeyEventArgs e){if(e.Key==Key.Escape)CloseHelp();}
    private void OwnerDeactivated(object? sender,EventArgs e)=>CloseHelp();
    private void AncestorPropertyChanged(object? sender,AvaloniaPropertyChangedEventArgs e)
    {if(e.Property==IsVisibleProperty&&Equals(e.NewValue,false))CloseHelp();}
    private void OpenHelp(){if(IsEffectivelyVisible&&!string.IsNullOrWhiteSpace(Message))ToolTip.SetIsOpen(this,true);}
    private void CloseHelp(){closeTimer.Stop();ToolTip.SetIsOpen(this,false);}
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if(change.Property==MessageProperty){CloseHelp();ToolTip.SetTip(this,Message);AutomationProperties.SetHelpText(this,Message);}
        if(change.Property==ToolTip.IsOpenProperty){closeTimer.Stop();if(ToolTip.GetIsOpen(this))closeTimer.Start();}
        if(change.Property==IsVisibleProperty&&!IsVisible)CloseHelp();
    }
    protected override void OnClick(){base.OnClick();if(ToolTip.GetIsOpen(this))CloseHelp();else OpenHelp();}
    protected override void OnKeyDown(KeyEventArgs e){if(e.Key==Key.Escape){ToolTip.SetIsOpen(this,false);e.Handled=true;}else base.OnKeyDown(e);}
}

public sealed class UiIcon:AvaloniaObject
{
    public static readonly AttachedProperty<string?> KindProperty=AvaloniaProperty.RegisterAttached<UiIcon,Control,string?>("Kind");
    public static string? GetKind(Control control)=>control.GetValue(KindProperty);
    public static void SetKind(Control control,string? value)=>control.SetValue(KindProperty,value);
    private static readonly Dictionary<string,Geometry> shapes=new(StringComparer.Ordinal);
    private static readonly Dictionary<string,string> paths=new()
    {
        ["github"]="M12 1a11 11 0 0 0-3.48 21.44c.55.1.75-.24.75-.53v-2.05c-3.06.67-3.71-1.3-3.71-1.3-.5-1.27-1.22-1.61-1.22-1.61-1-.68.08-.67.08-.67 1.1.08 1.68 1.13 1.68 1.13.98 1.68 2.57 1.2 3.2.92.1-.71.38-1.2.7-1.48-2.44-.28-5.01-1.22-5.01-5.44 0-1.2.43-2.18 1.13-2.95-.11-.28-.49-1.4.11-2.91 0 0 .92-.3 3.03 1.13a10.5 10.5 0 0 1 5.51 0c2.1-1.43 3.03-1.13 3.03-1.13.6 1.51.22 2.63.11 2.91.7.77 1.13 1.75 1.13 2.95 0 4.23-2.57 5.16-5.02 5.43.4.34.75 1.01.75 2.04v3.03c0 .29.2.64.76.53A11 11 0 0 0 12 1z",
        ["queue"]="M4 4h3v3H4z M10 4h10v3H10z M4 10h3v3H4z M10 10h10v3H10z M4 16h3v3H4z M10 16h10v3H10z",
        ["recipe"]="M3 3h8v18H3z M13 3h8v18h-8z M5 6v2h4V6z M15 6v2h4V6z M5 11v2h4v-2z M15 11v2h4v-2z",
        ["settings"]="M3 5h10v2H3z M17 5h4v2h-4z M13 3h4v6h-4z M3 11h3v2H3z M10 11h11v2H10z M6 9h4v6H6z M3 17h10v2H3z M17 17h4v2h-4z M13 15h4v6h-4z",
        ["account"]="M12 2a4 4 0 1 1 0 8a4 4 0 1 1 0-8 M4 21v-3a8 6 0 0 1 16 0v3z",
        ["chain"]="M5 3h5v5H5z M14 16h5v5h-5z M7 8h2v5h8v3h-2v-1H7z M14 3h5v5h-5z M5 16h5v5H5z",
        ["anchor"]="M11 1h2v4h-2z M11 19h2v4h-2z M1 11h4v2H1z M19 11h4v2h-4z M12 5a7 7 0 1 1 0 14a7 7 0 1 1 0-14 M12 7a5 5 0 1 0 0 10a5 5 0 1 0 0-10 M12 10a2 2 0 1 1 0 4a2 2 0 1 1 0-4",
        ["help"]="M12 2a10 10 0 1 1 0 20a10 10 0 1 1 0-20 M12 4a8 8 0 1 0 0 16a8 8 0 1 0 0-16 M9 8a3 3 0 0 1 6 0c0 2-2 2-2 4h-2c0-3 2-3 2-4a1 1 0 0 0-2 0z M11 15h2v2h-2z",
        ["add"]="M11 3h2v8h8v2h-8v8h-2v-8H3v-2h8z",
        ["close"]="M5 3 12 10 19 3 21 5 14 12 21 19 19 21 12 14 5 21 3 19 10 12 3 5z",
        ["play"]="M7 3v18l15-9z",["pause"]="M6 3h4v18H6z M14 3h4v18h-4z",
        ["refresh"]="M18 4V1l5 5-5 5V7a7 7 0 1 0 0 10l2 1a9 9 0 1 1-2-14z",
        ["save"]="M3 3h15l3 3v15H3z M6 3v7h10V3z M7 14v6h10v-6z",
        ["folder"]="M2 5h8l2 2h10v14H2z",
        ["heart"]="M12 21 3 12C-3 4 7-2 12 6c5-8 15-2 9 6z",
        ["image"]="M2 3h20v18H2z M4 5v14h16V5z M6 17l4-6 3 4 2-3 3 5z M17 6a2 2 0 1 1 0 4a2 2 0 1 1 0-4",
        ["check"]="M2 12l6 6L22 4l-2-2L8 14l-4-4z",
        ["trash"]="M4 6h16v2H4z M9 3h6v2H9z M6 9h12l-1 12H7z",
        ["up"]="M3 13 12 4l9 9-2 2-6-6v12h-2V9l-6 6z",
        ["down"]="M3 11 12 20l9-9-2-2-6 6V3h-2v12L5 9z",
        ["export"]="M11 2h2v11l4-4 2 2-7 7-7-7 2-2 4 4z M3 16h2v4h14v-4h2v6H3z",
        ["logo"]="M12 2l2.6 7.4L22 12l-7.4 2.6L12 22l-2.6-7.4L2 12l7.4-2.6z",
        ["search"]="M10 3a7 7 0 1 1 0 14a7 7 0 1 1 0-14 M10 5a5 5 0 1 0 0 10a5 5 0 1 0 0-10 M14.6 16 16 14.6 21.7 20.3 20.3 21.7z",
        ["external"]="M14 3h7v7h-2V6.4l-8.3 8.3-1.4-1.4L17.6 5H14z M5 5h6v2H7v10h10v-4h2v6H5z",
        ["inbox"]="F0 M3 13l3-9h12l3 9v7H3z M5.2 13H9l1 2h4l1-2h3.8L16.6 6H7.4z",
        ["touch"]="M9 2h2v9h1V7h2v5h1V8h2v5h1v-3h2v7a5 5 0 0 1-5 5h-3a5 5 0 0 1-4.6-3L5 13l1.7-1 2.3 3z",
        ["palette"]="F0 M12 2a10 10 0 1 0 0 20h2v-4h4a4 4 0 0 0 4-4C22 7 17.5 2 12 2z M7.5 8a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3z M12 5.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3z M16.5 8a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3z",
        ["repair"]="M21 6.5a5.5 5.5 0 0 1-7.4 5.2L6 19.3 3.7 17l7.6-7.6A5.5 5.5 0 0 1 17.5 2l-3 3 .5 3 3 .5z",
        ["bag"]="M4 8h16l-1 13H5z M8 8V6a4 4 0 0 1 8 0v2h-2V6a2 2 0 0 0-4 0v2z",
        ["wing"]="M3 20c0-9 6-15 18-17-1 5-3 8-6 9h3c-2 3-5 5-9 5h2c-2 2-5 3-8 3z",
        ["grip"]="M8 4h3v3H8z M13 4h3v3h-3z M8 10.5h3v3H8z M13 10.5h3v3h-3z M8 17h3v3H8z M13 17h3v3h-3z",
        ["edit"]="M14.1 4.9l5 5L8.6 20.4H3.6v-5z M16.2 2.8l1.4-1.4a1 1 0 0 1 1.4 0l3.6 3.6a1 1 0 0 1 0 1.4l-1.4 1.4z",
        ["sort-up"]="M12 5l6 8H6z",["sort-down"]="M12 19l-6-8h12z",
        ["cart"]="M2 3h4l.7 3H22l-2.6 9.3a2 2 0 0 1-1.9 1.5H9.3a2 2 0 0 1-2-1.6L4.4 5H2z M9 18.5a1.75 1.75 0 1 0 0 3.5a1.75 1.75 0 1 0 0-3.5z M18 18.5a1.75 1.75 0 1 0 0 3.5a1.75 1.75 0 1 0 0-3.5z",
        ["keyboard"]="F0 M2 6h20v12H2z M4 8v8h16V8z M5 9h2v2H5z M8 9h2v2H8z M11 9h2v2h-2z M14 9h2v2h-2z M17 9h2v2h-2z M7 13h10v2H7z",
        ["clock"]="F0 M12 2a10 10 0 1 1 0 20a10 10 0 1 1 0-20z M12 4a8 8 0 1 0 0 16a8 8 0 1 0 0-16z M11 6h2v5.4l3.9 2.3-1 1.7L11 12.6z",
        ["sync"]="F0 M4 5h16v11H4z M6 7v7h12V7z M9 18h6v2H9z M11 9h2v3h-2z"
    };
    static UiIcon()
    {
        KindProperty.Changed.AddClassHandler<Control>((control,_)=>Apply(control));
        ContentControl.ContentProperty.Changed.AddClassHandler<ContentControl>((control,_)=>Apply(control));
    }
    private static void Apply(Control control)
    {
        if(GetKind(control) is not string kind||control is not ContentControl host||host.Content is not string label)return;
        if(AutomationProperties.GetName(host) is null)AutomationProperties.SetName(host,label);
        host.Content=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,VerticalAlignment=VerticalAlignment.Center,Children={Create(kind),new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center}}};
    }
    public static PathIcon Create(string kind)=>new(){Data=Shape(kind),Width=16,Height=16,VerticalAlignment=VerticalAlignment.Center};
    internal static Geometry Shape(string kind)
    {if(!shapes.TryGetValue(kind,out var geometry)){geometry=Geometry.Parse(paths.GetValueOrDefault(kind,paths["queue"]));shapes[kind]=geometry;}return geometry;}
}

public static class SearchablePicker
{
    // Long lists use AutoCompleteBox; opening the full list on focus keeps it usable as a dropdown too.
    public static void OpenOnFocus<T>(AutoCompleteBox box,Func<T,string> label)
    {
        string? selectedText=box.SelectedItem is T initial?label(initial):null;
        box.SelectionChanged+=(_,_)=>{if(box.SelectedItem is T selected)selectedText=label(selected);};
        box.ItemFilter=(search,item)=>string.IsNullOrEmpty(search)
            ||string.Equals(search,box.SelectedItem is T selected?label(selected):selectedText,StringComparison.CurrentCulture)
            ||item is T choice&&label(choice).Contains(search,StringComparison.CurrentCultureIgnoreCase);
        box.GotFocus+=(_,_)=>box.IsDropDownOpen=true;
        box.PointerReleased+=(_,_)=>{if(!box.IsDropDownOpen)box.IsDropDownOpen=true;};
    }
}

// XAML-usable vector icon sharing UiIcon's path table.
public sealed class Icon:PathIcon
{
    public static readonly StyledProperty<string?> KindProperty=AvaloniaProperty.Register<Icon,string?>(nameof(Kind));
    public string? Kind{get=>GetValue(KindProperty);set=>SetValue(KindProperty,value);}
    protected override Type StyleKeyOverride=>typeof(PathIcon);
    static Icon(){WidthProperty.OverrideDefaultValue<Icon>(16);HeightProperty.OverrideDefaultValue<Icon>(16);VerticalAlignmentProperty.OverrideDefaultValue<Icon>(VerticalAlignment.Center);}
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {base.OnPropertyChanged(change);if(change.Property==KindProperty)Data=Kind is null?null:UiIcon.Shape(Kind);}
}
