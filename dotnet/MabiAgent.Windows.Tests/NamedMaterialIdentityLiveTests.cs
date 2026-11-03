using System.Diagnostics;
using System.Text.Json.Nodes;
using MabiAgent.Capture;
using OpenCvSharp;
using Xunit;
namespace MabiAgent.Windows.Tests;
public sealed class NamedMaterialIdentityLiveTests
{
    [Fact] public async Task ExplicitNamedDepositWithFullContainerEvidence()
    {
        var name=Environment.GetEnvironmentVariable("MABI_FULL_IDENTITY_NAME");if(name is null)return;
        Assert.Contains(name,new[]{"柔軟原木","堅硬原木","蜂蠟"});var root=RecognitionTests.Root();var withdrawal=name=="蜂蠟";
        var path=Path.Combine(root,"build/named-material-identity",name+"-full.json");
        if(File.Exists(path))Assert.Contains(JsonNode.Parse(File.ReadAllText(path))!["status"]!.GetValue<string>(),new[]{"baseline","no_container_change_confirmed"});
        var metadata=new AccountItemService(Path.Combine(root,"build/current-queue-live-check/accounts"),Path.Combine(root,"config/items.verified.tw.json"));
        var decoder=ClientInventoryDecoder.Load(Path.Combine(root,"config/client-inventory-schema.disk.json"));
        LoginInventory? decoded=null;var decodeGate=new object();
        LoginInventory Decode(byte[] body){var value=decoder.Decode(body);lock(decodeGate)decoded=value;return value;}
        var game=Assert.Single(GameWindowDiscovery.List());Assert.NotEqual(game.State.WindowId,game.State.ForegroundId);
        await using var capture=new CaptureCoordinator(Decode,identityMetadata:metadata.KnownCaptureIdentities());capture.SelectProcess(game.State.ProcessId);_=capture.RunAsync();await Task.Delay(2000);
        using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        var store=new MabiAgent.Workflows.JsonWorkflowStore(path);using var session=new ScreenWorkflowSession(ScreenRoutes.Load(root),game.State,InputTransportFactory.Create(game.State),ocr,new MabiAgent.Workflows.JsonWorkflowStore(path+".intents"));session.Start();
        var report=new JsonObject{["name"]=name,["source"]="backpack",["destination"]="account_storage",["status"]="baseline"};
        async Task Refresh()
        {var script=new ScreenPrivateTheaterRefresh(session);script.Start(DateTimeOffset.UtcNow);var timer=Stopwatch.StartNew();while(script.Status==MabiAgent.Workflows.PrivateTheaterRefreshStatus.Running&&timer.Elapsed.TotalSeconds<180){script.Tick(DateTimeOffset.UtcNow);await Task.Delay(200);}Assert.Equal(MabiAgent.Workflows.PrivateTheaterRefreshStatus.Complete,script.Status);await Task.Delay(3000);Assert.NotNull(capture.Snapshot().Inventory?.VerifiedThrough);}
        try
        {
            await Refresh();LoginInventory before;lock(decodeGate)before=decoded!;
            var plan=new MabiAgent.Workflows.WithdrawalPlan(1,name,1,"account_storage");var port=session.CreateStoragePort(plan,"full-identity",store);var timer=Stopwatch.StartNew();
            while(timer.Elapsed.TotalSeconds<120){var f=port.Observe();if(f.Scope==plan.Scope)break;if(f.Stable)port.Perform(new("navigate",1,name,1,plan.Scope));await Task.Delay(200);}
            Assert.Equal("storage_account",session.CurrentPage);
            var raw=Environment.GetEnvironmentVariable("MABI_FULL_IDENTITY_POINT")!.Split(',').Select(int.Parse).ToArray();session.Guard.ClickAt(new(raw[0],raw[1]));await Task.Delay(1200);
            var image=session.Guard.Capture().Image;var nameBox=withdrawal?new PixelBox(451,311,575,356):new PixelBox(450,269,450+name.Length*29,302);var line=ocr.RecognizeRgb(image.Crop(nameBox));
            Assert.True(new ScreenQuantityReader(ocr).ExactColor(image,nameBox,name,minimum:.75),"name="+line?.Text+"; confidence="+line?.Confidence);
            report["status"]="deposit_dispatch_attempted";store.Flush(report);session.Guard.PressKey(32,TimeSpan.FromMilliseconds(80));await Task.Delay(1500);
            image=session.Guard.Capture().Image;
            if(new ScreenQuantityReader(ocr).Read(image,new(1,"deposit_quantity",new(935,771,987,809),"integer")).Value?.Quantity==1)
            {Assert.Equal(1,new ScreenQuantityReader(ocr).Read(image,new(1,"deposit_quantity",new(935,771,987,809),"integer")).Value?.Quantity);report["status"]="deposit_confirm_dispatch_attempted";store.Flush(report);session.Guard.PressKey(32,TimeSpan.FromMilliseconds(80));await Task.Delay(1500);}
            await Refresh();LoginInventory after;lock(decodeGate)after=decoded!;
            var prior=before.Containers[InventoryContainer.AccountStorage].ToDictionary(r=>r.StackKeyHex);
            var changed=after.Containers[InventoryContainer.AccountStorage].Where(r=>!prior.TryGetValue(r.StackKeyHex,out var old)||old.StackableId!=r.StackableId||old.Quantity!=r.Quantity).ToArray();
            report["before_storage"]=System.Text.Json.JsonSerializer.SerializeToNode(before.Containers[InventoryContainer.AccountStorage].Select(r=>new{r.ItemId,r.StackableId,r.Quantity}));report["after_storage"]=System.Text.Json.JsonSerializer.SerializeToNode(after.Containers[InventoryContainer.AccountStorage].Select(r=>new{r.ItemId,r.StackableId,r.Quantity}));
            if(changed.Length==0)report["status"]="no_container_change_confirmed";
            var item=Assert.Single(changed);if(withdrawal){Assert.Equal(1,prior[item.StackKeyHex].Quantity-item.Quantity);report["source"]="account_storage";report["destination"]="backpack";}else Assert.Equal(1,item.Quantity);Assert.Equal(before.CharacterCode,after.CharacterCode);
            report["wire_id"]=item.ItemId;report["wire_stack"]=item.StackableId;report["before_storage"]=System.Text.Json.JsonSerializer.SerializeToNode(before.Containers[InventoryContainer.AccountStorage].Select(r=>new{r.ItemId,r.StackableId,r.Quantity}));report["after_storage"]=System.Text.Json.JsonSerializer.SerializeToNode(after.Containers[InventoryContainer.AccountStorage].Select(r=>new{r.ItemId,r.StackableId,r.Quantity}));
            var snapshot=capture.Snapshot().Inventory!;metadata.Record(snapshot);metadata.RecordCaptureAssociation(snapshot.Identity!.AccountId,item.ItemId,name,"named_single_deposit_full_container_diff",Path.GetRelativePath(root,path));
            report["status"]="direct_named_container_event_associated";
        }catch(Exception error){report["error"]=error.ToString();throw;}finally{session.Pause();store.Flush(report);}
    }
    [Fact] public void ExplicitReviewedSingleUnitTransferAssociation()
    {
        if(Environment.GetEnvironmentVariable("MABI_REVIEW_WOOL_IDENTITY")!="1")return;
        var root=RecognitionTests.Root();var evidence="build/named-material-identity/厚實羊毛-retrieve.json";
        var report=JsonNode.Parse(File.ReadAllText(Path.Combine(root,evidence)))!.AsObject();
        Assert.Equal("厚實羊毛",report["name"]!.GetValue<string>());Assert.Equal(1,report["quantity"]!.GetValue<int>());
        Assert.Equal("prepare_transfer",report["pending"]!["kind"]!.GetValue<string>());
        Assert.Contains("item=639212127; update_stack=588519427013621760",report["packet_error"]!.GetValue<string>());
        var metadata=new AccountItemService(Path.Combine(root,"build/current-queue-live-check/accounts"),Path.Combine(root,"config/items.verified.tw.json"));
        metadata.RecordCaptureAssociation("24081432",639212127,"厚實羊毛","named_storage_single_unit_transfer",evidence);
        report["wire_id"]=639212127;report["status"]="direct_named_single_unit_transfer_associated";
        report["pending"]=null;report["review_note"]="Single stored unit moved immediately on prepare; current image confirms named unit returned to backpack. Wire instance is explicit in corresponding inventory event. Inventory totals remain invalid until full sync.";
        File.WriteAllText(Path.Combine(root,evidence),report.ToJsonString(new(){WriteIndented=true}));
    }
    [Fact] public async Task ExplicitRetrieveNamedIdentityWithFreshCapture()
    {
        var name=Environment.GetEnvironmentVariable("MABI_RETRIEVE_IDENTITY");if(name is null)return;
        Assert.Contains(name,new[]{"厚實羊毛","柔軟原木","堅硬原木","蜂蠟"});
        var root=RecognitionTests.Root();var directory=Path.Combine(root,"build/named-material-identity");Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,name+"-retrieve.json");
        if(File.Exists(path))Assert.Equal("bootstrap",JsonNode.Parse(File.ReadAllText(path))!["status"]!.GetValue<string>());
        var game=Assert.Single(GameWindowDiscovery.List());Assert.NotEqual(game.State.WindowId,game.State.ForegroundId);
        var metadata=new AccountItemService(Path.Combine(root,"build/current-queue-live-check/accounts"),Path.Combine(root,"config/items.verified.tw.json"));
        var rows=new List<ConsumableStack>();var gate=new object();bool observing=false;
        var decoder=ClientInventoryDecoder.Load(Path.Combine(root,"config/client-inventory-schema.disk.json"));
        await using var capture=new CaptureCoordinator(decoder.Decode,identityMetadata:metadata.KnownCaptureIdentities(),observeFrame:(frame,connection,at)=>
        {if(frame.Opcode!=GameProtocol.InventoryOpcode)return;lock(gate)if(observing&&rows.Count<100)rows.AddRange(ConsumableCodec.ParseUpdates(GameProtocol.DecodeBody(frame.Flag,frame.Body)));});
        capture.SelectProcess(game.State.ProcessId);_=capture.RunAsync();await Task.Delay(2000);Assert.Equal(CaptureState.Listening,capture.Snapshot().State);
        using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        var store=new MabiAgent.Workflows.JsonWorkflowStore(path);var journal=new MabiAgent.Workflows.JsonWorkflowStore(Path.Combine(directory,"intents.json"));
        using var session=new ScreenWorkflowSession(ScreenRoutes.Load(root),game.State,InputTransportFactory.Create(game.State),ocr,journal);session.Start();
        session.SetVerifiedOwnerResolver(()=>capture.Snapshot().Inventory?.Scope is{} s?new(s.SessionId,s.AccountId,s.CharacterId):null);
        var report=new JsonObject{["name"]=name,["quantity"]=1,["status"]="bootstrap"};
        try
        {
            var theater=new ScreenPrivateTheaterRefresh(session);theater.Start(DateTimeOffset.UtcNow);var timer=Stopwatch.StartNew();
            while(theater.Status==MabiAgent.Workflows.PrivateTheaterRefreshStatus.Running&&timer.Elapsed.TotalSeconds<180){theater.Tick(DateTimeOffset.UtcNow);await Task.Delay(200);}
            Assert.Equal(MabiAgent.Workflows.PrivateTheaterRefreshStatus.Complete,theater.Status);timer.Restart();
            while(capture.Snapshot().Inventory?.VerifiedThrough is null&&timer.Elapsed.TotalSeconds<20)await Task.Delay(200);
            report["capture_message"]=capture.Snapshot().Message;report["packet_error"]=capture.Snapshot().Inventory?.Error;Assert.NotNull(capture.Snapshot().Inventory?.VerifiedThrough);
            var plan=new MabiAgent.Workflows.WithdrawalPlan(1,name,1,"account_storage");var port=session.CreateStoragePort(plan,"identity:"+name,store);timer.Restart();bool selected=false,prepared=false,confirmed=false;
            while(timer.Elapsed.TotalSeconds<180)
            {
                var frame=port.Observe();report["last_frame"]=System.Text.Json.JsonSerializer.SerializeToNode(frame);report["packet_error"]=capture.Snapshot().Inventory?.Error;
                if(frame.TransferDialog&&frame.DialogQuantity==1&&frame.ConfirmAvailable)
                {report["pending"]=new JsonObject{["kind"]="confirm_transfer",["name"]=name,["quantity"]=1};report["status"]="confirm_dispatch_attempted";store.Flush(report);SaveImage();lock(gate){rows.Clear();observing=true;}port.Perform(new("confirm_transfer",1,name,1,plan.Scope));confirmed=true;await Task.Delay(6500);break;}
                if(prepared&&frame.Page=="storage_account")
                {lock(gate)confirmed=rows.Count>0;if(confirmed)break;}
                if(frame.TransferAvailable&&!prepared){report["pending"]=new JsonObject{["kind"]="prepare_transfer",["name"]=name,["quantity"]=1};store.Flush(report);lock(gate){rows.Clear();observing=true;}port.Perform(new("prepare_transfer",1,name,1,plan.Scope));prepared=true;}
                else if(frame.SelectAvailable&&!selected){port.Perform(new("select_item",1,name,1,plan.Scope));selected=true;}
                else if(frame.Scope!=plan.Scope&&frame.Stable&&frame.Page!="storage_detail")port.Perform(new("navigate",1,name,1,plan.Scope));
                await Task.Delay(250);
            }
            Assert.True(confirmed,"具名取料未发送");
            ConsumableStack[] received;lock(gate){observing=false;received=rows.ToArray();}
            report["events"]=new JsonArray(received.Select(r=>(JsonNode)new JsonObject{["item_id"]=r.ItemId,["stack_id"]=r.StackableId,["quantity"]=r.Quantity}).ToArray());
            var item=Assert.Single(received.Select(r=>r.ItemId).Distinct());var snapshot=capture.Snapshot().Inventory!;
            if(snapshot.Error is null&&snapshot.VerifiedThrough is not null)metadata.Record(snapshot);
            metadata.RecordCaptureAssociation(snapshot.Identity!.AccountId,item,name,"user_authorized_named_storage_transfer",Path.GetRelativePath(root,path));
            report["wire_id"]=item;report["status"]="direct_event_associated";report["pending"]=null;
        }
        catch(Exception error){report["error"]=error.ToString();throw;}
        finally{session.Pause();lock(gate)report["received_events"]=new JsonArray(rows.Select(r=>(JsonNode)new JsonObject{["item_id"]=r.ItemId,["stack_id"]=r.StackableId,["quantity"]=r.Quantity}).ToArray());store.Flush(report);SaveImage();}
        void SaveImage(){if(session.LastCapture is not{} frame)return;using var rgb=frame.Image.ToMat();using var bgr=new Mat();Cv2.CvtColor(rgb,bgr,ColorConversionCodes.RGB2BGR);Cv2.ImWrite(Path.ChangeExtension(path,"jpg"),bgr,new ImageEncodingParam(ImwriteFlags.JpegQuality,85));}
    }
    [Fact] public async Task ExplicitNamedSingleDepositIdentity()
    {
        var name=Environment.GetEnvironmentVariable("MABI_IDENTITY_NAME");if(name is null)return;
        Assert.Contains(name,new[]{"厚實羊毛","柔軟原木","堅硬原木","蜂蠟"});
        var root=RecognitionTests.Root();var directory=Path.Combine(root,"build/named-material-identity");Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,name+".json");var confirm=Environment.GetEnvironmentVariable("MABI_IDENTITY_CONFIRM")=="1";
        var saved=File.Exists(path)?JsonNode.Parse(File.ReadAllText(path))!.AsObject():null;
        if(confirm)Assert.Equal("awaiting_quantity_review",saved?["status"]?.GetValue<string>());else Assert.Null(saved);
        var game=Assert.Single(GameWindowDiscovery.List());Assert.NotEqual(game.State.WindowId,game.State.ForegroundId);
        var events=new List<JsonObject>();var gate=new object();
        var metadata=new AccountItemService(Path.Combine(root,"data/agent-run/desktop/accounts"));
        var decoder=ClientInventoryDecoder.Load(Path.Combine(root,"config/client-inventory-schema.disk.json"));
        await using var capture=new CaptureCoordinator(decoder.Decode,identityMetadata:metadata.KnownCaptureIdentities(),observeFrame:(frame,connection,at)=>
        {
            if(frame.Opcode!=GameProtocol.InventoryOpcode)return;
            var rows=ConsumableCodec.ParseUpdates(GameProtocol.DecodeBody(frame.Flag,frame.Body));
            lock(gate)foreach(var row in rows)if(events.Count<100)events.Add(new JsonObject{["item_id"]=row.ItemId,["stack_id"]=row.StackableId,["quantity"]=row.Quantity,["connection"]=connection,["at"]=at});
        });
        capture.SelectProcess(game.State.ProcessId);_=capture.RunAsync();var timer=Stopwatch.StartNew();
        while(capture.Snapshot().State!=CaptureState.Listening&&timer.Elapsed.TotalSeconds<12)await Task.Delay(100);Assert.Equal(CaptureState.Listening,capture.Snapshot().State);
        var ops=InputTransportFactory.Create(game.State);using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var guard=new ForegroundInputGuard(game.State,ScreenWorkflowSession.ProcessScope(game.State),ops,_=>throw new InvalidOperationException("固定具名搬料"));guard.Start();
        var report=saved??new JsonObject{["name"]=name,["source"]="backpack",["destination"]="account_storage",["quantity"]=1,["at"]=DateTimeOffset.Now.ToString("O"),["status"]="preparing"};
        try
        {
            if(confirm)
            {
                var current=guard.Capture().Image;var read=new ScreenQuantityReader(ocr);
                Assert.True(read.ExactColor(current,new(148,341,359,371),name,minimum:.75));
                Assert.Equal(1,read.Read(current,new(1,"deposit_quantity",new(935,771,987,809),"integer")).Value?.Quantity);
                SaveImage(current,"confirm");report["status"]="confirm_dispatch_attempted";Flush();
                guard.PressKey(32,TimeSpan.FromMilliseconds(80));await Task.Delay(6500);SaveImage(guard.Capture().Image,"after");
                report["status"]="sent_result_requires_direct_event_review";Flush();return;
            }
            var point=Environment.GetEnvironmentVariable("MABI_IDENTITY_POINT")!.Split(',').Select(int.Parse).ToArray();guard.ClickAt(new(point[0],point[1]));await Task.Delay(1200);
            var image=guard.Capture().Image;SaveImage(image,"named");
            var reader=new ScreenQuantityReader(ocr);Assert.True(reader.ExactColor(image,new(450,269,665,302),name,minimum:.75),"当前具名详情不匹配，未转移");
            report["status"]="prepare_sent";Flush();guard.PressKey(32,TimeSpan.FromMilliseconds(80));await Task.Delay(1600);SaveImage(guard.Capture().Image,"prepared");
            report["status"]="awaiting_quantity_review";Flush();
        }
        finally{guard.Pause("identity_stage_end");lock(gate)report["events"]=new JsonArray(events.Select(e=>(JsonNode)e.DeepClone()).ToArray());Flush();}
        void Flush()=>File.WriteAllText(path,report.ToJsonString(new(){WriteIndented=true}));
        void SaveImage(RgbImage image,string stage){using var rgb=image.ToMat();using var bgr=new Mat();Cv2.CvtColor(rgb,bgr,ColorConversionCodes.RGB2BGR);Cv2.ImWrite(Path.Combine(directory,name+"-"+stage+".jpg"),bgr,new ImageEncodingParam(ImwriteFlags.JpegQuality,85));}
    }
}
