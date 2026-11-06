using System.Text.Json.Nodes;
using MabiAgent.Core;
using OpenCvSharp;
using Xunit;
namespace MabiAgent.Windows.Tests;
public sealed class PurchaseCalibrationTests
{
    [Fact] public void CoveredShopLetteringStillLocatesOnlyTheGeneralStore()
    {
        var root=RecognitionTests.Root();using var bgr=Cv2.ImRead(Path.Combine(root,"tests/fixtures/screen_ui/shop-map-label-covered-current.webp"));
        using var rgb=new Mat();Cv2.CvtColor(bgr,rgb,ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var frame=new RgbImage(rgb.Width,rgb.Height,pixels);var routes=ScreenRoutes.Load(root);
        using var matcher=new TemplateMatcher(routes.Profile);using var recognizer=new ScreenPageRecognizer(routes.Profile,matcher);
        var view=recognizer.Recognize(frame);Assert.Equal("region_map",view.Page);
        Assert.True(new PixelBox(445,407,520,438).Contains(view.Targets["map.general_store"]));
        // The same scene also contains 食品店. Removing only the intended word
        // must not let that unrelated shop or the green overlay become a match.
        for(var y=407;y<438;y++)Array.Clear(pixels,(y*rgb.Width+445)*3,75*3);
        Assert.Null(matcher.Anchor(new(rgb.Width,rgb.Height,pixels),"purchase.map_store_label"));
    }
    [Theory][InlineData(12)][InlineData(24)]
    public void PurchaseLabelCanShiftWithTheQuotedPrice(int offset)
    {
        var image=Load("shop-bottle.jpg");var pixels=image.CopyPixels();var source=image.CopyPixels();
        var box=new PixelBox(984,891,1027,916);
        for(var y=box.Top;y<box.Bottom;y++)
        {
            for(var x=box.Left;x<box.Right;x++)Array.Copy(source,(y*1280+box.Left-1)*3,pixels,(y*1280+x)*3,3);
            Array.Copy(source,(y*1280+box.Left)*3,pixels,(y*1280+box.Left+offset)*3,box.Width*3);
        }
        using var recognizer=new ScreenPageRecognizer(ScreenRoutes.Load(RecognitionTests.Root()).Profile);
        var page=recognizer.Recognize(new RgbImage(1280,960,pixels));Assert.Equal("shop",page.Page);
        Assert.InRange(page.Targets["purchase.confirm"].X,box.Left+offset,box.Right+offset);
    }
    [Fact] public void PaperSelectsItsOwnTileAndBeeswaxCannotStartPurchase()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var ops=new ReplayOps(Load("shop-materials.jpg"));
        using var session=new ScreenWorkflowSession(routes,ops.State,ops,new EmptyOcr(),new MemoryStore());session.Start();
        var paper=session.CreatePurchasePort(2,"紙","paper-test",()=>null,()=>new());paper.NavigateToShop();
        Assert.True(new PixelBox(840,235,950,410).Contains(Assert.Single(ops.Clicks)));
        var wax=session.CreatePurchasePort(3,"蜂蠟","wax-test",()=>null,()=>new());Assert.Throws<InputBlockedException>(wax.NavigateToShop);Assert.Single(ops.Clicks);
    }
    [Fact] public void CurrentMerchantMenuRequiresNameAndCannotBecomeRepairMenu()
    {
        using var source=Cv2.ImRead(Path.Combine(RecognitionTests.Root(),"tests/fixtures/screen_ui/shop-menu-current.png"));using var rgb=new Mat();Cv2.CvtColor(source,rgb,ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var routes=ScreenRoutes.Load(RecognitionTests.Root());using var recognizer=new ScreenPageRecognizer(routes.Profile);
        var menu=recognizer.Recognize(new RgbImage(1280,960,pixels));Assert.Equal("shop_dialog",menu.Page);Assert.Contains("purchase.open",menu.Targets.Keys);Assert.DoesNotContain("repair.open",menu.Targets.Keys);
        for(var y=70;y<101;y++)Array.Clear(pixels,(y*1280+500)*3,84*3);
        var unrelated=recognizer.Recognize(new RgbImage(1280,960,pixels));Assert.NotEqual("repair_dialog",unrelated.Page);Assert.NotEqual("shop_dialog",unrelated.Page);
        var expected=routes.Configuration["maintenance"]!["navigation"]!["purchase_shop"]!["shop_travel"]!["wait_for_pages"]!.AsArray().Select(n=>n!.GetValue<string>());
        Assert.Contains("npc_dialogue",expected);Assert.Contains("shop_dialog",expected);
    }
    [Fact] public void CurrentSingleDigitNineMeetsTheUnchangedQuantityConfidence()
    {
        var root=RecognitionTests.Root();using var bgr=Cv2.ImRead(Path.Combine(root,"tests/fixtures/screen_ui/shop-quantity-nine-native.png"));using var rgb=new Mat();Cv2.CvtColor(bgr,rgb,ColorConversionCodes.BGR2RGB);var bytes=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,bytes,0,bytes.Length);var image=new RgbImage(rgb.Width,rgb.Height,bytes);
        using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));var reader=new ScreenQuantityReader(ocr);var spec=ScreenRoutes.Load(root).Configuration["purchase"]!["quantity"]!;var region=NavigationProfile.ReadBox(spec["region"]!);
        Assert.Equal(.8,spec["min_confidence"]!.GetValue<double>());
        var reading=reader.Read(image,new(1,"quantity",new(region.Left-920,region.Top-787,region.Right-920,region.Bottom-787),MinimumConfidence:.8,Thresholds:[120,160]));
        Assert.Equal(9,reading.Value?.Quantity);Assert.All(reading.Readings,r=>Assert.True(r.Confidence>=.8));
    }
    [Fact] public void StepperWaitsForExactAcknowledgementAndNeverRepeatsUnknownClick()
    {var s=new ScreenQuantityStepper();Assert.Equal(1,s.Next(1,3,0));s.Issued(1,1,0);Assert.Equal(0,s.Next(1,3,1));Assert.Equal(0,s.Next(null,3,2));Assert.Equal(1,s.Next(2,3,3));s.Issued(2,1,3);Assert.Throws<InputBlockedException>(()=>s.Next(2,3,9));}
    [Fact] public void StepperDecrementsAndStopsAtRequestedValue()
    {var s=new ScreenQuantityStepper();Assert.Equal(-1,s.Next(2,1,0));s.Issued(2,-1,0);Assert.Equal(0,s.Next(1,1,1));Assert.Equal(0,s.Next(null,1,2));}
    [Theory][InlineData(20)][InlineData(30)] public void StepperSupportsMergedMultiRoundDemand(int target)
    {
        var s=new ScreenQuantityStepper();
        for(var quantity=1;quantity<target;quantity++){Assert.Equal(1,s.Next(quantity,target,quantity));s.Issued(quantity,1,quantity);}
        Assert.Equal(0,s.Next(target,target,target));Assert.Throws<InputBlockedException>(()=>s.Next(target,1000,target+1));
        var region=ScreenRoutes.Load(RecognitionTests.Root()).Configuration["purchase"]!["quantity"]!["region"]!.AsArray();
        Assert.True(region[2]!.GetValue<int>()-region[0]!.GetValue<int>()>=70);
    }
    private static RgbImage Load(string file)
    {using var bgr=Cv2.ImRead(Path.Combine(RecognitionTests.Root(),"assets/templates/purchase",file));using var rgb=new Mat();Cv2.CvtColor(bgr,rgb,ColorConversionCodes.BGR2RGB);var bytes=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,bytes,0,bytes.Length);return new(rgb.Width,rgb.Height,bytes);}
    [Theory]
    [InlineData("shop-map.jpg","shop_travel")]
    [InlineData("shop-map-unselected.jpg","region_map")]
    [InlineData("shop-dialog.jpg","shop_dialog")]
    [InlineData("shop-intro.jpg","npc_dialogue")]
    [InlineData("shop-farewell.jpg","npc_dialogue")]
    [InlineData("shop-exited.jpg","scene")]
    [InlineData("shop-materials.jpg","shop_materials")]
    [InlineData("shop-default.jpg","shop_catalog")]
    [InlineData("shop-bottle.jpg","shop")]
    [InlineData("shop-bottle-two.jpg","shop")]
    public void RecordedShopScreensRecognizeOnlyTheirCalibratedPage(string file,string page)
    {var routes=ScreenRoutes.Load(RecognitionTests.Root());using var r=new ScreenPageRecognizer(routes.Profile);var image=Load(file);Assert.Equal(page,r.Recognize(image).Page);}
    [Theory][InlineData("shop-intro.jpg")][InlineData("shop-farewell.jpg")]
    public void ChangingActorLinesPreservesDialogueShellWithoutGrantingConsumption(string file)
    {
        var original=Load(file);var pixels=original.CopyPixels();
        for(var y=108;y<161;y++)for(var x=520;x<745;x++)
        {
            var at=(y*1280+x)*3;var shade=(byte)(y%24<13&&x%18<8?160:255);
            pixels[at]=pixels[at+1]=pixels[at+2]=shade;
        }
        using var recognizer=new ScreenPageRecognizer(ScreenRoutes.Load(RecognitionTests.Root()).Profile);
        var page=recognizer.Recognize(new RgbImage(1280,960,pixels));Assert.Equal("npc_dialogue",page.Page);
        Assert.Equal(new[]{"dialog.advance"},page.Targets.Keys);Assert.Empty(page.Hits);
        var target=page.TargetRegions!["dialog.advance"];Assert.True(target.Contains(page.Targets["dialog.advance"]));
        Assert.False(page.TargetRegions.ContainsKey("dialog.content"));
    }
    [Fact] public void WhitePanelWithGreenLabelCannotImpersonateDialogueWithoutItsTail()
    {
        var pixels=new byte[1280*960*3];
        for(var y=82;y<190;y++)for(var x=430;x<850;x++){var at=(y*1280+x)*3;pixels[at]=pixels[at+1]=pixels[at+2]=255;}
        for(var y=70;y<98;y++)for(var x=460;x<540;x++){var at=(y*1280+x)*3;pixels[at]=170;pixels[at+1]=215;pixels[at+2]=0;}
        Assert.Null(NpcDialogueRecognition.Recognize(new(1280,960,pixels)));
    }
    [Fact] public void MutableDialogueContentDoesNotDrivePurchaseProgress()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var ops=new ReplayOps(Load("shop-intro.jpg"));
        using var session=new ScreenWorkflowSession(routes,ops.State,ops,new EmptyOcr(),new MemoryStore());session.Start();
        var port=session.CreatePurchasePort(1,"空瓶","dialogue-lines",()=>null,()=>new());
        for(var i=0;i<5&&ops.Clicks.Count==0;i++)port.NavigateToShop();Assert.Single(ops.Clicks);
        port.NavigateToShop();Assert.Single(ops.Clicks);
        var pixels=ops.Image.CopyPixels();
        for(var y=108;y<161;y++)for(var x=520;x<745;x++){var at=(y*1280+x)*3;var shade=(byte)(y%24<13&&x%18<8?160:255);pixels[at]=pixels[at+1]=pixels[at+2]=shade;}
        ops.Image=new(1280,960,pixels);
        for(var i=0;i<5&&ops.Clicks.Count<2;i++)port.NavigateToShop();Assert.Single(ops.Clicks);
        port.NavigateToShop();Assert.Single(ops.Clicks);Assert.Equal(0,ops.Keys);
    }
    [Fact] public void RuntimeDoesNotLoadActorLinesAsTemplates()
    {
        var profile=ScreenRoutes.Load(RecognitionTests.Root()).Profile;
        var retired=new[]{"repair_speech.png","repair_exit.png","repair_result_text.png","storage_speech_body.png","storage_farewell_text.png","storage_farewell_continue.png","storage_farewell_body_stable.png","storage_chest_hint.png","storage_chest_farewell_text.png","intro_text.png","farewell_text.png","attendance_greeting_text.png"};
        Assert.DoesNotContain(profile.Templates.Values,t=>retired.Contains(Path.GetFileName(t.Path)));
    }
    private sealed class EmptyOcr:IOcrEngine{public OcrLine Recognize(int width,int height,byte[] pixels)=>new("",0);}
    [Theory][InlineData("shop-bottle.jpg",1,50)][InlineData("shop-bottle-two.jpg",2,100)]
    public void RecordedPaymentScreenReadsCurrentNameQuantityPriceHoldingAndGold(string file,long quantity,long price)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var c=routes.Configuration["purchase"]!;using var ocr=new OnnxTextRecognizer(Path.Combine(RecognitionTests.Root(),"models/"+OnnxTextRecognizer.DefaultModelFileName));var reader=new ScreenQuantityReader(ocr);var image=Load(file);
        Assert.True(reader.Exact(image,NavigationProfile.ReadBox(c["item_name_region"]!),"空瓶"));
        foreach(var (key,expected)in new[]{("quantity",quantity),("total_price",price),("bottles",140L),("gold",1410756L)})
        {var spec=c[key]!;var got=reader.Read(image,new(1,key,NavigationProfile.ReadBox(spec["region"]!),Format:spec["format"]!.GetValue<string>(),Thresholds:spec["thresholds"]!.AsArray().Select(v=>v!.GetValue<int>()).ToArray(),MinimumConfidence:.80));Assert.True(got.Value?.Quantity==expected,key+": "+System.Text.Json.JsonSerializer.Serialize(got));}
    }
    [Fact] public void MaterialsPageCanReadSameItemHoldingForPostPurchaseReconciliation()
    {var c=ScreenRoutes.Load(RecognitionTests.Root()).Configuration["purchase"]!;using var ocr=new OnnxTextRecognizer(Path.Combine(RecognitionTests.Root(),"models/"+OnnxTextRecognizer.DefaultModelFileName));var reader=new ScreenQuantityReader(ocr);var image=Load("shop-materials.jpg");Assert.True(reader.Exact(image,NavigationProfile.ReadBox(c["item_name_regions"]!["shop_materials"]!),"空瓶"));var spec=c["bottles_by_page"]!["shop_materials"]!;var result=reader.Read(image,new(1,"holding",NavigationProfile.ReadBox(spec["region"]!),Thresholds:[90,120],MinimumConfidence:.80));Assert.Equal(140,result.Value?.Quantity);}
    [Fact] public void RealPortReplaysNavigationAndOneQuantityStepThroughGuardWithoutPaymentOrTyping()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());using var ocr=new OnnxTextRecognizer(Path.Combine(RecognitionTests.Root(),"models/"+OnnxTextRecognizer.DefaultModelFileName));var ops=new ReplayOps(Load("shop-map-unselected.jpg"));using var session=new ScreenWorkflowSession(routes,ops.State,ops,ocr,new MemoryStore());var packet=new InventorySnapshot("character");packet.Set(new(1,140,"character","packet_capture","v1",true));var port=session.CreatePurchasePort(1,"空瓶","replay",()=>null,()=>new(),packetInventory:()=>packet);session.Start();
        // Observe through the purchase port: navigation owns an independent
        // recognition context and cannot supply its stable frames to quantity reads.
        void Stable(string file){ops.Image=Load(file);port.Observe();port.Observe();}
        foreach(var file in new[]{"shop-map.jpg","shop-dialog.jpg","shop-default.jpg","shop-materials.jpg"})
        {
            Stable(file);port.Observe();var before=ops.Clicks.Count;var deadline=System.Diagnostics.Stopwatch.StartNew();
            // Mirror scheduler ticks: a recognized next page may still be inside the configured settle interval.
            do {port.NavigateToShop();if(ops.Clicks.Count>before)break;Thread.Sleep(25);}while(deadline.Elapsed<TimeSpan.FromSeconds(2));
            Assert.True(ops.Clicks.Count==before+1,$"{file}: expected one navigation click, got {ops.Clicks.Count-before}");
            port.NavigateToShop();Assert.Equal(before+1,ops.Clicks.Count);
        }
        Stable("shop-bottle.jpg");port.Observe();var one=port.Observe();Assert.Equal(1,one.Quantity);Assert.Equal(50,one.TotalPrice);Assert.Equal(140,one.Bottles);Assert.True(one.DirectPurchase);Assert.True(one.ButtonVerified);
        port.PrepareQuantity(2);Assert.Equal(5,ops.Clicks.Count);port.PrepareQuantity(2);Assert.Equal(5,ops.Clicks.Count);
        Stable("shop-bottle-two.jpg");port.Observe();port.Observe();port.PrepareQuantity(2);Assert.Equal(5,ops.Clicks.Count);
        Assert.Throws<InputBlockedException>(port.ConfirmPurchase);Assert.Equal(5,ops.Clicks.Count);Assert.Equal(0,ops.Keys);
        var disabled=ops.Image.CopyPixels();for(var y=873;y<931;y++)for(var x=712;x<1220;x++){var i=(y*1280+x)*3;if(disabled[i+1]>120&&disabled[i+1]-disabled[i]>65){disabled[i]=disabled[i+1]=disabled[i+2]=70;}}ops.Image=new(1280,960,disabled);port.Observe();Assert.False(port.Observe().ButtonVerified);
    }
    [Fact] public void SelectedItemObservationCompletesEntryBeforeThePaidExit()
    {
        var root=RecognitionTests.Root();using var ocr=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));var ops=new ReplayOps(Load("shop-materials.jpg"));using var session=new ScreenWorkflowSession(ScreenRoutes.Load(root),ops.State,ops,ocr,new MemoryStore());var port=session.CreatePurchasePort(1,"空瓶","entry-then-exit",()=>null,()=>new());session.Start();
        port.Observe();port.Observe();port.NavigateToShop();Assert.Single(ops.Clicks);
        ops.Image=Load("shop-bottle.jpg");port.Observe();port.Observe();Thread.Sleep(600);port.ExitShop();
        Assert.Equal(2,ops.Clicks.Count);Assert.True(ops.Clicks[1].X<50);Assert.Equal(0,ops.Keys);
    }
    [Fact] public void RecordedIntroductionAndFarewellAdvanceOnceAndFinishOnlyAfterScene()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());using var ocr=new OnnxTextRecognizer(Path.Combine(RecognitionTests.Root(),"models/"+OnnxTextRecognizer.DefaultModelFileName));
        var ops=new ReplayOps(Load("shop-intro.jpg"));using var session=new ScreenWorkflowSession(routes,ops.State,ops,ocr,new MemoryStore());var port=session.CreatePurchasePort(1,"空瓶","exit-replay",()=>null,()=>new());session.Start();
        void Stable(string file){ops.Image=Load(file);session.Observe();session.Observe();port.Observe();}
        Stable("shop-intro.jpg");for(var i=0;i<4&&ops.Clicks.Count==0;i++)port.NavigateToShop();Assert.Single(ops.Clicks);port.NavigateToShop();Assert.Single(ops.Clicks);
        foreach(var file in new[]{"shop-bottle.jpg","shop-materials.jpg","shop-farewell.jpg"})
        {Stable(file);var before=ops.Clicks.Count;for(var i=0;i<4&&ops.Clicks.Count==before;i++)port.ExitShop();Assert.Equal(before+1,ops.Clicks.Count);port.ExitShop();Assert.Equal(before+1,ops.Clicks.Count);}
        Stable("shop-exited.jpg");Assert.Equal("scene",port.Observe().Page);port.ExitShop();Assert.Equal(4,ops.Clicks.Count);Assert.Equal(0,ops.Keys);
    }
    private sealed class MemoryStore:MabiAgent.Workflows.IWorkflowStore
    {private JsonObject? value;public JsonObject? Read()=>value?.DeepClone().AsObject();public void Flush(JsonObject state)=>value=state.DeepClone().AsObject();}
    private sealed class ReplayOps(RgbImage image):INativeWindowsOps
    {
        public RgbImage Image=image;public NativeWindowState State=new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,1280,960,96),new(0,0,1280,960),1,true,false,true,new(0,0));public List<PixelPoint> Clicks=[];private PixelPoint point;public int Keys;
        public NativeWindowState ReadWindow(nint hwnd)=>State;public RgbImage CaptureClient(NativeWindowState state)=>Image;public void MoveClient(nint hwnd,PixelPoint position)=>point=position;public void MouseButton(MouseButton button,bool down){if(down)Clicks.Add(point);}public void Wheel(int delta)=>throw new InvalidOperationException("unexpected scroll");public void Key(ushort key,bool down,bool unicode=false){Keys++;throw new InvalidOperationException("unexpected typing");}public bool AnyPhysicalKeyDown()=>false;
    }
}
