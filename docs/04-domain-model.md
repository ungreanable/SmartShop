# 04 — Domain Model & Events

## 1. ERD (ภาพรวม แยกตาม Module/Schema)

```mermaid
erDiagram
    %% identity
    USER ||--o{ MEMBERSHIP : has
    USER {
        uuid id PK
        string line_user_id UK
        string display_name
        string picture_url
        string phone "nullable, masked"
        bool is_system_admin
    }

    %% plants
    PLANT ||--o{ MEMBERSHIP : contains
    PLANT {
        uuid id PK
        string name
        string time_zone "Asia/Bangkok"
        string join_code UK "resettable, ระบุ Plant เท่านั้น ไม่ให้สิทธิ์"
    }
    MEMBERSHIP {
        uuid id PK
        uuid plant_id FK
        uuid user_id FK
        string role "Member|PlantAdmin"
        string status "Pending|Active|Rejected|Suspended|Left"
        string house_no
        string soi
        string request_message
        uuid reviewed_by
        timestamptz reviewed_at
    }

    %% shops
    PLANT ||--o{ SHOP_APPLICATION : receives
    PLANT ||--o{ SHOP : has
    SHOP ||--o{ OPENING_HOURS : defines
    SHOP ||--o{ CLOSURE_PERIOD : defines
    SHOP ||--o{ PAYMENT_METHOD : accepts
    SHOP ||--|{ SHOP_MEMBER : "managed by"
    SHOP {
        uuid id PK
        uuid plant_id FK
        uuid owner_user_id FK
        string name
        string code "prefix for order no."
        string status "Active|Suspended|Vacation"
        string override_mode "None|ForceOpen|ForceClosed"
        timestamptz override_until
        timestamptz busy_until
        string accept_mode "Manual|Auto"
        int accept_timeout_min
        int reminder_after_min
        bool allow_preorder_when_closed
        bool pickup_enabled
        string pickup_instruction
        bool delivery_enabled
        string delivery_zone_note
        decimal delivery_min_order "nullable"
    }
    SHOP_MEMBER {
        uuid shop_id FK
        uuid user_id FK
        string role "Owner|Manager|Staff"
        bool receive_order_notifications
        string display_label "เช่น แม่ (เครื่อง 2)"
    }
    OPENING_HOURS {
        uuid shop_id FK
        int day_of_week
        time open_at
        time close_at
    }
    PAYMENT_METHOD {
        uuid id PK
        uuid shop_id FK
        string type "Cash|PromptPayQr|BankTransfer|Custom"
        string display_name
        jsonb details
        bool requires_proof
        int sort_order
        bool enabled
    }

    %% catalog
    SHOP ||--o{ ITEM : sells
    ITEM ||--o| INVENTORY : tracks
    ITEM ||--o{ MODIFIER_GROUP : has
    INVENTORY ||--o{ STOCK_MOVEMENT : logs
    ITEM {
        uuid id PK
        uuid shop_id FK
        string kind "Product|Service|PreOrderRound"
        string stock_mode "Untracked|Tracked|Daily|Slot"
        decimal price
        bool is_available
        jsonb availability_windows
        date sale_from
        date sale_to
        int max_per_order
    }
    INVENTORY {
        uuid item_id PK
        int on_hand
        int reserved
        int daily_quota
        int low_stock_threshold
        uint xmin "concurrency"
    }
    STOCK_MOVEMENT {
        uuid id PK
        uuid item_id FK
        string type "Restock|Reserve|Commit|Release|Adjust|DailyReset"
        int quantity
        uuid order_id "nullable"
        uuid actor_user_id
    }

    %% ordering
    SHOP ||--o{ ORDER : receives
    USER ||--o{ ORDER : places
    ORDER ||--|{ ORDER_LINE : contains
    ORDER ||--o{ ORDER_EVENT : timeline
    ORDER {
        uuid id PK
        uuid plant_id FK
        uuid shop_id FK
        uuid customer_id FK
        string order_no "A-023"
        string status
        string fulfillment_type "Pickup|Delivery"
        jsonb delivery_address "nullable"
        uuid accepted_by
        timestamptz scheduled_from "null = ASAP"
        timestamptz scheduled_to
        decimal total "ไม่มีค่าส่ง = ผลรวม lines"
        string note
        string idempotency_key UK
    }
    ORDER_LINE {
        uuid id PK
        uuid order_id FK
        uuid item_id
        string item_name_snapshot
        decimal unit_price_snapshot
        jsonb modifiers_snapshot
        int quantity
    }

    %% payments
    ORDER ||--|| PAYMENT : paid_by
    PAYMENT ||--o{ PAYMENT_PROOF : has
    PAYMENT {
        uuid id PK
        uuid order_id FK
        string method_type
        jsonb method_snapshot
        decimal amount
        string status "Unpaid|PendingVerification|Paid|Rejected|Refunded"
    }
    PAYMENT_PROOF {
        uuid id PK
        uuid payment_id FK
        string media_key
        string slip_ref "from slip QR, for duplicate check"
        string status
    }
```

