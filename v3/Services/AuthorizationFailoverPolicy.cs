using System.Net;

namespace CskinNative.Services;

public static class AuthorizationFailoverPolicy
{
    public static bool ShouldTryNext(HttpStatusCode statusCode, string? errorCode, bool transportFailure)
    {
        if (transportFailure || (int)statusCode == 0) return true;
        if (IsQuotaError(errorCode)) return true;
        if ((int)statusCode < 500 && IsBusinessError(errorCode)) return false;

        var code = (int)statusCode;
        return code is 408 or 429 || code >= 500;
    }

    private static bool IsQuotaError(string? errorCode) => errorCode is
        "QUOTA_EXCEEDED" or
        "RATE_LIMITED" or
        "SUPABASE_QUOTA_EXCEEDED" or
        "SUPABASE_RATE_LIMITED";

    private static bool IsBusinessError(string? errorCode) => errorCode is
        "LICENSE_NOT_FOUND" or
        "INVALID_LEASE" or
        "LICENSE_ALREADY_BOUND" or
        "DEVICE_KEY_CHANGED" or
        "LICENSE_REVOKED" or
        "LICENSE_EXPIRED" or
        "LEASE_EXPIRED" or
        "INVALID_SIGNATURE" or
        "INVALID_REQUEST" or
        "INVALID_CODE" or
        "REPLAYED_REQUEST" or
        "SELF_UNBIND_DAILY_LIMIT";
}
