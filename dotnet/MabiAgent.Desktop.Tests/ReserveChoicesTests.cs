using MabiAgent.Capture;
using MabiAgent.Desktop.Editors;
using MabiAgent.Windows;
using Xunit;

namespace MabiAgent.Desktop.Tests;

public sealed class ReserveChoicesTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"mabi-reserve-"+Guid.NewGuid().ToString("N"));
    [Fact] public void WarehouseChoicesRefreshWithoutReplacingReserveDraftOrAddingGatheringPoints()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"config/screen_catalog.1280x960.tw.json")))root=root.Parent;
        Assert.NotNull(root);
        var accounts=new AccountItemService(Path.Combine(directory,"accounts"));
        accounts.Record(new(new("a","c","s"),new("a","c","角色"),Enum.GetValues<InventoryContainer>().ToDictionary(c=>c,c=>(IReadOnlyDictionary<uint,long>)new Dictionary<uint,long>{{123,8},{456,9}}),1,1,1,2,null));
        accounts.UpdateNames("a",[new(123,"目录外纪念品")],NameEditOperation.Confirm);
        var vm=new MainViewModel(new Workspace(root!.FullName,directory),new GameWindowSelection(()=>[]));
        vm.InitializeEditorSelfCheck(new EditorRepository(root.FullName,Path.Combine(directory,"editors")),accounts);
        Assert.Equal("目录外纪念品",Assert.Single(vm.ReserveChoices).Name);Assert.Equal("123",vm.ReserveChoices[0].Id);
        vm.SelectedReserveItem=vm.ReserveChoices[0];vm.ReserveQuantity="17";vm.SetMaterialReserveCommand.Execute(null);vm.ReserveQuantity="29";
        void Refresh()=>typeof(MainViewModel).GetMethod("RefreshReserveChoices",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(vm,null);
        accounts.UpdateNames("a",[new(456,"仓库外材料")],NameEditOperation.Confirm);Refresh();
        Assert.Equal(2,vm.ReserveChoices.Count);Assert.Equal("29",vm.ReserveQuantity);Assert.Equal(17,Assert.Single(vm.MaterialReserves).Quantity);Assert.Equal("123",vm.SelectedReserveItem?.Id);
        accounts.UpdateNames("a",[new(123,null)],NameEditOperation.Clear);Refresh();
        Assert.Equal("456",Assert.Single(vm.ReserveChoices).Id);Assert.Null(vm.SelectedReserveItem);Assert.Single(vm.MaterialReserves);
        vm.PrepareClose();
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