### Notifications (schema `notifications`)

```mermaid
erDiagram
    NOTIFICATION ||--o{ NOTIFICATION_DELIVERY : "sent via"
    NOTIFICATION {
        uuid id PK
        uuid user_id
        uuid source_event_id
        string type "OrderPlaced|OrderAccepted|..."
        string priority "High|Medium|Low"
        jsonb payload
        timestamptz read_at
    }
    NOTIFICATION_DELIVERY {
        uuid notification_id FK
        string channel "SignalR|Line|WebPush|Fcm"
        string status "Pending|Sent|Failed|Skipped"
        string failure_reason "NotFriend|Blocked|QuotaExceeded|ApiError|..."
        int attempts
    }
    USER_DEVICE {
        uuid id PK
        uuid user_id
        string kind "WebPush|Fcm"
        string endpoint_or_token
        string device_label
        timestamptz last_seen_at
    }
    LINE_FRIENDSHIP {
        uuid user_id PK
        bool is_friend
        timestamptz changed_at
    }
    NOTIFICATION_PREFERENCE {
        uuid user_id
        string type
        bool in_line
        bool in_push
        string push_mode "Always|OnlyWhenLineFails"
    }
```

> FK ข้าม Module (เช่น `ORDER.shop_id`) เป็นแค่ ID ไม่มี Foreign Key Constraint จริงใน DB เพื่อให้แยก Schema/Service ได้

## 2. Order State Machine

```mermaid
stateDiagram-v2
    [*] --> PendingAcceptance: PlaceOrder
    PendingAcceptance --> Accepted: Accept (ร้าน / Auto)
    PendingAcceptance --> Rejected: Reject (ร้าน)
    PendingAcceptance --> Cancelled: Cancel (ลูกค้า)
    PendingAcceptance --> Expired: Timeout
    Accepted --> Preparing: StartPreparing
    Accepted --> Cancelled: Cancel (ร้าน / ลูกค้าขอแล้วร้านอนุมัติ)
    Preparing --> Ready: MarkReady
    Preparing --> Cancelled
    Ready --> OutForDelivery: Dispatch (กรณีส่ง)
    Ready --> Delivered: HandOver (กรณีรับที่ร้าน)
    OutForDelivery --> Delivered: MarkDelivered (+ รูป optional)
    Delivered --> Completed: ConfirmReceived (ลูกค้า) / AutoComplete
    Completed --> [*]
    Rejected --> [*]
    Cancelled --> [*]
    Expired --> [*]
```

- สำหรับ **Service**: `Ready` = "พร้อมให้บริการ", `OutForDelivery` = "กำลังไปให้บริการที่บ้าน", `Delivered` = "ให้บริการแล้ว"
- `Accept` บันทึก `accepted_by` (สมาชิกร้านคนที่กด) ใช้ Optimistic Concurrency กันสมาชิกหลายคนกดพร้อมกัน
- ร้านข้ามขั้นได้ (เช่น Accept แล้วกด `Delivered` ทันทีสำหรับของพร้อมขาย) โดยระบบเติม Event ที่ข้ามให้ใน Timeline
- ทุก Transition บันทึก `ORDER_EVENT` (ใคร, เมื่อไร, เหตุผล, รูป)

## 3. Stock Reservation

**Reserve ต้องเป็น Synchronous** (ลูกค้าต้องรู้ทันทีว่าของพอไหม) จึงเรียกผ่าน Contract `IInventoryService.ReserveAsync()` ใน Transaction เดียวกับการสร้าง Order (Module ต่างกันแต่ DB เดียวกัน) ส่วน Commit/Release เป็น **Asynchronous** ผ่าน Event

