#!/usr/bin/env python3
"""
Admin CLI for the IMAP Migration web UI.

Bootstrap the first admin and manage users without going through the browser.
IMAP_WEB_SECRET_KEY must be set (same value the web app uses).

Examples:
  python manage_web.py create-admin --username admin
  python manage_web.py list-users
  python manage_web.py set-password --username admin
  python manage_web.py set-role --username bob --role operator
  python manage_web.py gen-secret          # print a strong IMAP_WEB_SECRET_KEY
"""
import argparse
import getpass
import os
import secrets
import sys

from web.auth import ROLES, hash_password, validate_password_strength
from web.crypto import SecretBox
from web.store import Store


def _open_store() -> Store:
    secret = os.environ.get("IMAP_WEB_SECRET_KEY", "")
    if not secret:
        sys.exit("ERROR: set IMAP_WEB_SECRET_KEY first (see: gen-secret).")
    box = SecretBox(secret)
    return Store(os.environ.get("IMAP_WEB_DB", "web_app.db"), box)


def _prompt_password() -> str:
    while True:
        pw = getpass.getpass("New password: ")
        err = validate_password_strength(pw)
        if err:
            print(err)
            continue
        if pw != getpass.getpass("Confirm password: "):
            print("Passwords do not match.")
            continue
        return pw


def cmd_gen_secret(_args) -> None:
    print(secrets.token_urlsafe(64))


def cmd_create_admin(args) -> None:
    store = _open_store()
    if store.get_user(args.username):
        sys.exit(f"User '{args.username}' already exists.")
    pw = _prompt_password()
    store.create_user(args.username, hash_password(pw), "admin")
    print(f"Admin user '{args.username}' created.")


def cmd_create_user(args) -> None:
    store = _open_store()
    if args.role not in ROLES:
        sys.exit(f"Role must be one of: {', '.join(ROLES)}")
    if store.get_user(args.username):
        sys.exit(f"User '{args.username}' already exists.")
    pw = _prompt_password()
    store.create_user(args.username, hash_password(pw), args.role)
    print(f"User '{args.username}' ({args.role}) created.")


def cmd_list_users(_args) -> None:
    store = _open_store()
    rows = store.list_users()
    if not rows:
        print("No users.")
        return
    print(f"{'username':<24} {'role':<10} {'active':<7} last_login")
    for u in rows:
        print(f"{u['username']:<24} {u['role']:<10} "
              f"{'yes' if u['is_active'] else 'no':<7} {u['last_login'] or '—'}")


def cmd_set_password(args) -> None:
    store = _open_store()
    if not store.get_user(args.username):
        sys.exit(f"No such user: {args.username}")
    pw = _prompt_password()
    store.set_password(args.username, hash_password(pw))
    print(f"Password updated for '{args.username}'.")


def cmd_set_role(args) -> None:
    store = _open_store()
    if args.role not in ROLES:
        sys.exit(f"Role must be one of: {', '.join(ROLES)}")
    if not store.get_user(args.username):
        sys.exit(f"No such user: {args.username}")
    store.set_role(args.username, args.role)
    print(f"Role for '{args.username}' set to {args.role}.")


def main() -> None:
    p = argparse.ArgumentParser(description="Manage the IMAP Migration web UI")
    sub = p.add_subparsers(dest="cmd", required=True)

    sub.add_parser("gen-secret", help="Print a strong IMAP_WEB_SECRET_KEY").set_defaults(func=cmd_gen_secret)

    s = sub.add_parser("create-admin", help="Create the first admin user")
    s.add_argument("--username", required=True)
    s.set_defaults(func=cmd_create_admin)

    s = sub.add_parser("create-user", help="Create a user with a role")
    s.add_argument("--username", required=True)
    s.add_argument("--role", default="viewer")
    s.set_defaults(func=cmd_create_user)

    sub.add_parser("list-users", help="List web users").set_defaults(func=cmd_list_users)

    s = sub.add_parser("set-password", help="Reset a user's password")
    s.add_argument("--username", required=True)
    s.set_defaults(func=cmd_set_password)

    s = sub.add_parser("set-role", help="Change a user's role")
    s.add_argument("--username", required=True)
    s.add_argument("--role", required=True)
    s.set_defaults(func=cmd_set_role)

    args = p.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
