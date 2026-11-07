using MabiAgent.Windows;
using OpenCvSharp;
using Xunit;

namespace MabiAgent.Windows.Tests;

public sealed class GatheringLabelRecognitionTests
{
    internal static RgbImage WoolFrame()
    {
        using var bgr=Cv2.ImRead(Path.Combine(RecognitionTests.Root(),"tests/fixtures/production/wool-nodes-current.png"));
        using var rgb=new Mat();Cv2.CvtColor(bgr,rgb,ColorConversionCodes.BGR2RGB);
        var pixels=new byte[rgb.Width*rgb.Height*3];System.Runtime.InteropServices.Marshal.Copy(rgb.Data,pixels,0,pixels.Length);
        return new(rgb.Width,rgb.Height,pixels);
    }

    [Fact] public void CurrentWoolListDistinguishesAllThreeCompleteNames()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var frame=WoolFrame();
        using var matcher=new TemplateMatcher(routes.Profile);
        for(var i=0;i<3;i++)Assert.NotNull(matcher.Anchor(frame,$"preset_catalog_point_wool_{i:00}_node"));
        using var recognizer=new ScreenPageRecognizer(routes.Profile,matcher);var view=recognizer.Recognize(frame);
        Assert.Equal("wool_nodes",view.Page);
        for(var i=0;i<3;i++)Assert.Contains($"gathering.open_point_wool_{i:00}",view.Targets.Keys);
    }

    public static IEnumerable<object[]> Labels()=>ScreenRoutes.Load(RecognitionTests.Root()).Configuration["gathering_routes"]!.AsObject()
        .Where(p=>p.Key.StartsWith("point_",StringComparison.Ordinal)).Select(p=>new object[]{p.Key});

    [Theory][MemberData(nameof(Labels))]
    public void CompleteNamesRemainDistinctFromOtherRowsAfterScrolling(string key)
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var profile=routes.Profile;
        var node=routes.Configuration["gathering_routes"]![key]!;
        var siblings=routes.Configuration["gathering_routes"]!.AsObject()
            .Where(p=>p.Key!=key&&p.Key.StartsWith("point_",StringComparison.Ordinal)&&p.Value!["skill"]!.GetValue<string>()==node["skill"]!.GetValue<string>()).Select(p=>p.Key);
        using var matcher=new TemplateMatcher(profile);var name=$"preset_catalog_{key}_node";
        foreach(var sibling in siblings)
        {
            var other=$"preset_catalog_{sibling}_node";
            var frame=Rows(profile,(name,350),(other,650));
            Assert.True(matcher.Anchor(frame,name) is not null,$"Missing {key} beside {sibling}");
            Assert.True(matcher.Anchor(frame,other) is not null,$"Missing {sibling} beside {key}");
            Assert.Null(matcher.Anchor(Rows(profile,(other,480)),name));
        }
        Assert.Null(matcher.Anchor(Rows(profile,(name,350),(name,650)),name));
    }

    private static RgbImage Rows(NavigationProfile profile,params (string Name,int Top)[] rows)
    {
        var pixels=new byte[1280*960*3];Array.Fill(pixels,(byte)32);
        foreach(var (name,top) in rows)
        {
            var template=profile.Templates[name];var source=template.Image.CopyPixels();
            for(var y=0;y<template.Image.Height;y++)Array.Copy(source,y*template.Image.Width*3,pixels,((top+y)*1280+template.Box.Left)*3,template.Image.Width*3);
        }
        return new(1280,960,pixels);
    }
}
