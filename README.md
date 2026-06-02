# IMAP Migration Tool

Migrate mailboxes between any two IMAP servers — designed for
**MDaemon → iRedMail** but works with any IMAP-compatible pair.

Highlights
- Zero third-party dependencies (pure Python 3 stdlib)
- TLS/SSL on by default; certificate verification enforced
- Passwords **never** appear in logs or terminal output
- Resumable via SQLite state database — re-run safely
- Incremental / delta sync before cutover
- Dry-run and test-connection modes
- Bulk migration from CSV, single-mailbox from CLI
- Full audit log, summary CSV, failed-items CSV

---

## Architecture

```
imap_migrate.py          ← CLI entry point
src/
  config.py              ← CLI argument parsing, MigrationConfig
  security.py            ← TLS context, secret masking, file-permission checks
  logger.py              ← Logging with SensitiveDataFilter
  state_db.py            ← SQLite state (duplicate detection, resume)
  imap_client.py         ← IMAP4/IMAP4_SSL wrapper (connect, fetch, append)
  migrator.py            ← Migration engine (folders, batches, retry)
  report.py              ← Summary CSV, failed-items CSV
accounts.csv             ← Minimal accounts template
accounts_advanced.csv    ← Per-row host/port/ssl overrides template
config.example.ini       ← Example configuration file
requirements.txt         ← No third-party deps — documents stdlib modules used
```

---

## Requirements

- Python **3.8+** (3.10+ recommended for best TLS defaults)
- Linux/macOS server with network access to both mail servers
- IMAP access enabled on both source and target servers

```bash
python3 --version   # must be 3.8 or newer
```

---

## Installation

```bash
# 1. Clone or copy the tool to your migration server
git clone https://github.com/your-org/imap-migrate.git
# or just copy the folder

# 2. No pip install needed — all stdlib
python3 imap_migrate.py --help

# 3. (Optional) make it executable
chmod +x imap_migrate.py
```

---

## Quick Start

### 1. Prepare accounts.csv

```bash
cp accounts.csv my_accounts.csv
# Edit with real credentials
nano my_accounts.csv
chmod 600 my_accounts.csv    # IMPORTANT: restrict access
```

Minimum CSV format:

```
source_email,source_username,source_password,target_email,target_username,target_password
user1@old.com,user1@old.com,Password1!,user1@new.com,user1@new.com,Password1!
user2@old.com,user2@old.com,Password2!,user2@new.com,user2@new.com,Password2!
```

Optional additional columns per row (override the global CLI flags):

```
source_host,source_port,target_host,target_port,use_ssl
```

---

### 2. Test connections

Verify credentials before migrating any data:

```bash
python3 imap_migrate.py \
  --source-host mail.old-domain.com \
  --target-host mail.new-domain.com \
  --csv my_accounts.csv \
  --test-connection
```

Expected output:

```
[TEST] Source OK: user1@old.com @ mail.old-domain.com:993
[TEST] Target OK: user1@new.com @ mail.new-domain.com:993
Test complete — all connections OK
```

---

### 3. Dry run

Simulate migration without writing any data to the target:

```bash
python3 imap_migrate.py \
  --source-host mail.old-domain.com \
  --target-host mail.new-domain.com \
  --csv my_accounts.csv \
  --ssl \
  --dry-run
```

The state database is not written in dry-run mode.

---

### 4. Single mailbox migration

```bash
python3 imap_migrate.py \
  --source-host mail.old-domain.com \
  --source-port 993 \
  --source-user user1@old-domain.com \
  --target-host mail.new-domain.com \
  --target-port 993 \
  --target-user user1@new-domain.com \
  --ssl \
  --log user1_migration.log
```

If you omit `--source-pass` / `--target-pass`, the tool prompts
for passwords interactively (recommended — no secrets in shell history).

---

### 5. Bulk migration from CSV

```bash
python3 imap_migrate.py \
  --source-host mail.old-domain.com \
  --source-port 993 \
  --target-host mail.new-domain.com \
  --target-port 993 \
  --csv my_accounts.csv \
  --ssl \
  --log migration.log
```

---

### 6. Incremental / delta sync before cutover

After the initial migration, run incremental mode to catch new mail:

```bash
python3 imap_migrate.py \
  --source-host mail.old-domain.com \
  --target-host mail.new-domain.com \
  --csv my_accounts.csv \
  --ssl \
  --incremental \
  --log incremental.log
```

Already-migrated messages are detected by UID and Message-ID in the
SQLite state database (`migration_state.db`) and skipped instantly.

---

### 7. Resume after failure

Simply re-run the same command. The state database ensures no message
is imported twice:

```bash
# Same command as the original run — already migrated messages are skipped
python3 imap_migrate.py \
  --source-host mail.old-domain.com \
  --target-host mail.new-domain.com \
  --csv my_accounts.csv \
  --ssl \
  --log migration.log
```

---

### 8. Migrate specific folders only

```bash
python3 imap_migrate.py \
  --source-host mail.old-domain.com \
  --target-host mail.new-domain.com \
  --csv my_accounts.csv \
  --ssl \
  --folders INBOX Sent Drafts
```

---

### 9. Port 143 with STARTTLS (no SSL)

```bash
python3 imap_migrate.py \
  --source-host mail.old-domain.com \
  --source-port 143 \
  --target-host mail.new-domain.com \
  --target-port 143 \
  --no-ssl \
  --csv my_accounts.csv
```

STARTTLS is attempted automatically on port 143 if the server advertises it.

---

## Reviewing logs

```bash
# Live tail during migration
tail -f migration.log

# Count migrated
grep "Migrated UID" migration.log | wc -l

# Find errors
grep ERROR migration.log

# View summary report
column -t -s, migration_summary.csv

# View failed items
cat failed_items.csv
```

