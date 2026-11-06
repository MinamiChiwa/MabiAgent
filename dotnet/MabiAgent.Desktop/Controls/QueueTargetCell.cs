using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;

namespace MabiAgent.Desktop.Controls;

/// <summary>
/// Hosts the plan editor only while the row is actually being edited. A long queue
/// keeps one text block per visible row instead of one input control per row, and
/// the editor is created on demand and dropped again when editing ends.
/// </summary>
public sealed class QueueTargetCell:ContentControl
{
    private JobRow? row;
    private TextBox? editor;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if(row is not null)row.PropertyChanged-=RowChanged;
        row=DataContext as JobRow;
        if(row is not null)row.PropertyChanged+=RowChanged;
        base.OnDataContextChanged(e);
        Sync();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if(row is null&&DataContext is JobRow current){row=current;row.PropertyChanged+=RowChanged;}
        Sync();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if(row is not null)row.PropertyChanged-=RowChanged;
        row=null;RemoveEditor();
        base.OnDetachedFromVisualTree(e);
    }
    private void RowChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is null or nameof(JobRow.IsEditingTarget))Sync();
    }
    private void Sync()
    {
        if(row is null||!row.IsEditingTarget){RemoveEditor();return;}
        if(editor is null)
        {
            var box=new TextBox{VerticalContentAlignment=Avalonia.Layout.VerticalAlignment.Center};
            box.Classes.Add("queue-target");
            box.Bind(TextBox.TextProperty,new Binding(nameof(JobRow.TargetInput)){Source=row,Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
            box.Bind(IsEnabledProperty,new Binding(nameof(JobRow.CanEditTarget)){Source=row});
            box.AddHandler(KeyDownEvent,EditorKeyDown,RoutingStrategies.Bubble);
            box.LostFocus+=EditorLostFocus;
            var suffix=new TextBlock{VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center,Opacity=0.7};
            suffix.Classes.Add("suffix");
            suffix.Bind(TextBlock.TextProperty,new Binding(nameof(JobRow.InputUnit)){Source=row});
            var host=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};
            Grid.SetColumn(suffix,1);
            host.Children.Add(box);host.Children.Add(suffix);
            editor=box;Content=host;
        }
        Dispatcher.UIThread.Post(()=>{if(editor is{ } box){box.Focus();box.SelectAll();}},DispatcherPriority.Loaded);
    }
    private void RemoveEditor()
    {
        // Enter ends the edit before the focused TextBox leaves the tree. Removing the
        // subscription first prevents that tree change from starting a second save.
        if(editor is not null)editor.LostFocus-=EditorLostFocus;
        editor=null;Content=null;
    }
    private void EditorKeyDown(object? sender,KeyEventArgs e)
    {
        if(row is not{ } current)return;
        if(e.Key==Key.Escape){current.ResetTargetDraft();current.EndTargetEdit();e.Handled=true;}
        else if(e.Key==Key.Enter){e.Handled=true;var owner=this.FindAncestorOfType<QueuePanel>();current.EndTargetEdit();owner?.CommitRow(current);}
    }
    private void EditorLostFocus(object? sender,RoutedEventArgs e)
    {
        if(row is not{ } current)return;
        current.EndTargetEdit();
        this.FindAncestorOfType<QueuePanel>()?.CommitRow(current);
    }
}
