using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCvSharp;
using MabiAgent.Workflows;
namespace MabiAgent.Desktop;

// Temporary acceptance entry: the existing service owns all game inputs and state.
public sealed partial class AutomationService
{
    public async Task RunLiveQueueAcceptanceAsync(string reportPath,bool storageAcceptance=false,bool startPaused=false,DateTimeOffset? until=null)
    {
        var began=DateTimeOffset.Now;var report=new JsonObject{["began"]=began.ToString("O"),["shared_service"]=true,["trace"]=new JsonArray()};
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);string? prior=null,lastSupplyRequest=null;var lastSaved=DateTimeOffset.MinValue;
        var before=selection.Selected?.State??throw new InvalidOperationException("游戏窗口不唯一");
        try
        {
            if(storageAcceptance&&workspace.Read().Jobs.Any())throw new InvalidOperationException("单份取料验收只允许空隔离队列");
            StartCapture();
            var captureDeadline=DateTimeOffset.UtcNow+TimeSpan.FromSeconds(12);
            var listening=await LiveAcceptanceStartup.WaitForAsync(()=>CaptureStatus.State==MabiAgent.Capture.CaptureState.Listening,captureDeadline,milliseconds=>Task.Delay(milliseconds,lifetime.Token),token:lifetime.Token);
            if(!listening)throw new InvalidOperationException("验收库存监听未在12秒内就绪："+CaptureStatus.Message);
            if(startPaused)
            {Status=Status with{Running=false,Message="验收已暂停，请点击继续",PauseReason="manual"};NotifyChanged();}
            else
            {
            // A new capture process has no current identity or quantities. Bootstrap
            // through the same service refresh before resuming qualified storage plans.
            if(CaptureStatus.Inventory?.VerifiedThrough is null)
            {
                var restored=LiveAcceptanceStartup.HasRestoredPendingWork(workspace.DataPath);
                if(!restored)await RefreshInventoryAsync();var bootstrap=DateTimeOffset.Now;
                while((refreshingInventory||recovering)&&DateTimeOffset.Now-bootstrap<TimeSpan.FromMinutes(6))
                {if(File.Exists(Path.ChangeExtension(reportPath,"stop"))){report["stopped"]="requested_during_bootstrap";return;}Save();await Task.Delay(1000,lifetime.Token);}
                if(!restored&&CaptureStatus.Inventory?.VerifiedThrough is null&&Status.PauseReason!="manual")throw new InvalidOperationException("验收启动刷新未取得当前抓包，保留原意图");
            }
            if(Status.PauseReason!="manual")await StartAsync();
            if(storageAcceptance&&IsRunning&&!File.Exists(Path.Combine(workspace.DataPath,"storage-supply.json")))
            {
                await gate.WaitAsync(lifetime.Token);try{BeginQuickReentry(true);}finally{gate.Release();wake.Signal();}
                var bootstrap=DateTimeOffset.Now;
                while(refreshingInventory&&DateTimeOffset.Now-bootstrap<TimeSpan.FromSeconds(120)){Save();await Task.Delay(1000,lifetime.Token);}
                await gate.WaitAsync(lifetime.Token);
                try
                {
                    var captured=ports?.VerifiedCapture()??throw new InvalidOperationException("取料前未取得当前完整抓包");
                    if(captured.Quantity(MabiAgent.Capture.InventoryContainer.CharacterStorage,580126530) is not >=1)throw new InvalidOperationException("角色仓当前高级生皮不足或未知");
                    if(Accounts.ResolveVerified(captured.Identity!.AccountId,"高級生皮")!=580126530)throw new InvalidOperationException("取料材料身份未可信关联");
                    report["before_bag"]=captured.Quantity(MabiAgent.Capture.InventoryContainer.Backpack,580126530);report["before_storage"]=captured.Quantity(MabiAgent.Capture.InventoryContainer.CharacterStorage,580126530);
                    var storageScope="character:"+JsonSerializer.Serialize(new[]{captured.Scope!.AccountId,captured.Scope.CharacterId});
                    var action="storage-live-one-hide";var store=new JsonWorkflowStore(Path.Combine(workspace.DataPath,"storage-supply.json"));
                    var plan=new WithdrawalPlan(4773149089738807,"高級生皮",1,storageScope);
                    var requested=new StorageSupplyRunner([plan],ports!.Storage(plan,action,store),store).Snapshot();
                    requested["runtime_scope"]=MabiAgent.Windows.ScreenWorkflowSession.ProcessScope(before).SessionId+":owner:"+JsonSerializer.Serialize(new[]{captured.Scope.AccountId,captured.Scope.CharacterId});requested["runtime_action"]=action;store.Flush(requested);
                    host!.Pause("acceptance_attach_plan");DestroyHost();
                }
                finally{gate.Release();}
                await StartAsync();
            }
            }
            while(DateTimeOffset.Now-began<TimeSpan.FromHours(24)&& (until is null||DateTimeOffset.Now<until))
            {
                await Task.Delay(1000,lifetime.Token);
                await gate.WaitAsync(lifetime.Token);AutomationStatus state;bool isRunning;
                try{state=Status;isRunning=IsRunning;}finally{gate.Release();}
                var signature=JsonSerializer.Serialize(state)+":"+session?.CurrentPage;
                if(File.Exists(Path.ChangeExtension(reportPath,"stop"))){report["stopped"]="requested";break;}
                if(signature!=prior)
                {
                    var rows=report["trace"]!.AsArray();rows.Add(new JsonObject{["at"]=DateTimeOffset.Now.ToString("O"),["status"]=JsonSerializer.SerializeToNode(state),["page"]=session?.CurrentPage,["stage"]=ExecutionStage});while(rows.Count>100)rows.RemoveAt(0);prior=signature;
                    Save();
                }
                else if(DateTimeOffset.Now-lastSaved>TimeSpan.FromSeconds(5))Save();
                if(!isRunning||state.PauseReason is not null)
                {report["paused"]=state.PauseReason??state.Fault??state.Message;Save();continue;}
                report.Remove("paused");
                var supplyRequestPath=Path.Combine(workspace.DataPath,"live-supply-acceptance-request.json");
                var supplyRequest=File.Exists(supplyRequestPath)?File.ReadAllText(supplyRequestPath):null;
                if(supplyRequest is not null&&supplyRequest!=lastSupplyRequest)
                {
                    var supplied=await RunLiveSupplyAcceptanceHookAsync(lifetime.Token);
                    if(supplied is not null)
                    {
                        lastSupplyRequest=supplyRequest;report["supply_acceptance"]??=new JsonObject();
                        report["supply_acceptance"]![supplied["request_id"]?.GetValue<string>()??"invalid"]=supplied.DeepClone();Save();
                    }
                }
                var queue=workspace.Read();if(queue.Jobs.Any()&&queue.Jobs.All(j=>j.Completed>=j.Target)){report["queue_complete"]=true;break;}
                if(storageAcceptance)
                {
                    await gate.WaitAsync(lifetime.Token);
                    try
                    {
                        var withdrawal=new JsonWorkflowStore(Path.Combine(workspace.DataPath,"storage-supply.json")).Read();report["withdrawal"]=withdrawal?.DeepClone();
                        if(withdrawal?["status"]?.GetValue<string>()=="complete"&&host?.SafeToSwitch==true&&session?.CurrentPage=="scene"&&session.LastObservation?.View.Targets.ContainsKey("storage.room_exit")!=true)
                        {var captured=ports!.VerifiedCapture();report["storage_complete"]=true;report["after_bag"]=captured?.Quantity(MabiAgent.Capture.InventoryContainer.Backpack,580126530);report["after_storage"]=captured?.Quantity(MabiAgent.Capture.InventoryContainer.CharacterStorage,580126530);break;}
                    }
                    finally{gate.Release();}
                }
            }
            if(until is not null&&DateTimeOffset.Now>=until)report["stopped"]="acceptance_deadline";
        }
        catch(OperationCanceledException) when(disposed){report["stopped"]="window_closed";}
        catch(Exception e){report["error"]=e.ToString();throw;}
        finally
        {
            if(!disposed)await PauseAsync();report["ended"]=DateTimeOffset.Now.ToString("O");report["status"]=JsonSerializer.SerializeToNode(Status);
            var after=selection.Selected?.State;report["window"]=after is null?null:WindowReport(after);report["initial_window"]=WindowReport(before);Save();
        }
        JsonNode WindowReport(MabiAgent.Windows.NativeWindowState state)=>JsonSerializer.SerializeToNode(new{hwnd=(long)state.WindowId,foreground=(long)state.ForegroundId,state.Geometry,state.Cursor,state.Minimized})!;
        void Save()
        {
            lastSaved=DateTimeOffset.Now;
            var snapshot=CaptureStatus;report["capture"]=JsonSerializer.SerializeToNode(new{snapshot.State,snapshot.Message,snapshot.Metrics,inventory_error=snapshot.Inventory?.Error,full_sync_revision=snapshot.Inventory?.FullSyncRevision});
            if(ports is not null)
            {
                var current=ports.Inventory();var items=new RuntimeQueue(workspace.DataPath).Read().Jobs.Select(j=>j.ItemId).Distinct().Order();
                report["inventory"]=new JsonArray(items.Select(id=>JsonSerializer.SerializeToNode(current.Query(id))).ToArray());
            }
            report["heartbeat"]=DateTimeOffset.Now.ToString("O");File.WriteAllText(reportPath,report.ToJsonString(new(){WriteIndented=true}));
            if(session?.LastCapture is{} capture)
            {
                using var rgb=new Mat(capture.Image.Height,capture.Image.Width,MatType.CV_8UC3);var pixels=capture.Image.CopyPixels();System.Runtime.InteropServices.Marshal.Copy(pixels,0,rgb.Data,pixels.Length);using var bgr=new Mat();Cv2.CvtColor(rgb,bgr,ColorConversionCodes.RGB2BGR);Cv2.ImWrite(Path.ChangeExtension(reportPath,"jpg"),bgr,new ImageEncodingParam(ImwriteFlags.JpegQuality,70));
                if(session.CurrentPage is{} page&&page.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_'))
                    Cv2.ImWrite(Path.Combine(Path.GetDirectoryName(reportPath)!,"page-"+page+".jpg"),bgr,new ImageEncodingParam(ImwriteFlags.JpegQuality,85));
                // Keep one native task-label sample per acceptance job for missing source calibration.
                if(Status.ActivePhase=="confirm_start"&&Status.ActiveJob is{} job&&session.Matcher.Anchor(capture.Image,"gathering_observer.task_title") is not null)
                {
                    var key=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(job)))[..16];
                    var sample=Path.Combine(Path.GetDirectoryName(reportPath)!,"task-"+key+".jpg");
                    if(!File.Exists(sample))Cv2.ImWrite(sample,bgr,new ImageEncodingParam(ImwriteFlags.JpegQuality,85));
                }
            }
        }
    }
}
