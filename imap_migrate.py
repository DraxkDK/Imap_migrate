#!/usr/bin/env python3
"""
IMAP Migration Tool — parallel worker-pool engine.

Architecture:
  CSV → Job Queue → ThreadPoolExecutor(--max-workers) → Workers → StateDB + Logs + Reports

Usage:
  python imap_migrate.py --help
"""
import csv
import getpass
import logging
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from src.config import MailboxPair, MigrationConfig, parse_args
from src.logger import setup_logging
from src.migrator import ParallelMigrator
from src.report import Reporter
from src.security import check_file_permissions
from src.state_db import StateDB

logger = logging.getLogger(__name__)

_REQUIRED_COLS = {
    "source_email", "source_username", "source_password",
    "target_email", "target_username", "target_password",
}


# ---------------------------------------------------------------------------
# CSV loading
# ---------------------------------------------------------------------------

def load_csv(path: str, cfg: MigrationConfig) -> list:
    check_file_permissions(path)
    pairs = []
    try:
        with open(path, newline="", encoding="utf-8") as fh:
            reader = csv.DictReader(fh)
            if not reader.fieldnames:
                logger.error("CSV file is empty: %s", path)
                return pairs

            present = {c.strip().lower() for c in reader.fieldnames}
            missing = _REQUIRED_COLS - present
            if missing:
                logger.error("CSV missing columns: %s", ", ".join(sorted(missing)))
                sys.exit(1)

            for lineno, row in enumerate(reader, start=2):
                row = {k.strip(): (v.strip() if v else "") for k, v in row.items()}
                try:
                    pair = MailboxPair(
                        source_email=row["source_email"],
                        source_username=row["source_username"],
                        source_password=row["source_password"],
                        target_email=row["target_email"],
                        target_username=row["target_username"],
                        target_password=row["target_password"],
                    )
                    if row.get("source_host"):
                        pair.source_host = row["source_host"]
                    if row.get("source_port"):
                        pair.source_port = int(row["source_port"])
                    if row.get("target_host"):
                        pair.target_host = row["target_host"]
                    if row.get("target_port"):
                        pair.target_port = int(row["target_port"])
                    if row.get("use_ssl"):
                        pair.use_ssl = row["use_ssl"].lower() in ("1", "true", "yes")
                    pairs.append(pair)
                except (KeyError, ValueError) as exc:
                    logger.warning("Skipping row %d: %s", lineno, exc)

    except FileNotFoundError:
        logger.error("CSV not found: %s", path)
        sys.exit(1)

    logger.info("Loaded %d mailbox pair(s) from %s", len(pairs), path)
    return pairs


# ---------------------------------------------------------------------------
# Single-mailbox helper
# ---------------------------------------------------------------------------

def build_single_pair(cfg: MigrationConfig) -> MailboxPair:
    src_user = cfg.source_user or ""
    src_pass = cfg.source_pass
    tgt_user = cfg.target_user or ""
    tgt_pass = cfg.target_pass

    if not src_user:
        src_user = input("Source username: ").strip()
    if not src_pass:
        src_pass = getpass.getpass(f"Source password for {src_user}: ")
    if not tgt_user:
        tgt_user = input("Target username: ").strip()
    if not tgt_pass:
        tgt_pass = getpass.getpass(f"Target password for {tgt_user}: ")

    return MailboxPair(
        source_email=src_user, source_username=src_user, source_password=src_pass,
        target_email=tgt_user, target_username=tgt_user, target_password=tgt_pass,
    )


# ---------------------------------------------------------------------------
# Config validation
# ---------------------------------------------------------------------------

