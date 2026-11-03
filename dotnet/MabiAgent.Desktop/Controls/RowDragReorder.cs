using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
namespace MabiAgent.Desktop.Controls;

// In-app pointer reorder for list rows. OS drag-and-drop custom formats do not round-trip
// reliably, so the drag stays inside the list: a snapshot follows the pointer, an insertion
// line shows the drop slot, and edges auto-scroll. The owner applies the move on release.
internal sealed class RowDragReorder
{
    const double Threshold=6,Edge=40;
    readonly Control list;
    readonly Func<Visual,Control?> rowOf;
    readonly Func<Visual,bool> isHandle;
    readonly Func<object,bool> canStart;
    readonly Func<IReadOnlyList<object>> items;
    readonly Action<object> scrollIntoView;
    readonly Func<object,object,bool,Task> drop;
    readonly Canvas layer=new(){IsHitTestVisible=false};
    readonly Border line=new(){Height=3,CornerRadius=new CornerRadius(2),IsVisible=false};
    readonly Image ghost=new(){Stretch=Stretch.Fill};
    readonly Border ghostFrame;
    readonly DispatcherTimer scroller=new(){Interval=TimeSpan.FromMilliseconds(70)};
    Point pressAt,pointer;object? pending,dragging;Control? sourceRow;double grab;IPointer? captured;
    (object Target,bool After)? slot;
    public bool IsDragging=>dragging is not null;

    public RowDragReorder(Control list,Func<Visual,Control?> rowOf,Func<Visual,bool> isHandle,Func<object,bool> canStart,
        Func<IReadOnlyList<object>> items,Action<object> scrollIntoView,Func<object,object,bool,Task> drop)
    {
        (this.list,this.rowOf,this.isHandle,this.canStart,this.items,this.scrollIntoView,this.drop)=(list,rowOf,isHandle,canStart,items,scrollIntoView,drop);
        ghostFrame=new Border{Child=ghost,CornerRadius=new CornerRadius(10),ClipToBounds=true,Opacity=.94,BoxShadow=BoxShadows.Parse("0 10 28 0 #47000000"),IsVisible=false};
        line.Transitions=[new DoubleTransition{Property=Canvas.TopProperty,Duration=TimeSpan.FromMilliseconds(90)}];
        layer.Children.Add(line);layer.Children.Add(ghostFrame);
        list.AddHandler(InputElement.PointerPressedEvent,Pressed,RoutingStrategies.Tunnel,true);
        list.AddHandler(InputElement.PointerMovedEvent,Moved,RoutingStrategies.Tunnel,true);
        list.AddHandler(InputElement.PointerReleasedEvent,Released,RoutingStrategies.Tunnel,true);
        list.AddHandler(InputElement.PointerCaptureLostEvent,(_,_)=>{if(IsDragging)Finish();});
        list.AddHandler(InputElement.KeyDownEvent,(_,e)=>{if(e.Key==Key.Escape&&IsDragging){Finish();e.Handled=true;}},RoutingStrategies.Tunnel);
        list.DetachedFromVisualTree+=(_,_)=>Finish();
        scroller.Tick+=(_,_)=>AutoScroll();
    }

    void Pressed(object? sender,PointerPressedEventArgs e)
    {
        pending=null;
        if(e.Source is not Visual source||!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed||e.KeyModifiers!=KeyModifiers.None)return;
        if(!isHandle(source)||rowOf(source) is not {DataContext:{} item} row)return;
        pending=item;sourceRow=row;pressAt=e.GetPosition(list);
    }
    void Moved(object? sender,PointerEventArgs e)
    {
        pointer=e.GetPosition(list);
        if(IsDragging){Track();e.Handled=true;return;}
        if(pending is null||sourceRow is null||!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)return;
        var delta=pointer-pressAt;if(delta.X*delta.X+delta.Y*delta.Y<Threshold*Threshold)return;
        var item=pending;pending=null;
        if(!canStart(item))return;
        Begin(item,e.Pointer);e.Handled=true;
    }
    async void Released(object? sender,PointerReleasedEventArgs e)
    {
        pending=null;if(!IsDragging)return;
        var source=dragging!;var target=slot;Finish();e.Handled=true;
        if(target is {} t&&!ReferenceEquals(t.Target,source))await drop(source,t.Target,t.After);
    }

