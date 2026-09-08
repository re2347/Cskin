namespace CskinNative.Services;

public sealed class AuthorizationService
{
    private const long ClockToleranceSeconds = 5 * 60;
    private readonly LeaseStore _store = new();
    private readonly RememberedLicenseStore _remembered = new();
    private readonly string _clientVersion;

    public AuthorizationService(string? baseUrl = null, string? clientVersion = null)
    {
        BaseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? (AuthorizationClient.IsSupabaseOptIn ? AuthorizationClient.SupabaseAuthBaseUrl : AuthorizationClient.DefaultBaseUrl)
            : baseUrl.TrimEnd('/');
        _clientVersion = string.IsNullOrWhiteSpace(clientVersion) ? typeof(AuthorizationService).Assembly.GetName().Version?.ToString() ?? "0.0.0" : clientVersion;
    }

    public string BaseUrl { get; }

    public static bool IsEnabled
    {
        get
        {
#if CSKIN_AUTH_REQUIRED
            return true;
#else
            // Debug builds remain opt-in so local development is not blocked.
            return string.Equals(Environment.GetEnvironmentVariable("CSKIN_AUTH_ENABLED"), "1", StringComparison.OrdinalIgnoreCase)
                || File.Exists(AppPaths.AuthorizationEnabledMarker);
#endif
        }
    }

    public async Task<AuthorizationAttempt> RestoreAsync(CancellationToken cancellationToken = default)
    {
        var stored = _store.Load();
        if (stored is null || string.IsNullOrWhiteSpace(stored.DeviceId) || string.IsNullOrWhiteSpace(stored.LeaseToken))
            return AuthorizationAttempt.Failure("尚未激活，请输入激活码");

        using var device = DeviceIdentity.LoadOrCreate();
        var leaseDeviceId = string.Equals(stored.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase)
            ? device.DeviceId
            : string.Equals(stored.DeviceId, device.LegacyDeviceId, StringComparison.OrdinalIgnoreCase)
                ? stored.DeviceId
                : null;
        if (leaseDeviceId is null)
        {
            _store.Clear();
            return AuthorizationAttempt.Failure("设备信息已变化，请重新激活");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var clockMovedBack = stored.ServerTime > 0 && now + ClockToleranceSeconds < stored.ServerTime;
        using var client = CreateClient();
        var online = await client.VerifyAsync(stored, device, _clientVersion, leaseDeviceId, cancellationToken);
        if (online.Succeeded && online.Value is not null)
        {
            var refreshed = NormalizeLease(online.Value, leaseDeviceId, now);
            _store.Save(refreshed);
            return AuthorizationAttempt.Success(refreshed, offline: false);
        }

        if (!clockMovedBack && stored.GraceUntil > now && IsTransient(online.ErrorCode))
            return AuthorizationAttempt.Success(stored, offline: true);

        if (online.ErrorCode is "LICENSE_REVOKED" or "LICENSE_EXPIRED" or "INVALID_LEASE" or "LEASE_EXPIRED")
        {
            _store.Clear();
            if (online.ErrorCode is "LICENSE_REVOKED" or "LICENSE_EXPIRED") _remembered.Clear();
        }
        if (online.ErrorCode == "INVALID_SIGNATURE")
        {
            _store.Clear();
            return AuthorizationAttempt.Failure("设备密钥已更新，请重新激活", "INVALID_SIGNATURE");
        }
        return AuthorizationAttempt.Failure(online.ErrorMessage.Length > 0 ? online.ErrorMessage : "授权验证失败，请重新联网验证", online.ErrorCode);
    }

    public async Task<AuthorizationAttempt> ActivateAsync(
        string code,
        bool rememberCode = false,
        bool allowLegacyDeviceIdFallback = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return AuthorizationAttempt.Failure("请输入激活码");
        using var device = DeviceIdentity.LoadOrCreate();
        using var client = CreateClient();
        var normalizedCode = code.Trim();
        var activationDeviceId = device.DeviceId;
        var result = await client.ActivateAsync(device, normalizedCode, _clientVersion, activationDeviceId, cancellationToken);
        // Before motherboard binding was introduced, existing leases were
        // bound to the DPAPI-backed public-key hash. Only the protected
        // remembered-key recovery path may retry with that legacy ID; this is
        // not used for ordinary manual activations on a different machine.
        if (!result.Succeeded && allowLegacyDeviceIdFallback
            && !string.Equals(device.DeviceId, device.LegacyDeviceId, StringComparison.OrdinalIgnoreCase)
            && result.ErrorCode == "LICENSE_ALREADY_BOUND")
        {
            activationDeviceId = device.LegacyDeviceId;
            result = await client.ActivateAsync(device, normalizedCode, _clientVersion, activationDeviceId, cancellationToken);
            if (result.Succeeded)
            {
                var migrated = await client.ActivateAsync(device, normalizedCode, _clientVersion, device.DeviceId, cancellationToken);
                if (migrated.Succeeded)
                {
                    activationDeviceId = device.DeviceId;
                    result = migrated;
                }
            }
        }
        if (!result.Succeeded || result.Value is null)
            return AuthorizationAttempt.Failure(result.ErrorMessage.Length > 0 ? result.ErrorMessage : "激活失败，请稍后重试", result.ErrorCode);

        var lease = NormalizeLease(result.Value, activationDeviceId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (!_store.Save(lease)) return AuthorizationAttempt.Failure("授权信息无法保存，请检查用户目录权限");
        if (rememberCode) _remembered.Save(code.Trim());
        else _remembered.Clear();
        return AuthorizationAttempt.Success(lease, offline: false);
    }

    public string? LoadRememberedCode() => _remembered.Load();

    public void ClearCurrentLease() => _store.Clear();

    public void ForgetRememberedCode() => _remembered.Clear();

    private AuthorizationClient CreateClient() =>
        string.Equals(BaseUrl, AuthorizationClient.DefaultBaseUrl, StringComparison.OrdinalIgnoreCase)
            || string.Equals(BaseUrl, AuthorizationClient.SupabaseAuthBaseUrl, StringComparison.OrdinalIgnoreCase)
            ? new AuthorizationClient()
            : new AuthorizationClient(BaseUrl);

    private static AuthorizationLease NormalizeLease(AuthorizationLease lease, string deviceId, long now)
    {
        lease.DeviceId = deviceId;
        lease.SavedAtUtc = now;
        return lease;
    }

    private static bool IsTransient(string code) => code is "AUTH_UNREACHABLE" or "AUTH_TIMEOUT" or "INVALID_RESPONSE" or "AUTH_REQUEST_FAILED";
}

public sealed record AuthorizationAttempt(bool Allowed, bool Offline, AuthorizationLease? Lease, string Message)
{
    public string ErrorCode { get; init; } = "";
    public static AuthorizationAttempt Success(AuthorizationLease lease, bool offline) => new(true, offline, lease, offline ? "已使用短期离线授权" : "授权验证成功") { ErrorCode = offline ? "OFFLINE_GRACE" : "OK" };
    public static AuthorizationAttempt Failure(string message, string errorCode = "AUTH_FAILED") => new(false, false, null, message) { ErrorCode = errorCode };
}
