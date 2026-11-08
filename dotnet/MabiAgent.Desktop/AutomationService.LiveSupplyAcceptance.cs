using System.Text.Json;
using System.Text.Json.Nodes;
using MabiAgent.Core;
using MabiAgent.Workflows;

namespace MabiAgent.Desktop;

// Explicit six-queue acceptance hook. The request file is the only entry:
// ordinary coordinator paths never call into this code and never fabricate stock.
public sealed partial class AutomationService
{
    private const long AcceptanceBottleItemId = 1202292919;
    private const string AcceptanceWaterRoute = "gathering_water_bottle_well";
    private static readonly JsonSerializerOptions LiveSupplyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Processes one live-supply request if <c>live-supply-acceptance-request.json</c>
    /// exists in this service's state directory. Returns null when no request exists.
    /// The correlated water job is released only after the real transfer/purchase
    /// has a fresh packet balance and an explicit runner completion record.
    /// </summary>
    internal async Task<JsonObject?> RunLiveSupplyAcceptanceHookAsync(CancellationToken cancellationToken = default)
    {
        var requestPath = Path.Combine(workspace.DataPath, "live-supply-acceptance-request.json");
        if (!File.Exists(requestPath)) return null;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Evaluate safety under the same gate that serializes coordinator work.
            // An active native batch or an existing pause must never be interrupted.
            if (host is null || !host.SafeToSwitch || !IsRunning || Status.PauseReason is not null)
                return null;
            return await RunLiveSupplyAcceptanceHookCoreAsync(requestPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
            wake.Signal();
        }
    }

