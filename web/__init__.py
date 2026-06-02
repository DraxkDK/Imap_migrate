"""
IMAP Migration Tool — optional web interface (Flask).

The CLI core (imap_migrate.py + src/) stays pure-stdlib. This package adds a
secured, multi-user web UI on top of it:

  - multi-user authentication with roles (admin / operator / viewer)
  - IMAP account credentials encrypted at rest (Fernet)
  - CSRF protection, hardened security headers, brute-force lockout
  - reverse-proxy aware (run behind Nginx/Caddy which terminates TLS)
  - dashboard, account management, run/test/dry-run, reports & logs

Entry point: web.app.create_app()
"""
__version__ = "1.0.0"
