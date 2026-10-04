using System.Globalization;
using SmartShop.Contracts.Catalog;
using SmartShop.Contracts.Identity;
using SmartShop.Contracts.Ordering;
using SmartShop.Contracts.Payments;
using SmartShop.Contracts.Plants;
using SmartShop.Contracts.Reviews;
using SmartShop.Contracts.Shops;
using SmartShop.Modules.Notifications.Domain;
using SmartShop.Modules.Notifications.Services;
using P = SmartShop.Modules.Notifications.Domain.NotificationPriority;

namespace SmartShop.Modules.Notifications.Handlers;

internal static class Links
{
    public static string CustomerOrder(Guid orderId) => $"/orders/{orderId}";
    public static string ShopOrder(Guid shopId, Guid orderId) => $"/merchant/{shopId}/orders/{orderId}";
    public static string Shop(Guid shopId) => $"/shops/{shopId}";
    public static string Baht(decimal amount) => "฿" + amount.ToString("#,0.##", CultureInfo.InvariantCulture);
}

/// <summary>Who hears about which order event (see docs/01-features.md, notification matrix).</summary>
public static class OrderNotificationHandler
{
    // ---- to every shop member who has order notifications switched on ----
    public static async Task Handle(OrderPlaced e, NotificationDispatcher d, IShopDirectory shops, IUserDirectory users, CancellationToken ct)
    {
        var customer = (await users.GetProfilesAsync([e.CustomerId], ct)).GetValueOrDefault(e.CustomerId)?.DisplayName ?? "ลูกค้า";
        var when = e.ScheduledFrom is { } f ? $" · รับ {f.ToOffset(TimeSpan.FromHours(7)):dd/MM HH:mm}" : "";
        var how = e.FulfillmentType == FulfillmentType.Delivery ? "ให้ร้านไปส่ง" : "รับที่ร้าน";
        await d.NotifyAsync(await shops.GetOrderNotificationRecipientsAsync(e.ShopId, ct), e.PlantId, e.EventId,
            new("order.new", P.High, $"🛎️ ออเดอร์ใหม่ #{e.OrderNo}",
                $"{customer} · {e.Lines.Sum(l => l.Quantity)} รายการ · {Links.Baht(e.Total)} · {how}{when}",
                Links.ShopOrder(e.ShopId, e.OrderId), ExemptFromQuietHours: true), ct);
    }

    public static async Task Handle(OrderPendingReminder e, NotificationDispatcher d, IShopDirectory shops, CancellationToken ct) =>
        await d.NotifyAsync(await shops.GetOrderNotificationRecipientsAsync(e.ShopId, ct), e.PlantId, e.EventId,
            new("order.reminder", P.High, $"⏰ ยังไม่มีใครรับออเดอร์ #{e.OrderNo}", "ลูกค้ากำลังรออยู่ กรุณากดรับหรือปฏิเสธออเดอร์",
                Links.ShopOrder(e.ShopId, e.OrderId), ExemptFromQuietHours: true), ct);

    public static async Task Handle(OrderCancellationRequested e, NotificationDispatcher d, IShopDirectory shops, CancellationToken ct) =>
        await d.NotifyAsync(await shops.GetOrderNotificationRecipientsAsync(e.ShopId, ct), e.PlantId, e.EventId,
            new("order.cancel_requested", P.High, $"ลูกค้าขอยกเลิกออเดอร์ #{e.OrderNo}", e.Reason ?? "กรุณาตรวจสอบและตอบกลับ",
                Links.ShopOrder(e.ShopId, e.OrderId)), ct);

    // ---- to the customer ----
    public static Task Handle(OrderAccepted e, NotificationDispatcher d, CancellationToken ct) =>
        Customer(d, e.CustomerId, e.PlantId, e.EventId, e.OrderId, "order.accepted", P.High, $"✅ {e.ShopName} รับออเดอร์ #{e.OrderNo} แล้ว", "ร้านกำลังเตรียมให้ค่ะ", ct);

    public static Task Handle(OrderRejected e, NotificationDispatcher d, CancellationToken ct) =>
        Customer(d, e.CustomerId, e.PlantId, e.EventId, e.OrderId, "order.rejected", P.High, $"❌ {e.ShopName} ไม่สามารถรับออเดอร์ #{e.OrderNo}", e.Reason, ct);

    public static Task Handle(OrderExpired e, NotificationDispatcher d, CancellationToken ct) =>
        Customer(d, e.CustomerId, e.PlantId, e.EventId, e.OrderId, "order.expired", P.High, $"ออเดอร์ #{e.OrderNo} หมดเวลา", $"{e.ShopName} ไม่ได้ตอบรับในเวลาที่กำหนด", ct);

