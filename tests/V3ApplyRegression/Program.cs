using CskinNative.Services;

var failures = new List<string>();

Run("starting a replacement apply cancels the previous operation", () =>
{
    using var controller = new ApplyOperationController();
    using var lifetime = new CancellationTokenSource();
    var first = controller.Begin(lifetime.Token);
    var second = controller.Begin(lifetime.Token);

    True(first.IsCancellationRequested, "the previous skin apply token was not cancelled");
    False(second.IsCancellationRequested, "the replacement skin apply token was cancelled immediately");
    False(controller.IsCurrent(first), "the cancelled operation remained the current owner");
    True(controller.IsCurrent(second), "the replacement operation did not become the current owner");
    controller.Complete(first);
    True(controller.IsCurrent(second), "completing a stale operation cleared the replacement owner");
});

Run("manual cancellation releases the active apply operation", () =>
{
    using var controller = new ApplyOperationController();
    using var lifetime = new CancellationTokenSource();
    var token = controller.Begin(lifetime.Token);

    True(controller.Cancel(), "active apply cancellation was not reported");
    True(token.IsCancellationRequested, "active apply token was not cancelled");
    False(controller.Cancel(), "cancelling an idle apply operation reported success");
});

Run("a cancelled operation token remains usable until completion", () =>
{
    using var controller = new ApplyOperationController();
    using var lifetime = new CancellationTokenSource();
    var token = controller.Begin(lifetime.Token);
    True(controller.Cancel(), "operation was not cancelled");
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
    True(linked.IsCancellationRequested, "a cancelled operation token lost its cancellation state");
    controller.Complete(token);
});

Run("apply phases have bounded deadlines and diagnostic messages", () =>
{
    Equal(TimeSpan.FromSeconds(90), ApplyOperationPolicy.TimeoutFor(ApplyOperationPhase.Cache), "cache timeout");
    Equal(TimeSpan.FromSeconds(5), ApplyOperationPolicy.TimeoutFor(ApplyOperationPhase.AvailabilityProbe), "availability probe timeout");
    Equal(TimeSpan.FromSeconds(15), ApplyOperationPolicy.TimeoutFor(ApplyOperationPhase.Download), "download timeout");
    Equal(TimeSpan.FromSeconds(5), ApplyOperationPolicy.TimeoutFor(ApplyOperationPhase.Engine), "engine timeout");
    True(ApplyOperationPolicy.TimeoutMessage(ApplyOperationPhase.Cache).Contains("缓存", StringComparison.Ordinal), "cache timeout message did not identify the phase");
    True(ApplyOperationPolicy.TimeoutMessage(ApplyOperationPhase.AvailabilityProbe).Contains("资源", StringComparison.Ordinal), "availability probe timeout message did not identify the phase");
    True(ApplyOperationPolicy.TimeoutMessage(ApplyOperationPhase.Download).Contains("15", StringComparison.Ordinal), "download timeout message did not identify the deadline");
    True(ApplyOperationPolicy.TimeoutMessage(ApplyOperationPhase.Engine).Contains("引擎", StringComparison.Ordinal), "engine timeout message did not identify the phase");
    True(ApplyOperationPolicy.TimeoutMessage(ApplyOperationPhase.Engine).Contains("5", StringComparison.Ordinal), "engine timeout message did not identify the deadline");
    try
    {
        ApplyOperationPolicy.RunWithTimeoutAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            CancellationToken.None,
            ApplyOperationPhase.Cache,
            TimeSpan.FromMilliseconds(20)).GetAwaiter().GetResult();
        throw new InvalidOperationException("a stalled cache operation did not time out");
    }
    catch (TimeoutException ex)
    {
        True(ex.Message.Contains("缓存", StringComparison.Ordinal), "timeout exception did not identify the cache phase");
    }
});

Run("resource availability uses metadata endpoint instead of file download", () =>
    Equal("v1/skins/availability?skinId=123", SkinRepository.AvailabilityPath(123), "availability endpoint path"));

Run("resource gateways probe in parallel within one five-second budget", () =>
{
    var started = Environment.TickCount64;
    var batch = ResourceProbePolicy.ProbeInParallelAsync(
        ["supabase", "custom-cloudflare", "workers"],
        async (endpoint, token) =>
        {
            if (endpoint == "custom-cloudflare") await Task.Delay(80, token);
            else await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new ResourceProbeAttempt(
                endpoint,
                new SkinResourceProbeResult(endpoint == "custom-cloudflare", "ok", endpoint, 200),
                DnsMilliseconds: 12,
                ConnectHttpMilliseconds: 30,
                ElapsedMilliseconds: 42);
        },
        CancellationToken.None).GetAwaiter().GetResult();

    True(batch.Winner?.Result.Available == true, "the fast gateway did not win the parallel probe");
    Equal("custom-cloudflare", batch.Winner!.Endpoint, "parallel probe selected the wrong gateway");
    True(Environment.TickCount64 - started < 1500, "parallel probe waited for a slow gateway");
    Equal(3, batch.Attempts.Count, "parallel probe did not retain per-gateway diagnostics");
});

