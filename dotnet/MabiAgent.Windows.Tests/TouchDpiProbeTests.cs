using System.Text.Json.Nodes;
using MabiAgent.Windows;
using Xunit;
using Xunit.Abstractions;
namespace MabiAgent.Windows.Tests;

// Explicit orchestration of the existing guarded probes; never a runtime input backend.
public sealed class TouchDpiProbeTests(ITestOutputHelper output)
{
    [Fact] public void ExplicitCurrentDpiPrimitives()
    {
        if(Environment.GetEnvironmentVariable("MABI_TOUCH_DPI_SEQUENCE")!="1")return;
        var dpi=int.Parse(Environment.GetEnvironmentVariable("MABI_TOUCH_EXPECTED_DPI")??throw new InvalidOperationException("需要指定实测DPI"));
        Assert.Contains(dpi,new[]{96,120,144,168});
        var hwnd=(nint)long.Parse(Environment.GetEnvironmentVariable("MABI_BACKGROUND_HWND")??throw new InvalidOperationException("需要当前窗口"));
        var state=Assert.Single(GameWindowDiscovery.List(),w=>w.State.WindowId==hwnd).State;Assert.Equal(dpi,state.Geometry.Dpi);Assert.Equal(1280,state.Geometry.Width);Assert.Equal(960,state.Geometry.Height);
        var names=new[]{"MABI_TOUCH_GAME_PROBE","MABI_TOUCH_FORWARD_PROBE","MABI_TOUCH_CURSOR_RESTORE","MABI_TOUCH_MENU_TARGET","MABI_TOUCH_GAME_MOUSE_COMPETITION","MABI_TOUCH_MAP_GAME","MABI_TOUCH_MAP_MOUSE","MABI_TOUCH_MAP_DIRECTION"};
        var original=names.ToDictionary(n=>n,Environment.GetEnvironmentVariable);var samples=new JsonArray();
        var result=new JsonObject{["at"]=DateTimeOffset.Now.ToString("O"),["dpi"]=dpi,["scale_percent"]=dpi*100/96,["samples"]=samples};
        var path=Path.Combine(RecognitionTests.Root(),"build/reference-optimization-validation.json");
        void Collect(string name,string key,DateTimeOffset began)
        {
            var source=JsonNode.Parse(File.ReadAllText(path))![key];var sample=new JsonObject{["action"]=name};
            if(source?["at"]?.GetValue<string>() is {} at&&DateTimeOffset.Parse(at)>=began)
                foreach(var field in new[]{"at","geometry","target","before_page","after_page","game_response_verified","map_transform","before","after","foreground_unchanged","cursor_unchanged","cursor_visible_after","device_destroyed","auxiliary_windows_closed","error"})sample[field]=source[field]?.DeepClone();
            else sample["result_recorded"]=false;
            samples.Add(sample);
        }
        void Menu(string target)
        {
            Assert.Equal(dpi,new NativeWindowsOps().ReadWindow(hwnd).Geometry.Dpi);Environment.SetEnvironmentVariable("MABI_TOUCH_MENU_TARGET",target);var began=DateTimeOffset.Now;
            try{new OccludedTouchProbeTests(output).ExplicitOccludedGameMenuProbe();}finally{Collect(target,"touch_forward_game",began);}
            Assert.True(samples[^1]!["game_response_verified"]!.GetValue<bool>(),"菜单新画面未通过，不重发");
        }
        void Gesture(string kind)
        {
            Assert.Equal(dpi,new NativeWindowsOps().ReadWindow(hwnd).Geometry.Dpi);Environment.SetEnvironmentVariable("MABI_TOUCH_MAP_GAME",kind);var began=DateTimeOffset.Now;
            try{new MapTouchGestureProbeTests(output).ExplicitGameMapGestureProbe();}finally{Collect(kind,"touch_map_"+kind,began);}
        }
        try
        {
            Environment.SetEnvironmentVariable("MABI_TOUCH_GAME_PROBE","1");Environment.SetEnvironmentVariable("MABI_TOUCH_FORWARD_PROBE","1");Environment.SetEnvironmentVariable("MABI_TOUCH_CURSOR_RESTORE","absolute_same_pixel");
            Environment.SetEnvironmentVariable("MABI_TOUCH_GAME_MOUSE_COMPETITION",null);Environment.SetEnvironmentVariable("MABI_TOUCH_MAP_MOUSE",null);Environment.SetEnvironmentVariable("MABI_TOUCH_MAP_DIRECTION","1");
            Menu("scene.open_skills");Menu("skills.close");Menu("scene.open_map");Gesture("drag");Gesture("pinch");Menu("map.close");
            result["passed"]=true;result["final_page"]="scene";
        }
        catch(Exception e){result["passed"]=false;result["error"]=e.GetType().Name+": "+e.Message;throw;}
        finally
        {
            foreach(var (name,value) in original)Environment.SetEnvironmentVariable(name,value);
            var report=JsonNode.Parse(File.ReadAllText(path))!.AsObject();report["touch_dpi_validation"]??=new JsonObject();report["touch_dpi_validation"]!["samples"]??=new JsonObject();report["touch_dpi_validation"]!["samples"]![dpi.ToString()]=result;
            File.WriteAllText(path,report.ToJsonString(new(){WriteIndented=true}));
        }
    }
}
