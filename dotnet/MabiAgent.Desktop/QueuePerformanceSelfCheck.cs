using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MabiAgent.Desktop.Controls;
using MabiAgent.Windows;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MabiAgent.Desktop;

/// <summary>
/// Measures the queue list before and after the interface work: realized row
/// containers, row-level input controls, collection notifications and whether a
/// refresh keeps selection and scroll. Runs on isolated temp data; never sends
/// desktop or game input.
/// </summary>
internal static class QueuePerformanceSelfCheck
{
    public static async Task<string[]> Run(string root, string? output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mabi-queue-perf-" + Guid.NewGuid().ToString("N"));
        var workspace = new Workspace(root, directory);
        var vm = new MainViewModel(workspace, new GameWindowSelection(() => []));
        var checks = new List<string>();
        MainWindow? window = null;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }
        async Task FlushUi(DispatcherPriority priority)
        {
            await Dispatcher.UIThread.InvokeAsync(() => window!.UpdateLayout(), priority);
        }
        async Task<bool> Settle(bool waitForDeferred = false)
        {
            if (!waitForDeferred)
            {
                await Task.Delay(20);
                await FlushUi(DispatcherPriority.ContextIdle);
                return true;
            }

            // Keep measuring the complete first visit. The background build queue is
            // drained one card per dispatcher turn; the deadline is a safety guard, not
            // an acceptance threshold that hides outstanding work.
            for (var attempt = 0; attempt < 300; attempt++)
            {
                await Task.Delay(10);
                await FlushUi(DispatcherPriority.ContextIdle);
                if (DeferredSettingsSection.HasPendingBuilds) continue;
                await Task.Delay(10);
                await FlushUi(DispatcherPriority.ContextIdle);
                if (!DeferredSettingsSection.HasPendingBuilds) return true;
            }
            return false;
        }
        async Task WaitForDeferred(DeferredSettingsSection section)
        {
            for (var attempt = 0; attempt < 50 && !section.IsBuilt; attempt++)
            {
                section.BringIntoView();
                await Settle();
            }
            await Settle();
        }
        async Task<bool> WaitForAccountGrid()
        {
            var accounts=window!.FindControl<Accounts.AccountItemsPanel>("AccountsPanel");
            if(accounts is null)return false;
            for(var attempt=0;attempt<300&&!accounts.IsGridReady;attempt++){await Task.Delay(10);await FlushUi(DispatcherPriority.ContextIdle);}
            await Task.Delay(10);await FlushUi(DispatcherPriority.ContextIdle);
            return accounts.IsGridReady;
        }
        async Task<(double TotalMs, double SetterMs, double LoadedMs, double RenderUpdateLayoutMs, double InputReadyMs, double PostRenderInputDelayMs, double FirstInputHeartbeatMs, double MaxInputHeartbeatGapMs, int InputHeartbeatCount, int InputHeartbeatGapsOver100Ms, double SettleMs, bool DeferredSettled)> SwitchPage(int page, Action? afterFirstRender = null)
        {
            // The total remains visible-to-settled. Loaded and Render values are nested
            // measurements from the same visit; they do not shorten the measured window.
            var watch = Stopwatch.StartNew();
            var setterWatch = Stopwatch.StartNew();
            var firstInputHeartbeatMs = -1d;
            var maxInputHeartbeatGapMs = 0d;
            var inputHeartbeatCount = 0;
            var inputHeartbeatGapsOver100Ms = 0;
            var lastInputHeartbeatMs = watch.Elapsed.TotalMilliseconds;
            void RecordInputHeartbeat()
            {
                var now = watch.Elapsed.TotalMilliseconds;
                if (firstInputHeartbeatMs < 0) firstInputHeartbeatMs = now;
                var gap = now - lastInputHeartbeatMs;
                maxInputHeartbeatGapMs = Math.Max(maxInputHeartbeatGapMs, gap);
                if (gap >= 100) inputHeartbeatGapsOver100Ms++;
                lastInputHeartbeatMs = now;
                inputHeartbeatCount++;
            }
            var inputHeartbeat = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => RecordInputHeartbeat());
            inputHeartbeat.Start();

            SettingsPanel? settingsPage = null;
            EventHandler? shownHandler = null;
            TaskCompletionSource<double>? shownSignal = null;
            if (page == 2)
            {
                settingsPage = window!.FindControl<SettingsPanel>("SettingsPage");
                if (settingsPage is not null)
                {
                    shownSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    shownHandler = (_, _) => shownSignal.TrySetResult(watch.Elapsed.TotalMilliseconds);
                    settingsPage.PageShown += shownHandler;
                }
            }
            try { vm.SelectedPage = page; setterWatch.Stop(); }
            catch
            {
                setterWatch.Stop();
                if (settingsPage is not null && shownHandler is not null) settingsPage.PageShown -= shownHandler;
                throw;
            }

