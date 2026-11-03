using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;
public class OnnxOcrTests
{
    [Fact] public void CurrentPurchasedPaperListReadsNameAndGoldForReconciliation()
    {
        var root=RecognitionTests.Root();using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/paper-purchased-current.jpg"));using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);var reader=new ScreenQuantityReader(engine);
        var name=new PixelBox(880,253,915,279);Assert.True(reader.ExactColor(frame,name,"紙",minimum:.75),"name="+reader.ColorText(frame,name,.75));
        var gold=reader.Read(frame,new(1,"gold",new(1093,24,1195,53),"grouped_integer",MinimumConfidence:.8,Thresholds:[120,160]));
        Assert.True(gold.Value?.Quantity==1091259,System.Text.Json.JsonSerializer.Serialize(gold));
    }
    [Fact] public void CurrentEditedSaleQuantityReadsFortyNine()
    {
        var root=RecognitionTests.Root();using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/surplus-edited-native.png"));using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        var spec=ScreenRoutes.Load(root).Configuration["maintenance"]!["surplus"]!["quantity"]!;var reader=new ScreenQuantityReader(engine);var region=NavigationProfile.ReadBox(spec["region"]!);
        var result=reader.Read(frame,new(1,"sale_selected_quantity",region,"integer",Thresholds:[165,210]));
        Assert.True(result.Value?.Quantity==49,System.Text.Json.JsonSerializer.Serialize(result)+"; color="+reader.ColorText(frame,region,.75));
    }
    [Theory][InlineData("surplus-detail-after-cancel.jpg","bag_item_detail","detail_title","原木")][InlineData("surplus-detail-native.png","bag_item_detail","detail_title","原木")][InlineData("surplus-copper-detail-current.jpg","bag_item_detail","detail_title","銅礦石")][InlineData("surplus-sale-current.jpg","bag_sale","sale_title_region","原木")]
    public void CurrentSurplusDetailUsesStableFunctionalTextAndItemName(string file,string page,string titleKey,string name)
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production",file));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        using var recognizer=new ScreenPageRecognizer(routes.Profile);var view=recognizer.Recognize(frame);
        using var matcher=new TemplateMatcher(routes.Profile);
        Assert.True(view.Page==page,"page="+view.Page+"; "+string.Join("; ",new[]{"surplus_item_sell","surplus_item_source"}.Select(n=>n+"="+matcher.Anchor(frame,n)?.Score)));
        using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        var title=NavigationProfile.ReadBox(routes.Configuration["maintenance"]!["surplus"]![titleKey]!);
        var reader=new ScreenQuantityReader(engine);
        Assert.True(reader.FindGridLabel(frame,title,1,name) is not null,"title="+reader.ColorText(frame,title,.75));
    }
    [Fact] public void CurrentOrangeBackpackWeightReadsUsingConfiguredThresholds()
    {
        var root=RecognitionTests.Root();using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/backpack-weight-orange-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        var spec=ScreenRoutes.Load(root).Configuration["maintenance"]!["weight"]!;
        var text=new ScreenQuantityReader(engine).ColorText(frame,NavigationProfile.ReadBox(spec["region"]!),.75);
        var read=text is null?null:QuantityParser.Parse(text,"fixed_ratio");
        Assert.NotNull(read);Assert.Equal(76308,read.Quantity);Assert.Equal(81000,read.Capacity);
        using var listBgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/surplus-raw-log-list-current.jpg"));
        using var listRgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(listBgr,listRgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var listPixels=new byte[listRgb.Width*listRgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(listRgb.Data,listPixels,0,listPixels.Length);
        var label=new ScreenQuantityReader(engine).ColorText(new(listRgb.Width,listRgb.Height,listPixels),new PixelBox(730,260,785,285),.75);
        Assert.Equal("原木",label);
    }
      [Fact] public void CurrentThickWoolStorageDetailKeepsNameAndContainerIdentity()
      {
          var root=RecognitionTests.Root();using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
          using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/storage-thick-wool-detail-current.jpg"));
          using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
          var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
          var routes=ScreenRoutes.Load(root);using var matcher=new TemplateMatcher(routes.Profile);
          Assert.NotNull(matcher.Anchor(frame,"storage_account_dim"));
          var box=new PixelBox(451,311,575,356);var line=engine.RecognizeRgb(frame.Crop(box));
          Assert.True(new ScreenQuantityReader(engine).ExactColor(frame,box,"厚實羊毛",minimum:.75),"name="+line?.Text+"; confidence="+line?.Confidence);
      }
    [Theory][InlineData("storage-clay-detail-current.jpg",451,311,575,356)][InlineData("storage-clay-transfer-current.jpg",148,382,274,428)]
    public void CurrentClayDetailReadsActualStableNameRegion(string file,int left,int top,int right,int bottom)
    {
        var root=RecognitionTests.Root();using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production",file));using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        var box=new PixelBox(left,top,right,bottom);var line=engine.RecognizeRgb(frame.Crop(box));
        Assert.True(new ScreenQuantityReader(engine).ExactColor(frame,box,"黏土",minimum:.75),"name="+line?.Text+" confidence="+line?.Confidence);
    }
    [Fact] public void CurrentOreFullTextIsNotReliableEnoughToBindTask()
    {
        var root=RecognitionTests.Root();using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/ore-task-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        Assert.False(new ScreenQuantityReader(engine).ExactColor(frame,new(1174,224,1233,249),"採集礦脈",minimum:.75));
    }
    [Fact] public void CurrentStorageHideNameIsReadFromItsStableRowLabel()
    {
        var root=RecognitionTests.Root();using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/storage-hide-row-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        var box=new PixelBox(350,713,450,744);var line=engine.RecognizeRgb(frame.Crop(box));
        Assert.True(line?.Text=="高級生皮","text="+line?.Text+"; confidence="+line?.Confidence);
        Assert.True(new ScreenQuantityReader(engine).ExactColor(frame,box,"高級生皮",minimum:.75));
    }
    [Theory][InlineData("煤炭",1130,360,false)][InlineData("膠水",930,360,false)][InlineData("高級生皮",725,606,false)][InlineData("煤炭",1130,360,true)]
    public void ScrolledBagLabelsKeepTheirActualRow(string name,int minimumX,int minimumY,bool singleColumn)
    {
        var root=RecognitionTests.Root();using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/production/bag-materials-scrolled-current.jpg"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var height=rgb.Height;var width=rgb.Width;
        if(singleColumn)for(var y=0;y<height;y++)Array.Clear(pixels,y*width*3,1128*3);
        var result=new ScreenQuantityReader(engine).FindGridLabel(new(rgb.Width,rgb.Height,pixels),new(720,180,1230,790),5,name);
        Assert.NotNull(result);Assert.InRange(result.Value.Center.X,minimumX+15,minimumX+70);Assert.InRange(result.Value.Top,minimumY-8,minimumY+8);
    }
    [Theory][InlineData("tidy-count-current.png",1)][InlineData("tidy-count-zero-current.png",0)]
    public void CurrentTidyCountReadsEnabledAndDisabledText(string file,long expected)
    {
        var root=RecognitionTests.Root();var spec=ScreenRoutes.Load(root).Configuration["maintenance"]!["tidy"]!["count"]!;
        using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/screen_ui",file));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var reader=new ScreenQuantityReader(engine);var frame=new RgbImage(rgb.Width,rgb.Height,pixels);
        var result=ScreenMaintenancePort.ReadTidyCount(reader,frame,new(0,0,rgb.Width,rgb.Height),spec);
        Assert.True(result?.Quantity==expected,"text="+reader.ColorText(frame,new(0,0,rgb.Width,rgb.Height),.75));
    }
    [Fact] public void ConfiguredPrimaryRepairQuoteReadsFullAmountFromNativeFixture()
    {
        var root=RecognitionTests.Root();var routes=ScreenRoutes.Load(root);var spec=routes.Configuration["maintenance"]!["repair"]!["preview_quote"]!;
        using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/screen_ui/repair-controls-enabled.png"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var box=NavigationProfile.ReadBox(spec["region"]!);box=new(box.Left-280,box.Top-824,box.Right-280,box.Bottom-824);
        var reading=new ScreenQuantityReader(engine).Read(new(rgb.Width,rgb.Height,pixels),new(1,"preview",box,"grouped_integer",Thresholds:[165,185],MinimumConfidence:.90));
        Assert.True(reading.Value?.Quantity==21886,System.Text.Json.JsonSerializer.Serialize(reading));
    }
    [Fact] public void CapturedExactRepairPreviewIsAnInteger()
    {
        var root=RecognitionTests.Root();using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/screen_ui/repair-exact-quote.png"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var reading=new ScreenQuantityReader(engine).Read(new(rgb.Width,rgb.Height,pixels),new(1,"preview",new(26,10,82,34),"grouped_integer",Thresholds:[165,185],MinimumConfidence:.90));
        Assert.True(reading.Value?.Quantity==19913,System.Text.Json.JsonSerializer.Serialize(reading));
    }
    [Fact] public void CapturedCompactRepairQuoteIsNotAnExactInteger()
    {
        var root=RecognitionTests.Root();using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/screen_ui/repair-compact-quote.png"));
        using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        var reading=new ScreenQuantityReader(engine).Read(new(rgb.Width,rgb.Height,pixels),new(1,"repair_quote_gold",new(35,5,94,34),"grouped_integer",Thresholds:[165,185]));
        Assert.Null(reading.Value);Assert.Equal(2,reading.Readings.Count);
        Assert.All(reading.Readings,r=>{Assert.Equal("1.9萬",r.Text);Assert.True(r.Confidence>=.95);Assert.Null(r.Value);});
    }
    [Fact] public void LowCpuModeAndSafeSessionSwitchPreserveRecognitionAndCanRevert()
    {
        var path=Path.Combine(RecognitionTests.Root(),"models",OnnxTextRecognizer.DefaultModelFileName);
        using var engine=new OnnxTextRecognizer(path);using var mat=new OpenCvSharp.Mat(48,160,OpenCvSharp.MatType.CV_8UC1,OpenCvSharp.Scalar.White);
        OpenCvSharp.Cv2.PutText(mat,"123",new(8,36),OpenCvSharp.HersheyFonts.HersheySimplex,1.1,OpenCvSharp.Scalar.Black,2);
        var pixels=new byte[48*160];System.Runtime.InteropServices.Marshal.Copy(mat.Data,pixels,0,pixels.Length);
        var baseline=engine.Recognize(160,48,pixels);Assert.Equal("123",baseline?.Text);Assert.False(engine.LowCpu);
        engine.ConfigureLowCpu(true);Assert.True(engine.LowCpu);var efficient=engine.Recognize(160,48,pixels);
        Assert.Equal(baseline!.Text,efficient?.Text);Assert.Equal(baseline.Confidence,efficient!.Confidence,5);
        engine.ConfigureLowCpu(false);Assert.False(engine.LowCpu);Assert.Equal(baseline.Text,engine.Recognize(160,48,pixels)?.Text);
        engine.Dispose();Assert.Throws<ObjectDisposedException>(()=>engine.ConfigureLowCpu(true));
        using var initiallyEfficient=new OnnxTextRecognizer(path,lowCpu:true);Assert.True(initiallyEfficient.LowCpu);
        Assert.Equal(baseline.Text,initiallyEfficient.Recognize(160,48,pixels)?.Text);
    }
    [Fact] public void TimeoutKeepsNumbersAndNamesUnknown()
    {
        var reader=new ScreenQuantityReader(new TimedOutEngine());var image=new RgbImage(8,8,new byte[192]);Assert.Null(reader.Text(image,new(0,0,8,8)));Assert.False(reader.Exact(image,new(0,0,8,8),"鐵礦石"));var result=reader.Read(image,new(1,"bag",new(0,0,8,8)));Assert.Null(result.Value);Assert.All(result.Readings,r=>Assert.Equal(nameof(TimeoutException),r.Error));
    }
    private sealed class TimedOutEngine:IOcrEngine{public OcrLine? Recognize(int width,int height,byte[] pixels)=>throw new TimeoutException();}
    [Fact] public void CtcDropsBlanksAndAdjacentDuplicatesWithoutSentinelBias()
    {
        var result=OnnxTextRecognizer.Decode([0,1,1,0,2],[.9f,.8f,.7f,.9f,1f],["","1","2"]);Assert.NotNull(result);Assert.Equal("12",result.Text);Assert.Equal(.9,result.Confidence,5);
    }
    [Fact] public void ExistingModelCanRecognizeRenderedDigitsWithoutPythonBridge()
    {
        var path=Path.Combine(RecognitionTests.Root(),"models",OnnxTextRecognizer.DefaultModelFileName);
        using var engine=new OnnxTextRecognizer(path);using var mat=new OpenCvSharp.Mat(48,160,OpenCvSharp.MatType.CV_8UC1,OpenCvSharp.Scalar.White);
        OpenCvSharp.Cv2.PutText(mat,"123",new(8,36),OpenCvSharp.HersheyFonts.HersheySimplex,1.1,OpenCvSharp.Scalar.Black,2);var bytes=new byte[48*160];System.Runtime.InteropServices.Marshal.Copy(mat.Data,bytes,0,bytes.Length);
        var result=engine.Recognize(160,48,bytes);Assert.NotNull(result);Assert.Equal("123",result.Text);Assert.True(result.Confidence>=.75);
        var rgb=new byte[bytes.Length*3];for(var i=0;i<bytes.Length;i++)rgb.AsSpan(i*3,3).Fill(bytes[i]);
        var candidate=engine.RecognizeRgb(new(160,48,rgb));Assert.Equal(result.Text,candidate?.Text);Assert.Equal(result.Confidence,candidate!.Confidence,5);
    }
    [Fact] public void DefaultModelReadsTraditionalSearchAndRejectsMissingOrExtraGlyphs()
    {
        var root=RecognitionTests.Root();using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));var reader=new ScreenQuantityReader(engine);
        using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"assets/templates/gathering_ui/bag_search_iron_full_name.png"));using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);var source=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,source,0,source.Length);
        var width=rgb.Width;var height=rgb.Height;for(var kind=0;kind<4;kind++){var pixels=(byte[])source.Clone();for(var y=0;y<height;y++)for(var x=0;x<width;x++){var i=(y*rgb.Width+x)*3;if(kind==1&&x<27||kind==2&&x>=47)pixels.AsSpan(i,3).Clear();if(kind==3&&x>=80&&x<102)Array.Copy(source,(y*rgb.Width+x-76)*3,pixels,i,3);}var image=new RgbImage(rgb.Width,rgb.Height,pixels);Assert.Equal(kind==0,reader.Exact(image,new(0,0,rgb.Width,rgb.Height),"鐵礦石"));}
    }
    [Fact] public void CapturedBagQuantityUsesTwoIndependentThresholds()
    {
        var root=RecognitionTests.Root();using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));var reader=new ScreenQuantityReader(engine);using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"tests/fixtures/screen_ui/ocr/bag-iron-quantity.png"));using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);var bytes=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,bytes,0,bytes.Length);var image=new RgbImage(rgb.Width,rgb.Height,bytes);var result=reader.Read(image,new(198875762,"backpack_filtered_search",new(0,0,rgb.Width,rgb.Height)));Assert.Equal(38,result.Value?.Quantity);Assert.Equal(2,result.Readings.Count);Assert.All(result.Readings,r=>Assert.Equal(38,r.Value?.Quantity));
    }
    [Fact] public void UnclearTraditionalNameDoesNotAuthorizeAnExpectedName()
    {
        var root=RecognitionTests.Root();using var engine=new OnnxTextRecognizer(Path.Combine(root,"models",OnnxTextRecognizer.DefaultModelFileName));var reader=new ScreenQuantityReader(engine);using var bgr=OpenCvSharp.Cv2.ImRead(Path.Combine(root,"assets/templates/screen_ui/bag_names/plant_fiber_name.png"));using var rgb=new OpenCvSharp.Mat();OpenCvSharp.Cv2.CvtColor(bgr,rgb,OpenCvSharp.ColorConversionCodes.BGR2RGB);var bytes=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,bytes,0,bytes.Length);Assert.False(reader.Exact(new(rgb.Width,rgb.Height,bytes),new(0,0,rgb.Width,rgb.Height),"植物纖維"));
    }
}
