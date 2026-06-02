"""
Configuration management: CLI args + MigrationConfig dataclass.
"""
import argparse
import os
import sys
from dataclasses import dataclass, field
from typing import Dict, List, Optional


@dataclass
class MailboxPair:
    source_email: str
    source_username: str
    source_password: str
    target_email: str
    target_username: str
    target_password: str
    source_host: Optional[str] = None
    source_port: Optional[int] = None
    target_host: Optional[str] = None
    target_port: Optional[int] = None
    use_ssl: Optional[bool] = None


@dataclass
class MigrationConfig:
    # --- Servers ---
    source_host: str = ""
    source_port: int = 993
    target_host: str = ""
    target_port: int = 993

    # --- TLS ---
    use_ssl: bool = True
    verify_cert: bool = True
    allow_insecure: bool = False

    # --- Single-mailbox mode ---
    source_user: Optional[str] = None
    source_pass: Optional[str] = None
    target_user: Optional[str] = None
    target_pass: Optional[str] = None

    # --- Bulk mode ---
    csv_file: Optional[str] = None

    # --- Migration behaviour ---
    incremental: bool = False
    dry_run: bool = False
    test_connection: bool = False
    resume: bool = False
    folders_only: List[str] = field(default_factory=list)
    folder_mapping: Dict[str, str] = field(default_factory=dict)

    # --- Parallel engine ---
    max_workers: int = 20
    source_connection_limit: int = 50
    target_connection_limit: int = 50
    messages_per_second: float = 0.0   # 0 = unlimited

    # --- Move-aware sync ---
    sync_moves: bool = False                    # master switch — must be True for move-target
    move_policy: str = "copy-only"              # copy-only | global-dedupe | move-target
    allow_target_delete: bool = False           # required for move-target to delete old copy

    # --- Reliability ---
    retry_count: int = 3
    retry_delay: int = 5
    timeout: int = 60
    batch_size: int = 50

    # --- Output ---
    log_dir: str = "logs"
    log_file: str = "logs/migration.log"
    state_db: str = "migration_state.db"
    failed_report: str = "reports/failed_items.csv"
    summary_report: str = "reports/migration_summary.csv"
    moved_report: str = "reports/moved_items.csv"

    # --- Verbosity ---
    verbose: bool = False


