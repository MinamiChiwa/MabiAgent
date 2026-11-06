using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MabiAgent.Update;

namespace MabiAgent.Desktop;

// Optional, confirmation-gated update channel. Checking never downloads; downloading
// never replaces a file; installing hands the verified plan to a staged updater after
// the app has paused its own work and released the game input.
public partial class MainViewModel
{
    private readonly CancellationTokenSource updateLifetime = new();
    private UpdateService? updateService;
    private UpdateCheck? updateCheck;
    private UpdatePreferences updatePreferences = new(UpdatePreferences.DefaultCheckOnStart, false, null);
    private string? stagedPlan;
    [ObservableProperty] private string updateStatus = "尚未检查更新";
    [ObservableProperty] private bool updateAvailable;
    [ObservableProperty] private bool updateBusy;
    [ObservableProperty] private double updateProgress;
    [ObservableProperty] private bool updateReady;
    [ObservableProperty] private bool autoCheckUpdates = UpdatePreferences.DefaultCheckOnStart;
    private bool updatePreferencesLoaded;
    private bool updatesReleased;
    public bool HasUpdateNotification => UpdateAvailable;
    public string TitleUpdateTooltip
    {
        get
        {
            if (UpdateBusy) return "更新正在处理中，打开设置查看";
            if (UpdateReady) return "新版本已准备，打开设置安装";
            if (updateCheck is { Available: true } check && !string.IsNullOrWhiteSpace(check.RemoteVersion))
                return $"发现新版本 {check.RemoteVersion}，打开设置更新";
            return "打开设置并检查新版本";
        }
    }
    /// <summary>Set by the window so a finished update can close the app gracefully.</summary>
    public Action? ShutdownRequested { get; set; }
    /// <summary>Set by the window so the shell can bring the update card into view.</summary>
    public Action? UpdateSectionRequested { get; set; }
    /// <summary>The install root, not the app/ folder the assemblies live in.</summary>
    private string UpdateRoot => InstallLayout.ResolveRoot(AppContext.BaseDirectory);
    private string UpdatePreferencesPath => Path.Combine(DataPath, "updates", "preferences.json");

    /// <summary>Default download mirror; failed requests fall back to the official asset URL.</summary>
    private const string DefaultMirror = "https://v6.gh-proxy.org/";
    private bool applyingUpdate;

    private UpdateService Updates => updateService ??= new UpdateService(UpdateRoot, new HttpClientHandler(), new TransportOptions(LoadMirrorPrefix()));

