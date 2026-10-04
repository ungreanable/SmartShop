# 07 — Installation (Web)

คู่มือติดตั้ง SmartShop ฝั่ง Web (PWA + LINE LIFF) ตั้งแต่ทดลองบนเครื่องตัวเองไปจนถึงขึ้น Server จริง
(แอปมือถือยังไม่รวมในคู่มือนี้)

## เลือกแบบที่จะติดตั้ง

| แบบ | ใช้ทำอะไร | ต้องมี LINE ไหม | เวลาโดยประมาณ |
|---|---|---|---|
| **A. Development บนเครื่องตัวเอง** | ลองใช้งานทุกหน้าจอ, พัฒนาต่อ | ❌ ใช้ปุ่ม Dev login | 15–30 นาที |
| **B. Docker ทั้งชุดบนเครื่องตัวเอง** | ลองแบบเดียวกับ Server จริง (Caddy + HTTPS + Container) | ❌ เปิดโหมดทดลอง (Staging + Dev login) | 20–40 นาที |
| **C. Server จริง + LINE** | ให้คนในหมู่บ้านใช้งานจริง | ✅ | 1–2 ชม. (รวมตั้งค่า LINE) |

แนะนำให้เริ่มที่ **A** เพื่อดูหน้าจอและ Flow ทั้งหมดก่อน แล้วค่อยไป **C**

---

## 1. Checklist สิ่งที่ต้องเตรียม

### 1.1 โปรแกรม (แบบ A และ B)

| โปรแกรม | เวอร์ชัน | ใช้ทำอะไร | ติดตั้ง (Windows) |
|---|---|---|---|
| **Git** | ล่าสุด | ดึง Source code (มี Git Bash สำหรับรัน Script) | `winget install Git.Git` |
| **.NET SDK** | **10.0.300 ขึ้นไป** | Build และรันระบบ | `winget install Microsoft.DotNet.SDK.10` |
| **Docker Desktop** | ล่าสุด (เปิด WSL 2) | รัน PostgreSQL, Valkey, RabbitMQ, SeaweedFS | `winget install Docker.DockerDesktop` |
| Python 3 | ไม่บังคับ | ใช้กับ `scripts/dev-api.sh` (เรียก API จาก Terminal) | `winget install Python.Python.3.12` |

