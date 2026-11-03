using System.Text.Json.Nodes;
using Xunit;
namespace MabiAgent.Windows.Tests;

public sealed class RecognitionCacheTests
{
    [Fact] public void SameFrameReusesResultsButFreshFrameAndThresholdsRemainIndependent()
    {
        var profile=Profile();using var matcher=new TemplateMatcher(profile);var image=Image(profile);
        var hit=matcher.Match(image,"scene_bag");Assert.NotNull(hit);Assert.Equal(hit,matcher.Match(image,"scene_bag"));Assert.Equal(1,matcher.CacheStatistics.ResultHits);
        Assert.Null(matcher.Match(image,"scene_bag",thresholdOverride:1.01));
        var before=matcher.CacheStatistics.NativeMatches;Assert.NotNull(matcher.Match(new(image.Width,image.Height,image.CopyPixels()),"scene_bag"));Assert.True(matcher.CacheStatistics.NativeMatches>before);
        Assert.Null(matcher.Match(new(1280,960,new byte[1280*960*3]),"scene_bag"));Assert.InRange(matcher.CacheStatistics.Results,0,1);
    }
    [Fact] public void AliasesModesAndSearchOverridesCannotShareIncorrectResults()
    {
        var original=Profile();var definition=(JsonObject)original.Definition.DeepClone();var alias=definition["templates"]!["scene_bag"]!.DeepClone();definition["templates"]!["alias"]=alias;
        var glyph=alias.DeepClone();glyph["match_mode"]="yellow_text";definition["templates"]!["yellow"]=glyph;
        var profile=NavigationProfile.FromDefinition(RecognitionTests.Root(),definition);using var matcher=new TemplateMatcher(profile);var image=Image(profile);
        Assert.NotNull(matcher.Match(image,"scene_bag"));Assert.Equal("alias",matcher.Match(image,"alias")?.Name);
        var entries=matcher.CacheStatistics.ReferenceEntries;matcher.Match(image,"yellow");Assert.Equal(entries+1,matcher.CacheStatistics.ReferenceEntries);
        Assert.Null(matcher.Match(image,"scene_bag",searchOverride:new(0,0,50,50)));Assert.NotNull(matcher.Match(image,"scene_bag"));
    }
    [Fact] public void CacheIsBoundedAndDisposeReleasesNativeBuffers()
    {
        var profile=Profile();var matcher=new TemplateMatcher(profile);var image=Image(profile);
        for(var i=0;i<2200;i++)matcher.Match(image,"scene_bag",searchOverride:new(i%100,i/100,i%100+80,i/100+80));
        var stats=matcher.CacheStatistics;Assert.InRange(stats.ReferenceBytes,0,8*1024*1024);Assert.InRange(stats.FrameBytes,0,8*1024*1024);Assert.InRange(stats.FrameEntries,0,1024);Assert.InRange(stats.Results,0,2048);
        matcher.Dispose();Assert.Equal(0,matcher.CacheStatistics.ReferenceBytes);Assert.Equal(0,matcher.CacheStatistics.FrameBytes);Assert.Equal(0,matcher.CacheStatistics.Results);Assert.Throws<ObjectDisposedException>(()=>matcher.Match(image,"scene_bag"));matcher.Dispose();
    }
    [Fact] public void BorrowedMatcherSurvivesRecognizerDisposal()
    {
        var profile=Profile();using var matcher=new TemplateMatcher(profile);using(var recognizer=new ScreenPageRecognizer(profile,matcher))recognizer.Recognize(Image(profile));Assert.NotNull(matcher.Match(Image(profile),"scene_bag"));
    }
    [Fact] public void SingleFrameProofDispatchesWithoutSecondBusinessScan()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();routes.Profile.Definition["pages"]=JsonNode.Parse("""{"scene":{"anchors":["scene_bag"],"targets":{"scene.open_bag":"scene_bag"}}}""");routes.Profile.Definition["page_variants"]=new JsonObject();
        var ops=new ChangingOps(Image(routes.Profile));using var session=new ScreenWorkflowSession(routes,ops.Window,ops,new EmptyOcr(),new EmptyStore());session.Start();session.Observe();session.Observe();
        var proof=session.Guard.ObserveTarget("scene.open_bag");ops.Image=new(1280,960,new byte[1280*960*3]);
        session.Guard.Click(proof);Assert.True(ops.Inputs>0);Assert.Equal(3,ops.Captures);
    }
    private sealed class EmptyOcr:IOcrEngine{public OcrLine? Recognize(int w,int h,byte[] data)=>null;}
    private sealed class EmptyStore:MabiAgent.Workflows.IWorkflowStore{public JsonObject? Read()=>null;public void Flush(JsonObject state)=>throw new InvalidOperationException();}
    private sealed class ChangingOps(RgbImage image):INativeWindowsOps
    {
        public RgbImage Image=image;public int Inputs,Captures;public NativeWindowState Window=>new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,1280,960,96),new(0,0,1280,960),1,true,false,true,new(0,0));
        public NativeWindowState ReadWindow(nint hwnd)=>Window;public RgbImage CaptureClient(NativeWindowState state){Captures++;return new(Image.Width,Image.Height,Image.CopyPixels());}
        public void MoveClient(nint hwnd,PixelPoint point)=>Inputs++;public void MouseButton(MouseButton button,bool down)=>Inputs++;public void Wheel(int delta)=>Inputs++;public void Key(ushort key,bool down,bool unicode=false)=>Inputs++;public bool AnyPhysicalKeyDown()=>false;
    }
    private static NavigationProfile Profile()=>NavigationProfile.Load(RecognitionTests.Root(),"config/navigation_ui.1280x960.tw.json");
    private static RgbImage Image(NavigationProfile profile)
    {
        var template=profile.Templates["scene_bag"];var bytes=new byte[1280*960*3];var source=template.Image.CopyPixels();for(var row=0;row<template.Image.Height;row++)Array.Copy(source,row*template.Image.Width*3,bytes,((template.Box.Top+row)*1280+template.Box.Left)*3,template.Image.Width*3);return new(1280,960,bytes);
    }
}
