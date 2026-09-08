using System.Text.Json;

namespace CskinNative.Services;

public sealed record ResourceProbeAttempt(
    string Endpoint,
    SkinResourceProbeResult Result,
    long DnsMilliseconds,
    long ConnectHttpMilliseconds,
    long ElapsedMilliseconds,
    string Detail = "");

public sealed record ResourceProbeBatchResult(
    ResourceProbeAttempt? Winner,
    IReadOnlyList<ResourceProbeAttempt> Attempts);

public static class ResourceProbePolicy
{
    public static TimeSpan TotalTimeout => TimeSpan.FromSeconds(5);

    public static async Task<ResourceProbeBatchResult> ProbeInParallelAsync(
        IReadOnlyList<string> endpoints,
        Func<string, CancellationToken, Task<ResourceProbeAttempt>> probe,
        CancellationToken cancellationToken = default,
        TimeSpan? totalTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(probe);
        if (endpoints.Count == 0) return new(null, []);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(totalTimeout ?? TotalTimeout);
        var tasks = endpoints
            .Select(endpoint => RunProbeAsync(endpoint, probe, linked.Token, cancellationToken))
            .ToArray();
        var pending = tasks.ToList();
        var attempts = new List<ResourceProbeAttempt>(tasks.Length);
        ResourceProbeAttempt? winner = null;

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completed);
            var attempt = await completed.ConfigureAwait(false);
            attempts.Add(attempt);
            if (!attempt.Result.Available) continue;

            winner = attempt;
            linked.Cancel();
            break;
        }

        if (pending.Count > 0)
        {
            var remaining = await Task.WhenAll(pending).ConfigureAwait(false);
            attempts.AddRange(remaining);
        }

        return new(winner, attempts);
    }

    private static async Task<ResourceProbeAttempt> RunProbeAsync(
        string endpoint,
        Func<string, CancellationToken, Task<ResourceProbeAttempt>> probe,
        CancellationToken linkedToken,
        CancellationToken parentToken)
    {
        try
        {
            return await probe(endpoint, linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!parentToken.IsCancellationRequested)
        {
            return new(
                endpoint,
                new SkinResourceProbeResult(false, "资源探测超时", endpoint),
                0,
                0,
                0,
                "linked timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            return new(
                endpoint,
                new SkinResourceProbeResult(false, "资源探测请求异常", endpoint),
                0,
                0,
                0,
                ex.Message);
        }
    }
}

public static class SkinCachePolicy
{
    public static bool CanSkipAvailabilityProbe(bool hasTrustedCache) => hasTrustedCache;
}
