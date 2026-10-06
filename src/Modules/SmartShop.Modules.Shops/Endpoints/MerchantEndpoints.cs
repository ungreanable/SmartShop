using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartShop.Contracts.Media;
using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Media;
using SmartShop.Infrastructure.Tenancy;
using SmartShop.Modules.Shops.Domain;
using SmartShop.Modules.Shops.Services;
using SmartShop.SharedKernel;
using SmartShop.SharedKernel.Scheduling;

namespace SmartShop.Modules.Shops.Endpoints;

public sealed record ProfileRequest(string Name, string? Category, string? Description, Guid? LogoId, Guid? CoverId, string? HouseNo, string? Phone, string? LineContact);
public sealed record HoursRequest(List<OpeningHourDto> Hours);
public sealed record ClosureRequest(DateTimeOffset Start, DateTimeOffset End, string? Reason);

/// <summary>Manual status button: <c>open</c>, <c>close</c> or <c>auto</c> (follow the schedule). Until null = until changed.</summary>
public sealed record StatusRequest(string Action, DateTimeOffset? Until);
public sealed record BusyRequest(int Minutes);
public sealed record VacationRequest(DateTimeOffset? Until, string? Message);
public sealed record OrderSettingsRequest(AcceptMode AcceptMode, int AcceptTimeoutMinutes, int ReminderAfterMinutes, int AutoCompleteHours,
    bool AllowPreorderWhenClosed, int PrepTimeMinutes, int SlotIntervalMinutes, bool RequirePaymentBeforePreparing);
public sealed record DeliveryOptionsRequest(bool PickupEnabled, string? PickupInstruction, bool DeliveryEnabled, string? DeliveryZoneNote, decimal? DeliveryMinOrder);
public sealed record PaymentMethodRequest(PaymentMethodType Type, string DisplayName, string? PromptPayId, Guid? QrImageId,
    string? BankName, string? AccountNumber, string? AccountName, string? Instructions, Guid? ImageId, bool? RequiresProof, bool Enabled = true);
public sealed record ReorderRequest(List<Guid> Ids);

