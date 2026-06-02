"""
Routes for the IMAP Migration web UI.

Two blueprints:
  auth_bp  — login / logout / change own password (public + self-service)
  bp       — everything else, behind login + role checks
"""
import io
import os

from flask import (
    Blueprint, abort, current_app, flash, g, jsonify, redirect,
    render_template, request, send_file, url_for,
)

from .auth import (
    attempt_login, hash_password, login_required, login_user, logout_user,
    require_role, validate_password_strength, verify_password, ROLES,
)
from .jobs import read_jobs_from_state_db

auth_bp = Blueprint("auth", __name__)
bp = Blueprint("main", __name__)

# Filenames the UI is allowed to serve, by directory. Prevents path traversal:
# we only ever join a *basename* onto a fixed, configured directory.
_ALLOWED_REPORTS = {
    "migration_summary.csv", "failed_items.csv", "moved_items.csv",
}


def _client_ip() -> str:
    return request.remote_addr or ""


def _audit(action: str, detail: str = "") -> None:
    current_app.store.audit(
        action,
        username=(g.user["username"] if g.get("user") else ""),
        detail=detail,
        ip=_client_ip(),
    )


# ===========================================================================
# Auth
# ===========================================================================

@auth_bp.route("/login", methods=["GET", "POST"])
def login():
    if g.get("user") is not None:
        return redirect(url_for("main.dashboard"))

    if request.method == "POST":
        username = (request.form.get("username") or "").strip()
        password = request.form.get("password") or ""
        user, error = attempt_login(current_app.store, username, password)
        if user is None:
            _audit("login_failed", f"username={username}")
            flash(error, "error")
            return render_template("login.html"), 401
        login_user(user)
        _audit("login_success")
        nxt = request.args.get("next", "")
        # Only allow local redirects (no open-redirect to other hosts).
        if nxt.startswith("/") and not nxt.startswith("//"):
            return redirect(nxt)
        return redirect(url_for("main.dashboard"))

    return render_template("login.html")


@auth_bp.route("/logout", methods=["POST"])
@login_required
def logout():
    _audit("logout")
    logout_user()
    flash("You have been signed out.", "info")
    return redirect(url_for("auth.login"))


@auth_bp.route("/account/password", methods=["GET", "POST"])
@login_required
def change_password():
    if request.method == "POST":
        current = request.form.get("current_password") or ""
        new = request.form.get("new_password") or ""
        confirm = request.form.get("confirm_password") or ""
        if not verify_password(g.user["password_hash"], current):
            flash("Current password is incorrect.", "error")
        elif new != confirm:
            flash("New passwords do not match.", "error")
        else:
            err = validate_password_strength(new)
            if err:
                flash(err, "error")
            else:
                current_app.store.set_password(g.user["username"], hash_password(new))
                _audit("password_changed")
                flash("Password updated.", "success")
                return redirect(url_for("main.dashboard"))
    return render_template("change_password.html")


# ===========================================================================
# Dashboard + live status
# ===========================================================================

@bp.route("/")
@login_required
def dashboard():
    cfg = current_app.config["BASE_MIGRATION_CONFIG"]
    jobs = read_jobs_from_state_db(cfg.state_db)
    accounts = current_app.store.list_accounts()
    return render_template(
        "dashboard.html",
        job=current_app.jobs.snapshot(),
        jobs=jobs,
        account_count=len(accounts),
        cfg=cfg,
        servers=current_app.store.get_global_servers(),
    )


@bp.route("/settings/servers", methods=["POST"])
@require_role("operator")
def update_servers():
    """Edit the global default source/target servers from the UI. Applies to
    the next job immediately — no app restart needed. Blank host = per-account."""
    f = request.form
    current_app.store.set_global_servers(
        source_host=(f.get("source_host") or "").strip(),
        source_port=(f.get("source_port") or "").strip(),
        target_host=(f.get("target_host") or "").strip(),
        target_port=(f.get("target_port") or "").strip(),
    )
    _audit(
        "servers_updated",
        f"src={f.get('source_host', '')}:{f.get('source_port', '')} "
        f"dst={f.get('target_host', '')}:{f.get('target_port', '')}",
    )
    flash("Default servers updated.", "success")
    return redirect(url_for("main.dashboard"))


@bp.route("/status.json")
@login_required
def status_json():
    cfg = current_app.config["BASE_MIGRATION_CONFIG"]
    return jsonify({
        "job": current_app.jobs.snapshot(),
        "mailboxes": read_jobs_from_state_db(cfg.state_db),
    })


