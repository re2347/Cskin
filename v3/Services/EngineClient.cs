using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CskinNative.Services;

public sealed class EngineClient : IDisposable
{
    private static readonly int[] CandidatePorts = [5336, 13055, 13581];
    private static readonly Regex ReadyLine = new(
        @"Selector ready at http://127\.0\.0\.1:(?<port>[1-9]\d{2,5})/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const int MaxLogProbePorts = 4;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(750);
    private readonly HttpClient _http;
    private Process? _engineProcess;
    private int _port;
    private bool _startedEngine;
    private string? _gameDirectory;
    private string? _configuredGameDirectory;
    private string? _configuredEnginePath;

    public EngineClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 64,
            AutomaticDecompression = DecompressionMethods.All
        };
        // Rebuilding the portable catalog walks thousands of .fantome files;
        // it commonly takes 8-15 seconds on first run. Keep probe requests
        // short (they use their own linked token) while allowing API writes
        // enough time to finish instead of reporting a false apply failure.
        // Apply returns as soon as runoverlay is armed; the engine observes DLL
        // redirection in the background. The host application applies its own
        // short deadline; the engine subprocess keeps its independent timeout
        // so its concrete error can still be logged.
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(200) };
    }

    public string? EnginePath { get; private set; }
    public string? PortableRoot => EnginePath is null ? null : Path.GetDirectoryName(EnginePath);
    public bool IsReady => _port != 0;
    public string? GameDirectory => _gameDirectory ?? _configuredGameDirectory;

    public void SetGameDirectory(string? path) => _configuredGameDirectory = NormalizeGameDirectory(path);

    public void SetEngineExecutable(string? path)
    {
        var requested = NormalizeEngineExecutable(path);
        var normalized = FindEngine(requested);
        if (requested is not null && normalized is not null
            && !string.Equals(requested, normalized, StringComparison.OrdinalIgnoreCase))
            AppLog.Warn($"引擎路径已重定向到当前便携包：requested={requested} selected={normalized}");
        if (string.Equals(EnginePath, normalized, StringComparison.OrdinalIgnoreCase)) return;
        StopStartedEngine();
        EnginePath = normalized;
        _configuredEnginePath = requested;
    }

    public static string? NormalizeEngineExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
            return Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath)
                ? fullPath
                : null;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public Task<bool> RestartForGameDirectoryAsync(CancellationToken cancellationToken = default) => RestartAsync(cancellationToken);

    public static string? TryDetectLeagueGameDirectory() => FindLeagueGameDirectory();

    public static string? NormalizeGameDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var raw = path.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        var candidates = new List<string>();
        void AddCandidate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                var full = Path.GetFullPath(value);
                if (!candidates.Contains(full, StringComparer.OrdinalIgnoreCase)) candidates.Add(full);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }

        // The settings dialog accepts the common portable form
        // `wegameapps\\英雄联盟\\Game`. Resolve that form against every
        // ready fixed drive instead of the installer's current directory.
        AddCandidate(raw);
        if (!Path.IsPathRooted(raw))
        {
            foreach (var driveRoot in FixedDriveRoots())
                AddCandidate(Path.Combine(driveRoot, raw));
        }

        foreach (var candidate in candidates)
        {
            if (IsGameDirectory(candidate)) return candidate;
            var nestedGame = Path.Combine(candidate, "Game");
            if (IsGameDirectory(nestedGame)) return Path.GetFullPath(nestedGame);
        }
        return null;
    }

    public async Task<bool> EnsureReadyAsync(string? configuredPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        EnginePath ??= FindEngine(configuredPath ?? _configuredEnginePath);
        // A configured game directory must be passed in the child process
        // environment. Do not attach to a stale engine that was launched
        // before the setting was saved; it may still point at another PC's
        // install layout or at the legacy selector.
        var mayReuseRunningEngine = _configuredGameDirectory is null || _startedEngine;
        if (mayReuseRunningEngine && await ProbeCandidatesAsync(cancellationToken)) return true;

        // A previous launch can still be using an earlier game directory.
        // Replace only the selected engine process before starting it again.
        if (EnginePath is null)
        {
            AppLog.Error("本地引擎文件缺失，无法启动");
            progress?.Report("安装器中的本地引擎缺失，请使用完整安装包重新安装");
            return false;
        }

        progress?.Report("正在启动本地引擎");
        var startInfo = new ProcessStartInfo
        {
            FileName = EnginePath,
            WorkingDirectory = Path.GetDirectoryName(EnginePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        var gameDirectory = ResolveGameDirectory();
        _gameDirectory = gameDirectory;
        if (gameDirectory is not null) startInfo.Environment["AATROX_GAME_DIR"] = gameDirectory;
        // The portable engine exposes its API through a local HTTP server and
        // otherwise opens the legacy browser selector on every launch.
        startInfo.ArgumentList.Add("--no-browser");
        try
        {
            _engineProcess = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            AppLog.Error($"本地引擎启动异常：{EnginePath}", ex);
            progress?.Report($"本地引擎启动失败：{ex.Message}");
            return false;
        }
        if (_engineProcess is null)
        {
            AppLog.Error($"本地引擎启动失败：{EnginePath}");
            progress?.Report("本地引擎启动失败");
            return false;
        }
        _startedEngine = true;

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(14);
        while (DateTime.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ProbeCandidatesAsync(cancellationToken)) return true;
            await Task.Delay(250, cancellationToken);
        }

        progress?.Report("本地引擎启动超时");
        AppLog.Error($"本地引擎启动超时：{EnginePath}");
        StopStartedEngine();
        return false;
    }

    public async Task<Health?> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!IsReady && !await ProbeCandidatesAsync(cancellationToken)) return null;
        return await GetAsync<Health>("/api/health", cancellationToken);
    }

    public Task<List<Champion>?> GetChampionsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<Champion>>("/api/champions", cancellationToken);

    public Task<Champion?> GetChampionAsync(int id, CancellationToken cancellationToken = default) =>
        GetAsync<Champion>($"/api/champions/{id}", cancellationToken);

    public Task<ChampionSelection?> GetChampionSelectionAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ChampionSelection>("/api/champion-selection", cancellationToken);

    public async Task<RebuildResult?> RebuildAsync(CancellationToken cancellationToken = default)
    {
        if (!IsReady) return null;
        try
        {
            using var response = await PostJsonAsync("/api/rebuild", new { }, cancellationToken);
            if (response.IsSuccessStatusCode)
                return await ReadResponseAsync<RebuildResult>(response, cancellationToken);
            if (response.StatusCode != HttpStatusCode.NotFound) return null;
        }
        catch (HttpRequestException ex)
        {
            AppLog.WarnThrottled("engine-rebuild-http", $"引擎索引重建请求失败：{ex.Message}", TimeSpan.FromSeconds(10));
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.WarnThrottled("engine-rebuild-timeout", "引擎索引重建请求超时", TimeSpan.FromSeconds(10));
            return null;
        }

        // Older API-only engines have no rebuild route. Restarting the process
        // is a single bounded fallback so it can reload the generated index.
        var restarted = await RestartAsync(cancellationToken);
        return restarted ? new RebuildResult { Skins = 0 } : null;
    }

    private async Task<bool> RestartAsync(CancellationToken cancellationToken)
    {
        StopStartedEngine();
        _port = 0;
        return await EnsureReadyAsync(EnginePath, cancellationToken: cancellationToken);
    }

    private async Task<bool> RestartWithGameDirectoryAsync(CancellationToken cancellationToken)
    {
        _gameDirectory = ResolveGameDirectory();
        if (_gameDirectory is null) return false;
        StopStartedEngine();
        _port = 0;
        return await EnsureReadyAsync(EnginePath, cancellationToken: cancellationToken);
    }

    public async Task<ApplyResult?> ApplyAsync(int skinId, CancellationToken cancellationToken = default, int targetSkinId = 0)
    {
        if (!IsReady)
        {
            AppLog.Error($"皮肤应用请求被拒绝：引擎未就绪 skinId={skinId} targetSkinId={targetSkinId}");
            LogEngineDiagnostics("apply-engine-not-ready");
            return null;
        }
        var requestStartedUtc = DateTime.UtcNow;
        var retriedTransientResponse = false;
        var restartedForGameDirectory = false;
        AppLog.Info($"提交皮肤应用：skinId={skinId} targetSkinId={targetSkinId} port={_port} "
            + $"engine={EnginePath} engineRoot={PortableRoot} game={GameDirectory}");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            HttpResponseMessage response;
            string payload;
            try
            {
                response = await PostJsonAsync("/api/apply", new { skinId, targetSkinId }, cancellationToken);
                payload = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                AppLog.Error($"皮肤应用请求网络失败：skinId={skinId} targetSkinId={targetSkinId} "
                    + $"port={_port} engine={EnginePath}", ex);
                LogEngineDiagnostics($"apply-http-error attempt={attempt + 1}");
                throw;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                AppLog.Error($"皮肤应用请求超时：skinId={skinId} targetSkinId={targetSkinId} port={_port}", ex);
                LogEngineDiagnostics($"apply-timeout attempt={attempt + 1}");
                throw;
            }
            using (response)
            {
                var responseSummary = payload.Length > 1200 ? payload[..1200] + "…" : payload;
                AppLog.Info($"皮肤应用响应：attempt={attempt + 1} status={(int)response.StatusCode} "
                    + $"success={response.IsSuccessStatusCode} body={responseSummary}");
            if (!response.IsSuccessStatusCode)
            {
                var error = ExtractError(payload);
                AppLog.Warn($"皮肤应用失败响应：skinId={skinId} targetSkinId={targetSkinId} "
                    + $"status={(int)response.StatusCode} error={error}");
                LogEngineDiagnostics($"apply-http-{(int)response.StatusCode} attempt={attempt + 1}");
                if (!restartedForGameDirectory && IsGameDirectoryError(error)
                    && await RestartWithGameDirectoryAsync(cancellationToken))
                {
                    restartedForGameDirectory = true;
                    continue;
                }
                // The Python service can answer once with JSONDecodeError while
                // its overlay worker finishes warming up. A short retry is
                // enough; surfacing it immediately makes a valid apply look
                // like a broken engine.
                if (!retriedTransientResponse && IsTransientApplyError(error))
                {
                    retriedTransientResponse = true;
                    await Task.Delay(180, cancellationToken);
                    continue;
                }
                return new ApplyResult { Ok = false, Error = error };
            }

            if (TryDeserialize(payload, out ApplyResult? result) && result is not null)
            {
                if (!result.Ok)
                {
                    LogEngineDiagnostics($"apply-json-error attempt={attempt + 1}");
                    return result;
                }
                if (result.SkinId > 0 && result.SkinId != skinId)
                {
                    LogEngineDiagnostics($"apply-skin-mismatch attempt={attempt + 1}");
                    return new ApplyResult { Ok = false, Error = "本地引擎返回的皮肤编号不匹配" };
                }
                // A valid JSON result is authoritative. Existing cached
                // overlays do not rewrite info.json on every apply, so a
                // timestamp check here would reject legitimate re-applies.
                AppLog.Info($"皮肤应用响应确认：skinId={skinId} mode={result.Mode} overlayStatus={result.OverlayStatus} "+
                    $"injectionStatus={result.InjectionStatus} wadCount={result.OverlayWadCount} "
                    + $"wadBytes={result.OverlayWadBytes} engineRoot={PortableRoot}");
                LogEngineDiagnostics($"apply-confirmed attempt={attempt + 1}");
                return result;
            }

            // Some older engine builds arm the overlay and then close the
            // response without a body. The marker is written only after the
            // requested package has been copied, so it is a reliable success
            // fallback for that response quirk.
            if (HasRecentOverlay(skinId, requestStartedUtc))
            {
                LogEngineDiagnostics($"apply-marker-fallback attempt={attempt + 1}");
                return new ApplyResult { Ok = true, OverlayStatus = "armed", SkinId = skinId };
            }

            LogEngineDiagnostics($"apply-invalid-response attempt={attempt + 1}");
            return new ApplyResult { Ok = false, Error = "本地引擎返回无效响应，请重试应用" };
            }
        }

        return new ApplyResult { Ok = false, Error = "本地引擎应用请求超出重试上限" };
    }

    private static bool IsGameDirectoryError(string error) =>
        error.Contains("英雄联盟游戏目录", StringComparison.Ordinal)
        || error.Contains("game directory", StringComparison.OrdinalIgnoreCase);

    private static string ExtractError(string payload)
    {
        var fallback = FriendlyError(payload);
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("error", out var value))
                return FriendlyError(value.GetString());
            return fallback;
        }
        catch (JsonException) { return fallback; }
    }

    private static string FriendlyError(string? payload)
    {
        var value = payload?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(value)) return "本地引擎返回空响应，请重试应用";
        if (value.StartsWith("Expecting value", StringComparison.OrdinalIgnoreCase)
            || value.Contains("JSONDecodeError", StringComparison.OrdinalIgnoreCase))
            return "本地引擎返回无效响应，请重试应用";
        return value;
    }

    private static bool IsTransientApplyError(string error) =>
        error.Equals("本地引擎返回无效响应，请重试应用", StringComparison.Ordinal)
        || error.Equals("本地引擎返回空响应，请重试应用", StringComparison.Ordinal)
        || error.Contains("JSONDecodeError", StringComparison.OrdinalIgnoreCase)
        || error.StartsWith("Expecting value", StringComparison.OrdinalIgnoreCase);

    private bool HasRecentOverlay(int skinId, DateTime requestStartedUtc)
    {
        if (skinId <= 0) return false;
        var root = PortableRoot;
        if (string.IsNullOrWhiteSpace(root)) return false;
        var marker = Path.Combine(
            root,
            "data",
            "injection",
            "mods",
            $"skin_{skinId}",
            "META",
            "info.json");
        try
        {
            if (!File.Exists(marker)) return false;
            // File timestamps on some Windows volumes have a two-second
            // granularity, so allow a small clock skew around the request.
            return File.GetLastWriteTimeUtc(marker) >= requestStartedUtc.AddSeconds(-3);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void LogEngineDiagnostics(string reason)
    {
        var root = PortableRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            AppLog.Warn($"引擎诊断跳过：reason={reason} engineRoot=空 engine={EnginePath}");
            return;
        }

        foreach (var relative in new[] { Path.Combine("data", "selector.log"),
                                          Path.Combine("data", "injection", "overlay.log") })
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path))
            {
                AppLog.Warn($"引擎诊断日志不存在：reason={reason} path={path}");
                continue;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var length = stream.Length;
                var offset = Math.Max(0, length - 8 * 1024);
                stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var tail = reader.ReadToEnd();
                var compact = Regex.Replace(tail, @"\s+", " ").Trim();
                if (compact.Length > 8000) compact = compact[^8000..];
                AppLog.Info($"引擎诊断日志：reason={reason} path={path} bytes={length} "
                    + $"tail={(string.IsNullOrWhiteSpace(compact) ? "<empty>" : compact)}");
            }
            catch (IOException ex)
            {
                AppLog.Warn($"读取引擎诊断日志失败：reason={reason} path={path} error={ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLog.Warn($"读取引擎诊断日志无权限：reason={reason} path={path} error={ex.Message}");
            }
        }
    }

    private async Task<bool> ProbeCandidatesAsync(CancellationToken cancellationToken)
    {
        foreach (var port in ReadLogPorts().Concat(CandidatePorts).Distinct())
        {
            try
            {
                using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCancellation.CancelAfter(ProbeTimeout);
                using var response = await _http.GetAsync($"http://127.0.0.1:{port}/api/health", probeCancellation.Token);
                if (!response.IsSuccessStatusCode) continue;
                var health = await response.Content.ReadFromJsonAsync<Health>(cancellationToken: probeCancellation.Token);
                // Refuse the legacy web selector even if it is still running
                // on a discovered port. Only the API-only engine is supported.
                if (health?.Engine != true || health.RoseEngine != true) continue;
                // Several Cskin variants expose the same health endpoint. A
                // response alone is not enough: only reuse a process owned by
                // this portable installation, otherwise apply/sync can target a
                // stale web-era engine with a different game directory.
                if (!IsOwnedEnginePort(port)) continue;
                _port = port;
                AppLog.Info($"引擎就绪确认：port={port} engine={EnginePath} engineRoot={PortableRoot} "
                    + $"game={GameDirectory}");
                return true;
            }
            catch (HttpRequestException ex)
            {
                AppLog.WarnThrottled("engine-probe-http", $"引擎端口 {port} 探测失败：{ex.Message}", TimeSpan.FromSeconds(10));
            }
            catch (JsonException ex)
            {
                AppLog.WarnThrottled("engine-probe-json", $"引擎端口 {port} 返回无效健康数据：{ex.Message}", TimeSpan.FromSeconds(10));
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                AppLog.WarnThrottled("engine-probe-timeout", $"引擎端口 {port} 探测超时", TimeSpan.FromSeconds(10));
            }
        }
        return false;
    }

    private IEnumerable<int> ReadLogPorts()
    {
        var root = PortableRoot;
        if (string.IsNullOrWhiteSpace(root)) yield break;

        var logPath = Path.Combine(root, "data", "selector.log");
        if (!File.Exists(logPath)) yield break;

        string tail;
        try
        {
            // selector.log can grow to several megabytes. Only the tail is
            // relevant because each restart writes a fresh "ready" line.
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var offset = Math.Max(0, length - 64 * 1024);
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            tail = reader.ReadToEnd();
        }
        catch (IOException ex)
        {
            AppLog.WarnThrottled("engine-log-read", $"读取引擎日志失败：{ex.Message}", TimeSpan.FromSeconds(10));
            yield break;
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.WarnThrottled("engine-log-permission", $"读取引擎日志无权限：{ex.Message}", TimeSpan.FromSeconds(10));
            yield break;
        }

        var ports = ReadyLine.Matches(tail)
            .Select(match => int.TryParse(match.Groups["port"].Value, out var port) ? port : 0)
            .Where(port => port is > 0 and <= 65535)
            .Reverse()
            .Distinct()
            .Take(MaxLogProbePorts);
        foreach (var port in ports) yield return port;
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(BaseUrl(path), cancellationToken);
        if (!response.IsSuccessStatusCode)
            AppLog.WarnThrottled($"engine-api-{path}", $"引擎接口 {path} 返回 HTTP {(int)response.StatusCode}", TimeSpan.FromSeconds(10));
        return await ReadResponseAsync<T>(response, cancellationToken);
    }

    private async Task<T?> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) return default;
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return TryDeserialize(payload, out T? value) ? value : default;
    }

    private static bool TryDeserialize<T>(string payload, out T? value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            value = JsonSerializer.Deserialize<T>(payload, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string BaseUrl(string path) => $"http://127.0.0.1:{_port}{path}";

    private async Task<HttpResponseMessage> PostJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        // The embedded Python HTTP server does not parse chunked request
        // bodies reliably. StringContent sets Content-Length explicitly.
        using var content = new StringContent(
            JsonSerializer.Serialize(value),
            Encoding.UTF8,
            "application/json");
        return await _http.PostAsync(BaseUrl(path), content, cancellationToken);
    }

    private static string? FindEngine(string? configuredPath)
    {
        var bundled = AppPaths.BundledEngineExecutable;
        var configured = NormalizeEngineExecutable(configuredPath);
        if (configured is null) return bundled;

        // A copied portable folder may carry app-settings.json from another
        // computer. Never route downloads and overlays to that stale absolute
        // path when the current folder contains its own Engine tree. An
        // explicitly selected engine inside this portable root remains valid.
        if (bundled is not null && !IsUnderRoot(configured, AppPaths.Root))
        {
            AppLog.Warn($"忽略便携包外的旧引擎路径：{configured}，改用随包引擎：{bundled}");
            return bundled;
        }
        return configured;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private string? ResolveGameDirectory()
    {
        if (IsGameDirectory(_configuredGameDirectory)) return Path.GetFullPath(_configuredGameDirectory!);
        return FindLeagueGameDirectory();
    }

    public void Dispose()
    {
        StopStartedEngine();
        _http.Dispose();
    }

    public void Stop()
    {
        StopStartedEngine();
    }

    private static string? FindLeagueGameDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("AATROX_GAME_DIR");
        var normalizedConfigured = NormalizeGameDirectory(configured);
        if (normalizedConfigured is not null) return normalizedConfigured;

        // Reading the executable path is locale-independent and works even
        // when WMIC returns the Chinese command line in the wrong code page.
        foreach (var process in Process.GetProcessesByName("LeagueClientUx"))
        {
            try
            {
                foreach (var candidate in GameDirectoriesFromClientExecutable(process.MainModule?.FileName))
                {
                    if (IsGameDirectory(candidate)) return Path.GetFullPath(candidate);
                }
            }
            catch { }
            finally { process.Dispose(); }
        }

        // WeGame installs the client under a localized directory. Prefer the
        // known game executable locations before parsing WMIC's command line.
        foreach (var candidate in KnownGameDirectoryCandidates())
        {
            if (IsGameDirectory(candidate)) return Path.GetFullPath(candidate);
        }

        // The client lockfile is independent of the system locale and works
        // when reading another process's executable path is denied by UAC.
        var lockfileGameDirectory = LeagueClient.TryFindLeagueGameDirectoryFromLockfile();
        if (lockfileGameDirectory is not null) return lockfileGameDirectory;

        // A few clients expose a process path but no lockfile while starting;
        // make one final executable-path attempt before giving up.
        foreach (var process in Process.GetProcessesByName("LeagueClientUx"))
        {
            try
            {
                var executable = process.MainModule?.FileName;
                var clientDirectory = executable is null ? null : Path.GetDirectoryName(executable);
                var gameDirectory = clientDirectory is null ? null : Path.Combine(Directory.GetParent(clientDirectory)?.FullName ?? clientDirectory, "Game");
                if (IsGameDirectory(gameDirectory)) return Path.GetFullPath(gameDirectory!);
            }
            catch { }
            finally { process.Dispose(); }
        }
        return null;
    }

    private static bool IsGameDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "League of Legends.exe"));

    private bool IsOwnedEnginePort(int port)
    {
        var pid = FindListenerPid(port);
        if (pid <= 0 || string.IsNullOrWhiteSpace(EnginePath)) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            var executable = process.MainModule?.FileName;
            if (!string.Equals(executable, EnginePath, StringComparison.OrdinalIgnoreCase)) return false;

            // An installer can replace the portable engine while a crashed or
            // abandoned process from the previous build is still listening on
            // the same port. Do not attach to that stale process: its Python
            // bytecode and overlay behavior may differ from the files on disk.
            var engineWriteTime = File.GetLastWriteTimeUtc(EnginePath);
            var processStartTime = process.StartTime.ToUniversalTime();
            if (processStartTime >= engineWriteTime.AddSeconds(-2)) return true;

            // It is our engine path, but it predates the installed files. End
            // that abandoned process before EnsureReadyAsync starts the new
            // build, otherwise its overlay can remain active in the game.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch { }
            return false;
        }
        catch { return false; }
    }

    private static int FindListenerPid(int port)
    {
        if (port <= 0) return 0;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netstat.exe",
                    Arguments = "-ano -p tcp",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.ASCII
                }
            };
            if (!process.Start()) return 0;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!outputTask.Wait(TimeSpan.FromSeconds(1.5)))
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
                return 0;
            }

            foreach (var line in outputTask.GetAwaiter().GetResult().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length < 5
                    || !columns[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)
                    || !columns[^2].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                var local = columns[1];
                if (!local.EndsWith($":{port}", StringComparison.Ordinal)) continue;
                return int.TryParse(columns[^1], out var pid) ? pid : 0;
            }
            try { if (!process.HasExited) process.Kill(); } catch { }
        }
        catch { }
        return 0;
    }

    private static IEnumerable<string> KnownGameDirectoryCandidates()
    {
        foreach (var driveRoot in FixedDriveRoots())
        {
            yield return Path.Combine(driveRoot, "wegameapps", "英雄联盟", "Game");
            yield return Path.Combine(driveRoot, "Program Files", "腾讯游戏", "英雄联盟", "Game");
            yield return Path.Combine(driveRoot, "Program Files (x86)", "腾讯游戏", "英雄联盟", "Game");
            yield return Path.Combine(driveRoot, "Riot Games", "League of Legends", "Game");
        }
    }

    private static IEnumerable<string> GameDirectoriesFromClientExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) yield break;
        string? directory;
        try { directory = Path.GetDirectoryName(executablePath); }
        catch (ArgumentException) { yield break; }
        if (string.IsNullOrWhiteSpace(directory)) yield break;

        // Handle both ...\\英雄联盟\\LeagueClient\\LeagueClientUx.exe and
        // a client executable placed directly below the Game directory.
        var current = new DirectoryInfo(directory);
        for (var depth = 0; current is not null && depth < 4; depth++, current = current.Parent)
        {
            yield return Path.Combine(current.FullName, "Game");
            if (current.Name.Equals("Game", StringComparison.OrdinalIgnoreCase))
                yield return current.FullName;
        }
    }

    private static IEnumerable<string> FixedDriveRoots()
    {
        var roots = new List<string>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return roots; }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                    roots.Add(drive.RootDirectory.FullName);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return roots;
    }

    private void StopStartedEngine()
    {
        if (!_startedEngine)
        {
            _engineProcess?.Dispose();
            _engineProcess = null;
            return;
        }

        try
        {
            if (_engineProcess is { HasExited: false })
            {
                _engineProcess.Kill(entireProcessTree: true);
                _engineProcess.WaitForExit(3000);
            }
        }
        catch { }
        finally
        {
            _engineProcess?.Dispose();
            _engineProcess = null;
            _startedEngine = false;
            _port = 0;
        }
    }
}
