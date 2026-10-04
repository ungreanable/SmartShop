# 05 — Roadmap

## Phase 0: Foundation (1–2 สัปดาห์)
- [ ] Repo: Solution Structure, Central Package Management, `.editorconfig`, Analyzers, License, CONTRIBUTING
- [ ] .NET Aspire AppHost + ServiceDefaults (Postgres, Valkey, RabbitMQ, SeaweedFS)
- [ ] BuildingBlocks: SharedKernel, Wolverine + EF Core Outbox, HybridCache, S3 Storage
- [ ] ArchitectureTests (Module boundary) + Integration Test base (Testcontainers)
- [ ] GitHub Actions: build/test/publish container → GHCR
- [ ] `deploy/docker-compose.yml` + Caddyfile + `.env.example`

## Phase 1: MVP (ใช้แทน OpenChat ได้จริง)
- [ ] **Identity:** LINE Login (Web + LIFF), JWT + Refresh Rotation, Profile, PDPA Consent
- [ ] **Plants:** Plant CRUD (System Admin), ลิงก์/QR ขอเข้าหมู่บ้าน, Join Request + Admin Approval (บังคับ), Plant Switcher, Plant Admin
- [ ] **Shops:** Shop Application + Review, Shop Profile, **Shop Members (Owner/Manager/Staff)**, Weekly Hours, Closure Period, Manual Override, Busy Mode, Accept Mode/Timeout, Delivery Options (Pickup/Delivery, ไม่มีค่าส่ง), Payment Methods (Cash / PromptPay QR + Dynamic QR / Bank / Custom)
- [ ] **Catalog:** Categories, Items (Product/Service), Stock Mode (Untracked/Tracked/Daily), Availability Window, Sold-out Toggle, Low-stock Alert, Stock Ledger
- [ ] **Ordering:** Server Cart, Checkout (Fulfillment, ASAP/Scheduled), Order State Machine, Reserve/Commit/Release, Auto-expire, Auto-complete, Delivery Photo, Timeline
- [ ] **Payments:** Slip Upload (Private), Verify/Reject
- [ ] **Notifications:** Fan-out ถึงสมาชิกร้าน, In-app Inbox, SignalR (+ เสียงเตือน Order ใหม่), LINE OA Push (Flex Message) + Webhook follow/unfollow, **Web Push (PWA) เป็น Fallback**, Reminder Order ค้าง, Notification Health Check, LINE Quota Guard
- [ ] **Search:** Home Feed (Open Now first), ค้นหาร้าน/สินค้า (`pg_trgm`)
- [ ] **Web:** Blazor Web App (PWA + LIFF) โหมดลูกค้า / ร้าน / Admin, Share Link (Generic Preview, ต้องเป็นสมาชิกถึงจะเห็นเนื้อหา)
- [ ] **Audit Log**

**นิยาม "เสร็จ" ของ MVP:** ร้าน 3–5 ร้านในหมู่บ้านนำร่องรับ Order ผ่านระบบได้ครบ Flow 2 สัปดาห์โดยไม่ต้องย้อนกลับไปใช้ OpenChat

## Phase 2: Engagement & Mobile
- [ ] Options/Add-ons (Modifier Groups), Max per order
- [ ] Service Slot Booking, Pre-order Round + "สรุปของที่ต้องทำ"
- [ ] Rating & Review, Favorite + แจ้งเตือนเมื่อร้านเปิด, Re-order
- [ ] In-order Chat, Report/Dispute
- [ ] Merchant Dashboard + Export CSV
- [ ] Announcements, Plant Categories
- [ ] Slip QR Duplicate Check
- [ ] **.NET MAUI App** (iOS/Android) + FCM/APNs
- [ ] Delivery Option ระดับสินค้า
- [ ] TH/EN

## Phase 3: Scale & Ecosystem
- [ ] Promotions / Coupons, Group Buy
- [ ] Slip Verification Plugin (3rd-party, optional)
- [ ] PostgreSQL Row-Level Security
- [ ] Helm Chart / Kubernetes, Observability Stack (Grafana)
- [ ] Public API + Webhooks สำหรับ Integration
- [ ] Auth Provider เพิ่มเติม (Google, Phone OTP)

---

## Decisions Log

| # | เรื่อง | ข้อสรุป |
|---|---|---|
| 1 | การส่งของ | มีแค่ **ร้านไปส่งเอง** หรือ **ลูกค้ามารับเอง** ร้านเลือกเปิดได้เอง **ไม่มี Rider และไม่มีค่าส่ง** |
| 2 | การมองเห็น | **คนนอกหมู่บ้านดูอะไรไม่ได้เลย** ต้องเป็นสมาชิกที่ Plant Admin อนุมัติแล้วเท่านั้น (ไม่มีการเข้าอัตโนมัติ) |
| 3 | License | **MIT** ส่วน AGPL ใช้ได้เฉพาะ Software ที่รันแยก Container |
| 4 | ผู้รับแจ้งเตือนของร้าน | ร้านมีสมาชิกได้หลายคน (Owner/Manager/Staff) แจ้งเตือน Fan-out ไปทุกคน ทุกอุปกรณ์ ถ้าส่ง LINE ไม่ได้ให้ Fallback ไป Web Push / FCM / In-app |
| 5 | Hosting | ออกแบบ Multi-plant ใช้ได้ทั้ง Instance กลางและ Self-host ต่อหมู่บ้าน |
| 6 | Frontend | Blazor (Web/PWA/LIFF) + MAUI Blazor Hybrid ตามโจทย์ ".NET ทั้งหมด" |

## คำถามที่ยังเปิดอยู่

| # | คำถาม | ข้อเสนอ default |
|---|---|---|
| 1 | ร้านไม่มีสมาชิกคนไหนมีช่องทาง Push เลย ควร **บังคับไม่ให้เปิดร้าน** หรือแค่เตือน? | ให้ Plant Admin ตั้งค่าได้ ค่าเริ่มต้นคือ "แค่เตือน" |
| 2 | งบโควต้า LINE OA ต่อเดือน (แพ็กเกจไหน)? | เริ่มจาก Free Plan + Quota Guard ส่งเฉพาะ 🔴 สูง |
| 3 | ช่วง Reminder และ Timeout ของ Order ใหม่ | Reminder 3 นาที, Timeout 15 นาที (ร้านปรับได้) |
