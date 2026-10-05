using SmartShop.Contracts.Shops;
using SmartShop.Infrastructure.Media;
using SmartShop.Modules.Shops.Domain;
using SmartShop.SharedKernel.Scheduling;

namespace SmartShop.Modules.Shops.Services;

public sealed record ShopStatusDto(
    ShopOpenState State, ShopClosedReason Reason, DateTimeOffset? Until, DateTimeOffset? NextOpenAt,
    bool AcceptingOrders, bool AcceptsPreorders);

public sealed record OpeningHourDto(DayOfWeek Day, TimeOnly OpenAt, TimeOnly CloseAt);

public sealed record ClosureDto(Guid Id, DateTimeOffset Start, DateTimeOffset End, string? Reason);

public sealed record PaymentMethodDto(
    Guid Id, PaymentMethodType Type, string DisplayName,
    string? PromptPayId, Guid? QrImageId, string? QrImageUrl,
    string? BankName, string? AccountNumber, string? AccountName,
    string? Instructions, Guid? ImageId, string? ImageUrl,
    bool RequiresProof, bool Enabled, int SortOrder);

public sealed record ShopDetailsDto(
    Guid Id, string Name, string Code, string? Category, string? Description,
    string? LogoUrl, string? CoverUrl, string? HouseNo, string? Phone, string? LineContact,
    ShopStatusDto Status, IReadOnlyList<OpeningHourDto> Hours, IReadOnlyList<ClosureDto> UpcomingClosures,
    string? VacationMessage,
    bool PickupEnabled, string? PickupInstruction, bool DeliveryEnabled, string? DeliveryZoneNote, decimal? DeliveryMinOrder,
    int PrepTimeMinutes, decimal RatingAverage, int RatingCount,
    IReadOnlyList<PaymentMethodDto> PaymentMethods, ShopRole? MyRole, bool IsFavorite);

public sealed record ShopSettingsDto(
    ShopDetailsDto Shop, Guid? LogoId, Guid? CoverId, ShopLifecycle Lifecycle, string? SuspendReason,
    AcceptMode AcceptMode, int AcceptTimeoutMinutes, int ReminderAfterMinutes, int AutoCompleteHours,
    bool AllowPreorderWhenClosed, int SlotIntervalMinutes, bool RequirePaymentBeforePreparing,
    OverrideMode OverrideMode, DateTimeOffset? OverrideUntil, DateTimeOffset? BusyUntil, DateTimeOffset? VacationUntil,
    bool VisibleWhileSuspended);

public sealed record MyShopDto(Guid Id, string Name, string Code, string? LogoUrl, ShopRole Role, ShopStatusDto Status, ShopLifecycle Lifecycle, bool ReceiveOrderNotifications);

internal static class ShopMapper
{
    public static ShopStatusDto Status(Shop shop, DateTimeOffset now)
    {
        var status = ShopScheduleEvaluator.Evaluate(shop.ToSchedule(), now);
        var preorders = !status.AcceptingOrders && shop.AllowPreorderWhenClosed
                        && status.Reason is not (ShopClosedReason.Suspended or ShopClosedReason.Vacation)
                        && status.State != ShopOpenState.Busy;
        return new ShopStatusDto(status.State, status.Reason, status.Until, status.NextOpenAt, status.AcceptingOrders, preorders);
    }

    public static PaymentMethodDto Payment(PaymentMethod p, IMediaUrls media) => new(
        p.Id, p.Type, p.DisplayName, p.PromptPayId, p.QrImageId, media.For(p.QrImageId),
        p.BankName, p.AccountNumber, p.AccountName, p.Instructions, p.ImageId, media.For(p.ImageId),
        p.RequiresProof, p.Enabled, p.SortOrder);

    public static ShopDetailsDto Details(Shop s, IMediaUrls media, DateTimeOffset now, Guid? viewerId, bool isFavorite, bool includeDisabledPayments = false) => new(
        s.Id, s.Name, s.Code, s.Category, s.Description,
        media.For(s.LogoId, MediaVariant.Small), media.For(s.CoverId), s.HouseNo, s.Phone, s.LineContact,
        Status(s, now),
        s.Hours.Select(h => new OpeningHourDto(h.Day, h.OpenAt, h.CloseAt)).ToList(),
        s.Closures.Where(c => c.End > now).OrderBy(c => c.Start).Select(c => new ClosureDto(c.Id, c.Start, c.End, c.Reason)).ToList(),
        s.VacationUntil > now ? s.VacationMessage : null,
        s.PickupEnabled, s.PickupInstruction, s.DeliveryEnabled, s.DeliveryZoneNote, s.DeliveryMinOrder,
        s.PrepTimeMinutes, s.RatingAverage, s.RatingCount,
        s.PaymentMethods.Where(p => includeDisabledPayments || p.Enabled).OrderBy(p => p.SortOrder).Select(p => Payment(p, media)).ToList(),
        viewerId is { } v ? s.Member(v)?.Role : null,
        isFavorite);

    public static ShopSettingsDto Settings(Shop s, IMediaUrls media, DateTimeOffset now, Guid viewerId) => new(
        Details(s, media, now, viewerId, false, includeDisabledPayments: true), s.LogoId, s.CoverId, s.Status, s.SuspendReason,
        s.AcceptMode, s.AcceptTimeoutMinutes, s.ReminderAfterMinutes, s.AutoCompleteHours,
        s.AllowPreorderWhenClosed, s.SlotIntervalMinutes, s.RequirePaymentBeforePreparing,
        s.OverrideMode, s.OverrideUntil, s.BusyUntil, s.VacationUntil, s.VisibleWhileSuspended);

    /// <summary>A, B, ... Z, AA, AB ... : short, readable prefixes for order numbers.</summary>
    public static string CodeFor(int index)
    {
        var code = "";
        index++;
        while (index > 0)
        {
            index--;
            code = (char)('A' + (index % 26)) + code;
            index /= 26;
        }
        return code;
    }
}
