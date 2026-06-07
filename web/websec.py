"""
Web hardening: CSRF tokens, response security headers, and reverse-proxy
awareness.

This is deliberately dependency-light (no Flask-WTF): a synchronizer-token
CSRF scheme plus a strict set of security headers covers the main browser-side
attack surface (CSRF, clickjacking, MIME sniffing, mixed content, referrer
leakage, and a restrictive Content-Security-Policy that blocks injected
inline/3rd-party scripts).
"""
import hmac
import secrets

from flask import abort, current_app, g, request, session
from werkzeug.middleware.proxy_fix import ProxyFix

CSRF_SESSION_KEY = "_csrf_token"
# State-changing methods must carry a valid CSRF token.
_PROTECTED_METHODS = {"POST", "PUT", "PATCH", "DELETE"}


# ---------------------------------------------------------------------------
# CSRF
# ---------------------------------------------------------------------------

def generate_csrf_token() -> str:
    token = session.get(CSRF_SESSION_KEY)
    if not token:
        token = secrets.token_urlsafe(32)
        session[CSRF_SESSION_KEY] = token
    return token


def _csrf_protect() -> None:
    if request.method not in _PROTECTED_METHODS:
        return
    # API clients may authenticate per-request; here everything is cookie/session
    # based, so every unsafe request must present the token.
    expected = session.get(CSRF_SESSION_KEY, "")
    sent = (
        request.form.get("csrf_token")
        or request.headers.get("X-CSRF-Token", "")
    )
    if not expected or not sent or not hmac.compare_digest(expected, sent):
        current_app.logger.warning(
            "CSRF rejection: %s %s from %s",
            request.method, request.path, request.remote_addr,
        )
        abort(400, description="Invalid or missing CSRF token.")


# ---------------------------------------------------------------------------
# Security headers
# ---------------------------------------------------------------------------

_CSP = (
    "default-src 'self'; "
    "script-src 'self'; "
    "style-src 'self'; "
    "img-src 'self' data:; "
    "font-src 'self'; "
    "connect-src 'self'; "
    "form-action 'self'; "
    "frame-ancestors 'none'; "
    "base-uri 'self'; "
    "object-src 'none'"
)


def _set_security_headers(response):
    response.headers["X-Content-Type-Options"] = "nosniff"
    response.headers["X-Frame-Options"] = "DENY"
    response.headers["Referrer-Policy"] = "no-referrer"
    response.headers["Content-Security-Policy"] = _CSP
    response.headers["Permissions-Policy"] = (
        "geolocation=(), microphone=(), camera=()"
    )
    response.headers["Cross-Origin-Opener-Policy"] = "same-origin"
    response.headers["Cache-Control"] = "no-store"
    # HSTS only makes sense once served over HTTPS (typically at the proxy).
    if request.is_secure or current_app.config.get("FORCE_HSTS"):
        response.headers["Strict-Transport-Security"] = (
            "max-age=31536000; includeSubDomains"
        )
    return response


# ---------------------------------------------------------------------------
# Wiring
# ---------------------------------------------------------------------------

def init_security(app) -> None:
    """Register CSRF protection, security headers, secure-cookie/session
    settings, and reverse-proxy handling on the Flask app."""

    # --- Secure session cookies -------------------------------------------
    app.config.setdefault("SESSION_COOKIE_HTTPONLY", True)
    app.config.setdefault("SESSION_COOKIE_SAMESITE", "Lax")
    # Secure flag: only send the cookie over HTTPS. Controlled by config so the
    # app still works on a plain-HTTP localhost during development.
    app.config.setdefault(
        "SESSION_COOKIE_SECURE",
        app.config.get("BEHIND_TLS_PROXY", True),
    )
    app.config.setdefault("SESSION_COOKIE_NAME", "imapmig_session")
    app.config.setdefault("PERMANENT_SESSION_LIFETIME", 3600)  # 1h idle window

    # --- Reverse-proxy awareness ------------------------------------------
    # When running behind Nginx/Caddy (which terminate TLS), trust the
    # X-Forwarded-* headers from exactly one proxy hop so request.is_secure,
    # request.remote_addr, and url_for(_external) reflect the real client/scheme.
    # x_prefix honours X-Forwarded-Prefix so url_for() emits the right path when
    # the app is mounted under a sub-path (e.g. /imap behind the shared proxy).
    if app.config.get("BEHIND_TLS_PROXY", True):
        app.wsgi_app = ProxyFix(
            app.wsgi_app, x_for=1, x_proto=1, x_host=1, x_port=1, x_prefix=1
        )

    app.before_request(_csrf_protect)
    app.after_request(_set_security_headers)

    @app.context_processor
    def _inject_csrf():
        return {"csrf_token": generate_csrf_token}
