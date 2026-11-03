using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MabiAgent.Capture;

namespace MabiAgent.Desktop.Accounts;

public sealed record AccountChoice(string Id, string Label);
public sealed record AccountCharacterColumn(string Id, string Label, int Index);
public sealed class AccountItemEditRow(AccountItemRow value, IReadOnlyList<AccountCharacterColumn> columns) : ObservableObject
{
    private string? name = value.Name;
    public uint ItemId => value.ItemId;
    public string? Name { get => name; set { if (SetProperty(ref name, value)) OnPropertyChanged(nameof(Dirty)); } }
    public string? OriginalName { get; init; } = value.Name;
    public NameAssociation OriginalAssociation { get; init; } = value.Association;
    public bool Dirty => Name != OriginalName;
    public string Association => OriginalAssociation switch { NameAssociation.UserConfirmed => "已确认", NameAssociation.BuiltinVerified => "已验证", NameAssociation.Candidate => "候选", NameAssociation.UserCleared => "已清除", _ => "未关联" };
    public long? AccountTotal => value.AccountTotal;
    public long? ObservedTotal => value.ObservedTotal;
    public long? SharedStorage => value.SharedStorage;
    public long?[] CharacterTotals { get; } = columns.Select(c => value.Characters.GetValueOrDefault(c.Id)?.Total).ToArray();
    public long? QuantityForCharacter(string characterId)=>value.Characters.GetValueOrDefault(characterId)?.Total;
    public string[] CharacterDetails { get; } = columns.Select(c => value.Characters.TryGetValue(c.Id, out var q) ? "背包：" + Number(q.Backpack) + "；角色仓：" + Number(q.CharacterStorage) : "未知").ToArray();
    private static string Number(long? quantity) => quantity?.ToString("N0", CultureInfo.CurrentCulture) ?? "未知";
}

