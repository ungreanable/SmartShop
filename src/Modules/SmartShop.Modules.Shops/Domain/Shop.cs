using SmartShop.Contracts.Shops;
using SmartShop.SharedKernel;
using SmartShop.SharedKernel.Scheduling;

namespace SmartShop.Modules.Shops.Domain;

public enum ShopLifecycle { Active, Suspended }

public sealed class Shop
{
    private Shop() { }

    public Guid Id { get; private set; }
    public Guid PlantId { get; private set; }
    public string Name { get; private set; } = "";

    /// <summary>Short prefix for human friendly order numbers (A-023). Unique within the plant.</summary>
    public string Code { get; private set; } = "";

    public string? Category { get; private set; }
    public string? Description { get; private set; }
    public Guid? LogoId { get; private set; }
    public Guid? CoverId { get; private set; }
    public string? HouseNo { get; private set; }
    public string? Phone { get; private set; }
    public string? LineContact { get; private set; }
    public string TimeZoneId { get; private set; } = "Asia/Bangkok";
    public ShopLifecycle Status { get; private set; }
    public string? SuspendReason { get; private set; }

    /// <summary>While suspended: customers still see the shop (marked suspended, no ordering) instead of it disappearing.</summary>
    public bool VisibleWhileSuspended { get; private set; }

    /// <summary>Shown in the village shop list, search and the shop page.</summary>
    public bool IsListed => Status == ShopLifecycle.Active || VisibleWhileSuspended;

    // ---- open / closed ----
    public List<OpeningHour> Hours { get; private set; } = [];
    public List<ShopClosure> Closures { get; private set; } = [];
    public OverrideMode OverrideMode { get; private set; }
    public DateTimeOffset? OverrideUntil { get; private set; }
    public DateTimeOffset? BusyUntil { get; private set; }
    public DateTimeOffset? VacationUntil { get; private set; }
    public string? VacationMessage { get; private set; }


    // ---- ordering ----
    public AcceptMode AcceptMode { get; private set; } = AcceptMode.Manual;
    public int AcceptTimeoutMinutes { get; private set; } = 15;
    public int ReminderAfterMinutes { get; private set; } = 3;
    public int AutoCompleteHours { get; private set; } = 12;
    public bool AllowPreorderWhenClosed { get; private set; }
    public int PrepTimeMinutes { get; private set; } = 20;
    public int SlotIntervalMinutes { get; private set; } = 30;
    public bool RequirePaymentBeforePreparing { get; private set; }

    // ---- delivery options (no delivery fee inside the village) ----
    public bool PickupEnabled { get; private set; } = true;
    public string? PickupInstruction { get; private set; }
    public bool DeliveryEnabled { get; private set; }
    public string? DeliveryZoneNote { get; private set; }
    public decimal? DeliveryMinOrder { get; private set; }

    public decimal RatingAverage { get; private set; }
    public int RatingCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public uint Version { get; private set; }

    public List<ShopMember> Members { get; private set; } = [];
    public List<PaymentMethod> PaymentMethods { get; private set; } = [];

    public static Shop Create(Guid plantId, string code, string timeZoneId, Guid ownerId, ShopApplication application, DateTimeOffset now)
    {
        var shop = new Shop
        {
            Id = Ids.New(),
            PlantId = plantId,
            Code = code,
            Name = application.Name,
            Category = application.Category,
            Description = application.Description,
            HouseNo = application.HouseNo,
            Phone = application.Phone,
            TimeZoneId = timeZoneId,
            Status = ShopLifecycle.Active,
            CreatedAt = now,
        };
        shop.Members.Add(new ShopMember(shop.Id, ownerId, ShopRole.Owner, now));
        shop.PaymentMethods.Add(PaymentMethod.Create(shop.Id, PaymentMethodType.Cash, "เงินสด", new PaymentMethodDetails(), 0));
        return shop;
    }

    public void UpdateProfile(string name, string? category, string? description, Guid? logoId, Guid? coverId,
        string? houseNo, string? phone, string? lineContact)
    {
        Name = Guard.NotEmpty(name, "Shop name", 80);
        Category = Guard.Optional(category, "Category", 60);
        Description = Guard.Optional(description, "Description", 2000);
        LogoId = logoId;
        CoverId = coverId;
        HouseNo = Guard.Optional(houseNo, "House number", 30);
        Phone = Guard.Optional(phone, "Phone", 20);
        LineContact = Guard.Optional(lineContact, "LINE contact", 100);
    }

