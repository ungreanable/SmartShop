namespace SmartShop.UI.Services;

// Client-side views of API responses. Enums travel as strings, keeping the client tolerant to new values.

public sealed record PagedResult<T>(List<T> Items, int Total, int Page, int PageSize);
public sealed record UploadResult(Guid Id, string ContentType, int? Width, int? Height, string? Url, string? ThumbnailUrl);

// ---- identity / villages ----
public sealed record Me(Guid Id, string DisplayName, string? PictureUrl, string? Phone, string Locale, bool IsSystemAdmin,
    string? ConsentVersion, DateTimeOffset? ConsentAt, List<string> LinkedProviders);
public sealed record LineConfig(string ChannelId, string LiffId, string AuthorizeUrl);
public sealed record MyMembership(Guid PlantId, string PlantName, string? PictureUrl, string Role, string Status, string? HouseNo, string? Soi,
    string? Nickname, string? DecisionReason)
{
    public bool IsActive => Status == "Active";
    public bool IsAdmin => Role == "PlantAdmin";
}
public sealed record JoinPreview(Guid PlantId, string Name, string? PictureUrl, string? Description);
public sealed record PlantDetails(Guid Id, string Name, string? Description, string? PictureUrl, Guid? PictureId, string TimeZoneId, string PushRequirement, MyMembership Me);
public sealed record PlantSummary(Guid Id, string Name, string JoinCode, string TimeZoneId, int ActiveMembers, int PendingMembers, DateTimeOffset CreatedAt);
public sealed record MemberRow(Guid MembershipId, Guid UserId, string DisplayName, string? PictureUrl, string Role, string Status,
    string? HouseNo, string? Soi, string? Nickname, string? RequestMessage, string? DecisionReason, DateTimeOffset RequestedAt);
public sealed record PlantCounts(int Active, int Pending, int Suspended, int Admins);
public sealed record JoinLink(string JoinCode, string Url);
public sealed record Announcement(Guid Id, string Title, string? Body, string? ImageUrl, Guid? ImageId, DateTimeOffset StartsAt, DateTimeOffset? EndsAt,
    bool IsPinned, DateTimeOffset CreatedAt);

// ---- shops ----
public sealed record ShopStatus(string State, string Reason, DateTimeOffset? Until, DateTimeOffset? NextOpenAt, bool AcceptingOrders, bool AcceptsPreorders = false);
public sealed record OpeningHour(string Day, string OpenAt, string CloseAt);
public sealed record Closure(Guid Id, DateTimeOffset Start, DateTimeOffset End, string? Reason);
public sealed record PaymentMethod(Guid Id, string Type, string DisplayName, string? PromptPayId, Guid? QrImageId, string? QrImageUrl,
    string? BankName, string? AccountNumber, string? AccountName, string? Instructions, Guid? ImageId, string? ImageUrl,
    bool RequiresProof, bool Enabled, int SortOrder);
public sealed record ShopDetails(Guid Id, string Name, string Code, string? Category, string? Description, string? LogoUrl, string? CoverUrl,
    string? HouseNo, string? Phone, string? LineContact, ShopStatus Status, List<OpeningHour> Hours, List<Closure> UpcomingClosures,
    string? VacationMessage, bool PickupEnabled, string? PickupInstruction, bool DeliveryEnabled, string? DeliveryZoneNote,
    decimal? DeliveryMinOrder, int PrepTimeMinutes, decimal RatingAverage, int RatingCount, List<PaymentMethod> PaymentMethods,
    string? MyRole, bool IsFavorite);
public sealed record ShopSettings(ShopDetails Shop, Guid? LogoId, Guid? CoverId, string Lifecycle, string? SuspendReason, string AcceptMode,
    int AcceptTimeoutMinutes, int ReminderAfterMinutes, int AutoCompleteHours, bool AllowPreorderWhenClosed, int SlotIntervalMinutes,
    bool RequirePaymentBeforePreparing, string OverrideMode, DateTimeOffset? OverrideUntil, DateTimeOffset? BusyUntil, DateTimeOffset? VacationUntil);
public sealed record MyShop(Guid Id, string Name, string Code, string? LogoUrl, string Role, ShopStatus Status, string Lifecycle, bool ReceiveOrderNotifications);
public sealed record TimeWindow(DateTimeOffset Start, DateTimeOffset End);
public sealed record ShopApplication(Guid Id, string Name, string? Category, string? Description, string? HouseNo, string? Phone,
    List<Guid> SampleImageIds, List<string?> SampleImageUrls, string Status, string? ReviewNote, DateTimeOffset SubmittedAt,
    DateTimeOffset? ReviewedAt, Guid? ShopId, Guid ApplicantId, string? ApplicantName, string? ApplicantPictureUrl);
