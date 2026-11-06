using System.IO;
using Avalonia.Controls;
using MabiAgent.Capture;

namespace MabiAgent.Desktop.Accounts;

/// <summary>Run on the Avalonia UI thread with application resources loaded. Uses only a temporary account directory.</summary>
public static class AccountItemsSelfCheck
{
    public static string[] Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mabi-account-ui-" + Guid.NewGuid().ToString("N"));
        var checks = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }
        try
        {
            var service = new AccountItemService(directory); var empty = new AccountItemsViewModel(service);
            Check(empty.Items.Count == 0 && !Directory.Exists(directory), "账户空状态读取不落盘");
            InventorySnapshot Snapshot(string who) => new(new("account", who, "session"), new("account", who, "角色" + who),
                new Dictionary<InventoryContainer, IReadOnlyDictionary<uint, long>>
                { [InventoryContainer.Backpack] = new Dictionary<uint,long> { [123] = 7, [456] = 20 }, [InventoryContainer.CharacterStorage] = new Dictionary<uint,long>(), [InventoryContainer.AccountStorage] = new Dictionary<uint,long> { [123] = 10 } }, 1, 1, 1, 2, null);
            service.Record(Snapshot("one")); service.Record(Snapshot("two"));
            service.RecordCharacterSlot("account","two",1);service.RecordCharacterSlot("account","one",3);
            var vm = new AccountItemsViewModel(service); var panel = new AccountItemsPanel(vm);
            Check(panel.DataContext == vm && vm.CharacterColumns.Count == 2 && vm.Items.Count == 2, "账户表构造及动态角色列");
            Check(vm.CharacterColumns.Select(c=>c.Id).SequenceEqual(new[]{"two","one"})&&vm.CharacterColumns[0].Label=="1号 · 角色two"&&vm.CharacterColumns[1].Label=="3号 · 角色one","账户角色列按已确认槽位排序并显示抓包名称");
            Check(vm.Items[0].AccountTotal == 24 && vm.Items[0].SharedStorage == 10, "共有仓只汇总一次");
            var grid=panel.FindControl<DataGrid>("ItemsGrid")!;
            var updateButton = panel.FindControl<Button>("UpdateAssociationsButton")!;
            Check(Avalonia.Automation.AutomationProperties.GetName(updateButton) == "更新物品关联" && updateButton.Command == vm.UpdateAssociationsCommand,
                "仓库更新物品关联按钮绑定异步命令");
            Avalonia.Controls.DataGridColumn NumberColumn()=>grid.Columns.Single(c=>Equals(c.Header,"道具编号"));
            uint[] VisibleIds()=>vm.ItemsView.Cast<AccountItemEditRow>().Select(r=>r.ItemId).ToArray();
            panel.SortColumn(NumberColumn());panel.SortColumn(NumberColumn());
            Check(VisibleIds().SequenceEqual(new uint[]{456,123}),"账户编号表头可切换降序");
            vm.Refresh(true);
            Check(VisibleIds().SequenceEqual(new uint[]{456,123}),"账户手动刷新重建列后保留降序");
            panel.SortColumn(NumberColumn());
            Check(VisibleIds().SequenceEqual(new uint[]{123,456}),"账户刷新后再次点击同列表头切换升序");
            panel.SortColumn(NumberColumn());vm.RefreshLive();
            Check(VisibleIds().SequenceEqual(new uint[]{456,123}),"账户实时刷新保留排序方向");
            vm.ResetSortCommand.Execute(null);panel.SortColumn(NumberColumn());
            Check(VisibleIds().SequenceEqual(new uint[]{123,456}),"账户重置排序后首次表头点击恢复升序");
            var characterColumn=grid.Columns.Single(c=>c.Header?.ToString()?.Contains("[one]",StringComparison.Ordinal)==true);
            panel.SortColumn(characterColumn);panel.SortColumn(characterColumn);vm.RefreshLive();
            Check(VisibleIds().SequenceEqual(new uint[]{456,123}),"角色数量列按稳定角色编号保留降序");
            vm.Refresh(true);
            Check(VisibleIds().SequenceEqual(new uint[]{456,123}),"角色数量列手动刷新穿过空列重建仍保留降序");
            vm.ResetSortCommand.Execute(null);
            vm.Items[0].Name = "铁矿石"; vm.Items[1].Name = "原木";
            vm.ConfirmSelectedCommand.Execute(null);
            Check(service.View("account").Items.All(r => r.Name is null), "未选中行不能默认批量关联");
            vm.SetSelection([vm.Items[0]]); vm.ConfirmSelectedCommand.Execute(null);
            Check(service.ResolveVerified("account", "鐵礦石") == 123 && service.ResolveVerified("account", "原木") is null, "只保存显式选中行");
            Check(vm.Items.Single(r => r.ItemId == 456).Name == "原木" && vm.HasDirtyEdits, "批量保存保留其他行草稿");
            vm.Refresh(false); Check(vm.HasDirtyEdits && vm.Error.Length > 0, "刷新不覆盖未保存名称");
            vm.Refresh(true); Check(!vm.HasDirtyEdits, "显式放弃重读账户文件");
            vm.SetSelection([vm.Items[0]]); vm.Search = "无匹配项";
            Check(vm.SelectedCount == 0 && !vm.ClearSelectedCommand.CanExecute(null), "过滤隐藏行后撤销批量选择");
            vm.Search = ""; vm.SetSelection([vm.Items[0]]); vm.ClearSelectedCommand.Execute(null);
            Check(service.View("account").Items.Single(r => r.ItemId == 123).Association == NameAssociation.UserCleared, "清除保留玩家决定");
            var export = Path.Combine(directory, "account-export.csv"); vm.ExportCsv(export);
            Check(vm.LastExportPath == export && File.ReadAllText(export).Contains("角色one [one]", StringComparison.Ordinal), "CSV导出角色列与实际路径");
            var files = service.Files("account");
            File.WriteAllLines(files.Inventory, File.ReadAllLines(files.Inventory).Where(l => !l.Contains("full_coverage", StringComparison.Ordinal)));
            vm.Refresh(true); Check(vm.Items.All(r => r.AccountTotal is null), "缺覆盖数量保持未知");
            return checks.ToArray();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
