# 06 — Public API & Webhooks

ร้านค้าเชื่อมต่อ SmartShop กับระบบอื่นได้ เช่น เครื่องพิมพ์ใบสั่งหน้าร้าน, POS, Google Sheets หรือเครื่องมือ Automation (n8n, Make, Zapier)
ตั้งค่าได้ที่ **ร้าน → ตั้งค่า → 🔌 เชื่อมต่อระบบอื่น** (เฉพาะ Owner)

## API keys

- สร้าง Key ได้สูงสุด 10 อันต่อร้าน, เลือกสิทธิ์ (scope) ได้: `orders:read`, `menu:read`
- Key มีรูปแบบ `ssk_<prefix>_<secret>` **แสดงครั้งเดียวตอนสร้าง** ระบบเก็บไว้แค่ SHA-256 hash
- ยกเลิก (Revoke) ได้ทันที และระบบบันทึกเวลาใช้งานล่าสุด
- Rate limit: 120 requests/นาที ต่อ Key (`RateLimiting:PublicApiPerMinute`)

```http
GET /api/public/v1/orders?since=2026-10-01T00:00:00Z&status=PendingAcceptance&limit=50
X-Api-Key: ssk_1a2b3c4d_...
```

| Endpoint | Scope | คำอธิบาย |
|---|---|---|
| `GET /api/public/v1/shop` | ใดก็ได้ | ข้อมูลร้านของ Key นี้ + scopes |
| `GET /api/public/v1/orders` | `orders:read` | Order ล่าสุดก่อน (`since`, `status`, `limit` ≤ 200) |
| `GET /api/public/v1/orders/{id}` | `orders:read` | Order เดียว พร้อมรายการ, ที่อยู่ส่ง, สถานะการจ่ายเงิน |
| `GET /api/public/v1/menu` | `menu:read` | สินค้าทั้งหมด พร้อมราคาและจำนวนคงเหลือ |

Response ของ Order: `{ "order": { orderId, orderNo, status, fulfillmentType, houseNo, soi, addressNote, note, scheduledFrom, scheduledTo, subtotal, discount, total, paymentMethodType, paymentStatus, placedAt, acceptedAt, completedAt, lines: [...] }, "customerName": "..." }`

Error: `401 api_key_invalid` (ไม่มี Key / Key ผิด / ถูกยกเลิก), `403 api_key_scope`, `429` (เกิน Rate limit)

## Webhooks

SmartShop ส่ง `POST` (JSON) ไปยัง URL ของร้านเมื่อเกิดเหตุการณ์ที่เลือกไว้ (สูงสุด 5 URL ต่อร้าน)

> **ปิดไว้เป็นค่าเริ่มต้น** (ยังไม่เปิดให้ใช้ใน Release นี้) เปิดได้ด้วย `FEATURES_WEBHOOKS=true` ใน `deploy/.env` (`Features:Webhooks`) เมื่อปิด หน้าตั้งค่าร้านจะซ่อนส่วน Webhook, API จัดการ Webhook ตอบ `404` และไม่มีการส่ง Event ออกไป

| Event | เมื่อไร |
|---|---|
| `order.placed` | ลูกค้าสั่ง |
| `order.accepted` / `order.preparing` / `order.ready` / `order.delivered` / `order.completed` | สถานะเปลี่ยน |
| `order.cancelled` / `order.rejected` / `order.expired` | Order ไม่สำเร็จ |
| `payment.verified` | ยืนยันการชำระเงินแล้ว (`automatic: true` ถ้ายืนยันโดยระบบตรวจสลิป) |
| `ping` | กดปุ่ม "ทดสอบ" |

```http
POST https://example.com/smartshop-hook
Content-Type: application/json
X-SmartShop-Event: order.placed
X-SmartShop-Delivery: 01a1...
X-SmartShop-Signature: t=1759600000,v1=5d41402abc4b2a76b9719d911017c592...

{ "id": "01a1...", "type": "order.placed", "createdAt": "...", "shopId": "...", "data": { "order": { ... }, "customerName": "..." } }
```

### ตรวจลายเซ็น

`v1 = hex(HMAC-SHA256(signingSecret, t + "." + rawBody))` ให้เทียบแบบ constant-time และปฏิเสธถ้า `t` เก่ากว่า 5 นาที

```csharp
static bool Verify(string secret, string header, string body)
{
    var parts = header.Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(parts["t"]) > 300) return false;
    var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{parts["t"]}.{body}"))).ToLowerInvariant();
    return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts["v1"]));
}
```

### การส่งซ้ำ

- ถือว่าสำเร็จเมื่อได้ HTTP 2xx ภายใน 10 วินาที (ไม่ตาม Redirect)
- ถ้าไม่สำเร็จจะลองใหม่หลัง 1 นาที, 5 นาที, 30 นาที, 2 ชม., 6 ชม. (รวม 6 ครั้ง)
- ถ้าล้มเหลวติดกัน 20 ครั้ง ระบบจะปิด URL นั้นอัตโนมัติ (เปิดใหม่ได้จากหน้าตั้งค่า)
- Event อาจมาซ้ำหรือสลับลำดับได้ ให้ใช้ `id` กันการประมวลผลซ้ำ และดู `status` ของ Order เป็นหลัก

### ความปลอดภัย (SSRF)

URL ต้องเป็น `https://` และต้องชี้ไปที่ IP สาธารณะ ระบบตรวจทั้งตอนบันทึกและตอนเชื่อมต่อจริง (กันกรณี DNS เปลี่ยนไปชี้ IP ภายใน)
Loopback, Private network, Link-local (รวม Cloud metadata `169.254.169.254`) และ CGNAT ถูกปฏิเสธทั้งหมด
สำหรับ Development เท่านั้น: `Integrations:Webhooks:AllowInsecure` / `AllowPrivateNetworks`
