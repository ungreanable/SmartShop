using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace SmartShop.UI.Services;

public sealed record RealtimeNotification(string Type, string Title, string Body, string? Link, string Priority, bool Urgent);

/// <summary>
/// SignalR connection to <c>/hubs/realtime</c>: live order boards, tracking pages and new-order alarms.
/// Phones drop connections all the time (screen off, LINE in the background, weak signal), so it keeps coming back
/// for as long as the user is signed in, and raises <see cref="Resynced"/> afterwards: events sent while it was
/// away are lost, so pages reload what they show.
/// </summary>
public sealed class Realtime(Session session, AppConfig config, IJSRuntime js) : IAsyncDisposable
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(60);

    private HubConnection? _hub;
    private Guid? _plant;
    private readonly HashSet<Guid> _shops = [];
    private bool _stopped = true;
    private bool _restarting;
    private CancellationTokenSource? _retryWait;
    private DotNetObjectReference<Realtime>? _self;

    public event Action<RealtimeNotification>? Notification;
    public event Action<JsonElement>? OrderUpdated;
    public event Action<JsonElement>? NewOrder;
    public event Action<JsonElement>? OrderClaimed;
    public event Action<JsonElement>? PaymentUpdated;
    public event Action<JsonElement>? ShopStatusChanged;
    public event Action<JsonElement>? MenuChanged;
    public event Action<JsonElement>? OrderMessage;
    /// <summary>Connected again after a break: events may have been missed, reload what the page shows.</summary>
    public event Action? Resynced;

    public bool Connected => _hub?.State == HubConnectionState.Connected;

    public async Task StartAsync()
    {
        if (!session.IsAuthenticated) return;
        _stopped = false;
        await WatchPageAsync();
        if (_hub is { State: not HubConnectionState.Disconnected }) return;
        _hub ??= Build();
        if (!await TryStartAsync()) _ = RestartLoopAsync();
    }

    public async Task JoinPlantAsync(Guid plantId)
    {
        if (_plant == plantId) return;
        if (_plant is { } old && Connected) await SafeInvoke("LeavePlant", old);
        _plant = plantId;
        if (Connected) await SafeInvoke("JoinPlant", plantId);
    }

    public async Task JoinShopAsync(Guid shopId)
    {
        if (!_shops.Add(shopId)) return;
        if (Connected) await SafeInvoke("JoinShop", shopId);
    }

    public async Task StopAsync()
    {
        _stopped = true;
        _retryWait?.Cancel();
        if (_hub is not null) await _hub.StopAsync();
        _plant = null;
        _shops.Clear();
    }

    /// <summary>Called from smartshop.js when the app comes back to the front or the network returns.</summary>
    [JSInvokable]
    public void OnPageVisible()
    {
        // Skip the rest of the back-off wait: the user is looking at the app now.
        if (!_stopped && _hub is { State: HubConnectionState.Disconnected }) _retryWait?.Cancel();
    }

    private HubConnection Build()
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(config.ApiBaseUrl), "hubs/realtime"), o => o.AccessTokenProvider = () => session.GetAccessTokenAsync())
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .Build();

        hub.On<JsonElement>("notification", e => Notification?.Invoke(e.Deserialize<RealtimeNotification>(ApiJson.Options)!));
        hub.On<JsonElement>("order.updated", e => OrderUpdated?.Invoke(e));
        hub.On<JsonElement>("order.new", e => NewOrder?.Invoke(e));
        hub.On<JsonElement>("order.claimed", e => OrderClaimed?.Invoke(e));
        hub.On<JsonElement>("payment.updated", e => PaymentUpdated?.Invoke(e));
        hub.On<JsonElement>("shop.status", e => ShopStatusChanged?.Invoke(e));
        hub.On<JsonElement>("shop.menu", e => MenuChanged?.Invoke(e));
        hub.On<JsonElement>("order.message", e => OrderMessage?.Invoke(e));
        hub.Reconnected += async _ =>
        {
            await RejoinAsync();
            Resynced?.Invoke();
        };
        // The automatic reconnect above gives up after about 40 s; keep trying with a slower pace from here on.
        hub.Closed += error =>
        {
            if (!_stopped) _ = RestartLoopAsync();
            return Task.CompletedTask;
        };
        return hub;
    }

    private async Task<bool> TryStartAsync()
    {
        try
        {
            await _hub!.StartAsync();
            await RejoinAsync();
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException or System.Net.WebSockets.WebSocketException)
        {
            return false; // the app still works without real time; RestartLoopAsync tries again
        }
    }

    private async Task RestartLoopAsync()
    {
        if (_restarting) return;
        _restarting = true;
        try
        {
            var delay = FirstRetry;
            while (!_stopped && session.IsAuthenticated && _hub is { State: HubConnectionState.Disconnected })
            {
                using (var wait = _retryWait = new CancellationTokenSource())
                {
                    try { await Task.Delay(delay, wait.Token); }
                    catch (OperationCanceledException) { delay = FirstRetry; } // back in front: try now, and keep it snappy
                    _retryWait = null;
                }
                if (_stopped || !session.IsAuthenticated || _hub.State != HubConnectionState.Disconnected) break;
                if (await TryStartAsync())
                {
                    Resynced?.Invoke();
                    break;
                }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetry.Ticks));
            }
        }
        finally
        {
            _restarting = false;
        }
    }

    private async Task WatchPageAsync()
    {
        if (_self is not null) return;
        _self = DotNetObjectReference.Create(this);
        try { await js.InvokeVoidAsync("smartshop.onPageVisible", _self); }
        catch (JSException) { } // an older smartshop.js right after an update: the back-off loop still reconnects
    }

    private Task RejoinAsync()
    {
        var joins = _shops.Select(shop => SafeInvoke("JoinShop", shop)).ToList();
        if (_plant is { } plant) joins.Add(SafeInvoke("JoinPlant", plant));
        return Task.WhenAll(joins);
    }

    private async Task SafeInvoke(string method, Guid id)
    {
        try { await _hub!.InvokeAsync(method, id); }
        catch (Exception e) when (e is Microsoft.AspNetCore.SignalR.HubException or InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stopped = true;
        _retryWait?.Cancel();
        if (_hub is not null) await _hub.DisposeAsync();
        _self?.Dispose();
    }
}
