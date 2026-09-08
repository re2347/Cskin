using System.Text.Json.Serialization;

namespace CskinNative.Services;

public sealed class UpdateManifest
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("sequence")]
    public int Sequence { get; set; } = 1;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "stable";

    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; set; } = "0.4.0";

    [JsonPropertyName("minimumVersion")]
    public string MinimumVersion { get; set; } = "0.4.0";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "请更新到最新版本";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "当前版本已停止使用，请下载最新版本。";

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = "https://wwboj.lanzoum.com/b01euq54ah";

    [JsonPropertyName("downloadPassword")]
    public string DownloadPassword { get; set; } = "c1le";

    [JsonPropertyName("groupNumber")]
    public string GroupNumber { get; set; } = "712178830";

    [JsonPropertyName("effectiveAt")]
    public string EffectiveAt { get; set; } = "2026-09-08T00:00:00Z";

    public static UpdateManifest Default => new();
}

public enum UpdateGateStatus
{
    Allowed,
    Required,
    Unavailable,
}

public sealed record UpdateGateDecision(UpdateGateStatus Status, string Message)
{
    public bool IsRequired => Status == UpdateGateStatus.Required;
}

public sealed record UpdateCheckResult(
    UpdateGateDecision Decision,
    UpdateManifest? Manifest,
    string Endpoint,
    string Error)
{
    public UpdateGateStatus Status => Decision.Status;
    public bool IsRequired => Decision.IsRequired;

    public static UpdateCheckResult Unavailable(string error = "版本服务暂时不可用") =>
        new(new UpdateGateDecision(UpdateGateStatus.Unavailable, error), null, "", error);
}