    public static async Task Handle(OrderCancelled e, NotificationDispatcher d, IShopDirectory shops, CancellationToken ct)
    {
        if (e.ByCustomer)
            await d.NotifyAsync(await shops.GetOrderNotificationRecipientsAsync(e.ShopId, ct), e.PlantId, e.EventId,
                new("order.cancelled", P.Medium, $"ลูกค้ายกเลิกออเดอร์ #{e.OrderNo}", e.Reason ?? "-", Links.ShopOrder(e.ShopId, e.OrderId)), ct);
        else
            await Customer(d, e.CustomerId, e.PlantId, e.EventId, e.OrderId, "order.cancelled", P.High, $"ออเดอร์ #{e.OrderNo} ถูกยกเลิก", e.Reason ?? e.ShopName, ct);
    }

    public static Task Handle(OrderPreparing e, NotificationDispatcher d, CancellationToken ct) =>
        Customer(d, e.CustomerId, e.PlantId, e.EventId, e.OrderId, "order.preparing", P.Medium, $"👩‍🍳 {e.ShopName} กำลังเตรียมออเดอร์ #{e.OrderNo}", "อีกสักครู่นะคะ", ct);

    public static Task Handle(OrderReady e, NotificationDispatcher d, CancellationToken ct) =>
        Customer(d, e.CustomerId, e.PlantId, e.EventId, e.OrderId, "order.ready", P.Medium, $"📦 ออเดอร์ #{e.OrderNo} พร้อมแล้ว",
            e.FulfillmentType == FulfillmentType.Pickup ? $"มารับได้ที่ {e.ShopName} เลยค่ะ" : "ร้านกำลังจะไปส่ง", ct);

    public static Task Handle(OrderOutForDelivery e, NotificationDispatcher d, CancellationToken ct) =>
        Customer(d, e.CustomerId, e.PlantId, e.EventId, e.OrderId, "order.out_for_delivery", P.Medium, $"🛵 {e.ShopName} กำลังไปส่งออเดอร์ #{e.OrderNo}", "เตรียมรับของได้เลย", ct);

    public static Task Handle(OrderDelivered e, NotificationDispatcher d, CancellationToken ct) =>
        Customer(d, e.CustomerId, e.PlantId, e.EventId, e.OrderId, "order.delivered", P.High, $"ร้านส่งออเดอร์ #{e.OrderNo} แล้ว",
            $"ได้รับของแล้วกด \"ยืนยันได้รับ\" ด้วยนะคะ (ระบบจะยืนยันให้อัตโนมัติใน {e.AutoCompleteHours} ชม.)", ct);

    public static Task Handle(OrderCompleted e, NotificationDispatcher d, CancellationToken ct) =>
        e.Automatic
            ? Customer(d, e.CustomerId, e.PlantId, e.EventId, e.OrderId, "order.completed", P.Low, $"ออเดอร์ #{e.OrderNo} เสร็จสมบูรณ์", "ให้คะแนนร้านได้นะคะ ⭐", ct)
            : Task.CompletedTask;

    // ---- conversation and reports ----
    public static async Task Handle(OrderMessagePosted e, NotificationDispatcher d, IShopDirectory shops, CancellationToken ct)
    {
        if (e.FromShop)
            await Customer(d, e.CustomerId, e.PlantId, e.EventId, e.OrderId, "order.message", P.Medium, $"💬 ข้อความจากร้าน (#{e.OrderNo})", e.Preview, ct);
        else
            await d.NotifyAsync((await shops.GetOrderNotificationRecipientsAsync(e.ShopId, ct)).Where(u => u != e.SenderId), e.PlantId, e.EventId,
                new("order.message", P.Medium, $"💬 ข้อความจากลูกค้า (#{e.OrderNo})", e.Preview, Links.ShopOrder(e.ShopId, e.OrderId)), ct);
    }

    public static async Task Handle(OrderReported e, NotificationDispatcher d, IPlantDirectory plants, CancellationToken ct) =>
        await d.NotifyAsync(await plants.GetActiveAdminIdsAsync(e.PlantId, ct), e.PlantId, e.EventId,
            new("admin.report", P.Medium, $"🚩 มีการรายงานปัญหาออเดอร์ #{e.OrderNo}", e.Reason, "/admin/reports"), ct);

    public static Task Handle(OrderReportResolved e, NotificationDispatcher d, CancellationToken ct) =>
        d.NotifyAsync([e.ReporterId], e.PlantId, e.EventId, new("report.resolved", P.Medium, "ผู้ดูแลหมู่บ้านตอบกลับการรายงานแล้ว", e.Resolution, Links.CustomerOrder(e.OrderId)), ct);

