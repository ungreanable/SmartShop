using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace SmartShop.UI.Services;

public sealed record RealtimeNotification(string Type, string Title, string Body, string? Link, string Priority, bool Urgent);

/// <summary>SignalR connection to <c>/hubs/realtime</c>: live order boards, tracking pages and new-order alarms.</summary>
public sealed class Realtime(Session session, AppConfig config) : IAsyncDisposable
{
    private HubConnection? _hub;
    private Guid? _plant;
    private readonly HashSet<Guid> _shops = [];

    public event Action<RealtimeNotification>? Notification;
    public event Action<JsonElement>? OrderUpdated;
    public event Action<JsonElement>? NewOrder;
    public event Action<JsonElement>? OrderClaimed;
    public event Action<JsonElement>? PaymentUpdated;
    public event Action<JsonElement>? ShopStatusChanged;
    public event Action<JsonElement>? MenuChanged;
    public event Action<JsonElement>? OrderMessage;

    public bool Connected => _hub?.State == HubConnectionState.Connected;

    public async Task StartAsync()
    {
        if (!session.IsAuthenticated || _hub is { State: not HubConnectionState.Disconnected }) return;
        _hub ??= Build();
        try
        {
            await _hub.StartAsync();
            await RejoinAsync();
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // The automatic reconnect policy keeps trying; the app works without real time (manual refresh).
        }
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
        if (_hub is not null) await _hub.StopAsync();
        _plant = null;
        _shops.Clear();
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
        hub.Reconnected += _ => RejoinAsync();
        return hub;
    }

    private async Task RejoinAsync()
    {
        if (_plant is { } plant) await SafeInvoke("JoinPlant", plant);
        foreach (var shop in _shops) await SafeInvoke("JoinShop", shop);
    }

    private async Task SafeInvoke(string method, Guid id)
    {
        try { await _hub!.InvokeAsync(method, id); }
        catch (Exception e) when (e is Microsoft.AspNetCore.SignalR.HubException or InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }
}