```sql
-- Reserve แบบ Atomic: ไม่ต้อง Lock, กัน Overselling ได้แม้มีคนสั่งพร้อมกัน
UPDATE catalog.inventory
SET reserved = reserved + @qty
WHERE item_id = @itemId
  AND on_hand - reserved >= @qty;
-- affected rows = 0  →  ของไม่พอ → ยกเลิก Transaction ทั้งหมด
```

| Event ที่รับ | Action | SQL |
|---|---|---|
| (sync) PlaceOrder | Reserve | `reserved += q WHERE on_hand - reserved >= q` |
| `OrderCompleted` | Commit | `on_hand -= q, reserved -= q` |
| `OrderCancelled` / `OrderRejected` / `OrderExpired` | Release | `reserved -= q` |
| ร้านปรับยอด | Adjust | `on_hand = x` (ห้ามต่ำกว่า `reserved`) |
| `ResetDailyStock` | DailyReset | `on_hand = daily_quota + reserved` |

ทุก Action เขียน `STOCK_MOVEMENT` และถ้า Available เปลี่ยนเป็น 0 / กลับมามากกว่า 0 → Publish `ItemSoldOut` / `ItemBackInStock`

> **Service Slot (P2)** ใช้หลักเดียวกัน โดย Inventory แยกตาม `(item_id, slot_start)` มี `capacity` แทน `on_hand`

## 4. Shop Status Evaluation

```csharp
// ภาพรวม Logic (อยู่ใน Shops.Domain, Pure function ทดสอบง่าย)
ShopStatus Evaluate(Shop shop, DateTimeOffset nowUtc)
{
    var local = TimeZoneInfo.ConvertTime(nowUtc, shop.TimeZone);

    if (shop.Status == Suspended)                  return Closed(Reason.Suspended);
    if (shop.ActiveClosurePeriod(local) is { } c)  return Closed(Reason.TemporaryClosed, until: c.End);
    if (shop.Override is { Until: var u } o && (u is null || u > nowUtc))
        return o.Mode == ForceOpen ? Open(until: u) : Closed(Reason.Manual, until: u);

    var slot = shop.Schedule.CurrentSlot(local);
    if (slot is null) return Closed(Reason.OutsideHours, nextOpen: shop.Schedule.NextOpen(local));

    return shop.BusyUntil > nowUtc ? Busy(until: shop.BusyUntil) : Open(until: slot.CloseAt);
}
```

## 5. Event Catalog

Integration Events ทั้งหมดอยู่ใน `SmartShop.Contracts` ตั้งชื่อเป็น **Past Tense** และมี `EventId`, `OccurredAt`, `PlantId`, `Version` เสมอ

