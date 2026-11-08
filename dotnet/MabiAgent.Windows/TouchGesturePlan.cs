using System.Diagnostics;
using System.Text.Json.Nodes;
using MabiAgent.Windows;
namespace MabiAgent.Windows;

internal sealed class LocalTouchFramesUnavailableException(string message):Exception(message);

internal sealed record TouchGesturePlan(string Kind,PixelPoint[][] Points,int ReleaseDelayMilliseconds=0)
{
    internal int Steps=>Points.Length-1;
    internal int ContactCount=>Points[0].Length;
    internal int MessageCount=>Steps+2*ContactCount;
    internal static TouchGesturePlan Create(string kind,PixelPoint center,int direction=1)
    {
        if(kind is not ("drag" or "pinch")||direction is not (-1 or 1))throw new ArgumentException("未知手势");
        return new(kind,Enumerable.Range(0,7).Select(i=>kind=="drag"?new[]{new PixelPoint(center.X+direction*120*i/6,center.Y+direction*40*i/6)}:
            new[]{new PixelPoint(center.X-(direction==1?60+90*i/6:150-90*i/6),center.Y),new PixelPoint(center.X+(direction==1?60+90*i/6:150-90*i/6),center.Y)}).ToArray());
    }
    internal TouchGesturePlan Screen(ClientGeometry geometry)=>new(Kind,Points.Select(frame=>frame.Select(p=>new PixelPoint(checked(geometry.X+p.X),checked(geometry.Y+p.Y))).ToArray()).ToArray(),ReleaseDelayMilliseconds);
    internal PixelBox Bounds=>new(Points.SelectMany(p=>p).Min(p=>p.X)-12,Points.SelectMany(p=>p).Min(p=>p.Y)-12,Points.SelectMany(p=>p).Max(p=>p.X)+12,Points.SelectMany(p=>p).Max(p=>p.Y)+12);
    internal void Generate(SyntheticTouchDevice probe,nint hwnd,Action check)
    {
        check();probe.Begin();check();probe.Down(Points[0],hwnd);
        foreach(var frame in Points.Skip(1)){Thread.Sleep(20);check();probe.Move(frame);}
        Thread.Sleep(20);check();probe.Up();probe.Dispose();
    }
    internal List<TouchEnvelope> Collect(TouchInputWindow cover)
    {
        var result=new List<TouchEnvelope>();var timer=Stopwatch.StartNew();var ups=new HashSet<uint>();
        do
        {
            while(cover.Touches.TryDequeue(out var envelope))
            {
                result.Add(envelope);
                foreach(var contact in envelope.Decoded["contacts"]!.AsArray())if((contact!["flags"]!.GetValue<uint>()&7)==4)ups.Add(contact["id"]!.GetValue<uint>());
            }
            if(ups.Count>=ContactCount||result.Count>MessageCount)break;
            Thread.Sleep(2);
        }while(timer.ElapsedMilliseconds<250);
        try{if(result.Count==0)throw new LocalTouchFramesUnavailableException($"触控帧不完整：0/{MessageCount}，手势 {Kind}");Validate(result);return result;}
        catch{foreach(var envelope in result)envelope.Dispose();throw;}
    }
    // Windows can coalesce UPDATE frames. Boundaries and coordinates must still form one complete planned gesture.
    internal void Validate(IReadOnlyList<TouchEnvelope> messages)
    {
        if(messages.Count<2*ContactCount||messages.Count>MessageCount)throw new InputBlockedException($"触控帧不完整：{messages.Count}/{MessageCount}，手势 {Kind}");
        var ids=new Dictionary<uint,int>();var released=new HashSet<uint>();var lastStep=0;
        foreach(var message in messages)
        {
            var contacts=message.Decoded["contacts"]!.AsArray();var starting=ids.Count<ContactCount;
            var ending=contacts.Any(c=>(c!["flags"]!.GetValue<uint>()&7)==4)||released.Count>0;
            if(starting&&ending||contacts.Count!=(starting?ids.Count+1:ContactCount-released.Count))throw new InputBlockedException("触点数量或边界不符");
            var transitions=0;
            foreach(var node in contacts)
            {
                var c=node!;var id=c["id"]!.GetValue<uint>();var flags=c["flags"]!.GetValue<uint>()&7;
                if(flags==2&&starting)
                {
                    var x=c["screen_hundredths"]![0]!.GetValue<int>();var y=c["screen_hundredths"]![1]!.GetValue<int>();
                    var index=Array.FindIndex(Points[0],p=>p.X*100==x&&p.Y*100==y);
                    if(index<0||ids.ContainsKey(id)||ids.ContainsValue(index))throw new InputBlockedException("触点按下来源不符");ids.Add(id,index);transitions++;
                }
                else if(flags==4&&ending){if(!ids.ContainsKey(id)||!released.Add(id))throw new InputBlockedException("触点释放不符");transitions++;}
                else if(flags!=1||released.Contains(id))throw new InputBlockedException("触控状态不连续");
                if(!ids.ContainsKey(id))throw new InputBlockedException("触点ID不符");
            }
            if((starting||ending)&&transitions!=1)throw new InputBlockedException("触点边界状态不完整");
            bool Matches(int step)=>contacts.All(c=>Points[step][ids[c!["id"]!.GetValue<uint>()]] is var p&&p.X*100==c["screen_hundredths"]![0]!.GetValue<int>()&&p.Y*100==c["screen_hundredths"]![1]!.GetValue<int>());
            var matched=starting?Matches(0)?0:-1:ending?Matches(Steps)?Steps:-1:Enumerable.Range(lastStep,Steps-lastStep+1).FirstOrDefault(Matches,-1);
            if(matched<0)throw new InputBlockedException("触点坐标偏离轨迹或倒退");lastStep=matched;
        }
        if(ids.Count!=ContactCount||released.Count!=ContactCount)throw new InputBlockedException("触点生命周期未闭合");
    }
}
