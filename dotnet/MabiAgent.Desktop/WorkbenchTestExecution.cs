using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MabiAgent.Core;
using MabiAgent.Desktop.Editors;
using MabiAgent.Windows;
using MabiAgent.Workflows;
using MabiAgent.Capture;

namespace MabiAgent.Desktop;

/// <summary>Explicit workbench tests share the normal input guard and durable consumption journal.</summary>
public static class WorkbenchTestExecution
{
    public static Task RunAsync(EditorRepository repository,GameWindowSelection selection,Func<bool> isRunning,Func<IOcrEngine> getOcr,
        WorkbenchDocument document,WorkbenchChain chain,IReadOnlyCollection<string>? selected,bool recognizeOnly,
        IProgress<WorkbenchRunRow> progress,CancellationToken cancellation,Func<CaptureStatus> capture,AccountItemService accounts)
        =>Task.Run(async()=>
        {
            cancellation.ThrowIfCancellationRequested();if(isRunning())throw new InvalidOperationException("请先暂停运行中的任务再测试。");
            if(!recognizeOnly&&File.Exists(Path.Combine(repository.StateDirectory,"stop-reset.json")))throw new InvalidOperationException("上次停止重置未完成，请先重试停止全部");
            WorkbenchValidation.Validate(document);WorkbenchCompiler.ValidateAssets(repository,document);
            var cards=chain.Cards.Where(c=>c.Enabled&&(selected is null||selected.Contains(c.Id))).ToArray();
            if(cards.Length==0)throw new InvalidOperationException("请选择至少一个启用步骤。");
            var draft=new WorkbenchDocument(document.ToJson());foreach(var row in draft.Chains)row.Enabled=row.Id==chain.Id;
            var selectedChain=draft.Chains.FirstOrDefault(c=>c.Id==chain.Id)??throw new InvalidOperationException("任务链已删除。");
            var configuration=PublishedCatalogCompiler.CompileAvailable(repository,includeWorkbench:false);WorkbenchCompiler.Apply(repository,draft,configuration,allowStandaloneTests:true);
            var definition=configuration["workbench_chains"]?[selectedChain.RuntimeKey]?.DeepClone()?.AsObject()??throw new InvalidDataException("任务链未编译。");
            // The outer flow owns card order; native ports must not recursively start a second card flow.
            configuration.Remove("workbench_chains");var routes=ScreenRoutes.FromConfiguration(repository.Root,configuration,repository.UserAssetRoot);
            foreach(var card in cards)Report(progress,card.Name,"配置通过","步骤、引用与图片已校验；尚未执行游戏操作。");
            var game=selection.Selected;
            if(game is null)
            {
                if(!recognizeOnly)throw new InvalidOperationException("请先选择游戏窗口。");
                foreach(var card in cards)Report(progress,card.Name,"当前页待验证","未选择游戏窗口。");return;
            }
            var settingsPath=Path.Combine(repository.StateDirectory,"settings.json");
            InputOptions ReadInput()=>InputOptions.FromJson(File.Exists(settingsPath)?JsonNode.Parse(File.ReadAllBytes(settingsPath))!.AsObject():new());
            var inputOptions=ReadInput();var diagnostics=new RuntimeDiagnostics(repository.StateDirectory);var ops=InputTransportFactory.Create(game.State,inputOptions,diagnostics);var current=ops.ReadWindow(game.State.WindowId);
            if(ops is TouchInputOps touch)touch.DiagnosticWriteFailed+=failure=>Report(progress,"触控诊断","写入失败",$"{failure.GestureId}/{failure.Stage}；{failure.Error}");
            EnsureIdentity(game.State,current);
            using var session=new ScreenWorkflowSession(routes,current,ops,getOcr(),new JsonWorkflowStore(Path.Combine(repository.StateDirectory,"native-input-intents.json")),
                recognizeOnly?null:()=>NativeWindowActivation.Activate(current),diagnostics:diagnostics,inputOptions:inputOptions);
            session.LifeVipEnabled=()=>File.Exists(settingsPath)&&DesktopPreferences.FromJson(JsonNode.Parse(File.ReadAllBytes(settingsPath))!.AsObject()).LifeVip;
            session.ConfigureRuntimeLimitRecognition(repository.Root);
            using var stopInput=cancellation.Register(()=>session.Guard.RequestPause());
            var runtimeCatalog=new RuntimeCatalog(configuration);
            var packetPorts=new DesktopPorts(session,capture,current,new CapturePlanningAdapter(accounts,runtimeCatalog.Names),runtimeCatalog,()=>File.Exists(settingsPath)?JsonNode.Parse(File.ReadAllBytes(settingsPath))!.AsObject():new());
            var inputStamp=File.GetLastWriteTimeUtc(settingsPath).Ticks;
            using var settingsMonitor=new Timer(_=>
            {
                try{var stamp=File.GetLastWriteTimeUtc(settingsPath).Ticks;if(Interlocked.Exchange(ref inputStamp,stamp)!=stamp&&ReadInput()!=inputOptions)session.Guard.RequestPause();}
                catch{session.Guard.RequestPause();}
            },null,100,100);
            if(recognizeOnly)
            {
                session.Start();
                try
                {
                UiObservation? observation=null;
                try{cancellation.ThrowIfCancellationRequested();observation=session.Observe();}
                catch(InputBlockedException error){Report(progress,chain.Name,"当前页待验证",error.Message);}
                var evaluator=new ScreenWorkbenchFlow(session,definition,selected);
                foreach(var card in cards)
                {
                    var compiled=(definition["cards"] as JsonArray??[]).OfType<JsonObject>().Single(c=>S(c,"id")==card.Id);
                    var page=S(compiled,"page_id");var matched=observation is {Stable:true,IdentityVerified:true}&&(page.Length==0||observation.View.Page==page)&&evaluator.EvaluateCondition(compiled,observation);
                    var target=S(compiled,"target");if(card.Action is "click" or "right"&&target.Length>0)matched&=observation?.View.Targets.ContainsKey(target)==true;
                    Report(progress,card.Name,matched?"当前页识别通过":"当前页待验证",matched?"当前画面已核实；未点击，未验证执行结果。":"当前画面尚未满足此步骤；配置校验通过不代表实机流程通过。");
                }
                return;
                }
                finally{session.Pause("manual");}
            }
            if(chain.Data["source_path"] is JsonArray importedPath)
            {
                await TestImportedNavigationAsync(repository,session,ops,current,isRunning,chain,cards,definition,importedPath,selected,progress,cancellation).ConfigureAwait(false);
                return;
            }
            var catalog=new RuntimeCatalog(configuration);var storage=PrepareStorage(repository,chain,cards);
            ProductionRunner? production=null;GatheringRunner? gathering=null;ScreenGatheringPort? gatheringPort=null;IdleFishingRunner? fishing=null;
            session.Start();var flow=new ScreenWorkbenchFlow(session,definition,selected);var total=Stopwatch.StartNew();string? last=null;string? activeCardId=null;double cardEnteredAt=0;
            var maximumSeconds=Math.Min(7200,Math.Max(30,cards.Sum(c=>c.TimeoutSeconds*(c.RetryLimit+1))+30));
            try
            {
                while(true)
                {
                    cancellation.ThrowIfCancellationRequested();if(isRunning())throw new InputBlockedException("主任务已开始，调试测试已停止。");
                    var actual=ops.ReadWindow(current.WindowId);EnsureIdentity(current,actual);
                    if(session.Guard.RequiresForeground&&actual.ForegroundId!=current.WindowId)throw new InputBlockedException("游戏失焦，测试已停止；请显式重新测试。");
                    if(session.Guard.Paused)throw new InputBlockedException(session.Guard.PauseReason??"输入已暂停。");
                    if(total.Elapsed.TotalSeconds>maximumSeconds)throw new TimeoutException("本次流程测试超过总时限。");
                    var result=flow.Tick();var card=flow.CurrentCard;var signature=result.CardId+":"+result.State+":"+result.Message;
                    if(activeCardId!=result.CardId){activeCardId=result.CardId;cardEnteredAt=total.Elapsed.TotalSeconds;}
                    if(signature!=last){Report(progress,card is null?chain.Name:S(card,"name",S(card,"id")),result.State==WorkbenchFlowState.Completed?"执行完成":"执行中",result.Message+" · 页面 "+session.CurrentPage);last=signature;}
                    if(result.State==WorkbenchFlowState.Failed)throw new InputBlockedException(result.Message);
                    if(result.State==WorkbenchFlowState.Completed){Report(progress,chain.Name,"本次路径完成","本次执行路径已确认；跳过或未执行的步骤保留为配置通过。");return;}
                    if(result.State==WorkbenchFlowState.AwaitingSemantic&&card is not null)
                    {
                        var role=S(card,"role");JsonObject state;
                        if(chain.Kind=="production")
                        {
                            if(chain.RuntimeKey!="iron_ingot_from_iron_ore")throw new InputBlockedException("生产实机测试仅使用铁矿石加工铁锭；其他配方可测试识别与导航。");
                            if(!catalog.Production.TryGetValue(chain.RuntimeKey,out var recipe))throw new InvalidDataException("生产任务未配置。");
                            if(production is null)
                            {
                                var store=new JsonWorkflowStore(Path.Combine(storage.Directory,"production.json"));
                                if(role=="result"&&store.Read() is null)throw new InputBlockedException("当前测试没有已确认委托的槽位；请先测试加工步骤。");
                                if(store.Read() is null)
                                {
                                    var inputs=configuration["recipes"]?[recipe.Key]?["materials"] as JsonArray??[];
                                    foreach(var input in inputs)
                                    {
                                        var item=input!["item_id"]!.GetValue<long>();if(!catalog.Names.TryGetValue(item,out var name))throw new InputBlockedException("生产材料名称尚未核实。");
                                    }
                                }
                                if(total.Elapsed.TotalSeconds-cardEnteredAt>(card["timeout_seconds"]?.GetValue<int>()??15))throw new TimeoutException("生产步骤超过时限，尚未提交生产。");
                                production=new(session.CreateProductionPort(recipe.Key,storage.RunId,store),store,recipe,count:1,availableRounds:(key,limit)=>
                                {
                                    var stock=packetPorts.Inventory();var rounds=(long)limit;
                                    foreach(var (item,quantity) in catalog.Recipes[key].Inputs)
                                    {
                                        var observed=stock.Query(item);
                                        if(!observed.Confirmed||observed.Source!="packet_capture"||observed.Quantity is null)return 0;
                                        rounds=Math.Min(rounds,observed.Quantity.Value/quantity);
                                    }
                                    return (int)Math.Max(0,rounds);
                                });
                                production.Resume(explicitRequest:true);
                            }
                            state=production.Tick();CheckRunner(state);
                            if(role=="production"&&I(state,"submitted")>=1&&state["pending"] is null)flow.CompleteSemantic(true);
                            else if(role=="result"&&S(state,"status")=="complete")flow.CompleteSemantic(true);
                            else if(role is not("production" or "result"))throw new InputBlockedException("生产任务动作类型无效："+role);
                        }
                        else if(chain.Kind=="gathering")
                        {
                            if(!catalog.Gathering.TryGetValue(chain.RuntimeKey,out var route))throw new InvalidDataException("采集任务未配置。");
                            if(configuration["gathering_routes"]?[route.Key]?["point_only"]?.GetValue<bool>()==true)
                            {
                                if(fishing is null)
                                {
                                    var store=new JsonWorkflowStore(Path.Combine(storage.Directory,"fishing.json"));
                                    if(role is "observation" or "result"&&store.Read() is null)throw new InputBlockedException("当前测试没有已确认投竿的钓鱼轮次；请先测试开始步骤。");
                                    // A one-second target finishes at the existing runner's next native round boundary.
                                    fishing=new(new FishingPlan(route.Key,1),session.CreateFishingPort(route.Key,storage.RunId,store),store);
                                    fishing.Resume(explicitRequest:true);
                                }
                                state=fishing.Tick();CheckRunner(state);
                                if(role=="gathering_start"&&I(state,"submitted")>=1&&state["pending"] is null)flow.CompleteSemantic(true);
                                else if(role is "observation" or "result"&&S(state,"status")=="complete"&&I(state,"completed")>=1)flow.CompleteSemantic(true);
                                else if(role is not("gathering_start" or "observation" or "result"))throw new InputBlockedException("钓鱼任务动作类型无效："+role);
                                await Task.Delay(150,cancellation).ConfigureAwait(false);continue;
                            }
                            if(gathering is null)
                            {
                                var store=new JsonWorkflowStore(Path.Combine(storage.Directory,"gathering.json"));
                                if(role=="observation"&&store.Read() is null)throw new InputBlockedException("当前测试没有已确认启动的原生采集批次；请先测试采集开始步骤。");
                                if(total.Elapsed.TotalSeconds-cardEnteredAt>(card["timeout_seconds"]?.GetValue<int>()??15))throw new TimeoutException("采集步骤超过时限，尚未开始采集。");
                                gatheringPort=session.CreateGatheringPort(route.Key,storage.RunId,store);
                                gathering=new(gatheringPort,store,route,storage.RunId);
                                gathering.Resume(explicitRequest:true);
                            }
                            state=gathering.Tick();CheckRunner(state);
                            if(role=="gathering_start"&&I(state,"submitted")>=1&&state["pending"] is null&&gatheringPort!.ObserveOnly(route,storage.RunId).TaskId is not null)flow.CompleteSemantic(true);
                            else if(role is "observation" or "result"&&S(state,"status")=="complete")flow.CompleteSemantic(true);
                            else if(role is not("gathering_start" or "observation" or "result"))throw new InputBlockedException("采集任务动作类型无效："+role);
                        }
                        else throw new InputBlockedException("该步骤没有可核验的原任务动作；请选择识别或导航步骤测试。");
                    }
                    await Task.Delay(150,cancellation).ConfigureAwait(false);
                }
            }
            catch(Exception error)
            {
                repository.ReportFault(error,"锚点流程测试 · 卡片 "+(flow.Progress.CardId??"无")+" · 页面 "+session.CurrentPage,chain.Id);Report(progress,chain.Name,error is OperationCanceledException?"已停止":"未完成",error.Message+" · 卡片 "+(flow.Progress.CardId??"无")+" · 页面 "+session.CurrentPage+"；已提交或结果不明的动作保留在测试记录中。");throw;
            }
            finally
            {
                production?.Pause("manual");gathering?.Pause("manual");fishing?.Pause("manual");session.Pause("manual");
            }
        },cancellation);

