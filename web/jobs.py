"""
Background migration runner for the web UI.

A migration can take hours, so it must not run inside a request. JobManager
launches the existing src.migrator.ParallelMigrator in a single background
thread and exposes a thread-safe, JSON-serialisable status snapshot that the
dashboard polls.

Only ONE job runs at a time (a migration touches shared servers, the state DB,
and report files). Starting a job while one is active is rejected.
"""
import os
import threading
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import List, Optional

from src.config import MailboxPair, MigrationConfig
from src.migrator import ParallelMigrator
from src.report import Reporter
from src.state_db import StateDB


def _now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


@dataclass
class JobState:
    kind: str = "idle"          # idle | test | dry-run | migrate
    status: str = "idle"        # idle | running | done | failed
    started_by: str = ""
    started_at: str = ""
    ended_at: str = ""
    message: str = ""
    error: str = ""
    # live counters mirrored from migrator GlobalStats
    total: int = 0
    running: int = 0
    completed: int = 0
    failed: int = 0
    pending: int = 0
    total_migrated: int = 0

    def as_dict(self) -> dict:
        return {
            "kind": self.kind, "status": self.status,
            "started_by": self.started_by, "started_at": self.started_at,
            "ended_at": self.ended_at, "message": self.message,
            "error": self.error, "total": self.total, "running": self.running,
            "completed": self.completed, "failed": self.failed,
            "pending": self.pending, "total_migrated": self.total_migrated,
        }


