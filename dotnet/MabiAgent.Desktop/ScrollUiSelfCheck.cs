using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MabiAgent.Windows;
namespace MabiAgent.Desktop;
internal static class ScrollUiSelfCheck
{
 public static async Task<string[]> Run(string root)
 {
  var dir=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"mabi-scroll-"+Guid.NewGuid().ToString("N"));
  var workspace=new Workspace(root,dir);var vm=new MainViewModel(workspace,new GameWindowSelection(()=>[]));
  MainWindow? window=null;var checks=new List<string>();
  try{
   await vm.RefreshAsync();var recipe=vm.Recipes.First();for(var i=0;i<80;i++)workspace.AddProduction(recipe.Id,10);await vm.RefreshAsync();
   vm.InitializeEditorSelfCheck(new Editors.EditorRepository(root,dir),new MabiAgent.Capture.AccountItemService(System.IO.Path.Combine(dir,"accounts")));
   for(uint i=1;i<=300;i++)vm.AccountItems!.Items.Add(new Accounts.AccountItemEditRow(new MabiAgent.Capture.AccountItemRow(i,$"测试道具{i}",MabiAgent.Capture.NameAssociation.UserConfirmed,true,new Dictionary<string,MabiAgent.Capture.CharacterItemQuantity>(),0,0,0),[]));
   window=new MainWindow(vm){Width=1120,Height=800};window.Show();await Task.Delay(100);
   void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);checks.Add(label);}
   foreach(var page in new[]{0,1,2,0,1,2}){
    var watch=System.Diagnostics.Stopwatch.StartNew();vm.SelectedPage=page;window.UpdateLayout();watch.Stop();await Task.Delay(60);window.UpdateLayout();
    if(page==1){var accounts=window.FindControl<Accounts.AccountItemsPanel>("AccountsPanel")!;for(var wait=0;wait<250&&!accounts.IsGridReady;wait++){window.UpdateLayout();await Task.Delay(20);}Check(accounts.IsGridReady,"仓库分批列与行数据完成接入");}
    if(page==2){window.FindControl<SettingsPanel>("SettingsPage")!.FindControl<Controls.DeferredSettingsSection>("VipSettingsSection")!.EnsureBuilt();window.UpdateLayout();}
    var panel=window.FindControl<Grid>("PageHost")!.Children.First(c=>c.IsVisible);
    foreach(var grid in panel.GetVisualDescendants().OfType<DataGrid>()){
     var cell=grid.GetVisualDescendants().OfType<DataGridCell>().FirstOrDefault();
     var rows=grid.ItemsSource?.Cast<object>().ToArray()??[];
     if(cell is null||rows.Length==0)continue;
     var bars=grid.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().Where(b=>b.Maximum>0).ToArray();
     var before=bars.Sum(b=>b.Value);Wheel(cell,window,-1);window.UpdateLayout();
     Check(bars.Sum(b=>b.Value)>before,$"{grid.Name} 行上滚轮移动表格");
     Check(grid.Bounds.Height<=panel.Bounds.Height+1,$"{grid.Name} 使用页面提供的有限视口");
     var selected=rows[Math.Min(2,rows.Length-1)];grid.SelectedItem=selected;window.UpdateLayout();
     if(page==1&&grid.Name=="ItemsGrid"&&selected is Accounts.AccountItemEditRow selectedRow){var selectedId=selectedRow.ItemId;vm.AccountItems!.ItemsView.Refresh();window.UpdateLayout();Check(grid.SelectedItems.OfType<Accounts.AccountItemEditRow>().Any(r=>r.ItemId==selectedId),"仓库筛选刷新保持当前选中行");}
     var last=rows.Last();grid.ScrollIntoView(last,null);window.UpdateLayout();await Task.Delay(30);window.UpdateLayout();
     Check(grid.GetVisualDescendants().OfType<DataGridRow>().Any(r=>ReferenceEquals(r.DataContext,last)),$"{grid.Name} 末行可达");
     Check(grid.GetVisualDescendants().OfType<DataGridRow>().Count()<80,$"{grid.Name} 只实现有限行容器");
     grid.ScrollIntoView(rows.First(),null);window.UpdateLayout();
    }
    foreach(var viewer in panel.GetVisualDescendants().OfType<ScrollViewer>().Where(v=>v.IsEffectivelyVisible&&v.Extent.Height>v.Viewport.Height+1&&!v.GetVisualAncestors().OfType<DataGrid>().Any())){
     viewer.Offset=default;window.UpdateLayout();var before=viewer.Offset.Y;var source=WheelSource(viewer,window);Check(source is not null,$"页面{page+1} 长内容存在视口内滚轮目标");Wheel(source!,window,-1);window.UpdateLayout();Check(viewer.Offset.Y>before,$"页面{page+1} 长内容接受滚轮");
    }
    checks.Add($"页面{page+1} 切换及布局 {watch.Elapsed.TotalMilliseconds:F1}ms");
   }
   foreach(var theme in new[]{Avalonia.Styling.ThemeVariant.Dark,Avalonia.Styling.ThemeVariant.Light})
   foreach(var width in new[]{860d,1340d}){
    Application.Current!.RequestedThemeVariant=theme;window.Width=width;window.UpdateLayout();
    foreach(var page in new[]{0,2,1,0,2})vm.SelectedPage=page;
    await Task.Delay(260);window.UpdateLayout();
    var navigation=window.FindControl<ListBox>("Navigation")!;var track=window.FindControl<Grid>("TabTrack")!;var indicator=window.FindControl<Border>("TabIndicator")!;var item=(ListBoxItem)navigation.SelectedItem!;var origin=item.TranslatePoint(default,track)!.Value;
    Check(indicator.RenderTransform is Avalonia.Media.TranslateTransform slide&&Math.Abs(slide.X-origin.X)<1&&Math.Abs(indicator.Width-item.Bounds.Width)<1,$"{theme} {width} 快速切页滑块落到当前页签");
    Check(!indicator.IsHitTestVisible&&indicator.Bounds.Width>0,$"{theme} {width} 滑块不阻挡页签输入");
   }
   return checks.ToArray();
  }finally{vm.PrepareClose();window?.Close();if(System.IO.Directory.Exists(dir))System.IO.Directory.Delete(dir,true);}
 }
 internal static void Wheel(Control target,Window window,double delta)
 {var mouse=new Pointer(Pointer.GetNextFreeId(),PointerType.Mouse,true);var at=target.TranslatePoint(new Point(10,10),window)??default;target.RaiseEvent(new PointerWheelEventArgs(target,mouse,window,at,0,new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.Other),KeyModifiers.None,new Vector(0,delta)));}
 private static TextBlock? WheelSource(ScrollViewer viewer,Window window)
 {
  var origin=viewer.TranslatePoint(default,window);if(origin is null)return null;var viewport=new Rect(origin.Value,viewer.Viewport);
  return viewer.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text=>
  {
   if(!text.IsEffectivelyVisible||!text.IsHitTestVisible||text.Bounds.Width<=0||text.Bounds.Height<=0)return false;
   if(text.GetVisualAncestors().TakeWhile(ancestor=>!ReferenceEquals(ancestor,viewer)).OfType<ScrollViewer>().Any())return false;
   if(text.GetVisualAncestors().OfType<Avalonia.Controls.Primitives.ScrollBar>().Any())return false;
   var point=text.TranslatePoint(new Point(Math.Min(10,text.Bounds.Width/2),Math.Min(10,text.Bounds.Height/2)),window);
   return point is { } value&&viewport.Contains(value);
  });
 }
}