    private static Task Customer(NotificationDispatcher d, Guid customerId, Guid plantId, Guid eventId, Guid orderId, string type, P priority,
        string title, string body, CancellationToken ct) =>
        d.NotifyAsync([customerId], plantId, eventId, new(type, priority, title, body, Links.CustomerOrder(orderId)), ct);
}

public static class PaymentNotificationHandler
{
    public static async Task Handle(PaymentProofUploaded e, NotificationDispatcher d, IShopDirectory shops, CancellationToken ct) =>
        await d.NotifyAsync(await shops.GetOrderNotificationRecipientsAsync(e.ShopId, ct), e.PlantId, e.EventId,
            new("payment.proof", P.High, $"🧾 ลูกค้าแนบสลิปออเดอร์ #{e.OrderNo}", "กรุณาตรวจสอบการชำระเงิน", Links.ShopOrder(e.ShopId, e.OrderId)), ct);

    public static async Task Handle(PaymentDuplicateSlipDetected e, NotificationDispatcher d, IShopDirectory shops, CancellationToken ct) =>
        await d.NotifyAsync(await shops.GetOrderNotificationRecipientsAsync(e.ShopId, ct), e.PlantId, e.EventId,
            new("payment.duplicate", P.High, $"⚠️ สลิปออเดอร์ #{e.OrderNo} อาจซ้ำกับออเดอร์อื่น", "กรุณาตรวจสอบยอดเงินในบัญชีก่อนยืนยัน", Links.ShopOrder(e.ShopId, e.OrderId)), ct);

    public static Task Handle(PaymentVerified e, NotificationDispatcher d, CancellationToken ct) =>
        d.NotifyAsync([e.CustomerId], e.PlantId, e.EventId, new("payment.verified", P.Medium, $"💚 ร้านยืนยันการชำระเงิน #{e.OrderNo} แล้ว", "ขอบคุณค่ะ", Links.CustomerOrder(e.OrderId)), ct);

    public static Task Handle(PaymentRejected e, NotificationDispatcher d, CancellationToken ct) =>
        d.NotifyAsync([e.CustomerId], e.PlantId, e.EventId, new("payment.rejected", P.High, $"สลิปออเดอร์ #{e.OrderNo} ไม่ผ่านการตรวจสอบ", e.Reason, Links.CustomerOrder(e.OrderId)), ct);
}

public static class CommunityNotificationHandler
{
    public static async Task Handle(MembershipRequested e, NotificationDispatcher d, IPlantDirectory plants, IUserDirectory users, CancellationToken ct)
    {
        var name = (await users.GetProfilesAsync([e.UserId], ct)).GetValueOrDefault(e.UserId)?.DisplayName ?? e.Nickname ?? "?";
        await d.NotifyAsync(await plants.GetActiveAdminIdsAsync(e.PlantId, ct), e.PlantId, e.EventId,
            new("admin.join_request", P.Medium, "👋 มีคำขอเข้าหมู่บ้านใหม่", $"{name} · บ้านเลขที่ {e.HouseNo}", "/admin/members"), ct);
    }

    public static async Task Handle(MembershipApproved e, NotificationDispatcher d, IPlantDirectory plants, CancellationToken ct)
    {
        var plant = await plants.GetPlantAsync(e.PlantId, ct);
        await d.NotifyAsync([e.UserId], e.PlantId, e.EventId, new("membership.approved", P.High, $"🎉 ยินดีต้อนรับสู่ {plant?.Name}", "เริ่มสั่งของจากร้านในหมู่บ้านได้เลย", "/"), ct);
    }

    public static async Task Handle(MembershipRejected e, NotificationDispatcher d, IPlantDirectory plants, CancellationToken ct)
    {
        var plant = await plants.GetPlantAsync(e.PlantId, ct);
        await d.NotifyAsync([e.UserId], e.PlantId, e.EventId, new("membership.rejected", P.High, $"คำขอเข้า {plant?.Name} ไม่ได้รับอนุมัติ", e.Reason ?? "ติดต่อผู้ดูแลหมู่บ้าน", "/villages"), ct);
    }

    public static async Task Handle(ShopApplicationSubmitted e, NotificationDispatcher d, IPlantDirectory plants, CancellationToken ct) =>
        await d.NotifyAsync(await plants.GetActiveAdminIdsAsync(e.PlantId, ct), e.PlantId, e.EventId,
            new("admin.shop_application", P.Medium, "🏪 มีคำขอเปิดร้านใหม่", e.ShopName, "/admin/applications"), ct);

