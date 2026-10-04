# 08 — Deploy บน Cloud หลัง Cloudflare (Hetzner + Nginx)

คู่มือนี้สำหรับกรณีที่มี **Server บน Cloud (เช่น Hetzner)** ซึ่งมี **Nginx** ใช้งานอยู่แล้ว และ **Domain อยู่บน Cloudflare**
ตัวอย่างใช้ Subdomain `shop.ungrean.com` ให้เปลี่ยนเป็นชื่อที่ต้องการ

```
ผู้ใช้ ──HTTPS──▶ Cloudflare ──HTTPS (Origin Certificate)──▶ Nginx บน Server :443
                                                              │
                                                              ▼ http://127.0.0.1:8088 (เข้าจากภายนอกไม่ได้)
                                                      Caddy (container) ── /api /hubs ─▶ api
                                                                         └─ อื่น ๆ ────▶ web
```

- Nginx ตัวเดิมทำ HTTPS และรับ Request ส่วน SmartShop รันใน Docker และเปิดแค่ `127.0.0.1:8088`
- ไม่กระทบเว็บอื่นที่ใช้ Nginx อยู่ เพราะเพิ่มแค่ไฟล์ Site ใหม่ 1 ไฟล์
- ระบบใช้ IP จริงของผู้ใช้จาก Header `CF-Connecting-IP` (ใช้จำกัดจำนวนครั้งการ Login ต่อคน)

> ยังไม่มี Nginx? ดูทางเลือกท้ายคู่มือ (Cloudflare Tunnel หรือให้ Caddy ทำ HTTPS เอง)

---

## 0. สิ่งที่ต้องเตรียม

| รายการ | หมายเหตุ |
|---|---|
| Server Ubuntu 22.04/24.04 ที่มี Docker + Compose plugin และ Nginx | RAM อย่างน้อย 4 GB (SmartShop ใช้ราว 1.5–2 GB) ตรวจสถาปัตยกรรมด้วย `uname -m` ซึ่งได้ `x86_64` (CX/CPX/CCX) หรือ `aarch64` (CAX/ARM) |
| สิทธิ์จัดการ Domain บน Cloudflare | สำหรับสร้าง DNS record และ Origin Certificate |
| เครื่อง Windows ที่ใช้พัฒนา (เครื่องนี้) | ใช้ Build Image แล้วส่งขึ้น Server |
| ค่าต่าง ๆ จาก LINE | ไม่บังคับในรอบแรก ทดลองด้วยโหมด Dev login ก่อนได้ (ข้อ 5) |

---

## 1. ตั้งค่า Cloudflare

1. **DNS** → Add record
   - Type `A`, Name `shop`, IPv4 ของ Server, Proxy status **Proxied (เมฆสีส้ม)**
   - ถ้ามี IPv6 เพิ่ม `AAAA` ด้วย
2. **SSL/TLS → Overview:** ตั้งเป็น **Full (strict)**
   - ถ้าเว็บอื่นใน Domain เดียวกันยังใช้โหมดอื่นอยู่ ให้สร้าง **Configuration Rule** เฉพาะ `shop.ungrean.com` แทน
3. **SSL/TLS → Origin Server → Create Certificate**
   - Hostnames: `*.ungrean.com`, `ungrean.com`, อายุ 15 ปี (ถ้ามี Origin Certificate สำหรับ Nginx อยู่แล้ว ใช้อันเดิมได้)
   - บันทึกเป็นไฟล์บน Server:

     ```bash
     sudo mkdir -p /etc/ssl/cloudflare
     sudo nano /etc/ssl/cloudflare/ungrean.com.pem     # วาง Origin Certificate
     sudo nano /etc/ssl/cloudflare/ungrean.com.key     # วาง Private Key
     sudo chmod 600 /etc/ssl/cloudflare/ungrean.com.key
     ```
4. **Network:** เปิด **WebSockets** ไว้ (ค่าเริ่มต้นเปิดอยู่แล้ว) จำเป็นสำหรับการแจ้ง Order แบบ Real-time
5. **Speed → Optimization:** ปิด **Rocket Loader** สำหรับ `shop.ungrean.com` เพราะทำให้ Blazor โหลดไม่ขึ้น (ใช้ Configuration Rule ได้)
6. **Security:** ถ้าเปิด **Bot Fight Mode** หรือ **Under Attack Mode** ไว้ ต้องยกเว้น Path ที่ไม่ใช่ Browser
   - ไปที่ **Security → WAF → Custom rules** → Create rule
   - Expression: `(http.host eq "shop.ungrean.com" and (starts_with(http.request.uri.path, "/api/webhooks/") or starts_with(http.request.uri.path, "/api/public/")))`
   - Action: **Skip** (ติ๊กทุก Security feature ที่ข้ามได้)
   - ถ้าไม่ยกเว้น LINE จะส่ง Webhook เข้ามาไม่ได้
