using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;
public sealed class GameWindowSelectionTests
{
    private static GameWindow Window(nint id,int pid=1,long started=42)=>new(new(id,pid,started,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,1280,960,96),new(0,0,1280,960),id,true,false,true,new(0,0)),"玛奇");
    [Fact] public void OneGameIsSelectedAndChoiceIsShared()
    {
        IReadOnlyList<GameWindow> windows=[Window(1)];var model=new GameWindowSelection(()=>windows);var changes=0;model.Changed+=_=>changes++;
        model.Refresh();Assert.Equal((nint)1,model.Selected!.State.WindowId);
        windows=[Window(1),Window(2,2)];model.Refresh();Assert.Equal((nint)1,model.Selected!.State.WindowId);
        model.Select(windows[1]);Assert.Equal((nint)2,model.Selected!.State.WindowId);Assert.Equal(2,changes);
    }
    [Fact] public void MultipleGamesNeedSelectionAndReusedHandleIsNotOldProcess()
    {
        IReadOnlyList<GameWindow> windows=[Window(1),Window(2,2)];var model=new GameWindowSelection(()=>windows);model.Refresh();Assert.Null(model.Selected);
        var old=windows[0];model.Select(old);windows=[Window(1,1,99),Window(2,2)];model.Refresh();Assert.Null(model.Selected);
        Assert.Throws<InputBlockedException>(()=>model.Select(old));
    }
}
