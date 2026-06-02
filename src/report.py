"""
Thread-safe report generation.

 - migration_summary.csv  — one row per mailbox
 - failed_items.csv       — one row per failed message
 - moved_items.csv        — one row per detected / acted-on move
"""
import csv
import logging
import os
import threading
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import List, Optional

logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# Stats data classes
# ---------------------------------------------------------------------------

@dataclass
class FolderStats:
    folder: str
    total: int = 0
    migrated: int = 0
    skipped: int = 0
    failed: int = 0
    moved: int = 0


@dataclass
class MailboxStats:
    source_mailbox: str
    target_mailbox: str
    start_time: Optional[str] = None
    end_time: Optional[str] = None
    status: str = "pending"
    folders: List[FolderStats] = field(default_factory=list)
    error: str = ""

    @property
    def total_messages(self) -> int:
        return sum(f.total for f in self.folders)

    @property
    def total_migrated(self) -> int:
        return sum(f.migrated for f in self.folders)

    @property
    def total_skipped(self) -> int:
        return sum(f.skipped for f in self.folders)

    @property
    def total_failed(self) -> int:
        return sum(f.failed for f in self.folders)

    @property
    def total_moved(self) -> int:
        return sum(f.moved for f in self.folders)

    @property
    def total_folders(self) -> int:
        return len(self.folders)


@dataclass
class FailedItem:
    source_mailbox: str
    folder: str
    uid: str
    message_id: str
    error: str


# ---------------------------------------------------------------------------
# Reporter
# ---------------------------------------------------------------------------

