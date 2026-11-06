using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace MabiAgent.Update;

public sealed record ReleaseAsset(string Name, long Size, string? Digest, Uri Url);
public sealed record ReleaseInfo(string Tag, string Version, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Asset(string name) => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// <paramref name="ApiBase"/> and <paramref name="AllowPlaintextLoopback"/> exist only so an
/// integration test can point the client at a loopback server; the app never sets them, and
/// plaintext is accepted for loopback hosts that were explicitly added to the allowlist.
/// </summary>
public sealed record TransportOptions(string? MirrorPrefix = null, string[]? ExtraHosts = null, TimeProvider? Time = null,
    string? ApiBase = null, bool AllowPlaintextLoopback = false);

/// <summary>
/// Talks to the official release channel. The manifest channel and the blob channel
/// have separate host allowlists: <c>allowMirror=false</c> can never reach the mirror,
/// not even through a redirect. Every hop is validated by hand because automatic
/// redirects are disabled on the handlers this library creates.
/// </summary>
public sealed class UpdateTransport : IDisposable
{
    public static readonly string[] OfficialHosts =
    [
        "api.github.com", "github.com", "objects.githubusercontent.com",
        "release-assets.githubusercontent.com", "github-releases.githubusercontent.com"
    ];
    public const double MirrorRetrySeconds = 600;
    private readonly HttpClient http;
    private readonly string mirror;
    private readonly string apiBase;
    private readonly bool allowPlaintext;
    private readonly HashSet<string> officialHosts;
    private readonly HashSet<string> mirrorHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider time;
    private double mirrorDownUntil;
    public List<string> Notes { get; } = [];

    public UpdateTransport(HttpMessageHandler handler, TransportOptions? options = null)
    {
        options ??= new TransportOptions();
        DisableRedirects(handler);
        http = new HttpClient(handler, true) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MabiAgent-Updater/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        mirror = Normalize(options.MirrorPrefix, options.AllowPlaintextLoopback);
        apiBase = string.IsNullOrWhiteSpace(options.ApiBase) ? "https://api.github.com" : options.ApiBase.TrimEnd('/');
        allowPlaintext = options.AllowPlaintextLoopback;
        officialHosts = new HashSet<string>(OfficialHosts, StringComparer.OrdinalIgnoreCase);
        // Test seam for a local HTTP server; production callers pass nothing.
        foreach (var host in options.ExtraHosts ?? []) officialHosts.Add(host);
        if (mirror.Length > 0) mirrorHosts.Add(new Uri(mirror).Host);
        time = options.Time ?? TimeProvider.System;
    }
    public bool MirrorConfigured => mirror.Length > 0;
    public bool MirrorAvailable => MirrorConfigured && Epoch() >= mirrorDownUntil;

    private static void DisableRedirects(HttpMessageHandler handler)
    {
        // Manual hops only: a mirror must never become the manifest channel by redirect.
        if (handler is HttpClientHandler client) client.AllowAutoRedirect = false;
        else if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
    }

    /// <summary>Only an explicit prefix counts; nothing is guessed from a project name.</summary>
    public static string NormalizeMirror(string? prefix) => Normalize(prefix, false);

    private static string Normalize(string? prefix, bool allowPlaintext)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return "";
        var value = prefix.Trim();
        if (!value.EndsWith('/')) value += "/";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "";
        var scheme = uri.Scheme == Uri.UriSchemeHttps || (allowPlaintext && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        return scheme ? uri.AbsoluteUri : "";
    }

    public async Task<ReleaseInfo> LatestReleaseAsync(string repository, CancellationToken token)
    {
        var uri = new Uri(apiBase + "/repos/" + repository + "/releases/latest");
        var text = await GetStringAsync(uri, false, token).ConfigureAwait(false);
        var node = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("发布接口返回的不是 JSON 对象");
        var tag = node["tag_name"]?.GetValue<string>() ?? throw new InvalidDataException("发布接口缺少 tag");
        var assets = new List<ReleaseAsset>();
        if (node["assets"] is JsonArray array)
            foreach (var item in array)
            {
                if (item is not JsonObject asset) continue;
                var name = asset["name"]?.GetValue<string>() ?? "";
                var size = asset["size"]?.GetValue<long>() ?? 0;
                var digest = asset["digest"]?.GetValue<string>();
                var url = asset["browser_download_url"]?.GetValue<string>() ?? "";
                if (name.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)) continue;
                assets.Add(new ReleaseAsset(name, size, digest, parsed));
            }
        return new ReleaseInfo(tag, UpdateVersion.Normalize(tag), assets);
    }

    public async Task<string> GetStringAsync(Uri uri, bool allowMirror, CancellationToken token)
    {
        using var response = await FollowAsync(uri, HttpCompletionOption.ResponseContentRead, token, allowMirror).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads and verifies one asset. A mirror that returns a well-formed but wrong
    /// body, or drops mid-stream, counts as a failed mirror: the same asset is retried
    /// against the official host instead of failing the whole update.
    /// </summary>
    public async Task<string> DownloadVerifiedAsync(ReleaseAsset asset, string? expectedSha256, long? expectedSize, string path, bool allowMirror, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (allowMirror && MirrorAvailable && asset.Url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var mirrored = new Uri(mirror + asset.Url.AbsoluteUri);
            try
            {
                var hash = await DownloadOnceAsync(mirrored, path, token, true).ConfigureAwait(false);
                if (Matches(hash, expectedSha256, path, expectedSize)) { Notes.Add("经镜像传输：" + mirrored.Host); return hash; }
                Notes.Add("镜像内容与清单不符，改用官方地址");
            }
            catch (HttpRequestException error) { Notes.Add("镜像下载中断，改用官方地址：" + error.Message); }
            catch (IOException error) { Notes.Add("镜像下载中断，改用官方地址：" + error.Message); }
            catch (InvalidDataException error) { Notes.Add("镜像响应异常，改用官方地址：" + error.Message); }
            // A mirror that redirects to a host outside its own allowlist is a mirror failure,
            // not a reason to abort the update: fall back to the official address as well.
            catch (InvalidOperationException error) { Notes.Add("镜像跳转到不允许的地址，改用官方地址：" + error.Message); }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { Notes.Add("镜像下载超时，改用官方地址"); }
            mirrorDownUntil = Epoch() + MirrorRetrySeconds;
            TryDelete(path);
        }
        var direct = await DownloadOnceAsync(asset.Url, path, token, false).ConfigureAwait(false);
        if (!Matches(direct, expectedSha256, path, expectedSize))
        {
            TryDelete(path);
            throw new InvalidDataException("文件校验失败：" + asset.Name);
        }
        return direct;
    }

    private static bool Matches(string hash, string? expectedSha256, string path, long? expectedSize)
    {
        if (expectedSize is { } size && new FileInfo(path).Length != size) return false;
        return expectedSha256 is null || string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> DownloadOnceAsync(Uri uri, string path, CancellationToken token, bool allowMirror)
    {
        using var response = await FollowAsync(uri, HttpCompletionOption.ResponseHeadersRead, token, allowMirror).ConfigureAwait(false);
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        await target.FlushAsync(token).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal void MarkMirrorDown() => mirrorDownUntil = Epoch() + MirrorRetrySeconds;

    private async Task<HttpResponseMessage> FollowAsync(Uri uri, HttpCompletionOption option, CancellationToken token, bool allowMirror)
    {
        var current = uri;
        for (var hop = 0; hop < 5; hop++)
        {
            EnsureHost(current, allowMirror);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await http.SendAsync(request, option, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                // A 404/403 body is not an asset: only a successful response may reach a caller.
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException("更新地址返回 HTTP " + status + "：" + current.AbsoluteUri);
                }
                return response;
            }
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("更新重定向缺少目标地址");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            EnsureHost(current, allowMirror);
        }
        throw new InvalidDataException("更新重定向层数过多");
    }

    private void EnsureHost(Uri uri, bool allowMirror)
    {
        var plaintextAllowed = allowPlaintext && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && officialHosts.Contains(uri.Host);
        if (uri.Scheme != Uri.UriSchemeHttps && !plaintextAllowed) throw new InvalidOperationException("更新只允许 HTTPS 地址：" + uri);
        if (officialHosts.Contains(uri.Host)) return;
        if (allowMirror && mirrorHosts.Contains(uri.Host)) return;
        throw new InvalidOperationException("不允许的更新主机：" + uri.Host);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private double Epoch() => (time.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds;
    public void Dispose() => http.Dispose();
}
