# 05 — Roadmap

สถานะ: ✅ เสร็จ · 🟡 ทำบางส่วน · ⏭️ ไม่ทำ (มีเหตุผลในตาราง) · ⬜ ยังไม่ทำ

## Phase 0: Foundation
- ✅ Repo: Solution Structure, Central Package Management, `.editorconfig`, Analyzers, License, CONTRIBUTING
- ✅ .NET Aspire AppHost + ServiceDefaults (Postgres, Valkey, RabbitMQ, SeaweedFS) และ `deploy/docker-compose.dev.yml`
- ✅ BuildingBlocks: SharedKernel, Wolverine + EF Core Outbox, HybridCache, S3 Storage
- ✅ ArchitectureTests (Module boundary, ชื่อ Handler) + Integration Test base (Testcontainers)
- ✅ GitHub Actions: build/test/format/Razor check, publish container → GHCR, CodeQL, Dependabot
- ✅ `deploy/docker-compose.yml` + Caddyfile + `.env.example`

## Phase 1: MVP (ใช้แทน OpenChat ได้จริง)
- ✅ **Identity:** LINE Login (Web + LIFF + Mobile PKCE), JWT + Refresh Rotation (Reuse detection), Profile, PDPA Consent, Export/ลบข้อมูล
- ✅ **Plants:** Plant CRUD (System Admin), ลิงก์/QR ขอเข้าหมู่บ้าน, Join Request + Admin Approval, Plant Switcher, Plant Admin
- ✅ **Shops:** Shop Application + Review, Shop Profile, Shop Members (Owner/Manager/Staff + Invite), Weekly Hours, Closure Period, Manual Override, Busy Mode, Accept Mode/Timeout, Delivery Options, Payment Methods (Cash / PromptPay Dynamic QR / Bank / Custom)
- ✅ **Catalog:** Categories, Items (Product/Service), Stock Mode (Untracked/Tracked/Daily/Slot), Availability Window, Sold-out, Low-stock Alert, Stock Ledger
- ✅ **Ordering:** Server Cart, Checkout (Fulfillment, ASAP/Scheduled, Idempotency), State Machine, Reserve/Commit/Release, Auto-expire, Auto-complete, Delivery Photo, Timeline
- ✅ **Payments:** Slip Upload (Private), Verify/Reject, Payment ก่อนเริ่มทำ (ร้านเลือกได้)
- ✅ **Notifications:** Fan-out ถึงสมาชิกร้าน, In-app Inbox, SignalR + เสียงเตือน, LINE Flex Push + Webhook follow/unfollow, Web Push Fallback, Reminder, Notification Health Check, LINE Quota Guard, ช่วงห้ามรบกวน
- ✅ **Search:** Home Feed (ร้านที่เปิดอยู่ก่อน), ค้นหาร้าน/สินค้า (`pg_trgm`)
- ✅ **Web:** Blazor WebAssembly PWA + LIFF (โหมดลูกค้า / ร้าน / Admin), Share Link แบบ Generic Preview
- ✅ **Audit Log** (ทำใน Module `Audit` สร้างจาก Integration Event)

## Phase 2: Engagement & Mobile
- ✅ Options/Add-ons (Modifier Groups), Max per order
- ✅ Service Slot Booking, Pre-order Round + "สรุปของที่ต้องทำ"
- ✅ Rating & Review (ร้านตอบกลับ, Admin ซ่อนได้), Favorite + แจ้งเตือนเมื่อร้านเปิด, Re-order
- ✅ In-order Chat, Report/Dispute
- ✅ Merchant Dashboard (ยอดขาย) + Export CSV
- 🟡 Announcements ✅ / Plant Categories: ใช้หมวดหมู่ที่ร้านกรอกเอง + ตัวกรองหน้า Home (ยังไม่มีรายการหมวดหมู่ที่ Admin กำหนดเอง)
- ✅ Slip QR Duplicate Check
- 🟡 **.NET MAUI App:** Android (FCM, LINE Login ผ่าน System browser, Deep link) + Windows สำหรับทดสอบ, Build ใน CI ได้ / iOS + APNs ยังไม่ทำ (ต้อง Build บน Mac)
- ✅ Delivery Option ระดับสินค้า
- ✅ TH/EN (เขียนสองภาษาในคอมโพเนนต์)

## Phase 3: Scale & Ecosystem
- ✅ Promotions / Coupons (อัตโนมัติ + โค้ด, จำกัดจำนวนรวม/ต่อคน, คืนสิทธิ์เมื่อยกเลิก), Group Buy (Pre-order Round ที่มียอดขั้นต่ำ)
- ✅ Slip Verification Plugin (HTTP, ไม่บังคับ, ยืนยันอัตโนมัติได้)
- ✅ PostgreSQL Row-Level Security (Opt-in: `Database:RowLevelSecurity`)
- ✅ Helm Chart / Kubernetes, Observability Stack (Grafana otel-lgtm)
- ✅ Public API + Webhooks ([docs/06-public-api.md](06-public-api.md))
- ⏭️ Auth Provider เพิ่มเติม (Google, Phone OTP): ไม่ทำตามโจทย์ "Authentication ใช้ LINE อย่างเดียว"

## งานที่ยังเหลือ / ข้อเสนอถัดไป
- ⬜ iOS build + APNs (ต้องใช้ Mac และ Apple Developer account)
- ⬜ E2E test ของ UI (Playwright) และ Component test (bUnit)
- ⬜ รายการหมวดหมู่ร้านที่ Plant Admin กำหนดเอง
- ⬜ Pilot กับหมู่บ้านจริง 3–5 ร้าน (นิยาม "เสร็จ" ของ MVP)

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
| 7 | Web rendering | Blazor **WebAssembly Standalone** + Web host (YARP proxy `/api`, `/hubs`) แทน Interactive Auto/Prerender: Deploy ง่าย, Same-origin, ใช้ในแอป LINE ได้ |
| 8 | Login | **LINE อย่างเดียว** (ไม่ทำ Google / Phone OTP) |
| 9 | Mobile solution | แยก `SmartShop.Mobile.slnx` เพราะต้องใช้ MAUI workload |

## คำถามที่ยังเปิดอยู่

| # | คำถาม | ค่าเริ่มต้นที่ทำไว้ |
|---|---|---|
| 1 | ร้านไม่มีสมาชิกคนไหนมีช่องทาง Push เลย ควร **บังคับไม่ให้เปิดร้าน** หรือแค่เตือน? | Plant Admin ตั้งค่าได้ (ค่าเริ่มต้น "แค่เตือน") |
| 2 | งบโควต้า LINE OA ต่อเดือน (แพ็กเกจไหน)? | Free Plan + Quota Guard (`LINE_MONTHLY_QUOTA`, กันโควต้าไว้ให้แจ้งเตือนสำคัญ) |
| 3 | ช่วง Reminder และ Timeout ของ Order ใหม่ | Reminder 3 นาที, Timeout 15 นาที (ร้านปรับได้) |
| 4 | ใช้บริการตรวจสลิปอัตโนมัติเจ้าไหน (มีค่าใช้จ่าย)? | ปิดไว้ ร้านตรวจเอง (ต่อผ่าน `SLIP_VERIFIER_URL` ได้) |