    static async Task TestImportedNavigationAsync(EditorRepository repository,ScreenWorkflowSession session,INativeWindowsOps ops,NativeWindowState window,Func<bool> isRunning,
        WorkbenchChain chain,WorkbenchCard[] cards,JsonObject definition,JsonArray path,IReadOnlyCollection<string>? selected,IProgress<WorkbenchRunRow> progress,CancellationToken cancellation)
    {
        var route=EditorRepository.GetPath(session.Routes.Configuration,path)?.DeepClone()?.AsObject()??throw new InvalidDataException("原导航配置不存在，请重新载入工作台。");
        if(ContainsPlaceholder(route))throw new InputBlockedException("共享导航模板尚未指定商店目标，请选择维修或杂货店的具体导航链再测试。");
        var compiled=(definition["cards"] as JsonArray??[]).OfType<JsonObject>().Where(c=>cards.Any(row=>row.Id==S(c,"id"))).ToArray();
        if(compiled.Any(c=>c["consumes_resources"]?.GetValue<bool>()==true||S(c,"action") is not("click" or "right" or "scroll" or "zoom_max" or "drag")))
            throw new InputBlockedException("导入导航链包含任务或消费动作，请使用对应原任务流程测试。");
        var port=session.CreateMaintenancePort("workbench-navigation:"+chain.Id,new JsonWorkflowStore(Path.Combine(repository.StateDirectory,"anchor-debug/navigation-test.json")));
        session.Start();
        try
        {
            if(chain.Data["staged_route"]?.GetValue<bool>()==true&&selected is null)
            {
                await RunRoute(route,null,Math.Min(7200,Math.Max(30,cards.Sum(c=>c.TimeoutSeconds*(c.RetryLimit+1))+30))).ConfigureAwait(false);
                Report(progress,chain.Name,"本次导航路径完成","原导航阶段已确认；只执行当前可用分支，其他候选与搜索卡片仍为配置通过。");
            }
            else
            {
                foreach(var card in compiled)
                {
                    port.ResetNavigation();var step=card["source"]?.DeepClone()?.AsObject()??new JsonObject();step["target"]=S(card,"target");step["kind"]=S(card,"action","click");
                    var expected=S(card,"expected_page_id");if(expected.Length>0)step["wait_for_pages"]=new JsonArray(expected);
                    // A selected node never follows the source stage's next pointer.
                    step.Remove("next");
                    await RunRoute(step,card,card["timeout_seconds"]?.GetValue<int>()??15).ConfigureAwait(false);
                    var search=step["search_only"]?.GetValue<bool>()==true;
                    Report(progress,S(card,"name",S(card,"id")),search?"搜索步骤完成":"步骤结果已确认",search?"本次有界搜索已执行，尚未据此判定到达目的地。":"仅此选中节点完成；未自动运行前后节点。");
                }
            }
        }
        catch(Exception error)
        {
            repository.ReportFault(error,"导入导航测试 · "+chain.Name+" · 页面 "+session.CurrentPage,chain.Id);
            Report(progress,chain.Name,error is OperationCanceledException?"已停止":"未完成",error.Message+" · 页面 "+session.CurrentPage);throw;
        }
        finally{port.ResetNavigation();session.Pause("manual");}

        async Task RunRoute(JsonObject navigation,JsonObject? card,double seconds)
        {
            var timer=Stopwatch.StartNew();NavigationTestTrace? previous=null;
            while(true)
            {
                cancellation.ThrowIfCancellationRequested();if(isRunning())throw new InputBlockedException("主任务已开始，导航测试已停止。");
                var actual=ops.ReadWindow(window.WindowId);EnsureIdentity(window,actual);
                if(session.Guard.RequiresForeground&&actual.ForegroundId!=window.WindowId||session.Guard.Paused)throw new InputBlockedException("游戏失焦或已暂停，请显式重新测试。");
                if(timer.Elapsed.TotalSeconds>seconds)throw new TimeoutException("导航测试超时："+(card is null?chain.Name:S(card,"name")));
                if(card is not null&&port.LastTestNavigationTrace is null&&S(card,"page_id") is {Length:>0} requiredPage)
                {
                    var observation=session.Observe();
                    if(!observation.Stable||observation.View.Page!=requiredPage)
                    {await Task.Delay(150,cancellation).ConfigureAwait(false);continue;}
                }
                var result=card is null?port.TestNavigationRoute(navigation):port.TestNavigationStep(navigation);
                var trace=port.LastTestNavigationTrace;
                if(trace is not null&&trace!=previous)
                {
                    var phase=trace.Phase switch{"maximum_observed"=>"最大缩放已确认","search_step_complete"=>"搜索步骤完成","confirmed"=>"画面变化已确认","zoom_max"=>"继续放大确认","dispatch"=>"已操作，等待确认",_=>trace.Phase};
                    Report(progress,card is null?trace.Target:S(card,"name"),phase,"目标 "+trace.Target+" · 页面 "+session.CurrentPage+(trace.Phase=="search_step_complete"?"；此结果不代表已到达目的地。":""));previous=trace;
                }
                if(result.Error is not null)throw new InputBlockedException(result.Error);
                if(result.Completed)return;
                await Task.Delay(150,cancellation).ConfigureAwait(false);
            }
        }
    }
    static bool ContainsPlaceholder(JsonNode? node)=>node switch
    {JsonObject obj=>obj.Any(p=>ContainsPlaceholder(p.Value)),JsonArray array=>array.Any(ContainsPlaceholder),JsonValue value=>value.TryGetValue<string>(out var text)&&text is "$shop" or "$npc" or "$travel",_=>false};

