using System.Text.Json.Nodes;
namespace MabiAgent.Windows;

public sealed record InputOptions(string Scheme="touch",bool CenterOffset=true)
{
    public bool IsTouch=>Scheme=="touch";
    public void Validate(){if(Scheme is not("touch" or "mouse"))throw new ArgumentException("请选择后台触控或前台鼠标");}
    public static InputOptions FromJson(JsonObject settings)
    {var result=new InputOptions(settings["input"]?["scheme"]?.GetValue<string>()??"touch",settings["input"]?["center_offset"]?.GetValue<bool>()??true);result.Validate();return result;}
}

public static class InputTransportFactory
{
    public static INativeWindowsOps Create(NativeWindowState target,InputOptions? options=null)
    {options??=new();options.Validate();return options.IsTouch?new TouchInputOps(target):new NativeWindowsOps();}
}

public interface IGuardedPointerGestures
{
    void Click(PixelPoint point,MouseButton button);
    void Drag(PixelPoint from,PixelPoint to,int steps);
    void Scroll(PixelPoint point,PixelBox surface,int steps,bool zoom);
}
