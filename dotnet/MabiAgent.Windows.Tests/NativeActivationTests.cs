using MabiAgent.Windows;
using Xunit;
public sealed class NativeActivationTests
{
    [Fact] public void ExplicitActivationWaitsForActualForeground()
    {
        var checks=0;var waits=0;
        NativeWindowActivation.WaitForForeground(()=>++checks>=3,()=>waits++);
        Assert.Equal(3,checks);Assert.Equal(2,waits);
    }
    [Fact] public void MissingForegroundTimesOutWithoutAnotherActivation()
    {
        var waits=0;
        Assert.Throws<InputBlockedException>(()=>NativeWindowActivation.WaitForForeground(()=>false,()=>waits++));
        Assert.InRange(waits,1,20);
    }
}
