using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace MabiAgent.Windows;

public sealed record StopHotkey(uint Modifiers,uint Key,string Text)
{
    public const string Default="Ctrl+Alt+F10";
    public bool IsMouse=>Key is 1 or 2 or 4 or 5 or 6 or >=0x100 and <=0x103;
    public bool UsesRegistration=>!IsMouse&&Modifier(Key)==0&&Key!=0x7b;
    public static uint Modifier(uint key)=>key switch{0x10 or 0xa0 or 0xa1=>4,0x11 or 0xa2 or 0xa3=>2,0x12 or 0xa4 or 0xa5=>1,0x5b or 0x5c=>8,_=>0};
    private static readonly Dictionary<uint,string> Names=new()
    {
        [1]="MouseLeft",[2]="MouseRight",[4]="MouseMiddle",[5]="MouseX1",[6]="MouseX2",
        [0x100]="WheelUp",[0x101]="WheelDown",[0x102]="WheelLeft",[0x103]="WheelRight",
        [8]="Backspace",[9]="Tab",[13]="Enter",[0x13]="Pause",[0x14]="CapsLock",[0x1b]="Escape",[0x20]="Space",
        [0x21]="PageUp",[0x22]="PageDown",[0x23]="End",[0x24]="Home",[0x25]="Left",[0x26]="Up",[0x27]="Right",[0x28]="Down",
        [0x2c]="PrintScreen",[0x2d]="Insert",[0x2e]="Delete",[0x5b]="LeftWin",[0x5c]="RightWin",[0x5d]="Apps",
        [0x6a]="Multiply",[0x6b]="Add",[0x6c]="Separator",[0x6d]="Subtract",[0x6e]="Decimal",[0x6f]="Divide",[0x90]="NumLock",[0x91]="ScrollLock",
        [0xa0]="LeftShift",[0xa1]="RightShift",[0xa2]="LeftCtrl",[0xa3]="RightCtrl",[0xa4]="LeftAlt",[0xa5]="RightAlt",
        [0xba]="Semicolon",[0xbb]="OemPlus",[0xbc]="Comma",[0xbd]="Minus",[0xbe]="Period",[0xbf]="Slash",[0xc0]="Backtick",[0xdb]="LeftBracket",[0xdc]="Backslash",[0xdd]="RightBracket",[0xde]="Quote"
    };
    public static StopHotkey FromKey(uint key,uint modifiers=0)
    {
        if(key==0||key>0x103)throw new ArgumentException("该按键没有可用的 Windows 键值");
        modifiers&=15;modifiers&=~Modifier(key);
        var name=Names.GetValueOrDefault(key)??(key is >=0x30 and <=0x39 or >=0x41 and <=0x5a?((char)key).ToString():key is >=0x70 and <=0x87?"F"+(key-0x6f):key is >=0x60 and <=0x69?"NumPad"+(key-0x60):"VK"+key.ToString("X2"));
        var parts=new List<string>();if((modifiers&2)!=0)parts.Add("Ctrl");if((modifiers&1)!=0)parts.Add("Alt");if((modifiers&4)!=0)parts.Add("Shift");if((modifiers&8)!=0)parts.Add("Win");parts.Add(name);
        return new(modifiers,key,string.Join('+',parts));
    }
    public static StopHotkey Parse(string text)
    {
        var parts=text.Split('+',StringSplitOptions.TrimEntries);
        uint modifiers=0,key=0;string? name=null;
        for(var index=0;index<parts.Length;index++)
        {
            var part=parts[index];
            var modifier=part.ToUpperInvariant() switch{"CTRL" or "CONTROL"=>2u,"ALT"=>1u,"SHIFT"=>4u,"WIN"=>8u,_=>0u};
            if(modifier!=0&&index<parts.Length-1){if((modifiers&modifier)!=0)throw new ArgumentException("快捷键修饰键重复");modifiers|=modifier;continue;}
            if(key!=0)throw new ArgumentException("快捷键只能包含一个主键");
            name=part.ToUpperInvariant();
            if(name.Length==1&&char.IsAsciiLetterOrDigit(name[0]))key=name[0];
            else if(name.StartsWith('F')&&int.TryParse(name.AsSpan(1),out var number)&&number is >=1 and <=24)key=(uint)(111+number);
            else if(name.StartsWith("NUMPAD")&&uint.TryParse(name.AsSpan(6),out var digit)&&digit<=9)key=0x60+digit;
            else if(name.StartsWith("VK")&&uint.TryParse(name.AsSpan(2),System.Globalization.NumberStyles.HexNumber,null,out var vk)&&vk>0&&vk<=0xff)key=vk;
            else key=Names.FirstOrDefault(p=>string.Equals(p.Value,name,StringComparison.OrdinalIgnoreCase)).Key;
            if(key==0)key=name switch{"CTRL" or "CONTROL"=>0xa2u,"ALT"=>0xa4u,"SHIFT"=>0xa0u,"WIN"=>0x5bu,"ESC"=>0x1bu,_=>0u};
            if(key==0)throw new ArgumentException("无法识别该快捷键，请使用录入按钮直接按键");
        }
        if(key==0)throw new ArgumentException("请录入一个键盘键或鼠标键");
        if((modifiers&Modifier(key))!=0)throw new ArgumentException("快捷键修饰键重复");
        return FromKey(key,modifiers);
    }
}

