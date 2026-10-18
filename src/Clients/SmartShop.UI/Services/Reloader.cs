using System.Text.Json;

namespace SmartShop.UI.Services;

/// <summary>
/// Turns bursts of real-time events into one reload: events within the wait join the reload already scheduled,
/// two loads never run at once (a slower, older response could overwrite a newer one), and a request made while
/// loading runs once more afterwards. Call it from the renderer's context (inside <c>InvokeAsync</c>).
/// </summary>
public sealed class Reloader(Func<Task> load) : IDisposable
{
    private CancellationTokenSource? _wait;
    private Task? _running;
    private bool _again;
    private bool _disposed;

    /// <summary>Reload after <paramref name="delay"/>, unless a reload is already waiting (then this joins it).</summary>
    public void Schedule(TimeSpan delay)
    {
        if (_disposed || _wait is not null) return;
        _wait = new CancellationTokenSource();
        _ = WaitThenRunAsync(delay, _wait.Token);
    }

    /// <summary>A random wait between <paramref name="min"/> and <paramref name="max"/>, so a whole village's phones
    /// do not all ask the server at the same moment after one shop opens.</summary>
    public void ScheduleJittered(TimeSpan min, TimeSpan max) =>
        Schedule(min + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * (max - min).TotalMilliseconds));

    /// <summary>Reload now (e.g. after the user's own action); a waiting reload is folded into this one.</summary>
    public Task RunNowAsync()
    {
        CancelWait();
        return RunAsync();
    }

    private async Task WaitThenRunAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { return; }
        CancelWait();
        await RunAsync();
    }

    private Task RunAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_running is { IsCompleted: false })
        {
            _again = true;
            return _running;
        }
        return _running = LoopAsync();
    }

    private async Task LoopAsync()
    {
        do
        {
            _again = false;
            try { await load(); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or ObjectDisposedException)
            {
                // Offline for a moment or the page is gone: the next event or reconnect reloads again.
            }
        } while (_again && !_disposed);
    }

    private void CancelWait()
    {
        _wait?.Cancel();
        _wait?.Dispose();
        _wait = null;
    }

    public void Dispose()
    {
        _disposed = true;
        CancelWait();
    }
}