    private async Task<JsonObject> RunLiveSupplyAcceptanceHookCoreAsync(string requestPath, CancellationToken cancellationToken)
    {
        LiveSupplyRequest request;
        try { request = ReadLiveSupplyRequest(requestPath); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var invalid = new JsonObject
            {
                ["schema_version"] = 1,
                ["status"] = "failed",
                ["error"] = error.ToString(),
                ["request_path"] = requestPath,
                ["at"] = DateTimeOffset.Now.ToString("O")
            };
            var invalidReport = Path.Combine(workspace.DataPath, "live-supply-acceptance-invalid.json");
            SaveLiveSupplyReport(invalidReport, invalid);
            return invalid;
        }

        var stateDirectory = Path.Combine(workspace.DataPath, "live-supply-acceptance", request.RequestId);
        Directory.CreateDirectory(stateDirectory);
        var reportPath = Path.Combine(stateDirectory, "report.json");
        var metaPath = Path.Combine(stateDirectory, "baseline.json");
        var report = new JsonObject
        {
            ["schema_version"] = 1,
            ["status"] = "running",
            ["started"] = DateTimeOffset.Now.ToString("O"),
            ["request_id"] = request.RequestId,
            ["mode"] = request.Mode,
            ["correlated_job_id"] = request.JobId,
            ["item_id"] = request.ItemId,
            ["item_name"] = request.Name,
            ["requested_quantity"] = request.Quantity,
            ["debug_trigger"] = new JsonObject
            {
                ["forced_path"] = request.Mode,
                ["inventory_modified"] = false,
                ["shortfall_fabricated"] = false,
                ["storage_precedence_bypassed"] = request.Mode == "force-shop"
            },
            ["report_path"] = reportPath,
            ["baseline_path"] = metaPath,
            ["trace"] = new JsonArray()
        };

        var hostWasRunning = IsRunning && Status.PauseReason is null;
        var hostPausedByHelper = false;
        var jobReleased = false;
        string? failure = null;
        var startedAt = DateTimeOffset.UtcNow;
        var deadline = startedAt.AddSeconds(request.TimeoutSeconds);
        StorageSupplyRunner? withdrawalRunner = null;
        BottlePurchase? purchaseRunner = null;
        var windowBefore = selection.Selected?.State;
        report["host_before"] = new JsonObject
        {
            ["status"] = Status.Message,
            ["pause_reason"] = Status.PauseReason,
            ["was_running"] = hostWasRunning
        };

        try
        {
            if (host is null || ports is null || session is null)
                throw new InvalidOperationException("验收供料需要已启动的正式执行 host");

            var job = ReadLiveSupplyQueue().Jobs.SingleOrDefault(item => item.Id == request.JobId)
                ?? throw new InvalidOperationException("关联水井任务不存在");
            ValidateLiveSupplyJob(job, request);
            report["job_before"] = LiveSupplyJobEvidence(job);

            host.Pause("acceptance_force_supply:" + request.RequestId);
            hostPausedByHelper = true;
            // The worker is held by this method's gate. Resuming the shared session
            // here gives the existing runner the sole input lease without changing
            // the input scheme, activating the foreground, or moving the window.
            session.Resume();

            var current = ReadLiveSupplyInventory(request);
            var meta = ReadOrCreateLiveSupplyBaseline(metaPath, request, current);
            report["before"] = meta["before"]!.DeepClone();
            var beforeBag = meta["before"]!["bottle_bag"]?.GetValue<long>()
                ?? throw new InvalidDataException("供料基线缺少空瓶背包数量");
            var beforeThrough = RequiredFinite(meta["before"]!["verified_through"], "供料基线抓包覆盖点");

            if (request.Mode == "force-storage")
            {
                var storageScope = meta["storage_scope"]?.GetValue<string>()
                    ?? throw new InvalidDataException("仓库供料基线缺少仓库范围");
                var beforeStorage = meta["before"]!["selected_storage_quantity"]?.GetValue<long>()
                    ?? throw new InvalidDataException("仓库供料基线缺少仓库数量");
                report["correlated_storage_scope"] = storageScope;

                var statePath = Path.Combine(stateDirectory, "storage-supply.json");
                var inner = new JsonWorkflowStore(statePath);
                var action = "acceptance-storage:" + request.RequestId;
                var bound = new LiveSupplyAcceptanceStore(inner, ports.Scope, action);
                var plan = new WithdrawalPlan(request.ItemId, request.Name, request.Quantity, storageScope,
                    [request.RequestId]);
                var port = ports.Storage(plan, action, bound);
                withdrawalRunner = new StorageSupplyRunner([plan], port, bound, timeoutSeconds: 20);
                if (withdrawalRunner.Snapshot()["status"]?.GetValue<string>() != "complete")
                    withdrawalRunner.Resume(true);
                await DriveLiveStorageAsync(withdrawalRunner, session, report, deadline, cancellationToken).ConfigureAwait(false);

                var after = await WaitForLiveSupplyPacketAsync(
                    request,
                    beforeThrough,
                    item => item.BagQuantity == checked(beforeBag + request.Quantity)
                        && (item.Mapped.Storage.GetValueOrDefault(storageScope)?.GetValueOrDefault(request.ItemId) ?? 0) == checked(beforeStorage - request.Quantity),
                    report,
                    deadline,
                    cancellationToken).ConfigureAwait(false);
                report["after"] = LiveSupplyInventoryEvidence(after, request.ItemId, storageScope);
            }
            else
            {
                var purchasePath = Path.Combine(stateDirectory, "purchase.json");
                var port = session.CreatePurchasePort(
                    request.ItemId,
                    request.Name,
                    "acceptance-purchase:" + request.RequestId,
                    () => purchaseRunner?.State,
                    () => ports.PurchasePreferences,
                    () => ports.Inventory());
                purchaseRunner = new BottlePurchase(
                    purchasePath,
                    port,
                    preferences: () => ports.PurchasePreferences,
                    coverage: () => ports.Inventory().Query(request.ItemId).VerifiedThrough);

                if (purchaseRunner.State.Stage == PurchaseStage.Idle)
                {
                    // The target is expressed in the real shop balance. This buys
                    // exactly the requested quantity while ignoring warehouse bottles;
                    // captured inventory and the missing-stock request remain untouched.
                    var target = checked(beforeBag + request.Quantity);
                    purchaseRunner.Request(beforeBag, targetStock: target);
                }

                if (purchaseRunner.State.Stage != PurchaseStage.Complete)
                    await DriveLivePurchaseAsync(purchaseRunner, report, deadline, cancellationToken).ConfigureAwait(false);

                var intent = purchaseRunner.State.Intent;
                if (intent is null || !purchaseRunner.State.PaymentConfirmed)
                    throw new InvalidDataException("商店供料没有已核对的付款意图");
                // The purchase runner already reconciled packet item quantity
                // and current gold before leaving the shop. Login-packet gold
                // can be absent or stale after payment; it is not a second receipt.
                var after = await WaitForLiveSupplyPacketAsync(
                    request,
                    beforeThrough,
                    item => item.BagQuantity == checked(beforeBag + request.Quantity),
                    report,
                    deadline,
                    cancellationToken).ConfigureAwait(false);
                report["after"] = LiveSupplyInventoryEvidence(after, request.ItemId, null);
                report["payment_confirmed"] = true;
                report["gold_reconciliation_source"] = "existing_purchase_runner";
                report["confirmed_after_gold"] = checked(intent.BeforeGold - intent.Price);
            }

            var latestJob = ReadLiveSupplyQueue().Jobs.SingleOrDefault(item => item.Id == request.JobId)
                ?? throw new InvalidOperationException("关联水井任务在供料期间消失");
            ValidateLiveSupplyJobRoute(latestJob, request);
            if (!latestJob.Cancelled && latestJob.Completed < latestJob.Target)
            {
                new RuntimeQueue(workspace.DataPath).Edit(queue => queue.Control(request.JobId, "continue"));
                jobReleased = true;
            }
            report["job_after"] = LiveSupplyJobEvidence(ReadLiveSupplyQueue().Jobs.Single(item => item.Id == request.JobId));
            report["job_unpaused"] = jobReleased;
            report["status"] = "complete";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = "验收供料已取消；关联任务保持暂停，请求与意图保留";
            report["status"] = "cancelled";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = error.ToString();
            report["status"] = "failed";
        }
        finally
        {
            SaveLiveSupplyFrame(stateDirectory);
            if (withdrawalRunner is not null) report["storage_runner"] = withdrawalRunner.Snapshot();
            if (purchaseRunner is not null)
            {
                report["purchase_state"] = JsonSerializer.SerializeToNode(purchaseRunner.State);
                report["purchase_intent"] = JsonSerializer.SerializeToNode(purchaseRunner.State.Intent);
            }
            report["job_unpaused"] = jobReleased;
            report["completed"] = report["status"]?.GetValue<string>() == "complete";
            report["finished"] = DateTimeOffset.Now.ToString("O");
            report["elapsed_seconds"] = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;
            if (failure is not null) report["error"] = failure;

            if (hostPausedByHelper && hostWasRunning && host is not null)
            {
                if (session?.Guard.Paused == true)
                {
                    host.Pause(session.Guard.PauseReason ?? "acceptance_input_paused");
                    report["host_resumed"] = false;
                    report["host_resume_blocked_by"] = session.Guard.PauseReason;
                }
                else
                {
                    host.Continue();
                    report["host_resumed"] = true;
                }
            }
            else report["host_resumed"] = false;

            var windowAfter = selection.Selected?.State;
            report["window_unchanged"] = windowBefore is { } beforeWindow && windowAfter is { } afterWindow
                && beforeWindow.Geometry == afterWindow.Geometry
                && beforeWindow.ForegroundId == afterWindow.ForegroundId
                && beforeWindow.Cursor == afterWindow.Cursor;

            SaveLiveSupplyReport(reportPath, report);
            if (report["status"]?.GetValue<string>() == "complete")
            {
                try
                {
                    File.Delete(requestPath);
                    report["request_consumed"] = true;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    report["request_consumed"] = false;
                    report["request_cleanup_error"] = error.Message;
                }
                SaveLiveSupplyReport(reportPath, report);
            }
            else report["request_consumed"] = false;
            NotifyChanged();
        }
        return report;
    }

