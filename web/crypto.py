"""
At-rest encryption for stored IMAP credentials.

IMAP passwords cannot be one-way hashed (we must replay them to log into the
source/target servers), so they are encrypted with Fernet (AES-128-CBC + HMAC).

The Fernet key is derived from a master secret supplied via the environment
variable IMAP_WEB_SECRET_KEY. It MUST be set and kept out of source control.
Losing it means stored account passwords can no longer be decrypted (re-enter
them); leaking it is equivalent to leaking the passwords, so treat it like one.

    # generate a strong key once and store it in your secret manager / env:
    python -c "import secrets;print(secrets.token_urlsafe(64))"
"""
import base64
import hashlib
from typing import Optional

from cryptography.fernet import Fernet, InvalidToken


def _derive_fernet_key(master_secret: str) -> bytes:
    """Derive a urlsafe-base64 32-byte Fernet key from an arbitrary master
    secret using SHA-256. (The master secret is expected to be high-entropy;
    this only normalises its length/encoding.)"""
    digest = hashlib.sha256(master_secret.encode("utf-8")).digest()
    return base64.urlsafe_b64encode(digest)


class SecretBox:
    def __init__(self, master_secret: str) -> None:
        if not master_secret or len(master_secret) < 16:
            raise ValueError(
                "IMAP_WEB_SECRET_KEY must be set to a strong value "
                "(>=16 chars). Generate one with: "
                "python -c \"import secrets;print(secrets.token_urlsafe(64))\""
            )
        self._fernet = Fernet(_derive_fernet_key(master_secret))

    def encrypt(self, plaintext: str) -> str:
        return self._fernet.encrypt(plaintext.encode("utf-8")).decode("ascii")

    def decrypt(self, token: str) -> str:
        try:
            return self._fernet.decrypt(token.encode("ascii")).decode("utf-8")
        except InvalidToken as exc:
            raise ValueError(
                "Cannot decrypt stored credential — IMAP_WEB_SECRET_KEY "
                "differs from the one used when it was saved."
            ) from exc