            var loadedMs = 0d;
            try
            {
                if (shownSignal is not null)
                {
                    var completed = await Task.WhenAny(shownSignal.Task, Task.Delay(1000));
                    loadedMs = ReferenceEquals(completed, shownSignal.Task) ? await shownSignal.Task : -1;
                }
            }
            finally
            {
                if (settingsPage is not null && shownHandler is not null) settingsPage.PageShown -= shownHandler;
            }

            var renderWatch = Stopwatch.StartNew();
            await FlushUi(DispatcherPriority.Render);
            renderWatch.Stop();
            afterFirstRender?.Invoke();

            // This short pulse remains as a first-render checkpoint. The timer above is
            // the end-to-end probe: it spans initial layout and every deferred build.
            var inputSignal = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            var postRenderInputWatch = Stopwatch.StartNew();
            Dispatcher.UIThread.Post(() => inputSignal.TrySetResult(postRenderInputWatch.Elapsed.TotalMilliseconds), DispatcherPriority.Input);
            var postRenderInputDelayMs = await inputSignal.Task;
            var inputReadyMs = watch.Elapsed.TotalMilliseconds;

            var settleWatch = Stopwatch.StartNew();
            var deferredSettled = page == 1 ? await WaitForAccountGrid() : await Settle(page == 2);
            var finalHeartbeat = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() => { RecordInputHeartbeat(); finalHeartbeat.TrySetResult(true); }, DispatcherPriority.Input);
            await finalHeartbeat.Task;
            inputHeartbeat.Stop();
            settleWatch.Stop();
            watch.Stop();
            return (watch.Elapsed.TotalMilliseconds, setterWatch.Elapsed.TotalMilliseconds, loadedMs,
                renderWatch.Elapsed.TotalMilliseconds, inputReadyMs, postRenderInputDelayMs,
                firstInputHeartbeatMs, maxInputHeartbeatGapMs, inputHeartbeatCount, inputHeartbeatGapsOver100Ms,
                settleWatch.Elapsed.TotalMilliseconds, deferredSettled);
        }
        try
        {
            await vm.RefreshAsync();
            var recipe = vm.Recipes.First();
            for (var i = 0; i < 200; i++) workspace.AddProduction(recipe.Id, 10 * (i + 1));
            await vm.RefreshAsync();
            window = new MainWindow(vm) { Width = 1120, Height = 800 };
            window.Show();
            await Settle();
            var panel = window.GetVisualDescendants().OfType<QueuePanel>().Single();
            var grid = panel.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "ManualGrid");

            var realizedTop = grid.GetVisualDescendants().OfType<DataGridRow>().Count();
            var inputsTop = panel.GetVisualDescendants().OfType<TextBox>().Count(b => b.DataContext is JobRow);
            Check(vm.Jobs.Count >= 200, "性能测量使用 200 条以上队列");
            Check(realizedTop < 80, $"默认窗口只实现可见行：{realizedTop} 行容器 / {vm.Jobs.Count} 条");
            Check(inputsTop < 80, $"默认窗口行内输入控件不随总条数增长：{inputsTop}");

            // Wheel and container reuse while scrolling.
            var scroll = grid.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .Where(b => b.Maximum > 0).FirstOrDefault();
            Check(scroll is not null, "列表存在可滚动范围");
            var beforeOffset = scroll!.Value;
            var wheelWatch = Stopwatch.StartNew();
            ScrollUiSelfCheck.Wheel(grid.GetVisualDescendants().OfType<DataGridRow>().First(), window, -3);
            await Settle();
            var wheelSettleMs = wheelWatch.Elapsed.TotalMilliseconds;
            Check(scroll.Value > beforeOffset, "滚轮可滚动队列列表");
            Check(wheelSettleMs < 500, $"滚轮与布局在 500ms 内完成：{wheelSettleMs:F1}ms");

            var rowsBefore = grid.GetVisualDescendants().OfType<DataGridRow>().ToArray();
            grid.SelectedItems.Clear(); grid.SelectedItems.Add(vm.Jobs[0]);
            var selectedId = vm.Jobs[0].Id;
            var offsetBefore = scroll.Value;

            // Unchanged live refresh must not rebuild anything. One warm-up read
            // happens first so the measurement is not the initial file sync.
            var liveWatch = Stopwatch.StartNew();
            await vm.RefreshLiveQueueAsync();
            await Settle();
            var liveRefreshMs = liveWatch.Elapsed.TotalMilliseconds;
            var notifications = 0;
            var actions = new List<string>();
            void CountChange(object? _, NotifyCollectionChangedEventArgs e) { notifications++; actions.Add(e.Action.ToString()); }
            vm.QueueView.CollectionChanged += CountChange;
            try
            {
                for (var i = 0; i < 5; i++) await vm.RefreshLiveQueueAsync();
                await Settle();
            }
            finally { vm.QueueView.CollectionChanged -= CountChange; }
            Check(notifications == 0, $"队列文件未变化时不重建行集合（{notifications} 次通知）");
            var unchangedNotifications = notifications;
            var rowsAfter = grid.GetVisualDescendants().OfType<DataGridRow>().ToArray();
            Check(rowsAfter.Intersect(rowsBefore).Any(), "未变化的刷新复用行容器");
            Check(vm.SelectedJobIds.Contains(selectedId), "未变化的刷新保留选中");
            Check(Math.Abs(scroll.Value - offsetBefore) < 1, "未变化的刷新保留滚动位置");

            // Rewriting identical queue contents changes only the file timestamp. The
            // report distinguishes useful row updates from full-row notification churn.
            var queuePath = Path.Combine(vm.DataPath, "work-queue.json");
            var queueBytes = File.ReadAllBytes(queuePath);
            var rowEvents = 0;
            var rowEventNames = new List<string?>();
            void CountRow(object? _, PropertyChangedEventArgs e) { rowEvents++; rowEventNames.Add(e.PropertyName); }
            vm.Jobs[0].PropertyChanged += CountRow;
            try
            {
                File.SetLastWriteTimeUtc(queuePath, DateTime.UtcNow.AddSeconds(2));
                await vm.RefreshLiveQueueAsync();
                await Settle();
            }
            finally { vm.Jobs[0].PropertyChanged -= CountRow; }
            Check(File.ReadAllBytes(queuePath).SequenceEqual(queueBytes), "性能测量没有改变队列内容");
            var unchangedRowPropertyEvents = rowEvents;
            Check(unchangedRowPropertyEvents == 0, $"内容未变化的队列刷新不广播行属性（{unchangedRowPropertyEvents} 次）");

            // One appended row: exactly one collection notification, no full reset of selection.
            vm.QueueView.CollectionChanged += CountChange;
            var appended = 0;
            try { workspace.AddProduction(recipe.Id, 999); await vm.RefreshLiveQueueAsync(); appended = notifications - unchangedNotifications; }
            finally { vm.QueueView.CollectionChanged -= CountChange; }
            Check(appended >= 1, "新增一条任务触发一次集合通知");
            Check(vm.SelectedJobIds.Contains(selectedId), "新增后台任务后仍保留原选中");

            // Scrolling to the tail keeps the realized set bounded.
            grid.ScrollIntoView(vm.QueueView.Cast<JobRow>().Last(), null);
            await Settle();
            var realizedBottom = grid.GetVisualDescendants().OfType<DataGridRow>().Count();
            Check(realizedBottom < 80, $"滚到底部仍只实现可见行：{realizedBottom} 行容器");
            var inputsBottom = panel.GetVisualDescendants().OfType<TextBox>().Count(b => b.DataContext is JobRow);
            Check(inputsBottom <= inputsTop + 4, $"行内输入控件不随滚动增长：{inputsBottom}");

            // Target editing still works after the template change. Reflection keeps this
            // one check runnable against both the plain-text-box and the on-demand cell,
            // so the before/after numbers come from the same measurement code.
            var target = vm.QueueView.Cast<JobRow>().First(j => j.CanEditTarget);
            grid.ScrollIntoView(target, null);
            await Settle();
            var begin = target.GetType().GetMethod("BeginTargetEdit");
            var editing = false;
            TextBox? editor = null;
            if (begin is not null)
            {
                editing = begin.Invoke(target, null) is true;
                await Settle();
                Check(editing, "双击计划单元格进入编辑");
                editor = panel.GetVisualDescendants().OfType<TextBox>()
                    .FirstOrDefault(b => ReferenceEquals(b.DataContext, target) && b.IsEffectivelyVisible);
                Check(editor is not null, "进入编辑后才创建输入控件");
            }
            else
            {
                editor = panel.GetVisualDescendants().OfType<TextBox>()
                    .FirstOrDefault(b => ReferenceEquals(b.DataContext, target));
                Check(editor is not null, "行内计划可直接编辑");
            }
            editor!.SetCurrentValue(TextBox.TextProperty, "61");
            editor.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            for (var i = 0; i < 100; i++) { await Task.Delay(20); if (!vm.IsBusy) break; }
            await Settle();
            Check(workspace.Read().Jobs.Single(j => j.Id == target.Id).Target == 61, "编辑后回车保存计划");
            if (editing)
                Check(!(bool)target.GetType().GetProperty("IsEditingTarget")!.GetValue(target)!
                    && panel.GetVisualDescendants().OfType<TextBox>().Count(b => ReferenceEquals(b.DataContext, target)) == 0,
                    "结束编辑后换回文本并移除输入控件");


            // Page switches include synchronous property handlers, lazy construction and layout.
            // The first visit is kept separately because constructing a page for the first time
            // has a different cost from returning to an already-created page.
            var firstPageMs = new Dictionary<int, double>();
            var firstPagePhases = new Dictionary<int, (double TotalMs, double SetterMs, double LoadedMs, double RenderUpdateLayoutMs, double InputReadyMs, double PostRenderInputDelayMs, double FirstInputHeartbeatMs, double MaxInputHeartbeatGapMs, int InputHeartbeatCount, int InputHeartbeatGapsOver100Ms, double SettleMs, bool DeferredSettled)>();
            var pageSwitchMs = new Dictionary<int, List<double>>();
            var pageSwitchPhases = new Dictionary<int, List<(double TotalMs, double SetterMs, double LoadedMs, double RenderUpdateLayoutMs, double InputReadyMs, double PostRenderInputDelayMs, double FirstInputHeartbeatMs, double MaxInputHeartbeatGapMs, int InputHeartbeatCount, int InputHeartbeatGapsOver100Ms, double SettleMs, bool DeferredSettled)>>();
            var accountGridLoadMs = 0d;
            var accountGridMeasureCount = 0;
            var accountGridArrangeCount = 0;
            var accountGridMeasureMs = 0d;
            var accountGridArrangeMs = 0d;
            var accountGridMaxMeasureMs = 0d;
            var accountGridMaxArrangeMs = 0d;
            var settingsFirstVisualDescendantsTotal = 0;
            var settingsFirstControlTypeTop10 = new Dictionary<string, int>(StringComparer.Ordinal);
            var settingsFirstViewerViewportWidth = 0d;
            var settingsFirstViewerViewportHeight = 0d;
            var settingsFirstViewerExtentWidth = 0d;
            var settingsFirstViewerExtentHeight = 0d;
            var settingsFirstViewerVisibleControls = 0;
            var settingsFirstViewerOffscreenControls = 0;
            var settingsFirstDeferredImplemented = 0;
            var settingsFirstDeferredTotal = 0;
            var settingsFirstDataGrids = 0;
            var settingsSettledVisualDescendantsTotal = 0;
            var settingsSettledDeferredImplemented = 0;
            var settingsScrolledDeferredImplemented = 0;
            var settingsScrolledDeferredTotal = 0;
            var settingsSkeletonMeasureCount = 0;
            var settingsSkeletonArrangeCount = 0;
            var settingsSkeletonMeasureMs = 0d;
            var settingsSkeletonArrangeMs = 0d;
            var settingsSkeletonMaxMeasureMs = 0d;
            var settingsSkeletonMaxArrangeMs = 0d;
            var settingsSkeletonVisibleToLoadedMs = 0d;
            var settingsSkeletonRenderMs = 0d;
            var settingsSkeletonRenderCount = 0;
            Dictionary<string, DeferredSettingsSection.Profile> settingsDeferredProfiles = [];

            void CaptureSettingsFirstRender()
            {
                var settingsPage = window!.FindControl<SettingsPanel>("SettingsPage")
                    ?? throw new InvalidOperationException("首访缺少 SettingsPage。");
                var sections = settingsPage.GetVisualDescendants().OfType<DeferredSettingsSection>().ToArray();
                Check(settingsPage.IsEffectivelyVisible, "首次进入设置页后实际页面可见");
                Check(sections.Length > 0, "首次布局先创建设置卡外壳");
                settingsFirstDeferredTotal = sections.Length;
                settingsPage.CaptureInitialLayoutProfile();
                settingsSkeletonMeasureCount = settingsPage.InitialMeasureCount;
                settingsSkeletonArrangeCount = settingsPage.InitialArrangeCount;
                settingsSkeletonMeasureMs = settingsPage.InitialMeasureTotalMs;
                settingsSkeletonArrangeMs = settingsPage.InitialArrangeTotalMs;
                settingsSkeletonMaxMeasureMs = settingsPage.InitialMaxMeasureMs;
                settingsSkeletonMaxArrangeMs = settingsPage.InitialMaxArrangeMs;
                settingsSkeletonVisibleToLoadedMs = settingsPage.InitialVisibleToLoadedMs;
                settingsSkeletonRenderMs = settingsPage.InitialRenderMs;
                settingsSkeletonRenderCount = settingsPage.InitialRenderCount;
                settingsFirstDeferredImplemented = sections.Count(section => section.IsBuilt);
                settingsFirstDataGrids = settingsPage.GetVisualDescendants().OfType<DataGrid>().Count();
                Check(settingsFirstDeferredImplemented < settingsFirstDeferredTotal,
                    $"首次 Render 前不预创建全部延迟卡：{settingsFirstDeferredImplemented}/{settingsFirstDeferredTotal}");
                Check(settingsFirstDataGrids == 0, $"设置首屏不预创建离屏材料 DataGrid：实际 {settingsFirstDataGrids}");

                var settingsVisuals = settingsPage.GetVisualDescendants().ToArray();
                settingsFirstVisualDescendantsTotal = settingsVisuals.Length;
                var settingsControls = settingsVisuals.OfType<Control>().ToArray();
                settingsFirstControlTypeTop10 = settingsControls
                    .GroupBy(control => control.GetType().Name, StringComparer.Ordinal)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key, StringComparer.Ordinal)
                    .Take(10)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

                var settingsColumns = settingsPage.FindControl<Grid>("SettingsColumns")
                    ?? throw new InvalidOperationException("首访设置页视觉树缺少 SettingsColumns 网格，无法统计 ScrollViewer 状态。");
                var settingsViewer = settingsColumns.GetVisualAncestors().OfType<ScrollViewer>().First();
                settingsFirstViewerViewportWidth = settingsViewer.Viewport.Width;
                settingsFirstViewerViewportHeight = settingsViewer.Viewport.Height;
                settingsFirstViewerExtentWidth = settingsViewer.Extent.Width;
                settingsFirstViewerExtentHeight = settingsViewer.Extent.Height;
                Check(settingsFirstViewerViewportHeight > 0 && settingsFirstViewerExtentHeight > settingsFirstViewerViewportHeight,
                    "设置页在有限 viewport 内测量并保留可滚动内容");
                var viewerControls = settingsViewer.GetVisualDescendants().OfType<Control>().ToArray();
                settingsFirstViewerVisibleControls = viewerControls.Count(control =>
                {
                    if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
                    if (control.TranslatePoint(default, settingsViewer) is not { } origin) return false;
                    return origin.X + control.Bounds.Width > 0
                        && origin.Y + control.Bounds.Height > 0
                        && origin.X < settingsViewer.Viewport.Width
                        && origin.Y < settingsViewer.Viewport.Height;
                });
                settingsFirstViewerOffscreenControls = viewerControls.Length - settingsFirstViewerVisibleControls;
                var workbenchButton = settingsPage.FindControl<Button>("OpenWorkbenchButton");
                Check(workbenchButton is { IsEffectivelyVisible: true } && workbenchButton.IsEnabled,
                    "工作台入口仅位于设置页且首屏可用");
            }

            foreach (var page in new[] { 1, 2, 0 })
            {
                var sample = await SwitchPage(page, page == 2 ? CaptureSettingsFirstRender : null);
                firstPageMs[page] = sample.TotalMs;
                firstPagePhases[page] = sample;
                pageSwitchMs[page] = [];
                pageSwitchPhases[page] = [];
                Check(sample.DeferredSettled || page != 2, "首访设置页的可见卡后台构建均已收敛");
                Check(sample.LoadedMs >= 0 || page != 2, "首访设置页实际到达 Loaded 阶段");
                Check(sample.PostRenderInputDelayMs < 100 || page != 2, $"首访设置页首帧后 100ms 内可接收输入优先任务：{sample.PostRenderInputDelayMs:F1}ms");
                if (page == 1) {
                    var accounts = window.FindControl<Accounts.AccountItemsPanel>("AccountsPanel")!;
                    Check(sample.DeferredSettled && accounts.IsGridReady, "仓库 DataGrid 列与行数据完成分批接入");
                    accountGridLoadMs = accounts.GridLoadMs;
                    accountGridMeasureCount = accounts.GridMeasureCount;
                    accountGridArrangeCount = accounts.GridArrangeCount;
                    accountGridMeasureMs = accounts.GridMeasureMs;
                    accountGridArrangeMs = accounts.GridArrangeMs;
                    accountGridMaxMeasureMs = accounts.GridMaxMeasureMs;
                    accountGridMaxArrangeMs = accounts.GridMaxArrangeMs;
                    checks.Add($"仓库首访最大输入心跳间隔 {sample.MaxInputHeartbeatGapMs:F1}ms，超过 100ms {sample.InputHeartbeatGapsOver100Ms} 次");
                }
                if (page == 2) checks.Add($"首访设置页最大输入心跳间隔 {sample.MaxInputHeartbeatGapMs:F1}ms，超过 100ms {sample.InputHeartbeatGapsOver100Ms} 次");
                if (page == 2)
                {
                    var settingsPage = window.FindControl<SettingsPanel>("SettingsPage")!;
                    var settingsVisuals = settingsPage.GetVisualDescendants().ToArray();
                    settingsSettledVisualDescendantsTotal = settingsVisuals.Length;
                    var settingsColumns = settingsPage.FindControl<Grid>("SettingsColumns")
                        ?? throw new InvalidOperationException("设置页视觉树缺少 SettingsColumns 网格。");
                    var settingsViewer = settingsColumns.GetVisualAncestors().OfType<ScrollViewer>().First();

                    var deferredSections = new[]
                    {
                        settingsPage.FindControl<DeferredSettingsSection>("VipSettingsSection") ?? throw new InvalidOperationException("设置页缺少延迟 VIP 卡。"),
                        settingsPage.FindControl<DeferredSettingsSection>("OperationSettingsSection") ?? throw new InvalidOperationException("设置页缺少延迟操作方案卡。"),
                        settingsPage.FindControl<DeferredSettingsSection>("AppearanceSettingsSection") ?? throw new InvalidOperationException("设置页缺少延迟外观卡。"),
                        settingsPage.FindControl<DeferredSettingsSection>("DataSyncSettingsSection") ?? throw new InvalidOperationException("设置页缺少延迟数据同步卡。"),
                        settingsPage.FindControl<DeferredSettingsSection>("RepairSettingsSection") ?? throw new InvalidOperationException("设置页缺少延迟修理卡。"),
                        settingsPage.FindControl<DeferredSettingsSection>("PurchaseSettingsSection") ?? throw new InvalidOperationException("设置页缺少延迟购买卡。"),
                        settingsPage.FindControl<DeferredSettingsSection>("BackpackSettingsSection") ?? throw new InvalidOperationException("设置页缺少延迟背包整理卡。"),
                        settingsPage.FindControl<DeferredSettingsSection>("ReserveSettingsSection") ?? throw new InvalidOperationException("设置页缺少延迟材料留存卡。"),
                        settingsPage.FindControl<DeferredSettingsSection>("WingSettingsSection") ?? throw new InvalidOperationException("设置页缺少延迟精灵之翼卡。"),
                        settingsPage.FindControl<DeferredSettingsSection>("UpdateSettingsSection") ?? throw new InvalidOperationException("设置页缺少延迟版本更新卡。")
                    };
                    settingsScrolledDeferredTotal = deferredSections.Length;
                    settingsSettledDeferredImplemented = deferredSections.Count(section => section.IsBuilt);
                    Check(settingsFirstDeferredTotal == settingsScrolledDeferredTotal,
                        $"首访结构统计覆盖全部设置卡：{settingsFirstDeferredTotal}/{settingsScrolledDeferredTotal}");
                    Check(settingsSettledDeferredImplemented > settingsFirstDeferredImplemented,
                        $"可见设置卡在首屏渲染后逐张实现：{settingsFirstDeferredImplemented}→{settingsSettledDeferredImplemented}");
                    Check(settingsSettledVisualDescendantsTotal > settingsFirstVisualDescendantsTotal,
                        "设置卡实际内容在首次 Render 后加载");

                    foreach (var section in deferredSections)
                    {
                        await WaitForDeferred(section);
                        Check(section.IsBuilt, $"滚动到设置卡 {section.Name} 后完成模板实现");
                        Check(section.Content is Control content && ReferenceEquals(content.DataContext, vm),
                            $"设置卡 {section.Name} 沿当前 MainViewModel DataContext 创建");
                        Check(section.GetVisualDescendants().OfType<Control>().Any(control => control.IsEffectivelyVisible && control.IsEnabled && control is Button or ToggleSwitch or TextBox or ComboBox or AutoCompleteBox or DataGrid),
                            $"设置卡 {section.Name} 实现后包含可见且可用的控件");
                        var card = section.Child as Border
                            ?? throw new InvalidOperationException($"设置卡 {section.Name} 缺少卡片边框。");
                        foreach (var row in card.GetVisualDescendants().OfType<Grid>().Where(row => row.Classes.Contains("setting-row")))
                        {
                            var origin = row.TranslatePoint(default, card);
                            Check(origin is { } point && point.Y >= -1 && point.Y + row.Bounds.Height <= card.Bounds.Height + 1,
                                $"设置卡 {section.Name} 的设置行完整位于卡片边框内：{origin?.Y:F1}+{row.Bounds.Height:F1}/{card.Bounds.Height:F1}");
                        }
                    }

                    settingsScrolledDeferredImplemented = deferredSections.Count(section => section.IsBuilt);
                    settingsDeferredProfiles = deferredSections.ToDictionary(section => section.Name ?? section.GetType().Name, section => section.SnapshotProfile(), StringComparer.Ordinal);
                    Check(settingsScrolledDeferredImplemented == settingsScrolledDeferredTotal,
                        $"滚动到底后全部延迟卡均已实现：{settingsScrolledDeferredImplemented}/{settingsScrolledDeferredTotal}");

                    var backpackSection = deferredSections.Single(section => section.Name == "BackpackSettingsSection");
                    var reserveSection = deferredSections.Single(section => section.Name == "ReserveSettingsSection");
                    var thresholdEditor = backpackSection.GetVisualDescendants().OfType<TextBox>()
                        .Single(control => control.Name == "BackpackThresholdEditor");
                    var reservePicker = reserveSection.GetVisualDescendants().OfType<AutoCompleteBox>()
                        .Single(control => control.Name == "ReservePicker");
                    var originalThreshold = vm.BackpackThreshold;
                    var originalReserve = vm.SelectedReserveItem;
                    var selectedReserve = vm.ReserveChoices.FirstOrDefault(choice => !Equals(choice, originalReserve))
                        ?? vm.ReserveChoices.First();
                    var editedThreshold = originalThreshold == "77" ? "76" : "77";
                    thresholdEditor.SetCurrentValue(TextBox.TextProperty, editedThreshold);
                    reservePicker.SelectedItem = selectedReserve;
                    await Settle();
                    Check(vm.BackpackThreshold == editedThreshold && Equals(vm.SelectedReserveItem, selectedReserve),
                        "延迟背包卡编辑值及选择通过现有界面绑定生效");

                    var deferredContents = deferredSections.Select(section => section.Content).ToArray();
                    var offsetBeforeReturn = settingsViewer.Offset;
                    vm.SelectedPage = 0;
                    await Settle();
                    vm.SelectedPage = 2;
                    await Settle(true);
                    for (var i = 0; i < deferredSections.Length; i++)
                        Check(ReferenceEquals(deferredSections[i].Content, deferredContents[i]),
                            $"设置页返回后复用延迟卡实例：{deferredSections[i].Name}");
                    var returnedThreshold = backpackSection.GetVisualDescendants().OfType<TextBox>()
                        .Single(control => control.Name == "BackpackThresholdEditor");
                    var returnedReserve = reserveSection.GetVisualDescendants().OfType<AutoCompleteBox>()
                        .Single(control => control.Name == "ReservePicker");
                    Check(ReferenceEquals(returnedThreshold, thresholdEditor) && returnedThreshold.Text == editedThreshold,
                        "设置页返回后保留延迟卡编辑控件及值");
                    Check(ReferenceEquals(returnedReserve, reservePicker) && Equals(returnedReserve.SelectedItem, selectedReserve),
                        "设置页返回后保留延迟卡选择控件及值");
                    Check(Math.Abs(settingsViewer.Offset.Y - offsetBeforeReturn.Y) < 1,
                        "设置页返回后保留设置 ScrollViewer 位置");

                    thresholdEditor.SetCurrentValue(TextBox.TextProperty, originalThreshold);
                    reservePicker.SelectedItem = originalReserve;
                    settingsViewer.Offset = default;
                    await Settle();
                }
            }
            foreach (var page in new[] { 1, 2, 0, 1, 2, 0 })
            {
                var sample = await SwitchPage(page);
                pageSwitchMs[page].Add(sample.TotalMs);
                pageSwitchPhases[page].Add(sample);
            }
            vm.SelectedPage = 0;
            await Settle();

            var metrics = new
            {
                generated_at = DateTimeOffset.Now.ToString("O"),
                jobs = vm.Jobs.Count,
                window = $"{window.Width}×{window.Height}",
                realized_rows_top = realizedTop,
                realized_rows_bottom = realizedBottom,
                row_inputs_top = inputsTop,
                row_inputs_bottom = inputsBottom,
                on_demand_editor = begin is not null,
                page_switch_first_ms = firstPageMs.ToDictionary(p => p.Key, p => Math.Round(p.Value, 1)),
                page_switch_first_phase_ms = firstPagePhases.ToDictionary(
                    p => p.Key,
                    p => new
                    {
                        total_ms = Math.Round(p.Value.TotalMs, 1),
                        vm_selected_page_setter_ms = Math.Round(p.Value.SetterMs, 1),
                        page_visible_to_loaded_ms = Math.Round(p.Value.LoadedMs, 1),
                        render_update_layout_ms = Math.Round(p.Value.RenderUpdateLayoutMs, 1),
                        first_visible_input_ready_ms = Math.Round(p.Value.InputReadyMs, 1),
                        post_first_render_input_delay_ms = Math.Round(p.Value.PostRenderInputDelayMs, 1),
                        first_input_heartbeat_ms = Math.Round(p.Value.FirstInputHeartbeatMs, 1),
                        max_input_heartbeat_gap_ms = Math.Round(p.Value.MaxInputHeartbeatGapMs, 1),
                        input_heartbeat_count = p.Value.InputHeartbeatCount,
                        input_heartbeat_gaps_over_100ms = p.Value.InputHeartbeatGapsOver100Ms,
                        deferred_settle_ms = Math.Round(p.Value.SettleMs, 1),
                        deferred_settled = p.Value.DeferredSettled
                    }),
                page_switch_return_phase_ms = pageSwitchPhases.ToDictionary(
                    p => p.Key,
                    p => p.Value.Select(sample => new
                    {
                        total_ms = Math.Round(sample.TotalMs, 1),
                        vm_selected_page_setter_ms = Math.Round(sample.SetterMs, 1),
                        page_visible_to_loaded_ms = Math.Round(sample.LoadedMs, 1),
                        render_update_layout_ms = Math.Round(sample.RenderUpdateLayoutMs, 1),
                        first_visible_input_ready_ms = Math.Round(sample.InputReadyMs, 1),
                        post_first_render_input_delay_ms = Math.Round(sample.PostRenderInputDelayMs, 1),
                        first_input_heartbeat_ms = Math.Round(sample.FirstInputHeartbeatMs, 1),
                        max_input_heartbeat_gap_ms = Math.Round(sample.MaxInputHeartbeatGapMs, 1),
                        input_heartbeat_count = sample.InputHeartbeatCount,
                        input_heartbeat_gaps_over_100ms = sample.InputHeartbeatGapsOver100Ms,
                        deferred_settle_ms = Math.Round(sample.SettleMs, 1),
                        deferred_settled = sample.DeferredSettled
                    }).ToArray()),
                page_switch_median_ms = pageSwitchMs.ToDictionary(p => p.Key, p => Math.Round(p.Value.Order().ElementAt(p.Value.Count / 2), 1)),
                page_switch_max_ms = pageSwitchMs.ToDictionary(p => p.Key, p => Math.Round(p.Value.Max(), 1)),
                account_grid_load_ms = Math.Round(accountGridLoadMs, 1),
                account_grid_measure_passes = accountGridMeasureCount,
                account_grid_arrange_passes = accountGridArrangeCount,
                account_grid_measure_ms = Math.Round(accountGridMeasureMs, 1),
                account_grid_arrange_ms = Math.Round(accountGridArrangeMs, 1),
                account_grid_max_measure_ms = Math.Round(accountGridMaxMeasureMs, 1),
                account_grid_max_arrange_ms = Math.Round(accountGridMaxArrangeMs, 1),
                settings_first_skeleton_measure_passes = settingsSkeletonMeasureCount,
                settings_first_skeleton_arrange_passes = settingsSkeletonArrangeCount,
                settings_first_skeleton_measure_ms = Math.Round(settingsSkeletonMeasureMs, 1),
                settings_first_skeleton_arrange_ms = Math.Round(settingsSkeletonArrangeMs, 1),
                settings_first_skeleton_max_measure_ms = Math.Round(settingsSkeletonMaxMeasureMs, 1),
                settings_first_skeleton_max_arrange_ms = Math.Round(settingsSkeletonMaxArrangeMs, 1),
                settings_first_skeleton_visible_to_loaded_ms = Math.Round(settingsSkeletonVisibleToLoadedMs, 1),
                settings_first_skeleton_render_ms = Math.Round(settingsSkeletonRenderMs, 1),
                settings_first_skeleton_render_passes = settingsSkeletonRenderCount,
                settings_deferred_profiles = settingsDeferredProfiles,
                settings_longest_deferred_build = settingsDeferredProfiles.Values.OrderByDescending(profile => profile.BuildMs).FirstOrDefault(),
                settings_longest_build_to_next_arrange = settingsDeferredProfiles.Values.OrderByDescending(profile => profile.BuildToNextArrangeMs).FirstOrDefault(),
                settings_longest_staged_reveal = settingsDeferredProfiles.Values.OrderByDescending(profile => profile.MaxStagedRevealToArrangeMs).FirstOrDefault(),
                settings_longest_built_measure = settingsDeferredProfiles.Values.OrderByDescending(profile => profile.MaxBuiltMeasureMs).FirstOrDefault(),
                settings_longest_built_arrange = settingsDeferredProfiles.Values.OrderByDescending(profile => profile.MaxBuiltArrangeMs).FirstOrDefault(),
                settings_first_visual_descendants_total = settingsFirstVisualDescendantsTotal,
                settings_first_control_type_top10 = settingsFirstControlTypeTop10,
                settings_first_scroll_viewer_viewport = new
                {
                    width = Math.Round(settingsFirstViewerViewportWidth, 1),
                    height = Math.Round(settingsFirstViewerViewportHeight, 1)
                },
                settings_first_scroll_viewer_extent = new
                {
                    width = Math.Round(settingsFirstViewerExtentWidth, 1),
                    height = Math.Round(settingsFirstViewerExtentHeight, 1)
                },
                settings_first_scroll_viewer_visible_controls = settingsFirstViewerVisibleControls,
                settings_first_scroll_viewer_offscreen_controls = settingsFirstViewerOffscreenControls,
                settings_first_deferred_implemented = settingsFirstDeferredImplemented,
                settings_first_deferred_total = settingsFirstDeferredTotal,
                settings_first_data_grids = settingsFirstDataGrids,
                settings_settled_visual_descendants_total = settingsSettledVisualDescendantsTotal,
                settings_settled_visible_deferred_implemented = settingsSettledDeferredImplemented,
                settings_scrolled_deferred_implemented = settingsScrolledDeferredImplemented,
                settings_scrolled_deferred_total = settingsScrolledDeferredTotal,
                wheel_settle_ms = Math.Round(wheelSettleMs, 1),
                live_refresh_unchanged_ms = Math.Round(liveRefreshMs, 1),
                refresh_notifications_unchanged = unchangedNotifications,
                append_notifications = appended,
                refresh_actions = string.Join(",", actions),
                unchanged_row_property_events = unchangedRowPropertyEvents,
                unchanged_row_property_names = rowEventNames.Distinct().ToArray(),
                selection_kept = vm.SelectedJobIds.Contains(selectedId),
                game_input = false
            };
            if (output is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
            }
            var blockedPages = new[] { 1, 2 }.Where(page => firstPagePhases[page].MaxInputHeartbeatGapMs >= 100).ToArray();
            if (blockedPages.Length > 0) throw new InvalidOperationException("首访输入仍有超过 100ms 的同步阻塞：页面 " + string.Join("、", blockedPages));
            checks.Add("性能数值：" + JsonSerializer.Serialize(metrics));
            return checks.ToArray();
        }
        finally { vm.PrepareClose(); window?.Close(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