def validate(cfg: MigrationConfig, pairs: list) -> None:
    errors = []
    if not cfg.source_host and any(not p.source_host for p in pairs):
        errors.append("--source-host required (or set per-row in CSV)")
    if not cfg.target_host and any(not p.target_host for p in pairs):
        errors.append("--target-host required (or set per-row in CSV)")
    for e in errors:
        logger.error("Config error: %s", e)
    if errors:
        sys.exit(1)


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main() -> None:
    cfg = parse_args()

    # Create output directories before setting up logging
    os.makedirs(cfg.log_dir, exist_ok=True)
    os.makedirs(os.path.dirname(cfg.summary_report) if os.path.dirname(cfg.summary_report) else ".", exist_ok=True)

    setup_logging(cfg.log_file, verbose=cfg.verbose)

    logger.info("=" * 70)
    logger.info("IMAP Migration Tool  |  parallel engine")
    logger.info("Source : %s:%s", cfg.source_host or "(per CSV)", cfg.source_port)
    logger.info("Target : %s:%s", cfg.target_host or "(per CSV)", cfg.target_port)
    logger.info(
        "Workers: max=%d  src_limit=%d  tgt_limit=%d  rate=%.1f msg/s",
        cfg.max_workers, cfg.source_connection_limit,
        cfg.target_connection_limit, cfg.messages_per_second,
    )

    if cfg.allow_insecure:
        logger.warning("SECURITY: TLS cert verification DISABLED (--allow-insecure-cert)")
    if cfg.dry_run:
        logger.info("DRY-RUN mode — no data written to target")
    if cfg.resume:
        logger.info("RESUME mode — skipping completed mailboxes")
    if cfg.incremental:
        logger.info("INCREMENTAL mode — skipping previously migrated messages")
    if cfg.sync_moves:
        logger.info(
            "MOVE-SYNC mode — policy=%s  allow-delete=%s",
            cfg.move_policy, cfg.allow_target_delete,
        )
        if cfg.move_policy == "move-target" and not cfg.allow_target_delete:
            logger.warning(
                "move-target policy without --allow-target-delete: "
                "old target copies will NOT be removed (partial move only)"
            )

    # Build mailbox list
    pairs = []
    if cfg.csv_file:
        pairs = load_csv(cfg.csv_file, cfg)
    elif cfg.source_user or cfg.source_host:
        pairs = [build_single_pair(cfg)]
    else:
        logger.error("Provide --csv or --source-user for single-mailbox mode.")
        sys.exit(1)

    if not pairs:
        logger.error("No mailboxes to process.")
        sys.exit(1)

    validate(cfg, pairs)

    reporter = Reporter(cfg.summary_report, cfg.failed_report, cfg.moved_report,
                        cfg.folders_report)
    state_db = StateDB(cfg.state_db)

    if not cfg.dry_run and not cfg.test_connection:
        state_db.open()

    migrator = ParallelMigrator(cfg, state_db, reporter)

    # ---------------------------------------------------------------
    # TEST-CONNECTION
    # ---------------------------------------------------------------
    if cfg.test_connection:
        logger.info("--- Test-connection mode ---")
        ok = migrator.test_connections(pairs)
        logger.info("All connections: %s", "OK" if ok else "FAILED")
        state_db.close()
        sys.exit(0 if ok else 1)

    # ---------------------------------------------------------------
    # MIGRATION
    # ---------------------------------------------------------------
    all_stats = migrator.run(pairs)

    reporter.write_summary(all_stats)
    reporter.write_failed()
    reporter.write_moved()
    reporter.write_folders(all_stats)
    state_db.close()

    # Per-message detail is read back from the state DB (real runs only).
    if not cfg.dry_run:
        from src.report import export_items
        export_items(cfg.state_db, cfg.items_report)

    # Final summary
    total   = len(all_stats)
    success = sum(1 for s in all_stats if s.status in ("success", "partial"))
    failed  = sum(1 for s in all_stats if s.status == "failed")
    t_msgs  = sum(s.total_messages for s in all_stats)
    t_mig   = sum(s.total_migrated for s in all_stats)
    t_skip  = sum(s.total_skipped  for s in all_stats)
    t_mov   = sum(s.total_moved    for s in all_stats)
    t_fail  = sum(s.total_failed   for s in all_stats)

    logger.info("=" * 70)
    logger.info("Migration complete")
    logger.info("Mailboxes : total=%d  success=%d  failed=%d", total, success, failed)
    logger.info(
        "Messages  : total=%d  migrated=%d  skipped=%d  moved=%d  failed=%d",
        t_msgs, t_mig, t_skip, t_mov, t_fail,
    )
    logger.info("Logs      : %s/", cfg.log_dir)
    logger.info("Summary   : %s", cfg.summary_report)
    if t_mov and cfg.sync_moves:
        logger.info("Moved     : %s  (%d action(s))", cfg.moved_report, t_mov)
    if t_fail:
        logger.warning("Failed    : %s  (%d message(s))", cfg.failed_report, t_fail)

    sys.exit(0 if t_fail == 0 and failed == 0 else 1)


if __name__ == "__main__":
    main()