# ===========================================================================
# IMAP account management  (operator+)
# ===========================================================================

@bp.route("/accounts", methods=["GET", "POST"])
@require_role("operator")
def accounts():
    store = current_app.store
    if request.method == "POST":
        f = request.form
        try:
            store.add_account(
                source_email=(f.get("source_email") or "").strip(),
                source_username=(f.get("source_username") or "").strip(),
                source_password=f.get("source_password") or "",
                target_email=(f.get("target_email") or "").strip(),
                target_username=(f.get("target_username") or "").strip(),
                target_password=f.get("target_password") or "",
                source_host=(f.get("source_host") or "").strip(),
                source_port=int(f["source_port"]) if f.get("source_port") else None,
                target_host=(f.get("target_host") or "").strip(),
                target_port=int(f["target_port"]) if f.get("target_port") else None,
                use_ssl=None,
            )
            _audit("account_added", f.get("source_email", ""))
            flash("Account saved.", "success")
        except (ValueError, KeyError) as exc:
            flash(f"Invalid account data: {exc}", "error")
        return redirect(url_for("main.accounts"))

    return render_template("accounts.html", accounts=store.list_accounts())


@bp.route("/accounts/<int:account_id>/delete", methods=["POST"])
@require_role("operator")
def delete_account(account_id: int):
    current_app.store.delete_account(account_id)
    _audit("account_deleted", f"id={account_id}")
    flash("Account deleted.", "info")
    return redirect(url_for("main.accounts"))


@bp.route("/accounts/import", methods=["POST"])
@require_role("operator")
def import_accounts():
    """Import the same CSV format the CLI uses. Passwords are encrypted on save."""
    import csv
    file = request.files.get("csv_file")
    if not file or not file.filename:
        flash("No CSV file selected.", "error")
        return redirect(url_for("main.accounts"))

    required = {
        "source_email", "source_username", "source_password",
        "target_email", "target_username", "target_password",
    }
    try:
        text = file.read().decode("utf-8")
    except UnicodeDecodeError:
        flash("CSV must be UTF-8 encoded.", "error")
        return redirect(url_for("main.accounts"))

    reader = csv.DictReader(io.StringIO(text))
    present = {c.strip().lower() for c in (reader.fieldnames or [])}
    if not required.issubset(present):
        flash(f"CSV missing columns: {', '.join(sorted(required - present))}", "error")
        return redirect(url_for("main.accounts"))

    added = 0
    store = current_app.store
    for row in reader:
        row = {k.strip(): (v.strip() if v else "") for k, v in row.items()}
        if not row.get("source_email") or not row.get("target_email"):
            continue
        store.add_account(
            source_email=row["source_email"],
            source_username=row["source_username"],
            source_password=row["source_password"],
            target_email=row["target_email"],
            target_username=row["target_username"],
            target_password=row["target_password"],
            source_host=row.get("source_host", ""),
            source_port=int(row["source_port"]) if row.get("source_port") else None,
            target_host=row.get("target_host", ""),
            target_port=int(row["target_port"]) if row.get("target_port") else None,
            use_ssl=(row["use_ssl"].lower() in ("1", "true", "yes"))
            if row.get("use_ssl") else None,
        )
        added += 1
    _audit("accounts_imported", f"count={added}")
    flash(f"Imported {added} account(s).", "success")
    return redirect(url_for("main.accounts"))


# ===========================================================================
# Run migration / test / dry-run  (operator+)
# ===========================================================================

@bp.route("/run", methods=["POST"])
@require_role("operator")
def run_job():
    kind = request.form.get("kind", "")
    if kind not in ("test", "dry-run", "migrate"):
        abort(400, description="Unknown job kind.")

    pairs = current_app.store.get_account_pairs()
    overrides = {
        "incremental": bool(request.form.get("incremental")),
        "resume": bool(request.form.get("resume")),
        "servers": current_app.store.get_global_servers(),
    }
    ok, msg = current_app.jobs.start(
        kind, pairs, started_by=g.user["username"], overrides=overrides
    )
    _audit("job_started" if ok else "job_rejected", f"{kind}: {msg}")
    flash(msg, "success" if ok else "warning")
    return redirect(url_for("main.dashboard"))


# ===========================================================================
# Reports & logs  (viewer+)
# ===========================================================================

@bp.route("/reports")
@login_required
def reports():
    cfg = current_app.config["BASE_MIGRATION_CONFIG"]
    files = []
    for path in (cfg.summary_report, cfg.failed_report, cfg.moved_report):
        if os.path.exists(path):
            files.append({
                "name": os.path.basename(path),
                "size": os.path.getsize(path),
            })
    return render_template("reports.html", files=files)