---

## Output Files

| File | Description |
|---|---|
| `migration.log` | Full audit log (DEBUG level) — no passwords |
| `migration_state.db` | SQLite DB — tracks every migrated message |
| `migration_summary.csv` | One row per mailbox: counts + status |
| `failed_items.csv` | One row per failed message: UID + error |

### migration_summary.csv columns

```
source_mailbox, target_mailbox, total_folders, total_messages,
migrated, skipped, failed, status, start_time, end_time, error
```

### failed_items.csv columns

```
source_mailbox, folder, uid, message_id, error
```

---

## Folder Mapping

The tool applies built-in MDaemon → standard IMAP name translations:

| MDaemon folder | iRedMail / Dovecot folder |
|---|---|
| Sent Items | Sent |
| Sent Messages | Sent |
| Deleted Items | Trash |
| Deleted Messages | Trash |
| Junk E-mail | Junk |
| Junk Email | Junk |
| Spam | Junk |
| Archive | Archive |

All other folder names are preserved as-is.
Custom mappings can be added in `config.example.ini`.

---

## Environment Variables

Use environment variables to avoid secrets in the shell history:

```bash
export IMAP_SOURCE_HOST=mail.old-domain.com
export IMAP_SOURCE_USER=user1@old-domain.com
export IMAP_SOURCE_PASS=SecretPassword1

export IMAP_TARGET_HOST=mail.new-domain.com
export IMAP_TARGET_USER=user1@new-domain.com
export IMAP_TARGET_PASS=SecretPassword2

python3 imap_migrate.py --ssl --log migration.log
```

---

## Security Notes

1. **Passwords are never written to any log file.**
   `SensitiveDataFilter` masks all `password=`, `passwd=`, `secret=`,
   `token=` patterns before any log record is stored.

2. **TLS is on by default.** Port 993 (IMAPS) is used unless you
   explicitly pass `--no-ssl`.

3. **Certificate verification is on by default.**
   `--allow-insecure-cert` disables it but prints a prominent warning.
   Use only on isolated test networks.

4. **Protect your accounts.csv.**
   ```bash
   chmod 600 accounts.csv
   ```
   The tool will warn if the file has group- or world-readable permissions
   on Linux/macOS.

5. **Run as a dedicated service account**, not root or your personal user.

6. **Message bodies are never logged.** Only folder names, UIDs,
   Message-IDs, and counts appear in logs.

7. **State database (`migration_state.db`) is local only** and contains
   no message content — only UIDs and Message-IDs.

---

## Security Checklist

- [ ] `accounts.csv` is `chmod 600`
- [ ] Running over port 993 (IMAPS) or port 143 + STARTTLS
- [ ] `--allow-insecure-cert` is NOT used in production
- [ ] Passwords supplied via env vars or interactive prompt, not `--source-pass`
- [ ] Migration server has no other users with access to the working directory
- [ ] Log files are stored in a restricted directory
- [ ] `migration_state.db` is backed up before each run
- [ ] Dry run completed successfully before the real migration

---

## Testing Checklist

- [ ] `--test-connection` passes for all mailboxes in CSV
- [ ] `--dry-run` completes without errors
- [ ] Single test mailbox migrated and verified in target mail client
- [ ] Folders created correctly on target
- [ ] Email flags (read/unread, starred) preserved
- [ ] Email dates preserved
- [ ] Re-run with same CSV produces 0 migrated, N skipped (no duplicates)
- [ ] `--incremental` after adding a new message to source copies only that message
- [ ] `migration_summary.csv` totals match expected counts
- [ ] `failed_items.csv` is empty (or failures investigated)
- [ ] `migration.log` contains no plaintext passwords

---

## Troubleshooting

### "Authentication failed"
- Verify username and password manually with a mail client
- MDaemon: ensure IMAP is enabled per-account
- iRedMail: check `/var/log/dovecot/auth.log`

### "Cannot connect to host"
- Confirm firewall allows port 993 (or 143) from the migration server
- Test with: `nc -zv mail.old-domain.com 993`
- Check DNS: `dig mail.old-domain.com`

### "TLS certificate error"
- Target server may use a self-signed cert
- Import the cert or use `--allow-insecure-cert` (test only)
- Check cert validity: `openssl s_client -connect mail.old-domain.com:993`

### "APPEND failed"
- Target mailbox may be over quota
- Target server may have per-folder or per-message size limits
- Check target server logs: `/var/log/dovecot/mail.log`

### Migration is slow
- Increase `--batch-size` (try 100–200)
- Run multiple instances with different CSV slices (non-overlapping mailboxes)
- Use `--folders INBOX` to migrate the largest folder first

### Duplicate messages after re-run
- State DB may have been deleted — keep `migration_state.db`
- Check Message-ID uniqueness: some old mail systems reuse IDs
- The fallback duplicate check uses UID; both methods are applied

---

## Extending to Other Platforms

The tool is modular. To add a new target platform:

1. **MDaemon → Microsoft 365**: Microsoft 365 exposes IMAP; just point
   `--target-host outlook.office365.com --target-port 993 --ssl`.
   OAuth2 is not yet supported — use App Password.

2. **MDaemon → Zimbra**: Zimbra uses standard IMAP. Use standard
   connection flags; folder names may need custom `folder_mapping`.

3. **Generic IMAP → IMAP**: Already supported out of the box.
   Pass any source/target host with appropriate credentials.

4. **API-based targets (Microsoft Graph, Gmail API)**: Extend
   `src/imap_client.py` by creating an alternative `TargetClient`
   class with the same `ensure_folder` / `append_message` interface,
   then wire it into `src/migrator.py`.

---

## License

MIT License — use freely, at your own risk. No warranty.
Always test on non-production mailboxes first.
#   I m a p - M i g r a t e  
 