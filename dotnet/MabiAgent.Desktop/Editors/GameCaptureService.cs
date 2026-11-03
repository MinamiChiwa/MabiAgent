using Avalonia;
using Avalonia.Media.Imaging;
using MabiAgent.Windows;
namespace MabiAgent.Desktop.Editors;
// One explicit screenshot grants one activation attempt, never clicks or keys.
public sealed class SelectedGameCaptureService(GameWindowSelection selection,INativeWindowsOps ops,Func<bool>? isRunning=null):ILiveTargetCaptureService
{
    public async Task<EditorTargetSession> PrepareSelectionAsync(CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(isRunning?.Invoke()==true)throw new InputBlockedException("请先暂停运行，再定位锚点");
        var selected=selection.Selected??throw new InputBlockedException("请先选择游戏窗口");
        var before=ops.ReadWindow(selected.State.WindowId);Verify(selected.State,before);
        if(before.Minimized)throw new InputBlockedException("请先还原游戏窗口");
        if(before.ForegroundId!=before.WindowId){NativeWindowActivation.Activate(before);await Task.Delay(150,cancellationToken);}
        var state=ops.ReadWindow(before.WindowId);Verify(before,state);
        if(state.ForegroundId!=state.WindowId)throw new InputBlockedException("游戏未在前台");
        nint overlay=0;
        void Validate()
        {
            if(isRunning?.Invoke()==true)throw new InputBlockedException("运行期间不能编辑锚点");
            var current=ops.ReadWindow(state.WindowId);Verify(state,current);
            if(current.ForegroundId!=state.WindowId&&(overlay==0||current.ForegroundId!=overlay))throw new InputBlockedException("框选已失去前台，请重新定位");
            if(current.Geometry!=state.Geometry||current.WindowRect!=state.WindowRect||selection.Selected?.State.WindowId!=state.WindowId)
                throw new InputBlockedException("框选期间窗口已移动，请重新框选");
        }
        Bitmap Capture()
        {
            Validate();var image=ops.CaptureClient(state);Validate();
            return ImagePixels.FromRgb(image.Width,image.Height,image.CopyPixels());
        }
        return new(state.Geometry,Validate,Capture,handle=>overlay=handle);
    }
    public async Task<Bitmap> CaptureAsync(CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(isRunning?.Invoke()==true)throw new InputBlockedException("请先暂停运行，再截图编辑锚点");
        var selected=selection.Selected??throw new InputBlockedException("请先选择游戏窗口");var before=ops.ReadWindow(selected.State.WindowId);Verify(selected.State,before);
        if(before.Minimized)throw new InputBlockedException("请先还原游戏窗口");
        if(before.ForegroundId!=before.WindowId){cancellationToken.ThrowIfCancellationRequested();NativeWindowActivation.Activate(before);await Task.Delay(150,cancellationToken);}
        var state=ops.ReadWindow(before.WindowId);Verify(before,state);if(state.ForegroundId!=state.WindowId)throw new InputBlockedException("游戏未在前台");
        cancellationToken.ThrowIfCancellationRequested();var image=ops.CaptureClient(state);var after=ops.ReadWindow(state.WindowId);Verify(state,after);if(after.ForegroundId!=after.WindowId||after.Geometry!=state.Geometry||after.WindowRect!=state.WindowRect||selection.Selected?.State.WindowId!=state.WindowId)throw new InputBlockedException("截图期间窗口或选择已变化");
        return ImagePixels.FromRgb(image.Width,image.Height,image.CopyPixels());
    }
    static void Verify(NativeWindowState expected,NativeWindowState actual)
    {
        if(!GameWindowSelection.IsGame(actual)||actual.WindowId!=expected.WindowId||actual.ProcessId!=expected.ProcessId||actual.ProcessStartUtcTicks!=expected.ProcessStartUtcTicks||!string.Equals(actual.Executable,expected.Executable,StringComparison.OrdinalIgnoreCase))
            throw new InputBlockedException("游戏窗口身份已变化，请重新选择");
        if(actual.Minimized||!actual.Enabled)throw new InputBlockedException("请先还原并关闭游戏弹窗");
    }

}
// Existing native OCR is single-line. This reports only a recognized line; it
// does not claim full-image text detection or successful removal of every Lv.
public sealed class OnnxLineAnchorTextDetector(IOcrEngine engine):IAnchorTextDetector
{
    public IReadOnlyList<AnchorText> Detect(Bitmap image)
    {
        if(image.PixelSize.Width<=0||image.PixelSize.Height<=0||(long)image.PixelSize.Width*image.PixelSize.Height>1_048_576||image.PixelSize.Height>160)return [];
        var pixels=ImagePixels.Gray(image);var result=engine.Recognize(image.PixelSize.Width,image.PixelSize.Height,pixels);
        return result is null||result.Confidence<.6?[]:[new(result.Text,new PixelRect(0,0,image.PixelSize.Width,image.PixelSize.Height))];
    }
}

