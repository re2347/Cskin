using System.Net;

namespace CskinNative.Services;

public static class ResourceAuthorizationPolicy
{
    public static bool ShouldTryNext(HttpStatusCode statusCode, string? errorCode)
    {
        var status = (int)statusCode;
        if (status is 401 or 403)
        {
            // Resource gateways are independently deployed. A lease rejection
            // on one gateway may be caused by a stale replica, so probe the
            // next owned gateway once. Permanent license failures are not
            // recoverable by changing gateways.
            return errorCode is null or ""
                or "SKIN_AUTH_REQUIRED"
                or "SKIN_AUTH_INVALID";
        }

        return status is 408 or 429 || status >= 500;
    }

    public static string Describe(HttpStatusCode statusCode, string? errorCode, string? errorMessage)
    {
        if (string.Equals(errorCode, "SKIN_AUTH_INVALID", StringComparison.OrdinalIgnoreCase))
            return "资源授权已失效，请重新验证激活码";
        if (string.Equals(errorCode, "SKIN_AUTH_REQUIRED", StringComparison.OrdinalIgnoreCase))
            return "资源服务未收到有效授权，请重新验证激活码";
        if (string.Equals(errorCode, "LICENSE_EXPIRED", StringComparison.OrdinalIgnoreCase))
            return "授权已过期，请重新激活";
        if (string.Equals(errorCode, "LICENSE_REVOKED", StringComparison.OrdinalIgnoreCase))
            return "授权已被撤销，请联系管理员";
        if (string.Equals(errorCode, "LEASE_EXPIRED", StringComparison.OrdinalIgnoreCase))
            return "本地授权租约已过期，请重新验证激活码";
        if (!string.IsNullOrWhiteSpace(errorMessage))
            return errorMessage.Trim();
        return $"资源服务拒绝授权（HTTP {(int)statusCode}）";
    }
}