public sealed record AdminShopRow(Guid Id, string Name, string Code, string? LogoUrl, string Lifecycle, string? SuspendReason, ShopStatus Status,
    Guid OwnerId, int MemberCount, decimal RatingAverage, int RatingCount, DateTimeOffset CreatedAt);
public sealed record ShopMember(Guid UserId, string DisplayName, string? PictureUrl, string Role, bool ReceiveOrderNotifications, string? DisplayLabel, DateTimeOffset JoinedAt);
public sealed record Invite(string Code, string Role, DateTimeOffset ExpiresAt, string Url);
public sealed record AcceptedInvite(Guid ShopId, string ShopName, string Role);
public sealed record MemberChannel(Guid UserId, string DisplayName, string? DisplayLabel, bool ReceiveOrderNotifications, bool LineFriend,
    int WebPushDevices, int MobileDevices, bool HasPush);
public sealed record NotificationHealth(string Requirement, bool AnyRecipientHasPush, List<MemberChannel> Members);

// ---- home / search ----
public sealed record ShopCard(Guid Id, string Name, string? Category, string? Description, string? LogoUrl, string? CoverUrl, string? HouseNo,
    ShopStatus Status, bool PickupEnabled, bool DeliveryEnabled, bool AcceptsPreorders, int PrepTimeMinutes, decimal RatingAverage, int RatingCount, bool IsNew);
public sealed record HomeFeed(List<string> Categories, List<ShopCard> OpenNow, List<ShopCard> Shops);
public sealed record ItemHit(Guid ItemId, Guid ShopId, string ShopName, string Name, decimal Price, string? ThumbnailUrl, bool IsSoldOut);
public sealed record SearchResult(List<ShopCard> Shops, List<ItemHit> Items);

// ---- catalog ----
public sealed record Window(List<string> Days, string From, string To);
public sealed record ModifierOption(Guid Id, string Name, decimal PriceDelta, bool IsAvailable);
public sealed record ModifierGroup(Guid Id, string Name, int MinSelect, int MaxSelect, List<ModifierOption> Options);
public sealed record Round(Guid Id, string Title, string? Description, DateTimeOffset OrderCutoff, DateTimeOffset FulfillFrom, DateTimeOffset FulfillTo,
    int? MinTotalQuantity, int OrderedQuantity, string Status);
public sealed record MenuItem(Guid Id, Guid? CategoryId, string Kind, string StockMode, string Name, string? Description, decimal Price,
    List<string?> ImageUrls, string? ThumbnailUrl, bool IsOrderable, string? UnavailableReason, int? Remaining, int? MaxPerOrder,
    int? DurationMinutes, List<Window> Windows, DateOnly? SaleFrom, DateOnly? SaleTo, List<string>? AllowedFulfillment,
    List<ModifierGroup> ModifierGroups, Round? Round, bool IsRecommended);
public sealed record MenuCategory(Guid? Id, string Name, List<MenuItem> Items);
public sealed record Menu(Guid ShopId, List<MenuCategory> Categories);
public sealed record Category(Guid Id, string Name, int SortOrder);
public sealed record MerchantItem(MenuItem Item, bool IsAvailable, bool IsSoldOut, List<Guid> ImageIds, int SortOrder, int? OnHand, int? Reserved,
    int? DailyQuota, string? DailyResetTime, int? LowStockThreshold, int? SlotCapacity);
public sealed record StockResult(int OnHand, int Reserved, int Available);
public sealed record Movement(Guid Id, string Type, int Quantity, int OnHandAfter, int ReservedAfter, Guid? OrderId, string? Note, DateTimeOffset At);
public sealed record Slot(DateTimeOffset Start, DateTimeOffset End, int Remaining);

// ---- cart / orders ----
public sealed record Option(string Group, string Name, decimal PriceDelta);
public sealed record CartLine(Guid Id, Guid ItemId, string Name, string? ThumbnailUrl, int Quantity, decimal UnitPrice, List<Guid> OptionIds,
    List<Option> Options, decimal LineTotal, string? Note, DateTimeOffset? SlotStart, bool IsOrderable, string? Problem, int? Remaining);
