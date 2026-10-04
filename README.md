# SmartShop 🏘️🛒

> แพลตฟอร์ม Open Source สำหรับ **ร้านค้าชุมชนในหมู่บ้าน** — ทดแทนการซื้อขายผ่าน LINE OpenChat ที่ Order ตกหล่น ไม่รู้ว่าร้านเปิดหรือปิด และไม่รู้ว่าหมู่บ้านมีร้านอะไรบ้าง

UX ได้แรงบันดาลใจจาก Delivery Platform (Grab / LINE MAN) ที่ทั้งลูกค้าและเจ้าของร้านใช้จนคุ้นมือ แต่ปรับให้เหมาะกับ "ร้านในหมู่บ้าน": ไม่มี Rider และไม่มีค่าส่ง (ร้านไปส่งเองหรือลูกค้ามารับ), เฉพาะคนในหมู่บ้านที่ Admin อนุมัติแล้วเท่านั้น, มีบริการ (นวด, ซักรีด), มี Pre-order แบบเป็นรอบ และจ่ายเงินแบบโอนตรง / เงินสด

## ปัญหาที่ต้องการแก้

| ปัญหาเดิม (OpenChat) | SmartShop |
|---|---|
| ไม่รู้ว่าร้านเปิด/ปิดอยู่ไหม | สถานะร้าน Real-time (ตามตารางเวลา + เปิด/ปิดเองได้ + ปิดชั่วคราวเป็นช่วง) |
| ไม่รู้ว่าหมู่บ้านมีร้านอะไรบ้าง | หน้า Home ของหมู่บ้าน, หมวดหมู่, ค้นหา |
| Order อยู่ในแชท แล้วตกหล่น | Order มีสถานะชัดเจน + Timeline + แจ้งเตือนสมาชิกร้านทุกคน ทุกอุปกรณ์ (LINE / Web Push / In-app) |
| ไม่รู้ว่าโอนแล้วหรือยัง | แนบสลิป + ร้านกดยืนยันการชำระเงิน |
| ของหมดแต่ยังมีคนสั่ง | จัดการ Stock / จองสต็อกตอนสั่ง / Sold-out อัตโนมัติ |

## เอกสารออกแบบ

1. [Features & Roles](docs/01-features.md): บทบาทผู้ใช้และ Feature ทั้งหมด แยกตาม Phase
2. [UX / UI Flows](docs/02-ux-flows.md): หน้าจอและ Flow ที่อิงจาก Grab / LINE MAN
3. [Architecture](docs/03-architecture.md): สถาปัตยกรรม .NET แบบ Event-Driven, Cache, Docker, License
4. [Domain Model & Events](docs/04-domain-model.md): ERD, State Machine, Stock Logic, Event Catalog
5. [Roadmap](docs/05-roadmap.md): ลำดับการพัฒนา + คำถามที่ต้องตัดสินใจ

## Tech Stack (สรุป)

- **Backend:** .NET 10 (LTS), ASP.NET Core, EF Core, Wolverine (Messaging + Outbox)
- **Frontend:** Blazor Web App (PWA + LINE LIFF) และ .NET MAUI Blazor Hybrid (iOS/Android) ใช้ Razor Component ชุดเดียวกัน
- **Infra:** PostgreSQL, Valkey (Redis-compatible), RabbitMQ, SeaweedFS (S3), Caddy
- **Auth:** LINE Login (OIDC) → ระบบออก JWT ของตัวเอง
- **Dev/Deploy:** .NET Aspire (Local), Docker Compose (Production บน VPS เครื่องเดียว), GitHub Actions → GHCR

## License

[MIT](LICENSE) (Dependency ที่เป็น AGPL ใช้ได้เฉพาะแบบรันแยก Container ดู [License Policy](docs/03-architecture.md#license-policy))
