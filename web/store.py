"""
Persistent storage for the web UI (SQLite), kept separate from the migration
state DB.

Two concerns live here:

  users            — login accounts for the web UI (hashed passwords, roles,
                     brute-force lockout counters)
  imap_accounts    — the mailbox pairs to migrate. IMAP *passwords* are
                     encrypted at rest with Fernet; the plaintext only ever
                     exists in memory while a migration is actually running.
  audit_log        — security-relevant actions (who did what, when, from where)

All access goes through a per-process threading.Lock + WAL mode so the Flask
request threads and the background migration thread can share one DB safely.
"""
import json
import sqlite3
import threading
from contextlib import contextmanager
from datetime import datetime, timezone
from typing import List, Optional

from .crypto import SecretBox

_DDL = """
PRAGMA journal_mode=WAL;
PRAGMA busy_timeout=5000;
PRAGMA foreign_keys=ON;

CREATE TABLE IF NOT EXISTS users (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    username        TEXT    NOT NULL UNIQUE,
    password_hash   TEXT    NOT NULL,
    role            TEXT    NOT NULL DEFAULT 'viewer',
    is_active       INTEGER NOT NULL DEFAULT 1,
    failed_attempts INTEGER NOT NULL DEFAULT 0,
    locked_until    TEXT    DEFAULT '',
    last_login      TEXT    DEFAULT '',
    created_at      TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS imap_accounts (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    source_email    TEXT    NOT NULL,
    source_username TEXT    NOT NULL,
    source_secret   TEXT    NOT NULL,          -- Fernet ciphertext
    target_email    TEXT    NOT NULL,
    target_username TEXT    NOT NULL,
    target_secret   TEXT    NOT NULL,          -- Fernet ciphertext
    source_host     TEXT    DEFAULT '',
    source_port     INTEGER,
    target_host     TEXT    DEFAULT '',
    target_port     INTEGER,
    use_ssl         INTEGER,                   -- NULL = inherit global
    created_at      TEXT    NOT NULL,
    UNIQUE (source_email, target_email)
);

CREATE TABLE IF NOT EXISTS audit_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    ts          TEXT    NOT NULL,
    username    TEXT    DEFAULT '',
    action      TEXT    NOT NULL,
    detail      TEXT    DEFAULT '',
    ip          TEXT    DEFAULT ''
);
"""


def _now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