    public void SetHours(IReadOnlyList<OpeningHour> hours)
    {
        foreach (var day in hours.GroupBy(h => h.Day))
        {
            if (day.Count() > 4) throw new DomainException("validation", "At most 4 opening slots per day.");
            var ordered = day.OrderBy(h => h.OpenAt).ToList();
            for (var i = 1; i < ordered.Count; i++)
                if (ordered[i].OpenAt < ordered[i - 1].CloseAt && ordered[i - 1].CloseAt > ordered[i - 1].OpenAt)
                    throw new DomainException("hours_overlap", $"Opening slots on {day.Key} overlap.");
        }
        if (hours.Any(h => h.OpenAt == h.CloseAt))
            throw new DomainException("validation", "Opening and closing time cannot be equal.");
        Hours = [.. hours.OrderBy(h => h.Day).ThenBy(h => h.OpenAt)];
    }

    public ShopClosure AddClosure(DateTimeOffset start, DateTimeOffset end, string? reason, DateTimeOffset now)
    {
        if (end <= start) throw new DomainException("validation", "Closure end must be after its start.");
        if (end <= now) throw new DomainException("validation", "Closure must end in the future.");
        if (Closures.Count(c => c.End > now) >= 20) throw new DomainException("validation", "Too many upcoming closures.");
        var closure = new ShopClosure(Id, start, end, Guard.Optional(reason, "Reason", 200));
        Closures.Add(closure);
        return closure;
    }

    public void RemoveClosure(Guid closureId)
    {
        var closure = Closures.FirstOrDefault(c => c.Id == closureId) ?? throw new NotFoundException("Closure", closureId);
        Closures.Remove(closure);
    }

    /// <summary>Manual open/close button. <paramref name="until"/> null means "until I change it".</summary>
    public void SetOverride(OverrideMode mode, DateTimeOffset? until, DateTimeOffset now)
    {
        if (until is { } u && u <= now) throw new DomainException("validation", "The end time must be in the future.");
        OverrideMode = mode;
        OverrideUntil = mode == OverrideMode.None ? null : until;
        if (mode == OverrideMode.ForceClosed) BusyUntil = null;
    }

    public void SetBusy(int minutes, DateTimeOffset now)
    {
        if (minutes == 0) { BusyUntil = null; return; }
        Guard.Range(minutes, "Busy minutes", 5, 240);
        BusyUntil = now.AddMinutes(minutes);
    }

    public void StartVacation(DateTimeOffset? until, string? message, DateTimeOffset now)
    {
        if (until is { } u && u <= now) throw new DomainException("validation", "Vacation must end in the future.");
        VacationUntil = until ?? DateTimeOffset.MaxValue;
        VacationMessage = Guard.Optional(message, "Message", 200);
    }

    public void EndVacation()
    {
        VacationUntil = null;
        VacationMessage = null;
    }

    public void UpdateOrderSettings(AcceptMode acceptMode, int acceptTimeoutMinutes, int reminderAfterMinutes, int autoCompleteHours,
        bool allowPreorderWhenClosed, int prepTimeMinutes, int slotIntervalMinutes, bool requirePaymentBeforePreparing)
    {
        AcceptMode = acceptMode;
        AcceptTimeoutMinutes = Guard.Range(acceptTimeoutMinutes, "Accept timeout", 5, 240);
        ReminderAfterMinutes = Guard.Range(reminderAfterMinutes, "Reminder", 1, 60);
        if (ReminderAfterMinutes >= AcceptTimeoutMinutes)
            throw new DomainException("validation", "The reminder must happen before the order expires.");
        AutoCompleteHours = Guard.Range(autoCompleteHours, "Auto-complete hours", 1, 168);
        AllowPreorderWhenClosed = allowPreorderWhenClosed;
        PrepTimeMinutes = Guard.Range(prepTimeMinutes, "Preparation time", 0, 24 * 60);
        SlotIntervalMinutes = Guard.Range(slotIntervalMinutes, "Slot interval", 10, 240);
        RequirePaymentBeforePreparing = requirePaymentBeforePreparing;
    }

