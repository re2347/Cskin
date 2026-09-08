using System.Text.Json;

namespace CskinNative.Services;

public sealed class UpdateClient : IDisposable
{
    public static IReadOnlyList<string> PublicEndpoints { get; } =
    [
        "https://kzqgphxrghdclkwneqyn.supabase.co/functions/v1/auth",
        "https://license.re2347.ccwu.cc",
        "https://cskin-license-staging.2469416170.workers.dev",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(TimeSpan.FromSeconds(8));
        var errors = new List<string>();

        foreach (var endpoint in PublicEndpoints)
        {
            overall.Token.ThrowIfCancellationRequested();
            try
            {
                var baseUri = new Uri(endpoint.TrimEnd('/') + "/", UriKind.Absolute);
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "v1/client/version"));
                request.Headers.UserAgent.ParseAdd("PortableCskin/0.4.0");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, overall.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(overall.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{baseUri.Host}: HTTP {(int)response.StatusCode}");
                    continue;
                }

                var manifest = JsonSerializer.Deserialize<UpdateManifest>(body, JsonOptions);
                var decision = UpdatePolicy.Evaluate(currentVersion, manifest);
                if (decision.Status == UpdateGateStatus.Unavailable)
                {
                    errors.Add($"{baseUri.Host}: {decision.Message}");
                    continue;
                }

                return new UpdateCheckResult(decision, manifest, baseUri.Host, "");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                errors.Add($"{endpoint}: 超时");
                if (overall.IsCancellationRequested) break;
            }
            catch (HttpRequestException ex)
            {
                errors.Add($"{endpoint}: {ex.Message}");
            }
            catch (JsonException ex)
            {
                errors.Add($"{endpoint}: 返回数据无效（{ex.Message}）");
            }
            catch (UriFormatException ex)
            {
                errors.Add($"{endpoint}: URL 无效（{ex.Message}）");
            }
        }

        return UpdateCheckResult.Unavailable(errors.Count == 0 ? "版本服务暂时不可用" : "版本检查失败：" + string.Join("；", errors));
    }

    public void Dispose() => _http.Dispose();
}