def parse_args() -> MigrationConfig:
    parser = argparse.ArgumentParser(
        prog="imap_migrate.py",
        description="IMAP Migration Tool — parallel mailbox migration between IMAP servers",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
EXAMPLES
  Test connections:
    python imap_migrate.py --source-host src.example.com --target-host dst.example.com \\
        --csv accounts.csv --test-connection

  Dry run:
    python imap_migrate.py --source-host src.example.com --target-host dst.example.com \\
        --csv accounts.csv --dry-run

  Full parallel migration (200 mailboxes, 20 workers):
    python imap_migrate.py --source-host src.example.com --target-host dst.example.com \\
        --csv accounts.csv --ssl --max-workers 20 --log-dir logs

  Incremental sync:
    python imap_migrate.py --source-host src.example.com --target-host dst.example.com \\
        --csv accounts.csv --ssl --incremental --max-workers 20

  Resume failed migration:
    python imap_migrate.py --source-host src.example.com --target-host dst.example.com \\
        --csv accounts.csv --ssl --resume --max-workers 20

CONCURRENCY GUIDE
  --max-workers 20    Safe starting point (8 GB RAM VPS)
  --max-workers 50    Moderate load — test first
  --max-workers 100   High load — monitor server resources
  --max-workers 200   Maximum — requires 16+ GB RAM, tuned IMAP servers

ENVIRONMENT VARIABLES
  IMAP_SOURCE_HOST, IMAP_SOURCE_USER, IMAP_SOURCE_PASS
  IMAP_TARGET_HOST, IMAP_TARGET_USER, IMAP_TARGET_PASS
""",
    )

    src = parser.add_argument_group("Source Server")
    src.add_argument("--source-host", default=None)
    src.add_argument("--source-port", type=int, default=993)
    src.add_argument("--source-user", default=None)
    src.add_argument("--source-pass", default=None, help="Prefer env var IMAP_SOURCE_PASS")

    tgt = parser.add_argument_group("Target Server")
    tgt.add_argument("--target-host", default=None)
    tgt.add_argument("--target-port", type=int, default=993)
    tgt.add_argument("--target-user", default=None)
    tgt.add_argument("--target-pass", default=None, help="Prefer env var IMAP_TARGET_PASS")

    conn = parser.add_argument_group("Connection / TLS")
    conn.add_argument("--ssl", action="store_true", default=True)
    conn.add_argument("--no-ssl", action="store_true")
    conn.add_argument("--allow-insecure-cert", action="store_true")
    conn.add_argument("--timeout", type=int, default=60)

    par = parser.add_argument_group("Parallel Engine")
    par.add_argument("--max-workers", type=int, default=20,
                     help="Max concurrent mailbox workers (default: 20, max recommended: 200)")
    par.add_argument("--source-connection-limit", type=int, default=50,
                     help="Max simultaneous IMAP connections to source (default: 50)")
    par.add_argument("--target-connection-limit", type=int, default=50,
                     help="Max simultaneous IMAP connections to target (default: 50)")
    par.add_argument("--messages-per-second", "--rate-limit", type=float, default=0.0,
                     dest="messages_per_second",
                     help="Max messages per second across all workers (0=unlimited)")

    mig = parser.add_argument_group("Migration")
    mig.add_argument("--csv", dest="csv_file", default=None)
    mig.add_argument("--incremental", action="store_true")
    mig.add_argument("--dry-run", action="store_true")
    mig.add_argument("--test-connection", action="store_true")
    mig.add_argument("--resume", action="store_true",
                     help="Skip already-completed mailboxes, retry failed ones")
    mig.add_argument("--folders", nargs="+", dest="folders_only")
    mig.add_argument("--retry-count", type=int, default=3)
    mig.add_argument("--retry-delay", type=int, default=5)
    mig.add_argument("--batch-size", type=int, default=50)

    mov = parser.add_argument_group("Move-Aware Sync (delta sync)")
    mov.add_argument(
        "--sync-moves", action="store_true",
        help="Enable move detection during incremental/delta sync",
    )
    mov.add_argument(
        "--move-policy",
        choices=["copy-only", "global-dedupe", "move-target"],
        default="copy-only",
        help=(
            "copy-only: always copy, never delete (default, safest). "
            "global-dedupe: skip if fingerprint already on target. "
            "move-target: mirror folder move on target (requires --allow-target-delete)."
        ),
    )
    mov.add_argument(
        "--allow-target-delete", action="store_true",
        help="Permit deletion of old target copy when executing move-target policy",
    )

    out = parser.add_argument_group("Output")
    out.add_argument("--log-dir", default="logs", help="Directory for log files (default: logs/)")
    out.add_argument("--state-db", default="migration_state.db")
    out.add_argument("--failed-report", default="reports/failed_items.csv")
    out.add_argument("--moved-report", default="reports/moved_items.csv")
    out.add_argument("--summary", dest="summary_report", default="reports/migration_summary.csv")
    out.add_argument("--verbose", "-v", action="store_true")

    args = parser.parse_args()

    cfg = MigrationConfig()
    cfg.source_host = args.source_host or os.environ.get("IMAP_SOURCE_HOST", "")
    cfg.source_port = args.source_port
    cfg.target_host = args.target_host or os.environ.get("IMAP_TARGET_HOST", "")
    cfg.target_port = args.target_port

    cfg.use_ssl = not args.no_ssl
    cfg.allow_insecure = args.allow_insecure_cert
    cfg.verify_cert = not args.allow_insecure_cert
    cfg.timeout = args.timeout

    cfg.source_user = args.source_user or os.environ.get("IMAP_SOURCE_USER")
    cfg.source_pass = args.source_pass or os.environ.get("IMAP_SOURCE_PASS")
    cfg.target_user = args.target_user or os.environ.get("IMAP_TARGET_USER")
    cfg.target_pass = args.target_pass or os.environ.get("IMAP_TARGET_PASS")

    cfg.csv_file = args.csv_file
    cfg.incremental = args.incremental
    cfg.dry_run = args.dry_run
    cfg.test_connection = args.test_connection
    cfg.resume = args.resume
    cfg.folders_only = args.folders_only or []
    cfg.retry_count = args.retry_count
    cfg.retry_delay = args.retry_delay
    cfg.batch_size = args.batch_size

    cfg.max_workers = args.max_workers
    cfg.source_connection_limit = args.source_connection_limit
    cfg.target_connection_limit = args.target_connection_limit
    cfg.messages_per_second = args.messages_per_second

    cfg.sync_moves = args.sync_moves
    cfg.move_policy = args.move_policy
    cfg.allow_target_delete = args.allow_target_delete

    cfg.log_dir = args.log_dir
    cfg.log_file = os.path.join(args.log_dir, "migration.log")
    cfg.state_db = args.state_db
    cfg.failed_report = args.failed_report
    cfg.moved_report = args.moved_report
    cfg.summary_report = args.summary_report
    cfg.verbose = args.verbose

    return cfg
