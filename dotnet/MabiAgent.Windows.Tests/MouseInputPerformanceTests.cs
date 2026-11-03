using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;
namespace MabiAgent.Windows.Tests;

[CollectionDefinition("Native process metadata",DisableParallelization=true)]
public sealed class NativeProcessMetadataCollection {}

[Collection("Native process metadata")]
public sealed class MouseInputPerformanceTests(ITestOutputHelper output)
{
    [Fact] public void CurrentProcessIdentityMatchesRuntimeWithoutRetainingHandles()
    {
        if(!OperatingSystem.IsWindows())return;
        using var process=Process.GetCurrentProcess();var expected=process.MainModule!.FileName;var created=process.StartTime.ToUniversalTime().Ticks;
        var first=NativeWindowsOps.ReadProcessIdentity((uint)process.Id);
        Assert.Equal(expected,first.Executable,StringComparer.OrdinalIgnoreCase);Assert.Equal(created,first.CreatedUtcTicks);
        process.Refresh();var handles=process.HandleCount;
        for(var i=0;i<64;i++)Assert.Equal(first,NativeWindowsOps.ReadProcessIdentity((uint)process.Id));
        process.Refresh();Assert.InRange(process.HandleCount-handles,-32,32);
        Assert.Throws<InputBlockedException>(()=>NativeWindowsOps.ReadProcessIdentity(0));
        Assert.Throws<System.ComponentModel.Win32Exception>(()=>NativeWindowsOps.ReadProcessIdentity(uint.MaxValue));
    }
    // Explicit read-only probe: no capture, activation, mouse or keyboard input.
    [Fact] public void MeasureLiveWindowValidation()
    {
        var stage=Environment.GetEnvironmentVariable("MABI_INPUT_BENCHMARK");
        if(stage is not ("baseline" or "optimized"))return;
        var hwnd=(nint)long.Parse(Environment.GetEnvironmentVariable("MABI_INPUT_BENCHMARK_HWND")??throw new InvalidOperationException("需要明确指定只读窗口"));
        var ops=new NativeWindowsOps();var initial=ops.ReadWindow(hwnd);
        using var process=Process.GetProcessById(initial.ProcessId);
        Assert.Equal(process.MainModule!.FileName,initial.Executable,StringComparer.OrdinalIgnoreCase);
        Assert.Equal(process.StartTime.ToUniversalTime().Ticks,initial.ProcessStartUtcTicks);
        for(var i=0;i<5;i++)ops.ReadWindow(hwnd);
        var samples=new JsonArray();var allocated=GC.GetAllocatedBytesForCurrentThread();var cpu=Process.GetCurrentProcess();var startCpu=cpu.TotalProcessorTime;
        for(var i=0;i<80;i++)
        {
            var sw=Stopwatch.StartNew();var actual=ops.ReadWindow(hwnd);sw.Stop();
            Assert.Equal(initial.ProcessId,actual.ProcessId);Assert.Equal(initial.ProcessStartUtcTicks,actual.ProcessStartUtcTicks);Assert.Equal(initial.Executable,actual.Executable);Assert.Equal(initial.Geometry,actual.Geometry);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        var bytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
        var reportPath=Path.Combine(RecognitionTests.Root(),"build/runtime-mouse-performance.json");
        var report=File.Exists(reportPath)?JsonNode.Parse(File.ReadAllText(reportPath))!.AsObject():new JsonObject();
        var identity=new JsonObject{["window"]=hwnd.ToInt64(),["pid"]=initial.ProcessId,["created_ticks"]=initial.ProcessStartUtcTicks,["executable"]=initial.Executable,["geometry"]=System.Text.Json.JsonSerializer.SerializeToNode(initial.Geometry)};
        if(stage=="optimized"&&report["baseline"] is {} baseline)Assert.True(JsonNode.DeepEquals(baseline["identity"],identity),"需同一个未移动窗口比较");
        report[stage]=new JsonObject{["recorded_utc"]=DateTimeOffset.UtcNow.ToString("O"),["game_input"]=false,["identity"]=identity,["samples_ms"]=samples,["allocated_bytes"]=bytes,["cpu_ms"]=(cpu.TotalProcessorTime-startCpu).TotalMilliseconds};
        File.WriteAllText(reportPath,report.ToJsonString(new(){WriteIndented=true}));
        var ordered=samples.Select(s=>s!.GetValue<double>()).Order().ToArray();
        output.WriteLine($"{stage}: n={ordered.Length}, mean={ordered.Average():F4}ms, p95={ordered[(int)Math.Ceiling(ordered.Length*.95)-1]:F4}ms, allocated={bytes/ordered.Length}B/read");
    }
}