// Register and receive on a dedicated message thread so UI and workflow waits cannot block stop.
public sealed partial class GlobalStopHotkey:IDisposable
{
    private const uint CommandMessage=0x8001;
    private readonly ConcurrentQueue<(Action Action,TaskCompletionSource Completion)> commands=new();
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly object commandGate=new();
    private readonly Action stop;private readonly Action<Exception> report;
    private uint threadId;private int nextId=100,activeId;private volatile bool disposed;
    private StopHotkey? active;
    public string? Active=>active?.Text;
    internal uint ThreadId=>threadId;
    internal int ActiveId=>activeId;
    public GlobalStopHotkey(Action stop,Action<Exception> report)
    {
        this.stop=stop;this.report=report;
        keyboardCallback=KeyboardHook;mouseCallback=MouseHook;
        thread=new(Run){IsBackground=true,Name="MabiAgent global stop"};thread.Start();
    }
    public Task ApplyAsync(string text)
    {
        var binding=StopHotkey.Parse(text);
        return InvokeAsync(()=>
        {
            if(active==binding)return;
            var id=++nextId;
            if(binding.UsesRegistration&&!RegisterHotKey(0,id,binding.Modifiers|0x4000,binding.Key))
                throw new Win32Exception(Marshal.GetLastWin32Error(),"快捷键组合已被占用或无法注册，请更换组合");
            if(activeId!=0)UnregisterHotKey(0,activeId);
            activeId=binding.UsesRegistration?id:0;active=binding;held.Clear();modifierCandidate=0;
        });
    }
    internal async Task InvokeAsync(Action action)
    {
        await ready.Task.ConfigureAwait(false);
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(commandGate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);commands.Enqueue((action,done));
            if(!PostThreadMessage(threadId,CommandMessage,0,0))done.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
        }
        await done.Task.ConfigureAwait(false);
    }
    private void Run()
    {
        try
        {
            threadId=GetCurrentThreadId();PeekMessage(out _,0,0,0,0);InstallHooks();ready.TrySetResult();
            while(true)
            {
                var result=GetMessage(out var message,0,0,0);
                if(result==0)break;if(result<0)throw new Win32Exception(Marshal.GetLastWin32Error());
                if(message.Id==CommandMessage)
                    while(commands.TryDequeue(out var command))
                        try{command.Action();command.Completion.TrySetResult();}catch(Exception e){command.Completion.TrySetException(e);}
                // Registered keyboard messages reserve the combination; physical
                // input is handled by hooks to exclude automation and editing.
                TranslateMessage(ref message);DispatchMessage(ref message);
            }
        }
        catch(Exception e){ready.TrySetException(e);report(e);}
        finally
        {
            lock(commandGate)
            {
                disposed=true;if(activeId!=0)UnregisterHotKey(0,activeId);RemoveHooks();active=null;
                while(commands.TryDequeue(out var command))command.Completion.TrySetException(new ObjectDisposedException(nameof(GlobalStopHotkey)));
            }
        }
    }
    public void Dispose()
    {
        lock(commandGate)
        {
            if(disposed)return;disposed=true;
            if(ready.Task.IsCompletedSuccessfully)PostThreadMessage(threadId,0x12,0,0);
            else _=ready.Task.ContinueWith(_=>PostThreadMessage(threadId,0x12,0,0),TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        if(Thread.CurrentThread!=thread)thread.Join(2000);
    }
    [StructLayout(LayoutKind.Sequential)]private struct Message{public nint Window;public uint Id;public nuint WParam;public nint LParam;public uint Time;public int X,Y;public uint Private;}
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll",SetLastError=true)]private static extern bool RegisterHotKey(nint window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")]private static extern bool UnregisterHotKey(nint window,int id);
    [DllImport("user32.dll",SetLastError=true)]internal static extern bool PostThreadMessage(uint thread,uint message,nuint wParam,nint lParam);
    [DllImport("user32.dll",SetLastError=true)]private static extern int GetMessage(out Message message,nint window,uint first,uint last);
    [DllImport("user32.dll")]private static extern bool PeekMessage(out Message message,nint window,uint first,uint last,uint remove);
}
