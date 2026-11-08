using System.ComponentModel;
using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;

[Collection("Native process metadata")]
public sealed class GlobalStopHotkeyTests
{
    [Theory]
    [InlineData("MouseX2")][InlineData("F12")][InlineData("LeftCtrl")][InlineData("WheelDown")]
    public async Task PauseAndStopRejectSharedBindingsAndKeepIndependentActions(string shared)
    {
        var paused=0;var stopped=0;
        using(var pause=new GlobalStopHotkey(()=>paused++,_=>{}))
        using(var stop=new GlobalStopHotkey(()=>stopped++,_=>{}))
        {
            await pause.ApplyAsync(shared);
            await stop.ApplyAsync("Ctrl+Alt+Shift+F24");
            await Assert.ThrowsAsync<Win32Exception>(()=>stop.ApplyAsync(shared));
            Assert.Equal("Ctrl+Alt+Shift+F24",stop.Active);
            var key=StopHotkey.Parse(shared);
            void Press(GlobalStopHotkey receiver,bool editing=false)
            {
                if(key.IsMouse)receiver.HandlePhysicalMouse(key.Key,key.Modifiers,editing);
                else{receiver.HandlePhysicalKey(key.Key,false,key.Modifiers,editing);receiver.HandlePhysicalKey(key.Key,true,key.Modifiers,editing);}
            }
            await pause.InvokeAsync(()=>Press(pause,true));Assert.Equal(0,paused);
            await pause.InvokeAsync(()=>Press(pause));Assert.Equal(1,paused);Assert.Equal(0,stopped);
            await stop.InvokeAsync(()=>stop.HandlePhysicalKey(0x87,false,7,false));Assert.Equal(1,stopped);
        }
        using var replacement=new GlobalStopHotkey(()=>{},_=>{});
        await replacement.ApplyAsync(shared);
    }
    [Theory]
    [InlineData("alt + control + f10","Ctrl+Alt+F10")]
    [InlineData("SHIFT+ctrl+9","Ctrl+Shift+9")]
    [InlineData("Win+Pause","Win+Pause")]
    [InlineData("F10","F10")][InlineData("Ctrl+F12","Ctrl+F12")]
    [InlineData("MouseLeft","MouseLeft")][InlineData("shift+MouseX2","Shift+MouseX2")]
    [InlineData("Tab","Tab")][InlineData("Ctrl","LeftCtrl")][InlineData("WheelDown","WheelDown")]
    public void NormalizesUserBindings(string input,string expected)=>Assert.Equal(expected,StopHotkey.Parse(input).Text);
    [Theory]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Ctrl+")][InlineData("Ctrl+A+B")][InlineData("Alt+F25")]
    public void RejectsIncompleteOrUnsupportedBindings(string input)=>Assert.Throws<ArgumentException>(()=>StopHotkey.Parse(input));

    [Fact]
    public async Task ConflictKeepsOldBindingAndItsIndependentMessageLoopReceivesStop()
    {
        var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors=new List<Exception>();
        using var first=new GlobalStopHotkey(()=>stopped.TrySetResult(),e=>errors.Add(e));
        using var occupied=new GlobalStopHotkey(()=>{},e=>errors.Add(e));
        await first.ApplyAsync("Ctrl+Alt+Shift+F22").WaitAsync(TimeSpan.FromSeconds(5));
        await occupied.ApplyAsync("Ctrl+Alt+Shift+F23").WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<Win32Exception>(()=>first.ApplyAsync("Ctrl+Alt+Shift+F23"));
        Assert.Equal("Ctrl+Alt+Shift+F22",first.Active);
        // Exercise the matching path without sending keyboard or game input.
        await first.InvokeAsync(()=>first.HandlePhysicalKey(0x85,false,7,false));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await first.ApplyAsync("Ctrl+Alt+Shift+F24");
        await occupied.ApplyAsync("Ctrl+Alt+Shift+F22");
        Assert.Equal("Ctrl+Alt+Shift+F24",first.Active);Assert.Empty(errors);
    }

