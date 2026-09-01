using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CskinNative.Services;

public sealed class LeagueClient : IDisposable
{
    private static readonly string[] LockfileNames = ["lockfile"];
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Task<bool>? _refreshTask;
    private int _port;
    private int _pid;
    private string? _token;
    private string? _configuredGameDirectory;
    private DateTime _nextProbeUtc;
    private static readonly string[] ClientProcessNames = ["LeagueClientUx", "LeagueClient", "LeagueClientUxRender"];
    private static readonly TimeSpan FailedProbeBackoff = TimeSpan.FromSeconds(2);

    public LeagueClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AutomaticDecompression = DecompressionMethods.All
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2.5) };
    }

    public void SetGameDirectory(string? path)
    {
        _configuredGameDirectory = EngineClient.NormalizeGameDirectory(path);
        InvalidateConnection();
    }

    public async Task<ChampionSelection?> GetSelectionAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureConnectionAsync(cancellationToken)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{_port}/lol-champ-select/v1/session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicToken(_token!));
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            AppLog.InfoThrottled(
                "lcu-champ-select-http",
                $"LCU 英雄选择接口：HTTP {(int)response.StatusCode} ({response.StatusCode}) port={_port}",
                TimeSpan.FromSeconds(5));
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var activeSelection = await ReadActiveSelectionFallbackAsync(cancellationToken);
                if (activeSelection is { ResolvedChampionId: > 0 })
                {
                    AppLog.InfoThrottled(
                        "lcu-active-selection-fallback",
                        $"LCU 备用英雄接口读取成功：championId={activeSelection.ResolvedChampionId} skinId={activeSelection.SelectedSkinId} phase={activeSelection.Phase}",
                        TimeSpan.FromSeconds(5));
                    return activeSelection;
                }

                var gameflow = await ReadGameflowSelectionAsync(cancellationToken);
                if (gameflow is { ResolvedChampionId: > 0 })
                {
                    AppLog.InfoThrottled(
                        "lcu-gameflow-selection",
                        $"LCU 英雄选择会话已结束，改用游戏流选择：championId={gameflow.ResolvedChampionId} skinId={gameflow.SelectedSkinId} phase={gameflow.Phase}",
                        TimeSpan.FromSeconds(10));
                    return gameflow;
                }

                var phase = gameflow?.Phase ?? await ReadGameflowPhaseAsync(cancellationToken);
                var phaseText = string.IsNullOrWhiteSpace(phase) ? "未知" : phase;
                AppLog.InfoThrottled("lcu-no-session", $"LCU 已连接，但当前没有活动的英雄选择会话：阶段={phaseText}", TimeSpan.FromSeconds(10));
                return new ChampionSelection { Available = true, Phase = phase };
            }
            if (!response.IsSuccessStatusCode)
            {
                AppLog.WarnThrottled("lcu-status", $"LCU 接口返回 HTTP {(int)response.StatusCode} ({response.StatusCode})", TimeSpan.FromSeconds(10));
                InvalidateConnection();
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var hasLocalCellId = TryGetInt(root, "localPlayerCellId", out var localCellId) && localCellId >= 0;
            var currentSummonerId = hasLocalCellId ? 0 : await ReadCurrentSummonerIdAsync(cancellationToken);
            var selection = ParseSelection(root, currentSummonerId);
            AppLog.InfoThrottled(
                "lcu-selection-result",
                $"LCU 英雄选择解析：available={selection.Available} championId={selection.ResolvedChampionId} selectedSkinId={selection.SelectedSkinId} phase={selection.Phase}",
                TimeSpan.FromSeconds(5));
            if (selection.ResolvedChampionId <= 0)
            {
                // During the short transition between hover/lock and game
                // start the champ-select endpoint can return HTTP 200 with
                // an empty team. The gameflow payload may already contain
                // the local slot, so consult it before reporting an empty
                // selection to the UI.
                var gameflow = await ReadGameflowSelectionAsync(cancellationToken);
                if (gameflow is { ResolvedChampionId: > 0 })
                {
                    AppLog.InfoThrottled(
                        "lcu-gameflow-selection-empty-session",
                        $"LCU 英雄选择接口为空，改用游戏流选择：championId={gameflow.ResolvedChampionId} skinId={gameflow.SelectedSkinId} phase={gameflow.Phase}",
                        TimeSpan.FromSeconds(5));
                    return gameflow;
                }
                if (gameflow is not null && string.IsNullOrWhiteSpace(selection.Phase))
                {
                    selection.Phase = gameflow.Phase;
                    AppLog.InfoThrottled(
                        "lcu-gameflow-phase-empty-session",
                        $"LCU 英雄选择尚未选定英雄，使用游戏流阶段：{selection.Phase}",
                        TimeSpan.FromSeconds(5));
                }
            }
            return selection;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.WarnThrottled("lcu-timeout", "LCU 请求超时，正在重新探测客户端", TimeSpan.FromSeconds(10));
            InvalidateConnection();
            return null;
        }
        catch (HttpRequestException ex)
        {
            AppLog.WarnThrottled("lcu-http", $"LCU 请求失败：{ex.Message}", TimeSpan.FromSeconds(10));
            InvalidateConnection();
            return null;
        }
        catch (JsonException ex)
        {
            AppLog.WarnThrottled("lcu-json", $"LCU 返回数据无法解析：{ex.Message}", TimeSpan.FromSeconds(10));
            return null;
        }
    }

    private async Task<ChampionSelection?> ReadActiveSelectionFallbackAsync(CancellationToken cancellationToken)
    {
        foreach (var path in new[] { "/lol-champ-select/v1/session/my-selection", "/lol-champ-select/v1/current-champion" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{_port}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicToken(_token!));
            try
            {
                using var response = await _http.SendAsync(request, cancellationToken);
                AppLog.InfoThrottled(
                    $"lcu-fallback-http-{path}",
                    $"LCU 备用英雄接口 {path}：HTTP {(int)response.StatusCode} ({response.StatusCode}) port={_port}",
                    TimeSpan.FromSeconds(5));
                if (!response.IsSuccessStatusCode) continue;
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var selection = ParseFallbackSelection(body);
                if (selection is { ResolvedChampionId: > 0 }) return selection;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (HttpRequestException) { }
            catch (JsonException) { }
        }

        return null;
    }

    private static ChampionSelection? ParseFallbackSelection(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var trimmed = body.Trim();
        if (int.TryParse(trimmed.Trim('"'), out var championId) && championId > 0)
        {
            return new ChampionSelection { Available = true, ChampionId = championId, SelectedSkinId = championId * 1000 };
        }

        using var document = JsonDocument.Parse(trimmed);
        var root = document.RootElement;
        var selection = root.ValueKind == JsonValueKind.Object
            ? ParseSelection(root, 0)
            : null;
        return selection;
    }

    private async Task<string> ReadGameflowPhaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{_port}/lol-gameflow/v1/gameflow-phase");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicToken(_token!));
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            AppLog.InfoThrottled(
                "lcu-gameflow-phase-http",
                $"LCU 游戏流阶段接口：HTTP {(int)response.StatusCode} ({response.StatusCode}) port={_port}",
                TimeSpan.FromSeconds(5));
            if (!response.IsSuccessStatusCode) return "";
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body)) return "";
            string phase;
            try { phase = JsonSerializer.Deserialize<string>(body) ?? body.Trim('"'); }
            catch (JsonException) { phase = body.Trim('"'); }
            AppLog.InfoThrottled("lcu-gameflow-phase", $"LCU 游戏流阶段：{phase}", TimeSpan.FromSeconds(5));
            return phase;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ""; }
        catch (HttpRequestException) { return ""; }
    }

    private async Task<ChampionSelection?> ReadGameflowSelectionAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{_port}/lol-gameflow/v1/session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicToken(_token!));
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            AppLog.InfoThrottled(
                "lcu-gameflow-session-http",
                $"LCU 游戏流会话接口：HTTP {(int)response.StatusCode} ({response.StatusCode}) port={_port}",
                TimeSpan.FromSeconds(5));
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var currentSummonerId = await ReadCurrentSummonerIdAsync(cancellationToken);
            var selection = ParseGameflowSelection(root, currentSummonerId);
            var gameData = root.TryGetProperty("gameData", out var data) && data.ValueKind == JsonValueKind.Object
                ? data
                : default;
            var playerSelectionCount = CountArray(gameData, "playerChampionSelections");
            var teamOneCount = CountArray(gameData, "teamOne");
            var teamTwoCount = CountArray(gameData, "teamTwo");
            AppLog.InfoThrottled(
                "lcu-gameflow-session-result",
                $"LCU 游戏流会话解析：phase={selection.Phase} playerChampionSelections={playerSelectionCount} teamOne={teamOneCount} teamTwo={teamTwoCount} championId={selection.ResolvedChampionId} selectedSkinId={selection.SelectedSkinId} summonerId={currentSummonerId}",
                TimeSpan.FromSeconds(5));
            if (selection.ResolvedChampionId <= 0
                && IsLiveGameflowSelectionPhase(selection.Phase)
                && playerSelectionCount + teamOneCount + teamTwoCount > 0)
            {
                AppLog.InfoThrottled(
                    "lcu-gameflow-local-identity-miss",
                    $"LCU 游戏流候选已忽略：未匹配到当前用户 summonerId={currentSummonerId} phase={selection.Phase}",
                    TimeSpan.FromSeconds(5));
            }
            return selection;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    private static ChampionSelection ParseGameflowSelection(JsonElement root, long currentSummonerId)
    {
        var phase = GetString(root, "phase");
        if (!IsLiveGameflowSelectionPhase(phase))
            return new ChampionSelection { Available = true, Phase = phase };
        if (!root.TryGetProperty("gameData", out var gameData) || gameData.ValueKind != JsonValueKind.Object)
            return new ChampionSelection { Available = true, Phase = phase };

        JsonElement? local = null;
        foreach (var propertyName in new[] { "playerChampionSelections", "teamOne", "teamTwo" })
        {
            if (!gameData.TryGetProperty(propertyName, out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in entries.EnumerateArray())
            {
                if (GetInt(entry, "championId") <= 0) continue;
                var summonerId = GetLong(entry, "summonerId");
                if (GetBool(entry, "isLocalPlayer")
                    || (currentSummonerId > 0 && summonerId == currentSummonerId))
                    local = entry;
            }
        }

        // Gameflow can retain every player's previous selection. A sole
        // candidate is not proof that it belongs to this user, so only an
        // explicit local-player identity may drive UI synchronization.
        var selected = local;
        if (selected is not JsonElement value)
            return new ChampionSelection { Available = true, Phase = phase };

        var championId = GetInt(value, "championId");
        var selectedSkinId = GetInt(value, "selectedSkinId");
        if (selectedSkinId <= 0) selectedSkinId = GetInt(value, "skinId");
        if (selectedSkinId <= 0)
        {
            var skinIndex = GetInt(value, "selectedSkinIndex");
            if (skinIndex >= 0) selectedSkinId = championId * 1000 + skinIndex;
        }
        if (selectedSkinId / 1000 != championId) selectedSkinId = championId * 1000;
        return new ChampionSelection
        {
            Available = true,
            ChampionId = championId,
            SelectedSkinId = selectedSkinId,
            Phase = phase
        };
    }

    private async Task<long> ReadCurrentSummonerIdAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{_port}/lol-summoner/v1/current-summoner");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicToken(_token!));
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return 0;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return GetLong(document.RootElement, "summonerId");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return 0; }
        catch (HttpRequestException) { return 0; }
        catch (JsonException) { return 0; }
    }

    private async Task<bool> EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_port > 0 && !string.IsNullOrWhiteSpace(_token) && DateTime.UtcNow < _nextProbeUtc)
                return true;
            if (_port > 0 && !string.IsNullOrWhiteSpace(_token) && IsLeagueProcessAlive(_pid))
            {
                _nextProbeUtc = DateTime.UtcNow.AddSeconds(4);
                return true;
            }
            if (_port == 0 && DateTime.UtcNow < _nextProbeUtc)
                return false;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (_port > 0 && !string.IsNullOrWhiteSpace(_token) && DateTime.UtcNow < _nextProbeUtc)
                    return true;
                if (_port == 0 && DateTime.UtcNow < _nextProbeUtc)
                    return false;
            }

            // Process/WMI inspection is outside the UI thread, but a broken
            // WMI provider must not hold the WinForms poller forever. Reuse a
            // single in-flight refresh and impose a hard upper bound on the
            // caller; the worker may finish later and publish a valid token.
            Task<bool> refreshTask;
            lock (_gate)
            {
                if (_refreshTask is null || _refreshTask.IsCompleted)
                    _refreshTask = Task.Run(RefreshConnection);
                refreshTask = _refreshTask;
            }
            bool refreshed;
            try
            {
                refreshed = await refreshTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (TimeoutException)
            {
                AppLog.WarnThrottled("lcu-discovery-timeout", "LCU 进程探测超时，已跳过本轮轮询", TimeSpan.FromSeconds(10));
                refreshed = false;
            }
            if (!refreshed)
            {
                lock (_gate)
                {
                    if (_port == 0) _nextProbeUtc = DateTime.UtcNow.Add(FailedProbeBackoff);
                }
            }
            return refreshed;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private bool RefreshConnection()
    {
        // Prefer the lockfile when the client writes a complete one. Some
        // WeGame/Riot builds leave LeagueClient\lockfile empty, so retain the
        // command-line discovery used by the old desktop client as a fallback.
        foreach (var directory in ConfiguredClientDirectories().Concat(CandidateClientDirectories()))
        {
            if (!TryReadLockfile(directory, out var connection)) continue;
            SetConnection(connection.Port, connection.Pid, connection.Token);
            return true;
        }

        if (TryReadProcessCommandLine(out var processConnection))
        {
            SetConnection(processConnection.Port, processConnection.Pid, processConnection.Token);
            return true;
        }

        AppLog.WarnThrottled("lcu-not-found", "未找到有效 LCU 锁文件或客户端进程参数，轮询暂不可用", TimeSpan.FromSeconds(10));
        return false;
    }

    private static bool TryReadProcessCommandLine(out LockfileConnection connection)
    {
        connection = default;
        foreach (var processName in ClientProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (TryReadProcessCommandLineNative(process.Id, out var commandLine)
                        && TryParseProcessCommandLine(commandLine, process.Id, out connection))
                    {
                        AppLog.InfoThrottled("lcu-discovery-native", $"LCU 参数来源：进程查询 pid={connection.Pid} port={connection.Port}", TimeSpan.FromSeconds(10));
                        return true;
                    }
                }
                finally { process.Dispose(); }
            }
        }

        // Keep external fallbacks before the raw WMI provider. On some clean
        // Windows installations ManagementObjectSearcher.Get() can block even
        // with EnumerationOptions.Timeout, preventing the later fallbacks from
        // ever running and permanently stalling the poller.
        if (TryReadProcessCommandLineViaPowerShell(out connection))
        {
            AppLog.InfoThrottled("lcu-discovery-powershell", $"LCU 参数来源：PowerShell pid={connection.Pid} port={connection.Port}", TimeSpan.FromSeconds(10));
            return true;
        }
        if (TryReadProcessCommandLineViaWmic(out connection))
        {
            AppLog.InfoThrottled("lcu-discovery-wmic", $"LCU 参数来源：内置 wmic.exe pid={connection.Pid} port={connection.Port}", TimeSpan.FromSeconds(10));
            return true;
        }

        // Last resort for systems where the two bounded command-line tools are
        // unavailable. This call is itself bounded so a broken WMI provider
        // cannot hold the shared refresh task forever.
        var wmiTask = Task.Run(TryReadProcessCommandLineViaWmi);
        try
        {
            if (wmiTask.Wait(TimeSpan.FromSeconds(1)) && wmiTask.Result is { } wmiConnection)
            {
                connection = wmiConnection;
                AppLog.InfoThrottled("lcu-discovery-wmi", $"LCU 参数来源：WMI pid={connection.Pid} port={connection.Port}", TimeSpan.FromSeconds(10));
                return true;
            }
        }
        catch (AggregateException) { }
        AppLog.WarnThrottled(
            "lcu-discovery-failed",
            "LCU 参数探测失败：进程查询、PowerShell、内置 wmic.exe 和 WMI 均未取得端口/令牌；若 LeagueClient 以管理员权限运行，请以相同权限启动本程序",
            TimeSpan.FromSeconds(15));
        return false;
    }

    private static LockfileConnection? TryReadProcessCommandLineViaWmi()
    {
        try
        {
            var options = new System.Management.EnumerationOptions
            {
                ReturnImmediately = true,
                Timeout = TimeSpan.FromSeconds(1)
            };
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new SelectQuery("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='LeagueClientUx.exe' OR Name='LeagueClient.exe' OR Name='LeagueClientUxRender.exe'"),
                options);
            using var processes = searcher.Get();
            foreach (ManagementObject process in processes)
            {
                var commandLine = process["CommandLine"] as string;
                var processId = int.TryParse(process["ProcessId"]?.ToString(), out var parsedPid) ? parsedPid : 0;
                if (TryParseProcessCommandLine(commandLine, processId, out var connection)) return connection;
            }
        }
        catch (ManagementException) { }
        catch (COMException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static bool TryParseProcessCommandLine(string? commandLine, int fallbackPid, out LockfileConnection connection)
    {
        connection = default;
        var value = commandLine ?? "";
        var portMatch = Regex.Match(value, @"--app-port\s*(?:=|\s+)\s*(?<value>\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var tokenMatch = Regex.Match(value, @"--remoting-auth-token\s*(?:=|\s+)\s*(?:""(?<quoted>[^""]+)""|'(?<single>[^']+)'|(?<value>[^\s]+))", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var pidMatch = Regex.Match(value, @"--app-pid\s*(?:=|\s+)\s*(?<value>\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var token = tokenMatch.Groups["quoted"].Success
            ? tokenMatch.Groups["quoted"].Value
            : tokenMatch.Groups["single"].Success
                ? tokenMatch.Groups["single"].Value
                : tokenMatch.Groups["value"].Value.Trim('"');
        var pid = int.TryParse(pidMatch.Groups["value"].Value, out var appPid) ? appPid : fallbackPid;
        if (!int.TryParse(portMatch.Groups["value"].Value, out var port)
            || port <= 0 || pid <= 0 || string.IsNullOrWhiteSpace(token)
            || !IsLeagueProcessAlive(pid)) return false;
        connection = new LockfileConnection(pid, port, token);
        return true;
    }

    private static bool TryReadProcessCommandLineViaPowerShell(out LockfileConnection connection)
    {
        connection = default;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                    "Get-CimInstance Win32_Process -Filter \"Name='LeagueClientUx.exe' OR Name='LeagueClient.exe' OR Name='LeagueClientUxRender.exe'\" | ForEach-Object { \"$($_.ProcessId)|$($_.CommandLine)\" }" }
            };
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(1500))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                try { process.WaitForExit(500); } catch (InvalidOperationException) { }
                return false;
            }
            var output = outputTask.GetAwaiter().GetResult();
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf('|');
                if (separator <= 0 || !int.TryParse(line[..separator].Trim(), out var pid)) continue;
                if (TryParseProcessCommandLine(line[(separator + 1)..], pid, out connection)) return true;
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (IOException) { }
        return false;
    }

    private static bool TryReadProcessCommandLineViaWmic(out LockfileConnection connection)
    {
        connection = default;
        try
        {
            var command = AppPaths.FindBundledFile(Path.Combine("Engine", "wmic.exe")) ?? "wmic.exe";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("process");
            process.StartInfo.ArgumentList.Add("where");
            process.StartInfo.ArgumentList.Add("name='LeagueClientUx.exe' OR name='LeagueClient.exe' OR name='LeagueClientUxRender.exe'");
            process.StartInfo.ArgumentList.Add("get");
            process.StartInfo.ArgumentList.Add("ProcessId,CommandLine");
            process.StartInfo.ArgumentList.Add("/value");
            if (!process.Start()) return false;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(2500))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                try { process.WaitForExit(500); } catch (InvalidOperationException) { }
                return false;
            }
            var output = outputTask.GetAwaiter().GetResult();
            var fallbackPid = FindLiveClientPid();
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = line.Trim();
                if (normalized.StartsWith("CommandLine=", StringComparison.OrdinalIgnoreCase))
                    normalized = normalized["CommandLine=".Length..];
                if (TryParseProcessCommandLine(normalized, fallbackPid, out connection)) return true;
            }
            if (TryParseProcessCommandLine(output, fallbackPid, out connection)) return true;
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (IOException) { }
        return false;
    }

    private static int FindLiveClientPid()
    {
        foreach (var processName in ClientProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (!process.HasExited) return process.Id;
                }
                catch (InvalidOperationException) { }
                finally { process.Dispose(); }
            }
        }
        return 0;
    }

    private void SetConnection(int port, int pid, string token)
    {
        bool changed;
        lock (_gate)
        {
            changed = _port != port || _pid != pid || !string.Equals(_token, token, StringComparison.Ordinal);
            _port = port;
            _pid = pid;
            _token = token;
            _nextProbeUtc = DateTime.UtcNow.AddSeconds(4);
        }
        if (changed) AppLog.Info($"LCU 连接已建立：port={port} pid={pid}");
    }

    internal static string? TryFindLeagueGameDirectoryFromLockfile()
    {
        foreach (var directory in CandidateClientDirectories())
        {
            if (!TryReadLockfile(directory, out _)) continue;
            var installRoot = Directory.GetParent(directory)?.FullName;
            var gameDirectory = installRoot is null ? null : Path.Combine(installRoot, "Game");
            if (IsGameDirectory(gameDirectory)) return Path.GetFullPath(gameDirectory!);
        }
        return null;
    }

    private static bool TryReadLockfile(string directory, out LockfileConnection connection)
    {
        connection = default;
        foreach (var name in LockfileNames)
        {
            var path = Path.Combine(directory, name);
            try
            {
                if (!File.Exists(path)) continue;
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var raw = reader.ReadToEnd().Trim();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    AppLog.WarnThrottled($"lcu-lock-empty-{path}", $"LCU 锁文件为空：{path}", TimeSpan.FromSeconds(10));
                    continue;
                }
                var fields = raw.Split(':', 5);
                if (fields.Length < 5
                    || !int.TryParse(fields[1], out var pid)
                    || !int.TryParse(fields[2], out var port)
                    || pid <= 0 || port <= 0
                    || string.IsNullOrWhiteSpace(fields[3])
                    || !IsLeagueProcessAlive(pid)) continue;
                connection = new LockfileConnection(pid, port, fields[3]);
                return true;
            }
            catch (IOException ex)
            {
                AppLog.WarnThrottled($"lcu-lock-read-{path}", $"读取 LCU 锁文件失败：{path}；{ex.Message}", TimeSpan.FromSeconds(10));
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLog.WarnThrottled($"lcu-lock-permission-{path}", $"读取 LCU 锁文件无权限：{path}；{ex.Message}", TimeSpan.FromSeconds(10));
            }
        }
        return false;
    }

    private static bool IsLeagueProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            return ClientProcessNames.Any(name => process.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static IReadOnlyList<string> CandidateClientDirectories()
    {
        var directories = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                var full = Path.GetFullPath(value);
                if (!directories.Contains(full, StringComparer.OrdinalIgnoreCase)) directories.Add(full);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }

        foreach (var processName in ClientProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try { Add(Path.GetDirectoryName(process.MainModule?.FileName)); }
                catch { }
                finally { process.Dispose(); }
            }
        }

        foreach (var driveRoot in FixedDriveRoots())
        {
            Add(Path.Combine(driveRoot, "wegameapps", "英雄联盟", "LeagueClient"));
            Add(Path.Combine(driveRoot, "Program Files", "腾讯游戏", "英雄联盟", "LeagueClient"));
            Add(Path.Combine(driveRoot, "Program Files (x86)", "腾讯游戏", "英雄联盟", "LeagueClient"));
            Add(Path.Combine(driveRoot, "Riot Games", "League of Legends", "LeagueClient"));
        }
        return directories;
    }

    private IReadOnlyList<string> ConfiguredClientDirectories()
    {
        if (string.IsNullOrWhiteSpace(_configuredGameDirectory)) return [];
        var game = _configuredGameDirectory;
        var directories = new List<string>();
        void Add(string value)
        {
            try
            {
                var full = Path.GetFullPath(value);
                if (!directories.Contains(full, StringComparer.OrdinalIgnoreCase)) directories.Add(full);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }

        var gameRoot = Directory.GetParent(game)?.FullName;
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            Add(Path.Combine(gameRoot, "LeagueClient"));
            Add(gameRoot);
        }
        Add(game);
        return directories;
    }

    private static IReadOnlyList<string> FixedDriveRoots()
    {
        var roots = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                        roots.Add(drive.RootDirectory.FullName);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch { }
        return roots;
    }

    private static bool IsGameDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "League of Legends.exe"));

    private readonly record struct LockfileConnection(int Pid, int Port, string Token);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;
    private const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        nint processHandle,
        int processInformationClass,
        nint processInformation,
        int processInformationLength,
        out int returnLength);

    private static bool TryReadProcessCommandLineNative(int pid, out string commandLine)
    {
        commandLine = "";
        // ProcessCommandLineInformation only needs query-limited access on
        // current Windows builds. Requiring PROCESS_VM_READ first makes an
        // unelevated portable app fail against a client launched by WeGame at
        // a different integrity level.
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == 0)
            handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, pid);
        if (handle == 0) return false;
        nint buffer = 0;
        try
        {
            _ = NtQueryInformationProcess(handle, ProcessCommandLineInformation, 0, 0, out var length);
            if (length <= 0) return false;
            buffer = Marshal.AllocHGlobal(length);
            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _) != 0)
                return false;
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            if (value.Buffer == 0 || value.Length == 0) return false;
            commandLine = Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? "";
            return !string.IsNullOrWhiteSpace(commandLine);
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (AccessViolationException) { return false; }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            CloseHandle(handle);
        }
    }

    private static ChampionSelection ParseSelection(JsonElement session, long currentSummonerId)
    {
        var hasLocalCellId = TryGetInt(session, "localPlayerCellId", out var localCellId) && localCellId >= 0;
        var championId = GetInt(session, "championId");
        var championPickIntent = GetInt(session, "championPickIntent");
        var selectedSkinId = 0;

        if (session.TryGetProperty("myTeam", out var team) && team.ValueKind == JsonValueKind.Array)
        {
            foreach (var player in team.EnumerateArray())
            {
                var playerCellId = GetInt(player, "cellId");
                var playerSummonerId = GetLong(player, "summonerId");
                var isLocal = GetBool(player, "isLocalPlayer")
                    || (hasLocalCellId && playerCellId == localCellId)
                    || (currentSummonerId > 0 && playerSummonerId == currentSummonerId);
                if (!isLocal) continue;
                championId = GetInt(player, "championId") > 0 ? GetInt(player, "championId") : championId;
                championPickIntent = GetInt(player, "championPickIntent") > 0
                    ? GetInt(player, "championPickIntent")
                    : championPickIntent;
                selectedSkinId = ReadSkinId(player, championId);
                break;
            }
        }

        if (session.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
        {
            foreach (var round in actions.EnumerateArray())
            {
                if (round.ValueKind != JsonValueKind.Array) continue;
                foreach (var action in round.EnumerateArray())
                {
                    if (!string.Equals(GetString(action, "type"), "pick", StringComparison.OrdinalIgnoreCase)) continue;
                    var actorCellId = GetInt(action, "actorCellId");
                    // Cell 0 is a valid local slot. If the response omitted
                    // localPlayerCellId entirely, actions cannot be tied to
                    // the current player and must not override myTeam data.
                    if (!hasLocalCellId || actorCellId != localCellId) continue;
                    championId = GetInt(action, "championId") > 0 ? GetInt(action, "championId") : championId;
                    championPickIntent = GetInt(action, "championPickIntent") > 0
                        ? GetInt(action, "championPickIntent")
                        : championPickIntent;
                    var actionSkin = ReadSkinId(action, championId);
                    if (selectedSkinId <= 0 && actionSkin > 0) selectedSkinId = actionSkin;
                }
            }
        }

        if (championId <= 0) championId = championPickIntent;
        if (championId > 0 && selectedSkinId / 1000 != championId) selectedSkinId = championId * 1000;
        return new ChampionSelection
        {
            Available = true,
            ChampionId = championId,
            SelectedSkinId = selectedSkinId,
            Phase = GetString(session, "phase")
        };
    }

    private static int GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : 0;
    }

    private static bool TryGetInt(JsonElement element, string name, out int result)
    {
        result = 0;
        if (!element.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt32(out result);
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out result);
    }

    private static bool IsLiveGameflowSelectionPhase(string phase) =>
        phase.Equals("ChampSelect", StringComparison.OrdinalIgnoreCase)
        || phase.Equals("GameStart", StringComparison.OrdinalIgnoreCase)
        || phase.Equals("InProgress", StringComparison.OrdinalIgnoreCase)
        || phase.Equals("Reconnect", StringComparison.OrdinalIgnoreCase);

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) ? number : 0;
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;

    private static int ReadSkinId(JsonElement element, int championId)
    {
        var selectedSkinId = GetInt(element, "selectedSkinId");
        if (selectedSkinId <= 0) selectedSkinId = GetInt(element, "skinId");
        if (selectedSkinId <= 0)
        {
            var selectedSkinIndex = GetInt(element, "selectedSkinIndex");
            if (selectedSkinIndex >= 0 && championId > 0)
                selectedSkinId = championId * 1000 + selectedSkinIndex;
        }
        return selectedSkinId;
    }

    private static int CountArray(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static string BasicToken(string token) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{token}"));

    private void InvalidateConnection()
    {
        lock (_gate)
        {
            _port = 0;
            _pid = 0;
            _token = null;
            _nextProbeUtc = DateTime.UtcNow.AddSeconds(1);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _refreshGate.Dispose();
    }
}
