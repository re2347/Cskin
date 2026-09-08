using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CskinNative.Services;

public sealed record SkinResourceProbeResult(bool Available, string Reason, string? Endpoint = null, int? StatusCode = null);

public sealed class SkinRepository
{
    public const string PrivateRepositoryUrl = "https://gitcode.com/Re2347/skin";
    public const string RepositoryLabel = "privateskin";
    private const string CacheMarkerSchema = "private-gateway-v2";
    private const string CachedIndexFileName = "skin_worker_index.json";
    private static readonly ResourceEndpoint[] ResourceEndpoints =
    [
        new("supabase", new Uri("https://kzqgphxrghdclkwneqyn.supabase.co/functions/v1/auth/")),
        new("cloudflare", new Uri("https://license.re2347.ccwu.cc/")),
        new("cloudflare", new Uri("https://cskin-license-staging.2469416170.workers.dev/")),
    ];
    private static readonly HttpClient RepositoryHttp = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _downloadGate = new(4, 4);
    private readonly SemaphoreSlim _indexPrimeGate = new(1, 1);
    private ConcurrentDictionary<int, string> _paths = [];
    private ConcurrentDictionary<int, string> _names = [];
    private ConcurrentDictionary<int, string> _englishNames = [];
    private ConcurrentDictionary<int, string> _blobShas = [];
    private ConcurrentDictionary<int, string> _previewPaths = [];
    private string? _portableRoot;
    private string _revision = "";
    private bool _stopping;
    private int _syncInProgress;

    public SkinRepository(string? portableRoot) => _portableRoot = portableRoot;

    public string RepositoryPath => $"{RepositoryLabel} via Supabase/Cloudflare";
    public string RepositoryUrl => PrivateRepositoryUrl;
    public string Revision => _revision;
    public int RemoteSkinCount => _paths.Count;
    public bool IsReady => _paths.Count > 0;
    public bool IsSyncing => Volatile.Read(ref _syncInProgress) != 0;
    public bool LastSyncChanged { get; private set; }
    public IReadOnlyDictionary<int, string> SkinPaths => _paths;
    public IReadOnlyDictionary<int, string> SkinNames => _names;
    public IReadOnlyDictionary<int, string> SkinEnglishNames => _englishNames;
    public IReadOnlyDictionary<int, string> CachedPreviewPaths => _previewPaths;

    public static string AvailabilityPath(int skinId) => $"v1/skins/availability?skinId={skinId}";

    public void SetPortableRoot(string? portableRoot) => _portableRoot = portableRoot;

    public Task<bool> EnsureIndexReadyAsync(CancellationToken cancellationToken = default) =>
        PrimeLocalIndexAsync(null, cancellationToken);

    public void Stop() => _stopping = true;

