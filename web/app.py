"""
Flask application factory for the IMAP Migration web UI.

Configuration comes from environment variables so no secrets live in source:

  IMAP_WEB_SECRET_KEY   (required) master secret — Flask session signing +
                        Fernet key derivation for stored IMAP passwords.
  IMAP_WEB_DB           web users/accounts DB path   (default: web_app.db)
  IMAP_WEB_BEHIND_PROXY "1"/"0" — running behind a TLS-terminating reverse
                        proxy (default: 1). Enables ProxyFix + Secure cookies.

  Migration engine defaults (can also be edited per-run in the UI later):
  IMAP_SOURCE_HOST / IMAP_SOURCE_PORT
  IMAP_TARGET_HOST / IMAP_TARGET_PORT
  IMAP_WEB_MAX_WORKERS, IMAP_WEB_ALLOW_INSECURE_CERT
  IMAP_WEB_LOG_DIR, IMAP_WEB_STATE_DB, IMAP_WEB_REPORT_DIR
"""
import os

from flask import Flask, g

from src.config import MigrationConfig

from . import auth, websec
from .crypto import SecretBox
from .jobs import JobManager
from .store import Store
from .views import auth_bp, bp as main_bp


def _env_bool(name: str, default: bool) -> bool:
    val = os.environ.get(name)
    if val is None:
        return default
    return val.strip().lower() in ("1", "true", "yes", "on")


def _build_base_config() -> MigrationConfig:
    """MigrationConfig seeded from the environment. Per-run flags (dry_run /
    test_connection / incremental / resume) are set by JobManager."""
    cfg = MigrationConfig()
    cfg.source_host = os.environ.get("IMAP_SOURCE_HOST", "")
    cfg.source_port = int(os.environ.get("IMAP_SOURCE_PORT", "993"))
    cfg.target_host = os.environ.get("IMAP_TARGET_HOST", "")
    cfg.target_port = int(os.environ.get("IMAP_TARGET_PORT", "993"))

    cfg.allow_insecure = _env_bool("IMAP_WEB_ALLOW_INSECURE_CERT", False)
    cfg.verify_cert = not cfg.allow_insecure
    cfg.use_ssl = _env_bool("IMAP_WEB_USE_SSL", True)

    cfg.max_workers = int(os.environ.get("IMAP_WEB_MAX_WORKERS", "20"))

    log_dir = os.environ.get("IMAP_WEB_LOG_DIR", "logs")
    report_dir = os.environ.get("IMAP_WEB_REPORT_DIR", "reports")
    cfg.log_dir = log_dir
    cfg.log_file = os.path.join(log_dir, "migration.log")
    cfg.state_db = os.environ.get("IMAP_WEB_STATE_DB", "migration_state.db")
    cfg.summary_report = os.path.join(report_dir, "migration_summary.csv")
    cfg.failed_report = os.path.join(report_dir, "failed_items.csv")
    cfg.moved_report = os.path.join(report_dir, "moved_items.csv")
    return cfg


def create_app() -> Flask:
    secret = os.environ.get("IMAP_WEB_SECRET_KEY", "")
    if not secret:
        raise RuntimeError(
            "IMAP_WEB_SECRET_KEY is not set. Generate one with:\n"
            "  python -c \"import secrets;print(secrets.token_urlsafe(64))\"\n"
            "then export it before starting the web app."
        )

    app = Flask(__name__)
    app.config["SECRET_KEY"] = secret
    app.config["BEHIND_TLS_PROXY"] = _env_bool("IMAP_WEB_BEHIND_PROXY", True)
    app.config["MAX_CONTENT_LENGTH"] = 2 * 1024 * 1024  # 2 MB upload cap
    app.config["BASE_MIGRATION_CONFIG"] = _build_base_config()

    # Shared services attached to the app object.
    box = SecretBox(secret)
    app.store = Store(os.environ.get("IMAP_WEB_DB", "web_app.db"), box)
    app.jobs = JobManager(_build_base_config)

    websec.init_security(app)

    app.before_request(auth.load_current_user)

    @app.context_processor
    def _inject_user():
        return {"current_user": g.get("user")}

    app.register_blueprint(auth_bp)
    app.register_blueprint(main_bp)

    return app