    private QueueDocument ReadLiveSupplyQueue()
        => QueueDocument.Read(Path.Combine(workspace.DataPath, "work-queue.json"));
    private LiveSupplyRequest ReadLiveSupplyRequest(string path)
    {
        var request = JsonSerializer.Deserialize<LiveSupplyRequest>(
            File.ReadAllText(path),
            LiveSupplyJsonOptions)
            ?? throw new InvalidDataException("验收供料请求为空");
        if (request.SchemaVersion != 1) throw new InvalidDataException("验收供料请求版本不支持");
        request.RequestId = request.RequestId.Trim();
        request.Mode = request.Mode.Trim().ToLowerInvariant();
        request.JobId = request.JobId.Trim();
        request.Name = string.IsNullOrWhiteSpace(request.Name) ? "空瓶" : request.Name.Trim();
        request.RouteKey = string.IsNullOrWhiteSpace(request.RouteKey) ? AcceptanceWaterRoute : request.RouteKey.Trim();

        if (request.RequestId.Length is < 1 or > 96 || request.RequestId is "." or ".."
            || request.RequestId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new InvalidDataException("验收请求 request_id 只能包含字母、数字、点、下划线和连字符");
        if (request.Mode is not ("force-storage" or "force-shop"))
            throw new InvalidDataException("验收 mode 必须是 force-storage 或 force-shop");
        if (string.IsNullOrWhiteSpace(request.JobId) || request.JobId.Length > 160)
            throw new InvalidDataException("验收请求缺少有效 job_id");
        if (request.Quantity is < 1 or > 999)
            throw new InvalidDataException("验收数量必须在 1–999 之间");
        if (request.ItemId != AcceptanceBottleItemId || request.Name != "空瓶")
            throw new InvalidDataException("该验收 hook 只允许空瓶");
        if (request.RouteKey != AcceptanceWaterRoute)
            throw new InvalidDataException("该验收 hook 只允许水井采集路线");
        if (request.TimeoutSeconds is < 30 or > 900)
            throw new InvalidDataException("timeout_seconds 必须在 30–900 之间");
        return request;
    }

    private static void ValidateLiveSupplyJob(JobView job, LiveSupplyRequest request)
    {
        ValidateLiveSupplyJobRoute(job, request);
        if (!job.Paused) throw new InvalidOperationException("关联水井任务必须先暂停，供料完成后由 hook 解除");
        if (job.Pending) throw new InvalidOperationException("关联水井任务已经提交，不能在供料前释放");
    }

    private static void ValidateLiveSupplyJobRoute(JobView job, LiveSupplyRequest request)
    {
        if (job.Kind != "gathering" || job.Recipe != request.RouteKey)
            throw new InvalidOperationException("请求关联的任务不是指定水井采集路线");
        if (job.Cancelled) throw new InvalidOperationException("关联水井任务已取消");
        if (job.Completed >= job.Target) throw new InvalidOperationException("关联水井任务已经完成，不能触发供料");
    }

    private JsonObject ReadOrCreateLiveSupplyBaseline(string path, LiveSupplyRequest request, LiveSupplyInventoryRead current)
    {
        var normalized = JsonSerializer.SerializeToNode(request, LiveSupplyJsonOptions)!;
        if (File.Exists(path))
        {
            var saved = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
                ?? throw new InvalidDataException("验收供料基线损坏");
            if (!JsonNode.DeepEquals(saved["request"], normalized) || saved["before"] is not JsonObject)
                throw new InvalidDataException("验收供料基线属于其他请求");
            return saved;
        }

        var bottle = current.Mapped.Query(request.ItemId);
        if (!bottle.Confirmed || bottle.Quantity is not { } bag)
            throw new InvalidOperationException("空瓶背包数量未知，等待完整抓包");
        if (bag > long.MaxValue - request.Quantity)
            throw new InvalidOperationException("空瓶目标数量溢出");

        string? storageScope = null;
        long? storageQuantity = null;
        if (request.Mode == "force-storage")
        {
            var candidates = current.Mapped.Storage
                .Where(pair => pair.Value.GetValueOrDefault(request.ItemId) >= request.Quantity)
                .OrderByDescending(pair => pair.Key.StartsWith("character:", StringComparison.Ordinal))
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0)
                throw new InvalidOperationException("当前抓包没有任何仓库范围可取足空瓶");
            storageScope = candidates[0].Key;
            storageQuantity = candidates[0].Value[request.ItemId];
        }

        var before = LiveSupplyInventoryEvidence(current, request.ItemId, storageScope);
        before["bottle_bag"] = bag;
        var meta = new JsonObject
        {
            ["schema_version"] = 1,
            ["request"] = normalized,
            ["storage_scope"] = storageScope,
            ["before"] = before,
            ["created"] = DateTimeOffset.Now.ToString("O")
        };
        meta["before"]!["selected_storage_quantity"] = storageQuantity;
        AtomicJson.Write(path, meta);
        return meta;
    }

