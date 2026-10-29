# 09 — แยกเป็น 2 เครื่อง (Hetzner Private Network)

ใช้เมื่อเครื่องเดิม RAM ไม่พอ เช่นมี Service อื่นรันร่วมด้วย ย้าย **ตัวแอป** (Caddy + web + api) ไปเครื่องใหม่ ส่วน **ข้อมูล**
(PostgreSQL, Valkey, SeaweedFS) และ Nginx อยู่เครื่องเดิม คุยกันผ่าน Private Network (ความหน่วงไม่ถึง 1 ms ใน Location เดียวกัน)

```
ผู้ใช้ → Cloudflare → Nginx (เครื่องเดิม, เหมือนเดิม)
                         │ http://10.0.0.2:8088  (Private Network)
                         ▼
        เครื่องใหม่ 10.0.0.2 (APP):  caddy + web + api        ≈ 700–800 MB
                         │ 10.0.0.3:5432 / :6379 / :8333
                         ▼
        เครื่องเดิม 10.0.0.3 (DATA): postgres + valkey + seaweedfs + Nginx + Service อื่น
```

- **ไม่ต้องย้ายข้อมูล** แอปไม่เก็บอะไรไว้ในเครื่อง ข้อมูลทั้งหมดอยู่ที่เครื่องเดิมเหมือนเดิม
- เครื่องเดิมได้ RAM คืนประมาณ 650–700 MB และ CPU ที่ใช้ย่อรูปย้ายไปเครื่องใหม่
- Downtime แค่ช่วงสลับ (ไม่กี่นาที) ถอยกลับได้ทันที (ดูหัวข้อท้ายคู่มือ)
- ตัวอย่างใช้ **10.0.0.3 = เครื่องเดิม (DATA)** และ **10.0.0.2 = เครื่องใหม่ (APP)** ให้เปลี่ยนตาม IP จริงใน Hetzner Console → Networks

> ต้องใช้ Location ที่อยู่ Network zone เดียวกัน (เช่น Singapore กับ Singapore) Private Network ข้าม zone ไม่ได้

---

## 0. สิ่งที่ต้องมีก่อน

- Hetzner Network (เช่น `10.0.0.0/16`) ผูกทั้งสองเครื่องแล้ว
- เครื่องใหม่มี **IPv4** (ไว้ดึง image จาก GHCR และให้แอปเรียก LINE / Web Push ได้) และติดตั้ง Docker แล้ว
- Hetzner Cloud Firewall ของเครื่องใหม่: เปิดแค่ SSH ก็พอ (Request เข้ามาทาง Private Network จาก Nginx)

---

## 1. เครื่องเดิม: เตรียมเครื่อง (ไม่กระทบระบบที่รันอยู่)

**1.1 เพิ่ม swap 2 GB** กันเครื่องค้างตอน RAM ตึง (Hetzner Cloud ไม่มี swap ให้):
```bash
sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile && sudo mkswap /swapfile && sudo swapon /swapfile
```
```bash
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab && echo 'vm.swappiness=10' | sudo tee /etc/sysctl.d/99-swap.conf && sudo sysctl -p /etc/sysctl.d/99-swap.conf
```

**1.2 ให้ Docker เปิดพอร์ตบน Private IP ได้แม้ตอนบูตที่ Network ยังไม่พร้อม** (ไม่อย่างนั้น container อาจขึ้นไม่ได้หลังรีบูต
ด้วยข้อความ `cannot assign requested address`):
```bash
echo 'net.ipv4.ip_nonlocal_bind=1' | sudo tee /etc/sysctl.d/99-nonlocal-bind.conf && sudo sysctl -p /etc/sysctl.d/99-nonlocal-bind.conf
```

