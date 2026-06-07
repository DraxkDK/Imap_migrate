# Deploy: 1 web, 2 chức năng (migrate.draxk.com)

Hướng dẫn deploy **cả 2 app dưới 1 tên miền** trên **Linux** (Ubuntu/Debian).

```
                            ┌─  /         →  127.0.0.1:5000  (DKS Portal .NET)
  Nginx HTTPS  ────────────┤
  migrate.draxk.com         └─  /imap/    →  127.0.0.1:8000  (IMAP Migrate Flask)
```

Mọi lệnh chạy bằng `sudo` hoặc user có quyền sudo.

---

## 0. Chuẩn bị

```bash
# DNS: tạo bản ghi A   migrate.draxk.com  ->  <IP server>   (làm TRƯỚC certbot)
# Kiểm tra đã trỏ đúng chưa:
dig +short migrate.draxk.com

sudo apt update
sudo apt install -y nginx python3-venv python3-pip git certbot python3-certbot-nginx

# .NET 8 ASP.NET Core runtime (cho DKS Portal):
sudo apt install -y aspnetcore-runtime-8.0
# Nếu apt không có gói, cài qua Microsoft repo:
#   https://learn.microsoft.com/dotnet/core/install/linux
```

Đưa source lên server, ví dụ `/opt/src` (hoặc `git clone`). Đường dẫn dưới đây giả định:
- IMAP Migrate chạy ở `/opt/imap-migrate`
- DKS Portal chạy ở `/opt/dks-portal`

---

## 1. App 1 — DKS Portal (.NET) ở `/`

Build app này **cần .NET SDK** — build ngay trên server, hoặc build chỗ khác rồi copy thư mục publish lên.

```bash
# Tạo user dịch vụ + thư mục
sudo useradd --system --no-create-home --shell /usr/sbin/nologin dksportal
sudo mkdir -p /opt/dks-portal

# Publish (chạy ở máy có .NET SDK, trong thư mục project Portal):
cd Migrate/Automation/Portal/DKS.Migration.Portal
dotnet publish -c Release -o /opt/dks-portal

# (Tùy chọn) seed dữ liệu test + lấy token agent:
#   dotnet run -- --seed

# Phân quyền
sudo chown -R dksportal:dksportal /opt/dks-portal

# Cài service
sudo cp deploy/dks-portal.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now dks-portal
sudo systemctl status dks-portal --no-pager
curl -I http://127.0.0.1:5000        # phải trả về HTTP 200/302
```

---

## 2. App 2 — IMAP Migrate (Flask) ở `/imap/`

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin imapmig
sudo mkdir -p /opt/imap-migrate
# Copy source (web/, src/, wsgi.py, requirements-web.txt, manage_web.py ...) vào /opt/imap-migrate

cd /opt/imap-migrate
sudo python3 -m venv .venv
sudo .venv/bin/pip install -r requirements-web.txt gunicorn

# Tạo file env
sudo cp deploy/imap-migrate-web.env.example /etc/imap-migrate-web.env
sudo .venv/bin/python manage_web.py gen-secret      # copy chuỗi này
sudo nano /etc/imap-migrate-web.env                 # dán vào IMAP_WEB_SECRET_KEY, giữ IMAP_WEB_BEHIND_PROXY=1
sudo chown root:imapmig /etc/imap-migrate-web.env
sudo chmod 640 /etc/imap-migrate-web.env

# Tạo admin đầu tiên
sudo bash -c 'set -a; . /etc/imap-migrate-web.env; set +a; \
  /opt/imap-migrate/.venv/bin/python manage_web.py create-admin --username admin'

sudo chown -R imapmig:imapmig /opt/imap-migrate

# Cài service
sudo cp deploy/imap-migrate-web.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now imap-migrate-web
sudo systemctl status imap-migrate-web --no-pager
curl -I http://127.0.0.1:8000        # phải trả về HTTP 302 -> /login
```

---

## 3. Nginx + HTTPS

```bash
sudo cp deploy/nginx-combined.conf.example /etc/nginx/sites-available/migrate
sudo ln -sf /etc/nginx/sites-available/migrate /etc/nginx/sites-enabled/migrate
# Gỡ default nếu chiếm cổng 80/443:
sudo rm -f /etc/nginx/sites-enabled/default

sudo nginx -t                        # phải OK
sudo systemctl reload nginx

# Lấy chứng chỉ Let's Encrypt (tự sửa luôn 2 dòng ssl_certificate):
sudo certbot --nginx -d migrate.draxk.com
sudo nginx -t && sudo systemctl reload nginx
```

---

## 4. Kiểm tra

```bash
curl -I https://migrate.draxk.com/          # -> DKS Portal
curl -I https://migrate.draxk.com/imap/     # -> IMAP Migrate (302 /imap/login)
```

Mở trình duyệt:
- `https://migrate.draxk.com/` → DKS Portal, sidebar có mục **IMAP Migrate ↗**
- `https://migrate.draxk.com/imap/` → đăng nhập admin, menu có **DKS Portal ↗**

---

## 5. Agent (.NET) — sửa URL

Lệnh cài MSI và `appsettings.json` của agent phải trỏ về domain thật:

```
ApiUrl = https://migrate.draxk.com/api/agent
```

File MSI `DKSProfileAgent.msi` đặt vào nơi Portal phục vụ download
(`AgentDownloadUrl=/agent/DKSProfileAgent.msi` trong appsettings → đặt file ở
`/opt/dks-portal/wwwroot/agent/DKSProfileAgent.msi`).

---

## 6. Cập nhật về sau

```bash
# DKS Portal:
sudo systemctl stop dks-portal && dotnet publish -c Release -o /opt/dks-portal \
  && sudo chown -R dksportal:dksportal /opt/dks-portal && sudo systemctl start dks-portal
# IMAP Migrate:
# copy code mới vào /opt/imap-migrate rồi: sudo systemctl restart imap-migrate-web
```

## Lỗi thường gặp

- **502 Bad Gateway** → app backend chưa chạy: `journalctl -u dks-portal -e` / `-u imap-migrate-web -e`.
- **/imap/ bị vỡ CSS/redirect sai** → kiểm tra Nginx có `proxy_pass http://127.0.0.1:8000/;` (CÓ dấu `/` cuối) và `X-Forwarded-Prefix /imap`.
- **DKS Portal redirect loop** → thiếu `UseForwardedHeaders` (đã thêm trong Program.cs) hoặc Nginx chưa gửi `X-Forwarded-Proto`.
- **certbot fail** → DNS chưa trỏ về server, hoặc cổng 80 bị chặn.

> ⚠️ Bảo mật: DKS Portal (`/`) hiện CHƯA có đăng nhập — bất kỳ ai cũng xem được.
> Nên thêm auth, hoặc tạm giới hạn IP trong Nginx (`allow ...; deny all;`) cho tới khi có login.
