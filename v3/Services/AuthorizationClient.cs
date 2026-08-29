using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CskinNative.Services;

public sealed class AuthorizationClient : IDisposable
{
    // The custom domain is the stable public endpoint. Keep the workers.dev
    // hostname as a second project-owned endpoint until a mainland relay is
    // deployed; both endpoints use the same signed API and D1 state.
    public const string DefaultBaseUrl = "https://license.re2347.ccwu.cc";
    // Supabase is the primary provider. Cloudflare remains the automatic
    // failover path for transient failures and replica discrepancies.
    public const string SupabaseProjectUrl = "https://kzqgphxrghdclkwneqyn.supabase.co";
    public const string SupabaseAuthBaseUrl = SupabaseProjectUrl + "/functions/v1/auth";
    public const string SupabaseProviderName = "supabase";
    // Keep only endpoints owned by the project here. A mainland relay may be
    // added after it is deployed and health-checked; never accept an arbitrary
    // URL from a user or a downloaded config file.
    public static IReadOnlyList<string> DefaultBaseUrls { get; } =
    [
        DefaultBaseUrl,
        "https://cskin-license-staging.2469416170.workers.dev",
    ];

    public static IReadOnlyList<string> SupabaseFirstBaseUrls { get; } =
    [
        SupabaseAuthBaseUrl,
        DefaultBaseUrl,
        "https://cskin-license-staging.2469416170.workers.dev",
    ];

    // Set CSKIN_AUTH_PROVIDER=cloudflare only for an emergency operational
    // override. Normal releases always prefer Supabase first.
    public static bool IsSupabaseOptIn =>
        !string.Equals(Environment.GetEnvironmentVariable("CSKIN_AUTH_PROVIDER"), "cloudflare", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    // Keep endpoint preference scoped to this client instance. Supabase-first
    // and Cloudflare-first clients use different endpoint lists, so a shared
    // static index could make one provider start at the wrong endpoint.
    private int _preferredEndpoint;
    private readonly HttpClient _http;
    private readonly IReadOnlyList<Uri> _endpoints;

    public AuthorizationClient(string? baseUrl = null)
    {
        var endpointValues = string.IsNullOrWhiteSpace(baseUrl)
            ? IsSupabaseOptIn ? SupabaseFirstBaseUrls : DefaultBaseUrls
            : [baseUrl];
        _endpoints = endpointValues
            .Select(ParseEndpoint)
            .Distinct()
            .ToArray();
        if (_endpoints.Count == 0) throw new ArgumentException("没有可用的授权服务端点", nameof(baseUrl));

        _http = new HttpClient { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CskinNative/1.0");
    }

    public Task<ApiCallResult<AuthorizationLease>> ActivateAsync(DeviceIdentity device, string code, string clientVersion, string? deviceId = null, CancellationToken cancellationToken = default)
    {
        var resolvedDeviceId = deviceId ?? device.DeviceId;
        var normalizedCode = code.Trim();
        return PostWithFailoverAsync<ActivateRequest, AuthorizationLease>(
            "v1/activate",
            () =>
            {
                var requestId = Guid.NewGuid().ToString("N");
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var payload = SigningPayload.Activate(requestId, timestamp, resolvedDeviceId, device.PublicKeySpki, clientVersion, normalizedCode, "2");
                return new ActivateRequest(normalizedCode, resolvedDeviceId, device.PublicKeySpki, clientVersion, "2", requestId, timestamp, device.Sign(payload));
            },
            cancellationToken);
    }

    public Task<ApiCallResult<AuthorizationLease>> VerifyAsync(AuthorizationLease lease, DeviceIdentity device, string clientVersion, string? deviceId = null, CancellationToken cancellationToken = default)
    {
        var resolvedDeviceId = deviceId ?? device.DeviceId;
        return PostWithFailoverAsync<VerifyRequest, AuthorizationLease>(
            "v1/verify",
            () =>
            {
                var requestId = Guid.NewGuid().ToString("N");
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var payload = SigningPayload.Verify(requestId, timestamp, resolvedDeviceId, lease.LicenseId, lease.LeaseId, lease.LeaseToken, clientVersion);
                return new VerifyRequest(lease.LicenseId, resolvedDeviceId, lease.LeaseId, lease.LeaseToken, requestId, clientVersion, timestamp, device.Sign(payload));
            },
            cancellationToken);
    }

    private async Task<ApiCallResult<TResponse>> PostWithFailoverAsync<TRequest, TResponse>(
        string path,
        Func<TRequest> requestFactory,
        CancellationToken cancellationToken)
    {
        ApiCallResult<TResponse>? last = null;
        var start = Math.Abs(Volatile.Read(ref _preferredEndpoint));
        for (var attempt = 0; attempt < _endpoints.Count; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpointIndex = (start + attempt) % _endpoints.Count;
            var result = await PostAsync<TRequest, TResponse>(
                _endpoints[endpointIndex],
                path,
                requestFactory(),
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                Interlocked.Exchange(ref _preferredEndpoint, endpointIndex);
                return result;
            }

            last = result;
            if (attempt + 1 >= _endpoints.Count || !ShouldTryNextEndpoint(result, _endpoints[endpointIndex])) break;
            await Task.Delay(TimeSpan.FromMilliseconds(250 + (attempt * 500)), cancellationToken).ConfigureAwait(false);
        }

        return last ?? ApiCallResult<TResponse>.Failure("AUTH_UNREACHABLE", "无法连接授权服务", 0);
    }

    private async Task<ApiCallResult<TResponse>> PostAsync<TRequest, TResponse>(
        Uri endpoint,
        string path,
        TRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(new Uri(endpoint, path), request, JsonOptions, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var value = JsonSerializer.Deserialize<TResponse>(body, JsonOptions);
                return value is null
                    ? ApiCallResult<TResponse>.Failure("INVALID_RESPONSE", "授权服务返回的数据无效", response.StatusCode)
                    : ApiCallResult<TResponse>.Success(value, response.StatusCode);
            }

            var error = JsonSerializer.Deserialize<ErrorEnvelope>(body, JsonOptions);
            return ApiCallResult<TResponse>.Failure(error?.Error?.Code ?? "AUTH_REQUEST_FAILED", error?.Error?.Message ?? "授权请求失败", response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiCallResult<TResponse>.Failure("AUTH_TIMEOUT", "授权服务响应超时", HttpStatusCode.RequestTimeout);
        }
        catch (HttpRequestException)
        {
            return ApiCallResult<TResponse>.Failure("AUTH_UNREACHABLE", "无法连接授权服务", 0);
        }
        catch (JsonException)
        {
            return ApiCallResult<TResponse>.Failure("INVALID_RESPONSE", "授权服务返回的数据无效", 0);
        }
    }

    private static bool ShouldTryNextEndpoint<T>(ApiCallResult<T> result, Uri endpoint)
    {
        var statusCode = (int)result.StatusCode;
        if (result.ErrorCode is "LICENSE_NOT_FOUND" or "INVALID_LEASE" or "LICENSE_ALREADY_BOUND" or "DEVICE_KEY_CHANGED")
            return endpoint.Host.EndsWith("supabase.co", StringComparison.OrdinalIgnoreCase);
        return result.ErrorCode is "AUTH_TIMEOUT" or "AUTH_UNREACHABLE" or "INVALID_RESPONSE"
            || statusCode is 408 or 429
            || statusCode >= 500;
    }

    private static Uri ParseEndpoint(string value)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrWhiteSpace(endpoint.Query)
            || !string.IsNullOrWhiteSpace(endpoint.Fragment))
        {
            throw new ArgumentException($"授权服务端点必须是 HTTPS URL：{value}", nameof(value));
        }
        return endpoint;
    }