public sealed class AccountItemsViewModel : ObservableObject
{
    private readonly AccountItemService service;
    private AccountChoice? selectedAccount;
    private string search = "", status = "请选择账户", error = "";
    private bool loading;
    private AccountCharacterColumn[] columns = [];
    private AccountItemEditRow[] selectedRows = [];
    private string? lastExportPath;
    public ObservableCollection<AccountChoice> Accounts { get; } = [];
    public ObservableCollection<AccountItemEditRow> Items { get; } = [];
    public DataGridCollectionView ItemsView { get; }
    public IReadOnlyList<AccountCharacterColumn> CharacterColumns => columns;
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ConfirmSelectedCommand { get; }
    public RelayCommand ClearSelectedCommand { get; }
    public RelayCommand ResetSortCommand { get; }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string Error { get => error; private set { if(SetProperty(ref error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public string? LastExportPath { get => lastExportPath; private set => SetProperty(ref lastExportPath, value); }
    public int SelectedCount => selectedRows.Length;
    public bool HasSelection => selectedRows.Length > 0;
    public bool HasAccount => SelectedAccount is not null;
    public bool HasDirtyEdits => Items.Any(i => i.Dirty);
    public event EventHandler? ColumnsChanged;
    public event EventHandler? AssociationsChanged;
    public AccountChoice? SelectedAccount
    {
        get => selectedAccount;
        set
        {
            if (ReferenceEquals(selectedAccount, value)) return;
            if (!loading && HasDirtyEdits) { Error = "请先保存名称，或点击“放弃编辑并刷新”"; OnPropertyChanged(); return; }
            if (SetProperty(ref selectedAccount, value))
            {
                OnPropertyChanged(nameof(HasAccount)); LastExportPath = null;
                SetSelection([]); Items.Clear(); columns = []; ColumnsChanged?.Invoke(this, EventArgs.Empty);
                if (!loading) LoadAccount(false);
            }
        }
    }
    public string Search
    {
        get => search;
        set { if (SetProperty(ref search, value)) { ItemsView.Refresh(); SetSelection([]); } }
    }
    public AccountItemsViewModel(AccountItemService service)
    {
        this.service = service; ItemsView = new DataGridCollectionView(Items);
        ItemsView.Filter = item => item is AccountItemEditRow row && (string.IsNullOrWhiteSpace(Search) || row.ItemId.ToString(CultureInfo.InvariantCulture).Contains(Search, StringComparison.OrdinalIgnoreCase) || (row.Name?.Contains(Search, StringComparison.CurrentCultureIgnoreCase) ?? false) || row.Association.Contains(Search, StringComparison.CurrentCultureIgnoreCase));
        RefreshCommand = new(() => Refresh(false));
        ConfirmSelectedCommand = new(() => SaveSelected(NameEditOperation.Confirm), () => selectedRows.Length > 0 && HasAccount);
        ClearSelectedCommand = new(() => SaveSelected(NameEditOperation.Clear), () => selectedRows.Length > 0 && HasAccount);
        ResetSortCommand = new(() => { ItemsView.SortDescriptions.Clear(); ColumnsChanged?.Invoke(this, EventArgs.Empty); Status = "已按道具编号排序"; });
        Refresh(false);
    }
    public void SetSelection(IEnumerable<AccountItemEditRow> selection)
    {
        selectedRows = selection.Where(row => Items.Contains(row) && ItemsView.Contains(row)).Distinct().ToArray();
        OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(HasSelection)); ConfirmSelectedCommand.NotifyCanExecuteChanged(); ClearSelectedCommand.NotifyCanExecuteChanged();
    }
    public void Refresh(bool discardEdits)
    {
        if (!discardEdits && HasDirtyEdits) { Error = "存在未保存名称；请保存或放弃编辑"; return; }
        try
        {
            var id = SelectedAccount?.Id; var accounts = service.Accounts(); loading = true; Accounts.Clear();
            foreach (var account in accounts) Accounts.Add(new(account.AccountId, account.AccountId + " · " + string.Join("、", account.Characters.Values)));
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == id) ?? Accounts.FirstOrDefault();
            loading = false; LoadAccount(false);
        }
        catch (Exception e) { loading = false; Fail(e); }
    }
    private void LoadAccount(bool preserveDrafts)
    {
        try
        {
            var drafts = preserveDrafts ? Items.Where(i => i.Dirty).ToDictionary(i => i.ItemId, i => i) : [];
            var selectedIds=selectedRows.Select(r=>r.ItemId).ToHashSet();
            var view = SelectedAccount is null ? null : service.View(SelectedAccount.Id);
            columns = view?.Characters.Select(p=>new{p.Key,p.Value,Slot=service.CharacterSlot(view.AccountId,p.Key)})
                .OrderBy(p=>p.Slot??int.MaxValue).Select((p,i)=>new AccountCharacterColumn(p.Key,p.Slot is{} slot?$"{slot}号 · {p.Value}":p.Value,i)).ToArray() ?? [];
            SetSelection([]); Items.Clear();
            foreach (var row in view?.Items ?? [])
            { var item = new AccountItemEditRow(row, columns); if (drafts.TryGetValue(item.ItemId, out var draft)) item = new AccountItemEditRow(row,columns){OriginalName=draft.OriginalName,OriginalAssociation=draft.OriginalAssociation,Name=draft.Name}; Items.Add(item); }
            SetSelection(Items.Where(r=>selectedIds.Contains(r.ItemId)));
            ColumnsChanged?.Invoke(this, EventArgs.Empty); Error = "";
            Status = view is null ? "尚无账户记录；抓包同步后刷新" : $"{view.Items.Count} 种道具 · 历史记录 · " + (view.Complete ? "已记录角色覆盖完整" : "覆盖不完整，未知数量不计零");
        }
        catch (Exception e) { Fail(e); }
    }
    public bool IsCellEditing{get;set;}
    public void RefreshLive()
    {
        if(IsCellEditing)return;
        try
        {
            var source=service.Accounts().Select(a=>new AccountChoice(a.AccountId,a.AccountId+" · "+string.Join("、",a.Characters.Values))).ToArray();
            foreach(var account in source)if(!Accounts.Any(a=>a.Id==account.Id))Accounts.Add(account);
            if(SelectedAccount is null&&Accounts.Count>0)SelectedAccount=Accounts.First();else LoadAccount(true);
        }catch(Exception error){Fail(error);}
    }
    private void SaveSelected(NameEditOperation operation)
    {
        if (SelectedAccount is null || selectedRows.Length == 0) { Error = "请先选中要处理的行"; return; }
        try
        {
            var chosen = selectedRows.Where(ItemsView.Contains).ToArray(); if (chosen.Length == 0) throw new InvalidOperationException("请重新选择可见行");
            var current = service.View(SelectedAccount.Id).Items.ToDictionary(i => i.ItemId);
            foreach (var row in chosen)
            {
                if (!current.TryGetValue(row.ItemId, out var saved) || saved.Name != row.OriginalName || saved.Association != row.OriginalAssociation) throw new IOException("名称已由其他窗口修改，请刷新后重试");
                if (operation == NameEditOperation.Confirm && string.IsNullOrWhiteSpace(row.Name)) throw new ArgumentException("选中行存在空名称；清除请使用清除按钮");
            }
            service.UpdateNames(SelectedAccount.Id, chosen.Select(i => new ItemNameEdit(i.ItemId, i.Name)).ToArray(), operation);
            var ids = chosen.Select(i => i.ItemId).ToHashSet();
            foreach (var row in Items.Where(i => ids.Contains(i.ItemId))) row.Name = row.OriginalName;
            LoadAccount(true); Status = operation == NameEditOperation.Clear ? $"已清除 {chosen.Length} 项关联" : $"已确认 {chosen.Length} 项关联";
            AssociationsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) { Fail(e); }
    }
    public void ExportCsv(string path)
    {
        if (SelectedAccount is null) { Error = "请选择账户"; return; }
        try
        {
            var absolute = Path.GetFullPath(path); var temporary = absolute + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { using (var output = new StreamWriter(temporary, false, new UTF8Encoding(true))) service.ExportCsv(SelectedAccount.Id, output); File.Move(temporary, absolute, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            LastExportPath = absolute; Error = ""; Status = "已导出已保存数据：" + absolute;
        }
        catch (Exception e) { Fail(e); }
    }
    public AccountCsvPaths? CsvPaths()
    { try { return SelectedAccount is null ? null : service.Files(SelectedAccount.Id); } catch (Exception e) { Fail(e); return null; } }
    public void ReportError(Exception error) => Fail(error);
    private void Fail(Exception error) { service.ReportFault(error,"账户道具操作",SelectedAccount?.Id);Error = error.Message; Status = "操作未完成"; }
}
