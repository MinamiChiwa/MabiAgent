using System.Text.Json.Nodes;
using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Windows.Tests;

public sealed class ProductionCollectGuardTests
{
    [Fact]
    public void ExtraForeignReadySlotRejectsPersistedTargetsBeforeClick()
    {
        using var test=new CollectCase([0],[0]);
        test.ShowReady(0);
        var before=test.Port.Observe();
        Assert.Equal(new[]{0},before.ReadySlots);

        test.ShowReady(0,1);
        var current=test.ObserveReady();
        Assert.Equal(before.EmptySlots,current.EmptySlots);

        Assert.Throws<InputRejectedBeforeDispatchException>(()=>test.Port.Click("collect",test.Recipe));
        Assert.Empty(test.Native.Clicks);
        Assert.Equal(0,test.Journal.Read()?["intents"]?.AsObject().Count??0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactReadyTargetsOwnedByBatchOrPeersCanBeCollected(bool peerOwnsSecondSlot)
    {
        using var test=new CollectCase([0,1],peerOwnsSecondSlot?[0]:[0,1],peerOwnsSecondSlot);
        test.ObserveReady();

        test.Port.Click("collect",test.Recipe);

        test.AssertCollected();
    }

    [Theory][InlineData(true)][InlineData(false)]
    public void UnknownReadyTargetsCanBeCollectedRegardlessOfLegacyApproval(bool validApproval)
    {
        using var test=new CollectCase([0,1],[0],approvalLifetime:validApproval?3600:-3600);
        test.ObserveReady();test.Port.Click("collect",test.Recipe);test.AssertCollected();
    }

    private sealed class CollectCase:IDisposable
    {
        private readonly ScreenRoutes routes;
        private readonly DeviceRoute device;
        private readonly int[] emptySlots;
        public readonly CollectOps Native;
        public readonly MemoryStore Journal=new();
        public readonly ScreenWorkflowSession Session;
        public readonly ScreenProductionPort Port;
        public readonly ProductionRecipe Recipe;

        public CollectCase(int[] targets,int[] owned,bool peerOwnsSecondSlot=false,double? approvalLifetime=null)
        {
            routes=ScreenRoutes.Load(RecognitionTests.Root());routes.Configuration["notifications"]=new JsonObject();
            var route=routes.Recipes["iron_ingot_from_iron_ore"];device=routes.Devices[route.Device];
            routes.Profile.Definition.Remove("page_variants");routes.Profile.Definition.Remove("coordinate_control_targets");
            routes.Profile.Definition["pages"]=new JsonObject
            {
                [device.QueuePage]=new JsonObject{["anchors"]=new JsonArray(device.ArrivalAnchors.Select(a=>(JsonNode?)JsonValue.Create(a)).ToArray())}
            };
            emptySlots=Enumerable.Range(2,device.Slots.Count-2).ToArray();
            var pending=new JsonObject{["kind"]="collect",["collection_id"]="collect-once",["before_empty"]=Slots(emptySlots),["target_slots"]=Slots(targets)};
            if(peerOwnsSecondSlot)pending["collection_owners"]=new JsonObject{["peer-batch"]=Slots(1)};
            if(approvalLifetime is {} lifetime)pending["collect_all_approval"]=new JsonObject
            {
                ["source"]="user_explicit",["device"]=device.Key,["expires_at"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d+lifetime
            };
            var store=new MemoryStore(new JsonObject{["owned_slots"]=Slots(owned),["pending"]=pending});
            Native=new(Frame(0,1));
            Session=new(routes,Window(),Native,new EmptyOcr(),Journal);Session.Start();
            Port=Session.CreateProductionPort(route.Key,"collect-guard",store);Recipe=new(route.Key,route.Device,route.DurationSeconds);
        }

        public void ShowReady(params int[] slots)=>Native.CurrentImage=Frame(slots);

        public ProductionFrame ObserveReady()
        {
            var frame=Port.Observe();
            Assert.Equal(device.QueuePage,frame.Page);Assert.True(frame.Stable);Assert.True(frame.AtDevice);Assert.True(frame.CollectAvailable);
            Assert.Equal(new[]{0,1},frame.ReadySlots);Assert.Equal(emptySlots,frame.EmptySlots);
            return frame;
        }

        public void AssertCollected()
        {
            var point=Assert.Single(Native.Clicks);
            Assert.True(Session.LastObservation!.View.TargetRegions!["production.collect"].Contains(point));
            var intent=Assert.Single(Journal.Read()!["intents"]!.AsObject());
            Assert.Equal("production.collect",intent.Value!["target"]!.GetValue<string>());
        }

        private RgbImage Frame(params int[] ready)
        {
            var pixels=Stamp(routes.Profile,device.ArrivalAnchors.Concat([device.CollectAnchors[0]]).ToArray()).CopyPixels();
            for(var slot=0;slot<device.Slots.Count;slot++)
            {
                var box=device.Slots[slot];var side=box.Width;var center=(side-1)/2d;
                for(var y=0;y<side;y++)for(var x=0;x<side;x++)
                {
                    var radius=Math.Sqrt(Math.Pow(x-center,2)+Math.Pow(y-center,2))/side;
                    var rim=radius>.41&&radius<.47;
                    // Slots 0 and 1 stay occupied when the second slot becomes ready.
                    // Texture distinguishes occupied interiors from the empty grey-rim slots.
                    byte value=rim?(byte)100:slot<2&&radius<.35&&((x/4+y/4)%2==0)?(byte)210:(byte)20;
                    var p=((box.Top+y)*1280+box.Left+x)*3;
                    if(rim&&ready.Contains(slot)){pixels[p]=20;pixels[p+1]=180;pixels[p+2]=200;}
                    else pixels[p]=pixels[p+1]=pixels[p+2]=value;
                }
            }
            return new(1280,960,pixels);
        }

        public void Dispose()=>Session.Dispose();
    }

    private static JsonArray Slots(params int[] values)=>new(values.Select(v=>(JsonNode?)JsonValue.Create(v)).ToArray());

    private static RgbImage Stamp(NavigationProfile profile,params string[] names)
    {
        var pixels=new byte[1280*960*3];foreach(var name in names){var template=profile.Templates[name];var source=template.Image.CopyPixels();for(var y=0;y<template.Image.Height;y++)Array.Copy(source,y*template.Image.Width*3,pixels,((template.Box.Top+y)*1280+template.Box.Left)*3,template.Image.Width*3);}return new(1280,960,pixels);
    }

    private static NativeWindowState Window()=>new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,1280,960,96),new(0,0,1280,960),1,true,false,true,new(0,0));

    private sealed class EmptyOcr:IOcrEngine
    {
        public OcrLine? Recognize(int width,int height,byte[] grayPixels)=>null;
    }

    private sealed class MemoryStore(JsonObject? initial=null):IWorkflowStore
    {
        private JsonObject? value=initial;
        public JsonObject? Read()=>value is null?null:(JsonObject)value.DeepClone();
        public void Flush(JsonObject state)=>value=(JsonObject)state.DeepClone();
    }

    private sealed class CollectOps(RgbImage image):INativeWindowsOps,IGuardedWindowTransport,IGuardedPointerGestures
    {
        public RgbImage CurrentImage=image;
        public List<PixelPoint> Clicks{get;}=[];
        public bool RequiresForeground=>true;
        public void BindGuard(ForegroundInputGuard guard){}
        public NativeWindowState ReadWindow(nint hwnd)=>Window();
        public RgbImage CaptureClient(NativeWindowState state)=>CurrentImage;
        public void Click(PixelPoint point,MouseButton button)=>Clicks.Add(point);
        public void MouseButton(MouseButton button,bool down){if(down)throw new InvalidOperationException("unexpected mouse down");}
        public void MoveClient(nint hwnd,PixelPoint point)=>throw new InvalidOperationException("unexpected move");
        public void Drag(PixelPoint from,PixelPoint to,int steps)=>throw new InvalidOperationException("unexpected drag");
        public void Scroll(PixelPoint point,PixelBox surface,int steps,bool zoom)=>throw new InvalidOperationException("unexpected scroll");
        public void Wheel(int delta)=>throw new InvalidOperationException("unexpected wheel");
        public void Key(ushort key,bool down,bool unicode=false)=>throw new InvalidOperationException("unexpected key");
        public bool AnyPhysicalKeyDown()=>false;
    }
}