| Event | Publisher | Consumers |
|---|---|---|
| `UserRegistered` | Identity | Notifications (Welcome) |
| `MembershipRequested` | Plants | Notifications → Plant Admins |
| `MembershipApproved` / `MembershipRejected` / `MembershipSuspended` | Plants | Notifications → ผู้ขอ, Cache Invalidation |
| `PlantAdminAssigned` | Plants | Cache Invalidation |
| `ShopApplicationSubmitted` | Shops | Notifications → Plant Admins |
| `ShopApplicationChangesRequested` | Shops | Notifications → ผู้ขอ |
| `ShopApplicationApproved` | Shops | Shops (Create Shop), Notifications, Search |
| `ShopApplicationRejected` | Shops | Notifications |
| `ShopProfileUpdated` | Shops | Search, Cache |
| `OpeningHoursChanged` | Shops | Worker (Reschedule `EvaluateShopStatus`), Cache |
| `ShopStatusChanged` (Open/Closed/Busy) | Shops | Search, Cache, SignalR (Plant feed), Notifications (Favorite → "ร้านเปิดแล้ว") |
| `ShopSuspended` / `ShopReinstated` | Shops | Search, Cache, Notifications |
| `PaymentMethodsUpdated` / `DeliveryOptionsUpdated` | Shops | Cache, Search |
| `ShopMemberAdded` / `ShopMemberRemoved` / `ShopMemberRoleChanged` | Shops | Cache, Notifications → สมาชิกที่ถูกเพิ่ม/ลบ |
| `ShopMemberNotificationToggled` | Shops | Cache (`notify-recipients`) |
| `ShopOwnershipTransferred` | Shops | Cache, Notifications, Audit |
| `AnnouncementPublished` | Plants | Notifications → สมาชิก (ถ้าเลือกแจ้งเตือน), Audit |
| `LineFriendshipChanged` | Notifications (จาก LINE Webhook) | Cache (`user:{id}:channels`) |
| `ItemCreated` / `ItemUpdated` / `ItemDeleted` | Catalog | Search, Cache |
| `ItemSoldOut` / `ItemBackInStock` | Catalog | Search, Cache, SignalR (Shop page) |
| `StockLow` | Catalog | Notifications → ร้าน |
| `OrderPlaced` | Ordering | Payments (Create Payment), Integrations (Webhook), Notifications → **สมาชิกร้านทุกคนที่เปิดรับ**, Worker (Schedule Expire / Reminder / Auto-accept) |
| `OrderAccepted` | Ordering | Notifications → ลูกค้า, SignalR → หยุดเสียงเตือนบนเครื่องสมาชิกร้านคนอื่น |
| `OrderRejected` / `OrderCancelled` / `OrderExpired` | Ordering | Catalog (Release Stock), Promotions (คืนสิทธิ์คูปอง), Payments (Void/Refund flag), Integrations, Notifications |
| `OrderPreparing` / `OrderReady` / `OrderOutForDelivery` | Ordering | Notifications, SignalR |
| `OrderDelivered` | Ordering | Notifications → ลูกค้า, Worker (Schedule AutoComplete) |
| `OrderCompleted` | Ordering | Catalog (**Commit Stock**), Reviews (เปิดให้รีวิว), Analytics |
| `PaymentProofUploaded` | Payments | Notifications → ร้าน (ตรวจสลิปซ้ำทำตอนอัปโหลด) |
| `PaymentVerified` / `PaymentRejected` | Payments | Ordering (อาจปลด Gate "จ่ายก่อนทำ"), Integrations (`payment.verified`), Notifications |
| `ReviewSubmitted` | Reviews | Shops (Update Rating Aggregate → `ShopRatingChanged`), Notifications → ร้าน |
| `ReviewReplied` | Reviews | Notifications → ลูกค้า |
| `ReviewModerated` | Reviews | Shops (คำนวณ Rating ใหม่), Audit |
| `ShopRatingChanged` | Shops | Search |
| `UserErased` | Identity | ทุก Module ที่เก็บข้อมูลส่วนบุคคล (ลบ/ทำให้ไม่ระบุตัวตน) |

**Command แบบ Async / ตั้งเวลา** (`IScheduledCommand` ส่งไป Worker): `EvaluateShopStatus`, `ExpireOrderIfNotAccepted`, `RemindShopPendingOrder`, `AutoCompleteOrder`,
`VerifySlip` (ส่งสลิปไปตรวจกับบริการภายนอก), `DeliverWebhook` (ส่ง/ส่งซ้ำ Webhook แบบ Backoff), `SendTestNotification`

**Audit log** (Module `Audit`) สร้างจาก Event ของการอนุมัติ/ระงับสมาชิก, คำขอเปิดร้าน, ระงับร้าน, โอนร้าน, ประกาศ และการซ่อนรีวิว
จึงไม่มี Module ไหนต้องจำว่าต้องเขียน Audit เอง (Idempotent ด้วย `EventId`)

### ตัวอย่าง Contract

```csharp
namespace SmartShop.Contracts.Ordering;

public sealed record OrderPlaced(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PlantId,
    Guid OrderId,
    string OrderNo,
    Guid ShopId,
    Guid CustomerId,
    decimal Total,
    string FulfillmentType,
    string PaymentMethodType,
    IReadOnlyList<OrderPlacedLine> Lines) : IIntegrationEvent;

public sealed record OrderPlacedLine(Guid ItemId, int Quantity);
```

### ตัวอย่าง Handler (Wolverine)

```csharp
// Catalog module: ฟัง OrderCompleted แล้วตัด Stock จริง
public static class OrderCompletedHandler
{
    public static async Task Handle(OrderCompleted e, CatalogDbContext db, HybridCache cache, CancellationToken ct)
    {
        foreach (var line in e.Lines)
            await db.Inventory.CommitAsync(line.ItemId, line.Quantity, e.OrderId, ct);

        await db.SaveChangesAsync(ct);                       // + Outbox (ItemSoldOut ถ้าหมด)
        await cache.RemoveByTagAsync($"shop:{e.ShopId}", ct);
    }
}
```
