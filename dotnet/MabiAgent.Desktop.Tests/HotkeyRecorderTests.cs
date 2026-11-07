using Avalonia;
using Avalonia.Input;
using MabiAgent.Desktop.Controls;
using Xunit;
namespace MabiAgent.Desktop.Tests;

public sealed class HotkeyRecorderTests
{
    private static KeyEventArgs Press(HotkeyRecorder recorder,Key key,KeyModifiers modifiers=KeyModifiers.None)
    {
        var input=new KeyEventArgs{RoutedEvent=InputElement.KeyDownEvent,Key=key,KeyModifiers=modifiers};recorder.RaiseEvent(input);return input;
    }
    [Fact] public void IdleKeysAndClicksDoNotChangeDraft()
    {
        var recorder=new HotkeyRecorder{Text="Ctrl+Alt+F10"};
        Assert.False(Press(recorder,Key.N).Handled);Assert.Equal("Ctrl+Alt+F10",recorder.Text);
        var click=Click(recorder,PointerUpdateKind.LeftButtonPressed);
        Assert.False(click.Handled);Assert.False(recorder.IsRecording);Assert.Equal("Ctrl+Alt+F10",recorder.Text);
    }
    [Theory]
    [InlineData(Key.F10,KeyModifiers.None,"F10")][InlineData(Key.F12,KeyModifiers.Control,"Ctrl+F12")]
    [InlineData(Key.Tab,KeyModifiers.None,"Tab")][InlineData(Key.Back,KeyModifiers.None,"Backspace")]
    [InlineData(Key.N,KeyModifiers.Control|KeyModifiers.Shift,"Ctrl+Shift+N")]
    [InlineData(Key.NumPad0,KeyModifiers.None,"NumPad0")][InlineData(Key.Space,KeyModifiers.None,"Space")]
    public void ExplicitRecordingAcceptsKeyboardKeys(Key key,KeyModifiers modifiers,string expected)
    {
        var recorder=new HotkeyRecorder();recorder.BeginRecording();Assert.True(Press(recorder,key,modifiers).Handled);
        Assert.Equal(expected,recorder.Text);Assert.False(recorder.IsRecording);Assert.True(recorder.IsReadOnly);
    }
    [Fact] public void ModifierCommitsOnReleaseAndCancelKeepsOldDraft()
    {
        var recorder=new HotkeyRecorder{Text="F10"};recorder.BeginRecording();Press(recorder,Key.LeftCtrl,KeyModifiers.Control);
        Assert.Equal("F10",recorder.Text);Assert.True(recorder.IsRecording);
        recorder.RaiseEvent(new KeyEventArgs{RoutedEvent=InputElement.KeyUpEvent,Key=Key.LeftCtrl});
        Assert.Equal("LeftCtrl",recorder.Text);Assert.False(recorder.IsRecording);
        recorder.BeginRecording();recorder.CancelRecording();Press(recorder,Key.F11);Assert.Equal("LeftCtrl",recorder.Text);
    }
    private static PointerPressedEventArgs Click(HotkeyRecorder recorder,PointerUpdateKind kind)
    {
        var pointer=new Pointer(42,PointerType.Mouse,true);
        var input=new PointerPressedEventArgs(recorder,pointer,recorder,default,0,new PointerPointProperties(RawInputModifiers.None,kind),KeyModifiers.None,1);
        recorder.RaiseEvent(input);return input;
    }
    [Theory]
    [InlineData(PointerUpdateKind.LeftButtonPressed,"MouseLeft")][InlineData(PointerUpdateKind.RightButtonPressed,"MouseRight")]
    [InlineData(PointerUpdateKind.MiddleButtonPressed,"MouseMiddle")][InlineData(PointerUpdateKind.XButton1Pressed,"MouseX1")][InlineData(PointerUpdateKind.XButton2Pressed,"MouseX2")]
    public void RecordsNextMouseClickOnlyWhenArmed(PointerUpdateKind kind,string expected)
    {
        var recorder=new HotkeyRecorder{Text="F10"};recorder.BeginRecording();var click=Click(recorder,kind);
        Assert.True(click.Handled);Assert.Equal(expected,recorder.Text);Assert.False(recorder.IsRecording);
        Click(recorder,PointerUpdateKind.LeftButtonPressed);Assert.Equal(expected,recorder.Text);
    }
    [Fact] public void RecordsWheelAndStopsCapturingAfterCommit()
    {
        var recorder=new HotkeyRecorder();recorder.BeginRecording();var pointer=new Pointer(42,PointerType.Mouse,true);
        var wheel=new PointerWheelEventArgs(recorder,pointer,recorder,default,0,default,KeyModifiers.Control,new Vector(0,-1));
        recorder.RaiseEvent(wheel);Assert.True(wheel.Handled);Assert.Equal("Ctrl+WheelDown",recorder.Text);
    }
}
