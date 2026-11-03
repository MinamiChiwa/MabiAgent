using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;
public class SlotRecognitionTests
{
    [Fact] public void MissingRimIsUnknownNotEmpty()
    {
        var image=new RgbImage(60,60,new byte[60*60*3]);Assert.Null(SlotRecognition.EmptyIndices(image,[new(0,0,60,60)]));Assert.Empty(SlotRecognition.ReadyIndices(image,[new(0,0,60,60)])!);
    }
    [Fact] public void CompleteCyanRingIsReadyPartialRingIsNot()
    {
        byte[] Pixels(bool partial){var p=new byte[60*60*3];for(var y=0;y<60;y++)for(var x=0;x<60;x++){var radius=Math.Sqrt(Math.Pow(x-29.5,2)+Math.Pow(y-29.5,2))/60;if(radius>.41&&radius<.47&&(!partial||x<30)){var i=(y*60+x)*3;p[i]=20;p[i+1]=180;p[i+2]=200;}}return p;}
        Assert.Equal([0],SlotRecognition.ReadyIndices(new(60,60,Pixels(false)),[new(0,0,60,60)]));Assert.Empty(SlotRecognition.ReadyIndices(new(60,60,Pixels(true)),[new(0,0,60,60)])!);
    }
    [Fact] public void PositiveGreyRimIsEmpty()
    {
        var pixels=new byte[60*60*3];for(var y=0;y<60;y++)for(var x=0;x<60;x++){var radius=Math.Sqrt(Math.Pow(x-29.5,2)+Math.Pow(y-29.5,2))/60;byte value=radius>.41&&radius<.47?(byte)100:(byte)20;for(var c=0;c<3;c++)pixels[(y*60+x)*3+c]=value;}
        Assert.Equal([0],SlotRecognition.EmptyIndices(new(60,60,pixels),[new(0,0,60,60)]));
    }
}
