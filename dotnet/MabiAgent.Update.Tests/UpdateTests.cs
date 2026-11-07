using MabiAgent.Update;
using Xunit;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MabiAgent.Update.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mabi-update-test-" + Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(root);
    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static long Len(string text) => Encoding.UTF8.GetByteCount(text);
    private string Write(string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
    private void Baseline(params (string Path, string Body)[] files)
    {
        var array = new JsonArray();
        foreach (var (path, body) in files)
            array.Add(new JsonObject { ["path"] = path, ["bytes"] = body.Length, ["sha256"] = Sha(Encoding.UTF8.GetBytes(body)) });
        File.WriteAllText(Path.Combine(root, InstallManifest.FileName),
            new JsonObject { ["schema_version"] = 1, ["files"] = array }.ToJsonString());
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C://windows//system32.dll")]
    [InlineData("data/work-queue.json")]
    [InlineData("Data/settings.json")]
    [InlineData("CON")]
    [InlineData("app/aux.dll")]
    [InlineData("a//b.txt")]
    public void ManifestRejectsUnsafePaths(string path)
    {
        var json = new JsonObject
        {
            ["app_id"] = "MabiAgent", ["schema"] = 1, ["version"] = "0.6.0",
            ["files"] = new JsonArray(new JsonObject { ["path"] = path, ["size"] = 1, ["sha256"] = new string('a', 64) })
        }.ToJsonString();
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(json));
    }

    [Fact]
    public void ManifestRequiresEveryField()
    {
        var missing = new JsonObject
        {
            ["app_id"] = "MabiAgent", ["schema"] = 1, ["version"] = "0.6.0",
            ["files"] = new JsonArray(new JsonObject { ["path"] = "app/a.dll", ["size"] = 1 })
        }.ToJsonString();
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(missing));
        var wrongApp = new JsonObject
        {
            ["app_id"] = "Other", ["schema"] = 1, ["version"] = "0.6.0",
            ["files"] = new JsonArray(new JsonObject { ["path"] = "app/a.dll", ["size"] = 1, ["sha256"] = new string('a', 64) })
        }.ToJsonString();
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(wrongApp));
    }

    [Fact]
    public async Task DirectReleasePageReadsRelativeLinksAndDigestsWithoutApiRequests()
    {
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0", ("MabiAgent-0.6.0-win-x64.zip", 1000, null)));
        handler.Bytes("https://github.com/MinamiChiwa/MabiAgent/releases/expanded_assets/v0.6.0", Encoding.UTF8.GetBytes("""
            <ul><li class="Box-row"><div><a rel="nofollow" href='/MinamiChiwa/MabiAgent/releases/download/v0.6.0/MabiAgent-0.6.0-win-x64.zip'>package</a></div>
            <span class="Truncate-text">sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</span><span>139 MB</span></li>
            <li><a href="/MinamiChiwa/MabiAgent/archive/refs/tags/v0.6.0.zip">Source code</a></li></ul>
            """));
        using var transport = new UpdateTransport(handler, new TransportOptions("https://v6.gh-proxy.org/"));
        var release = await transport.LatestReleaseAsync(UpdateService.Repository, default);
        Assert.Equal("0.6.0", release.Version);
        var asset = Assert.Single(release.Assets);
        Assert.Equal(0, asset.Size);
        Assert.Equal("sha256:" + new string('a', 64), asset.Digest);
        Assert.Equal(Asset("v0.6.0", asset.Name), asset.Url.AbsoluteUri);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, url => Assert.Equal("github.com", new Uri(url).Host));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/MinamiChiwa/MabiAgent/releases/latest")]
    [InlineData("https://v6.gh-proxy.org/https://github.com/MinamiChiwa/MabiAgent/releases/latest")]
    public async Task DirectReleaseCheckRejectsApiAndMirrorRedirects(string destination)
    {
        var handler = new FakeHandler();
        handler.Redirect(Latest, destination);
        using var transport = new UpdateTransport(handler, new TransportOptions("https://v6.gh-proxy.org/"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.LatestReleaseAsync(UpdateService.Repository, default));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("https://github.com/other/project/releases/tag/v0.6.0")]
    [InlineData("https://github.com/login")]
    public async Task DirectReleaseCheckRequiresTheRequestedRepositoryTag(string destination)
    {
        var handler = new FakeHandler();
        handler.Redirect(Latest, destination);
        handler.Bytes(destination, []);
        using var transport = new UpdateTransport(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.LatestReleaseAsync(UpdateService.Repository, default));
    }

    [Theory]
    [InlineData("<html>Sign in</html>")]
    [InlineData("<ul><li><a href='/MinamiChiwa/MabiAgent/archive/refs/tags/v0.6.0.zip'>Source code</a></li></ul>")]
    public async Task MissingDirectAssetsFailsInsteadOfClaimingAnUpdate(string html)
    {
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0", ("MabiAgent-0.6.0-win-x64.zip", 1000, null)));
        handler.Bytes("https://github.com/MinamiChiwa/MabiAgent/releases/expanded_assets/v0.6.0", Encoding.UTF8.GetBytes(html));
        using var transport = new UpdateTransport(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.LatestReleaseAsync(UpdateService.Repository, default));
    }

    [Fact]
    public async Task LocalVersionAboveThePublishedOneIsNotAnUpdate()
    {
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.5.4", ("MabiAgent-0.5.4-win-x64.zip", 1000, new string('b', 64))));
        using var service = new UpdateService(root, handler);
        var check = await service.CheckAsync("0.5.8");
        Assert.False(check.Available);
        Assert.Contains("尚未发布", check.Summary);
    }

    [Fact]
    public async Task ReleaseWithoutManifestIsReportedAsFullPackageOnly()
    {
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0", ("MabiAgent-0.6.0-win-x64.zip", 1000, new string('b', 64))));
        using var service = new UpdateService(root, handler);
        var check = await service.CheckAsync("0.5.8");
        Assert.True(check.Available);
        Assert.False(check.HasManifest);
        Assert.Contains("完整包", check.Summary);
    }

    [Fact]
    public async Task ManifestDigestFromTheReleasePageIsEnforced()
    {
        var manifest = ManifestJson("0.6.0", ("app/a.dll", Encoding.UTF8.GetBytes("new")));
        var bytes = Encoding.UTF8.GetBytes(manifest);
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0", (UpdateManifest.FileName, bytes.LongLength, new string('0', 64))));
        handler.Bytes(Asset("v0.6.0", UpdateManifest.FileName), bytes);
        using var service = new UpdateService(root, handler);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync("0.5.8"));
        Assert.Contains("校验失败", error.Message);
    }

    [Fact]
    public async Task ManifestWithoutDigestStillUsesTheOfficialChannel()
    {
        var manifest = ManifestJson("0.6.0", ("app/a.dll", Encoding.UTF8.GetBytes("new")));
        var bytes = Encoding.UTF8.GetBytes(manifest);
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0", (UpdateManifest.FileName, bytes.LongLength, null)));
        handler.Bytes(Asset("v0.6.0", UpdateManifest.FileName), bytes);
        using var service = new UpdateService(root, handler, new TransportOptions("https://v6.gh-proxy.org/"));
        var check = await service.CheckAsync("0.5.8");
        Assert.True(check.HasManifest);
        Assert.DoesNotContain(handler.Requests, url => url.Contains("gh-proxy.org", StringComparison.Ordinal));
        Assert.Contains(service.Notes, note => note.Contains("未提供清单摘要", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManifestIsNeverFetchedThroughTheMirror()
    {
        var manifest = ManifestJson("0.6.0", ("app/a.dll", Encoding.UTF8.GetBytes("new")));
        var bytes = Encoding.UTF8.GetBytes(manifest);
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0", (UpdateManifest.FileName, bytes.LongLength, Sha(bytes))));
        handler.Bytes(Asset("v0.6.0", UpdateManifest.FileName), bytes);
        using var service = new UpdateService(root, handler, new TransportOptions("https://v6.gh-proxy.org/"));
        _ = await service.CheckAsync("0.5.8");
        Assert.DoesNotContain(handler.Requests, url => url.Contains("gh-proxy.org", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManifestRedirectToTheMirrorIsRefused()
    {
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0", (UpdateManifest.FileName, 10, new string('a', 64))));
        handler.Redirect(Asset("v0.6.0", UpdateManifest.FileName), "https://v6.gh-proxy.org/https://github.com/x");
        using var service = new UpdateService(root, handler, new TransportOptions("https://v6.gh-proxy.org/"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync("0.5.8"));
        Assert.Contains("不允许的更新主机", error.Message);
        Assert.DoesNotContain(handler.Requests, url => url.Contains("gh-proxy.org", StringComparison.Ordinal));
    }

    [Fact]
    public void LibraryCreatedHandlerNeverFollowsRedirectsAutomatically()
    {
        var handler = new HttpClientHandler();
        using var transport = new UpdateTransport(handler);
        Assert.False(handler.AllowAutoRedirect);
        var sockets = new SocketsHttpHandler { AllowAutoRedirect = true };
        using var second = new UpdateTransport(sockets);
        Assert.False(sockets.AllowAutoRedirect);
    }

    [Fact]
    public async Task ForeignAssetHostIsRejected()
    {
        var handler = new FakeHandler();
        handler.Release(new JsonObject
        {
            ["tag_name"] = "v0.6.0",
            ["assets"] = new JsonArray(new JsonObject
            {
                ["name"] = UpdateManifest.FileName, ["size"] = 10, ["digest"] = "sha256:" + new string('a', 64),
                ["browser_download_url"] = "https://evil.example.com/MinamiChiwa/MabiAgent/releases/download/v0.6.0/update-manifest.json"
            })
        }.ToJsonString());
        using var service = new UpdateService(root, handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync("0.5.8"));
        Assert.Contains("不允许的更新主机", error.Message);
    }

    [Fact]
    public async Task OnlyChangedFilesAreDownloadedAndVerified()
    {
        var same = Encoding.UTF8.GetBytes("unchanged");
        var changed = Encoding.UTF8.GetBytes("brand new content");
        Write("app/same.dll", "unchanged");
        Baseline(("app/same.dll", "unchanged"));
        var manifest = ManifestJson("0.6.0", ("app/same.dll", same), ("app/changed.dll", changed));
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        const string tag = "v0.6.0";
        var handler = new FakeHandler();
        handler.Release(ReleaseJson(tag,
            (UpdateManifest.FileName, manifestBytes.LongLength, Sha(manifestBytes)),
            ("blob-" + Sha(same) + ".bin", same.LongLength, Sha(same)),
            ("blob-" + Sha(changed) + ".bin", changed.LongLength, Sha(changed))));
        handler.Bytes(Asset(tag, UpdateManifest.FileName), manifestBytes);
        handler.Bytes(Asset(tag, "blob-" + Sha(same) + ".bin"), same);
        handler.Bytes(Asset(tag, "blob-" + Sha(changed) + ".bin"), changed);
        using var service = new UpdateService(root, handler);
        var check = await service.CheckAsync("0.5.8");
        var plan = await service.PrepareAsync(check);
        Assert.NotNull(plan);
        var product = plan!.Files.Where(f => !UpdateApplier.IsBaseline(f.RelativePath)).ToList();
        var file = Assert.Single(product);
        Assert.Equal("app/changed.dll", file.RelativePath);
        Assert.Equal("brand new content", File.ReadAllText(file.StagedPath));
        Assert.StartsWith(Path.Combine(root, "updates", "staging", "0.6.0"), file.StagedPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(handler.Requests, url => url.Contains("blob-" + Sha(same), StringComparison.Ordinal));
    }

    [Fact]
    public async Task UserModifiedFileIsKeptAndReportedAsConflict()
    {
        Write("app/config.json", "user edited");
        Baseline(("app/config.json", "shipped"));
        var next = Encoding.UTF8.GetBytes("shipped next");
        var manifest = ManifestJson("0.6.0", ("app/config.json", next));
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0",
            (UpdateManifest.FileName, manifestBytes.LongLength, Sha(manifestBytes)),
            ("blob-" + Sha(next) + ".bin", next.LongLength, Sha(next))));
        handler.Bytes(Asset("v0.6.0", UpdateManifest.FileName), manifestBytes);
        handler.Bytes(Asset("v0.6.0", "blob-" + Sha(next) + ".bin"), next);
        using var service = new UpdateService(root, handler);
        var plan = await service.PrepareAsync(await service.CheckAsync("0.5.8"));
        Assert.NotNull(plan);
        Assert.All(plan!.Files, f => Assert.True(UpdateApplier.IsBaseline(f.RelativePath)));
        Assert.Contains(plan.Conflicts!, c => c.StartsWith("app/config.json", StringComparison.Ordinal));
        Assert.Equal("user edited", File.ReadAllText(Path.Combine(root, "app", "config.json")));
        Assert.DoesNotContain(handler.Requests, url => url.Contains("blob-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutInstallBaselineExistingFilesAreNeverOverwritten()
    {
        Write("app/keep.dll", "user content");
        var next = Encoding.UTF8.GetBytes("next");
        var manifest = ManifestJson("0.6.0", ("app/keep.dll", next));
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0",
            (UpdateManifest.FileName, manifestBytes.LongLength, Sha(manifestBytes)),
            ("blob-" + Sha(next) + ".bin", next.LongLength, Sha(next))));
        handler.Bytes(Asset("v0.6.0", UpdateManifest.FileName), manifestBytes);
        handler.Bytes(Asset("v0.6.0", "blob-" + Sha(next) + ".bin"), next);
        using var service = new UpdateService(root, handler);
        var plan = await service.PrepareAsync(await service.CheckAsync("0.5.8"));
        Assert.All(plan!.Files, f => Assert.True(UpdateApplier.IsBaseline(f.RelativePath)));
        Assert.Contains(plan.Conflicts!, c => c.Contains("缺少安装基准清单", StringComparison.Ordinal));
        Assert.Equal("user content", File.ReadAllText(Path.Combine(root, "app", "keep.dll")));
    }

    [Fact]
    public async Task DeletedLocalFileIsKeptAndReportedAsConflict()
    {
        Baseline(("app/missing.dll", "shipped"), ("app/kept.dll", "shipped"));
        var next = Encoding.UTF8.GetBytes("restored");
        var manifest = ManifestJson("0.6.0", ("app/missing.dll", next), ("app/kept.dll", Encoding.UTF8.GetBytes("shipped2")));
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0",
            (UpdateManifest.FileName, manifestBytes.LongLength, Sha(manifestBytes)),
            ("blob-" + Sha(next) + ".bin", next.LongLength, Sha(next))));
        handler.Bytes(Asset("v0.6.0", UpdateManifest.FileName), manifestBytes);
        handler.Bytes(Asset("v0.6.0", "blob-" + Sha(next) + ".bin"), next);
        using var service = new UpdateService(root, handler);
        var plan = await service.PrepareAsync(await service.CheckAsync("0.5.8"));
        Assert.NotNull(plan);
        Assert.DoesNotContain(plan!.Files, f => !UpdateApplier.IsBaseline(f.RelativePath));
        Assert.Contains(plan.Conflicts!, c => c.StartsWith("app/missing.dll", StringComparison.Ordinal) && c.Contains("本地已删除", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(root, "app", "missing.dll")));
    }

    [Fact]
    public async Task FilesRemovedUpstreamArePlannedForDeletionNotDropped()
    {
        Write("app/old.dll", "old");
        Baseline(("app/old.dll", "old"));
        var next = Encoding.UTF8.GetBytes("next");
        var manifest = ManifestJson("0.6.0", ("app/new.dll", next));
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0",
            (UpdateManifest.FileName, manifestBytes.LongLength, Sha(manifestBytes)),
            ("blob-" + Sha(next) + ".bin", next.LongLength, Sha(next))));
        handler.Bytes(Asset("v0.6.0", UpdateManifest.FileName), manifestBytes);
        handler.Bytes(Asset("v0.6.0", "blob-" + Sha(next) + ".bin"), next);
        using var service = new UpdateService(root, handler);
        var plan = await service.PrepareAsync(await service.CheckAsync("0.5.8"));
        var obsolete = Assert.Single(plan!.Obsolete!);
        Assert.Equal("app/old.dll", obsolete.RelativePath);
        Assert.Equal(Sha(Encoding.UTF8.GetBytes("old")), obsolete.Sha256);
        // Prepare never deletes by itself; the applier does, and only what is still pristine.
        Assert.Equal("old", File.ReadAllText(Path.Combine(root, "app", "old.dll")));
        var result = UpdateApplier.Apply(plan, Path.Combine(root, "updates", "backup", "0.6.0"));
        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(root, "app", "old.dll")));
        Assert.Contains("app/old.dll", result.Removed!);
    }

    [Fact]
    public async Task EditedObsoleteFileIsKeptAndReported()
    {
        Write("app/old.dll", "user tweaked");
        Baseline(("app/old.dll", "old"));
        var next = Encoding.UTF8.GetBytes("next");
        var manifest = ManifestJson("0.6.0", ("app/new.dll", next));
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0",
            (UpdateManifest.FileName, manifestBytes.LongLength, Sha(manifestBytes)),
            ("blob-" + Sha(next) + ".bin", next.LongLength, Sha(next))));
        handler.Bytes(Asset("v0.6.0", UpdateManifest.FileName), manifestBytes);
        handler.Bytes(Asset("v0.6.0", "blob-" + Sha(next) + ".bin"), next);
        using var service = new UpdateService(root, handler);
        var plan = await service.PrepareAsync(await service.CheckAsync("0.5.8"));
        var result = UpdateApplier.Apply(plan!, Path.Combine(root, "updates", "backup", "0.6.0"));
        Assert.True(result.Success);
        Assert.Equal("user tweaked", File.ReadAllText(Path.Combine(root, "app", "old.dll")));
        Assert.Contains(result.Kept!, k => k.StartsWith("app/old.dll", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ObsoleteOnlyChangeStillProducesAPlan()
    {
        Write("app/gone.dll", "gone");
        Baseline(("app/gone.dll", "gone"));
        var same = Encoding.UTF8.GetBytes("same");
        Write("app/same.dll", "same");
        var manifest = ManifestJson("0.6.0", ("app/same.dll", same));
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0", (UpdateManifest.FileName, manifestBytes.LongLength, Sha(manifestBytes))));
        handler.Bytes(Asset("v0.6.0", UpdateManifest.FileName), manifestBytes);
        // A second baseline entry keeps the first from being the only difference.
        File.WriteAllText(Path.Combine(root, InstallManifest.FileName),
            new JsonObject
            {
                ["schema_version"] = 1,
                ["files"] = new JsonArray(
                    new JsonObject { ["path"] = "app/same.dll", ["bytes"] = same.Length, ["sha256"] = Sha(same) },
                    new JsonObject { ["path"] = "app/gone.dll", ["bytes"] = 4, ["sha256"] = Sha(Encoding.UTF8.GetBytes("gone")) })
            }.ToJsonString());
        using var service = new UpdateService(root, handler);
        var plan = await service.PrepareAsync(await service.CheckAsync("0.5.8"));
        Assert.NotNull(plan);
        Assert.DoesNotContain(plan!.Files, f => !UpdateApplier.IsBaseline(f.RelativePath));
        Assert.Single(plan.Obsolete!);
    }

    [Fact]
    public async Task BaselineIsPartOfThePlanSoASecondUpdateSeesPristineFiles()
    {
        Write("app/a.dll", "v1");
        Baseline(("app/a.dll", "v1"));
        var v2 = Encoding.UTF8.GetBytes("v2");
        var manifest = ManifestJson("0.6.0", ("app/a.dll", v2));
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0",
            (UpdateManifest.FileName, manifestBytes.LongLength, Sha(manifestBytes)),
            ("blob-" + Sha(v2) + ".bin", v2.LongLength, Sha(v2))));
        handler.Bytes(Asset("v0.6.0", UpdateManifest.FileName), manifestBytes);
        handler.Bytes(Asset("v0.6.0", "blob-" + Sha(v2) + ".bin"), v2);
        using var service = new UpdateService(root, handler);
        var plan = await service.PrepareAsync(await service.CheckAsync("0.5.8"));
        Assert.Contains(plan!.Files, f => UpdateApplier.IsBaseline(f.RelativePath));
        var result = UpdateApplier.Apply(plan, Path.Combine(root, "updates", "backup", "0.6.0"));
        Assert.True(result.Success);
        var baseline = InstallManifest.Load(root);
        Assert.NotNull(baseline);
        Assert.Equal(Sha(v2), baseline!["app/a.dll"]);

        // Second run: the installed file now matches the baseline, so nothing is a "user edit".
        var handler2 = new FakeHandler();
        var v3 = Encoding.UTF8.GetBytes("v3");
        var manifest2 = ManifestJson("0.6.1", ("app/a.dll", v3));
        var bytes2 = Encoding.UTF8.GetBytes(manifest2);
        handler2.Release(ReleaseJson("v0.6.1",
            (UpdateManifest.FileName, bytes2.LongLength, Sha(bytes2)),
            ("blob-" + Sha(v3) + ".bin", v3.LongLength, Sha(v3))));
        handler2.Bytes(Asset("v0.6.1", UpdateManifest.FileName), bytes2);
        handler2.Bytes(Asset("v0.6.1", "blob-" + Sha(v3) + ".bin"), v3);
        using var next = new UpdateService(root, handler2);
        var second = await next.PrepareAsync(await next.CheckAsync("0.6.0"));
        Assert.DoesNotContain(second!.Conflicts!, c => c.Contains("本地已修改", StringComparison.Ordinal));
        Assert.Single(second.Files, f => f.RelativePath == "app/a.dll");
    }

    [Fact]
    public async Task StagingStaysUnderTheToolDirectoryEvenForAHostileVersion()
    {
        var next = Encoding.UTF8.GetBytes("next");
        var manifest = ManifestJson("..", ("app/a.dll", next));
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v..",
            (UpdateManifest.FileName, manifestBytes.LongLength, Sha(manifestBytes)),
            ("blob-" + Sha(next) + ".bin", next.LongLength, Sha(next))));
        handler.Bytes(Asset("v..", UpdateManifest.FileName), manifestBytes);
        handler.Bytes(Asset("v..", "blob-" + Sha(next) + ".bin"), next);
        using var service = new UpdateService(root, handler);
        var check = await service.CheckAsync("0.5.8");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(check));
        Assert.False(Directory.Exists(Path.GetFullPath(Path.Combine(root, "staging"))));
    }

    [Fact]
    public async Task MirrorServingWrongBytesFallsBackToTheOfficialHost()
    {
        var payload = Encoding.UTF8.GetBytes("payload");
        var manifest = ManifestJson("0.6.0", ("app/changed.dll", payload));
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        const string tag = "v0.6.0";
        var handler = new FakeHandler();
        handler.Release(ReleaseJson(tag,
            (UpdateManifest.FileName, manifestBytes.LongLength, Sha(manifestBytes)),
            ("blob-" + Sha(payload) + ".bin", payload.LongLength, Sha(payload))));
        handler.Bytes(Asset(tag, UpdateManifest.FileName), manifestBytes);
        handler.Bytes(Asset(tag, "blob-" + Sha(payload) + ".bin"), payload);
        handler.MirrorBytes["https://v6.gh-proxy.org/" + Asset(tag, "blob-" + Sha(payload) + ".bin")] = Encoding.UTF8.GetBytes("tampered by mirror");
        using var service = new UpdateService(root, handler, new TransportOptions("https://v6.gh-proxy.org/"));
        var plan = await service.PrepareAsync(await service.CheckAsync("0.5.8"));
        Assert.NotNull(plan);
        Assert.Contains(service.Notes, note => note.Contains("镜像内容与清单不符", StringComparison.Ordinal));
        Assert.Equal("payload", File.ReadAllText(Assert.Single(plan!.Files, f => !UpdateApplier.IsBaseline(f.RelativePath)).StagedPath));
    }

    [Fact]
    public async Task MirrorDroppingMidStreamFallsBackToTheOfficialHost()
    {
        var payload = Encoding.UTF8.GetBytes("payload");
        var manifest = ManifestJson("0.6.0", ("app/changed.dll", payload));
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        const string tag = "v0.6.0";
        var handler = new FakeHandler();
        handler.Release(ReleaseJson(tag,
            (UpdateManifest.FileName, manifestBytes.LongLength, Sha(manifestBytes)),
            ("blob-" + Sha(payload) + ".bin", payload.LongLength, Sha(payload))));
        handler.Bytes(Asset(tag, UpdateManifest.FileName), manifestBytes);
        handler.Bytes(Asset(tag, "blob-" + Sha(payload) + ".bin"), payload);
        handler.BrokenMirror = true;
        using var service = new UpdateService(root, handler, new TransportOptions("https://v6.gh-proxy.org/"));
        var plan = await service.PrepareAsync(await service.CheckAsync("0.5.8"));
        Assert.NotNull(plan);
        Assert.Contains(service.Notes, note => note.Contains("镜像下载中断", StringComparison.Ordinal));
        Assert.Equal("payload", File.ReadAllText(Assert.Single(plan!.Files, f => !UpdateApplier.IsBaseline(f.RelativePath)).StagedPath));
    }

    [Fact]
    public async Task FullPackageWithoutDigestOrChecksumFileIsRejected()
    {
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0", ("MabiAgent-0.6.0-win-x64.zip", 1000, null)));
        using var service = new UpdateService(root, handler);
        var check = await service.CheckAsync("0.5.8");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadFullPackageAsync(check.Release!, Path.Combine(root, "updates")));
        Assert.Contains("拒绝使用未校验的包", error.Message);
    }

    [Fact]
    public async Task FullPackageUsesTrustedChecksumFileWhenDigestIsMissing()
    {
        var zip = Encoding.UTF8.GetBytes("zip body");
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0",
            ("MabiAgent-0.6.0-win-x64.zip", zip.LongLength, null),
            ("SHA256SUMS.txt", Len(Sha(zip) + "  MabiAgent-0.6.0-win-x64.zip\n"), null)));
        handler.Bytes(Asset("v0.6.0", "MabiAgent-0.6.0-win-x64.zip"), zip);
        handler.Bytes(Asset("v0.6.0", "SHA256SUMS.txt"), Encoding.UTF8.GetBytes(Sha(zip) + "  MabiAgent-0.6.0-win-x64.zip\n"));
        using var service = new UpdateService(root, handler);
        var check = await service.CheckAsync("0.5.8");
        var package = await service.DownloadFullPackageAsync(check.Release!, Path.Combine(root, "updates"));
        Assert.NotNull(package);
        Assert.Equal(Sha(zip), package!.Value.Sha256);
    }

    [Fact]
    public void ApplierRestoresPreviousFilesWhenOneWriteFails()
    {
        Write("app/keep.dll", "old");
        var staged = Path.Combine(root, "updates", "staging", "0.6.0", "app", "keep.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, "new");
        var missing = Path.Combine(root, "updates", "staging", "0.6.0", "app", "missing.dll");
        var plan = new UpdatePlan("0.6.0", root,
        [
            new PlannedFile("app/keep.dll", staged, Sha(Encoding.UTF8.GetBytes("new")), 3),
            new PlannedFile("app/missing.dll", missing, Sha(Encoding.UTF8.GetBytes("gone")), 4)
        ], false, null);
        var result = UpdateApplier.Apply(plan, Path.Combine(root, "updates", "backup", "0.6.0"));
        Assert.False(result.Success);
        Assert.Contains("暂存文件缺失", result.Error);
        Assert.Equal("old", File.ReadAllText(Path.Combine(root, "app", "keep.dll")));
        Assert.False(File.Exists(Path.Combine(root, "app", "missing.dll")));
        Assert.False(File.Exists(Path.Combine(root, "app", "keep.dll.new")));
        Assert.False(File.Exists(Path.Combine(root, "updates", UpdateApplier.JournalName)));
    }

    [Fact]
    public void ApplierWritesChangedFilesAndKeepsABackup()
    {
        Write("app/keep.dll", "old");
        var staged = Path.Combine(root, "updates", "staging", "0.6.0", "app", "keep.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, "new");
        var plan = new UpdatePlan("0.6.0", root, [new PlannedFile("app/keep.dll", staged, Sha(Encoding.UTF8.GetBytes("new")), 3)], false, null);
        var backup = Path.Combine(root, "updates", "backup", "0.6.0");
        var result = UpdateApplier.Apply(plan, backup);
        Assert.True(result.Success);
        Assert.Equal("new", File.ReadAllText(Path.Combine(root, "app", "keep.dll")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(backup, "app", "keep.dll")));
        Assert.False(File.Exists(Path.Combine(root, "updates", UpdateApplier.JournalName)));
    }

    [Fact]
    public void InterruptedApplyIsRolledBackFromTheJournal()
    {
        Write("app/keep.dll", "new");
        var backup = Path.Combine(root, "updates", "backup", "0.6.0", "app", "keep.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "old");
        var journal = new JsonObject
        {
            ["status"] = "applying", ["version"] = "0.6.0", ["install_root"] = root,
            ["backup_root"] = Path.Combine(root, "updates", "backup", "0.6.0"),
            ["entries"] = new JsonArray(new JsonObject
            {
                ["path"] = "app/keep.dll", ["target"] = Path.Combine(root, "app", "keep.dll"),
                ["backup"] = backup, ["created"] = false, ["state"] = "applied"
            })
        };
        Directory.CreateDirectory(Path.Combine(root, "updates"));
        File.WriteAllText(Path.Combine(root, "updates", UpdateApplier.JournalName), journal.ToJsonString());
        var note = UpdateApplier.Recover(root);
        Assert.True(note.Ok, note.Note);
        Assert.Contains("崩溃恢复", note.Note);
        Assert.Equal("old", File.ReadAllText(Path.Combine(root, "app", "keep.dll")));
        Assert.False(File.Exists(Path.Combine(root, "updates", UpdateApplier.JournalName)));
    }

    [Fact]
    public void PlanSurvivesARoundTripThroughJson()
    {
        var plan = new UpdatePlan("0.6.0", "D://game", [new PlannedFile("app/a.dll", "D://tmp//a.dll", new string('a', 64), 12)], false, null,
            ["app/b.dll：本地已修改"], [new ObsoleteFile("app/old.dll", new string('b', 64))]);
        var copy = UpdatePlan.FromJson(plan.ToJson());
        Assert.Equal(plan.InstallRoot, copy.InstallRoot);
        Assert.Equal(plan.Files, copy.Files);
        Assert.Equal(plan.Conflicts, copy.Conflicts);
        Assert.Equal(plan.Obsolete, copy.Obsolete);
    }

    [Fact]
    public void MovingEntryLeftByACrashIsRestored()
    {
        // The journal is persisted as "moving" before the target is touched, which is the
        // only record that survives a crash between File.Move and the "applied" update.
        Write("app/keep.dll", "new");
        var backup = Path.Combine(root, "updates", "backup", "0.6.0", "app", "keep.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "old");
        var journal = new JsonObject
        {
            ["status"] = "applying", ["version"] = "0.6.0", ["install_root"] = root,
            ["backup_root"] = Path.Combine(root, "updates", "backup", "0.6.0"),
            ["entries"] = new JsonArray(new JsonObject
            {
                ["path"] = "app/keep.dll", ["target"] = Path.Combine(root, "app", "keep.dll"),
                ["backup"] = backup, ["created"] = false, ["state"] = "moving",
                ["sha256"] = Sha(Encoding.UTF8.GetBytes("new"))
            })
        };
        Directory.CreateDirectory(Path.Combine(root, "updates"));
        File.WriteAllText(Path.Combine(root, "updates", UpdateApplier.JournalName), journal.ToJsonString());
        _ = UpdateApplier.Recover(root);
        Assert.Equal("old", File.ReadAllText(Path.Combine(root, "app", "keep.dll")));
    }

    [Fact]
    public void PlannedEntryIsLeftAloneByRecovery()
    {
        // "planned" means the write never started; nothing is inferred from the file hash.
        Write("app/keep.dll", "untouched");
        var backup = Path.Combine(root, "updates", "backup", "0.6.0", "app", "keep.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "old");
        var journal = new JsonObject
        {
            ["status"] = "applying", ["version"] = "0.6.0", ["install_root"] = root,
            ["backup_root"] = Path.Combine(root, "updates", "backup", "0.6.0"),
            ["entries"] = new JsonArray(new JsonObject
            {
                ["path"] = "app/keep.dll", ["target"] = Path.Combine(root, "app", "keep.dll"),
                ["backup"] = backup, ["created"] = false, ["state"] = "planned"
            })
        };
        Directory.CreateDirectory(Path.Combine(root, "updates"));
        var journalPath = Path.Combine(root, "updates", UpdateApplier.JournalName);
        File.WriteAllText(journalPath, journal.ToJsonString());
        var note = UpdateApplier.Recover(root);
        Assert.True(note.Ok, note.Note);
        Assert.Contains("没有需要还原", note.Note);
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(root, "app", "keep.dll")));
        Assert.False(File.Exists(journalPath));
    }

    [Fact]
    public void FailureRightAfterTheReplaceStillRestoresEverything()
    {
        Write("app/a.dll", "old-a");
        Write("app/b.dll", "old-b");
        var staging = Path.Combine(root, "updates", "staging", "0.6.0");
        Directory.CreateDirectory(Path.Combine(staging, "app"));
        File.WriteAllText(Path.Combine(staging, "app", "a.dll"), "new-a");
        File.WriteAllText(Path.Combine(staging, "app", "b.dll"), "new-b");
        var plan = new UpdatePlan("0.6.0", root,
        [
            new PlannedFile("app/a.dll", Path.Combine(staging, "app", "a.dll"), Sha(Encoding.UTF8.GetBytes("new-a")), 5),
            new PlannedFile("app/b.dll", Path.Combine(staging, "app", "b.dll"), Sha(Encoding.UTF8.GetBytes("new-b")), 5)
        ], false, null);
        var backup = Path.Combine(root, "updates", "backup", "0.6.0");
        try
        {
            // Crash right after the first file was replaced, before the journal says "applied".
            UpdateApplier.AfterReplaceForTesting = path => { if (path == "app/a.dll") throw new IOException("模拟：替换后、写日志前失败"); };
            var result = UpdateApplier.Apply(plan, backup);
            Assert.False(result.Success);
            Assert.Contains("模拟", result.Error);
            Assert.Equal("old-a", File.ReadAllText(Path.Combine(root, "app", "a.dll")));
            Assert.Equal("old-b", File.ReadAllText(Path.Combine(root, "app", "b.dll")));
            Assert.False(File.Exists(Path.Combine(root, "app", "a.dll.new")));
            Assert.False(File.Exists(Path.Combine(root, "updates", UpdateApplier.JournalName)));
        }
        finally { UpdateApplier.AfterReplaceForTesting = null; }
    }

    [Fact]
    public void RecoverKeepsTheJournalWhenARestoreCannotBeDone()
    {
        Write("app/keep.dll", "new");
        var journal = new JsonObject
        {
            ["status"] = "applying", ["version"] = "0.6.0", ["install_root"] = root,
            ["entries"] = new JsonArray(new JsonObject
            {
                ["path"] = "app/keep.dll", ["target"] = Path.Combine(root, "app", "keep.dll"),
                ["backup"] = Path.Combine(root, "updates", "backup", "0.6.0", "app", "keep.dll"),
                ["created"] = false, ["state"] = "applied", ["sha256"] = Sha(Encoding.UTF8.GetBytes("new"))
            })
        };
        Directory.CreateDirectory(Path.Combine(root, "updates"));
        var journalPath = Path.Combine(root, "updates", UpdateApplier.JournalName);
        File.WriteAllText(journalPath, journal.ToJsonString());
        var note = UpdateApplier.Recover(root);
        Assert.False(note.Ok);
        Assert.Contains("崩溃恢复未完成", note.Note);
        Assert.True(File.Exists(journalPath));
    }

    [Theory]
    [InlineData("applying")]
    [InlineData("rollback_failed")]
    public void RecoverKeepsAnUnfinishedJournalWithoutEntries(string status)
    {
        Write("app/keep.dll", "untouched");
        var journal = new JsonObject
        {
            ["status"] = status, ["version"] = "0.6.0", ["install_root"] = root,
            ["backup_root"] = Path.Combine(root, "updates", "backup", "0.6.0")
        };
        Directory.CreateDirectory(Path.Combine(root, "updates"));
        var journalPath = Path.Combine(root, "updates", UpdateApplier.JournalName);
        File.WriteAllText(journalPath, journal.ToJsonString());

        var note = UpdateApplier.Recover(root);

        Assert.False(note.Ok);
        Assert.Contains("缺少条目", note.Note);
        Assert.True(File.Exists(journalPath));
        Assert.Equal(status, JsonNode.Parse(File.ReadAllText(journalPath))!["status"]!.GetValue<string>());
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(root, "app", "keep.dll")));
    }

    [Fact]
    public void RecoverRefusesPathsOutsideTheInstallTree()
    {
        var outside = Path.Combine(Path.GetTempPath(), "mabi-outside-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllText(outside, "new");
        var journal = new JsonObject
        {
            ["status"] = "applying", ["version"] = "0.6.0", ["install_root"] = root,
            ["entries"] = new JsonArray(new JsonObject
            {
                ["path"] = "../" + Path.GetFileName(outside), ["target"] = outside,
                ["backup"] = outside, ["created"] = true, ["state"] = "applied", ["sha256"] = new string('a', 64)
            })
        };
        Directory.CreateDirectory(Path.Combine(root, "updates"));
        var journalPath = Path.Combine(root, "updates", UpdateApplier.JournalName);
        File.WriteAllText(journalPath, journal.ToJsonString());
        var note = UpdateApplier.Recover(root);
        Assert.False(note.Ok);
        Assert.Contains("崩溃恢复未完成", note.Note);
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(journalPath));
        File.Delete(outside);
    }

    [Fact]
    public void ApplyStopsWhenTheJournalCannotBeWritten()
    {
        Write("app/keep.dll", "old");
        var staged = Path.Combine(root, "updates", "staging", "0.6.0", "app", "keep.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, "new");
        Directory.CreateDirectory(Path.Combine(root, "updates", UpdateApplier.JournalName));
        var plan = new UpdatePlan("0.6.0", root, [new PlannedFile("app/keep.dll", staged, Sha(Encoding.UTF8.GetBytes("new")), 3)], false, null);
        var result = UpdateApplier.Apply(plan, Path.Combine(root, "updates", "backup", "0.6.0"));
        Assert.False(result.Success);
        Assert.Contains("更新日志", result.Error);
        Assert.Equal("old", File.ReadAllText(Path.Combine(root, "app", "keep.dll")));
    }

    [Fact]
    public async Task NonSuccessResponseIsNotTreatedAsAssetContent()
    {
        var handler = new FakeHandler();
        handler.Release(ReleaseJson("v0.6.0", (UpdateManifest.FileName, 10, new string('a', 64))));
        // No body registered for the manifest: the fake answers 404 with an error page.
        using var service = new UpdateService(root, handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync("0.5.8"));
        Assert.Contains("404", error.Message);
    }

    [Fact]
    public async Task MirrorRedirectToAForeignHostFallsBackToTheOfficialAddress()
    {
        var payload = Encoding.UTF8.GetBytes("payload");
        var manifest = ManifestJson("0.6.0", ("app/changed.dll", payload));
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        const string tag = "v0.6.0";
        var handler = new FakeHandler();
        handler.Release(ReleaseJson(tag,
            (UpdateManifest.FileName, manifestBytes.LongLength, Sha(manifestBytes)),
            ("blob-" + Sha(payload) + ".bin", payload.LongLength, Sha(payload))));
        handler.Bytes(Asset(tag, UpdateManifest.FileName), manifestBytes);
        handler.Bytes(Asset(tag, "blob-" + Sha(payload) + ".bin"), payload);
        handler.Redirect("https://v6.gh-proxy.org/" + Asset(tag, "blob-" + Sha(payload) + ".bin"), "https://evil.example.com/blob.bin");
        using var service = new UpdateService(root, handler, new TransportOptions("https://v6.gh-proxy.org/"));
        var plan = await service.PrepareAsync(await service.CheckAsync("0.5.8"));
        Assert.Contains(service.Notes, note => note.Contains("镜像跳转到不允许的地址", StringComparison.Ordinal));
        Assert.Equal("payload", File.ReadAllText(Assert.Single(plan!.Files, f => !UpdateApplier.IsBaseline(f.RelativePath)).StagedPath));
    }

    [Fact]
    public void VersionComparisonIsNumericNotTextual()
    {
        Assert.True(UpdateVersion.Compare("0.5.8", "0.5.10") < 0);
        Assert.Equal(0, UpdateVersion.Compare("v0.6.0", "0.6.0"));
        Assert.True(UpdateVersion.Compare("0.6.1", "0.6.0") > 0);
        Assert.Equal("0.6.0", UpdateVersion.Normalize("v0.6.0-beta1"));
    }

    [Fact]
    public void RunnerStageCopiesEveryDependency()
    {
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(Path.Combine(install, "app", "sub"));
        File.WriteAllText(Path.Combine(install, "MabiAgent.exe"), "exe");
        File.WriteAllText(Path.Combine(install, "app", "MabiAgent.Desktop.dll"), "ui");
        File.WriteAllText(Path.Combine(install, "app", "sub", "native.dll"), "native");
        var runner = UpdateRunner.Stage(install);
        try
        {
            var staged = Path.GetDirectoryName(runner)!;
            Assert.Equal("exe", File.ReadAllText(runner));
            Assert.Equal("ui", File.ReadAllText(Path.Combine(staged, "app", "MabiAgent.Desktop.dll")));
            Assert.Equal("native", File.ReadAllText(Path.Combine(staged, "app", "sub", "native.dll")));
            Assert.NotEqual(Path.GetFullPath(install), staged);
        }
        finally { Directory.Delete(Path.GetDirectoryName(runner)!, true); }
    }

    [Fact]
    public void InstallRootThatIsAJunctionIsRejected()
    {
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(Path.Combine(real, "app"));
        var link = Path.Combine(root, "link");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link); start.ArgumentList.Add(real);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
        process.WaitForExit(20000);
        try
        {
            Assert.True(process.ExitCode == 0, "无法创建目录联接，本项检查不能跳过：" + output);
            Assert.False(InstallPaths.TryResolve(link, "app/a.dll", out _, out var reason));
            Assert.Contains("重解析", reason);
        }
        finally
        {
            // A junction must be removed without recursing into its target.
            try { if (Directory.Exists(link)) Directory.Delete(link); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void InstallRootResolvesAboveTheAppFolder()
    {
        Directory.CreateDirectory(Path.Combine(root, "app"));
        File.WriteAllText(Path.Combine(root, InstallLayout.Entry), "");
        Assert.Equal(Path.GetFullPath(root), InstallLayout.ResolveRoot(Path.Combine(root, "app")));
        Assert.Equal(Path.GetFullPath(root), InstallLayout.ResolveRoot(root));
    }

    private const string Latest = "https://github.com/MinamiChiwa/MabiAgent/releases/latest";
    private static string Asset(string tag, string name) => $"https://github.com/MinamiChiwa/MabiAgent/releases/download/{tag}/{name}";

    private static string ManifestJson(string version, params (string Path, byte[] Body)[] files)
    {
        var array = new JsonArray();
        foreach (var (path, body) in files)
            array.Add(new JsonObject
            {
                ["path"] = path, ["size"] = body.Length, ["sha256"] = Sha(body), ["blob"] = "blob-" + Sha(body) + ".bin"
            });
        return new JsonObject
        {
            ["app_id"] = "MabiAgent", ["schema"] = 1, ["version"] = version, ["channel"] = "stable", ["files"] = array
        }.ToJsonString();
    }

    /// <summary>Sizes are the real byte lengths: a manifest asset with a fake size would fail verification.</summary>
    private static string ReleaseJson(string tag, params (string Name, long Size, string? Digest)[] assets)
    {
        var array = new JsonArray();
        foreach (var (name, size, digest) in assets)
            array.Add(new JsonObject
            {
                ["name"] = name,
                ["size"] = size,
                ["digest"] = digest is null ? null : "sha256:" + digest,
                ["browser_download_url"] = Asset(tag, name)
            });
        return new JsonObject { ["tag_name"] = tag, ["assets"] = array }.ToJsonString();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> content = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> redirects = new(StringComparer.Ordinal);
        public List<string> Requests { get; } = [];
        public Dictionary<string, byte[]> MirrorBytes { get; } = new(StringComparer.Ordinal);
        public bool BrokenMirror { get; set; }

        public void Release(string json)
        {
            var release = JsonNode.Parse(json)!;
            var tag = release["tag_name"]!.GetValue<string>();
            Redirect(Latest, "https://github.com/MinamiChiwa/MabiAgent/releases/tag/" + Uri.EscapeDataString(tag));
            Bytes("https://github.com/MinamiChiwa/MabiAgent/releases/tag/" + Uri.EscapeDataString(tag), []);
            var html = new StringBuilder("<ul>");
            foreach (var asset in release["assets"]!.AsArray())
                html.Append("<li><a href=\"").Append(WebUtility.HtmlEncode(asset!["browser_download_url"]!.GetValue<string>()))
                    .Append("\">asset</a><span>").Append(asset["digest"]?.GetValue<string>()).Append("</span><span>")
                    .Append(asset["size"]!.GetValue<long>()).Append(" Bytes</span></li>");
            html.Append("</ul>");
            Bytes("https://github.com/MinamiChiwa/MabiAgent/releases/expanded_assets/" + Uri.EscapeDataString(tag), Encoding.UTF8.GetBytes(html.ToString()));
        }
        public void Bytes(string url, byte[] bytes) => content[url] = bytes;
        public void Redirect(string url, string location) => redirects[url] = location;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            if (redirects.TryGetValue(url, out var location))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri(location) } });
            if (url.Contains("gh-proxy.org", StringComparison.Ordinal))
            {
                if (BrokenMirror) throw new HttpRequestException("connection reset mid-stream");
                if (MirrorBytes.TryGetValue(url, out var mirror)) return Task.FromResult(Ok(mirror));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            if (content.TryGetValue(url, out var bytes)) return Task.FromResult(Ok(bytes));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }
}