    public static Task Handle(ShopApplicationApproved e, NotificationDispatcher d, CancellationToken ct) =>
        d.NotifyAsync([e.ApplicantId], e.PlantId, e.EventId, new("application.approved", P.High, $"🎉 ร้าน {e.ShopName} ได้รับอนุมัติแล้ว", "เริ่มตั้งค่าร้านและเพิ่มสินค้าได้เลย", $"/merchant/{e.ShopId}"), ct);

    public static Task Handle(ShopApplicationRejected e, NotificationDispatcher d, CancellationToken ct) =>
        d.NotifyAsync([e.ApplicantId], e.PlantId, e.EventId, new("application.rejected", P.High, $"คำขอเปิดร้าน {e.ShopName} ไม่ได้รับอนุมัติ", e.Reason, "/shop-applications"), ct);

    public static Task Handle(ShopApplicationChangesRequested e, NotificationDispatcher d, CancellationToken ct) =>
        d.NotifyAsync([e.ApplicantId], e.PlantId, e.EventId, new("application.changes", P.High, $"ผู้ดูแลขอข้อมูลเพิ่มสำหรับร้าน {e.ShopName}", e.Note, "/shop-applications"), ct);

    public static Task Handle(ShopMemberAdded e, NotificationDispatcher d, CancellationToken ct) =>
        d.NotifyAsync([e.UserId], e.PlantId, e.EventId, new("shop.member_added", P.Medium, $"คุณเป็นสมาชิกร้าน {e.ShopName} แล้ว", $"สิทธิ์: {e.Role}", $"/merchant/{e.ShopId}"), ct);

    public static async Task Handle(ShopSuspended e, NotificationDispatcher d, IShopDirectory shops, CancellationToken ct) =>
        await d.NotifyAsync((await shops.GetMembersAsync(e.ShopId, ct)).Select(m => m.UserId), e.PlantId, e.EventId,
            new("shop.suspended", P.High, "ร้านของคุณถูกระงับชั่วคราว", e.Reason ?? "ติดต่อผู้ดูแลหมู่บ้าน", $"/merchant/{e.ShopId}"), ct);

    public static async Task Handle(StockLow e, NotificationDispatcher d, IShopDirectory shops, CancellationToken ct) =>
        await d.NotifyAsync((await shops.GetMembersAsync(e.ShopId, ct)).Where(m => m.Role >= ShopRole.Manager).Select(m => m.UserId), e.PlantId, e.EventId,
            new("stock.low", P.Low, $"📉 {e.ItemName} ใกล้หมด", $"เหลือ {e.Available} ชิ้น", $"/merchant/{e.ShopId}/menu"), ct);

    /// <summary>"Your favourite shop just opened" - answers the original problem of not knowing whether a shop is open.</summary>
    public static async Task Handle(ShopStatusChanged e, NotificationDispatcher d, IShopDirectory shops, CancellationToken ct)
    {
        if (!e.BecameOpen) return;
        await d.NotifyAsync(await shops.GetFavoritersAsync(e.ShopId, ct), e.PlantId, e.EventId,
            new("shop.opened", P.Low, $"🟢 {e.ShopName} เปิดแล้ว", "สั่งได้เลยตอนนี้", Links.Shop(e.ShopId)), ct);
    }

    public static async Task Handle(AnnouncementPublished e, NotificationDispatcher d, IPlantDirectory plants, CancellationToken ct)
    {
        if (!e.Notify) return;
        await d.NotifyAsync(await plants.GetActiveMemberIdsAsync(e.PlantId, ct), e.PlantId, e.EventId,
            new("plant.announcement", P.Low, "📢 ประกาศจากหมู่บ้าน", e.Title, "/"), ct);
    }

    public static async Task Handle(ReviewSubmitted e, NotificationDispatcher d, IShopDirectory shops, CancellationToken ct) =>
        await d.NotifyAsync((await shops.GetMembersAsync(e.ShopId, ct)).Select(m => m.UserId), e.PlantId, e.EventId,
            new("review.new", P.Low, $"⭐ รีวิวใหม่ {e.Rating}/5", "ลูกค้ารีวิวร้านของคุณ", $"/merchant/{e.ShopId}/reviews"), ct);

    public static Task Handle(ReviewReplied e, NotificationDispatcher d, CancellationToken ct) =>
        d.NotifyAsync([e.CustomerId], e.PlantId, e.EventId, new("review.reply", P.Low, "ร้านตอบกลับรีวิวของคุณ", "แตะเพื่อดู", Links.Shop(e.ShopId)), ct);
}
