"""
Parallel migration engine.

Architecture
------------
CSV accounts
    ↓
Job Queue  (200 MailboxPair items)
    ↓
ThreadPoolExecutor(max_workers=N)
    ↓  each thread picks 1 mailbox
Worker
  ├─ acquire source Semaphore   (source_connection_limit)
  ├─ acquire target Semaphore   (target_connection_limit)
  ├─ connect source IMAP
  ├─ connect target IMAP
  ├─ for each folder:
  │    ├─ ensure folder on target
  │    └─ for each UID batch:
  │         ├─ check StateDB (skip duplicates)
  │         ├─ rate_limiter.acquire()
  │         ├─ fetch message (BODY.PEEK[])
  │         ├─ append to target
  │         └─ record in StateDB
  ├─ update job_status in StateDB
  └─ release semaphores
    ↓
GlobalStats (thread-safe counters) → live progress on console
Reporter    (thread-safe lists)    → summary CSV + failed CSV
"""
import logging
import os
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime
from typing import List, Optional

from src.config import MailboxPair, MigrationConfig
from src.imap_client import IMAPClient, IMAPError
from src.logger import get_worker_logger
from src.move_detector import (
    ACTION_COPY, ACTION_MOVE, ACTION_SKIP,
    MoveDetector, compute_fingerprint,
)
from src.report import FolderStats, MailboxStats, Reporter
from src.state_db import StateDB

logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# Rate limiter (token bucket, thread-safe)
# ---------------------------------------------------------------------------

class RateLimiter:
    """
    Token bucket: rate=0 means unlimited.
    Each call to acquire() blocks until a token is available.
    """

    def __init__(self, rate: float) -> None:
        self.rate = rate
        self._lock = threading.Lock()
        self._tokens = float(rate) if rate > 0 else 0.0
        self._last = time.monotonic()

    def acquire(self) -> None:
        if self.rate <= 0:
            return
        with self._lock:
            now = time.monotonic()
            elapsed = now - self._last
            self._tokens = min(self.rate, self._tokens + elapsed * self.rate)
            self._last = now
            if self._tokens < 1.0:
                wait = (1.0 - self._tokens) / self.rate
                time.sleep(wait)
                self._tokens = 0.0
            else:
                self._tokens -= 1.0


# ---------------------------------------------------------------------------
# Global stats (thread-safe counters for live progress)
# ---------------------------------------------------------------------------

class GlobalStats:
    def __init__(self, total: int) -> None:
        self._lock = threading.Lock()
        self.total = total
        self.pending = total
        self.running = 0
        self.completed = 0
        self.failed = 0
        self.skipped = 0
        self.total_messages = 0
        self.total_migrated = 0
        self.total_failed_msgs = 0

    def mailbox_start(self) -> None:
        with self._lock:
            self.pending = max(0, self.pending - 1)
            self.running += 1

    def mailbox_done(self, status: str, stats: MailboxStats) -> None:
        with self._lock:
            self.running = max(0, self.running - 1)
            if status in ("success", "partial"):
                self.completed += 1
            elif status == "failed":
                self.failed += 1
            else:
                self.skipped += 1
            self.total_messages += stats.total_messages
            self.total_migrated += stats.total_migrated
            self.total_failed_msgs += stats.total_failed

    def snapshot(self) -> dict:
        with self._lock:
            return {
                "total": self.total,
                "pending": self.pending,
                "running": self.running,
                "completed": self.completed,
                "failed": self.failed,
                "skipped": self.skipped,
                "total_migrated": self.total_migrated,
            }


# ---------------------------------------------------------------------------
# Parallel migrator
# ---------------------------------------------------------------------------

