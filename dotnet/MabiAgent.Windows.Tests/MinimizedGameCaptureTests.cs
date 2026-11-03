using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using MabiAgent.Windows;
using Xunit;
namespace MabiAgent.Windows.Tests;

public sealed class MinimizedGameCaptureTests
{
    [Fact] public void ExplicitCurrentWindowCaptureBoundaryWithoutGameInput()
    {
        if(Environment.GetEnvironmentVariable("MABI_MINIMIZED_PROBE")!="1")return;
        var hwnd=(nint)long.Parse(Environment.GetEnvironmentVariable("MABI_BACKGROUND_HWND")??throw new InvalidOperationException("需指定已观察的游戏窗口"));
        var selected=Assert.Single(GameWindowDiscovery.List(),w=>w.State.WindowId==hwnd);var expected=selected.State;Assert.False(expected.Minimized);Assert.NotEqual(hwnd,expected.ForegroundId);
        var placement=new Placement{Size=(uint)Marshal.SizeOf<Placement>()};Assert.True(GetWindowPlacement(hwnd,ref placement));Assert.Contains(placement.Command,new uint[]{1,4,8});
        var ops=new NativeWindowsOps();var samples=new JsonArray();var result=new JsonObject{["at"]=DateTimeOffset.UtcNow.ToString("O"),["game_input_sent"]=false,["samples"]=samples,["foreground_before"]=(long)expected.ForegroundId,["before"]=WindowMessageDiagnostics.Read(expected)};
        void VerifyIdentity()
        {
            Assert.True(IsWindow(hwnd));Assert.NotEqual(0u,GetWindowThreadProcessId(hwnd,out var pid));Assert.Equal((uint)expected.ProcessId,pid);
            var identity=NativeWindowsOps.ReadProcessIdentity(pid);Assert.Equal(expected.ProcessStartUtcTicks,identity.CreatedUtcTicks);Assert.Equal(expected.Executable,identity.Executable);
        }
        NativeWindowState State(){VerifyIdentity();return ops.ReadWindow(hwnd);}
        void Capture(string phase,int count)
        {
            for(var i=0;i<count;i++)
            {
                Thread.Sleep(400);VerifyIdentity();var entry=new JsonObject{["phase"]=phase,["minimized"]=IsIconic(hwnd)};samples.Add(entry);
                if(GetClientRect(hwnd,out var client))entry["client_size"]=new JsonArray(client.Right-client.Left,client.Bottom-client.Top);
                try{var state=State();entry["geometry"]=new JsonArray(state.Geometry.X,state.Geometry.Y,state.Geometry.Width,state.Geometry.Height);var image=ops.CaptureCurrentClientFrame(state);entry["sha256"]=image.Image.Sha256;entry["all_black"]=image.Image.CopyPixels().All(p=>p==0);entry["backend"]=image.Evidence.Backend;}
                catch(Exception error){entry["error"]=error.GetType().Name+": "+error.Message;}
            }
        }
        var attempted=false;
        try
        {
            Capture("background",2);attempted=true;VerifyIdentity();Assert.True(ShowWindowAsync(hwnd,7));Assert.True(SpinWait.SpinUntil(()=>IsIconic(hwnd),3000));Capture("minimized",3);
            using var guard=new ForegroundInputGuard(expected,new("minimized-capture-only",null,null),new WindowMessageInputOps(expected),_=>throw new InvalidOperationException("不应进行输入识别"));
            Assert.Throws<InputBlockedException>(guard.Start);result["guard_blocks_unvalidated_minimized_input"]=true;
        }
        finally
        {
            try
            {
                if(attempted)
                {
                    // Minimized windows can have no client area. Recovery must not require ReadWindow/Capture.
                    VerifyIdentity();if(IsIconic(hwnd)){Assert.True(ShowWindowAsync(hwnd,4));Assert.True(SpinWait.SpinUntil(()=>!IsIconic(hwnd),3000));}
                    result["foreground_immediately_after_restore"]=(long)GetForegroundWindow();
                    // Some games activate themselves on restore; return only focus taken by this game.
                    if(GetForegroundWindow()==hwnd&&IsWindow(expected.ForegroundId))result["returned_previous_foreground"]=SetForegroundWindow(expected.ForegroundId);
                }
                Capture("restored_background",2);var final=State();result["after"]=WindowMessageDiagnostics.Read(final);result["geometry_restored"]=final.Geometry==expected.Geometry;result["foreground_unchanged"]=final.ForegroundId==expected.ForegroundId;result["cursor_unchanged"]=final.Cursor==expected.Cursor;
            }
            catch(Exception error){result["restore_error"]=error.GetType().Name+": "+error.Message;throw;}
            finally
            {
                var path=Path.Combine(RecognitionTests.Root(),"build/reference-optimization-validation.json");var report=File.Exists(path)?JsonNode.Parse(File.ReadAllText(path))!.AsObject():new JsonObject();report["minimized_capture"]=result;File.WriteAllText(path,report.ToJsonString(new(){WriteIndented=true}));
            }
        }
        Assert.True(result["geometry_restored"]!.GetValue<bool>());Assert.True(result["foreground_unchanged"]!.GetValue<bool>());
    }
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct Placement{public uint Size,Flags,Command;public Point Min,Max;public Rect Normal;}
    [DllImport("user32.dll")]private static extern bool GetWindowPlacement(nint hwnd,ref Placement placement);
    [DllImport("user32.dll")]private static extern bool ShowWindowAsync(nint hwnd,int command);
    [DllImport("user32.dll")]private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")]private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll")]private static extern bool GetClientRect(nint hwnd,out Rect rect);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint hwnd);
}
