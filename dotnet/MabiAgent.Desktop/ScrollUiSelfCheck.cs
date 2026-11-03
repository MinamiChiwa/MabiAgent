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
   var sources=new Dictionary<int,object>();
   foreach(var page in new[]{0,1,2,3,4,5,0,1,2,3,4,5}){
    var watch=System.Diagnostics.Stopwatch.StartNew();vm.SelectedPage=page;window.UpdateLayout();watch.Stop();await Task.Delay(60);
    var panel=window.FindControl<Grid>("PageHost")!.Children.First(c=>c.IsVisible);
    if(page>=4){var summary=window.FindControl<ListBox>("WorkbenchSummaryList")!;if(sources.TryGetValue(page,out var source)){Check(ReferenceEquals(source,summary.ItemsSource),$"页面{page+1} 重访复用列表");Check(summary.Scroll!.Offset.Y>0,$"页面{page+1} 重访保持滚动位置");}sources[page]=summary.ItemsSource!;}
    foreach(var grid in panel.GetVisualDescendants().OfType<DataGrid>()){
     var cell=grid.GetVisualDescendants().OfType<DataGridCell>().FirstOrDefault();if(cell is null)continue;
     var bars=grid.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().Where(b=>b.Maximum>0).ToArray();
     var before=bars.Sum(b=>b.Value);Wheel(cell,window,-1);window.UpdateLayout();
     Check(bars.Sum(b=>b.Value)>before,$"{grid.Name} 行上滚轮移动表格");
     Check(grid.Bounds.Height<=panel.Bounds.Height,$"{grid.Name} 有限视口");
     var last=grid.ItemsSource.Cast<object>().Last();grid.ScrollIntoView(last,null);window.UpdateLayout();await Task.Delay(30);window.UpdateLayout();
     Check(grid.GetVisualDescendants().OfType<DataGridRow>().Any(r=>ReferenceEquals(r.DataContext,last)),$"{grid.Name} 末行可达");
     grid.ScrollIntoView(grid.ItemsSource.Cast<object>().First(),null);window.UpdateLayout();
    }
    foreach(var viewer in panel.GetVisualDescendants().OfType<ScrollViewer>().Where(v=>v.IsEffectivelyVisible&&v.Extent.Height>v.Viewport.Height+1)){
     viewer.Offset=default;window.UpdateLayout();var before=viewer.Offset.Y;Wheel(viewer.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t=>t.IsEffectivelyVisible)??(Control)viewer,window,-1);window.UpdateLayout();Check(viewer.Offset.Y>before,$"页面{page+1} 长内容接受滚轮");
    }
    if(page>=4){var list=window.FindControl<ListBox>("WorkbenchSummaryList")!;Check(list.GetVisualDescendants().OfType<ListBoxItem>().Count()<30,$"页面{page+1} 仅生成可视列表项");Check(list.ItemCount==(page==4?vm.Workbench!.Document.Chains.Count:vm.Workbench!.Document.Anchors.Count),$"页面{page+1} 不截断列表");list.ScrollIntoView(list.ItemsSource!.Cast<object>().Last());window.UpdateLayout();await Task.Delay(30);Check(list.GetRealizedContainers().Any(c=>ReferenceEquals(c.DataContext,list.ItemsSource!.Cast<object>().Last())),$"页面{page+1} 最后一项可达");}
    checks.Add($"页面{page+1} 切换及布局 {watch.Elapsed.TotalMilliseconds:F1}ms");
   }
   foreach(var theme in new[]{Avalonia.Styling.ThemeVariant.Dark,Avalonia.Styling.ThemeVariant.Light})
   foreach(var width in new[]{860d,1340d}){
    Application.Current!.RequestedThemeVariant=theme;window.Width=width;window.UpdateLayout();
    foreach(var page in new[]{0,5,1,4,2})vm.SelectedPage=page;
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
}
