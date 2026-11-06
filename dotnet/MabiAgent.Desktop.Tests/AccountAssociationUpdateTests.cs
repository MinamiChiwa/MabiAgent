using MabiAgent.Capture;
using MabiAgent.Desktop.Accounts;
using Xunit;

namespace MabiAgent.Desktop.Tests;

public sealed class AccountAssociationUpdateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mabi-account-update-ui-" + Guid.NewGuid().ToString("N"));
    private AccountItemService CreateService()
    {
        var service = new AccountItemService(Path.Combine(directory, "accounts"));
        service.Record(new(new("a", "c", "session"), new("a", "c", "角色"),
            new Dictionary<InventoryContainer, IReadOnlyDictionary<uint, long>>
            {
                [InventoryContainer.Backpack] = new Dictionary<uint, long> { [123] = 7 },
                [InventoryContainer.CharacterStorage] = new Dictionary<uint, long>(),
                [InventoryContainer.AccountStorage] = new Dictionary<uint, long>()
            }, 1, 1, 1, 2, null));
        return service;
    }

    [Fact]
    public async Task UpdateReportsProgressPreventsReentryAndNotifiesHost()
    {
        var completion = new TaskCompletionSource<ClientCatalogUpdate>();
        var calls = 0; var notifications = 0;
        var vm = new AccountItemsViewModel(CreateService(), () => { calls++; return completion.Task; });
        vm.AssociationsChanged += (_, _) => notifications++;
        var running = vm.UpdateAssociationsCommand.ExecuteAsync(null);
        Assert.True(vm.UpdatingAssociations);
        Assert.False(vm.UpdateAssociationsCommand.CanExecute(null));
        Assert.Contains("正在读取", vm.AssociationUpdateStatus);
        vm.Search = "123";
        Assert.Single(vm.ItemsView);
        completion.SetResult(new(5913, 1)); await running;
        Assert.False(vm.UpdatingAssociations); Assert.Equal(1, calls); Assert.Equal(1, notifications);
        Assert.Contains("1 条关联已更新", vm.AssociationUpdateStatus);
        Assert.Equal("123", vm.Search);
    }

    [Fact]
    public async Task PendingEditsPreventUpdateAndFailureKeepsDraftAndRows()
    {
        var calls = 0;
        var vm = new AccountItemsViewModel(CreateService(), () => { calls++; throw new IOException("资源损坏"); });
        vm.Items[0].Name = "草稿";
        await vm.UpdateAssociationsCommand.ExecuteAsync(null);
        Assert.Equal(0, calls); Assert.True(vm.HasDirtyEdits); Assert.Contains("请先保存", vm.Error);
        vm.Refresh(true); vm.IsCellEditing = true;
        await vm.UpdateAssociationsCommand.ExecuteAsync(null); Assert.Equal(0, calls);
        vm.IsCellEditing = false;
        await vm.UpdateAssociationsCommand.ExecuteAsync(null);
        Assert.Equal(1, calls); Assert.Equal("资源损坏", vm.Error); Assert.False(vm.UpdatingAssociations);
        Assert.Equal(7L, Assert.Single(vm.Items).ObservedTotal);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
