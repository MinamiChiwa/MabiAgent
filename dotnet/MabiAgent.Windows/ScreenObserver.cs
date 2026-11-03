namespace MabiAgent.Windows;
/// <summary>Recognizes one current capture under its independently resolved session scope.</summary>
public sealed class ScreenObserver(Func<ClientCapture,SessionScope?> resolveScope,Func<ClientCapture,PageView> recognize)
{
    private readonly object gate=new();
    public string? StabilityReason{get;private set;}
    public void Reset(){lock(gate)StabilityReason=null;}
    public UiObservation Observe(ClientCapture capture)
    {
        lock(gate)
        {
            if(!double.IsFinite(capture.CapturedAt)||capture.Frame is {} frame&&(!frame.IsCurrent||frame.SourceTime!=capture.CapturedAt))
            {Reset();throw new InputBlockedException("预览、缓存或源时间不符的图像不能参与输入识别");}
            var scope=resolveScope(capture);
            if(scope is null){Reset();throw new InputBlockedException("当前会话身份未核实");}
            var view=recognize(capture);
            if(resolveScope(capture)!=scope){Reset();throw new InputBlockedException("识图期间会话变化");}
            var native=capture.Native;
            var usable=native.Visible&&!native.Minimized&&native.Enabled&&view.Page!="unknown"&&!view.Modal;
            StabilityReason=usable?null:!native.Visible||native.Minimized||!native.Enabled?"窗口不可用":view.Modal?"模态页面":"页面未知";
            return new(capture.Image.Sha256,capture.CapturedAt,scope,view,usable,true,capture.Frame,capture.ResourceRevision);
        }
    }
}
