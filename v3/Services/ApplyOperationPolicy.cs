namespace CskinNative.Services;

public enum ApplyOperationPhase
{
    Cache,
    AvailabilityProbe,
    Download,
    Engine,
}

public static class ApplyOperationPolicy
{
    public static TimeSpan TimeoutFor(ApplyOperationPhase phase) => phase switch
    {
        ApplyOperationPhase.Cache => TimeSpan.FromSeconds(90),
        ApplyOperationPhase.AvailabilityProbe => TimeSpan.FromSeconds(5),
        ApplyOperationPhase.Download => TimeSpan.FromSeconds(15),
        ApplyOperationPhase.Engine => TimeSpan.FromSeconds(5),
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    public static string TimeoutMessage(ApplyOperationPhase phase) => phase switch
    {
        ApplyOperationPhase.Cache => "缓存皮肤超时（90 秒），请检查网络、授权状态和日志后重试",
        ApplyOperationPhase.AvailabilityProbe => "资源可用性探测未完成（5 秒），资源不可用，应用失败",
        ApplyOperationPhase.Download => "皮肤下载超时（15 秒），请重新打开软件或重新应用",
        ApplyOperationPhase.Engine => "引擎应用超时（5 秒），请检查游戏进程和引擎日志后重试",
        _ => "皮肤应用超时，请查看日志后重试",
    };

    public static async Task<T> RunWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken parentToken,
        ApplyOperationPhase phase,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        linked.CancelAfter(timeout ?? TimeoutFor(phase));
        try
        {
            return await operation(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!parentToken.IsCancellationRequested && linked.IsCancellationRequested)
        {
            throw new TimeoutException(TimeoutMessage(phase));
        }
    }
}

public static class SkinPagingPolicy
{
    public static bool ShouldShowPager(int groupCount, int pageSize)
    {
        if (groupCount < 0) throw new ArgumentOutOfRangeException(nameof(groupCount));
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return groupCount > pageSize;
    }
}

public static class CatalogStartupPolicy
{
    public static bool ShouldRenderInitial(bool syncCompleted, bool syncSucceeded, bool localCatalogAvailable) =>
        syncCompleted && (syncSucceeded || localCatalogAvailable);
}

public sealed class ApplyOperationController : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private readonly List<CancellationTokenSource> _retired = [];

    public CancellationToken Begin(CancellationToken lifetimeToken)
    {
        lock (_gate)
        {
            RetireCurrentNoLock();
            _current = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            return _current.Token;
        }
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (_current is null) return false;
            RetireCurrentNoLock();
            return true;
        }
    }

    public bool IsCurrent(CancellationToken token)
    {
        lock (_gate)
        {
            return _current is not null && _current.Token == token;
        }
    }

    public void Complete(CancellationToken token)
    {
        lock (_gate)
        {
            if (_current is not null && _current.Token == token)
            {
                _current.Dispose();
                _current = null;
                return;
            }

            for (var i = 0; i < _retired.Count; i++)
            {
                if (_retired[i].Token != token) continue;
                _retired[i].Dispose();
                _retired.RemoveAt(i);
                return;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            RetireCurrentNoLock();
            foreach (var source in _retired) source.Dispose();
            _retired.Clear();
        }
    }

    private void RetireCurrentNoLock()
    {
        if (_current is null) return;
        try { _current.Cancel(); }
        catch (ObjectDisposedException) { }
        _retired.Add(_current);
        _current = null;
    }
}

public static class RememberedSelectionPolicy
{
    public static bool CanAttempt(bool engineReady, bool pollCompleted, int championId, int? skinId, bool hasCache) =>
        engineReady
        && pollCompleted
        && championId > 0
        && skinId is > 0
        && hasCache;
}
