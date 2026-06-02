"""
WSGI entry point for the IMAP Migration web UI.

Production (behind Nginx/Caddy which terminates TLS):

  Linux:
    gunicorn --workers 1 --threads 8 --bind 127.0.0.1:8000 wsgi:application
  Windows:
    waitress-serve --listen=127.0.0.1:8000 wsgi:application

IMPORTANT: use a SINGLE worker process (gunicorn --workers 1, threads for
concurrency). The background migration job and live status live in process
memory; multiple worker processes would not share them. Scale request handling
with threads, not processes.

Required environment variable:
  IMAP_WEB_SECRET_KEY   — see web/crypto.py / README.
"""
from web.app import create_app

application = create_app()
app = application  # alias for `flask --app wsgi run`
