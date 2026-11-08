using System.Text.Json.Nodes;
using MabiAgent.Capture;
using MabiAgent.Desktop;
using MabiAgent.Windows;
using MabiAgent.Workflows;
using Xunit;

namespace MabiAgent.Desktop.Tests;

public sealed class DesktopInventorySnapshotTests
{
    [Fact] public void MapTransitionCannotMixCaptureScopesWithinOneInventoryRead()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"config/screen_routes.1280x960.tw.json")))root=root.Parent;
        Assert.NotNull(root);var directory=Path.Combine(Path.GetTempPath(),"mabi-inventory-snapshot-"+Guid.NewGuid().ToString("N"));
        try
        {
            var window=new NativeWindowState(1,3,42,@"C:\Games\mabinogimobile.exe","UnityWndClass",new(0,0,1280,960,96),new(0,0,1280,960),1,true,false,true,new(0,0));
            var raw=new MabiAgent.Capture.InventorySnapshot(new("a","c","capture"),new("a","c","角色"),
                new Dictionary<InventoryContainer,IReadOnlyDictionary<uint,long>>
                {
                    [InventoryContainer.Backpack]=new Dictionary<uint,long>{{123,7},{456,20}},
                    [InventoryContainer.CharacterStorage]=new Dictionary<uint,long>{{123,9}},
                    [InventoryContainer.AccountStorage]=new Dictionary<uint,long>{{456,12}}
                },1,1,1,2,null);
            var accounts=new AccountItemService(directory);accounts.Record(raw);accounts.UpdateNames("a",[new(123,"矿石"),new(456,"原木")],NameEditOperation.Confirm);
            var mapping=new CapturePlanningAdapter(accounts,new Dictionary<long,string>{{9001,"矿石"},{9002,"原木"}});
            var routes=ScreenRoutes.Load(root.FullName);
            using var session=new ScreenWorkflowSession(routes,window,new NoInput(),new NoOcr(),new JsonWorkflowStore(Path.Combine(directory,"intents.json")));
            var target=new CaptureTarget(window.ProcessId,new DateTime(window.ProcessStartUtcTicks,DateTimeKind.Utc));
            var ready=new CaptureStatus(CaptureState.Listening,"ready",target,raw);
            var unavailable=ready with{Inventory=raw with{Error="Connection closed"}};
            var reads=0;
            var type=typeof(AutomationService).Assembly.GetType("MabiAgent.Desktop.DesktopPorts",true)!;
            var ports=Activator.CreateInstance(type,session,(Func<CaptureStatus>)(()=>reads++%2==0?ready:unavailable),window,mapping,new RuntimeCatalog(routes.Configuration),(Func<JsonObject>)(()=>new JsonObject()))!;
            var read=type.GetMethod("Inventory",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance)!;
            MabiAgent.Core.InventorySnapshot Inventory()=>(MabiAgent.Core.InventorySnapshot)read.Invoke(ports,null)!;
            reads=0;
            var inventory=Inventory();
            Assert.Equal(1,reads);Assert.Equal(7,inventory.Query(9001).Quantity);Assert.Equal(20,inventory.Query(9002).Quantity);
            Assert.All(inventory.Observations.Values,row=>Assert.Equal(inventory.Scope,row.Scope));Assert.Equal(2,inventory.Storage.Count);
            var disconnected=Inventory();Assert.Equal(2,reads);Assert.Null(disconnected.Query(9001).Quantity);
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    private sealed class NoOcr:IOcrEngine{public OcrLine? Recognize(int width,int height,byte[] grayPixels)=>throw new InvalidOperationException("库存转换不读取屏幕");}
    private sealed class NoInput:INativeWindowsOps
    {
        public NativeWindowState ReadWindow(nint hwnd)=>throw new InvalidOperationException();
        public RgbImage CaptureClient(NativeWindowState state)=>throw new InvalidOperationException();
        public void MoveClient(nint hwnd,PixelPoint point)=>throw new InvalidOperationException();
        public void MouseButton(MouseButton button,bool down)=>throw new InvalidOperationException();
        public void Wheel(int delta)=>throw new InvalidOperationException();
        public void Key(ushort key,bool down,bool unicode=false)=>throw new InvalidOperationException();
        public bool AnyPhysicalKeyDown()=>throw new InvalidOperationException();
    }
}
