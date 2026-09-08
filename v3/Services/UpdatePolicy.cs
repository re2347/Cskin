namespace CskinNative.Services;

public static class UpdatePolicy
{
    public const string CurrentVersionText = "0.4.1";

    public static UpdateGateDecision Evaluate(string? currentVersion, UpdateManifest? manifest)
    {
        if (manifest is null || !IsValidManifest(manifest))
            return new(UpdateGateStatus.Unavailable, "版本服务返回的数据无效");
        if (!Version.TryParse(Normalize(currentVersion), out var current)
            || !Version.TryParse(Normalize(manifest.MinimumVersion), out var minimum))
            return new(UpdateGateStatus.Unavailable, "本地或服务端版本号无效");

        return current < minimum
            ? new(UpdateGateStatus.Required, string.IsNullOrWhiteSpace(manifest.Message) ? "当前版本已停止使用，请下载最新版本。" : manifest.Message.Trim())
            : new(UpdateGateStatus.Allowed, "当前版本已是可用版本");
    }

    public static bool IsValidManifest(UpdateManifest manifest)
    {
        if (manifest.Schema != 1 || manifest.Sequence < 1
            || !string.Equals(manifest.Channel?.Trim(), "stable", StringComparison.OrdinalIgnoreCase)
            || !Version.TryParse(Normalize(manifest.LatestVersion), out var latest)
            || !Version.TryParse(Normalize(manifest.MinimumVersion), out var minimum)
            || minimum > latest
            || string.IsNullOrWhiteSpace(manifest.Title)
            || string.IsNullOrWhiteSpace(manifest.Message)
            || string.IsNullOrWhiteSpace(manifest.DownloadPassword)
            || string.IsNullOrWhiteSpace(manifest.GroupNumber)) return false;

        return Uri.TryCreate(manifest.DownloadUrl?.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrWhiteSpace(uri.Query)
            && string.IsNullOrWhiteSpace(uri.Fragment);
    }

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().TrimStart('v', 'V');
}
