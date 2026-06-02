#!/usr/bin/env python3
"""
Development runner for the IMAP Migration web UI.

For LOCAL development only — do NOT expose this to the internet. In production
run behind Nginx/Caddy via wsgi.py (gunicorn/waitress); see deploy/.

  # one-time:
  export IMAP_WEB_SECRET_KEY="$(python manage_web.py gen-secret)"
  export IMAP_WEB_BEHIND_PROXY=0      # plain http on localhost during dev
  python manage_web.py create-admin --username admin

  # run:
  python run_web.py            # http://127.0.0.1:5000
"""
import os

from web.app import create_app

if __name__ == "__main__":
    app = create_app()
    host = os.environ.get("IMAP_WEB_HOST", "127.0.0.1")
    port = int(os.environ.get("IMAP_WEB_PORT", "5000"))
    # debug=False even in dev: the reloader spawns a second process that would
    # not share the in-memory job manager, and the debugger is an RCE risk.
    app.run(host=host, port=port, debug=False, threaded=True)
