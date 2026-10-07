using System.Text.Json;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace SmartShop.UI.Services;

/// <summary>
/// Rings on every device of every shop member when a new order arrives, and stops as soon as anyone accepts,
/// rejects or the order expires (order.claimed). Works anywhere in the app, not only in merchant mode.
/// Real-time messages can arrive out of order or be missed (reconnects), so: claims seen before the order are
/// remembered, the device that acts stops at once (<see cref="Dismiss"/>), and while ringing the order status is
/// re-checked on the server every few seconds.
/// </summary>
public sealed class OrderAlarm(Realtime realtime, Browser browser, ISnackbar snackbar, NavigationManager nav, Loc l, Api api) : IDisposable
{
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(10);

    private readonly Dictionary<Guid, string> _pending = [];
    private readonly Dictionary<Guid, Snackbar?> _toasts = [];
    private readonly LinkedList<Guid> _claimedOrder = [];
    private readonly HashSet<Guid> _claimed = [];
    private Timer? _recheck;

    public IReadOnlyCollection<Guid> PendingOrderIds => _pending.Keys;
    public bool Ringing => _pending.Count > 0;
    public event Action? Changed;

    public void Attach()
    {
        realtime.NewOrder -= OnNewOrder;
        realtime.OrderClaimed -= OnClaimed;
        realtime.NewOrder += OnNewOrder;
        realtime.OrderClaimed += OnClaimed;
    }

    public async Task SilenceAsync()
    {
        foreach (var id in _pending.Keys.ToList()) RememberClaimed(id);
        _pending.Clear();
        foreach (var toast in _toasts.Values) if (toast is not null) snackbar.Remove(toast);
        _toasts.Clear();
        await StopIfIdleAsync();
        Changed?.Invoke();
    }

    /// <summary>This device accepted / rejected the order: stop right away instead of waiting for the server echo.</summary>
    public async Task Dismiss(Guid orderId)
    {
        RememberClaimed(orderId);
        if (_toasts.Remove(orderId, out var toast) && toast is not null) snackbar.Remove(toast);
        if (!_pending.Remove(orderId)) return;
        await StopIfIdleAsync();
        Changed?.Invoke();
    }

    private async void OnNewOrder(JsonElement e)
    {
        var orderId = e.GetProperty("orderId").GetGuid();
        var orderNo = e.GetProperty("orderNo").GetString() ?? "";
        // Already accepted (auto-accept shops, or the claim overtook the new-order message): nothing to ring for.
        if (_claimed.Contains(orderId) || _pending.ContainsKey(orderId)) return;

        _pending[orderId] = orderNo;
        await browser.StartAlarmAsync();
        // Seen right away when the app is in front, so the message goes after 10 s (the alarm keeps ringing until
        // someone accepts); otherwise it waits on screen for the user to come back.
        var focused = await browser.HasFocusAsync();
        _toasts[orderId] = snackbar.Add(l[$"🛎️ ออเดอร์ใหม่ #{orderNo}", $"🛎️ New order #{orderNo}"], Severity.Warning, o =>
        {
            o.RequireInteraction = !focused;
            if (focused) o.VisibleStateDuration = 10_000;
            o.Action = l["ดู", "View"];
            o.OnClick = _ =>
            {
                nav.NavigateTo($"/orders/{orderId}");
                return Task.CompletedTask;
            };
        });
        _recheck ??= new Timer(_ => _ = RecheckAsync(), null, RecheckEvery, RecheckEvery);
        Changed?.Invoke();
    }

    private async void OnClaimed(JsonElement e) => await Dismiss(e.GetProperty("orderId").GetGuid());

    /// <summary>Safety net for missed claims: drop orders that are no longer waiting for acceptance.</summary>
    private async Task RecheckAsync()
    {
        foreach (var orderId in _pending.Keys.ToList())
        {
            try
            {
                var order = await api.Get<Order>($"api/orders/{orderId}", silent: true);
                if (order is not null && order.Status != "PendingAcceptance") await Dismiss(orderId);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Offline for a moment: keep ringing, try again on the next tick.
            }
        }
    }

    private async Task StopIfIdleAsync()
    {
        if (_pending.Count > 0) return;
        await browser.StopAlarmAsync();
        _recheck?.Dispose();
        _recheck = null;
    }

    private void RememberClaimed(Guid orderId)
    {
        if (!_claimed.Add(orderId)) return;
        _claimedOrder.AddLast(orderId);
        if (_claimedOrder.Count > 500)
        {
            _claimed.Remove(_claimedOrder.First!.Value);
            _claimedOrder.RemoveFirst();
        }
    }

    public void Dispose() => _recheck?.Dispose();
}
