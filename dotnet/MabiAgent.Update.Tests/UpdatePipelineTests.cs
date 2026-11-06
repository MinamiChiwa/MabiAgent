using MabiAgent.Update;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace MabiAgent.Update.Tests;

/// <summary>
/// End-to-end: the real asset generator produces a release, a loopback HTTP server serves it,
/// and the client checks, stages and applies it. Nothing here talks to the internet.
/// </summary>
public sealed class UpdatePipelineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mabi-pipeline-" + Guid.NewGuid().ToString("N"));

    public UpdatePipelineTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { } }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void WriteFile(string directory, string relative, string content)
    {
        var path = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task GeneratedReleaseIsSelfContainedAndUpdatesOnlyTheChangedFile()
    {
        var package = Path.Combine(root, "package");
        var install = Path.Combine(root, "install");
        var assets = Path.Combine(root, "assets");

        WriteFile(package, "MabiAgent.exe", "exe-v2");
        WriteFile(package, "Start-MabiAgent.cmd", "start");
        WriteFile(package, "app/MabiAgent.Desktop.dll", "dll-v2");
        WriteFile(package, "app/unchanged.dll", "same");
        WriteFile(package, "data/work-queue.json", "private queue");
        WriteFile(package, "notes-private.txt", "not part of the release");
        WritePackageBaseline(package);

        WriteFile(install, "MabiAgent.exe", "exe-v2");
        WriteFile(install, "Start-MabiAgent.cmd", "start");
        WriteFile(install, "app/MabiAgent.Desktop.dll", "dll-v1");
        WriteFile(install, "app/unchanged.dll", "same");
        WriteFile(install, "data/work-queue.json", "my own queue");
        WriteInstallBaseline(install);

        RunGenerator(package, assets, "0.6.0");
        var manifestBytes = File.ReadAllBytes(Path.Combine(assets, "update-manifest.json"));
        var generated = JsonNode.Parse(File.ReadAllText(Path.Combine(assets, "update-manifest.json")))!;
        var paths = generated["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()).ToArray();
        Assert.DoesNotContain("data/work-queue.json", paths);
        // Only files the packager listed are released; a stray private file stays private.
        Assert.DoesNotContain("notes-private.txt", paths);
        Assert.Contains("release-manifest.json", paths);
        // Self-contained: every referenced blob was written.
        Assert.All(generated["files"]!.AsArray(), f => Assert.True(File.Exists(Path.Combine(assets, f!["blob"]!.GetValue<string>()))));

        LoopbackServer server = null!;
        server = new LoopbackServer(path => Route(path, server, assets, manifestBytes));
        using var handle = server;

        var options = new TransportOptions(null, ["127.0.0.1"], null, server.BaseUrl + "/api", true);
        using var service = new UpdateService(install, new HttpClientHandler(), options);
        var check = await service.CheckAsync("0.5.9");
        Assert.True(check.Available);
        Assert.True(check.HasManifest);

        var plan = await service.PrepareAsync(check);
        Assert.NotNull(plan);
        Assert.Equal(["app/MabiAgent.Desktop.dll"], plan!.Files.Where(f => !UpdateApplier.IsBaseline(f.RelativePath)).Select(f => f.RelativePath).ToArray());
        var unchangedBlob = "/dl/blob-" + Sha(Encoding.UTF8.GetBytes("same")) + ".bin";
        Assert.DoesNotContain(server.Requests, url => url.EndsWith(unchangedBlob, StringComparison.Ordinal));

        var result = UpdateApplier.Apply(plan, Path.Combine(install, "updates", "backup", "0.6.0"));
        Assert.True(result.Success, result.Error);
        Assert.Equal("dll-v2", File.ReadAllText(Path.Combine(install, "app", "MabiAgent.Desktop.dll")));
        Assert.Equal("my own queue", File.ReadAllText(Path.Combine(install, "data", "work-queue.json")));
        Assert.Equal("same", File.ReadAllText(Path.Combine(install, "app", "unchanged.dll")));
        Assert.Equal("dll-v1", File.ReadAllText(Path.Combine(install, "updates", "backup", "0.6.0", "app", "MabiAgent.Desktop.dll")));
        Assert.False(File.Exists(Path.Combine(install, "updates", UpdateApplier.JournalName)));

        var baseline = InstallManifest.Load(install);
        Assert.NotNull(baseline);
        Assert.Equal(Sha(Encoding.UTF8.GetBytes("dll-v2")), baseline!["app/MabiAgent.Desktop.dll"]);

        // The same release is not offered again once the install matches it.
        var again = await service.CheckAsync("0.6.0");
        Assert.False(again.Available);
    }

    private static (int Status, byte[] Body) Route(string path, LoopbackServer server, string assets, byte[] manifestBytes)
    {
        if (path == "/api/repos/MinamiChiwa/MabiAgent/releases/latest")
            return (200, Encoding.UTF8.GetBytes(ReleaseJson(assets, server.BaseUrl, manifestBytes)));
        const string prefix = "/dl/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return (404, []);
        var file = Path.Combine(assets, path[prefix.Length..]);
        return File.Exists(file) ? (200, File.ReadAllBytes(file)) : (404, []);
    }

    private static string ReleaseJson(string assets, string baseUrl, byte[] manifestBytes)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(assets, "update-manifest.json")))!;
        var list = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "update-manifest.json", ["size"] = manifestBytes.LongLength,
                ["digest"] = "sha256:" + Sha(manifestBytes),
                ["browser_download_url"] = baseUrl + "/dl/update-manifest.json"
            }
        };
        foreach (var file in manifest["files"]!.AsArray())
        {
            var blob = file!["blob"]!.GetValue<string>();
            var bytes = File.ReadAllBytes(Path.Combine(assets, blob));
            list.Add(new JsonObject
            {
                ["name"] = blob, ["size"] = bytes.LongLength, ["digest"] = "sha256:" + Sha(bytes),
                ["browser_download_url"] = baseUrl + "/dl/" + blob
            });
        }
        return new JsonObject { ["tag_name"] = "v0.6.0", ["assets"] = list }.ToJsonString();
    }

    private static void WritePackageBaseline(string package)
    {
        var rows = new JsonArray();
        foreach (var relative in new[] { "MabiAgent.exe", "Start-MabiAgent.cmd", "app/MabiAgent.Desktop.dll", "app/unchanged.dll" })
        {
            var path = Path.Combine(package, relative);
            rows.Add(new JsonObject { ["path"] = relative, ["bytes"] = new FileInfo(path).Length, ["sha256"] = Sha(File.ReadAllBytes(path)) });
        }
        File.WriteAllText(Path.Combine(package, InstallManifest.FileName), new JsonObject
        {
            ["schema_version"] = 1, ["built_at"] = 1, ["entry"] = "Start-MabiAgent.cmd", ["runtime"] = "win-x64",
            ["self_contained"] = true, ["game_input"] = false, ["pointer_changed"] = false, ["files"] = rows
        }.ToJsonString());
    }

    private static void WriteInstallBaseline(string install)
    {
        var rows = new JsonArray();
        foreach (var relative in new[] { "MabiAgent.exe", "Start-MabiAgent.cmd", "app/MabiAgent.Desktop.dll", "app/unchanged.dll" })
        {
            var path = Path.Combine(install, relative);
            rows.Add(new JsonObject { ["path"] = relative, ["bytes"] = new FileInfo(path).Length, ["sha256"] = Sha(File.ReadAllBytes(path)) });
        }
        File.WriteAllText(Path.Combine(install, InstallManifest.FileName), new JsonObject
        {
            ["schema_version"] = 1, ["built_at"] = 0, ["entry"] = "Start-MabiAgent.cmd", ["runtime"] = "win-x64",
            ["self_contained"] = true, ["game_input"] = false, ["pointer_changed"] = false, ["files"] = rows
        }.ToJsonString());
    }

    /// <summary>Runs the shipped generator, exactly as a release would.</summary>
    private static void RunGenerator(string package, string assets, string version)
    {
        var script = FindGenerator();
        var start = new ProcessStartInfo(PythonExecutable())
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(script)!,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--package"); start.ArgumentList.Add(package);
        start.ArgumentList.Add("--version"); start.ArgumentList.Add(version);
        start.ArgumentList.Add("--output"); start.ArgumentList.Add(assets);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 python");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120000), "生成器超时");
        Assert.True(process.ExitCode == 0, "生成器失败：" + error + output);
    }

    private static string FindGenerator()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "build_update_assets.py");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("找不到 tools/build_update_assets.py");
    }

    private static string PythonExecutable()
    {
        foreach (var name in new[] { "python", "python3", "py" })
        {
            try
            {
                var probe = Process.Start(new ProcessStartInfo(name) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--version" } });
                if (probe is null) continue;
                probe.WaitForExit(20000);
                if (probe.ExitCode == 0) return name;
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        throw new InvalidOperationException("本机没有可用的 python，无法运行资产生成器");
    }

    /// <summary>Minimal HTTP/1.1 server on 127.0.0.1 so no URL reservation is needed.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly Func<string, (int Status, byte[] Body)> handler;
        public List<string> Requests { get; } = [];
        public string BaseUrl { get; }

        public LoopbackServer(Func<string, (int Status, byte[] Body)> handler)
        {
            this.handler = handler;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { return; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using var _ = client;
            try
            {
                using var stream = client.GetStream();
                var text = new StringBuilder();
                var buffer = new byte[4096];
                while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
                    if (read == 0) return;
                    text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                var line = text.ToString().Split("\r\n")[0].Split(' ');
                var path = line.Length > 1 ? line[1] : "/";
                lock (Requests) Requests.Add(path);
                var (status, body) = handler(path);
                var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), stop.Token).ConfigureAwait(false);
                await stream.WriteAsync(body, stop.Token).ConfigureAwait(false);
                await stream.FlushAsync(stop.Token).ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }
    }
}