@bp.route("/reports/download/<name>")
@login_required
def download_report(name: str):
    if name not in _ALLOWED_REPORTS:
        abort(404)
    cfg = current_app.config["BASE_MIGRATION_CONFIG"]
    report_dir = os.path.dirname(cfg.summary_report) or "."
    path = os.path.join(report_dir, name)
    if not os.path.exists(path):
        abort(404)
    _audit("report_downloaded", name)
    return send_file(path, as_attachment=True, download_name=name)


@bp.route("/logs")
@login_required
def logs():
    cfg = current_app.config["BASE_MIGRATION_CONFIG"]
    log_path = cfg.log_file
    tail = ""
    if os.path.exists(log_path):
        # Show only the last ~64 KB so a huge log never blows up the response.
        with open(log_path, "rb") as fh:
            fh.seek(0, os.SEEK_END)
            size = fh.tell()
            fh.seek(max(0, size - 65536))
            tail = fh.read().decode("utf-8", errors="replace")
    return render_template("logs.html", tail=tail, log_path=log_path)


# ===========================================================================
# User management  (admin only)
# ===========================================================================

@bp.route("/users", methods=["GET", "POST"])
@require_role("admin")
def users():
    store = current_app.store
    if request.method == "POST":
        username = (request.form.get("username") or "").strip()
        password = request.form.get("password") or ""
        role = request.form.get("role") or "viewer"
        if role not in ROLES:
            flash("Invalid role.", "error")
        elif not username:
            flash("Username is required.", "error")
        elif store.get_user(username) is not None:
            flash("That username already exists.", "error")
        else:
            err = validate_password_strength(password)
            if err:
                flash(err, "error")
            else:
                store.create_user(username, hash_password(password), role)
                _audit("user_created", f"{username} role={role}")
                flash(f"User '{username}' created.", "success")
        return redirect(url_for("main.users"))

    return render_template("users.html", users=store.list_users(), roles=ROLES)


@bp.route("/users/<username>/role", methods=["POST"])
@require_role("admin")
def set_user_role(username: str):
    role = request.form.get("role") or ""
    if role not in ROLES:
        abort(400)
    if username == g.user["username"] and role != "admin":
        flash("You cannot remove your own admin role.", "error")
        return redirect(url_for("main.users"))
    current_app.store.set_role(username, role)
    _audit("user_role_changed", f"{username} -> {role}")
    flash("Role updated.", "success")
    return redirect(url_for("main.users"))


@bp.route("/users/<username>/active", methods=["POST"])
@require_role("admin")
def toggle_user_active(username: str):
    if username == g.user["username"]:
        flash("You cannot disable your own account.", "error")
        return redirect(url_for("main.users"))
    active = request.form.get("active") == "1"
    current_app.store.set_active(username, active)
    _audit("user_active_changed", f"{username} active={active}")
    flash("User updated.", "success")
    return redirect(url_for("main.users"))


@bp.route("/users/<username>/password", methods=["POST"])
@require_role("admin")
def reset_user_password(username: str):
    new = request.form.get("new_password") or ""
    err = validate_password_strength(new)
    if err:
        flash(err, "error")
    else:
        current_app.store.set_password(username, hash_password(new))
        _audit("user_password_reset", username)
        flash(f"Password for '{username}' reset.", "success")
    return redirect(url_for("main.users"))


@bp.route("/users/<username>/delete", methods=["POST"])
@require_role("admin")
def delete_user(username: str):
    if username == g.user["username"]:
        flash("You cannot delete your own account.", "error")
        return redirect(url_for("main.users"))
    current_app.store.delete_user(username)
    _audit("user_deleted", username)
    flash(f"User '{username}' deleted.", "info")
    return redirect(url_for("main.users"))


@bp.route("/audit")
@require_role("admin")
def audit_log():
    return render_template(
        "audit.html", entries=current_app.store.recent_audit(300)
    )


# ===========================================================================
# Error handlers
# ===========================================================================

@bp.app_errorhandler(400)
def err_400(e):
    return render_template("error.html", code=400,
                           message=getattr(e, "description", "Bad request")), 400


@bp.app_errorhandler(403)
def err_403(e):
    return render_template("error.html", code=403,
                           message="You do not have permission to do that."), 403


@bp.app_errorhandler(404)
def err_404(e):
    return render_template("error.html", code=404,
                           message="Page not found."), 404


@bp.app_errorhandler(413)
def err_413(e):
    return render_template("error.html", code=413,
                           message="Upload too large."), 413
