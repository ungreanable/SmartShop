# SmartShop 🏘️🛒

> แพลตฟอร์ม Open Source สำหรับ **ร้านค้าชุมชนในหมู่บ้าน** — ทดแทนการซื้อขายผ่าน LINE OpenChat ที่ Order ตกหล่น ไม่รู้ว่าร้านเปิดหรือปิด และไม่รู้ว่าหมู่บ้านมีร้านอะไรบ้าง

UX ได้แรงบันดาลใจจาก Delivery Platform (Grab / LINE MAN) ที่ทั้งลูกค้าและเจ้าของร้านใช้จนคุ้นมือ แต่ปรับให้เหมาะกับ "ร้านในหมู่บ้าน": ไม่มี Rider และไม่มีค่าส่ง (ร้านไปส่งเองหรือลูกค้ามารับ), เฉพาะคนในหมู่บ้านที่ Admin อนุมัติแล้วเท่านั้น, มีบริการ (นวด, ซักรีด), มี Pre-order แบบเป็นรอบ และจ่ายเงินแบบโอนตรง / เงินสด

## ปัญหาที่ต้องการแก้

| ปัญหาเดิม (OpenChat) | SmartShop |
|---|---|
| ไม่รู้ว่าร้านเปิด/ปิดอยู่ไหม | สถานะร้าน Real-time (ตามตารางเวลา + เปิด/ปิดเองได้ + ปิดชั่วคราวเป็นช่วง) |
| ไม่รู้ว่าหมู่บ้านมีร้านอะไรบ้าง | หน้า Home ของหมู่บ้าน, หมวดหมู่, ค้นหา |
| Order อยู่ในแชท แล้วตกหล่น | Order มีสถานะชัดเจน + Timeline + แจ้งเตือนสมาชิกร้านทุกคน ทุกอุปกรณ์ (LINE / Web Push / FCM / In-app) พร้อมเสียงเตือนจนกว่าจะมีคนรับ |
| ไม่รู้ว่าโอนแล้วหรือยัง | QR พร้อมเพย์พร้อมยอด, แนบสลิป, ตรวจสลิปซ้ำ, ร้านกดยืนยัน (หรือต่อระบบตรวจสลิปอัตโนมัติ) |
| ของหมดแต่ยังมีคนสั่ง | จัดการ Stock / จองสต็อกตอนสั่ง / Sold-out อัตโนมัติ |

## สถานะ

ทำครบทุก Phase ตาม [Roadmap](docs/05-roadmap.md) แล้ว ได้แก่ Web App (PWA + LINE LIFF), Mobile App (MAUI), Backend 13 Module, รีวิว, โปรโมชั่น/คูปอง, Audit log, Public API + Webhook, Row-Level Security, Helm chart

- **ลูกค้า:** ดูร้านที่เปิดอยู่, สั่งของ/จองบริการ/Pre-order, จ่ายเงินสดหรือโอน, ติดตาม Order แบบ Real-time, แชทกับร้าน, รีวิว
- **ร้านค้า:** รับ Order พร้อมเสียงเตือน, จัดการเมนูและสต็อก, ตั้งเวลาเปิดปิด, สมาชิกร้านหลายคน, ยอดขาย/CSV, โปรโมชั่น, เชื่อม POS/เครื่องพิมพ์ผ่าน API
- **Admin หมู่บ้าน:** อนุมัติสมาชิกและร้าน, ประกาศ, ดูแลรีวิว/รายงานปัญหา, Audit log

## เริ่มใช้งาน (Development)

ต้องมี [.NET 10 SDK](https://dotnet.microsoft.com/download) และ Docker

```bash
docker compose -f deploy/docker-compose.dev.yml up -d      # Postgres, Valkey, RabbitMQ, SeaweedFS
dotnet run --project src/Hosts/SmartShop.Api --launch-profile http      # :5080 (migrate อัตโนมัติ)
dotnet run --project src/Hosts/SmartShop.Worker --launch-profile http   # :5090
dotnet run --project src/Clients/SmartShop.Web --launch-profile http    # :5100 เปิดอันนี้ในเบราว์เซอร์
```

ใน Development ใช้ปุ่ม **Dev login** ได้โดยไม่ต้องตั้งค่า LINE หรือใช้ .NET Aspire แทนก็ได้: `dotnet run --project src/Aspire/SmartShop.AppHost` รายละเอียดอยู่ใน [CONTRIBUTING.md](CONTRIBUTING.md)

## Deploy

- **VPS เครื่องเดียว:** `deploy/docker-compose.yml` + Caddy (HTTPS อัตโนมัติ) ดู `deploy/.env.example`
- **Kubernetes:** Helm chart ที่ `deploy/helm/smartshop`
- **Observability (ไม่บังคับ):** `deploy/docker-compose.observability.yml` (Grafana + OpenTelemetry)
- **Mobile:** `src/Clients/SmartShop.Mobile` (Android; Build ด้วย `SmartShop.Mobile.slnx` และต้องติดตั้ง MAUI workload ก่อน)

## เอกสารออกแบบ

1. [Features & Roles](docs/01-features.md): บทบาทผู้ใช้และ Feature ทั้งหมด แยกตาม Phase
2. [UX / UI Flows](docs/02-ux-flows.md): หน้าจอและ Flow ที่อิงจาก Grab / LINE MAN
3. [Architecture](docs/03-architecture.md): สถาปัตยกรรม .NET แบบ Event-Driven, Cache, Security, Deploy, License
4. [Domain Model & Events](docs/04-domain-model.md): ERD, State Machine, Stock Logic, Event Catalog
5. [Roadmap](docs/05-roadmap.md): สถานะแต่ละ Phase และ Decisions Log
6. [Public API & Webhooks](docs/06-public-api.md): เชื่อมต่อ POS / เครื่องพิมพ์ / ระบบอื่น

## Tech Stack (สรุป)

- **Backend:** .NET 10, ASP.NET Core Minimal APIs, EF Core + PostgreSQL, Wolverine (Messaging + Outbox + Scheduling), HybridCache + Valkey, SignalR
- **Frontend:** Blazor WebAssembly PWA (+ LINE LIFF) และ .NET MAUI Blazor Hybrid ใช้ Razor Component ชุดเดียวกัน (MudBlazor)
- **Infra:** PostgreSQL, Valkey, RabbitMQ, SeaweedFS (S3), Caddy
- **Auth:** LINE Login → ระบบออก JWT เอง (+ Refresh token rotation)
- **Dev/Deploy:** Docker Compose / Aspire (Local), Docker Compose หรือ Helm (Production), GitHub Actions → GHCR

## License

[MIT](LICENSE) (Dependency ที่เป็น AGPL ใช้ได้เฉพาะแบบรันแยก Container ดู [License Policy](docs/03-architecture.md#license-policy))
