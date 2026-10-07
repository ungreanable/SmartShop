using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using SmartShop.UI.Components;

namespace SmartShop.UI.Services;

/// <summary>
/// Turns on push for this device. The browser's permission prompt only ever comes after our own explanation
/// (<see cref="PushPrimerDialog"/>) and a tap on "Continue": people who are not ready choose "Later" there instead of
/// "Block" in the browser, which the app cannot undo and which makes Chrome show quieter prompts for the site.
/// </summary>
public sealed class PushPrompt(IPushRegistrar push, Api api, IDialogService dialogs, IAppStorage storage, PushEvents events, ISnackbar snackbar, Loc l)
{
    private const string LaterKey = "smartshop.push.laterAt";
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromDays(14);

    /// <summary>From the "Enable" button: explains, asks the browser, registers. True when this device now gets push.</summary>
    public async Task<bool> EnableAsync(Channels? channels = null)
    {
        if (await push.PermissionAsync() == "denied")
        {
            await ShowUnblockHelpAsync();
            return false;
        }
        if (await push.PermissionAsync() != "granted" && !await ExplainAsync(null)) return false;
        return await SubscribeAsync(channels ?? await api.Get<Channels>("api/notifications/channels", silent: true), showResult: true);
    }

    /// <summary>
    /// At a moment where push clearly helps (order placed, shop orders page). Only asks devices that were never asked
    /// in the browser, and not again for two weeks after "Later".
    /// </summary>
    public async Task OfferAsync(string reason)
    {
        try
        {
            if (!await push.IsSupportedAsync() || await push.PermissionAsync() != "default") return;
            if (await storage.GetAsync(LaterKey) is { } later
                && DateTimeOffset.TryParse(later, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                && DateTimeOffset.UtcNow - at < AskAgainAfter) return;
            var channels = await api.Get<Channels>("api/notifications/channels", silent: true);
            if (channels is null || (push.Kind == "WebPush" && !channels.WebPushConfigured)) return;

            if (!await ExplainAsync(reason)) return;
            await SubscribeAsync(channels, showResult: true);
        }
        catch (Exception e) when (e is Microsoft.JSInterop.JSException or HttpRequestException or InvalidOperationException)
        {
            // Never in the way of what the user was doing; the profile page has the button.
        }
    }

    /// <summary>The browser remembers "Block": only the user can undo it, in the browser's own settings.</summary>
    public async Task ShowUnblockHelpAsync() => await dialogs.ShowMessageBoxAsync(
        l["การแจ้งเตือนถูกบล็อกไว้", "Notifications are blocked"],
        (MarkupString)l["เคยกด \"บล็อก\" ไว้ในเบราว์เซอร์ ต้องปลดบล็อกเองก่อน<br/><br/>"
            + "• <b>Chrome บนคอม</b>: กดไอคอนด้านซ้ายของช่อง URL → การแจ้งเตือน → อนุญาต แล้วโหลดหน้าใหม่<br/>"
            + "• <b>Chrome บน Android</b>: กด ⋮ → การตั้งค่า → การตั้งค่าเว็บไซต์ → การแจ้งเตือน → เลือกเว็บนี้ → อนุญาต<br/>"
            + "• <b>iPhone</b>: การตั้งค่า → การแจ้งเตือน → เลือกแอปนี้ (ต้องเพิ่มไปที่หน้าจอโฮมก่อน)",
            "Notifications were blocked in the browser; only you can unblock them:<br/><br/>"
            + "• <b>Chrome on a computer</b>: click the icon left of the address bar → Notifications → Allow, then reload.<br/>"
            + "• <b>Chrome on Android</b>: ⋮ → Settings → Site settings → Notifications → this site → Allow.<br/>"
            + "• <b>iPhone</b>: Settings → Notifications → this app (add it to the Home Screen first)."],
        yesText: "OK");

    private async Task<bool> ExplainAsync(string? reason)
    {
        var parameters = new DialogParameters<PushPrimerDialog> { { d => d.Reason, reason } };
        var result = await (await dialogs.ShowAsync<PushPrimerDialog>("", parameters,
            new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true, NoHeader = true })).Result;
        if (result is { Canceled: false }) return true;
        await storage.SetAsync(LaterKey, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return false;
    }

    private async Task<bool> SubscribeAsync(Channels? channels, bool showResult)
    {
        PushSubscriptionInfo? sub;
        try { sub = await push.SubscribeAsync(channels?.VapidPublicKey); }
        catch (PushSetupException e)
        {
            snackbar.Add(e.Reason == "no_service_worker"
                ? l["เบราว์เซอร์ยังเตรียมระบบแจ้งเตือนไม่เสร็จ (Service Worker) ลองโหลดหน้าใหม่แล้วกดอีกครั้ง", "The browser's service worker is not ready. Reload the page and try again."]
                : l["เปิดแจ้งเตือนไม่ได้: ", "Could not enable push: "] + e.Reason, Severity.Error);
            return false;
        }
        if (sub is null)
        {
            if (await push.PermissionAsync() == "denied") await ShowUnblockHelpAsync();
            else if (showResult) snackbar.Add(l["ยังไม่ได้อนุญาตการแจ้งเตือน", "Notifications were not allowed"], Severity.Warning);
            return false;
        }
        if (!await api.Post("api/notifications/devices", new { kind = push.Kind, endpoint = sub.Endpoint, p256dh = sub.P256dh, auth = sub.Auth, label = sub.Label }))
            return false;
        if (showResult) snackbar.Add(l["เปิดแจ้งเตือนบนอุปกรณ์นี้แล้ว", "Push enabled on this device"], Severity.Success);
        events.OnDeviceRegistered();
        return true;
    }
}
