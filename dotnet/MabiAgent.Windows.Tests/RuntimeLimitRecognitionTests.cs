using Xunit;
namespace MabiAgent.Windows.Tests;

public sealed class RuntimeLimitRecognitionTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void CurrentRuntimeMessagePausesWithoutDependingOnAnimatedEndButton(bool hideButton)
    {
        var root=RecognitionTests.Root();var image=FishingFeedbackTests.ReadImage(Path.Combine(root,"tests/fixtures/production/runtime-expiry-current.png"));
        if(hideButton)
        {
            var pixels=image.CopyPixels();
            for(var y=127;y<176;y++)for(var x=449;x<831;x++){var i=(y*image.Width+x)*3;pixels[i]=pixels[i+1]=pixels[i+2]=40;}
            image=new(image.Width,image.Height,pixels);
        }
        using var recognition=new RuntimeLimitRecognition(root);var page=recognition.Recognize(image);
        Assert.NotNull(page);Assert.Equal("restart_required",page!.Page);Assert.True(page.Modal);Assert.Empty(page.Targets);
    }
    [Fact]
    public void OrdinaryRecipeAndGreenConfirmationDoNotRequestRestart()
    {
        var root=RecognitionTests.Root();using var recognition=new RuntimeLimitRecognition(root);
        var image=FishingFeedbackTests.ReadImage(Path.Combine(root,"tests/fixtures/production/wood-plus-recipe-current.png"));
        Assert.Null(recognition.Recognize(image));
    }
}
