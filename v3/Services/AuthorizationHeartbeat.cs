namespace CskinNative.Services;

public sealed class AuthorizationHeartbeat : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private readonly AuthorizationService _authorization = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;
    private int _started;

    public event EventHandler<string>? AuthorizationLost;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(Interval, _lifetime.Token).ConfigureAwait(false);
                if (_lifetime.IsCancellationRequested) break;

                var result = await _authorization.RestoreAsync(_lifetime.Token).ConfigureAwait(false);
                if (!result.Allowed)
                {
                    AuthorizationLost?.Invoke(this, result.Message);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _lifetime.Dispose();
    }
}
