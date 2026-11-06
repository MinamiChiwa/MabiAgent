using System.IO;
using System.Text.Json;
using MabiAgent.Capture;
using MabiAgent.Windows;

namespace MabiAgent.Desktop;

public sealed partial class AutomationService
{
    private volatile bool itemNamesDirectoryRequired;
    public async Task<ClientCatalogUpdate> UpdateItemAssociationsAsync()
    {
        var target = selection.Selected?.State ?? throw new InvalidOperationException("请先在设置中绑定游戏窗口，再更新物品关联");
        var fallback = clientLayout.FallbackDirectory;
        return await Task.Run(() =>
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var identity = NativeWindowsOps.ReadProcessIdentity(checked((uint)target.ProcessId));
            if (identity.CreatedUtcTicks != target.ProcessStartUtcTicks) throw new InvalidDataException("游戏进程已变化，请重新绑定窗口");
            JsonElement catalog;
            try
            {
                var directory = ClientItemCatalogLocation.Resolve(identity.Executable, fallback);
                try { catalog = ClientItemCatalogExtractor.Extract(directory, lifetime.Token); }
                catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException &&
                    !string.IsNullOrWhiteSpace(fallback) && !Path.GetFullPath(directory).Equals(Path.GetFullPath(fallback), StringComparison.OrdinalIgnoreCase))
                { catalog = ClientItemCatalogExtractor.Extract(fallback!, lifetime.Token); }
                itemNamesDirectoryRequired = false;
            }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            {
                itemNamesDirectoryRequired = true;
                throw new InvalidDataException("物品名称更新失败：" + error.Message + "。可在设置的数据同步中填写游戏程序所在目录后重试。", error);
            }
            var after = NativeWindowsOps.ReadProcessIdentity(checked((uint)target.ProcessId));
            if (after != identity || selection.Selected?.State.ProcessId != target.ProcessId || selection.Selected?.State.ProcessStartUtcTicks != target.ProcessStartUtcTicks)
                throw new InvalidDataException("读取期间游戏窗口绑定已变化，请重试");
            lifetime.Token.ThrowIfCancellationRequested();
            var result = Accounts.UpdateClientCatalog(catalog);
            wake.Signal();
            return result;
        }, lifetime.Token);
    }
}