    public void UpdateDeliveryOptions(bool pickupEnabled, string? pickupInstruction, bool deliveryEnabled, string? deliveryZoneNote, decimal? deliveryMinOrder)
    {
        if (!pickupEnabled && !deliveryEnabled)
            throw new DomainException("delivery_option_required", "Enable at least one option: pickup or delivery.");
        PickupEnabled = pickupEnabled;
        PickupInstruction = Guard.Optional(pickupInstruction, "Pickup instruction", 300);
        DeliveryEnabled = deliveryEnabled;
        DeliveryZoneNote = Guard.Optional(deliveryZoneNote, "Delivery zone", 300);
        DeliveryMinOrder = deliveryMinOrder is { } min && min > 0 ? Guard.Money(min, "Minimum order") : null;
    }

    public void Suspend(string? reason, bool visible = false)
    {
        if (Status == ShopLifecycle.Suspended) throw new ConflictException("shop_state", "The shop is already suspended.");
        Status = ShopLifecycle.Suspended;
        SuspendReason = Guard.Optional(reason, "Reason", 500);
        VisibleWhileSuspended = visible;
    }

    public void SetVisibleWhileSuspended(bool visible)
    {
        if (Status != ShopLifecycle.Suspended) throw new ConflictException("shop_state", "The shop is not suspended.");
        VisibleWhileSuspended = visible;
    }

    public void Reinstate()
    {
        if (Status != ShopLifecycle.Suspended) throw new ConflictException("shop_state", "The shop is not suspended.");
        Status = ShopLifecycle.Active;
        SuspendReason = null;
        VisibleWhileSuspended = false;
    }

    public void UpdateRating(decimal average, int count)
    {
        RatingAverage = decimal.Round(average, 2);
        RatingCount = count;
    }



    public ShopScheduleSnapshot ToSchedule() => new(
        TimeZoneId,
        Hours.Select(h => new OpeningSlot(h.Day, h.OpenAt, h.CloseAt)).ToList(),
        Closures.Select(c => new ClosureWindow(c.Start, c.End, c.Reason)).ToList(),
        OverrideMode, OverrideUntil, BusyUntil, Status == ShopLifecycle.Suspended, VacationUntil);

    // ---- members ----
    public ShopMember? Member(Guid userId) => Members.FirstOrDefault(m => m.UserId == userId);

    public ShopMember AddMember(Guid userId, ShopRole role, DateTimeOffset now)
    {
        if (role == ShopRole.Owner) throw new DomainException("validation", "Use ownership transfer to change the owner.");
        if (Member(userId) is not null) throw new ConflictException("already_shop_member", "This person is already a member of the shop.");
        if (Members.Count >= 10) throw new DomainException("too_many_members", "A shop can have at most 10 members.");
        var member = new ShopMember(Id, userId, role, now);
        Members.Add(member);
        return member;
    }

    public void ChangeRole(Guid actorId, Guid userId, ShopRole role)
    {
        var member = Member(userId) ?? throw new NotFoundException("Shop member", userId);
        if (member.Role == ShopRole.Owner || role == ShopRole.Owner)
            throw new DomainException("owner_role", "Use ownership transfer to change the owner.");
        if (actorId == userId) throw new DomainException("validation", "You cannot change your own role.");
        member.ChangeRole(role);
    }

    public void RemoveMember(Guid actorId, Guid userId)
    {
        var member = Member(userId) ?? throw new NotFoundException("Shop member", userId);
        if (member.Role == ShopRole.Owner) throw new DomainException("owner_role", "The owner cannot be removed. Transfer ownership first.");
        var actor = Member(actorId);
        if (actorId != userId && (actor is null || actor.Role <= member.Role && actor.Role != ShopRole.Owner))
            throw new ForbiddenException("You cannot remove a member with the same or a higher role.");
        Members.Remove(member);
    }

    public void SetNotifications(Guid userId, bool enabled)
    {
        var member = Member(userId) ?? throw new NotFoundException("Shop member", userId);
        if (!enabled && !Members.Any(m => m.ReceiveOrderNotifications && m.UserId != userId))
            throw new DomainException("last_notification_recipient",
                "At least one member must keep order notifications on, otherwise new orders could be missed.");
        member.SetNotifications(enabled);
    }

    public void TransferOwnership(Guid fromUserId, Guid toUserId)
    {
        var from = Member(fromUserId);
        if (from?.Role != ShopRole.Owner) throw new ForbiddenException("Only the owner can transfer ownership.");
        var to = Member(toUserId) ?? throw new DomainException("validation", "The new owner must already be a member of the shop.");
        from.ChangeRole(ShopRole.Manager);
        to.ChangeRole(ShopRole.Owner);
    }

    public Guid OwnerId => Members.Single(m => m.Role == ShopRole.Owner).UserId;
}