    /// <summary>
    /// Mirror prefix resolution: an explicit setting (an empty string disables the mirror) wins,
    /// then the environment variable, then the mirror this project has already verified. The
    /// transport falls back to the official address whenever the mirror fails.
    /// </summary>
    private string? LoadMirrorPrefix()
    {
        var preferences = updatePreferencesLoaded ? updatePreferences : UpdatePreferences.Load(UpdatePreferencesPath);
        if (preferences.HasMirror) return preferences.Mirror ?? "";
        try
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("MABIAGENT_UPDATE_MIRROR");
            return string.IsNullOrWhiteSpace(fromEnvironment) ? DefaultMirror : fromEnvironment;
        }
        catch (System.Security.SecurityException) { return DefaultMirror; }
    }

    public void LoadUpdatePreferences()
    {
        if (updatePreferencesLoaded || updatesReleased) return;
        updatePreferences = UpdatePreferences.Load(UpdatePreferencesPath);
        AutoCheckUpdates = updatePreferences.CheckOnStart;
        updatePreferencesLoaded = true;
    }

    /// <summary>Rewrites only <c>check_on_start</c>; a configured mirror and any other key survive.</summary>
    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        if (!updatePreferencesLoaded) return;
        try
        {
            UpdatePreferences.SaveCheckOnStart(UpdatePreferencesPath, value);
            updatePreferences = updatePreferences with { CheckOnStart = value };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { UpdateStatus = "无法保存更新偏好：" + error.Message; }
    }

    [RelayCommand]
    public async Task CheckUpdateAsync()
    {
        if (UpdateBusy || updatesReleased) return;
        if (UpdateEntry.IsAutomatedRun(Environment.GetCommandLineArgs()))
        {
            UpdateStatus = "诊断或验收运行不联网检查更新";
            return;
        }
        UpdateBusy = true; updateCheck = null; stagedPlan = null; UpdateAvailable = false; UpdateReady = false; UpdateProgress = 0;
        try
        {
            var check = await Updates.CheckAsync(ProductInfo.Version, updateLifetime.Token);
            if (updatesReleased) return;
            updateCheck = check;
            UpdateStatus = check.Summary;
            UpdateAvailable = check.Available;
        }
        catch (OperationCanceledException) when (updateLifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
            if (!updatesReleased) UpdateStatus = "检查更新失败：" + error.Message;
        }
        finally { if (!updatesReleased) UpdateBusy = false; }
    }

    [RelayCommand]
    public async Task DownloadUpdateAsync()
    {
        if (UpdateBusy || updatesReleased || updateCheck is not { Available: true } check) return;
        UpdateBusy = true; UpdateProgress = 0; UpdateReady = false;
        try
        {
            if (check.HasManifest)
            {
                var progress = new Progress<UpdateProgress>(p =>
                {
                    if (updatesReleased) return;
                    UpdateProgress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
                    UpdateStatus = $"下载 {p.Done}/{p.Total}";
                });
                var plan = await Updates.PrepareAsync(check, progress, updateLifetime.Token);
                if (updatesReleased) return;
                if (plan is null)
                {
                    updateCheck = check with { Available = false, Summary = "已是最新，无需下载" };
                    UpdateAvailable = false;
                    UpdateStatus = updateCheck.Summary;
                    return;
                }
                var path = Path.Combine(UpdateRoot, "updates", "update-plan.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, plan.ToJson());
                if (updatesReleased) return;
                stagedPlan = path;
                UpdateProgress = 1; UpdateReady = true;
                var kept = plan.Conflicts?.Count ?? 0;
                UpdateStatus = $"已准备 {plan.Files.Count} 个文件" + (kept > 0 ? $"，保留 {kept} 个本地修改" : "");
                return;
            }
            var package = await Updates.DownloadFullPackageAsync(check.Release!, Path.Combine(UpdateRoot, "updates"), updateLifetime.Token);
            if (updatesReleased) return;
            UpdateStatus = package is null ? "该发布没有可下载的完整包" : $"已下载完整包 {Path.GetFileName(package.Value.Path)}";
        }
        catch (OperationCanceledException) when (updateLifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
            if (!updatesReleased)
            {
                UpdateStatus = "下载失败：" + error.Message;
                UpdateProgress = 0;
            }
        }
        finally { if (!updatesReleased) UpdateBusy = false; }
    }

    [RelayCommand]
    private async Task TitleUpdateAsync()
    {
        SelectedPage = 2;
        UpdateSectionRequested?.Invoke();
        if (updatesReleased || UpdateBusy || updateCheck is { Available: true }) return;
        await CheckUpdateAsync();
    }

    private bool CanApplyUpdate => UpdateReady && !UpdateBusy && !applyingUpdate && !updatesReleased;
    partial void OnUpdateAvailableChanged(bool value) => OnPropertyChanged(nameof(HasUpdateNotification));
    partial void OnUpdateReadyChanged(bool value) { ApplyUpdateCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(TitleUpdateTooltip)); }
    partial void OnUpdateBusyChanged(bool value) { ApplyUpdateCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(TitleUpdateTooltip)); }
    partial void OnUpdateStatusChanged(string value) => OnPropertyChanged(nameof(TitleUpdateTooltip));

    [RelayCommand(CanExecute = nameof(CanApplyUpdate))]
    public async Task ApplyUpdateAsync()
    {
        if (!UpdateReady || stagedPlan is null || applyingUpdate || updatesReleased) return;
        applyingUpdate = true; UpdateBusy = true;
        try
        {
            // 1. Stop automation and confirm the game input was actually released.
            if (automation is { IsRunning: true })
            {
                try { await automation.PauseAsync().WaitAsync(TimeSpan.FromSeconds(10), updateLifetime.Token); }
                catch (OperationCanceledException) when (updateLifetime.IsCancellationRequested) { return; }
                catch (Exception pause) when (pause is TimeoutException or InvalidOperationException or IOException)
                {
                    UpdateStatus = "未能暂停任务，已取消安装：" + pause.Message;
                    return;
                }
                if (!automation.InputPaused)
                {
                    UpdateStatus = "游戏输入尚未释放，已取消安装";
                    return;
                }
            }
            // 2. Persist settings before anything exits; invalid input cancels the update.
            if (!PrepareUpdate())
            {
                UpdateStatus = "设置未保存，已取消安装：请先修正输入的数值";
                return;
            }
            // 3. Start the runner from a copy so the files it replaces are not locked.
            var runner = UpdateRunner.Stage(UpdateRoot);
            _ = UpdateRunner.Start(runner, stagedPlan, Environment.ProcessId, UpdateRoot);
            // Destructive teardown only after the runner is actually running.
            PrepareClose();
            UpdateStatus = "正在退出并安装更新";
            ShutdownRequested?.Invoke();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UpdateStatus = "无法启动更新：" + error.Message;
        }
        finally
        {
            if (!updatesReleased)
            {
                applyingUpdate = false; UpdateBusy = false;
                ApplyUpdateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    internal void ReleaseUpdates()
    {
        if (updatesReleased) return;
        updatesReleased = true;
        updateLifetime.Cancel();
        updateService?.Dispose(); updateService = null;
        updateLifetime.Dispose();
    }
}
