namespace MabiAgent.Windows;

/// <summary>One reversible quantity click, then its observed quantity acknowledgment.</summary>
internal sealed class ScreenQuantityStepper
{
    private long? expected;private double deadline;private int steps;
    public int Next(long? current,long requested,double now)
    {
        if(requested is <1 or >999)throw new InputBlockedException("采购数量需在1至999之间");
        if(expected is{} pending)
        {
            if(current==pending)expected=null;
            else if(now>=deadline)throw new InputBlockedException("采购数量变化未核实");
            else return 0;
        }
        if(current is null)return 0;
        if(current<1||current>9999)throw new InputBlockedException("当前采购数量无效");
        if(current==requested)return 0;
        if(steps>=1024)throw new InputBlockedException("采购数量调整超过上限");
        return current<requested?1:-1;
    }
    public void Issued(long before,int delta,double now)
    {if(expected is not null||delta is not (1 or -1))throw new InputBlockedException("数量按钮尚未核实");expected=checked(before+delta);deadline=now+5;steps++;}
}