    void Begin(object item,IPointer device)
    {
        dragging=item;var row=sourceRow!;
        var origin=row.TranslatePoint(default,list)??default;grab=pressAt.Y-origin.Y;
        var scale=TopLevel.GetTopLevel(list)?.RenderScaling??1;
        var size=new PixelSize(Math.Max(1,(int)Math.Ceiling(row.Bounds.Width*scale)),Math.Max(1,(int)Math.Ceiling(row.Bounds.Height*scale)));
        var snapshot=new RenderTargetBitmap(size,new Vector(96*scale,96*scale));snapshot.Render(row);
        (ghost.Source as IDisposable)?.Dispose();ghost.Source=snapshot;ghost.Width=row.Bounds.Width;ghost.Height=row.Bounds.Height;
        ghostFrame.Background=Resource("WorkspaceSurface");line.Background=Resource("SemiColorPrimary");
        Canvas.SetLeft(ghostFrame,origin.X);ghostFrame.IsVisible=true;
        row.Classes.Add("drag-source");
        AdornerLayer.SetAdorner(list,layer);
        device.Capture(list);captured=device;scroller.Start();Track();
    }
    void Track()
    {
        if(sourceRow is null)return;
        var height=sourceRow.Bounds.Height;
        Canvas.SetTop(ghostFrame,Math.Clamp(pointer.Y-grab,-height/2,list.Bounds.Height-height/2));
        slot=null;line.IsVisible=false;
        var rows=Rows();if(rows.Length==0)return;
        var (row,top)=rows.FirstOrDefault(r=>pointer.Y<r.Top+r.Row.Bounds.Height);
        if(row is null)(row,top)=rows[^1];
        var after=pointer.Y>top+row.Bounds.Height/2;
        var left=row.TranslatePoint(default,list)?.X??0;
        Canvas.SetLeft(line,left+8);line.Width=Math.Max(0,row.Bounds.Width-16);
        Canvas.SetTop(line,(after?top+row.Bounds.Height:top)-1.5);
        line.IsVisible=true;slot=(row.DataContext!,after);
    }
    void AutoScroll()
    {
        if(!IsDragging)return;
        var rows=Rows().Where(r=>r.Top+r.Row.Bounds.Height>0&&r.Top<list.Bounds.Height).ToArray();if(rows.Length==0)return;
        var all=items();object? next=null;
        if(pointer.Y<rows[0].Top+Edge){var i=IndexOf(all,rows[0].Row.DataContext);if(i>0)next=all[i-1];}
        else if(pointer.Y>list.Bounds.Height-Edge){var i=IndexOf(all,rows[^1].Row.DataContext);if(i>=0&&i<all.Count-1)next=all[i+1];}
        if(next is null)return;scrollIntoView(next);list.UpdateLayout();Track();
    }
    (Control Row,double Top)[] Rows()=>list.GetVisualDescendants().OfType<Control>()
        .Where(c=>c.IsEffectivelyVisible&&c.Bounds.Height>0&&ReferenceEquals(rowOf(c),c)&&c.DataContext is not null)
        .Select(c=>(Row:c,Top:c.TranslatePoint(default,list)?.Y??0)).OrderBy(r=>r.Top).ToArray();
    static int IndexOf(IReadOnlyList<object> all,object? item){for(var i=0;i<all.Count;i++)if(ReferenceEquals(all[i],item))return i;return -1;}
    IBrush? Resource(string key)=>list.TryFindResource(key,list.ActualThemeVariant,out var value)?value as IBrush:null;
    void Finish()
    {
        scroller.Stop();pending=null;slot=null;
        sourceRow?.Classes.Remove("drag-source");sourceRow=null;
        if(dragging is null)return;dragging=null;
        line.IsVisible=false;ghostFrame.IsVisible=false;(ghost.Source as IDisposable)?.Dispose();ghost.Source=null;
        AdornerLayer.SetAdorner(list,null);
        var device=captured;captured=null;if(ReferenceEquals(device?.Captured,list))device!.Capture(null);
    }
}