7. **Caching:** ใช้ค่าเริ่มต้น **อย่า** ตั้ง "Cache Everything" ให้ Host นี้ (ระบบตั้ง `Cache-Control` ที่ถูกต้องไว้แล้ว และข้อมูลร้านเป็นข้อมูลเฉพาะสมาชิก)

## 2. Firewall ของ Server (แนะนำมาก)

ระบบเชื่อ IP ใน Header `CF-Connecting-IP` ที่ Nginx ส่งต่อมา ถ้ามีคนยิงตรงเข้า IP ของ Server โดยไม่ผ่าน Cloudflare ก็ปลอม Header นี้ได้
จึงควรให้ Port 80/443 รับเฉพาะ IP ของ Cloudflare:

- **Hetzner Cloud Console → Firewalls → Create Firewall**
- Inbound: `22/tcp` (เฉพาะ IP ของคุณ), `80/tcp` และ `443/tcp` จาก [ช่วง IP ของ Cloudflare](https://www.cloudflare.com/ips/) (ทั้ง IPv4 และ IPv6)
- Apply กับ Server

> ถ้าเว็บอื่นบน Server ไม่ได้อยู่หลัง Cloudflare และต้องเปิด 443 ให้ทุกคน ก็ยังใช้งานได้ ผลกระทบของการปลอม Header จำกัดอยู่ที่การจำกัดจำนวนครั้ง (Rate limit) เท่านั้น

## 3. เตรียม Image

### ทางหลัก: ใช้ Image จาก GitHub (ง่ายที่สุด)

ทุกครั้งที่ Push เข้า `main` ของ [github.com/ungreanable/SmartShop](https://github.com/ungreanable/SmartShop) Workflow **Containers** จะ Build Image ทั้ง x64 และ ARM ไปที่ `ghcr.io/ungreanable/smartshop-{api,worker,web}`
- Push เข้า `main` ได้ Tag `edge`
- สร้าง Release/Tag `v1.2.3` ได้ Tag `1.2.3` และ `latest`

บน Server ต้องมีแค่โฟลเดอร์ `deploy/`:

```bash
sudo mkdir -p /opt/smartshop && sudo chown $USER /opt/smartshop && cd /opt/smartshop
git clone --depth 1 https://github.com/ungreanable/SmartShop.git src && cp -r src/deploy . && cd deploy
```

ใน `.env` ตั้ง `IMAGE_PREFIX=ghcr.io/ungreanable` และ `TAG=edge` (หรือเลขเวอร์ชัน) แล้วรัน `docker compose pull`

> Package บน GHCR ที่สร้างครั้งแรกอาจเป็น Private ถ้า `docker compose pull` แจ้ง `denied` ให้ไปที่ GitHub → Profile → **Packages** → แต่ละ Package → **Package settings** → Change visibility → **Public**
> หรือบน Server รัน `docker login ghcr.io` ด้วย Token ที่มีสิทธิ์ `read:packages`

### ทางเลือก: Build บนเครื่อง Windows แล้วส่งขึ้น Server

ใช้ **Git Bash** ที่โฟลเดอร์โปรเจกต์:

```bash
# x86_64 (Hetzner CX/CPX/CCX) ใช้ linux-x64 · aarch64 (Hetzner CAX) ใช้ linux-arm64
bash scripts/build-images.sh smartshop-local latest linux-x64
```

```bash
docker save smartshop-local/smartshop-api:latest smartshop-local/smartshop-worker:latest smartshop-local/smartshop-web:latest | gzip > smartshop-images.tar.gz
```

```bash
ssh root@<server-ip> "mkdir -p /opt/smartshop"
scp smartshop-images.tar.gz root@<server-ip>:/opt/smartshop/
scp -r deploy root@<server-ip>:/opt/smartshop/
```

บน Server:

```bash
cd /opt/smartshop && gunzip -c smartshop-images.tar.gz | docker load
```

> ทางเลือก: ติดตั้ง .NET SDK บน Server แล้ว `git clone` + `bash scripts/build-images.sh` บน Server ได้เลย หรือ Push ขึ้น GitHub ให้ Workflow สร้าง Image ที่ `ghcr.io/<owner>` (ดูข้อ 5.3 ใน [07-installation.md](07-installation.md))

## 4. ตั้งค่า `.env` บน Server

```bash
cd /opt/smartshop/deploy
cp .env.example .env
chmod 600 .env
nano .env
```

| ตัวแปร | ค่า |
|---|---|
| `DOMAIN` | `shop.ungrean.com` |
| `CADDY_SITE` | `:80` (Caddy ไม่ทำ HTTPS เพราะ Nginx ทำแล้ว) |
| `SMARTSHOP_HTTP_PORT` | `8088` (ถ้าชนกับโปรแกรมอื่นบน Server ให้เปลี่ยน และแก้ใน Nginx ด้วย) |
| `IMAGE_PREFIX` / `TAG` | `ghcr.io/ungreanable` / `edge` (หรือ `smartshop-local` / `latest` ถ้า Build เอง) |
| `ADMIN_EMAIL` | อีเมลผู้ดูแล |
| `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD`, `JWT_SIGNING_KEY`, `MEDIA_SIGNING_KEY`, `STORAGE_SECRET_KEY` | ค่าสุ่มจาก `openssl rand -hex 32` คนละค่ากัน |
| `VAPID_PUBLIC_KEY` / `VAPID_PRIVATE_KEY` | รัน `docker run --rm smartshop-local/smartshop-api:latest vapid` |
| `APP_ENVIRONMENT` / `DEV_LOGIN_ENABLED` | ดูข้อ 5 |
| `LINE_*`, `SYSTEM_ADMIN_LINE_USER_ID`, `LINE_ADD_FRIEND_URL` | จาก LINE ([07-installation.md ข้อ 4](07-installation.md)) |

## 5. เลือกโหมดใช้งาน

**ทดลองก่อน (ยังไม่ตั้งค่า LINE):**

```ini
APP_ENVIRONMENT=Staging
DEV_LOGIN_ENABLED=true
```

> ⚠️ โหมดนี้ใครก็ Login เป็นใครก็ได้ ใช้ทดสอบชั่วคราวเท่านั้น (หรือจำกัดผู้เข้าถึงด้วย Cloudflare Access)

**ใช้งานจริง (มี LINE):**

```ini
APP_ENVIRONMENT=Production
DEV_LOGIN_ENABLED=false
```

ตั้งค่า LINE ตาม [07-installation.md ข้อ 4](07-installation.md) โดยใช้ URL เหล่านี้:

- LINE Login Callback URL: `https://shop.ungrean.com/auth/callback`
- LIFF Endpoint URL: `https://shop.ungrean.com/`
- Messaging API Webhook URL: `https://shop.ungrean.com/api/webhooks/line`

## 6. เปิด SmartShop

> **Server เล็ก (RAM 2 GB):** ใช้โปรไฟล์ `docker-compose.small.yml` เพิ่ม (ดูข้อ 9) ใช้ RAM ราว **335 MB** แทน 540 MB

```bash
cd /opt/smartshop/deploy
docker compose -f docker-compose.yml -f docker-compose.behind-proxy.yml up -d
docker compose -f docker-compose.yml -f docker-compose.behind-proxy.yml ps    # api/worker healthy, migrator Exited (0)
curl http://127.0.0.1:8088/health/ready                                      # Healthy
```

พิมพ์ `-f ... -f ...` ทุกครั้งยาวไป ใส่ไว้ใน `.env` ครั้งเดียวได้:

```ini
COMPOSE_FILE=docker-compose.yml:docker-compose.behind-proxy.yml
```

หลังจากนั้นใช้ `docker compose up -d`, `docker compose ps`, `docker compose logs -f api` ได้เลย

## 7. ตั้งค่า Nginx

```bash
sudo cp /opt/smartshop/deploy/nginx/smartshop.conf /etc/nginx/sites-available/smartshop.conf
sudo nano /etc/nginx/sites-available/smartshop.conf      # ตรวจ server_name, path ของ Certificate, port 8088
sudo ln -s /etc/nginx/sites-available/smartshop.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
```

- ถ้า `nginx -t` แจ้งว่า `$connection_upgrade` ซ้ำ ให้ลบบล็อก `map` ที่ต้นไฟล์ออก (แปลว่ามีอยู่แล้วใน `nginx.conf`)
- ไฟล์ตัวอย่างใช้ `listen 443 ssl http2;` ซึ่งใช้ได้ทุกเวอร์ชัน ถ้ามีคำเตือน `protocol options redefined for [::]:443` ให้ตั้ง `listen` ของทุก Site ที่ใช้ Port 443 ให้เหมือนกัน (มี `http2` ทุกไฟล์ หรือไม่มีเลย)

สิ่งที่ไฟล์นี้ตั้งไว้: Redirect HTTP→HTTPS, Origin Certificate, ส่ง `CF-Connecting-IP` ต่อให้ระบบ, WebSocket สำหรับ `/hubs/realtime` (Timeout 1 ชม.) และ Upload ได้ถึง 20 MB

## 8. ตรวจสอบ

- [ ] `https://shop.ungrean.com/health/ready` ตอบ `Healthy`
- [ ] `https://shop.ungrean.com/app-config.json` ค่า `lineChannelId` / `devLoginEnabled` ถูกต้อง
- [ ] เปิดหน้าเว็บแล้ว Login ได้ สร้างหมู่บ้าน เพิ่มสินค้าพร้อมรูปได้
- [ ] เปิด 2 เครื่อง (ลูกค้า / ร้าน) แล้วสั่ง Order หน้าร้านต้องเด้งและมีเสียงทันทีโดยไม่ต้องรีเฟรช (WebSocket ผ่าน Cloudflare + Nginx)
- [ ] (โหมดจริง) LINE Developers → Webhook → **Verify** สำเร็จ

ผ่านการทดสอบสาย Nginx → Caddy → API ด้วย Docker แล้ว: หน้าเว็บ, API, WebSocket (`101 Switching Protocols`) และ Rate limit แยกตาม IP จริงของแต่ละคน

## 9. ลดการใช้ทรัพยากร (Server เล็ก)

วัดจริงด้วยการใช้งานเบา ๆ (Login + เปิดหน้า + แจ้งเตือน):

| โปรไฟล์ | Container | RAM รวม |
|---|---|---|
| ปกติ | api, worker, web, rabbitmq, postgres, seaweedfs, caddy, valkey | ~540 MB |
| **เล็ก** (`docker-compose.small.yml`) | api (ทำงานเบื้องหลังเอง), web, postgres, seaweedfs, caddy, valkey | **~335 MB** |

โปรไฟล์เล็กทำอะไร:
- **ไม่มี RabbitMQ และ Worker:** API รันแบบ `Standalone` จัดการ Event และงานตั้งเวลาในตัว โดยคิวงานเก็บใน PostgreSQL จึงไม่หายตอนรีสตาร์ต ตัดได้ ~240 MB
- ลด Buffer ของ PostgreSQL และ Valkey ให้พอดีกับไม่กี่หมู่บ้าน
- จำกัด RAM ต่อ Container (`mem_limit`) ไม่ให้ตัวใดตัวหนึ่งกินจนเครื่องค้าง
- ทุกโปรไฟล์ใช้ .NET แบบ Workstation GC + Conserve memory อยู่แล้ว (ตั้งใน `docker-compose.yml`)

เปิดใช้ (ใส่ใน `.env` ครั้งเดียว):

```ini
COMPOSE_FILE=docker-compose.yml:docker-compose.behind-proxy.yml:docker-compose.small.yml
```

```bash
docker compose up -d
docker compose --profile full rm -sf worker rabbitmq     # ปิด Worker/RabbitMQ ตัวเดิม (ครั้งแรกที่เปลี่ยนโปรไฟล์)
docker volume rm smartshop_rabbitmq-data                 # ไม่บังคับ: คืนพื้นที่ดิสก์
```

กลับไปโปรไฟล์ปกติ (เช่น ขยายหลายเครื่อง): เอา `:docker-compose.small.yml` ออกจาก `COMPOSE_FILE` แล้ว `docker compose up -d` สลับไปมาได้ปลอดภัย

**แนะนำเพิ่มบน Server 2 GB:** สร้าง Swap 2 GB ไว้กันเครื่องค้างตอนหน่วยความจำพุ่ง (เช่น ตอน `docker compose pull`)

```bash
sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile && sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
```

ดูการใช้ RAM จริง: `docker stats --no-stream`

## 10. อัปเดตเวอร์ชันและสำรองข้อมูล

อัปเดต (Image จาก GitHub):

```bash
cd /opt/smartshop/deploy && docker compose pull && docker compose up -d       # migrator อัปเดตฐานข้อมูลให้อัตโนมัติ
```

ถ้า Build เอง ให้ทำข้อ 3 ทางเลือกใหม่ (Build → save → scp → load) แล้ว `docker compose up -d`

สำรองข้อมูล (ควรตั้ง cron ทุกวัน และเก็บไว้นอก Server ด้วย):

```bash
docker compose exec -T postgres pg_dump -U smartshop smartshop | gzip > /opt/smartshop/backup/db-$(date +%F).sql.gz
docker run --rm -v smartshop_seaweedfs-data:/data -v /opt/smartshop/backup:/backup alpine tar czf /backup/media-$(date +%F).tgz -C /data .
```

> ชื่อ Volume จริงดูได้จาก `docker volume ls` (ขึ้นต้นด้วยชื่อโฟลเดอร์ เช่น `deploy_seaweedfs-data`) และเก็บไฟล์ `.env` ไว้ที่ปลอดภัยด้วย

---

## ทางเลือกอื่น

### A. Cloudflare Tunnel (ไม่ต้องเปิด Port และไม่ต้องใช้ Nginx)

1. Cloudflare **Zero Trust → Networks → Tunnels → Create a tunnel** (Cloudflared) → คัดลอก Token
2. Public hostname: `shop.ungrean.com` → Service `HTTP` → `caddy:80`
3. `.env`: `CLOUDFLARE_TUNNEL_TOKEN=<token>` และ `CADDY_SITE=:80`
4. `docker compose -f docker-compose.yml -f docker-compose.cloudflare.yml up -d`

### B. Caddy ทำ HTTPS เอง (Server ไม่มี Nginx และ Port 80/443 ว่าง)

ใช้ `docker-compose.yml` ตามปกติ (ดู [07-installation.md](07-installation.md))
- ถ้าเปิดเมฆสีส้มไว้ ให้ใช้ Origin Certificate: วางไฟล์ไว้ที่ `deploy/certs/` แล้วตั้งค่าดังนี้

  ```ini
  TLS_DIRECTIVE=tls /etc/caddy/certs/origin.pem /etc/caddy/certs/origin.key
  ```
- ตั้ง `TRUSTED_PROXIES` เป็นช่วง IP ของ Cloudflare (คั่นด้วยช่องว่าง) ระบบจึงจะอ่าน IP จริงของผู้ใช้ได้

---

## แก้ปัญหา

| อาการ | สาเหตุ / วิธีแก้ |
|---|---|
| Cloudflare **521 / 522** | Nginx ไม่ทำงาน หรือ Firewall บล็อก IP ของ Cloudflare |
| Cloudflare **525 / 526** | SSL mode ไม่ใช่ Full (strict) หรือ Nginx ไม่ได้ใช้ Origin Certificate ของ Domain นี้ |
| Nginx **502 Bad Gateway** | SmartShop ยังไม่รัน (`docker compose ps`) หรือ Port ใน Nginx ไม่ตรงกับ `SMARTSHOP_HTTP_PORT` |
| หน้าเว็บค้างที่วงกลมโหลด | ปิด Rocket Loader และล้าง Cache ของ Cloudflare (Caching → Purge Everything) |
| Order ใหม่ไม่เด้ง ต้องรีเฟรช | WebSocket ถูกปิดใน Cloudflare หรือ Nginx ไม่มี `Upgrade` / `Connection` header |
| ทุกคนโดน **429** ตอน Login | Nginx ไม่ได้ส่ง `CF-Connecting-IP` ต่อ (ตรวจไฟล์ Site) |
| LINE Webhook Verify ไม่ผ่าน (403) | Bot Fight Mode / WAF บล็อก ให้เพิ่ม Rule ในข้อ 1.6 |
| อัปโหลดรูปไม่ได้ (413) | เพิ่ม `client_max_body_size` ใน Nginx |
| `exec format error` ตอนเริ่ม Container | Build Image ผิดสถาปัตยกรรม (x64 กับ arm64) ให้ทำข้อ 3 ใหม่ |