**1.3 อัปเดตโค้ด และเพิ่มค่าใน `.env`** (ยังไม่ต้องเปลี่ยน `COMPOSE_FILE`):
```bash
cd ~/smartshop && git pull && cd deploy
```
```bash
echo "DATA_PRIVATE_IP=10.0.0.3" >> .env && echo "APP_PRIVATE_IP=10.0.0.2" >> .env && echo "VALKEY_PASSWORD=$(openssl rand -hex 24)" >> .env
```

---

## 2. เครื่องใหม่: เตรียมให้พร้อม (ยังไม่กระทบผู้ใช้)

**2.1 ดึงโค้ด**
```bash
git clone https://github.com/ungreanable/SmartShop.git ~/smartshop
```

**2.2 คัดลอก `.env` จากเครื่องเดิม** ทุกค่าต้องตรงกัน (รหัสผ่าน, JWT/Media key, LINE, VAPID, TAG)
คัดลอกผ่านเครื่องตัวเองก็ได้ หรือถ้าเครื่องใหม่ SSH เข้าเครื่องเดิมได้:
```bash
scp root@10.0.0.3:~/smartshop/deploy/.env ~/smartshop/deploy/.env
```

**2.3 ตั้ง `COMPOSE_FILE` ของเครื่องใหม่** แก้บรรทัด `COMPOSE_FILE=` ใน `~/smartshop/deploy/.env` ให้เป็น:
```
COMPOSE_FILE=docker-compose.yml:docker-compose.behind-proxy.yml:docker-compose.small.yml:docker-compose.split-app.yml
```
และตรวจว่า `CADDY_SITE=:80` และ `SMARTSHOP_HTTP_PORT` เป็นพอร์ตที่จะให้ Nginx เรียก (เช่น `8088`)

**2.4 ตรวจคอนฟิกและดึง image ไว้ก่อน**
```bash
cd ~/smartshop/deploy && docker compose config --services
```
ต้องได้แค่ `web`, `migrator`, `api`, `caddy`
```bash
docker compose pull
```

---

## 3. สลับ (Downtime เริ่มตรงนี้ ประมาณ 2–5 นาที)

**3.1 เครื่องเดิม: เปลี่ยนเป็นเครื่องข้อมูล** แก้ `COMPOSE_FILE` ใน `~/smartshop/deploy/.env` เป็น:
```
COMPOSE_FILE=docker-compose.yml:docker-compose.behind-proxy.yml:docker-compose.small.yml:docker-compose.split-data.yml
```
แล้วสร้าง container ข้อมูลใหม่ (Valkey จะเริ่มใช้รหัสผ่าน และเปิดพอร์ตบน 10.0.0.3):
```bash
cd ~/smartshop/deploy && docker compose up -d postgres valkey seaweedfs
```
ตรวจว่าพอร์ตเปิดเฉพาะ Private IP (ต้องขึ้น `10.0.0.3:` ไม่ใช่ `0.0.0.0:`):
```bash
sudo ss -ltnp | grep -E ':(5432|6379|8333) '
```

**3.2 เครื่องใหม่: เปิดแอป**
```bash
cd ~/smartshop/deploy && docker compose up -d
```
ดูให้ `migrator` จบด้วย exit 0 และ `api` เป็น healthy:
```bash
docker compose ps
```

**3.3 เครื่องเดิม: ทดสอบว่าเรียกแอปบนเครื่องใหม่ได้**
```bash
curl -s -o /dev/null -w "%{http_code}\n" http://10.0.0.2:8088/health/ready
```
ต้องได้ `200`

**3.4 เครื่องเดิม: ชี้ Nginx ไปเครื่องใหม่** แก้ upstream ใน `/etc/nginx/sites-available/smartshop.conf`:
```nginx
upstream smartshop {
    server 10.0.0.2:8088;   # เดิม 127.0.0.1:<พอร์ตเดิม>
    keepalive 32;
    keepalive_timeout 60s;
}
```
```bash
sudo nginx -t && sudo systemctl reload nginx
```
**Downtime จบตรงนี้** เปิดเว็บแล้วลอง Login, ดูร้าน, อัปโหลดรูป, กดทดสอบแจ้งเตือน

