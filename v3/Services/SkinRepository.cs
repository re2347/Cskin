using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CskinNative.Services;

public sealed class SkinRepository
{
    public const string PrivateRepositoryUrl = "https://gitcode.com/Re2347/skin";
    public const string RepositoryLabel = "privateskin";
    private const string CacheMarkerSchema = "worker-private-v1";
    private const string CachedIndexFileName = "skin_worker_index.json";
    private static readonly Uri[] WorkerBaseUrls =
    [
        new("https://license.re2347.ccwu.cc/"),
        new("https://cskin-license-staging.2469416170.workers.dev/"),
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
    private ConcurrentDictionary<int, string> _previewPaths = [];
    private string? _portableRoot;
    private string _revision = "";
    private bool _stopping;
    private int _syncInProgress;

    public SkinRepository(string? portableRoot) => _portableRoot = portableRoot;

    public string RepositoryPath => $"{RepositoryLabel} via Worker";
    public string RepositoryUrl => PrivateRepositoryUrl;
    public int RemoteSkinCount => _paths.Count;
    public bool IsReady => _paths.Count > 0;
    public bool IsSyncing => Volatile.Read(ref _syncInProgress) != 0;
    public bool LastSyncChanged { get; private set; }
    public IReadOnlyDictionary<int, string> SkinPaths => _paths;
    public IReadOnlyDictionary<int, string> SkinNames => _names;
    public IReadOnlyDictionary<int, string> CachedPreviewPaths => _previewPaths;

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
            AppLog.Info($"开始同步私有皮肤索引：source={RepositoryLabel} transport=Worker gitClient=False");
            var remote = await DownloadIndexAsync(cancellationToken);
            if (remote is null || remote.Skins.Count == 0)
            {
                AppLog.Warn($"私有皮肤索引同步失败，继续使用本地索引：skins={_paths.Count}");
                return IsReady;
            }

            var paths = new Dictionary<int, string>();
            var names = new Dictionary<int, string>();
            foreach (var skin in remote.Skins)
            {
                if (skin.Id <= 0 || !TryNormalizeSkinPath(skin.Path, skin.Id, out var normalized)) continue;
                paths.TryAdd(skin.Id, normalized);
                if (!string.IsNullOrWhiteSpace(skin.Name)) names[skin.Id] = skin.Name.Trim();
            }
            if (paths.Count == 0)
            {
                AppLog.Warn("Worker 返回的私有皮肤索引不包含有效路径");
                return IsReady;
            }

            var previousRevision = _revision;
            _paths = new ConcurrentDictionary<int, string>(paths);
            _names = new ConcurrentDictionary<int, string>(MergeNames(names));
            _previewPaths = [];
            _revision = remote.Revision ?? "";
            LastSyncChanged = !string.Equals(previousRevision, _revision, StringComparison.OrdinalIgnoreCase);
            await SaveCachedIndexAsync(remote, cancellationToken);
            progress?.Report($"私有资源索引已就绪 · {_paths.Count:N0} 个皮肤");
            AppLog.Info($"私有皮肤索引同步完成：source={RepositoryLabel} skins={_paths.Count} revision={ShortRevision(_revision)}");
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
                AppLog.Info($"已加载上次 Worker 索引：skins={_paths.Count} revision={ShortRevision(_revision)}");
                return true;
            }

            var paths = await LoadBundledPathIndexAsync(cancellationToken);
            if (paths.Count == 0)
            {
                AppLog.Warn("随包皮肤路径索引为空，等待 Worker 索引");
                return false;
            }
            _paths = new ConcurrentDictionary<int, string>(paths);
            _names = new ConcurrentDictionary<int, string>(await LoadNamesFromFilesAsync(cancellationToken));
            progress?.Report($"随包资源索引已就绪 · {_paths.Count:N0} 个皮肤");
            AppLog.Info($"随包皮肤路径索引已就绪：skins={_paths.Count}；远程更新通过 Worker 获取");
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
        return HasTrustedCache(target) ? target : null;
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
        AppLog.Info($"准备下载皮肤：skinId={skinId} source={RepositoryLabel} transport=Worker engineRoot={_portableRoot} target={target} relativePath={relativePath}");
        if (HasTrustedCache(target)) return target;