    [Fact] public async Task SingleKeyMouseAndEditingSuppressionShareOneBinding()
    {
        var count=0;using var receiver=new GlobalStopHotkey(()=>Interlocked.Increment(ref count),_=>{});
        await receiver.ApplyAsync("F24");
        receiver.HandlePhysicalKey(0x87,false,0,true);receiver.HandlePhysicalKey(0x87,true,0,true);
        Assert.Equal(0,count);
        receiver.HandlePhysicalKey(0x87,false,0,false);receiver.HandlePhysicalKey(0x87,false,0,false);
        Assert.Equal(1,count);receiver.HandlePhysicalKey(0x87,true,0,false);
        await receiver.ApplyAsync("Ctrl+MouseX2");
        receiver.HandlePhysicalKey(0x87,false,0,false);receiver.HandlePhysicalMouse(6,0,false);
        receiver.HandlePhysicalMouse(6,2,true);Assert.Equal(1,count);
        receiver.HandlePhysicalMouse(6,2,false);Assert.Equal(2,count);
        await receiver.ApplyAsync("LeftCtrl");
        receiver.HandlePhysicalKey(0xa2,false,0,false);Assert.Equal(2,count);
        receiver.HandlePhysicalKey(0x41,false,2,false);receiver.HandlePhysicalKey(0xa2,true,2,false);
        Assert.Equal(2,count);
        receiver.HandlePhysicalKey(0xa2,false,0,false);receiver.HandlePhysicalKey(0xa2,true,2,false);
        Assert.Equal(3,count);
        await receiver.ApplyAsync("F12");receiver.HandlePhysicalKey(0x7b,false,0,false);Assert.Equal(4,count);
    }
    [Fact] public void EditingRegionSuppressesBeforeInitialClickAndUntilFocusLeaves()
    {
        var owner=new object();try
        {
            StopHotkeyEditing.Update(owner,123,10,20,100,80,false);
            Assert.True(StopHotkeyEditing.IsSuppressed(123,15,25));
            Assert.False(StopHotkeyEditing.IsSuppressed(999,15,25));
            Assert.False(StopHotkeyEditing.IsSuppressed(123,150,150));
            StopHotkeyEditing.Update(owner,123,10,20,100,80,true);
            Assert.True(StopHotkeyEditing.IsSuppressed(123,150,150));
        }finally{StopHotkeyEditing.Remove(owner);}
        Assert.False(StopHotkeyEditing.IsSuppressed(123,15,25));
    }
    [Fact] public async Task AutomationDoesNotStopItselfAndEditingCanRecordTheActiveKeyboardBinding()
    {
        var count=0;using var receiver=new GlobalStopHotkey(()=>count++,_=>{});
        await receiver.ApplyAsync("F24");
        receiver.ObserveKeyboard(0x100,0x87,0x10,0,false);Assert.Equal(0,count);
        var captured=0;void Capture(uint key,bool released,uint modifiers){if(!released&&key==0x87)captured++;}
        StopHotkeyEditing.KeyCaptured+=Capture;
        try{receiver.ObserveKeyboard(0x100,0x87,0,0,true);Assert.Equal(1,captured);Assert.Equal(0,count);}
        finally{StopHotkeyEditing.KeyCaptured-=Capture;}
        await receiver.ApplyAsync("MouseLeft");
        receiver.ObserveMouse(0x201,0,1,0,0,false);receiver.ObserveMouse(0x201,0,0,0xff515700,0,false);
        Assert.Equal(0,count);receiver.ObserveMouse(0x201,0,0,0,0,false);Assert.Equal(1,count);
    }
    [Theory][InlineData(0x201u,0u,1u)][InlineData(0x20bu,0x20000u,6u)]
    [InlineData(0x20au,0x780000u,0x100u)][InlineData(0x20au,0xff880000u,0x101u)]
    [InlineData(0x202u,0u,0u)]
    public void MapsMouseDownAndWheelButNotRelease(uint message,uint data,uint expected)=>Assert.Equal(expected,GlobalStopHotkey.MouseKey(message,data));
}