**3.5 เครื่องเดิม: ปิดแอปตัวเก่า คืน RAM**
```bash
cd ~/smartshop/deploy && docker compose stop caddy web api && docker compose rm -f caddy web api migrator
```
```bash
free -m && docker stats --no-stream --format "table {{.Name}}\t{{.MemUsage}}"
```

---

## 4. หลังย้ายแล้ว

- **อัปเดตเวอร์ชันใหม่:** ทำที่ **เครื่องใหม่** เท่านั้น (migrator รันจากเครื่องใหม่และอัปเดตฐานข้อมูลบนเครื่องเดิมให้)
  ```bash
  cd ~/smartshop && git pull && cd deploy
  ```
  ตั้ง `TAG=<เวอร์ชัน>` ใน `.env` ของ **ทั้งสองเครื่อง** (เลขเวอร์ชันที่โชว์ในแอปมาจากเครื่องใหม่) แล้วบนเครื่องใหม่:
  ```bash
  docker compose pull && docker compose up -d
  ```
- **เครื่องเดิม** อัปเดตเฉพาะตอนเปลี่ยน image ของ Postgres/Valkey/SeaweedFS หรือไฟล์ compose ฝั่งข้อมูล
- **สำรองข้อมูล** ยังทำที่เครื่องเดิมเหมือนเดิม (pg_dump และโฟลเดอร์ SeaweedFS)
- **เปลี่ยนค่าใน `.env`** เช่นรหัสผ่านหรือ key ต้องแก้ให้ตรงกัน **ทั้งสองเครื่อง**

---

## 5. ถอยกลับ (ถ้ามีปัญหา)

บนเครื่องเดิม:
1. แก้ upstream ของ Nginx กลับเป็น `127.0.0.1:<พอร์ตเดิม>` แล้ว `sudo nginx -t && sudo systemctl reload nginx`
2. แก้ `COMPOSE_FILE` กลับเป็นชุดเดิม (ไม่มี `split-data.yml`)
3. ```bash
   cd ~/smartshop/deploy && docker compose up -d
   ```
   Valkey จะกลับมาไม่มีรหัสผ่าน และพอร์ตที่เปิดบน Private IP จะปิดเอง
4. บนเครื่องใหม่: `docker compose down`

---

## 6. แก้ปัญหา

| อาการ | ตรวจ / แก้ |
|---|---|
| `api` บนเครื่องใหม่ไม่ healthy | `docker compose logs api --tail 50` ถ้าต่อฐานข้อมูลไม่ได้ ลอง `nc -zv 10.0.0.3 5432` จากเครื่องใหม่ |
| ต่อ Valkey ไม่ได้ / `NOAUTH` | `VALKEY_PASSWORD` สองเครื่องไม่ตรงกัน หรือยังไม่ได้รัน `docker compose up -d valkey` บนเครื่องเดิมหลังตั้งรหัส |
| อัปโหลดรูปไม่ได้ (`storage_unavailable`) | `nc -zv 10.0.0.3 8333` จากเครื่องใหม่ และ `STORAGE_ACCESS_KEY/SECRET_KEY` ต้องตรงกัน |
| หลังรีบูตเครื่องเดิม container ข้อมูลไม่ขึ้น (`cannot assign requested address`) | ยังไม่ได้ทำข้อ 1.2 (`ip_nonlocal_bind`) |
| เว็บขึ้น 502 จาก Cloudflare | `curl http://10.0.0.2:8088/health/ready` จากเครื่องเดิม และดูว่า Caddy เปิดบน `10.0.0.2` (`docker compose ps` บนเครื่องใหม่) |
| แจ้งเตือน LINE / Web Push ไม่ออก | เครื่องใหม่ต้องมี IPv4 (`curl -4 -s https://ifconfig.co`) |
