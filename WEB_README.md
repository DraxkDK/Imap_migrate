# IMAP Migration — Web UI

An optional, secured, multi-user web interface for the IMAP Migration tool.
The CLI core (`imap_migrate.py` + `src/`) is unchanged and still pure-stdlib;
the web layer lives in `web/` and only loads when you run it.

> ⚠️ This UI handles live mailbox credentials. **Always run it behind HTTPS**
> (a TLS-terminating reverse proxy) and never expose the app port directly.

---

## What it does

| Area | Capability |
|---|---|
| **Dashboard** | Live job status (auto-refresh), per-mailbox progress table |
| **Accounts** | Add/update/delete mailbox pairs, or import the CLI `accounts.csv` |
| **Run** | Test connections · Dry-run · Real migration (with incremental/resume) |
| **Reports** | View & download `migration_summary.csv`, `failed_items.csv`, `moved_items.csv` |
| **Logs** | Tail the migration log (passwords masked) |
| **Users** | Admin manages web users & roles |
| **Audit** | Admin reviews a log of security-relevant actions |

---

## Security model (defense against attackers)

This addresses the request to "prevent an attacker from accessing the web to
steal data". Layers, outermost first:

1. **HTTPS only** — TLS is terminated by Nginx/Caddy (see `deploy/`). The app
   runs on `127.0.0.1:8000` (loopback) and is never directly reachable.
   HSTS forces browsers to stay on HTTPS.
2. **Authentication** — every page except `/login` requires a session. Passwords
   are hashed with **scrypt** (salted, memory-hard). No password is ever stored
   or logged in plaintext.
3. **Brute-force protection** — after 5 failed logins an account is locked for
   15 minutes. Login responses don't reveal whether a username exists.
4. **Role-based access control** — `viewer` (read-only) < `operator` (run jobs +
   manage accounts) < `admin` (manage users + audit). Enforced server-side on
   every route.
5. **CSRF protection** — every state-changing request (POST/PUT/DELETE) must
   carry a per-session synchronizer token; mismatches are rejected with 400.
6. **Secure cookies** — session cookie is `HttpOnly`, `SameSite=Lax`, and
   `Secure` (HTTPS-only) in production. Session id rotates on login (anti-fixation).
7. **Hardened headers** — strict `Content-Security-Policy` (blocks injected/inline
   and third-party scripts → mitigates XSS), `X-Frame-Options: DENY`
   (anti-clickjacking), `X-Content-Type-Options: nosniff`, `Referrer-Policy:
   no-referrer`, `Cache-Control: no-store`.
8. **Credentials encrypted at rest** — stored IMAP passwords are encrypted with
   **Fernet (AES-128 + HMAC)**; the key is derived from `IMAP_WEB_SECRET_KEY`
   and never written to disk. Plaintext exists only in memory during a run.
9. **Path-traversal-safe downloads** — only fixed, allow-listed report/log
   filenames can be served.
10. **Audit logging** — logins, failures, account/user changes, job starts, and
    permission denials are recorded with user + IP + timestamp.
11. **Least-privilege deployment** — runs as a dedicated non-root service
    account with a hardened systemd sandbox (`deploy/imap-migrate-web.service`).

> Beyond the app, you can also restrict access at the proxy to a trusted
> office/VPN IP range (commented examples in `deploy/nginx.conf.example` and
> `deploy/Caddyfile.example`).

---

## Quick start (local development)

```bash
# 1. Install web dependencies (CLI stays stdlib-only)
python -m pip install -r requirements-web.txt

# 2. Generate the master secret and export it
export IMAP_WEB_SECRET_KEY="$(python manage_web.py gen-secret)"
export IMAP_WEB_BEHIND_PROXY=0        # allow plain http on localhost for dev

# 3. Create the first admin user
python manage_web.py create-admin --username admin

# 4. Run the dev server
python run_web.py                     # http://127.0.0.1:5000
```

On Windows PowerShell, set env vars with `$env:IMAP_WEB_SECRET_KEY = "..."`.