Run("all stalled resource gateways stop at the shared timeout", () =>
{
    var started = Environment.TickCount64;
    var batch = ResourceProbePolicy.ProbeInParallelAsync(
        ["supabase", "custom-cloudflare", "workers"],
        async (endpoint, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new ResourceProbeAttempt(
                endpoint,
                new SkinResourceProbeResult(false, "unreachable", endpoint),
                1,
                1,
                2);
        },
        CancellationToken.None,
        TimeSpan.FromMilliseconds(120)).GetAwaiter().GetResult();

    False(batch.Winner is not null, "a stalled gateway was reported as the winner");
    Equal(3, batch.Attempts.Count, "all gateway timeout diagnostics were not retained");
    True(Environment.TickCount64 - started < 1000, "stalled gateways exceeded the shared timeout");
    Equal(5d, ResourceProbePolicy.TotalTimeout.TotalSeconds, "resource probe total timeout");
});

Run("trusted local cache can skip remote availability probing", () =>
{
    True(SkinCachePolicy.CanSkipAvailabilityProbe(hasTrustedCache: true), "trusted cache did not skip remote probing");
    False(SkinCachePolicy.CanSkipAvailabilityProbe(hasTrustedCache: false), "untrusted cache skipped remote probing");
});

Run("resource probe diagnostics retain DNS connection HTTP and elapsed timings", () =>
{
    var attempt = new ResourceProbeAttempt(
        "supabase",
        new SkinResourceProbeResult(false, "timeout", "supabase", null),
        DnsMilliseconds: 11,
        ConnectHttpMilliseconds: 123,
        ElapsedMilliseconds: 134,
        Detail: "request timed out");

    Equal(11L, attempt.DnsMilliseconds, "DNS timing was not retained");
    Equal(123L, attempt.ConnectHttpMilliseconds, "connection timing was not retained");
    Equal(134L, attempt.ElapsedMilliseconds, "elapsed timing was not retained");
    True(attempt.Detail.Contains("timed", StringComparison.Ordinal), "diagnostic detail was not retained");
});

Run("skin pager is hidden for zero or one page and shown for multiple pages", () =>
{
    False(SkinPagingPolicy.ShouldShowPager(0, 24), "pager was shown for an empty catalog");
    False(SkinPagingPolicy.ShouldShowPager(24, 24), "pager was shown for exactly one page");
    True(SkinPagingPolicy.ShouldShowPager(25, 24), "pager was hidden for multiple pages");
});

Run("remembered apply is reserved only after polling and cache are ready", () =>
{
    False(RememberedSelectionPolicy.CanAttempt(engineReady: false, pollCompleted: true, championId: 120, skinId: 120003, hasCache: true), "engine-not-ready selection was reserved");
    False(RememberedSelectionPolicy.CanAttempt(engineReady: true, pollCompleted: false, championId: 120, skinId: 120003, hasCache: true), "pre-poll selection was reserved");
    False(RememberedSelectionPolicy.CanAttempt(engineReady: true, pollCompleted: true, championId: 120, skinId: 120003, hasCache: false), "uncached selection was reserved");
    True(RememberedSelectionPolicy.CanAttempt(engineReady: true, pollCompleted: true, championId: 120, skinId: 120003, hasCache: true), "ready cached selection was not eligible");
});

Run("startup does not render before remote sync completes", () =>
    False(CatalogStartupPolicy.ShouldRenderInitial(syncCompleted: false, syncSucceeded: false, localCatalogAvailable: true), "old catalog rendered before sync"));
Run("startup renders local catalog after a failed sync", () =>
    True(CatalogStartupPolicy.ShouldRenderInitial(syncCompleted: true, syncSucceeded: false, localCatalogAvailable: true), "offline fallback catalog was not renderable"));
Run("startup renders remote catalog after a successful sync", () =>
    True(CatalogStartupPolicy.ShouldRenderInitial(syncCompleted: true, syncSucceeded: true, localCatalogAvailable: false), "remote catalog was not renderable"));

if (failures.Count > 0)
{
    Console.Error.WriteLine("FAILED (" + failures.Count + ")");
    foreach (var failure in failures) Console.Error.WriteLine("- " + failure);
    return 1;
}

Console.WriteLine("PASS: all V3 apply regression tests");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine("PASS: " + name);
    }
    catch (Exception ex)
    {
        failures.Add(name + ": " + ex.Message);
    }
}

void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(message + "; expected=" + expected + ", actual=" + actual);
}

void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

void False(bool value, string message) => True(!value, message);