class Reporter:
    def __init__(
        self,
        summary_path: str,
        failed_path: str,
        moved_path: str = "reports/moved_items.csv",
        folders_path: str = "reports/folders_report.csv",
    ) -> None:
        self.summary_path = summary_path
        self.failed_path  = failed_path
        self.moved_path   = moved_path
        self.folders_path = folders_path
        self._lock   = threading.Lock()
        self._failed: list = []
        self._moved:  list = []
        self._stats:  List[MailboxStats] = []

    def add_failed(
        self,
        source_mailbox: str,
        folder: str,
        uid: str,
        message_id: str,
        error: str,
    ) -> None:
        with self._lock:
            self._failed.append(
                FailedItem(source_mailbox, folder, uid, message_id, error)
            )

    def add_moved(self, item) -> None:
        """item: src.move_detector.MovedItem"""
        with self._lock:
            self._moved.append(item)

    def add_mailbox_stats(self, stats: MailboxStats) -> None:
        with self._lock:
            self._stats.append(stats)

    # ------------------------------------------------------------------
    # Writers
    # ------------------------------------------------------------------

    def write_summary(self, stats_list: Optional[List[MailboxStats]] = None) -> None:
        rows = stats_list if stats_list is not None else self._stats
        _ensure_dir(self.summary_path)
        fields = [
            "source_mailbox", "target_mailbox", "total_folders", "total_messages",
            "migrated", "skipped", "moved", "failed", "status",
            "start_time", "end_time", "error",
        ]
        try:
            with open(self.summary_path, "w", newline="", encoding="utf-8") as fh:
                w = csv.DictWriter(fh, fieldnames=fields)
                w.writeheader()
                for s in rows:
                    w.writerow({
                        "source_mailbox": s.source_mailbox,
                        "target_mailbox": s.target_mailbox,
                        "total_folders":  s.total_folders,
                        "total_messages": s.total_messages,
                        "migrated":       s.total_migrated,
                        "skipped":        s.total_skipped,
                        "moved":          s.total_moved,
                        "failed":         s.total_failed,
                        "status":         s.status,
                        "start_time":     s.start_time or "",
                        "end_time":       s.end_time or "",
                        "error":          s.error,
                    })
            logger.info("Summary: %s", self.summary_path)
        except OSError as exc:
            logger.error("Cannot write summary: %s", exc)

    def write_failed(self) -> None:
        if not self._failed:
            return
        _ensure_dir(self.failed_path)
        fields = ["source_mailbox", "folder", "uid", "message_id", "error"]
        try:
            with open(self.failed_path, "w", newline="", encoding="utf-8") as fh:
                w = csv.DictWriter(fh, fieldnames=fields)
                w.writeheader()
                for item in self._failed:
                    w.writerow({
                        "source_mailbox": item.source_mailbox,
                        "folder":         item.folder,
                        "uid":            item.uid,
                        "message_id":     item.message_id,
                        "error":          item.error,
                    })
            logger.info("Failed items: %s (%d)", self.failed_path, len(self._failed))
        except OSError as exc:
            logger.error("Cannot write failed report: %s", exc)

    def write_moved(self) -> None:
        if not self._moved:
            return
        _ensure_dir(self.moved_path)
        fields = [
            "source_mailbox", "from_folder", "to_folder",
            "source_uid", "target_uid", "message_id", "fingerprint",
            "action", "status", "timestamp", "error",
        ]
        try:
            with open(self.moved_path, "w", newline="", encoding="utf-8") as fh:
                w = csv.DictWriter(fh, fieldnames=fields)
                w.writeheader()
                for item in self._moved:
                    w.writerow({
                        "source_mailbox": item.source_mailbox,
                        "from_folder":    item.from_folder,
                        "to_folder":      item.to_folder,
                        "source_uid":     item.source_uid,
                        "target_uid":     item.target_uid or "",
                        "message_id":     item.message_id,
                        "fingerprint":    item.fingerprint,
                        "action":         item.action,
                        "status":         item.status,
                        "timestamp":      item.timestamp,
                        "error":          item.error,
                    })
            logger.info("Moved items: %s (%d)", self.moved_path, len(self._moved))
        except OSError as exc:
            logger.error("Cannot write moved report: %s", exc)

    def write_folders(self, stats_list: Optional[List[MailboxStats]] = None) -> None:
        """One row per folder per mailbox — the per-folder breakdown."""
        rows = stats_list if stats_list is not None else self._stats
        _ensure_dir(self.folders_path)
        fields = [
            "source_mailbox", "target_mailbox", "folder",
            "total", "migrated", "skipped", "moved", "failed",
        ]
        try:
            with open(self.folders_path, "w", newline="", encoding="utf-8") as fh:
                w = csv.DictWriter(fh, fieldnames=fields)
                w.writeheader()
                for s in rows:
                    for f in s.folders:
                        w.writerow({
                            "source_mailbox": s.source_mailbox,
                            "target_mailbox": s.target_mailbox,
                            "folder":         f.folder,
                            "total":          f.total,
                            "migrated":       f.migrated,
                            "skipped":        f.skipped,
                            "moved":          f.moved,
                            "failed":         f.failed,
                        })
            logger.info("Folders report: %s", self.folders_path)
        except OSError as exc:
            logger.error("Cannot write folders report: %s", exc)

    @staticmethod
    def now_iso() -> str:
        return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def export_items(db_path: str, out_path: str) -> int:
    """Dump every per-message record from the state DB to a detailed CSV.

    Reads `migrated_messages` directly so we never hold the full message list
    in memory. Returns the number of rows written (0 if the DB is missing).
    """
    if not db_path or not os.path.exists(db_path):
        return 0
    import sqlite3

    _ensure_dir(out_path)
    fields = [
        "source_mailbox", "target_mailbox", "source_folder", "target_folder",
        "source_uid", "target_uid", "message_id", "message_date",
        "message_size", "status", "migration_time",
    ]
    n = 0
    conn = sqlite3.connect(db_path, timeout=10)
    try:
        cur = conn.execute(
            "SELECT source_mailbox, target_mailbox, source_folder, target_folder, "
            "source_uid, target_uid, message_id, message_date, message_size, "
            "status, migration_time FROM migrated_messages ORDER BY id"
        )
        with open(out_path, "w", newline="", encoding="utf-8") as fh:
            w = csv.writer(fh)
            w.writerow(fields)
            for row in cur:
                w.writerow(["" if v is None else v for v in row])
                n += 1
        logger.info("Items report: %s (%d row(s))", out_path, n)
    except (sqlite3.Error, OSError) as exc:
        logger.error("Cannot write items report: %s", exc)
    finally:
        conn.close()
    return n


def _ensure_dir(path: str) -> None:
    d = os.path.dirname(path)
    if d:
        os.makedirs(d, exist_ok=True)