---

## Production deployment (HTTPS behind a reverse proxy)

```bash
# On the server, as a dedicated user (e.g. /opt/imap-migrate owned by imapmig):
python -m venv .venv && . .venv/bin/activate
pip install -r requirements-web.txt

# Configure secrets/paths:
sudo cp deploy/imap-migrate-web.env.example /etc/imap-migrate-web.env
sudo nano /etc/imap-migrate-web.env        # set IMAP_WEB_SECRET_KEY etc.
sudo chmod 640 /etc/imap-migrate-web.env

# Create the first admin:
set -a; . /etc/imap-migrate-web.env; set +a
python manage_web.py create-admin --username admin

# Run the app as a service (Gunicorn, single process + threads):
sudo cp deploy/imap-migrate-web.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now imap-migrate-web

# Terminate TLS at the proxy:
#   Nginx  → deploy/nginx.conf.example  (then: certbot --nginx -d migrate.example.com)
#   Caddy  → deploy/Caddyfile.example   (automatic HTTPS)
```

**Important:** run a **single** Gunicorn worker process (use `--threads` for
concurrency). The background migration job and live status live in process
memory and are not shared across processes.

---

## Roles

| Role | Dashboard / Reports / Logs | Manage accounts | Run jobs | Manage users / Audit |
|---|:---:|:---:|:---:|:---:|
| viewer | ✓ | | | |
| operator | ✓ | ✓ | ✓ | |
| admin | ✓ | ✓ | ✓ | ✓ |

Manage users from the **Users** page (admin) or via `manage_web.py`.

---

## Environment variables

| Variable | Required | Default | Purpose |
|---|:---:|---|---|
| `IMAP_WEB_SECRET_KEY` | **yes** | — | Session signing + credential encryption key |
| `IMAP_WEB_BEHIND_PROXY` | | `1` | Trust `X-Forwarded-*`, set Secure cookies (set `0` for local http) |
| `IMAP_WEB_DB` | | `web_app.db` | Users/accounts/audit SQLite DB |
| `IMAP_WEB_STATE_DB` | | `migration_state.db` | Migration state DB (shared with CLI) |
| `IMAP_WEB_LOG_DIR` / `IMAP_WEB_REPORT_DIR` | | `logs` / `reports` | Output dirs |
| `IMAP_SOURCE_HOST` / `IMAP_SOURCE_PORT` | | — / `993` | Default source server |
| `IMAP_TARGET_HOST` / `IMAP_TARGET_PORT` | | — / `993` | Default target server |
| `IMAP_WEB_MAX_WORKERS` | | `20` | Parallel mailbox workers |
| `IMAP_WEB_ALLOW_INSECURE_CERT` | | `0` | **Never `1` in prod** — disables IMAP TLS cert checks |

---

## Key handling

`IMAP_WEB_SECRET_KEY` is the crown jewel:
- **Losing it** → stored IMAP account passwords can no longer be decrypted
  (re-enter them in the UI).
- **Leaking it** → equivalent to leaking those passwords *and* the ability to
  forge sessions. Store it in a secret manager / env file with `chmod 640`,
  never in source control. `.gitignore` already excludes `*.env` and the DBs.

---

## Files added for the web UI

```
web/
  app.py        ← Flask application factory
  views.py      ← routes (auth + main blueprints)
  auth.py       ← password hashing, RBAC, brute-force lockout
  websec.py     ← CSRF, security headers, secure sessions, ProxyFix
  crypto.py     ← Fernet at-rest encryption for IMAP credentials
  store.py      ← SQLite: users, encrypted accounts, audit log
  jobs.py       ← background migration runner (wraps ParallelMigrator)
  templates/    ← Jinja2 templates
  static/       ← style.css, dashboard.js
wsgi.py         ← production WSGI entry (gunicorn/waitress)
run_web.py      ← local dev runner
manage_web.py   ← admin CLI (create users, gen secret)
requirements-web.txt
deploy/         ← nginx.conf / Caddyfile / systemd unit / env example
```
