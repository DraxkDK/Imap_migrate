"""
Logging setup.

 - Main log  : logs/migration.log   (all workers aggregated)
 - Worker log: logs/worker-NNN.log  (one file per mailbox)
 - Console   : INFO (or DEBUG with --verbose)

All handlers share SensitiveDataFilter — passwords never appear in any log.
"""
import logging
import os
import sys
from src.security import mask_secrets


class SensitiveDataFilter(logging.Filter):
    """Mask passwords and secrets in every log record."""

    def filter(self, record: logging.LogRecord) -> bool:
        record.msg = mask_secrets(str(record.msg))
        if record.args:
            if isinstance(record.args, dict):
                record.args = {k: mask_secrets(str(v)) for k, v in record.args.items()}
            else:
                record.args = tuple(mask_secrets(str(a)) for a in record.args)
        return True


_MAIN_FMT = logging.Formatter(
    "%(asctime)s [%(levelname)-8s] %(name)s: %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)

_WORKER_FMT = logging.Formatter(
    "%(asctime)s [%(levelname)-8s] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)

_sensitive = SensitiveDataFilter()


def setup_logging(log_file: str, verbose: bool = False) -> logging.Logger:
    """
    Set up root logger with console + main log file handlers.
    Call once at startup before spawning workers.
    """
    os.makedirs(os.path.dirname(log_file) if os.path.dirname(log_file) else ".", exist_ok=True)

    root = logging.getLogger()
    root.setLevel(logging.DEBUG)

    # Console
    ch = logging.StreamHandler(sys.stdout)
    ch.setLevel(logging.DEBUG if verbose else logging.INFO)
    ch.setFormatter(_MAIN_FMT)
    ch.addFilter(_sensitive)
    root.addHandler(ch)

    # Main log file
    try:
        fh = logging.FileHandler(log_file, encoding="utf-8")
        fh.setLevel(logging.DEBUG)
        fh.setFormatter(_MAIN_FMT)
        fh.addFilter(_sensitive)
        root.addHandler(fh)
    except OSError as exc:
        root.warning("Cannot open log file '%s': %s", log_file, exc)

    return logging.getLogger("imap_migrate")


def get_worker_logger(worker_id: str, log_dir: str) -> logging.Logger:
    """
    Return a logger that writes to logs/worker-NNN.log.
    Safe to call from multiple threads — each worker_id gets its own logger.
    """
    logger_name = f"worker.{worker_id}"
    worker_logger = logging.getLogger(logger_name)

    # Avoid adding duplicate handlers if called more than once
    if not worker_logger.handlers:
        os.makedirs(log_dir, exist_ok=True)
        log_path = os.path.join(log_dir, f"{worker_id}.log")
        fh = logging.FileHandler(log_path, encoding="utf-8")
        fh.setLevel(logging.DEBUG)
        fh.setFormatter(_WORKER_FMT)
        fh.addFilter(_sensitive)
        worker_logger.addHandler(fh)
        worker_logger.setLevel(logging.DEBUG)
        worker_logger.propagate = True   # also flows to main migration.log

    return worker_logger
