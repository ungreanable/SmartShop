using System.Text.Json;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace SmartShop.UI.Services;

/// <summary>
/// Rings on every device of every shop member when a new order arrives, and stops as soon as anyone accepts,
/// rejects or the order expires (order.claimed). Works anywhere in the app, not only in merchant mode.
/// </summary>
public sealed class OrderAlarm(Realtime realtime, Browser browser, ISnackbar snackbar, NavigationManager nav, Loc l)
{
    private readonly Dictionary<Guid, (Guid ShopId, string OrderNo)> _pending = [];
    private readonly Dictionary<Guid, Snackbar?> _toasts = [];
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
        _pending.Clear();
        foreach (var toast in _toasts.Values) if (toast is not null) snackbar.Remove(toast);
        _toasts.Clear();
        await browser.StopAlarmAsync();
        Changed?.Invoke();
    }

    private async void OnNewOrder(JsonElement e)
    {
        var orderId = e.GetProperty("orderId").GetGuid();
        var orderNo = e.GetProperty("orderNo").GetString() ?? "";
        _pending[orderId] = (Guid.Empty, orderNo);
        await browser.StartAlarmAsync();
        _toasts[orderId] = snackbar.Add(l[$"🛎️ ออเดอร์ใหม่ #{orderNo}", $"🛎️ New order #{orderNo}"], Severity.Warning, o =>
        {
            o.RequireInteraction = true;
            o.Action = l["ดู", "View"];
            o.OnClick = _ =>
            {
                nav.NavigateTo($"/orders/{orderId}");
                return Task.CompletedTask;
            };
        });
        Changed?.Invoke();
    }

    private async void OnClaimed(JsonElement e)
    {
        var orderId = e.GetProperty("orderId").GetGuid();
        // Someone (on any device) accepted, rejected or the order expired: stop ringing and drop the toast.
        if (_toasts.Remove(orderId, out var toast) && toast is not null) snackbar.Remove(toast);
        if (!_pending.Remove(orderId)) return;
        if (_pending.Count == 0) await browser.StopAlarmAsync();
        Changed?.Invoke();
    }
}