    private async Task DriveLiveStorageAsync(StorageSupplyRunner runner, MabiAgent.Windows.ScreenWorkflowSession session,
        JsonObject report, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        string? prior = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = runner.Snapshot();
            var signature = state["status"] + ":" + state["phase"] + ":" + state["pending"];
            if (signature != prior)
            {
                TraceLiveSupply(report, "storage", state);
                prior = signature;
                SaveLiveSupplyReport(report["report_path"]!.GetValue<string>(), report);
            }

            var status = state["status"]?.GetValue<string>();
            if (status == "complete") break;
            if (status == "paused" && state["pause_reason"]?.GetValue<string>() is "restart")
            {
                runner.Resume(true);
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (status is "unknown" or "blocked" or "paused")
                throw new InvalidOperationException("仓库取料未完成：" + state["reason"]?.GetValue<string>());
            if (ports!.BeforeNavigationTick()) continue;
            runner.Tick();
            await Task.Delay(180, cancellationToken).ConfigureAwait(false);
        }

        var final = runner.Snapshot();
        if (final["status"]?.GetValue<string>() != "complete" || final["pending"] is not null)
            throw new TimeoutException("仓库取料未在期限内完成或核账");

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.CurrentPage == "scene"
                && session.LastObservation?.View.Targets.ContainsKey("storage.room_exit") != true) return;
            runner.ReleaseNavigation();
            if (runner.NavigationAbandoned)
                throw new InvalidOperationException("仓库取料结果已确认，但退出仓库未确认");
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("仓库取料完成，但未在期限内返回场景");
    }

    private async Task DriveLivePurchaseAsync(BottlePurchase runner, JsonObject report,
        DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        string? prior = null;
        var preDispatchRejections = 0;
        var lastFrame = DateTimeOffset.MinValue;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = JsonSerializer.SerializeToNode(runner.State)!;
            var signature = runner.State.Stage + ":" + (runner.State.Intent?.Id.ToString() ?? "");
            if (signature != prior)
            {
                TraceLiveSupply(report, "shop", state);
                prior = signature;
                SaveLiveSupplyReport(report["report_path"]!.GetValue<string>(), report);
            }
            if (runner.State.Stage == PurchaseStage.Complete) return;
            if (runner.State.Stage is PurchaseStage.Blocked or PurchaseStage.UnknownStock)
                throw new InvalidOperationException("商店购买未完成：" + runner.State.Reason);
            try
            {
                if (ports!.BeforeNavigationTick()) continue;
                runner.Tick();
                preDispatchRejections = 0;
            }
            catch (InputRejectedBeforeDispatchException error) when (++preDispatchRejections <= 3)
            {
                // No game input was sent. The existing runner will reread the
                // quantity or quote before another bounded attempt.
                session!.ReportWarning("acceptance_purchase_not_sent", error.Message, "purchase");
            }
            if (DateTimeOffset.UtcNow - lastFrame >= TimeSpan.FromSeconds(3))
            {
                SaveLiveSupplyFrame(Path.GetDirectoryName(report["report_path"]!.GetValue<string>())!);
                lastFrame = DateTimeOffset.UtcNow;
            }
            await Task.Delay(220, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("商店购买未在期限内完成");
    }

    private void SaveLiveSupplyFrame(string directory)
    {
        if (session?.LastCapture is not { } capture) return;
        try
        {
            using var rgb = new OpenCvSharp.Mat(capture.Image.Height, capture.Image.Width, OpenCvSharp.MatType.CV_8UC3);
            var pixels = capture.Image.CopyPixels();
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, rgb.Data, pixels.Length);
            using var bgr = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(rgb, bgr, OpenCvSharp.ColorConversionCodes.RGB2BGR);
            OpenCvSharp.Cv2.ImWrite(Path.Combine(directory, "current.jpg"), bgr,
                new OpenCvSharp.ImageEncodingParam(OpenCvSharp.ImwriteFlags.JpegQuality, 70));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OpenCvSharp.OpenCVException)
        {
            session.ReportWarning("acceptance_frame_save_failed", error.Message, "diagnostic");
        }
    }

    private async Task<LiveSupplyInventoryRead> WaitForLiveSupplyPacketAsync(LiveSupplyRequest request,
        double beforeThrough, Func<LiveSupplyInventoryRead, bool> expected, JsonObject report,
        DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        LiveSupplyInventoryRead? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                last = ReadLiveSupplyInventory(request);
                if (last.VerifiedThrough > beforeThrough && expected(last))
                {
                    report["fresh_packet_verified"] = true;
                    return last;
                }
            }
            catch (InvalidOperationException) { }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        report["last_observed"] = last?.Mapped is null ? null : LiveSupplyInventoryEvidence(last, request.ItemId,
            report["correlated_storage_scope"]?.GetValue<string>());
        throw new TimeoutException("供料动作已结束，但未取得满足数量变化的新抓包");
    }

    private LiveSupplyInventoryRead ReadLiveSupplyInventory(LiveSupplyRequest request)
    {
        if (ports is null) throw new InvalidOperationException("正式端口尚未就绪");
        var raw = ports.VerifiedCapture() ?? throw new InvalidOperationException("当前抓包尚未通过身份、覆盖和错误检查");
        var mapped = ports.Inventory();
        var bottle = mapped.Query(request.ItemId);
        if (!bottle.Confirmed || bottle.Quantity is not { } bag)
            throw new InvalidOperationException("当前抓包没有可确认的空瓶背包数量");
        var scope = mapped.Storage
            .Where(pair => pair.Value.GetValueOrDefault(request.ItemId) >= request.Quantity)
            .OrderByDescending(pair => pair.Key.StartsWith("character:", StringComparison.Ordinal))
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key)
            .FirstOrDefault();
        var storage = scope is null ? (long?)null : mapped.Storage[scope].GetValueOrDefault(request.ItemId);
        return new(bag, storage, scope, raw.Gold, raw.Revision, raw.FullSyncRevision, raw.VerifiedThrough ?? double.NaN, mapped);
    }

    private static JsonObject LiveSupplyInventoryEvidence(LiveSupplyInventoryRead read, long itemId, string? selectedScope)
    {
        var storages = new JsonObject();
        foreach (var (scope, values) in read.Mapped.Storage.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            storages[scope] = values.GetValueOrDefault(itemId);
        return new JsonObject
        {
            ["scope"] = read.Mapped.Scope,
            ["bottle_bag"] = read.BagQuantity,
            ["storage"] = storages,
            ["selected_storage_scope"] = selectedScope,
            ["selected_storage_quantity"] = selectedScope is null
                ? null
                : read.Mapped.Storage.GetValueOrDefault(selectedScope)?.GetValueOrDefault(itemId),
            ["gold"] = read.Gold,
            ["revision"] = read.Revision,
            ["full_sync_revision"] = read.FullSyncRevision,
            ["verified_through"] = read.VerifiedThrough
        };
    }

    private static JsonObject LiveSupplyJobEvidence(JobView job) => new()
    {
        ["id"] = job.Id,
        ["kind"] = job.Kind,
        ["route_key"] = job.Recipe,
        ["target"] = job.Target,
        ["completed"] = job.Completed,
        ["paused"] = job.Paused,
        ["cancelled"] = job.Cancelled,
        ["pending"] = job.Pending
    };

    private static double RequiredFinite(JsonNode? node, string label)
    {
        var value = node?.GetValue<double>() ?? double.NaN;
        if (!double.IsFinite(value)) throw new InvalidDataException(label + "无效");
        return value;
    }

    private static void TraceLiveSupply(JsonObject report, string stage, JsonNode state)
    {
        if (report["trace"] is not JsonArray trace) return;
        trace.Add(new JsonObject
        {
            ["at"] = DateTimeOffset.Now.ToString("O"),
            ["stage"] = stage,
            ["state"] = state.DeepClone()
        });
        while (trace.Count > 200) trace.RemoveAt(0);
    }

    private static void SaveLiveSupplyReport(string path, JsonObject report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicJson.Write(path, report);
    }

    private sealed class LiveSupplyAcceptanceStore(IWorkflowStore inner, string scope, string action) : IWorkflowStore
    {
        public JsonObject? Read() => inner.Read();
        public void Flush(JsonObject state)
        {
            state["runtime_scope"] = scope;
            state["runtime_action"] = action;
            state["runtime_source"] = "live_supply_acceptance";
            inner.Flush(state);
        }
    }

    private sealed class LiveSupplyRequest
    {
        public int SchemaVersion { get; set; } = 1;
        public string RequestId { get; set; } = "";
        public string Mode { get; set; } = "";
        public string JobId { get; set; } = "";
        public long Quantity { get; set; } = 10;
        public long ItemId { get; set; } = AcceptanceBottleItemId;
        public string Name { get; set; } = "空瓶";
        public string RouteKey { get; set; } = AcceptanceWaterRoute;
        public int TimeoutSeconds { get; set; } = 300;
    }

    private sealed record LiveSupplyInventoryRead(
        long BagQuantity,
        long? SelectedStorageQuantity,
        string? SelectedStorageScope,
        long? Gold,
        long Revision,
        long FullSyncRevision,
        double VerifiedThrough,
        InventorySnapshot Mapped);
}




