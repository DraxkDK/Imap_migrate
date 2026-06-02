"""
Security utilities: TLS context, credential masking, file-permission checks.
"""
import getpass
import logging
import os
import re
import ssl
import stat
import sys

logger = logging.getLogger(__name__)

# Patterns used to mask secrets in any string passed through logging.
_SECRET_PATTERNS = [
    (re.compile(r"(password\s*[=:]\s*)\S+", re.IGNORECASE), r"\1********"),
    (re.compile(r"(passwd\s*[=:]\s*)\S+", re.IGNORECASE), r"\1********"),
    (re.compile(r"(secret\s*[=:]\s*)\S+", re.IGNORECASE), r"\1********"),
    (re.compile(r"(token\s*[=:]\s*)\S+", re.IGNORECASE), r"\1********"),
    (re.compile(r"(AUTH\s+LOGIN\s+)\S+", re.IGNORECASE), r"\1********"),
]


def mask_secrets(text: str) -> str:
    """Return text with any credential-like values replaced by ********."""
    for pattern, replacement in _SECRET_PATTERNS:
        text = pattern.sub(replacement, text)
    return text


def build_ssl_context(verify_cert: bool = True, allow_insecure: bool = False) -> ssl.SSLContext:
    """
    Build an SSLContext appropriate for IMAP client use.
    Verification is on by default; pass allow_insecure=True only for testing.
    """
    if allow_insecure:
        logger.warning(
            "WARNING: TLS certificate verification is DISABLED (--allow-insecure-cert). "
            "This is insecure and should only be used for testing on trusted networks."
        )
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        return ctx

    ctx = ssl.create_default_context()
    # Enforce TLS 1.2+ and strong ciphers — defaults on Python 3.10+.
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    return ctx


def check_file_permissions(path: str) -> None:
    """
    Warn if a credential file is world- or group-readable (Unix only).
    On Windows this check is skipped.
    """
    if not os.path.exists(path):
        return
    if sys.platform.startswith("win"):
        return
    file_stat = os.stat(path)
    mode = file_stat.st_mode
    if mode & (stat.S_IRGRP | stat.S_IWGRP | stat.S_IROTH | stat.S_IWOTH):
        logger.warning(
            "SECURITY WARNING: '%s' has loose permissions (%s). "
            "Run: chmod 600 %s",
            path,
            oct(mode & 0o777),
            path,
        )


def prompt_password(prompt_text: str) -> str:
    """Prompt for a password without echoing it to the terminal."""
    try:
        return getpass.getpass(prompt_text)
    except (EOFError, KeyboardInterrupt):
        print()
        sys.exit(1)
