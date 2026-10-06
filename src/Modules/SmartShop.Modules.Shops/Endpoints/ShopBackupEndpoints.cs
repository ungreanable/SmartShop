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

namespace SmartShop.Modules.Shops.Endpoints;

/// <summary>Shop-settings section of a shop backup file (the menu is in the catalog section).</summary>
public sealed record ShopBackup(int Version, ProfileRequest Profile, List<OpeningHourDto> Hours, OrderSettingsRequest Order,
    DeliveryOptionsRequest Delivery, List<PaymentMethodRequest> PaymentMethods);
public sealed record ShopRestoreResult(int PaymentMethodsCreated, int PaymentMethodsUpdated, bool HoursRestored, bool PicturesKept);

internal static class ShopBackupEndpoints
{
    public const int CurrentVersion = 1;

    public static void Map(IEndpointRouteBuilder api)
    {
        var m = api.MapGroup("/merchant/shops/{shopId:guid}/backup").WithTags("Merchant").RequirePlantMember();

        m.MapGet("/", async (Guid shopId, MerchantContext ctx, CancellationToken ct) =>
        {
            var s = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            return new ShopBackup(CurrentVersion,
                new ProfileRequest(s.Name, s.Category, s.Description, s.LogoId, s.CoverId, s.HouseNo, s.Phone, s.LineContact),
                s.Hours.Select(h => new OpeningHourDto(h.Day, h.OpenAt, h.CloseAt)).ToList(),
                new OrderSettingsRequest(s.AcceptMode, s.AcceptTimeoutMinutes, s.ReminderAfterMinutes, s.AutoCompleteHours,
                    s.AllowPreorderWhenClosed, s.PrepTimeMinutes, s.SlotIntervalMinutes, s.RequirePaymentBeforePreparing),
                new DeliveryOptionsRequest(s.PickupEnabled, s.PickupInstruction, s.DeliveryEnabled, s.DeliveryZoneNote, s.DeliveryMinOrder),
                s.PaymentMethods.OrderBy(p => p.SortOrder).Select(p => new PaymentMethodRequest(p.Type, p.DisplayName, p.PromptPayId, p.QrImageId,
                    p.BankName, p.AccountNumber, p.AccountName, p.Instructions, p.ImageId, p.RequiresProof, p.Enabled)).ToList());
        });

        // Applies the saved settings. Payment methods are matched by type and name and never deleted.
        m.MapPost("/restore", async (Guid shopId, ShopBackup b, MerchantContext ctx, IMediaService media, CancellationToken ct) =>
        {
            var shop = await ctx.LoadAsync(shopId, ShopRole.Manager, ct);
            if (b.Version is < 1 or > CurrentVersion) throw new DomainException("backup_version", "This backup file is from an unsupported version.");
            if (b.PaymentMethods.Count > 10) throw new DomainException("validation", "At most 10 payment methods.");

            // Pictures are reused only when they are still the shop's or can be used by this user; otherwise the current ones stay.
            async Task<Guid?> PictureAsync(Guid? wanted, Guid? current, MediaPurpose purpose)
            {
                if (wanted is null || wanted == current) return wanted;
                try { await media.RequireOwnedAsync(wanted.Value, ctx.UserId, purpose, ct); return wanted; }
                catch (DomainException) { return current; }
                catch (ForbiddenException) { return current; }
            }
            var p = b.Profile;
            var logo = await PictureAsync(p.LogoId, shop.LogoId, MediaPurpose.ShopLogo);
            var cover = await PictureAsync(p.CoverId, shop.CoverId, MediaPurpose.ShopCover);
            shop.UpdateProfile(p.Name, p.Category, p.Description, logo, cover, p.HouseNo, p.Phone, p.LineContact);

            var previousHours = shop.Hours.Select(h => new OpeningHour(h.Day, h.OpenAt, h.CloseAt)).ToList();
            shop.SetHours(b.Hours.Select(h => new OpeningHour(h.Day, h.OpenAt, h.CloseAt)).ToList());
            var hoursRestored = true;
            if (shop.Hours.Count > 0)
            {
                // Same rule as saving hours by hand (e.g. the village requires a member who receives push).
                try { await ctx.EnsureCanOpenAsync(shop, ct); }
                catch (DomainException) { shop.SetHours(previousHours); hoursRestored = false; }
            }

            var o = b.Order;
            shop.UpdateOrderSettings(o.AcceptMode, o.AcceptTimeoutMinutes, o.ReminderAfterMinutes, o.AutoCompleteHours,
                o.AllowPreorderWhenClosed, o.PrepTimeMinutes, o.SlotIntervalMinutes, o.RequirePaymentBeforePreparing);
            var d = b.Delivery;
            shop.UpdateDeliveryOptions(d.PickupEnabled, d.PickupInstruction, d.DeliveryEnabled, d.DeliveryZoneNote, d.DeliveryMinOrder);

            int created = 0, updated = 0;
            var picturesKept = logo == p.LogoId && cover == p.CoverId;
            foreach (var r in b.PaymentMethods)
            {
                var method = shop.PaymentMethods.FirstOrDefault(m => m.Type == r.Type && string.Equals(m.DisplayName, r.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase));
                var request = r;
                try { await MerchantEndpoints.ValidateImagesAsync(r, method, ctx.UserId, media, ct); }
                catch (Exception e) when (e is DomainException or ForbiddenException)
                {
                    request = r with { QrImageId = method?.QrImageId, ImageId = method?.ImageId };
                    picturesKept = false;
                }

                if (method is null)
                {
                    if (shop.PaymentMethods.Count >= 10) throw new DomainException("validation", "At most 10 payment methods.");
                    method = PaymentMethod.Create(shop.Id, request.Type, request.DisplayName, MerchantEndpoints.Details(request), shop.PaymentMethods.Count);
                    shop.PaymentMethods.Add(method);
                    ctx.Db.PaymentMethods.Add(method);
                    created++;
                }
                else updated++;
                method.Update(request.DisplayName, MerchantEndpoints.Details(request), request.RequiresProof, request.Enabled);
            }
            MerchantEndpoints.EnsureOneEnabled(shop);

            await ctx.SaveAsync(shop, ct, true,
                new ShopProfileUpdated(shop.PlantId, shop.Id), new OpeningHoursChanged(shop.PlantId, shop.Id),
                new DeliveryOptionsUpdated(shop.PlantId, shop.Id), new PaymentMethodsUpdated(shop.PlantId, shop.Id));
            return new ShopRestoreResult(created, updated, hoursRestored, picturesKept);
        });
    }
}
