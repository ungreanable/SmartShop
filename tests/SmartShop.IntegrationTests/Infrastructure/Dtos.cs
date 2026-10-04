namespace SmartShop.IntegrationTests.Infrastructure;

// Client-side views of API responses (deliberately independent of server types, like a real client).

public sealed record ApplicationDto(Guid Id, string Name, string Status, string? ReviewNote, Guid? ShopId, string? ApplicantName, List<string?> SampleImageUrls);
public sealed record ApprovedDto(Guid ShopId, string Code);
public sealed record ShopStatusDto(string State, string Reason, DateTimeOffset? Until, DateTimeOffset? NextOpenAt, bool AcceptingOrders, bool AcceptsPreorders);
public sealed record MyShopDto(Guid Id, string Name, string Code, string Role, ShopStatusDto Status);
public sealed record PaymentMethodDto(Guid Id, string Type, string DisplayName, string? PromptPayId, string? BankName, string? AccountNumber, bool RequiresProof, bool Enabled);
public sealed record OpeningHourDto(string Day, string OpenAt, string CloseAt);
public sealed record ShopDetailsDto(Guid Id, string Name, string Code, ShopStatusDto Status, List<OpeningHourDto> Hours,
    bool PickupEnabled, bool DeliveryEnabled, string? DeliveryZoneNote, decimal? DeliveryMinOrder,
    List<PaymentMethodDto> PaymentMethods, string? MyRole, bool IsFavorite, decimal RatingAverage, int RatingCount);
public sealed record ShopSettingsDto(ShopDetailsDto Shop, string AcceptMode, int AcceptTimeoutMinutes, bool AllowPreorderWhenClosed);
public sealed record WindowDto(DateTimeOffset Start, DateTimeOffset End);
public sealed record InviteDto(string Code, string Role, DateTimeOffset ExpiresAt, string Url);
public sealed record AcceptedInviteDto(Guid ShopId, string ShopName, string Role);
public sealed record ShopMemberDto(Guid UserId, string DisplayName, string Role, bool ReceiveOrderNotifications);