        await _downloadGate.WaitAsync(cancellationToken);
        try
        {
            if (HasTrustedCache(target)) return target;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".partial";
            try { File.Delete(temp); } catch { }
            var downloaded = await DownloadSkinAsync(skinId, relativePath, temp, cancellationToken);
            if (downloaded is null || !File.Exists(temp) || new FileInfo(temp).Length <= 0)
            {
                try { File.Delete(temp); } catch { }
                AppLog.Error($"Worker 未能下载皮肤：skinId={skinId} path={relativePath}");
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
                etag = downloaded.ETag,
                bytes = targetBytes,
                sha256 = packageSha256,
            });
            await File.WriteAllTextAsync(CacheMarkerPath(target), marker, new UTF8Encoding(false), cancellationToken);
            AppLog.Info($"皮肤下载完成：skinId={skinId} target={target} bytes={targetBytes} source={downloaded.Source} revision={ShortRevision(downloaded.Revision)}");
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

    private async Task<WorkerIndex?> DownloadIndexAsync(CancellationToken cancellationToken)
    {
        foreach (var endpoint in WorkerBaseUrls)
        {
            if (_stopping) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "v1/skins/index"));
                if (!AddLeaseHeaders(request))
                {
                    AppLog.Warn("私有资源请求缺少有效授权租约，请重新验证密钥");
                    return null;
                }
                using var response = await RepositoryHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    AppLog.Warn($"私有皮肤索引授权被拒绝：endpoint={endpoint.Host} status={(int)response.StatusCode}");
                    return null;
                }
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Warn($"私有皮肤索引请求失败：endpoint={endpoint.Host} status={(int)response.StatusCode}");
                    continue;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var value = await JsonSerializer.DeserializeAsync<WorkerIndex>(stream, JsonOptions, timeout.Token);
                if (value is not null) return value;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                AppLog.Warn($"私有皮肤索引请求异常：endpoint={endpoint.Host} error={ex.Message}");
            }
        }
        return null;
    }

    private async Task<DownloadedPackage?> DownloadSkinAsync(int skinId, string relativePath, string outputPath, CancellationToken cancellationToken)
    {
        foreach (var endpoint in WorkerBaseUrls)
        {
            if (_stopping) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    new Uri(endpoint, "v1/skins/file?path=" + Uri.EscapeDataString(relativePath)));
                if (!AddLeaseHeaders(request)) return null;
                using var response = await RepositoryHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    AppLog.Warn($"私有皮肤下载授权被拒绝：skinId={skinId} endpoint={endpoint.Host} status={(int)response.StatusCode}");
                    return null;
                }
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Warn($"私有皮肤下载未命中：skinId={skinId} endpoint={endpoint.Host} status={(int)response.StatusCode} path={relativePath}");
                    continue;
                }

                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var destination = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
                await source.CopyToAsync(destination, timeout.Token);
                await destination.FlushAsync(timeout.Token);
                var bytes = destination.Length;
                if (bytes < 2) continue;
                var revision = Header(response, "X-Cskin-Revision");
                var etag = response.Headers.ETag?.Tag ?? Header(response, "ETag");
                AppLog.Info($"Worker 皮肤下载完成：skinId={skinId} endpoint={endpoint.Host} bytes={bytes} revision={ShortRevision(revision)} path={relativePath}");
                return new DownloadedPackage("worker-privateskin", revision, etag, bytes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                try { File.Delete(outputPath); } catch { }
                AppLog.Warn($"私有皮肤下载异常：skinId={skinId} endpoint={endpoint.Host} path={relativePath} error={ex.Message}");
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
        foreach (var skin in index.Skins)
        {
            if (skin.Id <= 0 || !TryNormalizeSkinPath(skin.Path, skin.Id, out var path)) continue;
            paths.TryAdd(skin.Id, path);
            if (!string.IsNullOrWhiteSpace(skin.Name)) names[skin.Id] = skin.Name.Trim();
        }
        _paths = new ConcurrentDictionary<int, string>(paths);
        _names = new ConcurrentDictionary<int, string>(MergeNames(names));
        _revision = index.Revision ?? "";
    }

    private Dictionary<int, string> MergeNames(Dictionary<int, string> remoteNames)
    {
        foreach (var pair in _names) remoteNames.TryAdd(pair.Key, pair.Value);
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

    private static bool HasTrustedCache(string target)
    {
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
            using var stream = file.OpenRead();
            return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), sha256.GetString(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private static async Task<Dictionary<int, string>> LoadNamesFromFilesAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();
        var path = AppPaths.Asset("skin_names_zh_CN.json");
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

    private sealed record DownloadedPackage(string Source, string Revision, string ETag, long Bytes);

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
    }
}