    static void CheckRunner(JsonObject state)
    {
        var status=S(state,"status");if(status is "unknown" or "paused")throw new InputBlockedException("原任务"+(status=="unknown"?"结果待核实":"已暂停")+"："+S(state,"reason",S(state,"pause_reason")));
    }
    static (string Directory,string RunId) PrepareStorage(EditorRepository repository,WorkbenchChain chain,WorkbenchCard[] cards)
    {
        var testRoot=EditorRepository.SafePath(repository.StateDirectory,"anchor-debug/test-runs");
        if(cards.Any(c=>c.Action=="semantic"&&c.Role is "production" or "gathering_start")&&System.IO.Directory.Exists(testRoot))
        {
            foreach(var folder in System.IO.Directory.EnumerateDirectories(testRoot))foreach(var filename in new[]{"production.json","gathering.json","fishing.json"})
            {
                var path=EditorRepository.SafePath(testRoot,Path.GetFileName(folder)+"/"+filename);if(!File.Exists(path))continue;
                var previous=JsonNode.Parse(File.ReadAllText(path))?.AsObject()??throw new InvalidDataException("已有测试记录损坏，请保留并核实。");
                if(previous["pending"] is not null||S(previous,"status")=="unknown")throw new InputBlockedException("已有测试存在待核实动作，请先核对；识别和非消费导航仍可单独测试。");
            }
        }
        var key=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(chain.Id)))[..24];var directory=EditorRepository.SafePath(repository.StateDirectory,"anchor-debug/test-runs/"+key);
        Directory.CreateDirectory(directory);var metadata=Path.Combine(directory,"run.json");
        foreach(var filename in new[]{"production.json","gathering.json","fishing.json"})
        {
            var path=Path.Combine(directory,filename);if(!File.Exists(path))continue;var saved=JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if((saved["pending"] is not null||S(saved,"status")=="unknown")&&cards.Any(c=>c.Action=="semantic"))throw new InputBlockedException("上次测试存在待核实动作，请先核对；禁止新建测试重放。");
            if(S(saved,"status")=="complete")
            {File.Move(path,Path.Combine(directory,"previous-"+filename),true);continue;}
            if((I(saved,"submitted")>0||saved["owned_slots"] is JsonArray {Count:>0})&&cards.Any(c=>c.Action=="semantic"&&c.Role is "production" or "gathering_start"))throw new InputBlockedException("上次测试任务仍在运行，请选择结果步骤继续核实。");
        }
        var run=File.Exists(metadata)?JsonNode.Parse(File.ReadAllText(metadata))!.AsObject():new JsonObject();
        if(!new[]{"production.json","gathering.json","fishing.json"}.Any(filename=>File.Exists(Path.Combine(directory,filename))))run=new JsonObject{["run_id"]="workbench-test:"+Guid.NewGuid().ToString("N"),["chain_id"]=chain.Id,["started_at"]=DateTimeOffset.UtcNow.ToString("O")};
        var id=S(run,"run_id");if(id.Length==0)throw new InvalidDataException("测试记录缺少运行标识，请保留记录并检查。");AtomicJson.Write(metadata,run);return(directory,id);
    }
    static void EnsureIdentity(NativeWindowState expected,NativeWindowState actual)
    {if(!GameWindowSelection.IsGame(actual)||actual.WindowId!=expected.WindowId||actual.ProcessId!=expected.ProcessId||actual.ProcessStartUtcTicks!=expected.ProcessStartUtcTicks||!string.Equals(actual.Executable,expected.Executable,StringComparison.OrdinalIgnoreCase))throw new InputBlockedException("游戏窗口身份已变化。");}
    static void Report(IProgress<WorkbenchRunRow> progress,string card,string result,string details)=>progress.Report(new(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),card,result,details));
    static string S(JsonNode node,string key,string fallback="")=>node[key]?.GetValue<string>()??fallback;
    static int I(JsonNode node,string key)=>node[key]?.GetValue<int>()??0;
}