internal static class MerchantEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        var m = api.MapGroup("/merchant/shops/{shopId:guid}").WithTags("Merchant").RequirePlantMember();

        m.MapGet("/", async (Guid shopId, MerchantContext ctx, IMediaUrls media, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Staff, ct);
            return ShopMapper.Settings(shop, media, ctx.Now, ctx.UserId);
        });

        m.MapPut("/profile", async (Guid shopId, ProfileRequest req, MerchantContext ctx, IMediaService media, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            if (req.LogoId is { } logo && logo != shop.LogoId) await media.RequireOwnedAsync(logo, ctx.UserId, MediaPurpose.ShopLogo, ct);
            if (req.CoverId is { } cover && cover != shop.CoverId) await media.RequireOwnedAsync(cover, ctx.UserId, MediaPurpose.ShopCover, ct);
            shop.UpdateProfile(req.Name, req.Category, req.Description, req.LogoId, req.CoverId, req.HouseNo, req.Phone, req.LineContact);
            await ctx.SaveAsync(shop, ct, false, new ShopProfileUpdated(shop.PlantId, shop.Id));
            return Results.NoContent();
        });

        m.MapPut("/hours", async (Guid shopId, HoursRequest req, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            shop.SetHours(req.Hours.Select(h => new OpeningHour(h.Day, h.OpenAt, h.CloseAt)).ToList());
            if (shop.Hours.Count > 0) await ctx.EnsureCanOpenAsync(shop, ct);
            await ctx.SaveAsync(shop, ct, true, new OpeningHoursChanged(shop.PlantId, shop.Id));
            return Results.NoContent();
        });

        m.MapPost("/closures", async (Guid shopId, ClosureRequest req, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            var closure = shop.AddClosure(req.Start, req.End, req.Reason, ctx.Now);
            await ctx.SaveAsync(shop, ct, true, new OpeningHoursChanged(shop.PlantId, shop.Id));
            return Results.Ok(new ClosureDto(closure.Id, closure.Start, closure.End, closure.Reason));
        });

        m.MapDelete("/closures/{closureId:guid}", async (Guid shopId, Guid closureId, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            shop.RemoveClosure(closureId);
            await ctx.SaveAsync(shop, ct, true, new OpeningHoursChanged(shop.PlantId, shop.Id));
            return Results.NoContent();
        });

        // The big open/close toggle in merchant mode. Staff may use it.
        m.MapPost("/status", async (Guid shopId, StatusRequest req, MerchantContext ctx, IMediaUrls media, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Staff, ct);
            var mode = req.Action.ToLowerInvariant() switch
            {
                "open" => OverrideMode.ForceOpen,
                "close" => OverrideMode.ForceClosed,
                "auto" => OverrideMode.None,
                _ => throw new DomainException("validation", "Action must be open, close or auto."),
            };
            if (mode != OverrideMode.ForceClosed) await ctx.EnsureCanOpenAsync(shop, ct);
            shop.SetOverride(mode, req.Until, ctx.Now);
            await ctx.SaveAsync(shop, ct, true);
            return ShopMapper.Status(shop, ctx.Now);
        });

        m.MapPost("/busy", async (Guid shopId, BusyRequest req, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Staff, ct);
            shop.SetBusy(req.Minutes, ctx.Now);
            await ctx.SaveAsync(shop, ct, true);
            return ShopMapper.Status(shop, ctx.Now);
        });

        m.MapPut("/vacation", async (Guid shopId, VacationRequest req, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            shop.StartVacation(req.Until, req.Message, ctx.Now);
            await ctx.SaveAsync(shop, ct, true, new ShopProfileUpdated(shop.PlantId, shop.Id));
            return ShopMapper.Status(shop, ctx.Now);
        });

        m.MapDelete("/vacation", async (Guid shopId, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            shop.EndVacation();
            await ctx.SaveAsync(shop, ct, true, new ShopProfileUpdated(shop.PlantId, shop.Id));
            return ShopMapper.Status(shop, ctx.Now);
        });

        m.MapPut("/order-settings", async (Guid shopId, OrderSettingsRequest r, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            shop.UpdateOrderSettings(r.AcceptMode, r.AcceptTimeoutMinutes, r.ReminderAfterMinutes, r.AutoCompleteHours,
                r.AllowPreorderWhenClosed, r.PrepTimeMinutes, r.SlotIntervalMinutes, r.RequirePaymentBeforePreparing);
            await ctx.SaveAsync(shop, ct, false, new ShopProfileUpdated(shop.PlantId, shop.Id));
            return Results.NoContent();
        });

        m.MapPut("/delivery-options", async (Guid shopId, DeliveryOptionsRequest r, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            shop.UpdateDeliveryOptions(r.PickupEnabled, r.PickupInstruction, r.DeliveryEnabled, r.DeliveryZoneNote, r.DeliveryMinOrder);
            await ctx.SaveAsync(shop, ct, false, new DeliveryOptionsUpdated(shop.PlantId, shop.Id));
            return Results.NoContent();
        });

        // ---- payment methods ----
        m.MapPost("/payment-methods", async (Guid shopId, PaymentMethodRequest r, MerchantContext ctx, IMediaService media, IMediaUrls urls, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            if (shop.PaymentMethods.Count >= 10) throw new DomainException("validation", "At most 10 payment methods.");
            await ValidateImagesAsync(r, null, ctx.UserId, media, ct);
            var method = PaymentMethod.Create(shop.Id, r.Type, r.DisplayName, Details(r), shop.PaymentMethods.Count);
            method.Update(r.DisplayName, Details(r), r.RequiresProof, r.Enabled);
            shop.PaymentMethods.Add(method);
            ctx.Db.PaymentMethods.Add(method);
            await ctx.SaveAsync(shop, ct, false, new PaymentMethodsUpdated(shop.PlantId, shop.Id));
            return Results.Ok(ShopMapper.Payment(method, urls));
        });

        m.MapPut("/payment-methods/{methodId:guid}", async (Guid shopId, Guid methodId, PaymentMethodRequest r, MerchantContext ctx,
            IMediaService media, IMediaUrls urls, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            var method = shop.PaymentMethods.FirstOrDefault(p => p.Id == methodId) ?? throw new NotFoundException("Payment method", methodId);
            if (method.Type != r.Type) throw new DomainException("validation", "The payment method type cannot be changed.");
            await ValidateImagesAsync(r, method, ctx.UserId, media, ct);
            method.Update(r.DisplayName, Details(r), r.RequiresProof, r.Enabled);
            EnsureOneEnabled(shop);
            await ctx.SaveAsync(shop, ct, false, new PaymentMethodsUpdated(shop.PlantId, shop.Id));
            return Results.Ok(ShopMapper.Payment(method, urls));
        });

        m.MapDelete("/payment-methods/{methodId:guid}", async (Guid shopId, Guid methodId, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            var method = shop.PaymentMethods.FirstOrDefault(p => p.Id == methodId) ?? throw new NotFoundException("Payment method", methodId);
            shop.PaymentMethods.Remove(method);
            ctx.Db.PaymentMethods.Remove(method);
            EnsureOneEnabled(shop);
            await ctx.SaveAsync(shop, ct, false, new PaymentMethodsUpdated(shop.PlantId, shop.Id));
            return Results.NoContent();
        });

        m.MapPut("/payment-methods/order", async (Guid shopId, ReorderRequest r, MerchantContext ctx, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            for (var i = 0; i < r.Ids.Count; i++)
                shop.PaymentMethods.FirstOrDefault(p => p.Id == r.Ids[i])?.SetOrder(i);
            await ctx.SaveAsync(shop, ct, false, new PaymentMethodsUpdated(shop.PlantId, shop.Id));
            return Results.NoContent();
        });
    }

    internal static PaymentMethodDetails Details(PaymentMethodRequest r) =>
        new(r.PromptPayId, r.QrImageId, r.BankName, r.AccountNumber, r.AccountName, r.Instructions, r.ImageId);

    internal static async Task ValidateImagesAsync(PaymentMethodRequest r, PaymentMethod? existing, Guid userId, IMediaService media, CancellationToken ct)
    {
        if (r.QrImageId is { } qr && qr != existing?.QrImageId) await media.RequireOwnedAsync(qr, userId, MediaPurpose.PaymentQr, ct);
        if (r.ImageId is { } image && image != existing?.ImageId) await media.RequireOwnedAsync(image, userId, MediaPurpose.PaymentQr, ct);
    }

    internal static void EnsureOneEnabled(Shop shop)
    {
        if (!shop.PaymentMethods.Any(p => p.Enabled))
            throw new DomainException("payment_method_required", "Keep at least one payment method enabled.");
    }
}
