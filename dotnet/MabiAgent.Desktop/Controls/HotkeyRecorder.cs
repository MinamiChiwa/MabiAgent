using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Win32.Input;
using MabiAgent.Windows;
namespace MabiAgent.Desktop.Controls;

public sealed class HotkeyRecorder:TextBox
{
    protected override Type StyleKeyOverride=>typeof(TextBox);
    private const string IdleMessage="点击“录入”后按键盘键、鼠标键或滚轮；点击“应用快捷键”保存。设置区域内暂停停止响应。";
    public static readonly StyledProperty<string> CaptureMessageProperty=AvaloniaProperty.Register<HotkeyRecorder,string>(nameof(CaptureMessage),IdleMessage);
    public static readonly StyledProperty<bool> IsRecordingProperty=AvaloniaProperty.Register<HotkeyRecorder,bool>(nameof(IsRecording));
    public string CaptureMessage{get=>GetValue(CaptureMessageProperty);set=>SetValue(CaptureMessageProperty,value);}
    public bool IsRecording{get=>GetValue(IsRecordingProperty);private set=>SetValue(IsRecordingProperty,value);}
    private uint pendingModifier;
    private int recordingGeneration;
    public HotkeyRecorder()
    {
        IsReadOnly=true;
        AddHandler(KeyDownEvent,CaptureKey,RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent,CaptureReleasedModifier,RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent,CaptureMouse,RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent,CaptureWheel,RoutingStrategies.Tunnel);
        LostFocus+=(_,_)=>CancelRecording();
        DetachedFromVisualTree+=(_,_)=>CancelRecording();
    }
    public void BeginRecording()
    {
        StopHotkeyEditing.KeyCaptured-=CaptureRegisteredKey;
        StopHotkeyEditing.KeyCaptured+=CaptureRegisteredKey;
        recordingGeneration++;pendingModifier=0;IsRecording=true;Focus();
        CaptureMessage="正在录入：请按键盘键，或在此框内点击鼠标键、滚动滚轮；可带修饰键。";
    }
    public void CancelRecording()
    {
        if(!IsRecording)return;
        StopHotkeyEditing.KeyCaptured-=CaptureRegisteredKey;
        recordingGeneration++;pendingModifier=0;IsRecording=false;CaptureMessage=IdleMessage;
    }
    private void Commit(uint key,KeyModifiers modifiers)
    {
        if(key==0){CaptureMessage="该按键没有可用的 Windows 键值，请重新按键。";return;}
        var flags=(modifiers.HasFlag(KeyModifiers.Control)?2u:0)|(modifiers.HasFlag(KeyModifiers.Alt)?1u:0)|(modifiers.HasFlag(KeyModifiers.Shift)?4u:0)|(modifiers.HasFlag(KeyModifiers.Meta)?8u:0);
        SetCurrentValue(TextProperty,StopHotkey.FromKey(key,flags).Text);
        StopHotkeyEditing.KeyCaptured-=CaptureRegisteredKey;
        recordingGeneration++;pendingModifier=0;IsRecording=false;CaptureMessage="已录入，点击“应用快捷键”后生效；离开设置区域并移开焦点后恢复停止响应。";
    }
    private void CaptureRegisteredKey(uint key,bool released,uint flags)
    {
        // Registered combinations may not produce a normal routed key event.
        // Forward their physical hook event without blocking the hook thread.
        var generation=recordingGeneration;
        Avalonia.Threading.Dispatcher.UIThread.Post(()=>
        {
            if(!IsRecording||generation!=recordingGeneration||TopLevel.GetTopLevel(this) is not Window {IsActive:true})return;
            var modifiers=((flags&2)!=0?KeyModifiers.Control:0)|((flags&1)!=0?KeyModifiers.Alt:0)|((flags&4)!=0?KeyModifiers.Shift:0)|((flags&8)!=0?KeyModifiers.Meta:0);
            if(released){if(pendingModifier==key)Commit(key,modifiers);return;}
            if(StopHotkey.Modifier(key)!=0)pendingModifier=key;else Commit(key,modifiers);
        });
    }
    private void CaptureKey(object? sender,KeyEventArgs e)
    {
        if(!IsRecording)return;
        e.Handled=true;
        var key=(uint)KeyInterop.VirtualKeyFromKey(e.Key);
        if(StopHotkey.Modifier(key)!=0){pendingModifier=key;return;}
        Commit(key,e.KeyModifiers);
    }
    private void CaptureReleasedModifier(object? sender,KeyEventArgs e)
    {
        if(!IsRecording)return;
        e.Handled=true;var key=(uint)KeyInterop.VirtualKeyFromKey(e.Key);
        if(key!=0&&key==pendingModifier)Commit(key,e.KeyModifiers);
    }
    private void CaptureMouse(object? sender,PointerPressedEventArgs e)
    {
        if(!IsRecording)return;
        var key=e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed=>1u,PointerUpdateKind.RightButtonPressed=>2u,
            PointerUpdateKind.MiddleButtonPressed=>4u,PointerUpdateKind.XButton1Pressed=>5u,PointerUpdateKind.XButton2Pressed=>6u,_=>0u
        };
        if(key==0)return;
        e.Handled=true;Commit(key,e.KeyModifiers);
    }
    private void CaptureWheel(object? sender,PointerWheelEventArgs e)
    {
        if(!IsRecording)return;
        if(e.Delta.Y==0&&e.Delta.X==0)return;
        e.Handled=true;Commit(e.Delta.Y>0?0x100u:e.Delta.Y<0?0x101u:e.Delta.X>0?0x103u:0x102u,e.KeyModifiers);
    }
}