public sealed record Cart(Guid? ShopId, string? ShopName, List<CartLine> Lines, decimal Subtotal, int ItemCount, bool CanCheckout);
public sealed record Address(string? HouseNo, string? Soi, string? Note);
public sealed record OrderLine(Guid ItemId, string Name, int Quantity, decimal UnitPrice, List<Option> Options, decimal LineTotal, string? Note, DateTimeOffset? SlotStart);
public sealed record Person(Guid Id, string Name, string? PictureUrl, string? Phone);
public sealed record TimelineEntry(string Type, string? ActorName, string? Note, string? PhotoUrl, DateTimeOffset At);
public sealed record Order(Guid Id, string OrderNo, string Status, Guid ShopId, string ShopName, Person Customer, string FulfillmentType,
    DateTimeOffset? ScheduledFrom, DateTimeOffset? ScheduledTo, Address? DeliveryAddress, string? Note, List<OrderLine> Lines,
    decimal Subtotal, decimal Discount, decimal Total, string? PromotionCode, Guid PaymentMethodId, string PaymentMethodType,
    string PaymentMethodName, string PaymentStatus, string? AcceptedByName, string? CancelReason, string? CancelRequestReason,
    DateTimeOffset? CancelRequestedAt, string? DeliveryPhotoUrl, DateTimeOffset PlacedAt, DateTimeOffset? ExpiresAt,
    DateTimeOffset? AutoCompleteAt, DateTimeOffset? CompletedAt, List<TimelineEntry> Timeline, Guid? PreOrderRoundId, string ViewerRole);
public sealed record OrderSummary(Guid Id, string OrderNo, string Status, Guid ShopId, string ShopName, string CustomerName, string? CustomerHouseNo,
    string FulfillmentType, DateTimeOffset? ScheduledFrom, DateTimeOffset? ScheduledTo, int ItemCount, string ItemsPreview, decimal Total,
    string PaymentMethodType, string PaymentStatus, DateTimeOffset PlacedAt, DateTimeOffset? ExpiresAt, bool CancelRequested);
public sealed record OrderCounts(int New, int Active, int Ready, int Today);
public sealed record PrepItem(Guid ItemId, string Name, string Options, int Quantity, int Orders);
public sealed record SalesDay(DateOnly Day, int Orders, decimal Revenue);
public sealed record TopItem(string Name, int Quantity, decimal Revenue);
public sealed record SalesReport(DateOnly From, DateOnly To, int Orders, decimal Revenue, decimal AverageOrder, int Cancelled, List<SalesDay> Days, List<TopItem> TopItems);
public sealed record ChatMessage(Guid Id, Guid SenderId, string SenderName, bool FromShop, string? Body, string? ImageUrl, DateTimeOffset SentAt);
public sealed record OrderReport(Guid Id, Guid OrderId, string OrderNo, Guid ShopId, string ShopName, Guid ReporterId, string ReporterName,
    string Reason, string Status, string? Resolution, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt);

// ---- payments ----
public sealed record Proof(Guid Id, string? Url, string? ThumbnailUrl, string ContentType, DateTimeOffset UploadedAt, string? DuplicateOfOrderNo, bool HasSlipReference);
public sealed record Payment(Guid Id, Guid OrderId, string OrderNo, decimal Amount, string MethodType, string DisplayName, string? PromptPayId,
    string? PromptPayQrDataUrl, string? QrImageUrl, string? BankName, string? AccountNumber, string? AccountName, string? Instructions,
    string? ImageUrl, bool RequiresProof, string Status, string? RejectReason, DateTimeOffset? VerifiedAt, bool RefundRequired,
    List<Proof> Proofs, bool CanUploadProof, bool CanVerify);

// ---- notifications ----
public sealed record NotificationItem(Guid Id, Guid? PlantId, string Type, string Priority, string Title, string Body, string? Link, DateTimeOffset CreatedAt, bool Read);
public sealed record Device(Guid Id, string Kind, string? Label, DateTimeOffset LastSeenAt);
public sealed record Channels(bool LineConfigured, bool LineFriend, string? LineAddFriendUrl, bool WebPushConfigured, string? VapidPublicKey,
    bool FcmConfigured, List<Device> Devices, bool HasPush);
public sealed record NotificationSettings(string PushMode, List<string> LineMuted, List<string> PushMuted, string? QuietFrom, string? QuietTo);

// ---- reviews / promotions (phase 2/3) ----
public sealed record Review(Guid Id, Guid OrderId, Guid CustomerId, string CustomerName, string? CustomerPictureUrl, int Rating, string? Comment,
    string? Reply, DateTimeOffset CreatedAt, DateTimeOffset? RepliedAt, bool Hidden);
public sealed record ReviewSummary(decimal Average, int Count, Dictionary<int, int> Distribution, List<Review> Reviews);
public sealed record Promotion(Guid Id, string? Code, string Title, string Type, decimal Value, decimal? MinOrder, decimal? MaxDiscount,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int? UsageLimit, int? PerUserLimit, int UsedCount, bool AutoApply, bool Active);
public sealed record PromotionBadge(Guid Id, string Title, string Description, decimal? MinOrder, DateTimeOffset? EndsAt);
public sealed record DiscountPreview(Guid PromotionId, string? Code, decimal Discount, string Description);
