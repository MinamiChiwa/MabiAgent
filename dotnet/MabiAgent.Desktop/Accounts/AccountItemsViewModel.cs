using System.Linq;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
/// <summary>
/// Replaces a loaded account table in one collection reset. Adding hundreds of
/// rows one by one makes an attached DataGrid process every intermediate state.
/// </summary>
public sealed class AccountItemRows : ObservableCollection<AccountItemEditRow>
{
    public void ApplySnapshot(IEnumerable<AccountItemEditRow> rows)
    {
        CheckReentrancy();
        var next = rows.ToArray();
        if (System.Linq.Enumerable.SequenceEqual(this, next)) return;
        Items.Clear();
        foreach (var row in next) Items.Add(row);
        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
    }
}
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
    private readonly Func<Task<ClientCatalogUpdate>>? updateClientCatalog;
    private bool updatingAssociations;
    private string associationUpdateStatus = "";
    public bool UpdatingAssociations { get => updatingAssociations; private set => SetProperty(ref updatingAssociations, value); }
    public string AssociationUpdateStatus { get => associationUpdateStatus; private set => SetProperty(ref associationUpdateStatus, value); }
    public AsyncRelayCommand UpdateAssociationsCommand { get; }
    private AccountChoice? selectedAccount;
    private string search = "", status = "请选择账户", error = "";
    private bool loading;
    private AccountCharacterColumn[] columns = [];
    private AccountItemEditRow[] selectedRows = [];
    private string? lastExportPath;
    public ObservableCollection<AccountChoice> Accounts { get; } = [];
    public AccountItemRows Items { get; } = [];
    private string? lastLiveStamp;
    private bool refreshingLive;
    private sealed record LiveSnapshot(AccountChoice[] Accounts, string? SelectedId, AccountCharacterColumn[] Columns, AccountItemRow[] Rows, bool HasView, bool Complete);
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
    public IReadOnlyList<AccountItemEditRow> SelectedRows => selectedRows;
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
    public AccountItemsViewModel(AccountItemService service, Func<Task<ClientCatalogUpdate>>? updateClientCatalog = null)
    {
        this.service = service; this.updateClientCatalog = updateClientCatalog; ItemsView = new DataGridCollectionView(Items);
        UpdateAssociationsCommand = new(UpdateAssociationsAsync, () => this.updateClientCatalog is not null);
        ItemsView.Filter = item => item is AccountItemEditRow row && (string.IsNullOrWhiteSpace(Search) || row.ItemId.ToString(CultureInfo.InvariantCulture).Contains(Search, StringComparison.OrdinalIgnoreCase) || (row.Name?.Contains(Search, StringComparison.CurrentCultureIgnoreCase) ?? false) || row.Association.Contains(Search, StringComparison.CurrentCultureIgnoreCase));
        RefreshCommand = new(() => Refresh(false));
        ConfirmSelectedCommand = new(() => SaveSelected(NameEditOperation.Confirm), () => selectedRows.Length > 0 && HasAccount);
        ClearSelectedCommand = new(() => SaveSelected(NameEditOperation.Clear), () => selectedRows.Length > 0 && HasAccount);
        ResetSortCommand = new(() => { ItemsView.SortDescriptions.Clear(); ColumnsChanged?.Invoke(this, EventArgs.Empty); Status = "已按道具编号排序"; });
        Refresh(false);
    }
    private async Task UpdateAssociationsAsync()
    {
        if (UpdatingAssociations) return;
        if (HasDirtyEdits || IsCellEditing) { Error = "请先保存名称，或放弃编辑后更新物品关联"; return; }
        if (updateClientCatalog is null) return;
        UpdatingAssociations = true; Error = ""; AssociationUpdateStatus = "正在读取游戏文件…";
        try
        {
            var result = await updateClientCatalog();
            LoadAccount(true);
            AssociationUpdateStatus = $"名称目录 {result.Names:N0} 条 · {result.ChangedAssociations:N0} 条关联已更新";
            AssociationsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) { AssociationUpdateStatus = "更新未完成"; Fail(e); }
        finally { UpdatingAssociations = false; }
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
            SetSelection([]);
            var rows = (view?.Items ?? []).Select(row =>
            {
                if (drafts.TryGetValue(row.ItemId, out var draft))
                    return new AccountItemEditRow(row, columns) { OriginalName = draft.OriginalName, OriginalAssociation = draft.OriginalAssociation, Name = draft.Name };
                return new AccountItemEditRow(row, columns);
            }).ToArray();
            Items.ApplySnapshot(rows);
            SetSelection(Items.Where(r=>selectedIds.Contains(r.ItemId)));
            ColumnsChanged?.Invoke(this, EventArgs.Empty); Error = "";
            Status = view is null ? "尚无账户记录；抓包同步后刷新" : $"{view.Items.Count} 种道具 · 历史记录 · " + (view.Complete ? "已记录角色覆盖完整" : "覆盖不完整，未知数量不计零");
        }
        catch (Exception e) { Fail(e); }
    }
    public bool IsCellEditing{get;set;}
    /// <summary>
    /// Live capture can advance many times while the account page is open. A
    /// stamp supplied by the host skips duplicate work; tests and explicit
    /// refreshes omit it to force a reload.
    /// </summary>
    public void RefreshLive(string? stamp=null)
    {
        if(IsCellEditing)return;
        if(stamp is not null&&string.Equals(stamp,lastLiveStamp,StringComparison.Ordinal))return;
        try
        {
            var source=service.Accounts().Select(a=>new AccountChoice(a.AccountId,a.AccountId+" · "+string.Join("、",a.Characters.Values))).ToArray();
            foreach(var account in source)if(!Accounts.Any(a=>a.Id==account.Id))Accounts.Add(account);
            if(SelectedAccount is null&&Accounts.Count>0)SelectedAccount=Accounts.First();else LoadAccount(true);
            lastLiveStamp=stamp;
        }catch(Exception error){Fail(error);}
    }
    public async Task RefreshLiveAsync(string? stamp=null)
    {
        if(IsCellEditing||refreshingLive)return;
        if(stamp is not null&&string.Equals(stamp,lastLiveStamp,StringComparison.Ordinal))return;
        refreshingLive=true;
        var selectedId=SelectedAccount?.Id;
        try
        {
            var snapshot=await Task.Run(()=>ReadLiveSnapshot(selectedId));
            if(IsCellEditing||SelectedAccount?.Id!=selectedId)return;
            ApplyLiveSnapshot(snapshot);
            lastLiveStamp=stamp;
        }
        catch(Exception error){Fail(error);}
        finally{refreshingLive=false;}
    }
    private LiveSnapshot ReadLiveSnapshot(string? selectedId)
    {
        var choices=service.Accounts().Select(a=>new AccountChoice(a.AccountId,a.AccountId+" · "+string.Join("、",a.Characters.Values))).ToArray();
        var id=choices.Any(a=>a.Id==selectedId)?selectedId:choices.FirstOrDefault()?.Id;
        var view=id is null?null:service.View(id);
        var columns=view?.Characters.Select(p=>new{p.Key,p.Value,Slot=service.CharacterSlot(view.AccountId,p.Key)})
            .OrderBy(p=>p.Slot??int.MaxValue).Select((p,i)=>new AccountCharacterColumn(p.Key,p.Slot is{} slot?$"{slot}号 · {p.Value}":p.Value,i)).ToArray()??[];
        return new(choices,id,columns,view?.Items.ToArray()??[],view is not null,view?.Complete==true);
    }
    private void ApplyLiveSnapshot(LiveSnapshot snapshot)
    {
        foreach(var stale in Accounts.Where(a=>!snapshot.Accounts.Any(next=>next.Id==a.Id)).ToArray())Accounts.Remove(stale);
        foreach(var account in snapshot.Accounts)if(!Accounts.Any(a=>a.Id==account.Id))Accounts.Add(account);
        var selected=Accounts.FirstOrDefault(a=>a.Id==snapshot.SelectedId);
        if(!ReferenceEquals(selectedAccount,selected))
        {
            selectedAccount=selected;
            OnPropertyChanged(nameof(SelectedAccount));
            OnPropertyChanged(nameof(HasAccount));
            LastExportPath=null;
        }
        var drafts=Items.Where(i=>i.Dirty).ToDictionary(i=>i.ItemId,i=>i);
        var selectedIds=selectedRows.Select(r=>r.ItemId).ToHashSet();
        SetSelection([]);
        columns=snapshot.Columns;
        var rows=snapshot.Rows.Select(row=>
        {
            if(drafts.TryGetValue(row.ItemId,out var draft))
                return new AccountItemEditRow(row,columns){OriginalName=draft.OriginalName,OriginalAssociation=draft.OriginalAssociation,Name=draft.Name};
            return new AccountItemEditRow(row,columns);
        }).ToArray();
        Items.ApplySnapshot(rows);
        SetSelection(Items.Where(r=>selectedIds.Contains(r.ItemId)));
        ColumnsChanged?.Invoke(this,EventArgs.Empty);
        Error="";
        Status=!snapshot.HasView?"尚无账户记录；抓包同步后刷新":$"{snapshot.Rows.Length} 种道具 · 历史记录 · "+(snapshot.Complete?"已记录角色覆盖完整":"覆盖不完整，未知数量不计零");
    }    private void SaveSelected(NameEditOperation operation)
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
