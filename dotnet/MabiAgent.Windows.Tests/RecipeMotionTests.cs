using System.Text.Json.Nodes;
using MabiAgent.Workflows;
using Xunit;
namespace MabiAgent.Windows.Tests;

public sealed class RecipeMotionTests
{
    [Theory][InlineData(0)][InlineData(60)][InlineData(255)]
    public void CurrentWoodIdentityIgnoresCountsAndEquipmentLevel(byte value)
    {
        var (session,ops,routes,_)=Create();using(session)
        {
            var port=session.CreateProductionPort("wood_first","identity",new MemoryStore());Assert.True(port.Observe().AtDevice);
            // 本例只改变等级和数量；到场检查点使用已观察到的设备身份。
            session.ConfirmDeviceArrival("wood");
            var pixels=Image("wood-recipe-current.png").CopyPixels();
            foreach(var box in new[]{new PixelBox(739,475,795,500),new PixelBox(944,574,1000,596),new PixelBox(938,728,1001,754)})
                for(var y=box.Top;y<box.Bottom;y++)for(var x=box.Left;x<box.Right;x++){var i=(y*1280+x)*3;pixels[i]=pixels[i+1]=pixels[i+2]=value;}
            ops.Image=new(1280,960,pixels);var view=port.Observe();
            Assert.Equal("wood_first",view.RecipeKey);Assert.True(view.AtDevice);Assert.True(view.CraftAvailable);
            var other=session.CreateProductionPort("catalog_wood_01","wrong-recipe",new MemoryStore()).Observe();
            Assert.Null(other.RecipeKey);Assert.False(other.CraftAvailable);
        }
    }

    [Fact]
    public void RecipeRoundTripsKeepOneDeadlineAcrossPortReconstructionAndUnknownFrames()
    {
        var (session,ops,routes,clock)=Create();using(session)
        {
            var store=new MemoryStore();var recipe=routes.Recipes["catalog_wood_01"];var requested=new ProductionRecipe(recipe.Key,recipe.Device,recipe.DurationSeconds);
            var port=new ScreenProductionPort(session,recipe.Key,"bounded",store,clock);
            port.Observe();port.Navigate("recipe",requested);
            for(var attempt=0;attempt<3;attempt++)
            {
                ops.Image=Image("wood-recipe-current.png");Assert.Null(port.Observe().RecipeKey);port.Navigate("recipe",requested);
                ops.Image=Image("wood-panel-current.png");port.Observe();port.Navigate("recipe",requested);clock.Seconds+=10;
            }
            port=new ScreenProductionPort(session,recipe.Key,"bounded",store,clock);clock.Seconds=44;
            ops.Image=Image("wood-recipe-current.png");Assert.Null(port.Observe().RecipeKey);var clicks=ops.Clicks;
            clock.Seconds=45;ops.Image=new(1280,960,new byte[1280*960*3]);
            Assert.Contains("配方选择超过45秒",Assert.Throws<TimeoutException>(()=>port.Observe()).Message);
            Assert.True(port.CanReleaseForeground());Assert.Equal(clicks,ops.Clicks);Assert.Null(store.Read());
        }
    }

    [Theory][InlineData("recipe",false)][InlineData("recipe_preview",true)]
    public void RecipeDeadlineRequiresDeviceProofOnlyForActualProduction(string destination,bool clears)
    {
        var (session,ops,routes,clock)=Create();using(session)
        {
            var port=new ScreenProductionPort(session,"wood_first","proof",new MemoryStore(),clock);
            port.Observe();port.Navigate(destination,new("wood_first","wood",90));
            session.Pause("manual");session.ResumeExplicit();ops.Image=Image("wood-recipe-current.png");
            var view=port.Observe();Assert.Equal("wood_first",view.RecipeKey);Assert.False(view.AtDevice);
            clock.Seconds=45;ops.Image=new(1280,960,new byte[1280*960*3]);
            if(clears)Assert.False(port.Observe().Stable);else Assert.Throws<TimeoutException>(()=>port.Observe());
        }
    }

    private static RgbImage Image(string name)=>FishingFeedbackTests.ReadImage(Path.Combine(RecognitionTests.Root(),"tests/fixtures/production",name));
    private static (ScreenWorkflowSession Session,FakeOps Ops,ScreenRoutes Routes,Clock Clock) Create()
    {
        var routes=ScreenRoutes.Load(RecognitionTests.Root());var ops=new FakeOps(Image("wood-panel-current.png"));
        var session=new ScreenWorkflowSession(routes,Window(),ops,new EmptyOcr(),new MemoryStore());session.Start();return(session,ops,routes,new());
    }
    private static NativeWindowState Window()=>new(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,1280,960,96),new(0,0,1280,960),1,true,false,true,new(0,0));
    private sealed class Clock:TimeProvider{public double Seconds;public override DateTimeOffset GetUtcNow()=>DateTimeOffset.UnixEpoch.AddSeconds(Seconds);}
    private sealed class EmptyOcr:IOcrEngine{public OcrLine? Recognize(int width,int height,byte[] pixels)=>null;}
    private sealed class MemoryStore:IWorkflowStore{private JsonObject? state;public JsonObject? Read()=>state is null?null:(JsonObject)state.DeepClone();public void Flush(JsonObject value)=>state=(JsonObject)value.DeepClone();}
    private sealed class FakeOps(RgbImage image):INativeWindowsOps,IGuardedWindowTransport
    {
        public RgbImage Image=image;public int Clicks;public bool RequiresForeground=>true;
        public void BindGuard(ForegroundInputGuard guard){}public NativeWindowState ReadWindow(nint hwnd)=>Window();
        public RgbImage CaptureClient(NativeWindowState state)=>Image;public void MoveClient(nint hwnd,PixelPoint position){}
        public void MouseButton(MouseButton button,bool down){if(down)Clicks++;}public void Wheel(int delta){}public void Key(ushort key,bool down,bool unicode=false){}public bool AnyPhysicalKeyDown()=>false;
    }
}