    public async Task<bool> UpdateEngineIndexAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_portableRoot) || _paths.Count == 0) return false;
        try
        {
            AppLog.Info($"更新引擎皮肤索引：engineRoot={_portableRoot} source={RepositoryLabel} skins={_paths.Count}");
            var directory = Path.Combine(_portableRoot, "selector_assets");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "skin_index.json");
            var root = new JsonObject();
            if (File.Exists(target))
            {
                try
                {
                    root = JsonNode.Parse(await File.ReadAllTextAsync(target, cancellationToken)) as JsonObject ?? new JsonObject();
                }
                catch (JsonException) { }
            }

            var bySkin = _paths.ToDictionary(pair => pair.Key.ToString(), pair => BuildEngineSkinRecord(pair.Key, pair.Value));
            root["bySkin"] = JsonSerializer.SerializeToNode(bySkin);
            root["schema"] ??= 1;
            var partial = target + ".partial";
            await File.WriteAllTextAsync(partial, root.ToJsonString(), new UTF8Encoding(false), cancellationToken);
            File.Move(partial, target, true);
            return true;
        }
        catch (IOException ex)
        {
            AppLog.Error("更新引擎皮肤索引失败", ex);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Error("更新引擎皮肤索引无权限", ex);
            return false;
        }
    }

    private object BuildEngineSkinRecord(int skinId, string path)
    {
        var relativePath = path.StartsWith("skins/", StringComparison.OrdinalIgnoreCase) ? path["skins/".Length..] : path;
        var championId = skinId / 1000;
        var baseSkinId = skinId;
        if (TryParseRepositoryPath(path, out var parsedChampionId, out var parsedBaseSkinId))
        {
            championId = parsedChampionId;
            baseSkinId = parsedBaseSkinId;
        }
        return new { id = skinId, name = _names.GetValueOrDefault(skinId, $"皮肤 {skinId}"), championId, baseSkinId, relativePath };
    }

    private static bool TryParseRepositoryPath(string path, out int championId, out int baseSkinId)
    {
        championId = 0;
        baseSkinId = 0;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4
            && parts[0].Equals("skins", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], out championId)
            && int.TryParse(parts[2], out baseSkinId);
    }

    public async Task<bool> SyncAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _syncInProgress, 1);
        try
        {
            LastSyncChanged = false;
            await PrimeLocalIndexAsync(progress, cancellationToken);
            progress?.Report("正在通过安全资源服务检查皮肤索引");
            AppLog.Info($"开始同步私有皮肤索引：source={RepositoryLabel} transport=Supabase/Cloudflare gitClient=False");
            var downloadedIndex = await DownloadIndexAsync(cancellationToken);
            var remote = downloadedIndex?.Index;
            if (remote is null || remote.Skins.Count == 0)
            {
                AppLog.Warn($"私有皮肤索引同步失败，继续使用本地索引：skins={_paths.Count}");
                return IsReady;
            }

            var paths = new Dictionary<int, string>();
            var names = new Dictionary<int, string>();
            var englishNames = new Dictionary<int, string>();
            var blobShas = new Dictionary<int, string>();
            foreach (var skin in remote.Skins)
            {
                if (skin.Id <= 0 || !TryNormalizeSkinPath(skin.Path, skin.Id, out var normalized)) continue;
                paths.TryAdd(skin.Id, normalized);
                if (!string.IsNullOrWhiteSpace(skin.Name)) names[skin.Id] = skin.Name.Trim();
                if (!string.IsNullOrWhiteSpace(skin.NameEn)) englishNames[skin.Id] = skin.NameEn.Trim();
                if (IsGitObjectId(skin.BlobSha)) blobShas[skin.Id] = skin.BlobSha.Trim();
            }
            if (paths.Count == 0)
            {
                AppLog.Warn("Worker 返回的私有皮肤索引不包含有效路径");
                return IsReady;
            }

            var previousRevision = _revision;
            _paths = new ConcurrentDictionary<int, string>(paths);
            _names = new ConcurrentDictionary<int, string>(MergeNames(names));
            _englishNames = new ConcurrentDictionary<int, string>(MergeNames(englishNames, _englishNames));
            _blobShas = new ConcurrentDictionary<int, string>(blobShas);
            _previewPaths = [];
            _revision = remote.Revision ?? "";
            LastSyncChanged = !string.Equals(previousRevision, _revision, StringComparison.OrdinalIgnoreCase);
            await SaveCachedIndexAsync(remote, cancellationToken);
            progress?.Report($"私有资源索引已就绪 · {_paths.Count:N0} 个皮肤");
            AppLog.Info($"私有皮肤索引同步完成：source={RepositoryLabel} gateway={downloadedIndex!.Gateway} upstream={downloadedIndex.Upstream} endpoint={downloadedIndex.Endpoint} skins={_paths.Count} revision={ShortRevision(_revision)}");
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _syncInProgress, 0);
        }
    }

    private async Task<bool> PrimeLocalIndexAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (IsReady) return true;
        await _indexPrimeGate.WaitAsync(cancellationToken);
        try
        {
            if (IsReady) return true;
            var cached = await ReadIndexFileAsync(Path.Combine(AppPaths.AssetsRoot, CachedIndexFileName), cancellationToken);
            if (cached is not null && cached.Skins.Count > 0)
            {
                LoadIndex(cached);
                progress?.Report($"上次私有资源索引已就绪 · {_paths.Count:N0} 个皮肤");
                AppLog.Info($"已加载上次私有资源索引：skins={_paths.Count} revision={ShortRevision(_revision)}");
                return true;
            }

            var paths = await LoadBundledPathIndexAsync(cancellationToken);
            if (paths.Count == 0)
            {
                AppLog.Warn("随包皮肤路径索引为空，等待安全资源服务索引");
                return false;
            }
            _paths = new ConcurrentDictionary<int, string>(paths);
            _names = new ConcurrentDictionary<int, string>(await LoadNamesFromFilesAsync(cancellationToken));
            _englishNames = new ConcurrentDictionary<int, string>(await LoadNamesFromFilesAsync(cancellationToken, "skin_names_en_US.json"));
            progress?.Report($"随包资源索引已就绪 · {_paths.Count:N0} 个皮肤");
            AppLog.Info($"随包皮肤路径索引已就绪：skins={_paths.Count}；远程更新通过 Supabase/Cloudflare 获取");
            return true;
        }
        finally
        {
            _indexPrimeGate.Release();
        }
    }

    public Task<bool> LoadPathIndexAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        PrimeLocalIndexAsync(progress, cancellationToken);

    public Task<int> EnsurePreviewImagesAsync(IEnumerable<int> skinIds, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public bool TryGetRemotePath(int skinId, out string relativePath) => _paths.TryGetValue(skinId, out relativePath!);

    public string? GetCachedSkinPath(int skinId)
    {
        if (_portableRoot is null || !TryGetRemotePath(skinId, out var relativePath)) return null;
        if (!TryResolveTarget(_portableRoot, relativePath, skinId, out var target)) return null;
        return HasTrustedCache(target, _revision, _blobShas.GetValueOrDefault(skinId)) ? target : null;
    }

    public async Task<SkinResourceProbeResult> ProbeSkinAvailabilityAsync(int skinId, CancellationToken cancellationToken = default)
    {
        if (_stopping) return new(false, "资源服务已停止");
        if (!TryGetRemotePath(skinId, out var relativePath))
            return new(false, $"私有资源索引中没有皮肤 {skinId}");

        var cachedPath = GetCachedSkinPath(skinId);
        if (SkinCachePolicy.CanSkipAvailabilityProbe(cachedPath is not null))
        {
            AppLog.Info($"资源可用性探测跳过：skinId={skinId} reason=trusted-local-cache path={cachedPath} revision={ShortRevision(_revision)}");
            return new(true, "trusted-local-cache", "local-cache", 200);
        }

        var expectedBlobSha = _blobShas.GetValueOrDefault(skinId);
        var endpoints = ResourceEndpoints.Select(endpoint => endpoint.BaseUrl.Host).ToArray();
        var batch = await ResourceProbePolicy.ProbeInParallelAsync(
            endpoints,
            (host, token) => ProbeGatewayAsync(
                ResourceEndpoints.First(endpoint => string.Equals(endpoint.BaseUrl.Host, host, StringComparison.OrdinalIgnoreCase)),
                skinId,
                relativePath,
                expectedBlobSha,
                token),
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var attempt in batch.Attempts)
        {
            var message = $"资源可用性探测通道结果：skinId={skinId} endpoint={attempt.Endpoint} available={attempt.Result.Available} reason={attempt.Result.Reason} status={attempt.Result.StatusCode?.ToString() ?? "<none>"} dnsMs={attempt.DnsMilliseconds} connectHttpMs={attempt.ConnectHttpMilliseconds} elapsedMs={attempt.ElapsedMilliseconds} detail={attempt.Detail}";
            if (attempt.Result.Available) AppLog.Info(message);
            else AppLog.Warn(message);
        }

        if (batch.Winner is not null) return batch.Winner.Result;
        var authorizationFailure = batch.Attempts.LastOrDefault(attempt => attempt.Result.StatusCode is 401 or 403);
        if (authorizationFailure is not null) return authorizationFailure.Result;
        var definitiveFailure = batch.Attempts.FirstOrDefault(attempt =>
            attempt.Result.StatusCode is int status && !IsTransientStatus((HttpStatusCode)status));
        return definitiveFailure?.Result ?? new(false, "所有安全资源通道均不可用");
    }

    private async Task<ResourceProbeAttempt> ProbeGatewayAsync(
        ResourceEndpoint endpoint,
        int skinId,
        string relativePath,
        string? expectedBlobSha,
        CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        var dnsMilliseconds = 0L;
        var connectHttpMilliseconds = 0L;
        int? statusCode = null;

        ResourceProbeAttempt Build(SkinResourceProbeResult result, string detail = "") => new(
            endpoint.BaseUrl.Host,
            result,
            dnsMilliseconds,
            connectHttpMilliseconds,
            Environment.TickCount64 - started,
            detail);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(endpoint.BaseUrl, AvailabilityPath(skinId)));
            if (!AddLeaseHeaders(request))
                return Build(new(false, "缺少有效授权租约", endpoint.BaseUrl.Host), "lease headers unavailable");

            var dnsStarted = Environment.TickCount64;
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(
                    endpoint.BaseUrl.Host,
                    System.Net.Sockets.AddressFamily.Unspecified,
                    cancellationToken).ConfigureAwait(false);
                dnsMilliseconds = Environment.TickCount64 - dnsStarted;
                AppLog.Info($"资源可用性探测 DNS：skinId={skinId} endpoint={endpoint.BaseUrl.Host} addresses={addresses.Length} dnsMs={dnsMilliseconds}");
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException or IOException)
            {
                dnsMilliseconds = Environment.TickCount64 - dnsStarted;
                return Build(new(false, "网关 DNS 解析失败", endpoint.BaseUrl.Host), ex.Message);
            }

            var requestStarted = Environment.TickCount64;
            using var response = await RepositoryHttp.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            connectHttpMilliseconds = Environment.TickCount64 - requestStarted;
            statusCode = (int)response.StatusCode;
            AppLog.Info($"资源可用性探测 HTTP 响应：skinId={skinId} endpoint={endpoint.BaseUrl.Host} status={statusCode} connectHttpMs={connectHttpMilliseconds} elapsedMs={Environment.TickCount64 - started}");

            var gateway = Header(response, "X-Cskin-Gateway");
            var upstream = Header(response, "X-Cskin-Upstream");
            var revision = Header(response, "X-Cskin-Revision");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var remoteError = await ReadRemoteErrorAsync(response, cancellationToken).ConfigureAwait(false);
                var reason = ResourceAuthorizationPolicy.Describe(response.StatusCode, remoteError.Code, remoteError.Message);
                return Build(new(false, reason, endpoint.BaseUrl.Host, statusCode), $"code={remoteError.Code} message={remoteError.Message}");
            }
            if (!response.IsSuccessStatusCode)
            {
                var transient = IsTransientStatus(response.StatusCode);
                return Build(new(false, $"资源不存在或不可下载（HTTP {statusCode}）", endpoint.BaseUrl.Host, statusCode), $"transient={transient} path={relativePath}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var availability = await JsonSerializer.DeserializeAsync<SkinAvailability>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (availability is null || !availability.Available || availability.SkinId != skinId
                || !TryNormalizeSkinPath(availability.Path, skinId, out var remotePath)
                || !string.Equals(remotePath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                return Build(new(false, "资源可用性探测响应无效", endpoint.BaseUrl.Host, statusCode), $"available={availability?.Available} path={availability?.Path}");
            }

            var responseBlobSha = Header(response, "X-Cskin-Blob-Sha");
            if (string.IsNullOrWhiteSpace(responseBlobSha)) responseBlobSha = availability.BlobSha ?? "";
            if (!string.Equals(gateway, endpoint.Gateway, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(upstream, "gitcode", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(revision)
                || (!string.IsNullOrWhiteSpace(_revision) && !string.Equals(revision, _revision, StringComparison.OrdinalIgnoreCase))
                || (IsGitObjectId(expectedBlobSha) && !string.Equals(responseBlobSha, expectedBlobSha, StringComparison.OrdinalIgnoreCase)))
            {
                return Build(new(false, "资源可用性探测响应校验失败", endpoint.BaseUrl.Host, statusCode),
                    $"gateway={gateway} upstream={upstream} revision={ShortRevision(revision)} expectedRevision={ShortRevision(_revision)} blob={ShortRevision(responseBlobSha)} expectedBlob={ShortRevision(expectedBlobSha)}");
            }

            return Build(new(true, "ok", endpoint.BaseUrl.Host, statusCode), $"path={relativePath}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Build(new(false, "资源探测超时", endpoint.BaseUrl.Host, statusCode), "request canceled by five-second probe budget");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return Build(new(false, "资源探测请求异常", endpoint.BaseUrl.Host, statusCode), ex.Message);
        }
    }

    public async Task<string?> EnsureSkinCachedAsync(int skinId, CancellationToken cancellationToken = default)
    {
        if (_portableRoot is null)
        {
            AppLog.Error($"无法缓存皮肤 {skinId}：引擎目录尚未准备好");
            return null;
        }
        if (!TryGetRemotePath(skinId, out var relativePath))
        {
            AppLog.Warn($"私有资源索引中没有皮肤 {skinId}");
            return null;
        }
        if (!TryResolveTarget(_portableRoot, relativePath, skinId, out var target))
        {
            AppLog.Error($"私有资源索引路径被拒绝：skinId={skinId} path={relativePath}");
            return null;
        }
        var expectedBlobSha = _blobShas.GetValueOrDefault(skinId);
        AppLog.Info($"准备下载皮肤：skinId={skinId} source={RepositoryLabel} transport=Supabase/Cloudflare revision={ShortRevision(_revision)} blob={ShortRevision(expectedBlobSha)} engineRoot={_portableRoot} target={target} relativePath={relativePath}");
        if (HasTrustedCache(target, _revision, expectedBlobSha)) return target;

        await _downloadGate.WaitAsync(cancellationToken);
        try
        {
            if (HasTrustedCache(target, _revision, expectedBlobSha)) return target;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".partial";
            try { File.Delete(temp); } catch { }
            var downloaded = await DownloadSkinAsync(skinId, relativePath, temp, cancellationToken);
            if (downloaded is null || !File.Exists(temp) || new FileInfo(temp).Length <= 0)
            {
                try { File.Delete(temp); } catch { }
                if (HasValidatedCache(target))
                {
                    AppLog.Warn($"远程资源通道均不可用，使用已通过哈希校验的本地缓存：skinId={skinId} cachedRevision={ShortRevision(ReadCacheRevision(target))} expectedRevision={ShortRevision(_revision)}");
                    return target;
                }
                AppLog.Error($"安全资源服务未能下载皮肤且没有有效本地缓存：skinId={skinId} path={relativePath}");
                return null;
            }
            if (!ValidateFantomeArchive(temp, out var validationDetail))
            {
                try { File.Delete(temp); } catch { }
                AppLog.Error($"皮肤包完整性校验失败：skinId={skinId} source={downloaded.Source} detail={validationDetail}");
                return null;
            }

            File.Move(temp, target, true);
            var targetBytes = new FileInfo(target).Length;
            string packageSha256;
            await using (var packageStream = File.OpenRead(target))
                packageSha256 = Convert.ToHexString(await SHA256.HashDataAsync(packageStream, cancellationToken));
            var marker = JsonSerializer.Serialize(new
            {
                schema = CacheMarkerSchema,
                source = downloaded.Source,
                repository = RepositoryLabel,
                revision = downloaded.Revision,
                blobSha = downloaded.BlobSha,
                etag = downloaded.ETag,
                gateway = downloaded.Gateway,
                upstream = downloaded.Upstream,
                bytes = targetBytes,
                sha256 = packageSha256,
            });
            await File.WriteAllTextAsync(CacheMarkerPath(target), marker, new UTF8Encoding(false), cancellationToken);
            AppLog.Info($"皮肤下载完成：skinId={skinId} target={target} bytes={targetBytes} source={downloaded.Source} gateway={downloaded.Gateway} upstream={downloaded.Upstream} revision={ShortRevision(downloaded.Revision)} blob={ShortRevision(downloaded.BlobSha)} elapsedMs={downloaded.ElapsedMilliseconds}");
            return target;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PortableCskin/0.3");
        return client;
    }

    private async Task<DownloadedIndex?> DownloadIndexAsync(CancellationToken cancellationToken)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(TimeSpan.FromSeconds(20));
        foreach (var endpoint in ResourceEndpoints)
        {
            if (_stopping || overall.IsCancellationRequested) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var started = Environment.TickCount64;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.BaseUrl, "v1/skins/index"));
                if (!AddLeaseHeaders(request))
                {
                    AppLog.Warn("私有资源请求缺少有效授权租约，请重新验证密钥");
                    return null;
                }
                using var response = await RepositoryHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    var remoteError = await ReadRemoteErrorAsync(response, timeout.Token);
                    var tryNext = ResourceAuthorizationPolicy.ShouldTryNext(response.StatusCode, remoteError.Code);
                    AppLog.Warn($"私有皮肤索引授权被拒绝：endpoint={endpoint.BaseUrl.Host} status={(int)response.StatusCode} code={remoteError.Code} message={remoteError.Message} failover={tryNext}");
                    if (tryNext) continue;
                    return null;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var transient = IsTransientStatus(response.StatusCode);
                    AppLog.Warn($"私有皮肤索引请求失败：endpoint={endpoint.BaseUrl.Host} status={(int)response.StatusCode} transient={transient}");
                    if (transient) continue;
                    return null;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var value = await JsonSerializer.DeserializeAsync<WorkerIndex>(stream, JsonOptions, timeout.Token);
                var gateway = Header(response, "X-Cskin-Gateway");
                var upstream = Header(response, "X-Cskin-Upstream");
                var revision = Header(response, "X-Cskin-Revision");
                if (!IsValidRemoteIndex(value, endpoint, gateway, upstream, revision, out var detail))
                {
                    AppLog.Warn($"私有皮肤索引副本校验失败，切换下一通道：endpoint={endpoint.BaseUrl.Host} detail={detail}");
                    continue;
                }
                var elapsed = Environment.TickCount64 - started;
                AppLog.Info($"私有皮肤索引响应有效：endpoint={endpoint.BaseUrl.Host} gateway={gateway} upstream={upstream} skins={value!.Skins.Count} revision={ShortRevision(value.Revision)} elapsedMs={elapsed}");
                return new DownloadedIndex(value, gateway, upstream, endpoint.BaseUrl.Host);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                AppLog.Warn($"私有皮肤索引通道超时，切换下一通道：endpoint={endpoint.BaseUrl.Host} elapsedMs={Environment.TickCount64 - started}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                AppLog.Warn($"私有皮肤索引临时异常，切换下一通道：endpoint={endpoint.BaseUrl.Host} elapsedMs={Environment.TickCount64 - started} error={ex.Message}");
            }
        }
        return null;
    }

    private async Task<DownloadedPackage?> DownloadSkinAsync(int skinId, string relativePath, string outputPath, CancellationToken cancellationToken)
    {
        foreach (var endpoint in ResourceEndpoints)
        {
            if (_stopping) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var started = Environment.TickCount64;
            try { File.Delete(outputPath); } catch { }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    new Uri(endpoint.BaseUrl, "v1/skins/file?skinId=" + skinId));
                if (!AddLeaseHeaders(request)) return null;
                using var response = await RepositoryHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    var remoteError = await ReadRemoteErrorAsync(response, timeout.Token);
                    var tryNext = ResourceAuthorizationPolicy.ShouldTryNext(response.StatusCode, remoteError.Code);
                    AppLog.Warn($"私有皮肤下载授权被拒绝：skinId={skinId} endpoint={endpoint.BaseUrl.Host} status={(int)response.StatusCode} code={remoteError.Code} message={remoteError.Message} failover={tryNext}");
                    if (tryNext) continue;
                    return null;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var transient = IsTransientStatus(response.StatusCode);
                    AppLog.Warn($"私有皮肤下载请求失败：skinId={skinId} endpoint={endpoint.BaseUrl.Host} status={(int)response.StatusCode} transient={transient} path={relativePath}");
                    if (transient) continue;
                    return null;
                }

                var gateway = Header(response, "X-Cskin-Gateway");
                var upstream = Header(response, "X-Cskin-Upstream");
                var revision = Header(response, "X-Cskin-Revision");
                var blobSha = Header(response, "X-Cskin-Blob-Sha");
                var expectedBlobSha = _blobShas.GetValueOrDefault(skinId);
                if (!string.Equals(gateway, endpoint.Gateway, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(upstream, "gitcode", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(revision)
                    || (!string.IsNullOrWhiteSpace(_revision) && !string.Equals(revision, _revision, StringComparison.OrdinalIgnoreCase))
                    || (IsGitObjectId(expectedBlobSha) && !string.Equals(blobSha, expectedBlobSha, StringComparison.OrdinalIgnoreCase)))
                {
                    AppLog.Warn($"私有皮肤资源副本校验失败，切换下一通道：skinId={skinId} endpoint={endpoint.BaseUrl.Host} gateway={gateway} upstream={upstream} revision={ShortRevision(revision)} expectedRevision={ShortRevision(_revision)} blob={ShortRevision(blobSha)} expectedBlob={ShortRevision(expectedBlobSha)}");
                    continue;
                }
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
                long bytes;
                await using (var destination = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                {
                    await source.CopyToAsync(destination, timeout.Token);
                    await destination.FlushAsync(timeout.Token);
                    bytes = destination.Length;
                }
                var validationDetail = bytes < 2 ? "empty response body" : "";
                if (bytes < 2 || !ValidateFantomeArchive(outputPath, out validationDetail))
                {
                    try { File.Delete(outputPath); } catch { }
                    AppLog.Warn($"私有皮肤资源内容无效，切换下一通道：skinId={skinId} endpoint={endpoint.BaseUrl.Host} bytes={bytes} detail={validationDetail}");
                    continue;
                }
                var etag = response.Headers.ETag?.Tag ?? Header(response, "ETag");
                var elapsed = Environment.TickCount64 - started;
                AppLog.Info($"私有皮肤流式下载完成：skinId={skinId} endpoint={endpoint.BaseUrl.Host} gateway={gateway} upstream={upstream} bytes={bytes} revision={ShortRevision(revision)} blob={ShortRevision(blobSha)} elapsedMs={elapsed} path={relativePath}");
                return new DownloadedPackage("private-gitcode", gateway, upstream, revision, blobSha, etag, bytes, elapsed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                try { File.Delete(outputPath); } catch { }
                AppLog.Warn($"私有皮肤下载通道超时，切换下一通道：skinId={skinId} endpoint={endpoint.BaseUrl.Host} elapsedMs={Environment.TickCount64 - started}");
            }
            catch (UnauthorizedAccessException ex)
            {
                try { File.Delete(outputPath); } catch { }
                AppLog.Error($"皮肤缓存目录无写入权限：skinId={skinId} path={outputPath}", ex);
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                try { File.Delete(outputPath); } catch { }
                AppLog.Warn($"私有皮肤下载临时异常，切换下一通道：skinId={skinId} endpoint={endpoint.BaseUrl.Host} elapsedMs={Environment.TickCount64 - started} path={relativePath} error={ex.Message}");
            }
        }
        return null;
    }

    private static bool AddLeaseHeaders(HttpRequestMessage request)
    {
        var lease = new LeaseStore().Load();
        if (lease is null
            || string.IsNullOrWhiteSpace(lease.LicenseId)
            || string.IsNullOrWhiteSpace(lease.LeaseId)
            || string.IsNullOrWhiteSpace(lease.LeaseToken)
            || string.IsNullOrWhiteSpace(lease.DeviceId)) return false;
        request.Headers.TryAddWithoutValidation("X-Cskin-License-Id", lease.LicenseId);
        request.Headers.TryAddWithoutValidation("X-Cskin-Lease-Id", lease.LeaseId);
        request.Headers.TryAddWithoutValidation("X-Cskin-Lease-Token", lease.LeaseToken);
        request.Headers.TryAddWithoutValidation("X-Cskin-Device-Id", lease.DeviceId);
        return true;
    }

    private void LoadIndex(WorkerIndex index)
    {
        var paths = new Dictionary<int, string>();
        var names = new Dictionary<int, string>();
        var englishNames = new Dictionary<int, string>();
        var blobShas = new Dictionary<int, string>();
        foreach (var skin in index.Skins)
        {
            if (skin.Id <= 0 || !TryNormalizeSkinPath(skin.Path, skin.Id, out var path)) continue;
            paths.TryAdd(skin.Id, path);
            if (!string.IsNullOrWhiteSpace(skin.Name)) names[skin.Id] = skin.Name.Trim();
            if (!string.IsNullOrWhiteSpace(skin.NameEn)) englishNames[skin.Id] = skin.NameEn.Trim();
            if (IsGitObjectId(skin.BlobSha)) blobShas[skin.Id] = skin.BlobSha.Trim();
        }
        _paths = new ConcurrentDictionary<int, string>(paths);
        _names = new ConcurrentDictionary<int, string>(MergeNames(names));
        _englishNames = new ConcurrentDictionary<int, string>(MergeNames(englishNames, _englishNames));
        _blobShas = new ConcurrentDictionary<int, string>(blobShas);
        _revision = index.Revision ?? "";
    }

    private Dictionary<int, string> MergeNames(Dictionary<int, string> remoteNames, IReadOnlyDictionary<int, string>? existing = null)
    {
        existing ??= _names;
        foreach (var pair in existing) remoteNames.TryAdd(pair.Key, pair.Value);
        return remoteNames;
    }

    private static async Task<WorkerIndex?> ReadIndexFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<WorkerIndex>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static async Task SaveCachedIndexAsync(WorkerIndex index, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AssetsRoot);
            var target = Path.Combine(AppPaths.AssetsRoot, CachedIndexFileName);
            var partial = target + ".partial";
            await File.WriteAllTextAsync(partial, JsonSerializer.Serialize(index, JsonOptions), new UTF8Encoding(false), cancellationToken);
            File.Move(partial, target, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"保存 Worker 皮肤索引失败：{ex.Message}");
        }
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? "" : "";

    private static async Task<RemoteError> ReadRemoteErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > 4096) body = body[..4096];
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var codeValue) ? codeValue.GetString() ?? "" : "";
                var message = error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() ?? "" : "";
                return new RemoteError(SanitizeDiagnostic(code), SanitizeDiagnostic(message));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or OperationCanceledException)
        {
            AppLog.Warn($"读取资源服务错误响应失败：status={(int)response.StatusCode} error={ex.Message}");
        }
        return new RemoteError("", "");
    }

    private static string SanitizeDiagnostic(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 180 ? compact : compact[..180];
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static bool IsGitObjectId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 40 && value.All(Uri.IsHexDigit);

    private static bool IsValidRemoteIndex(
        WorkerIndex? index,
        ResourceEndpoint endpoint,
        string gateway,
        string upstream,
        string headerRevision,
        out string detail)
    {
        if (!string.Equals(gateway, endpoint.Gateway, StringComparison.OrdinalIgnoreCase))
        {
            detail = $"gateway mismatch: actual={gateway} expected={endpoint.Gateway}";
            return false;
        }
        if (!string.Equals(upstream, "gitcode", StringComparison.OrdinalIgnoreCase))
        {
            detail = $"upstream mismatch: actual={upstream} expected=gitcode";
            return false;
        }
        if (index is null || index.Schema != 1 || !string.Equals(index.Repository, RepositoryLabel, StringComparison.Ordinal)
            || !string.Equals(index.Ref, "main", StringComparison.Ordinal) || index.Skins.Count == 0
            || string.IsNullOrWhiteSpace(index.Revision)
            || !string.Equals(index.Revision, headerRevision, StringComparison.OrdinalIgnoreCase)
            || index.Skins.Any(skin => skin.Id <= 0 || !TryNormalizeSkinPath(skin.Path, skin.Id, out _)))
        {
            detail = "schema, repository, revision, or skin path is invalid";
            return false;
        }
        detail = "ok";
        return true;
    }

    private static string ShortRevision(string? revision) =>
        string.IsNullOrWhiteSpace(revision) ? "<none>" : revision[..Math.Min(12, revision.Length)];

    private static bool TryNormalizeSkinPath(string? value, int skinId, out string path)
    {
        path = (value ?? "").Trim().Replace('\\', '/').TrimStart('/');
        if (!path.StartsWith("skins/", StringComparison.Ordinal)
            || !path.EndsWith($"/{skinId}.fantome", StringComparison.OrdinalIgnoreCase)
            || path.Contains("..", StringComparison.Ordinal)
            || path.Contains(':', StringComparison.Ordinal)) return false;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 4 or > 5 || parts.Skip(1).Take(parts.Length - 2).Any(part => !int.TryParse(part, out _))) return false;
        return true;
    }

    private static bool TryResolveTarget(string portableRoot, string relativePath, int skinId, out string target)
    {
        target = "";
        if (!TryNormalizeSkinPath(relativePath, skinId, out var normalized)) return false;
        try
        {
            var root = Path.GetFullPath(portableRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            target = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string CacheMarkerPath(string target) => target + ".source.json";

    private static bool HasTrustedCache(string target, string? expectedRevision, string? expectedBlobSha)
    {
        if (!TryReadValidatedCacheMarker(target, out var marker)) return false;
        if (!string.IsNullOrWhiteSpace(expectedRevision)
            && !string.Equals(marker.Revision, expectedRevision, StringComparison.OrdinalIgnoreCase)) return false;
        if (IsGitObjectId(expectedBlobSha)
            && !string.Equals(marker.BlobSha, expectedBlobSha, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool HasValidatedCache(string target) => TryReadValidatedCacheMarker(target, out _);

    private static string ReadCacheRevision(string target) =>
        TryReadValidatedCacheMarker(target, out var marker) ? marker.Revision : "";

    private static bool TryReadValidatedCacheMarker(string target, out CacheMarker marker)
    {
        marker = new CacheMarker();
        try
        {
            var file = new FileInfo(target);
            if (!file.Exists || file.Length <= 0) return false;
            var markerPath = CacheMarkerPath(target);
            if (!File.Exists(markerPath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("schema", out var schema)
                || !string.Equals(schema.GetString(), CacheMarkerSchema, StringComparison.Ordinal)
                || !root.TryGetProperty("bytes", out var bytes)
                || !bytes.TryGetInt64(out var expectedBytes)
                || expectedBytes != file.Length
                || !root.TryGetProperty("sha256", out var sha256)
                || string.IsNullOrWhiteSpace(sha256.GetString())) return false;
            marker = new CacheMarker
            {
                Revision = root.TryGetProperty("revision", out var revision) ? revision.GetString() ?? "" : "",
                BlobSha = root.TryGetProperty("blobSha", out var blobSha) ? blobSha.GetString() ?? "" : "",
            };
            using var stream = file.OpenRead();
            return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), sha256.GetString(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private static async Task<Dictionary<int, string>> LoadNamesFromFilesAsync(CancellationToken cancellationToken, string fileName = "skin_names_zh_CN.json")
    {
        var result = new Dictionary<int, string>();
        var path = AppPaths.Asset(fileName);
        if (!File.Exists(path)) return result;
        try
        {
            await using var stream = File.OpenRead(path);
            var payload = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken);
            if (payload is null) return result;
            foreach (var pair in payload)
                if (int.TryParse(pair.Key, out var skinId) && !string.IsNullOrWhiteSpace(pair.Value)) result[skinId] = pair.Value.Trim();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return result;
    }

    private static async Task<Dictionary<int, string>> LoadBundledPathIndexAsync(CancellationToken cancellationToken)
    {
        var paths = new Dictionary<int, string>();
        var path = AppPaths.Asset("skin_index.json");
        if (!File.Exists(path)) return paths;
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("bySkin", out var bySkin) || bySkin.ValueKind != JsonValueKind.Object) return paths;
            foreach (var property in bySkin.EnumerateObject())
            {
                if (!int.TryParse(property.Name, out var skinId) || !property.Value.TryGetProperty("relativePath", out var relativeValue)) continue;
                var relative = relativeValue.GetString()?.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(relative)) continue;
                if (!relative.StartsWith("skins/", StringComparison.OrdinalIgnoreCase)) relative = "skins/" + relative.TrimStart('/');
                if (TryNormalizeSkinPath(relative, skinId, out var normalized)) paths.TryAdd(skinId, normalized);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return paths;
    }

    private static bool ValidateFantomeArchive(string path, out string detail)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var packedWadCount = 0;
            var expandedWadFiles = 0;
            long expandedWadBytes = 0;
            var buffer = new byte[64 * 1024];
            foreach (var entry in archive.Entries)
            {
                var normalizedName = entry.FullName.Replace('\\', '/');
                if (normalizedName.StartsWith('/')
                    || normalizedName.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part == "..")
                    || (normalizedName.Length >= 2 && char.IsLetter(normalizedName[0]) && normalizedName[1] == ':'))
                {
                    detail = $"unsafe archive entry: {entry.FullName}";
                    return false;
                }
                if (!normalizedName.StartsWith("WAD/", StringComparison.OrdinalIgnoreCase) || normalizedName.EndsWith('/')) continue;
                var packedWad = normalizedName.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase);
                var expandedMarker = normalizedName.IndexOf(".wad.client/", StringComparison.OrdinalIgnoreCase);
                var expandedWad = expandedMarker >= 4 && expandedMarker + ".wad.client/".Length < normalizedName.Length;
                if (!packedWad && !expandedWad) continue;
                using var input = entry.Open();
                while (input.Read(buffer, 0, buffer.Length) > 0) { }
                if (packedWad) packedWadCount++;
                else
                {
                    expandedWadFiles++;
                    expandedWadBytes += entry.Length;
                }
            }
            if (packedWadCount == 0 && (expandedWadFiles == 0 || expandedWadBytes == 0))
            {
                detail = "no packed or expanded WAD entries";
                return false;
            }
            detail = $"packedWadCount={packedWadCount} expandedWadFiles={expandedWadFiles} expandedWadBytes={expandedWadBytes}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            detail = ex.Message;
            return false;
        }
    }

    private sealed record ResourceEndpoint(string Gateway, Uri BaseUrl);

    private sealed record RemoteError(string Code, string Message);

    private sealed class SkinAvailability
    {
        public bool Available { get; set; }
        public int SkinId { get; set; }
        public string Path { get; set; } = "";
        public string? Revision { get; set; }
        public string? BlobSha { get; set; }
    }

    private sealed record DownloadedIndex(WorkerIndex Index, string Gateway, string Upstream, string Endpoint);

    private sealed record DownloadedPackage(
        string Source,
        string Gateway,
        string Upstream,
        string Revision,
        string BlobSha,
        string ETag,
        long Bytes,
        long ElapsedMilliseconds);

    private sealed class CacheMarker
    {
        public string Revision { get; init; } = "";
        public string BlobSha { get; init; } = "";
    }

    private sealed class WorkerIndex
    {
        public int Schema { get; set; }
        public string Repository { get; set; } = "";
        public string Ref { get; set; } = "";
        public string? Revision { get; set; }
        public List<WorkerSkin> Skins { get; set; } = [];
    }

    private sealed class WorkerSkin
    {
        public int Id { get; set; }
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string BlobSha { get; set; } = "";
    }
}