class ParallelMigrator:
    def __init__(
        self,
        config: MigrationConfig,
        state_db: StateDB,
        reporter: Reporter,
    ) -> None:
        self.cfg = config
        self.db = state_db
        self.reporter = reporter

        # Semaphores enforce per-server connection caps
        self._src_sem = threading.Semaphore(config.source_connection_limit)
        self._tgt_sem = threading.Semaphore(config.target_connection_limit)

        # Global message-rate limiter (shared across all workers)
        self._rate = RateLimiter(config.messages_per_second)

        # Move-aware sync engine (None when sync_moves=False)
        self._move_detector: Optional[MoveDetector] = (
            MoveDetector(
                db=state_db,
                policy=config.move_policy,
                allow_target_delete=config.allow_target_delete,
                reporter=reporter,
            )
            if config.sync_moves
            else None
        )

        self._gstats: Optional[GlobalStats] = None
        self._stop_progress = threading.Event()

    # ------------------------------------------------------------------
    # Public: test connections
    # ------------------------------------------------------------------

    def test_connections(self, pairs: List[MailboxPair]) -> bool:
        n = min(self.cfg.max_workers, len(pairs), 20)  # cap test concurrency
        all_ok = True
        with ThreadPoolExecutor(max_workers=n, thread_name_prefix="test") as ex:
            futures = {ex.submit(self._test_one, p): p for p in pairs}
            for fut in as_completed(futures):
                if not fut.result():
                    all_ok = False
        return all_ok

    # ------------------------------------------------------------------
    # Public: run migration
    # ------------------------------------------------------------------

    def run(self, pairs: List[MailboxPair]) -> List[MailboxStats]:
        # Initialise job rows in DB for all mailboxes
        for p in pairs:
            self.db.init_job(p.source_email, p.target_email)

        # Resume: drop already-completed mailboxes
        if self.cfg.resume:
            before = len(pairs)
            pairs = [p for p in pairs if not self.db.is_mailbox_completed(p.source_email)]
            if before - len(pairs):
                logger.info(
                    "Resume: skipping %d completed mailbox(es), %d remaining",
                    before - len(pairs), len(pairs),
                )

        if not pairs:
            logger.info("All mailboxes already completed — nothing to do.")
            return []

        # Safety warning for high concurrency
        if self.cfg.max_workers >= 50:
            _print_high_concurrency_warning(self.cfg.max_workers)

        n_workers = min(self.cfg.max_workers, len(pairs))
        self._gstats = GlobalStats(len(pairs))
        self._stop_progress.clear()

        # Start live-progress thread
        pt = threading.Thread(
            target=self._progress_loop, daemon=True, name="progress"
        )
        pt.start()

        logger.info(
            "Parallel migration started: %d mailbox(es), %d worker(s), "
            "src_limit=%d, tgt_limit=%d",
            len(pairs), n_workers,
            self.cfg.source_connection_limit,
            self.cfg.target_connection_limit,
        )

        all_stats: List[MailboxStats] = []

        with ThreadPoolExecutor(
            max_workers=n_workers, thread_name_prefix="mig"
        ) as executor:
            future_map = {
                executor.submit(self._worker, pair, idx): pair
                for idx, pair in enumerate(pairs, 1)
            }
            for fut in as_completed(future_map):
                pair = future_map[fut]
                try:
                    stats = fut.result()
                except Exception as exc:
                    logger.error("Worker crashed for %s: %s", pair.source_email, exc)
                    stats = MailboxStats(
                        source_mailbox=pair.source_email,
                        target_mailbox=pair.target_email,
                        status="failed",
                        error=str(exc),
                        start_time=Reporter.now_iso(),
                        end_time=Reporter.now_iso(),
                    )
                    self.db.mark_failed(pair.source_email, str(exc))

                all_stats.append(stats)
                self.reporter.add_mailbox_stats(stats)
                self._gstats.mailbox_done(stats.status, stats)

        # Stop progress thread and print final line
        self._stop_progress.set()
        pt.join(timeout=2)
        self._print_progress(final=True)
        print()  # newline after inline progress

        return all_stats

    # ------------------------------------------------------------------
    # Per-mailbox worker — runs in thread pool
    # ------------------------------------------------------------------

    def _worker(self, pair: MailboxPair, idx: int) -> MailboxStats:
        worker_id = f"worker-{idx:03d}"
        wlog = get_worker_logger(worker_id, self.cfg.log_dir)

        self._gstats.mailbox_start()
        self.db.mark_running(pair.source_email, pair.target_email, worker_id)

        stats = MailboxStats(
            source_mailbox=pair.source_email,
            target_mailbox=pair.target_email,
            start_time=Reporter.now_iso(),
        )

        src_host = pair.source_host or self.cfg.source_host
        src_port = pair.source_port or self.cfg.source_port
        tgt_host = pair.target_host or self.cfg.target_host
        tgt_port = pair.target_port or self.cfg.target_port
        use_ssl  = pair.use_ssl if pair.use_ssl is not None else self.cfg.use_ssl

        source = IMAPClient(
            host=src_host, port=src_port, use_ssl=use_ssl,
            verify_cert=self.cfg.verify_cert, allow_insecure=self.cfg.allow_insecure,
            timeout=self.cfg.timeout,
        )
        target = IMAPClient(
            host=tgt_host, port=tgt_port, use_ssl=use_ssl,
            verify_cert=self.cfg.verify_cert, allow_insecure=self.cfg.allow_insecure,
            timeout=self.cfg.timeout,
        )

        try:
            with self._src_sem:
                source.connect(pair.source_username, pair.source_password)
            with self._tgt_sem:
                target.connect(pair.target_username, pair.target_password)

            wlog.info("[%s] START %s → %s", worker_id, pair.source_email, pair.target_email)
            self._migrate_folders(source, target, pair, stats, worker_id, wlog)

            stats.status = "success" if stats.total_failed == 0 else "partial"
            self.db.mark_completed(pair.source_email, stats)

        except IMAPError as exc:
            stats.status = "failed"
            stats.error = str(exc)
            self.db.mark_failed(pair.source_email, str(exc))
            wlog.error("[%s] FAILED %s: %s", worker_id, pair.source_email, exc)

        except Exception as exc:
            stats.status = "failed"
            stats.error = str(exc)
            self.db.mark_failed(pair.source_email, str(exc))
            wlog.exception("[%s] CRASH %s", worker_id, pair.source_email)

        finally:
            source.disconnect()
            target.disconnect()
            stats.end_time = Reporter.now_iso()

        wlog.info(
            "[%s] DONE %s status=%s folders=%d msgs=%d migrated=%d skipped=%d failed=%d",
            worker_id, pair.source_email, stats.status,
            stats.total_folders, stats.total_messages,
            stats.total_migrated, stats.total_skipped, stats.total_failed,
        )
        return stats

    # ------------------------------------------------------------------
    # Folder iteration
    # ------------------------------------------------------------------

    def _migrate_folders(
        self,
        source: IMAPClient,
        target: IMAPClient,
        pair: MailboxPair,
        stats: MailboxStats,
        worker_id: str,
        wlog: logging.Logger,
    ) -> None:
        folders = source.list_folders()
        if self.cfg.folders_only:
            lower = {f.lower() for f in self.cfg.folders_only}
            folders = [f for f in folders if f.lower() in lower]

        wlog.info("[%s] %s — %d folder(s) found", worker_id, pair.source_email, len(folders))

        for src_folder in folders:
            tgt_folder = IMAPClient.map_folder_name(src_folder, self.cfg.folder_mapping)
            fstats = self._migrate_folder(
                source, target, src_folder, tgt_folder, pair, worker_id, wlog
            )
            stats.folders.append(fstats)

    def _migrate_folder(
        self,
        source: IMAPClient,
        target: IMAPClient,
        src_folder: str,
        tgt_folder: str,
        pair: MailboxPair,
        worker_id: str,
        wlog: logging.Logger,
    ) -> FolderStats:
        fstats = FolderStats(folder=src_folder)

        count = source.select_folder(src_folder)
        if count is None:
            wlog.error("[%s] Cannot select '%s' — skipping", worker_id, src_folder)
            return fstats

        if not self.cfg.dry_run:
            if not target.ensure_folder(tgt_folder):
                wlog.error("[%s] Cannot create '%s' on target — skipping", worker_id, tgt_folder)
                return fstats

        uids = source.get_all_uids()
        fstats.total = len(uids)

        wlog.info(
            "[%s] %s | '%s' → '%s' | %d message(s)",
            worker_id, pair.source_email, src_folder, tgt_folder, len(uids),
        )

        for batch_start in range(0, len(uids), self.cfg.batch_size):
            batch = uids[batch_start: batch_start + self.cfg.batch_size]
            self._process_batch(
                source, target, batch,
                src_folder, tgt_folder, pair, fstats, worker_id, wlog,
            )

        wlog.info(
            "[%s] %s | '%s' complete: migrated=%d skipped=%d moved=%d failed=%d",
            worker_id, pair.source_email, src_folder,
            fstats.migrated, fstats.skipped, fstats.moved, fstats.failed,
        )
        return fstats

    # ------------------------------------------------------------------
    # Batch processing — move-aware
    # ------------------------------------------------------------------

    def _process_batch(
        self,
        source: IMAPClient,
        target: IMAPClient,
        uids: List[str],
        src_folder: str,
        tgt_folder: str,
        pair: MailboxPair,
        fstats: FolderStats,
        worker_id: str,
        wlog: logging.Logger,
    ) -> None:
        use_move_detect = self._move_detector is not None

        for uid in uids:
            # --- 1. UID-level duplicate check (same folder, already done) ---
            if self.db.is_uid_migrated(pair.source_email, src_folder, uid):
                fstats.skipped += 1
                continue

            # --- 2. Fetch envelope for fingerprint (when move-detect enabled)
            #        or just Message-ID header (cheap path) ---
            if use_move_detect:
                env = source.fetch_envelope_info(uid)
                message_id = env.get("message_id", "")
                fingerprint = compute_fingerprint(
                    message_id=message_id,
                    from_=env.get("from_", ""),
                    date_=env.get("date_", ""),
                    subject=env.get("subject_", ""),
                    size=env.get("size", 0),
                )
                message_date = env.get("date_", "")
                message_size = env.get("size", 0)
            else:
                message_id   = source.fetch_message_id_header(uid) or ""
                fingerprint  = ""
                message_date = ""
                message_size = 0

            # --- 3. Message-ID quick dup check (no move detector) ---
            if not use_move_detect:
                if message_id and self.db.is_message_id_migrated(
                    pair.source_email, pair.target_email, message_id
                ):
                    fstats.skipped += 1
                    self.db.record(
                        pair.source_email, pair.target_email,
                        src_folder, tgt_folder, uid, message_id,
                        status="skipped",
                    )
                    continue

            # --- 4. Move detection ---
            action = ACTION_COPY
            if use_move_detect:
                action = self._move_detector.check(
                    source_mailbox=pair.source_email,
                    target_mailbox=pair.target_email,
                    src_folder=src_folder,
                    uid=uid,
                    fingerprint=fingerprint,
                )
                if action == ACTION_SKIP:
                    wlog.debug(
                        "[%s] global-dedupe skip: fp=%s uid=%s", worker_id, fingerprint[:12], uid
                    )
                    fstats.skipped += 1
                    self.db.record(
                        pair.source_email, pair.target_email,
                        src_folder, tgt_folder, uid, message_id or None,
                        status="skipped", fingerprint=fingerprint,
                        message_date=message_date, message_size=message_size,
                    )
                    continue

                if action == ACTION_MOVE:
                    wlog.info(
                        "[%s] MOVE detected: fp=%s uid=%s '%s'→'%s'",
                        worker_id, fingerprint[:12], uid, src_folder, tgt_folder,
                    )
                    moved = self._move_detector.execute_move(
                        target=target,
                        fingerprint=fingerprint,
                        source_mailbox=pair.source_email,
                        target_mailbox=pair.target_email,
                        new_src_folder=src_folder,
                        new_tgt_folder=tgt_folder,
                        new_src_uid=uid,
                        message_id=message_id or "",
                        timestamp=Reporter.now_iso(),
                        worker_id=worker_id,
                    )
                    if moved:
                        fstats.moved += 1
                        # Update source-side uid record
                        self.db.record(
                            pair.source_email, pair.target_email,
                            src_folder, tgt_folder, uid, message_id or None,
                            status="success", fingerprint=fingerprint,
                            message_date=message_date, message_size=message_size,
                        )
                        continue
                    # Move failed — fall through to copy
                    wlog.warning(
                        "[%s] MOVE failed for fp=%s — falling back to copy", worker_id, fingerprint[:12]
                    )

            # --- 5. Rate limit then copy ---
            self._rate.acquire()

            ok = self._migrate_one(
                source, target, uid, src_folder, tgt_folder,
                pair, message_id or None, fingerprint,
                message_date, message_size,
                worker_id, wlog,
            )
            if ok:
                fstats.migrated += 1
            else:
                fstats.failed += 1

    # ------------------------------------------------------------------
    # Single message migration with exponential backoff retry
    # ------------------------------------------------------------------

    def _migrate_one(
        self,
        source: IMAPClient,
        target: IMAPClient,
        uid: str,
        src_folder: str,
        tgt_folder: str,
        pair: MailboxPair,
        message_id: Optional[str],
        fingerprint: str,
        message_date: str,
        message_size: int,
        worker_id: str,
        wlog: logging.Logger,
    ) -> bool:
        last_error = ""

        for attempt in range(1, self.cfg.retry_count + 1):
            try:
                result = source.fetch_message(uid)
                if result is None:
                    last_error = "fetch returned None"
                    time.sleep(_backoff(attempt, self.cfg.retry_delay))
                    continue

                raw_message, flags, internal_date = result

                if self.cfg.dry_run:
                    wlog.debug("[%s] [DRY-RUN] UID %s → %s", worker_id, uid, tgt_folder)
                    self.db.record(
                        pair.source_email, pair.target_email,
                        src_folder, tgt_folder, uid, message_id,
                        status="success", fingerprint=fingerprint,
                        message_date=message_date, message_size=message_size,
                    )
                    return True

                ok, target_uid = target.append_message(
                    tgt_folder, raw_message, flags, internal_date
                )
                if ok:
                    self.db.record(
                        pair.source_email, pair.target_email,
                        src_folder, tgt_folder, uid, message_id,
                        status="success", fingerprint=fingerprint,
                        target_uid=target_uid,
                        message_date=message_date, message_size=message_size,
                    )
                    wlog.debug("[%s] OK UID %s → %s (tgt_uid=%s)", worker_id, uid, tgt_folder, target_uid)
                    return True

                last_error = "APPEND non-OK"
                time.sleep(_backoff(attempt, self.cfg.retry_delay))

            except Exception as exc:
                last_error = str(exc)
                wlog.warning(
                    "[%s] UID %s attempt %d/%d: %s",
                    worker_id, uid, attempt, self.cfg.retry_count, exc,
                )
                time.sleep(_backoff(attempt, self.cfg.retry_delay))

        # All retries exhausted
        wlog.error(
            "[%s] FAIL UID %s in '%s' after %d attempt(s): %s",
            worker_id, uid, src_folder, self.cfg.retry_count, last_error,
        )
        self.db.record(
            pair.source_email, pair.target_email,
            src_folder, tgt_folder, uid, message_id,
            status="failed", fingerprint=fingerprint,
            message_date=message_date, message_size=message_size,
        )
        self.reporter.add_failed(
            source_mailbox=pair.source_email,
            folder=src_folder,
            uid=uid,
            message_id=message_id or "",
            error=last_error,
        )
        return False

    # ------------------------------------------------------------------
    # Test-connection helper
    # ------------------------------------------------------------------

    def _test_one(self, pair: MailboxPair) -> bool:
        src_host = pair.source_host or self.cfg.source_host
        src_port = pair.source_port or self.cfg.source_port
        tgt_host = pair.target_host or self.cfg.target_host
        tgt_port = pair.target_port or self.cfg.target_port
        use_ssl  = pair.use_ssl if pair.use_ssl is not None else self.cfg.use_ssl

        src = IMAPClient(host=src_host, port=src_port, use_ssl=use_ssl,
                         verify_cert=self.cfg.verify_cert, allow_insecure=self.cfg.allow_insecure,
                         timeout=self.cfg.timeout)
        tgt = IMAPClient(host=tgt_host, port=tgt_port, use_ssl=use_ssl,
                         verify_cert=self.cfg.verify_cert, allow_insecure=self.cfg.allow_insecure,
                         timeout=self.cfg.timeout)

        src_ok = tgt_ok = False
        try:
            src.connect(pair.source_username, pair.source_password)
            logger.info("[TEST] Source OK: %s @ %s:%s", pair.source_email, src_host, src_port)
            src_ok = True
        except IMAPError as exc:
            logger.error("[TEST] Source FAIL: %s — %s", pair.source_email, exc)
        finally:
            src.disconnect()

        try:
            tgt.connect(pair.target_username, pair.target_password)
            logger.info("[TEST] Target OK: %s @ %s:%s", pair.target_email, tgt_host, tgt_port)
            tgt_ok = True
        except IMAPError as exc:
            logger.error("[TEST] Target FAIL: %s — %s", pair.target_email, exc)
        finally:
            tgt.disconnect()

        return src_ok and tgt_ok

    # ------------------------------------------------------------------
    # Live progress
    # ------------------------------------------------------------------

    def _progress_loop(self) -> None:
        while not self._stop_progress.wait(timeout=5.0):
            self._print_progress()

    def _print_progress(self, final: bool = False) -> None:
        if self._gstats is None:
            return
        s = self._gstats.snapshot()
        ts = datetime.now().strftime("%H:%M:%S")
        line = (
            f"[{ts}] "
            f"Total:{s['total']:>4} | "
            f"Running:{s['running']:>3} | "
            f"Completed:{s['completed']:>4} | "
            f"Failed:{s['failed']:>3} | "
            f"Pending:{s['pending']:>4} | "
            f"Msgs migrated:{s['total_migrated']:>9,}"
        )
        if final:
            print(f"\n{line}")
        else:
            print(f"\r{line}", end="", flush=True)


# ---------------------------------------------------------------------------
# Module-level helpers
# ---------------------------------------------------------------------------

def _backoff(attempt: int, base: int = 5) -> float:
    """Exponential backoff capped at 60 s: base, base×2, base×4, ..."""
    return min(float(base) * (2 ** (attempt - 1)), 60.0)


def _print_high_concurrency_warning(workers: int) -> None:
    w = str(workers)
    lines = [
        f"  WARNING: HIGH CONCURRENCY — {w} concurrent workers",
        "  This may overload:",
        "    • Source MDaemon server  (IMAP connections / CPU)",
        "    • Target iRedMail server (Dovecot process limit)",
        "    • VPS RAM / CPU / network bandwidth",
        "    • Linux file descriptor limit  (ulimit -n)",
        "  Recommended ramp-up:  20 → 50 → 100 → 200",
        "  Reduce --max-workers if errors spike or servers lag.",
    ]
    width = max(len(l) for l in lines) + 2
    border = "═" * width
    print(f"\n╔{border}╗")
    for l in lines:
        print(f"║ {l:<{width - 1}}║")
    print(f"╚{border}╝\n")
