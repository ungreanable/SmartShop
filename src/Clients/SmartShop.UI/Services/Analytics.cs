using Microsoft.JSInterop;

namespace SmartShop.UI.Services;

/// <summary>
/// Anonymous usage statistics for shops (views, add-to-cart ...). Events are queued and sent in one small batch every
/// few seconds or when the page is hidden; failures are dropped. Nothing here may slow down, block or break a page:
/// every method returns at once or swallows its errors. The server decides who and when; the client only says what.
/// </summary>
public sealed class Analytics(Api api, IJSRuntime js) : IDisposable
{
    private const int MaxBatch = 50;
    // Offline for a long time: keep the newest events, not an ever-growing list in a low-end phone's memory.
    private const int MaxQueued = 200;
    private static readonly TimeSpan FlushAfter = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly List<object> _queue = [];
    private Timer? _timer;
    private DotNetObjectReference<Analytics>? _self;

    /// <summary>Whether the page is in front. Heartbeats pause while it is not.</summary>
    public bool Visible { get; private set; } = true;
    public event Action<bool>? VisibilityChanged;

    public void ShopView(Guid shopId) => Track("shop_view", shopId);
    public void ItemView(Guid shopId, Guid itemId) => Track("item_view", shopId, itemId);
    public void AddToCart(Guid shopId, Guid? itemId, int quantity, bool custom) => Track("add_to_cart", shopId, itemId, quantity, custom);
    public void CartView(Guid shopId) => Track("cart_view", shopId);

    /// <summary>"I am looking at this shop"; returns how many people (me included) are, or null when unknown.</summary>
    public async Task<int?> HeartbeatAsync(Guid shopId) => (await api.QuietAsync<PresenceCount>(HttpMethod.Post, $"api/analytics/presence/{shopId}", new { }))?.Count;

    public Task LeaveAsync(Guid shopId) => api.QuietAsync<object>(HttpMethod.Delete, $"api/analytics/presence/{shopId}");

    /// <summary>Shop members: people looking at the shop right now.</summary>
    public async Task<int?> LiveAsync(Guid shopId) => (await api.QuietAsync<PresenceCount>(HttpMethod.Get, $"api/merchant/shops/{shopId}/analytics/live"))?.Count;

    /// <summary>Starts listening for the page being hidden/shown (once per app).</summary>
    public async Task WatchVisibilityAsync()
    {
        if (_self is not null) return;
        _self = DotNetObjectReference.Create(this);
        try { await js.InvokeVoidAsync("smartshop.onVisibility", _self); }
        catch (JSException) { } // an older smartshop.js right after an update: heartbeats simply never pause
        catch (InvalidOperationException) { } // prerendering: no JS yet
    }

    [JSInvokable]
    public void OnVisibilityChanged(bool visible)
    {
        if (!visible) _ = FlushAsync();
        if (Visible == visible) return;
        Visible = visible;
        VisibilityChanged?.Invoke(visible);
    }

    private void Track(string type, Guid shopId, Guid? itemId = null, int? quantity = null, bool? custom = null)
    {
        lock (_gate)
        {
            _queue.Add(new { type, shopId, itemId, quantity, custom });
            if (_queue.Count > MaxQueued) _queue.RemoveRange(0, _queue.Count - MaxQueued);
            _timer ??= new Timer(_ => _ = FlushAsync(), null, FlushAfter, Timeout.InfiniteTimeSpan);
        }
        _ = WatchVisibilityAsync();
    }

    public async Task FlushAsync()
    {
        List<object> batch;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_queue.Count == 0) return;
            batch = _queue.Take(MaxBatch).ToList();
            _queue.RemoveRange(0, batch.Count);
            if (_queue.Count > 0) _timer = new Timer(_ => _ = FlushAsync(), null, FlushAfter, Timeout.InfiniteTimeSpan);
        }
        try { await api.QuietAsync<object>(HttpMethod.Post, "api/analytics/events", batch); }
        catch (Exception) { } // statistics are best effort: never let them surface
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _self?.Dispose();
    }
}