class Store:
    def __init__(self, db_path: str, box: SecretBox) -> None:
        self.db_path = db_path
        self._box = box
        self._lock = threading.Lock()
        self._conn = sqlite3.connect(db_path, check_same_thread=False, timeout=10)
        self._conn.row_factory = sqlite3.Row
        self._conn.executescript(_DDL)
        self._conn.commit()

    def close(self) -> None:
        with self._lock:
            self._conn.close()

    @contextmanager
    def _write(self):
        with self._lock:
            cur = self._conn.cursor()
            try:
                yield cur
                self._conn.commit()
            except Exception:
                self._conn.rollback()
                raise
            finally:
                cur.close()

    def _read_all(self, sql: str, params: tuple = ()) -> List[sqlite3.Row]:
        with self._lock:
            cur = self._conn.cursor()
            try:
                cur.execute(sql, params)
                return cur.fetchall()
            finally:
                cur.close()

    def _read_one(self, sql: str, params: tuple = ()) -> Optional[sqlite3.Row]:
        with self._lock:
            cur = self._conn.cursor()
            try:
                cur.execute(sql, params)
                return cur.fetchone()
            finally:
                cur.close()

    # ------------------------------------------------------------------
    # Users
    # ------------------------------------------------------------------

    def count_users(self) -> int:
        row = self._read_one("SELECT COUNT(*) AS n FROM users")
        return int(row["n"]) if row else 0

    def get_user(self, username: str) -> Optional[sqlite3.Row]:
        return self._read_one("SELECT * FROM users WHERE username=?", (username,))

    def get_user_by_id(self, user_id: int) -> Optional[sqlite3.Row]:
        return self._read_one("SELECT * FROM users WHERE id=?", (user_id,))

    def list_users(self) -> List[sqlite3.Row]:
        return self._read_all("SELECT * FROM users ORDER BY username")

    def create_user(self, username: str, password_hash: str, role: str) -> None:
        with self._write() as cur:
            cur.execute(
                "INSERT INTO users (username, password_hash, role, created_at) "
                "VALUES (?,?,?,?)",
                (username, password_hash, role, _now()),
            )

    def set_password(self, username: str, password_hash: str) -> None:
        with self._write() as cur:
            cur.execute(
                "UPDATE users SET password_hash=?, failed_attempts=0, locked_until='' "
                "WHERE username=?",
                (password_hash, username),
            )

    def set_role(self, username: str, role: str) -> None:
        with self._write() as cur:
            cur.execute("UPDATE users SET role=? WHERE username=?", (role, username))

    def set_active(self, username: str, active: bool) -> None:
        with self._write() as cur:
            cur.execute(
                "UPDATE users SET is_active=? WHERE username=?",
                (1 if active else 0, username),
            )

    def delete_user(self, username: str) -> None:
        with self._write() as cur:
            cur.execute("DELETE FROM users WHERE username=?", (username,))

    def record_login_success(self, username: str) -> None:
        with self._write() as cur:
            cur.execute(
                "UPDATE users SET failed_attempts=0, locked_until='', last_login=? "
                "WHERE username=?",
                (_now(), username),
            )

    def record_login_failure(self, username: str, lock_threshold: int,
                             locked_until: str) -> None:
        with self._write() as cur:
            cur.execute(
                "UPDATE users SET failed_attempts=failed_attempts+1 WHERE username=?",
                (username,),
            )
            cur.execute(
                "UPDATE users SET locked_until=? "
                "WHERE username=? AND failed_attempts>=?",
                (locked_until, username, lock_threshold),
            )

    # ------------------------------------------------------------------
    # IMAP accounts  (passwords encrypted at rest)
    # ------------------------------------------------------------------

    def list_accounts(self) -> List[dict]:
        """Return account metadata WITHOUT decrypting secrets (for display)."""
        rows = self._read_all("SELECT * FROM imap_accounts ORDER BY source_email")
        out = []
        for r in rows:
            d = dict(r)
            d.pop("source_secret", None)
            d.pop("target_secret", None)
            out.append(d)
        return out

    def add_account(self, *, source_email: str, source_username: str,
                    source_password: str, target_email: str, target_username: str,
                    target_password: str, source_host: str = "", source_port=None,
                    target_host: str = "", target_port=None, use_ssl=None) -> None:
        with self._write() as cur:
            cur.execute(
                "INSERT INTO imap_accounts "
                "(source_email, source_username, source_secret, target_email, "
                " target_username, target_secret, source_host, source_port, "
                " target_host, target_port, use_ssl, created_at) "
                "VALUES (?,?,?,?,?,?,?,?,?,?,?,?) "
                "ON CONFLICT (source_email, target_email) DO UPDATE SET "
                "  source_username=excluded.source_username, "
                "  source_secret=excluded.source_secret, "
                "  target_username=excluded.target_username, "
                "  target_secret=excluded.target_secret, "
                "  source_host=excluded.source_host, source_port=excluded.source_port, "
                "  target_host=excluded.target_host, target_port=excluded.target_port, "
                "  use_ssl=excluded.use_ssl",
                (
                    source_email, source_username, self._box.encrypt(source_password),
                    target_email, target_username, self._box.encrypt(target_password),
                    source_host, source_port, target_host, target_port,
                    None if use_ssl is None else (1 if use_ssl else 0), _now(),
                ),
            )

    def delete_account(self, account_id: int) -> None:
        with self._write() as cur:
            cur.execute("DELETE FROM imap_accounts WHERE id=?", (account_id,))

    def get_account_pairs(self) -> List[dict]:
        """Return all accounts WITH decrypted passwords — call only when launching
        a migration. The plaintext lives only in the returned dicts."""
        rows = self._read_all("SELECT * FROM imap_accounts ORDER BY source_email")
        out = []
        for r in rows:
            out.append({
                "source_email": r["source_email"],
                "source_username": r["source_username"],
                "source_password": self._box.decrypt(r["source_secret"]),
                "target_email": r["target_email"],
                "target_username": r["target_username"],
                "target_password": self._box.decrypt(r["target_secret"]),
                "source_host": r["source_host"] or None,
                "source_port": r["source_port"],
                "target_host": r["target_host"] or None,
                "target_port": r["target_port"],
                "use_ssl": None if r["use_ssl"] is None else bool(r["use_ssl"]),
            })
        return out

    # ------------------------------------------------------------------
    # Audit log
    # ------------------------------------------------------------------

    def audit(self, action: str, *, username: str = "", detail: str = "",
              ip: str = "") -> None:
        with self._write() as cur:
            cur.execute(
                "INSERT INTO audit_log (ts, username, action, detail, ip) "
                "VALUES (?,?,?,?,?)",
                (_now(), username, action, detail[:500], ip),
            )

    def recent_audit(self, limit: int = 200) -> List[sqlite3.Row]:
        return self._read_all(
            "SELECT * FROM audit_log ORDER BY id DESC LIMIT ?", (limit,)
        )
