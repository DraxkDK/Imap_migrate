"""
Authentication, role-based access control, and brute-force protection.

Roles (least privilege):
  viewer    — read-only: dashboard, reports, logs
  operator  — viewer + manage IMAP accounts + run test/dry-run/migration
  admin     — operator + manage web users and view the audit log

Passwords are hashed with Werkzeug's scrypt (salted, memory-hard). Login
failures increment a per-user counter; after LOCK_THRESHOLD failures the
account is locked for LOCK_MINUTES, blunting online password guessing.
"""
import functools
from datetime import datetime, timedelta, timezone

from flask import (
    abort, current_app, flash, g, redirect, request, session, url_for,
)
from werkzeug.security import check_password_hash, generate_password_hash

ROLES = ("viewer", "operator", "admin")
_ROLE_RANK = {"viewer": 0, "operator": 1, "admin": 2}

LOCK_THRESHOLD = 5      # failed attempts before lockout
LOCK_MINUTES = 15       # lockout duration


def hash_password(password: str) -> str:
    return generate_password_hash(password, method="scrypt")


def verify_password(password_hash: str, password: str) -> bool:
    return check_password_hash(password_hash, password)


def validate_password_strength(password: str) -> str:
    """Return an error message if the password is too weak, else ''."""
    if len(password) < 12:
        return "Password must be at least 12 characters long."
    classes = sum([
        any(c.islower() for c in password),
        any(c.isupper() for c in password),
        any(c.isdigit() for c in password),
        any(not c.isalnum() for c in password),
    ])
    if classes < 3:
        return ("Password must mix at least 3 of: lowercase, uppercase, "
                "digits, symbols.")
    return ""


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _parse_ts(value: str):
    if not value:
        return None
    try:
        return datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(
            tzinfo=timezone.utc
        )
    except ValueError:
        return None


def is_locked(user_row) -> bool:
    locked_until = _parse_ts(user_row["locked_until"])
    return locked_until is not None and locked_until > _now()


def attempt_login(store, username: str, password: str) -> tuple:
    """Return (user_row, error_message). user_row is None on failure.

    Uses a constant-ish path to avoid leaking whether a username exists."""
    user = store.get_user(username)

    if user is None:
        # Spend time hashing anyway so response timing doesn't reveal that the
        # username is unknown.
        verify_password(
            "scrypt:32768:8:1$placeholdersaltvalue$"
            "0" * 128, password)
        return None, "Invalid username or password."

    if not user["is_active"]:
        return None, "This account is disabled. Contact an administrator."

    if is_locked(user):
        return None, (
            f"Account locked due to repeated failures. Try again in "
            f"{LOCK_MINUTES} minutes."
        )

    if not verify_password(user["password_hash"], password):
        locked_until = (_now() + timedelta(minutes=LOCK_MINUTES)).strftime(
            "%Y-%m-%dT%H:%M:%SZ"
        )
        store.record_login_failure(username, LOCK_THRESHOLD, locked_until)
        return None, "Invalid username or password."

    store.record_login_success(username)
    return user, ""


def login_user(user_row) -> None:
    """Establish an authenticated session. Rotates the session id to prevent
    session fixation."""
    session.clear()
    session["uid"] = user_row["id"]
    session["uname"] = user_row["username"]
    session["role"] = user_row["role"]
    session.permanent = True


def logout_user() -> None:
    session.clear()


def load_current_user() -> None:
    """before_request hook: populate g.user from the session, revalidating
    against the DB so disabled/deleted users lose access immediately."""
    g.user = None
    uid = session.get("uid")
    if uid is None:
        return
    user = current_app.store.get_user_by_id(uid)
    if user is None or not user["is_active"]:
        session.clear()
        return
    g.user = user


def login_required(view):
    @functools.wraps(view)
    def wrapped(*args, **kwargs):
        if g.get("user") is None:
            flash("Please sign in to continue.", "warning")
            return redirect(url_for("auth.login", next=request.path))
        return view(*args, **kwargs)
    return wrapped


def require_role(*roles):
    """Decorator: allow access only to users whose role rank >= the lowest
    required role. e.g. require_role('operator') also admits 'admin'."""
    min_rank = min(_ROLE_RANK[r] for r in roles)

    def decorator(view):
        @functools.wraps(view)
        @login_required
        def wrapped(*args, **kwargs):
            user_rank = _ROLE_RANK.get(g.user["role"], -1)
            if user_rank < min_rank:
                current_app.store.audit(
                    "access_denied",
                    username=g.user["username"],
                    detail=f"{request.method} {request.path}",
                    ip=request.remote_addr or "",
                )
                abort(403)
            return view(*args, **kwargs)
        return wrapped
    return decorator
