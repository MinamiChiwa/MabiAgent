using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
namespace MabiAgent.Desktop;
public partial class App:Application
{
    private IHost? host;
    private bool closing;
    private IClassicDesktopStyleApplicationLifetime desktop=null!;
    public override void Initialize()=>AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted(){desktop=(IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;desktop.ShutdownMode=ShutdownMode.OnExplicitShutdown;base.OnFrameworkInitializationCompleted();Dispatcher.UIThread.Post(()=>Start(desktop.Args??[]));}
    private void Shutdown(int code=0)=>desktop.Shutdown(code);
    private async void Start(string[] arguments)
    {
        var e=new {Args=arguments};
        try
        {
            var builder=Host.CreateApplicationBuilder(e.Args);
            builder.Services.AddSingleton(new Workspace(FindRoot(e.Args),Argument(e.Args,"--state-dir")));
            builder.Services.AddSingleton(new MabiAgent.Windows.GameWindowSelection(MabiAgent.Windows.GameWindowDiscovery.List));
            builder.Services.AddSingleton<MainViewModel>();builder.Services.AddSingleton<MainWindow>();
            builder.Services.AddSingleton<AutomationService>();
            host=builder.Build();await host.StartAsync();
            if(e.Args.Contains("--live-queue-acceptance")||e.Args.Contains("--live-storage-acceptance"))
            {
                host.Services.GetRequiredService<MabiAgent.Windows.GameWindowSelection>().Refresh();
                var service=host.Services.GetRequiredService<AutomationService>();
                var acceptanceModel=host.Services.GetRequiredService<MainViewModel>();acceptanceModel.AttachAutomation(service);
                await acceptanceModel.RefreshAsync();await acceptanceModel.RefreshWindowsAsync();
                var acceptanceWindow=host.Services.GetRequiredService<MainWindow>();desktop.MainWindow=acceptanceWindow;
                acceptanceWindow.CloseRequest=()=>CloseWindowAsync(acceptanceWindow,e.Args);
                acceptanceWindow.Closed+=(_,_)=>Shutdown();acceptanceWindow.Show();
                try{await service.RunLiveQueueAcceptanceAsync(Argument(e.Args,"--report")??throw new ArgumentException("缺少报告路径"),e.Args.Contains("--live-storage-acceptance"),e.Args.Contains("--acceptance-start-paused"));}
                catch(Exception){Shutdown(1);return;} // The acceptance report already contains the failure; do not leave a modal dialog holding capture and input resources.
                finally{acceptanceModel.PrepareClose();await StopHostAsync();}
                Shutdown();return;
            }
            if(e.Args.Contains("--upgrade-chains"))
            {
                var service=host.Services.GetRequiredService<AutomationService>();
                var upgrades=service.Repository.UpgradePublishedChains();
                var issues=Editors.PublishedCatalogCompiler.CompileAvailable(service.Repository)["custom_chain_issues"];
                var output=Argument(e.Args,"--report")??throw new ArgumentException("缺少报告路径");
                File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(new{upgrades,issues,game_input=false}));
                await service.DisposeAsync();Shutdown();return;
            }
            if(e.Args.Contains("--observe-shop"))
            {
                host.Services.GetRequiredService<MabiAgent.Windows.GameWindowSelection>().Refresh();
                var service=host.Services.GetRequiredService<AutomationService>();
                var observation=await service.ObserveShopAsync();
                var reportPath=Argument(e.Args,"--report")??throw new ArgumentException("缺少报告路径");
                File.WriteAllText(reportPath,System.Text.Json.JsonSerializer.Serialize(new{observation,game_input=false}));
                await service.DisposeAsync();Shutdown();return;
            }
            if(e.Args.Contains("--self-check"))
            {
                var vm=host.Services.GetRequiredService<MainViewModel>();await vm.RefreshAsync();
                var window=host.Services.GetRequiredService<MainWindow>();
                var workspace=host.Services.GetRequiredService<Workspace>();
                vm.InitializeEditorSelfCheck(new Editors.EditorRepository(workspace.Root,workspace.DataPath),new MabiAgent.Capture.AccountItemService(Path.Combine(workspace.DataPath,"accounts")));
                if(e.Args.Contains("--editor-draft"))vm.ChainEditor!.NewDraftCommand.Execute(null);
                var uiChecks=e.Args.Contains("--memory-check")?await MemoryUiSelfCheck.Run(FindRoot(e.Args)):e.Args.Contains("--scroll-check")?await ScrollUiSelfCheck.Run(FindRoot(e.Args)):e.Args.Contains("--mini-check")?await CompactUiSelfCheck.Run(window,vm,Environment.GetEnvironmentVariable("MABI_MINI_PREVIEW")):e.Args.Contains("--help-check")?await PresentationSelfCheck.RunHelp(window,vm):e.Args.Contains("--queue-check")?await QueueUiSelfCheck.Run(FindRoot(e.Args),Argument(e.Args,"--queue-preview")):e.Args.Contains("--chrome-check")?await UiChromeRegression.Run(FindRoot(e.Args)):e.Args.Contains("--interaction-check")?await UiSelfCheck.Run(FindRoot(e.Args)):[];
                if(e.Args.Contains("--native-check"))
                {
                    var service=host.Services.GetRequiredService<AutomationService>();
                    var engine=service.GetOcr();
                    _=engine.Recognize(120,32,new byte[120*32]);
                    var routes=MabiAgent.Windows.ScreenRoutes.FromConfiguration(FindRoot(e.Args),Editors.PublishedCatalogCompiler.CompileAvailable(service.Repository),service.Repository.UserAssetRoot);
                    using var matcher=new MabiAgent.Windows.TemplateMatcher(routes.Profile);
                    using var runtimeLimit=new MabiAgent.Windows.RuntimeLimitRecognition(FindRoot(e.Args));
                    var pixels=new byte[1280*960*3];
                    _=matcher.Anchor(new MabiAgent.Windows.RgbImage(1280,960,pixels),routes.Profile.Templates.Keys.First());
                    uiChecks=uiChecks.Concat(new[]{"原生 OCR 推理及 OpenCV 加载"}).ToArray();
                    await service.DisposeAsync();
                }
                var page=Array.IndexOf(e.Args,"--page");
                if(page>=0&&page+1<e.Args.Length)vm.SelectedPage=int.Parse(e.Args[page+1]);
                if(e.Args.Contains("--compact")){window.Width=1000;window.Height=700;}
                if(e.Args.Contains("--light"))RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Light;
                if(e.Args.Contains("--dark"))RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark;
                if(e.Args.Contains("--expanded"))foreach(var expander in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Expander>())expander.IsExpanded=true;
                if(e.Args.Contains("--selection"))vm.SelectedJob=vm.Jobs.FirstOrDefault();
                if(e.Args.Contains("--dialog"))vm.AddDialogOpen=true;
                if(e.Args.Contains("--capture-drawer"))vm.OpenCaptureSettingsCommand.Execute(null);
                var suite=Argument(e.Args,"--preview-suite");
                if(suite is not null)
                {
                    Directory.CreateDirectory(suite);window.Show();
                    if(e.Args.Contains("--expanded"))foreach(var expander in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Expander>().ToArray())expander.IsExpanded=true;
                    foreach(var theme in new[]{Avalonia.Styling.ThemeVariant.Dark,Avalonia.Styling.ThemeVariant.Light})
                    foreach(var compact in new[]{false,true})
                    foreach(var selectedPage in Enumerable.Range(0,6))
                    {
                        RequestedThemeVariant=theme;window.Width=compact?1000:1340;window.Height=compact?700:920;vm.SelectedPage=selectedPage;await Task.Delay(260);window.UpdateLayout();
                        using var capture=new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Width,(int)window.Height));capture.Render(window);capture.Save(Path.Combine(suite,$"{theme}-{(compact?"compact":"normal")}-{selectedPage}.png"),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    }
                    window.Close();
                }
                var preview=Argument(e.Args,"--preview");
                if(preview is not null)
                {
                    window.Show();await Task.Delay(250);window.UpdateLayout();
                    using var bitmap=new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Width,(int)window.Height));bitmap.Render(window);bitmap.Save(preview,Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);window.Close();
                }
                var report=System.Text.Json.JsonSerializer.Serialize(new {ok=vm.Error is null,recipes=vm.Recipes.Count,targets=vm.Jobs.Count,window_constructed=true,ui_checks=uiChecks,game_input=false,migration_complete=false});
                var i=Array.IndexOf(e.Args,"--report");
                if(i>=0&&i+1<e.Args.Length)File.WriteAllText(e.Args[i+1],report);
                Shutdown(vm.Error is null?0:1);return;
            }
            var model=host.Services.GetRequiredService<MainViewModel>();
            if(!e.Args.Contains("--ui-preview")&&!e.Args.Contains("--close-check")&&!await StartupNotice.ConfirmAsync(host.Services.GetRequiredService<Workspace>().DataPath)){await StopHostAsync();Shutdown();return;}
            if(e.Args.Contains("--ui-preview"))
            {
                var workspace=host.Services.GetRequiredService<Workspace>();
                model.InitializeEditorSelfCheck(new Editors.EditorRepository(workspace.Root,workspace.DataPath),new MabiAgent.Capture.AccountItemService(Path.Combine(workspace.DataPath,"accounts")));
                var pageIndex=Array.IndexOf(e.Args,"--page");if(pageIndex>=0&&pageIndex+1<e.Args.Length)model.SelectedPage=int.Parse(e.Args[pageIndex+1]);
            }
            else
            {
                var automation=host.Services.GetRequiredService<AutomationService>();model.AttachAutomation(automation);automation.StartCapture();
            }
            await model.RefreshAsync();if(!e.Args.Contains("--ui-preview"))await model.RefreshWindowsAsync();
            var main=host.Services.GetRequiredService<MainWindow>();desktop.MainWindow=main;
            if(e.Args.Contains("--compact")){main.Width=1000;main.Height=700;}
            if(e.Args.Contains("--light"))RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Light;
            if(e.Args.Contains("--dark"))RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark;
            main.CloseRequest=()=>CloseWindowAsync(main,e.Args);
            main.Closed+=(_,_)=>Shutdown();
            main.Show();
            if(e.Args.Contains("--mini"))model.IsMiniMode=true;
            if(e.Args.Contains("--close-check"))Dispatcher.UIThread.Post(main.Close);
        }
        catch(Exception error)
        {
            var report=Argument(e.Args,"--report");
            if(e.Args.Contains("--self-check")&&report is not null)File.WriteAllText(report,System.Text.Json.JsonSerializer.Serialize(new{ok=false,error=error.ToString()}));
            else await Ursa.Controls.MessageBox.ShowAsync(error.Message,"启动失败");
            Shutdown(1);
        }
    }
    private static string? Argument(string[] args,string key)
    {var i=Array.IndexOf(args,key);if(i<0)return null;if(i+1>=args.Length||args[i+1].StartsWith("--"))throw new ArgumentException(key+" 缺少目录");return Path.GetFullPath(args[i+1]);}
    private static string FindRoot(string[] args)
    {
        var i=Array.IndexOf(args,"--root");if(i>=0&&i+1<args.Length)return Path.GetFullPath(args[i+1]);
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d is not null;d=d.Parent)
            if(File.Exists(Path.Combine(d.FullName,"config/screen_catalog.1280x960.tw.json")))return d.FullName;
        throw new DirectoryNotFoundException("未找到项目配置，请使用 --root 指定项目目录");
    }
    private async Task StopHostAsync(Action<string>? progress=null)
    {
        var current=host;if(current is null)return;
        progress?.Invoke("resolve_automation");
        var automation=current.Services.GetRequiredService<AutomationService>();
        progress?.Invoke("dispose_automation");
        await automation.DisposeAsync();
        progress?.Invoke("stop_host_services");
        await current.StopAsync(TimeSpan.FromSeconds(3));
        progress?.Invoke("dispose_host");
        if(current is IAsyncDisposable disposable)await disposable.DisposeAsync();else current.Dispose();
        host=null;
    }
    private async Task<bool> CloseWindowAsync(MainWindow window,string[] args)
    {
        if(closing)return false;closing=true;
        void Stage(string stage)
        {var path=Argument(args,"--report");if(args.Contains("--close-check")&&path is not null){var vm=(MainViewModel)window.DataContext!;File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new{ok=false,stage,chain_dirty=vm.ChainEditor?.HasUnsavedChanges,anchor_dirty=vm.AnchorEditor?.HasUnsavedChanges,account_dirty=vm.AccountItems?.HasDirtyEdits,settings_dirty=vm.SettingsDirty,settings_error=vm.SettingsError,vm.RepairMinutes,vm.GoldReserve,vm.BackpackThreshold,vm.MaterialReserve,vm.WingReserve,vm.ThemeKey}));}}
        try
        {
            Stage("confirm");
            if(!await window.ConfirmCloseAsync()){closing=false;return false;}
            window.IsEnabled=false;((MainViewModel)window.DataContext!).PrepareClose();
            Stage("stop_host");
            await StopHostAsync(Stage);
            var report=Argument(args,"--report");
            if(args.Contains("--close-check")&&report is not null)File.WriteAllText(report,System.Text.Json.JsonSerializer.Serialize(new{ok=true,closed=true,game_input=false}));
            return true;
        }
        catch(Exception error)
        {
            closing=false;window.IsEnabled=true;
            var report=Argument(args,"--report");
            if(args.Contains("--close-check")&&report is not null){File.WriteAllText(report,System.Text.Json.JsonSerializer.Serialize(new{ok=false,error=error.ToString()}));Shutdown(1);}
            else await Ursa.Controls.MessageBox.ShowAsync(window,"关闭未完成："+error.Message,"关闭工具");
            return false;
        }
    }
}