macOS: `brew install git dotnet-sdk` + Docker Desktop · Linux: [ติดตั้ง .NET](https://learn.microsoft.com/dotnet/core/install/linux) + Docker Engine

ตรวจสอบ:

```bash
git --version
dotnet --version        # ต้องขึ้นต้นด้วย 10.0.3xx ขึ้นไป
docker version          # ต้องเห็นทั้ง Client และ Server (Docker Desktop เปิดอยู่)
```

### 1.2 เครื่องและ Port

- RAM อย่างน้อย 8 GB (Docker ใช้ราว 1.5 GB), พื้นที่ว่าง ~10 GB
- Port ที่ต้องว่าง
  - แบบ A: `5080` (API), `5090` (Worker), `5100` (Web), `5432`, `6379`, `5672`, `15672`, `8333`
  - แบบ B: `80`, `443`

### 1.3 สำหรับแบบ C (Server จริง)

| สิ่งที่ต้องมี | รายละเอียด |
|---|---|
| **VPS** | 2 vCPU / 4 GB RAM / 40 GB SSD ขึ้นไป, Ubuntu 24.04 LTS (x64 หรือ ARM64) รองรับได้หลายหมู่บ้าน |
| **Domain** | เช่น `shop.example.com` ตั้ง DNS **A record** ชี้ไปที่ IP ของ VPS |
| **Firewall** | เปิด `22` (SSH), `80`, `443` (TCP) และ `443/UDP` (HTTP/3 ไม่บังคับ) |
| **Docker Engine + Compose plugin** | บน Server (ดูข้อ 5.2) |
| **อีเมลผู้ดูแล** | ใช้ขอ HTTPS certificate จาก Let's Encrypt อัตโนมัติ |
| **บัญชี LINE Developers** | ดูข้อ 4 |
| **LINE User ID ของคุณ** | ใช้กำหนดให้ตัวเองเป็น System Admin (สร้างหมู่บ้านได้) |

### 1.4 ค่าที่จะได้จาก LINE (เตรียมจดไว้)

| ค่า | ได้จาก | ใส่ใน `.env` |
|---|---|---|
| Channel ID (LINE Login) | LINE Login channel → Basic settings | `LINE_LOGIN_CHANNEL_ID` |
| Channel secret (LINE Login) | LINE Login channel → Basic settings | `LINE_LOGIN_CHANNEL_SECRET` |
| LIFF ID | LINE Login channel → LIFF | `LINE_LIFF_ID` |
| Channel access token (long-lived) | Messaging API channel → Messaging API | `LINE_MESSAGING_ACCESS_TOKEN` |
| Channel secret (Messaging API) | Messaging API channel → Basic settings | `LINE_MESSAGING_CHANNEL_SECRET` |
| Add friend URL (`https://lin.ee/...`) | LINE Official Account Manager → เพิ่มเพื่อน | `LINE_ADD_FRIEND_URL` |
| Your user ID (`U...`) | LINE Login channel → Basic settings (ด้านล่างสุด) | `SYSTEM_ADMIN_LINE_USER_ID` |

---

## 2. แบบ A: Development บนเครื่องตัวเอง (ไม่ต้องใช้ LINE)

### 2.1 ติดตั้ง

```bash
git clone <URL ของ Repository> SmartShop
cd SmartShop
dotnet tool restore
```

### 2.2 เปิดบริการพื้นฐาน (Docker)

```bash
docker compose -f deploy/docker-compose.dev.yml up -d
```

> ⚠️ คำสั่งนี้เปิด **เฉพาะบริการพื้นฐาน** เท่านั้น ยังเข้า http://localhost:5100 ไม่ได้จนกว่าจะรันข้อ 2.3 ครบทั้ง 3 ตัว

จะได้ PostgreSQL (`5432`, user/pass `postgres`/`postgres`), Valkey (`6379`), RabbitMQ (`5672`, หน้าจัดการ http://localhost:15672 `guest`/`guest`) และ SeaweedFS S3 (`8333`)

### 2.3 รันระบบ (เปิด Terminal 3 หน้าต่าง)

```bash
dotnet run --project src/Hosts/SmartShop.Api --launch-profile http       # API :5080 (สร้างตารางฐานข้อมูลให้อัตโนมัติ)
```

```bash
dotnet run --project src/Hosts/SmartShop.Worker --launch-profile http    # Worker :5090 (แจ้งเตือน, งานตั้งเวลา)
```

```bash
dotnet run --project src/Clients/SmartShop.Web --launch-profile http     # Web :5100
```

เปิด **http://localhost:5100** (API Docs: http://localhost:5080/scalar/v1)

> ทางเลือก: `dotnet run --project src/Aspire/SmartShop.AppHost` เปิดทุกอย่างพร้อม Aspire Dashboard ในคำสั่งเดียว

### 2.4 ลองใช้งานครบ Flow

ใช้ **ปุ่ม Dev login** ที่หน้า Login (พิมพ์ชื่อไหนก็ได้ ชื่อเดิมจะได้บัญชีเดิม) และเปิดหลาย Browser หรือหน้าต่าง Incognito เพื่อเป็นหลายคนพร้อมกัน

1. **ผู้ดูแลระบบ:** Dev login ชื่อ `admin` ✅ ติ๊ก **System admin** → **สร้างหมู่บ้าน** → ได้ **รหัสเข้าร่วม** และ QR
2. **เจ้าของร้าน** (หน้าต่างที่ 2): Dev login ชื่อ `paadaeng` → ใส่รหัสหมู่บ้าน → กรอกบ้านเลขที่ → รออนุมัติ
3. **admin:** ไอคอน 🛡️ → **สมาชิก** → อนุมัติ
4. **paadaeng:** หน้าแรก → **ขอเปิดร้าน** → กรอกข้อมูล
5. **admin:** ไอคอน 🛡️ → **คำขอร้าน** → อนุมัติ
6. **paadaeng:** โปรไฟล์ → **จัดการร้าน** → เพิ่มเมนู, ตั้งเวลาเปิด-ปิด, วิธีชำระเงิน → **เปิดร้าน**
7. **ลูกค้า** (หน้าต่างที่ 3): Dev login ชื่อ `somchai` → เข้าหมู่บ้าน (admin อนุมัติ) → สั่งของ
8. หน้าต่างร้านจะมี **เสียงเตือน** และ Order ใหม่ขึ้นทันที → รับ Order → เริ่มทำ → พร้อม → ส่ง → ลูกค้ากดได้รับ → รีวิว

> เสียงเตือนต้องมีการคลิกในหน้านั้นอย่างน้อย 1 ครั้งก่อน (นโยบายของ Browser)

### 2.5 คำสั่งที่ใช้บ่อย

```bash
docker compose -f deploy/docker-compose.dev.yml stop        # หยุดบริการ (ข้อมูลยังอยู่)
docker compose -f deploy/docker-compose.dev.yml down -v     # ลบทิ้งทั้งหมด เริ่มฐานข้อมูลใหม่
```

---

## 3. แบบ B: Docker ทั้งชุดบนเครื่องตัวเอง (โหมดทดลอง)

ใช้ไฟล์เดียวกับ Server จริง (`deploy/docker-compose.yml`) แต่เปิด Dev login ไว้ ไม่ต้องตั้งค่า LINE

### 3.1 Build Image บนเครื่อง

ใช้ **Git Bash** (Windows) หรือ Terminal (macOS/Linux):

```bash
bash scripts/build-images.sh        # ได้ smartshop-local/smartshop-{api,worker,web}:latest
```

### 3.2 สร้างไฟล์ `.env`

```bash
cd deploy
cp .env.example .env
```

แก้ไฟล์ `deploy/.env`:

```ini
DOMAIN=localhost
ADMIN_EMAIL=you@example.com
IMAGE_PREFIX=smartshop-local
TAG=latest
APP_ENVIRONMENT=Staging          # โหมดทดลอง
DEV_LOGIN_ENABLED=true           # มีปุ่ม Dev login
POSTGRES_PASSWORD=<สุ่ม>
RABBITMQ_PASSWORD=<สุ่ม>
JWT_SIGNING_KEY=<สุ่ม ยาวอย่างน้อย 32 ตัวอักษร>
MEDIA_SIGNING_KEY=<สุ่ม>
STORAGE_SECRET_KEY=<สุ่ม>
```

สร้างค่าสุ่มด้วย `openssl rand -hex 32` (มีใน Git Bash)

### 3.3 เปิดระบบ

```bash
docker compose up -d
docker compose ps        # api, worker ต้องขึ้น (healthy), migrator ต้องจบด้วย Exited (0)
```

เปิด **https://localhost** (Caddy ออก Certificate ภายในให้ Browser จะเตือนว่าไม่ปลอดภัย ให้กด "ดำเนินการต่อ") แล้วลองตามข้อ 2.4

ปิด: `docker compose down` · ลบข้อมูลทั้งหมด: `docker compose down -v`

> ⚠️ **ห้ามใช้โหมดทดลองบน Server จริง** เพราะใครก็ Login เป็นใครก็ได้ Dev login จะใช้ไม่ได้เสมอเมื่อ `APP_ENVIRONMENT=Production`

---

## 4. ตั้งค่า LINE (สำหรับแบบ C)

ทำที่ [LINE Developers Console](https://developers.line.biz/console/) ด้วยบัญชี LINE ของคุณ

> **สำคัญ:** LINE Login channel และ Messaging API channel ต้องอยู่ใต้ **Provider เดียวกัน** (User ID ของคนเดียวกันจะเป็นค่าเดียวกันเฉพาะใน Provider เดียวกัน ระบบใช้ค่านี้จับคู่คนที่ Login กับคนที่เป็นเพื่อน OA เพื่อส่งแจ้งเตือน)

### 4.1 สร้าง Provider
**Create a new provider** → ตั้งชื่อ เช่น `SmartShop หมู่บ้านสุขใจ`

### 4.2 Messaging API channel (LINE Official Account สำหรับส่งแจ้งเตือน)

1. ใน Provider → **Create a Messaging API channel** (ระบบจะพาไปสร้าง LINE Official Account)
2. แท็บ **Messaging API**
   - **Channel access token (long-lived)** → กด Issue → จดไว้ (`LINE_MESSAGING_ACCESS_TOKEN`)
   - **Webhook URL:** `https://<DOMAIN>/api/webhooks/line` → เปิด **Use webhook** (กด Verify ได้หลังติดตั้ง Server เสร็จ)
   - **Auto-reply messages / Greeting messages:** ปิดได้ (ตั้งที่ LINE Official Account Manager)
3. แท็บ **Basic settings** → **Channel secret** (`LINE_MESSAGING_CHANNEL_SECRET`)
4. [LINE Official Account Manager](https://manager.line.biz) → **เพิ่มเพื่อน** → คัดลอกลิงก์ `https://lin.ee/...` (`LINE_ADD_FRIEND_URL`)

โควต้า: แพ็กเกจฟรีส่งข้อความ Push ได้จำกัดต่อเดือน ระบบมี Quota guard (`LINE_MONTHLY_QUOTA`) ถ้าเกินจะส่งทาง Web Push และแจ้งเตือนในแอปแทน

### 4.3 LINE Login channel

1. ใน Provider เดียวกัน → **Create a LINE Login channel** → App types: เลือก **Web app**
2. แท็บ **Basic settings**
   - **Channel ID** (`LINE_LOGIN_CHANNEL_ID`) และ **Channel secret** (`LINE_LOGIN_CHANNEL_SECRET`)
   - **Linked LINE Official Account:** เลือก OA จากข้อ 4.2 (ตอน Login ระบบจะชวนให้เพิ่มเพื่อน OA ซึ่งจำเป็นสำหรับแจ้งเตือนทาง LINE)
   - ด้านล่างสุด **Your user ID** (`U...`) → `SYSTEM_ADMIN_LINE_USER_ID`
3. แท็บ **LINE Login** → **Callback URL:** `https://<DOMAIN>/auth/callback`
4. แท็บ **LIFF** → **Add**
   - Size: **Full**
   - Endpoint URL: `https://<DOMAIN>/`
   - Scopes: ✅ `openid` ✅ `profile`
   - Bot link feature: **On (Aggressive)**
   - คัดลอก **LIFF ID** (`LINE_LIFF_ID`) ลิงก์สำหรับแชร์ในกลุ่มหมู่บ้านคือ `https://liff.line.me/<LIFF ID>`
5. เมื่อพร้อมให้คนอื่นใช้ กด **Publish** ที่ channel (ระหว่างสถานะ Developing มีแค่ Admin/Tester ของ channel ที่ Login ได้)

---

## 5. แบบ C: ติดตั้งบน Server จริง

### 5.1 เตรียม Server
ตั้ง DNS A record ของ Domain ให้ชี้ IP ของ VPS ให้เรียบร้อยก่อน (ตรวจด้วย `nslookup shop.example.com`) ไม่อย่างนั้น Caddy ขอ HTTPS ไม่ได้

### 5.2 ติดตั้ง Docker บน Ubuntu

```bash
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER      # ออกแล้ว SSH เข้าใหม่
docker compose version
```

### 5.3 เตรียม Image (เลือกทางใดทางหนึ่ง)

**ทาง 1: Build บน Server** (ง่ายสุด)

```bash
sudo apt-get install -y dotnet-sdk-10.0      # หรือดูวิธีติดตั้งที่ learn.microsoft.com/dotnet/core/install/linux
git clone <URL ของ Repository> SmartShop && cd SmartShop
bash scripts/build-images.sh                  # IMAGE_PREFIX=smartshop-local
```

**ทาง 2: ใช้ Image จาก GitHub Container Registry**: Push Repository ขึ้น GitHub แล้ว Workflow `Containers` จะ Build และ Push ไปที่ `ghcr.io/<owner>/smartshop-*`
ตั้ง `IMAGE_PREFIX=ghcr.io/<owner>` ถ้า Package เป็น Private ต้อง `docker login ghcr.io` บน Server ก่อน (Server ต้องมีแค่โฟลเดอร์ `deploy/`)

### 5.4 สร้าง VAPID key (Web Push สำหรับแจ้งเตือนเมื่อส่ง LINE ไม่ได้)

```bash
docker run --rm smartshop-local/smartshop-api:latest vapid
```

### 5.5 ตั้งค่า `.env`

```bash
cd deploy && cp .env.example .env && nano .env
```

| ตัวแปร | ค่า |
|---|---|
| `DOMAIN` / `ADMIN_EMAIL` | Domain และอีเมลผู้ดูแล |
| `IMAGE_PREFIX` / `TAG` | `smartshop-local` / `latest` (หรือ `ghcr.io/<owner>`) |
| `APP_ENVIRONMENT` / `DEV_LOGIN_ENABLED` | **`Production` / `false`** |
| `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD`, `JWT_SIGNING_KEY`, `MEDIA_SIGNING_KEY`, `STORAGE_SECRET_KEY` | ค่าสุ่ม `openssl rand -hex 32` (ตั้งครั้งเดียว อย่าเปลี่ยน `POSTGRES_PASSWORD` หลังติดตั้ง) |
| `LINE_*`, `SYSTEM_ADMIN_LINE_USER_ID` | จากข้อ 4 |
| `VAPID_PUBLIC_KEY` / `VAPID_PRIVATE_KEY` | จากข้อ 5.4 |
| ไม่บังคับ | `DATABASE_ROW_LEVEL_SECURITY=true` (แยกข้อมูลหมู่บ้านระดับฐานข้อมูลเพิ่ม), `SLIP_VERIFIER_*`, `OTEL_EXPORTER_OTLP_ENDPOINT` |

เก็บไฟล์ `.env` ไว้ที่ปลอดภัย และ **ห้าม Commit ขึ้น Git**

### 5.6 เปิดระบบ

```bash
docker compose up -d
docker compose ps
docker compose logs -f caddy        # ดูว่าได้ Certificate แล้ว (certificate obtained successfully)
```

### 5.7 ตรวจหลังติดตั้ง

- [ ] `https://<DOMAIN>/health/ready` ตอบ `Healthy`
- [ ] `https://<DOMAIN>/app-config.json` มี `lineChannelId` และ `liffId` ตรงกับที่ตั้ง และ `devLoginEnabled` เป็น `false`
- [ ] LINE Developers → Messaging API → Webhook **Verify** สำเร็จ
- [ ] Login ด้วย LINE ด้วยบัญชีที่ตั้งเป็น `SYSTEM_ADMIN_LINE_USER_ID` → โปรไฟล์มีเมนู **ผู้ดูแลระบบ** → สร้างหมู่บ้าน
- [ ] เปิด `https://liff.line.me/<LIFF ID>` ในแอป LINE ต้องเข้าได้เลยโดยไม่ต้อง Login ซ้ำ
- [ ] เพิ่มเพื่อน OA → โปรไฟล์ → ทดสอบแจ้งเตือน ต้องได้ข้อความทาง LINE
- [ ] อัปโหลดรูป (โลโก้ร้าน/สินค้า) แล้วรูปขึ้น
- [ ] ลองสั่ง 1 Order ครบ Flow (ข้อ 2.4) โดยใช้ 2 บัญชี

### 5.8 ดูแลระบบ

```bash
docker compose pull && docker compose up -d          # อัปเดต (ทาง 2) หรือ build-images.sh ใหม่ (ทาง 1) แล้ว up -d
docker compose exec postgres pg_dump -U smartshop smartshop | gzip > backup-$(date +%F).sql.gz   # สำรองฐานข้อมูล
```

ควรสำรอง Volume `seaweedfs-data` (รูปทั้งหมด) และไฟล์ `.env` ด้วย
ถ้าต้องการกราฟ/Log ย้อนหลัง ดู `deploy/docker-compose.observability.yml` · Kubernetes ดู `deploy/helm/smartshop`

---

## 6. แก้ปัญหาที่พบบ่อย

| อาการ | สาเหตุ / วิธีแก้ |
|---|---|
| `dotnet run` แจ้งว่าไม่พบ SDK | ติดตั้ง .NET SDK 10.0.300 ขึ้นไป (`global.json` บังคับไว้) |
| API เริ่มไม่ได้ / ต่อฐานข้อมูลไม่ได้ (แบบ A) | Docker Desktop ยังไม่เปิด หรือ Port 5432 ถูกโปรแกรมอื่นใช้ (`docker compose -f deploy/docker-compose.dev.yml ps`) |
| Build ไม่ผ่าน "file is locked" | ปิด API/Worker/Web ที่รันค้างอยู่ก่อน Build ใหม่ |
| อัปโหลดรูปไม่ได้ (Error 500) | SeaweedFS ยังเริ่มไม่เสร็จ (รอ 10–20 วินาที) หรือ `STORAGE_ACCESS_KEY`/`STORAGE_SECRET_KEY` ถูกเปลี่ยนหลังติดตั้ง |
| ไม่มีปุ่ม Dev login | ปกติใน Production · แบบ B ต้องตั้ง `APP_ENVIRONMENT=Staging` และ `DEV_LOGIN_ENABLED=true` แล้ว `docker compose up -d` ใหม่ |
| LINE Login แจ้ง `redirect_uri` ไม่ถูกต้อง | Callback URL ใน LINE Login channel ต้องเป็น `https://<DOMAIN>/auth/callback` ตรงตัวอักษร |
| Login ได้แค่บัญชีผู้พัฒนา | กด **Publish** LINE Login channel หรือเพิ่มผู้ทดสอบใน Roles |
| ไม่ได้รับแจ้งเตือนทาง LINE | ผู้ใช้ยังไม่เพิ่มเพื่อน OA, Webhook ไม่ได้เปิด/Verify, Channel ไม่อยู่ Provider เดียวกัน หรือโควต้าหมด (ดูกล่อง **🔔 การแจ้งเตือนออเดอร์ใหม่** ในหน้าตั้งค่าร้าน) |
| Caddy ขอ Certificate ไม่ได้ | DNS ยังไม่ชี้มาที่ Server หรือ Port 80/443 ถูกปิด |
| Log มีบรรทัด `libgssapi_krb5.so.2` | เป็นแค่คำเตือน (ไม่ได้ใช้ Kerberos) ไม่มีผลกับการทำงาน |
| เสียงเตือน Order ไม่ดัง | คลิกในหน้าร้านค้า 1 ครั้งหลังเปิดหน้า (Browser บล็อกเสียงอัตโนมัติ) และเปิดหน้าทิ้งไว้ |

ดูเพิ่ม: [CONTRIBUTING.md](../CONTRIBUTING.md) (สำหรับนักพัฒนา), [03-architecture.md](03-architecture.md), [06-public-api.md](06-public-api.md)
