"""
Thread-safe SQLite state database — move-aware schema.

Tables
------
migrated_messages
  Stores every successfully migrated message with its fingerprint.
  The fingerprint allows cross-folder duplicate / move detection.

job_status
  Per-mailbox job lifecycle tracking for parallel workers.

SQLite WAL mode + internal threading.Lock ensure safe concurrent access
from up to 200 worker threads.
"""
import logging
import sqlite3
import threading
from contextlib import contextmanager
from datetime import datetime, timezone
from typing import Optional

logger = logging.getLogger(__name__)

_DDL = """
PRAGMA journal_mode=WAL;
PRAGMA busy_timeout=5000;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS migrated_messages (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    source_mailbox      TEXT    NOT NULL,
    target_mailbox      TEXT    NOT NULL,
    source_folder       TEXT    NOT NULL,
    target_folder       TEXT    NOT NULL,
    source_uid          TEXT    NOT NULL,
    target_uid          TEXT,
    message_id          TEXT,
    message_fingerprint TEXT    NOT NULL DEFAULT '',
    message_date        TEXT    DEFAULT '',
    message_size        INTEGER DEFAULT 0,
    migration_time      TEXT    NOT NULL,
    status              TEXT    NOT NULL DEFAULT 'success',
    UNIQUE (source_mailbox, source_folder, source_uid)
);

CREATE INDEX IF NOT EXISTS idx_fingerprint
    ON migrated_messages (source_mailbox, target_mailbox, message_fingerprint)
    WHERE status = 'success' AND message_fingerprint != '';

CREATE INDEX IF NOT EXISTS idx_message_id
    ON migrated_messages (message_id)
    WHERE message_id IS NOT NULL AND message_id != '';

CREATE TABLE IF NOT EXISTS job_status (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    source_mailbox  TEXT    NOT NULL UNIQUE,
    target_mailbox  TEXT    NOT NULL,
    status          TEXT    NOT NULL DEFAULT 'pending',
    worker_id       TEXT    DEFAULT '',
    start_time      TEXT    DEFAULT '',
    end_time        TEXT    DEFAULT '',
    total_messages  INTEGER DEFAULT 0,
    migrated        INTEGER DEFAULT 0,
    skipped         INTEGER DEFAULT 0,
    failed_msgs     INTEGER DEFAULT 0,
    error           TEXT    DEFAULT '',
    retry_count     INTEGER DEFAULT 0
);
"""


def _now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


