using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MabiAgent.Windows;
using System.IO;
namespace MabiAgent.Desktop;

internal static class QueueUiSelfCheck
{
    public static async Task<string[]> Run(string root,string? preview=null)
    {
        var directory=Path.Combine(Path.GetTempPath(),"mabi-queue-check-"+Guid.NewGuid().ToString("N"));
        var workspace=new Workspace(root,directory);var vm=new MainViewModel(workspace,new GameWindowSelection(()=>[]));
        var checks=new List<string>();MainWindow? window=null;
        void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);checks.Add(message);}
        async Task Settle(){await Task.Delay(60);window!.UpdateLayout();}
        async Task WaitIdle(){for(var i=0;i<100;i++){await Task.Delay(20);if(!vm.IsBusy)break;}Check(!vm.IsBusy,"队列修改在限定时间内完成");await Settle();}
        try
        {
            await vm.RefreshAsync();var recipe=vm.Recipes.First();
            workspace.AddProduction(recipe.Id,10);workspace.AddProduction(recipe.Id,20);workspace.AddProduction(recipe.Id,30);
            var fishing=workspace.Read().Gathering!.First(g=>g.Fishing);workspace.AddGathering("idle_fishing",fishing.Id,65);
            await vm.RefreshAsync();window=new MainWindow(vm){Width=1120,Height=800};window.Show();await Settle();
            var panel=window.GetVisualDescendants().OfType<QueuePanel>().Single();
            var grid=panel.GetVisualDescendants().OfType<DataGrid>().Single(g=>g.Name=="ManualGrid");
            Check(grid.SelectionMode==DataGridSelectionMode.Extended,"任务列表启用多选");
            Check(grid.Columns[0].Header?.ToString()==""&&!grid.Columns[0].CanUserSort,"拖动列无顺序标题和编号排序");
            Check(grid.Columns.All(c=>c.SortMemberPath!=nameof(JobRow.Order)),"表格无执行顺序列");
            Check(panel.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="任务会根据材料、加工时间和当前状态自动安排。"),"队列显示自动安排提示");
            var gathering=workspace.Read().Gathering!.First(g=>!g.Fishing);
            workspace.AddGathering("gathering",gathering.Id,7);await vm.RefreshAsync();await Settle();
            Check(vm.Jobs.Single(j=>j.IsGathering).TargetSuffix=="轮"&&vm.Jobs.First(j=>j.IsFishing).TargetSuffix=="","采集计划使用轮数，钓鱼使用时分秒");
            Check(!panel.GetVisualDescendants().Any(v=>v is Control{Name:"DetailsCard"}),"列表无底部编辑卡片");
            grid.SelectedItems.Add(vm.Jobs[0]);grid.SelectedItems.Add(vm.Jobs[1]);
            Check(vm.QueueSelectionCount==2,"表格多选连通批量操作");
            var ids=vm.SelectedJobIds.ToHashSet();vm.BatchTarget="48";await vm.ApplyBatchTargetCommand.ExecuteAsync(null);await Settle();
            Check(workspace.Read().Jobs.Where(j=>ids.Contains(j.Id)).All(j=>j.Target==48)&&vm.SelectedJobIds.SetEquals(ids),"批量编辑落盘并保留多选");
            var row=vm.Jobs.First(j=>!j.IsFishing);
            Check(row.BeginTargetEdit(),"双击计划单元格进入编辑");await Settle();
            var input=panel.GetVisualDescendants().OfType<TextBox>().First(b=>b.DataContext is JobRow j&&j.Id==row.Id);
            input.Focus();input.SetCurrentValue(TextBox.TextProperty,"57");
            workspace.EditQueue(q=>q.Control(vm.Jobs.Last().Id,"pause"));await vm.RefreshLiveQueueAsync();await Settle();
            Check(row.TargetInput=="57"&&vm.SelectedJobIds.SetEquals(ids),"运行刷新保留行内草稿和多选");
            input=panel.GetVisualDescendants().OfType<TextBox>().First(b=>b.DataContext is JobRow j&&j.Id==row.Id);
            input.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.Enter});await WaitIdle();
            Check(workspace.Read().Jobs.Single(j=>j.Id==row.Id).Target==57,"行内回车直接保存数量");
            input.SetCurrentValue(TextBox.TextProperty,"999");input.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.Escape});
            Check(row.TargetInput=="57"&&workspace.Read().Jobs.Single(j=>j.Id==row.Id).Target==57,"Esc 撤回行内草稿");
            // Ending the edit drops the editor instance, so the focus test reopens the cell.
            Check(row.BeginTargetEdit(),"重新进入计划编辑");await Settle();
            input=panel.GetVisualDescendants().OfType<TextBox>().First(b=>b.DataContext is JobRow j&&j.Id==row.Id);
            input.Focus();input.SetCurrentValue(TextBox.TextProperty,"59");grid.Focus();await WaitIdle();
            Check(workspace.Read().Jobs.Single(j=>j.Id==row.Id).Target==59,"行内失焦自动保存");
            var fish=vm.Jobs.First(j=>j.IsFishing);fish.TargetInput="1:02:03";await vm.SaveRowTargetAsync(fish);
            Check(workspace.Read().Jobs.Single(j=>j.Id==fish.Id).Target==3723,"行内时分秒正确保存");
            grid.ContextMenu!.Open(grid);await Settle();
            Check(grid.ContextMenu.Items.OfType<MenuItem>().Any(m=>m.Header?.ToString()?.StartsWith("删除所选")==true&&ReferenceEquals(m.Command,vm.DeleteSelectedCommand)),"右键菜单连接删除操作："+string.Join("/",grid.ContextMenu.Items.OfType<MenuItem>().Select(m=>m.Header)));grid.ContextMenu.Close();
            workspace.EditQueue(q=>q.Begin(row.Id,"queue-ui-pending","device:ui"));await vm.RefreshLiveQueueAsync();await Settle();
            grid.SelectedItems.Clear();grid.SelectedItems.Add(vm.Jobs.Single(j=>j.Id==row.Id));
            Check(vm.SelectedJob!.Pending,"复现待确认生产任务");
            grid.Focus();grid.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.Delete});await WaitIdle();
            Check(workspace.Read().Jobs.All(j=>j.Id!=row.Id),"Delete 键可直接删除待确认生产");
            var auto="auto-material:queue-ui";workspace.EditQueue(q=>q.Add(auto,"gathering",fishing.ItemId,3,fishing.Id,[]));await vm.RefreshLiveQueueAsync();
            vm.SetQueueSelection([vm.Jobs.Single(j=>j.Id==auto)]);await vm.DeleteSelectedCommand.ExecuteAsync(null);
            Check(!workspace.Read().Jobs.Any(j=>j.Id==auto),"自动补充任务可直接删除");
            var pause=vm.Jobs.First(j=>!j.IsFishing);workspace.EditQueue(q=>q.Control(pause.Id,"pause"));await vm.RefreshLiveQueueAsync();
            vm.QueueView.SortDescriptions.Add(Avalonia.Collections.DataGridSortDescription.FromPath(nameof(JobRow.Name)));vm.Search="已暂停";await Settle();
            Check(!vm.QueueView.IsEmpty&&vm.QueueView.Cast<JobRow>().All(j=>j.Status.Contains("已暂停"))&&vm.QueueView.SortDescriptions.Count==1,"进度更新后排序筛选保持一致");
            vm.SetQueueSelection([pause]);vm.SelectedJob=pause;
            workspace.EditQueue(q=>q.Control(pause.Id,"continue"));await vm.RefreshLiveQueueAsync();await Settle();
            Check(vm.SelectedJob is null&&!vm.SelectedJobIds.Contains(pause.Id),"状态刷新隐藏任务后清除单选和多选");
            vm.Search="";vm.ResetSortCommand.Execute(null);await Settle();
            // Real pointer sequence through the in-app reorder: grip of the last row → above the first row.
            var order=vm.QueueView.Cast<JobRow>().ToArray();var dragged=order[^1];
            Control Handle(JobRow job)=>grid.GetVisualDescendants().OfType<Control>().First(c=>c.Classes.Contains("queue-drag-handle")&&ReferenceEquals(c.DataContext,job));
            var mouse=new Pointer(Pointer.GetNextFreeId(),PointerType.Mouse,true);var held=new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.Other);
            var handle=Handle(dragged);var start=handle.TranslatePoint(new Point(handle.Bounds.Width/2,handle.Bounds.Height/2),window)!.Value;
            var firstRow=Handle(order[0]).GetVisualAncestors().OfType<DataGridRow>().First();var end=firstRow.TranslatePoint(new Point(40,3),window)!.Value;
            handle.RaiseEvent(new PointerPressedEventArgs(handle,mouse,window,start,0,new PointerPointProperties(RawInputModifiers.LeftMouseButton,PointerUpdateKind.LeftButtonPressed),KeyModifiers.None,1));
            foreach(var at in new[]{start+new Point(0,-10),end}){handle.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,handle,mouse,window,at,0,held,KeyModifiers.None));await Task.Delay(30);}
            Check(grid.GetVisualDescendants().OfType<DataGridRow>().Any(r=>r.Classes.Contains("drag-source")),"拖动开始后源行淡化显示");
            handle.RaiseEvent(new PointerReleasedEventArgs(handle,mouse,window,end,0,new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.LeftButtonReleased),KeyModifiers.None,Avalonia.Input.MouseButton.Left));await WaitIdle();
            Check(workspace.Read().Jobs.Where(j=>!j.Automatic).OrderBy(j=>j.Order).Select(j=>j.Id).First()==dragged.Id,"指针拖动最后一行到首行之前并保存顺序");
            Check(!grid.GetVisualDescendants().OfType<DataGridRow>().Any(r=>r.Classes.Contains("drag-source")),"放下后清除拖动状态");
            grid.SelectedItems.Clear();grid.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=Key.A,KeyModifiers=KeyModifiers.Control});
            Check(vm.QueueSelectionCount==vm.ManualCount,"Ctrl+A 全选当前列表");
            vm.BatchTarget="bad";var before=workspace.Read().Jobs.Select(j=>j.Target).ToArray();await vm.ApplyBatchTargetCommand.ExecuteAsync(null);
            Check(workspace.Read().Jobs.Select(j=>j.Target).SequenceEqual(before),"不适用的批量输入不产生部分修改");vm.Error=null;
            vm.BatchTarget="";
            foreach(var theme in new[]{Avalonia.Styling.ThemeVariant.Dark,Avalonia.Styling.ThemeVariant.Light})
            foreach(var size in new[]{new Size(1000,700),new Size(1340,920)})
            {
                Application.Current!.RequestedThemeVariant=theme;window.Width=size.Width;window.Height=size.Height;await Settle();
                Check(grid.Bounds.Height>=160&&grid.Bounds.Width>600,$"列表 {theme} {size.Width}×{size.Height} 保持可用布局");
                var batch=panel.GetVisualDescendants().OfType<TextBox>().Single(b=>b.Name=="BatchTargetInput");
                Check(batch.IsEffectivelyVisible&&batch.Bounds.Width>=150,"批量工具条显示完整");
            }
            var screenshot=preview??Path.Combine(root,"build/ui-component-review/queue-current.png");Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            using var bitmap=new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Width,(int)window.Height));bitmap.Render(window);bitmap.Save(screenshot,Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            return checks.ToArray();
        }
        finally{vm.PrepareClose();window?.Close();if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