class JobManager:
    def __init__(self, base_cfg_factory) -> None:
        """base_cfg_factory() -> MigrationConfig with output paths / engine
        tuning already filled in (servers/TLS come from app config)."""
        self._make_cfg = base_cfg_factory
        self._lock = threading.Lock()
        self._thread: Optional[threading.Thread] = None
        self._migrator: Optional[ParallelMigrator] = None
        self.state = JobState()

    # ------------------------------------------------------------------

    def is_running(self) -> bool:
        with self._lock:
            return self.state.status == "running"

    def snapshot(self) -> dict:
        with self._lock:
            d = self.state.as_dict()
        # Merge in the migrator's live counters if a run is active.
        mig = self._migrator
        if mig is not None and getattr(mig, "_gstats", None) is not None:
            try:
                s = mig._gstats.snapshot()
                d.update({
                    "total": s["total"], "running": s["running"],
                    "completed": s["completed"], "failed": s["failed"],
                    "pending": s["pending"],
                    "total_migrated": s["total_migrated"],
                })
            except Exception:
                pass
        return d

    # ------------------------------------------------------------------

    def start(self, kind: str, pairs_data: List[dict], *, started_by: str,
              overrides: Optional[dict] = None) -> tuple:
        """kind: 'test' | 'dry-run' | 'migrate'. Returns (ok, message)."""
        if not pairs_data:
            return False, "No IMAP accounts configured to migrate."
        with self._lock:
            if self.state.status == "running":
                return False, "A job is already running. Wait for it to finish."
            self.state = JobState(
                kind=kind, status="running", started_by=started_by,
                started_at=_now(), message=f"{kind} started",
                total=len(pairs_data),
            )
        self._thread = threading.Thread(
            target=self._run, args=(kind, pairs_data, overrides or {}),
            name="migration-job", daemon=True,
        )
        self._thread.start()
        return True, f"{kind} started for {len(pairs_data)} mailbox(es)."

    # ------------------------------------------------------------------

    def _build_pairs(self, pairs_data: List[dict]) -> List[MailboxPair]:
        pairs = []
        for d in pairs_data:
            p = MailboxPair(
                source_email=d["source_email"],
                source_username=d["source_username"],
                source_password=d["source_password"],
                target_email=d["target_email"],
                target_username=d["target_username"],
                target_password=d["target_password"],
            )
            p.source_host = d.get("source_host")
            p.source_port = d.get("source_port")
            p.target_host = d.get("target_host")
            p.target_port = d.get("target_port")
            p.use_ssl = d.get("use_ssl")
            pairs.append(p)
        return pairs

    def _run(self, kind: str, pairs_data: List[dict], overrides: dict) -> None:
        cfg: MigrationConfig = self._make_cfg()
        cfg.test_connection = (kind == "test")
        cfg.dry_run = (kind == "dry-run")
        cfg.incremental = bool(overrides.get("incremental", cfg.incremental))
        cfg.resume = bool(overrides.get("resume", cfg.resume))

        # Global default servers set from the UI take precedence over the env
        # (applied live, no restart). Per-account hosts still override these.
        servers = overrides.get("servers") or {}
        if servers.get("configured"):
            cfg.source_host = servers.get("source_host", "") or ""
            cfg.target_host = servers.get("target_host", "") or ""
            for attr, key in (("source_port", "source_port"),
                              ("target_port", "target_port")):
                raw = servers.get(key)
                if raw:
                    try:
                        setattr(cfg, attr, int(raw))
                    except (TypeError, ValueError):
                        pass

        os.makedirs(cfg.log_dir, exist_ok=True)
        for path in (cfg.summary_report, cfg.failed_report, cfg.moved_report):
            d = os.path.dirname(path)
            if d:
                os.makedirs(d, exist_ok=True)

        pairs = self._build_pairs(pairs_data)
        reporter = Reporter(cfg.summary_report, cfg.failed_report, cfg.moved_report,
                            cfg.folders_report)
        state_db = StateDB(cfg.state_db)
        if not cfg.dry_run and not cfg.test_connection:
            state_db.open()

        migrator = ParallelMigrator(cfg, state_db, reporter)
        self._migrator = migrator

        try:
            if kind == "test":
                ok = migrator.test_connections(pairs)
                self._finish(
                    status="done" if ok else "failed",
                    message=("All connections OK" if ok
                             else "One or more connections FAILED"),
                )
            else:
                all_stats = migrator.run(pairs)
                reporter.write_summary(all_stats)
                reporter.write_failed()
                reporter.write_moved()
                reporter.write_folders(all_stats)
                if not cfg.dry_run:
                    from src.report import export_items
                    export_items(cfg.state_db, cfg.items_report)
                t_fail = sum(s.total_failed for s in all_stats)
                n_fail = sum(1 for s in all_stats if s.status == "failed")
                msg = (
                    f"{kind} complete: {len(all_stats)} mailbox(es), "
                    f"{sum(s.total_migrated for s in all_stats)} migrated, "
                    f"{t_fail} message failure(s)"
                )
                self._finish(
                    status="failed" if (t_fail or n_fail) else "done",
                    message=msg,
                )
        except Exception as exc:  # noqa: BLE001 — surface any engine failure
            self._finish(status="failed", message="Job crashed", error=str(exc))
        finally:
            try:
                state_db.close()
            except Exception:
                pass
            self._migrator = None

    def _finish(self, *, status: str, message: str, error: str = "") -> None:
        with self._lock:
            self.state.status = status
            self.state.message = message
            self.state.error = error
            self.state.ended_at = _now()


def read_jobs_from_state_db(db_path: str, limit: int = 500) -> List[dict]:
    """Read per-mailbox job rows directly for the dashboard table."""
    if not os.path.exists(db_path):
        return []
    import sqlite3
    conn = sqlite3.connect(db_path, timeout=5)
    conn.row_factory = sqlite3.Row
    try:
        rows = conn.execute(
            "SELECT source_mailbox, target_mailbox, status, worker_id, "
            "start_time, end_time, total_messages, migrated, skipped, "
            "failed_msgs, error FROM job_status ORDER BY id DESC LIMIT ?",
            (limit,),
        ).fetchall()
        return [dict(r) for r in rows]
    except sqlite3.Error:
        return []
    finally:
        conn.close()