    public void Dispose() => _http.Dispose();

    private sealed record ActivateRequest(
        string Code,
        string DeviceId,
        string DevicePublicKey,
        string ClientVersion,
        string ProtocolVersion,
        string RequestId,
        long SignatureTimestamp,
        string Signature);

    private sealed record VerifyRequest(
        string LicenseId,
        string DeviceId,
        string LeaseId,
        string LeaseToken,
        string RequestId,
        string ClientVersion,
        long SignatureTimestamp,
        string Signature);

    private static class SigningPayload
    {
        public static string Activate(string requestId, long timestamp, string deviceId, string publicKey, string clientVersion, string code, string protocolVersion) =>
            string.Join("\n", "cskin-auth-v2", "POST", "/v1/activate", requestId, timestamp, deviceId, publicKey, clientVersion, protocolVersion, code);

        public static string Verify(string requestId, long timestamp, string deviceId, string licenseId, string leaseId, string leaseToken, string clientVersion) =>
            string.Join("\n", "cskin-auth-v2", "POST", "/v1/verify", requestId, timestamp, deviceId, licenseId, leaseId, leaseToken, clientVersion);
    }

    private sealed class ErrorEnvelope
    {
        public ApiError? Error { get; set; }
    }

    private sealed class ApiError
    {
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
    }
}

public sealed class ApiCallResult<T>
{
    private ApiCallResult(bool succeeded, T? value, string code, string message, HttpStatusCode statusCode)
    {
        Succeeded = succeeded;
        Value = value;
        ErrorCode = code;
        ErrorMessage = message;
        StatusCode = statusCode;
    }

    public bool Succeeded { get; }
    public T? Value { get; }
    public string ErrorCode { get; }
    public string ErrorMessage { get; }
    public HttpStatusCode StatusCode { get; }

    public static ApiCallResult<T> Success(T value, HttpStatusCode statusCode) => new(true, value, "", "", statusCode);
    public static ApiCallResult<T> Failure(string code, string message, HttpStatusCode statusCode) => new(false, default, code, message, statusCode);
}