class StateDB:
    def __init__(self, db_path: str) -> None:
        self.db_path = db_path
        self._conn: Optional[sqlite3.Connection] = None
        self._lock = threading.Lock()

    # ------------------------------------------------------------------
    # Lifecycle
    # ------------------------------------------------------------------

    def open(self) -> None:
        self._conn = sqlite3.connect(
            self.db_path, check_same_thread=False, timeout=10
        )
        self._conn.row_factory = sqlite3.Row
        self._conn.executescript(_DDL)
        self._conn.commit()
        logger.debug("State DB opened (WAL): %s", self.db_path)

    def close(self) -> None:
        if self._conn:
            self._conn.close()
            self._conn = None

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

    def _read_one(self, sql: str, params: tuple = ()):
        cur = self._conn.cursor()
        try:
            cur.execute(sql, params)
            return cur.fetchone()
        finally:
            cur.close()

    # ------------------------------------------------------------------
    # UID-level duplicate check
    # ------------------------------------------------------------------

    def is_uid_migrated(
        self, source_mailbox: str, source_folder: str, source_uid: str
    ) -> bool:
        row = self._read_one(
            "SELECT 1 FROM migrated_messages "
            "WHERE source_mailbox=? AND source_folder=? AND source_uid=? AND status='success' LIMIT 1",
            (source_mailbox, source_folder, source_uid),
        )
        return row is not None

    # ------------------------------------------------------------------
    # Message-ID duplicate check (legacy / quick path)
    # ------------------------------------------------------------------

    def is_message_id_migrated(
        self, source_mailbox: str, target_mailbox: str, message_id: str
    ) -> bool:
        if not message_id:
            return False
        row = self._read_one(
            "SELECT 1 FROM migrated_messages "
            "WHERE source_mailbox=? AND target_mailbox=? AND message_id=? AND status='success' LIMIT 1",
            (source_mailbox, target_mailbox, message_id),
        )
        return row is not None

    # ------------------------------------------------------------------
    # Fingerprint-based cross-folder lookup (move detection)
    # ------------------------------------------------------------------

    def find_by_fingerprint(
        self,
        source_mailbox: str,
        target_mailbox: str,
        fingerprint: str,
    ) -> Optional[dict]:
        """
        Return the first successful migration record matching this fingerprint.
        Returns None if the message has never been migrated.
        Returns a dict with keys: source_folder, target_folder, source_uid, target_uid.
        """
        if not fingerprint:
            return None
        row = self._read_one(
            "SELECT source_folder, target_folder, source_uid, target_uid "
            "FROM migrated_messages "
            "WHERE source_mailbox=? AND target_mailbox=? "
            "  AND message_fingerprint=? AND status='success' "
            "ORDER BY id DESC LIMIT 1",
            (source_mailbox, target_mailbox, fingerprint),
        )
        if row is None:
            return None
        return dict(row)

    def update_fingerprint_location(
        self,
        source_mailbox: str,
        fingerprint: str,
        new_src_folder: str,
        new_src_uid: str,
        new_tgt_folder: Optional[str] = None,
        new_target_uid: Optional[str] = None,
    ) -> None:
        """Update the stored folder/UID after a successful IMAP MOVE on target."""
        with self._write() as cur:
            if new_tgt_folder:
                cur.execute(
                    "UPDATE migrated_messages "
                    "SET source_folder=?, source_uid=?, target_folder=?, target_uid=?, "
                    "    migration_time=? "
                    "WHERE source_mailbox=? AND message_fingerprint=? AND status='success'",
                    (new_src_folder, new_src_uid, new_tgt_folder,
                     new_target_uid, _now(), source_mailbox, fingerprint),
                )
            else:
                cur.execute(
                    "UPDATE migrated_messages "
                    "SET source_folder=?, source_uid=?, migration_time=? "
                    "WHERE source_mailbox=? AND message_fingerprint=? AND status='success'",
                    (new_src_folder, new_src_uid, _now(), source_mailbox, fingerprint),
                )

    # ------------------------------------------------------------------
    # Write migration record
    # ------------------------------------------------------------------

    def record(
        self,
        source_mailbox: str,
        target_mailbox: str,
        source_folder: str,
        target_folder: str,
        source_uid: str,
        message_id: Optional[str],
        status: str = "success",
        fingerprint: str = "",
        target_uid: Optional[str] = None,
        message_date: str = "",
        message_size: int = 0,
    ) -> None:
        now = _now()
        with self._write() as cur:
            cur.execute(
                """
                INSERT INTO migrated_messages
                    (source_mailbox, target_mailbox, source_folder, target_folder,
                     source_uid, target_uid, message_id, message_fingerprint,
                     message_date, message_size, migration_time, status)
                VALUES (?,?,?,?,?,?,?,?,?,?,?,?)
                ON CONFLICT (source_mailbox, source_folder, source_uid)
                    DO UPDATE SET
                        status              = excluded.status,
                        target_uid          = excluded.target_uid,
                        message_fingerprint = CASE
                            WHEN excluded.message_fingerprint != ''
                            THEN excluded.message_fingerprint
                            ELSE message_fingerprint
                        END,
                        migration_time = excluded.migration_time
                """,
                (
                    source_mailbox, target_mailbox, source_folder, target_folder,
                    source_uid, target_uid, message_id, fingerprint,
                    message_date, message_size, now, status,
                ),
            )

    # ------------------------------------------------------------------
    # Job-level tracking
    # ------------------------------------------------------------------

    def init_job(self, source_mailbox: str, target_mailbox: str) -> None:
        with self._write() as cur:
            cur.execute(
                "INSERT INTO job_status (source_mailbox, target_mailbox, status) "
                "VALUES (?,?,'pending') ON CONFLICT (source_mailbox) DO NOTHING",
                (source_mailbox, target_mailbox),
            )

    def mark_running(self, source_mailbox: str, target_mailbox: str, worker_id: str) -> None:
        with self._write() as cur:
            cur.execute(
                "UPDATE job_status SET status='running', worker_id=?, start_time=?, "
                "target_mailbox=? WHERE source_mailbox=?",
                (worker_id, _now(), target_mailbox, source_mailbox),
            )

    def mark_completed(self, source_mailbox: str, stats) -> None:
        with self._write() as cur:
            cur.execute(
                "UPDATE job_status SET status='completed', end_time=?, "
                "total_messages=?, migrated=?, skipped=?, failed_msgs=? "
                "WHERE source_mailbox=?",
                (_now(), stats.total_messages, stats.total_migrated,
                 stats.total_skipped, stats.total_failed, source_mailbox),
            )

    def mark_failed(self, source_mailbox: str, error: str = "") -> None:
        with self._write() as cur:
            cur.execute(
                "UPDATE job_status SET status='failed', end_time=?, error=? "
                "WHERE source_mailbox=?",
                (_now(), error[:500], source_mailbox),
            )

    def is_mailbox_completed(self, source_mailbox: str) -> bool:
        row = self._read_one(
            "SELECT status FROM job_status WHERE source_mailbox=? LIMIT 1",
            (source_mailbox,),
        )
        return row is not None and row["status"] == "completed"