public sealed class OpeningHour
{
    private OpeningHour() { }

    public OpeningHour(DayOfWeek day, TimeOnly openAt, TimeOnly closeAt)
    {
        Day = day;
        OpenAt = openAt;
        CloseAt = closeAt;
    }

    public DayOfWeek Day { get; private set; }
    public TimeOnly OpenAt { get; private set; }
    public TimeOnly CloseAt { get; private set; }
}

public sealed class ShopClosure
{
    private ShopClosure() { }

    public ShopClosure(Guid shopId, DateTimeOffset start, DateTimeOffset end, string? reason)
    {
        Id = Ids.New();
        ShopId = shopId;
        Start = start;
        End = end;
        Reason = reason;
    }

    public Guid Id { get; private set; }
    public Guid ShopId { get; private set; }
    public DateTimeOffset Start { get; private set; }
    public DateTimeOffset End { get; private set; }
    public string? Reason { get; private set; }
}

public sealed class ShopMember
{
    private ShopMember() { }

    public ShopMember(Guid shopId, Guid userId, ShopRole role, DateTimeOffset now)
    {
        ShopId = shopId;
        UserId = userId;
        Role = role;
        ReceiveOrderNotifications = true;
        JoinedAt = now;
    }

    public Guid ShopId { get; private set; }
    public Guid UserId { get; private set; }
    public ShopRole Role { get; private set; }
    public bool ReceiveOrderNotifications { get; private set; }

    /// <summary>Optional label such as "แม่ (เครื่อง 2)" shown in the order timeline.</summary>
    public string? DisplayLabel { get; private set; }

    public DateTimeOffset JoinedAt { get; private set; }

    public void ChangeRole(ShopRole role) => Role = role;
    public void SetNotifications(bool enabled) => ReceiveOrderNotifications = enabled;
    public void SetLabel(string? label) => DisplayLabel = Guard.Optional(label, "Label", 40);
}

/// <summary>Single-use invitation (QR/link) for a plant member to join a shop.</summary>
public sealed class ShopInvite
{
    private ShopInvite() { }

    public ShopInvite(Guid shopId, ShopRole role, Guid createdBy, DateTimeOffset now)
    {
        if (role == ShopRole.Owner) throw new DomainException("validation", "Cannot invite an owner.");
        Id = Ids.New();
        ShopId = shopId;
        Role = role;
        Code = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
        CreatedBy = createdBy;
        CreatedAt = now;
        ExpiresAt = now.AddHours(24);
    }

    public Guid Id { get; private set; }
    public Guid ShopId { get; private set; }
    public string Code { get; private set; } = "";
    public ShopRole Role { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public Guid? AcceptedBy { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }

    public void Accept(Guid userId, DateTimeOffset now)
    {
        if (AcceptedBy is not null) throw new DomainException("invite_used", "This invitation has already been used.");
        if (now > ExpiresAt) throw new DomainException("invite_expired", "This invitation has expired. Ask for a new one.");
        AcceptedBy = userId;
        AcceptedAt = now;
    }
}

public sealed class ShopFavorite
{
    private ShopFavorite() { }

    public ShopFavorite(Guid userId, Guid shopId, DateTimeOffset now)
    {
        UserId = userId;
        ShopId = shopId;
        CreatedAt = now;
    }

    public Guid UserId { get; private set; }
    public Guid ShopId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>
/// Bookkeeping of the status evaluator, kept apart from <see cref="Shop"/> so background evaluations never
/// conflict (optimistic concurrency) with edits made by shop members.
/// </summary>
public sealed class ShopStatusTracker
{
    private ShopStatusTracker() { }

    public ShopStatusTracker(Guid shopId) => ShopId = shopId;

    public Guid ShopId { get; private set; }

    /// <summary>Last state announced through <c>ShopStatusChanged</c>; used to detect transitions.</summary>
    public ShopOpenState? LastAnnouncedState { get; private set; }

    /// <summary>Boundary for which a re-evaluation is already scheduled (avoids piling up duplicate timers).</summary>
    public DateTimeOffset? NextEvaluationAt { get; private set; }

    public void MarkAnnounced(ShopOpenState state) => LastAnnouncedState = state;

    /// <summary>Returns true when a new timer must be scheduled for <paramref name="boundary"/>.</summary>
    public bool ScheduleEvaluation(DateTimeOffset? boundary)
    {
        if (boundary == NextEvaluationAt) return false;
        NextEvaluationAt = boundary;
        return boundary is not null;
    }
}
