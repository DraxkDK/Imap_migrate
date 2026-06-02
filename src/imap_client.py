"""
IMAP client wrapper.
  - SSL/STARTTLS
  - UID-based fetch / append with flag + INTERNALDATE preservation
  - fetch_envelope_info() — headers needed for fingerprint computation
  - append_message()      — returns Optional[str] target_uid (APPENDUID)
  - move_message()        — RFC 6851 MOVE or COPY+DELETE fallback
  - Folder listing / creation
"""
import email
import imaplib
import logging
import re
import time
from typing import Dict, List, Optional, Tuple

from src.security import build_ssl_context

logger = logging.getLogger(__name__)

_VALID_FLAGS = {"\\Seen", "\\Answered", "\\Flagged", "\\Deleted", "\\Draft"}

_DEFAULT_FOLDER_MAP: Dict[str, str] = {
    "Sent Items":      "Sent",
    "Sent Messages":   "Sent",
    "Deleted Items":   "Trash",
    "Deleted Messages":"Trash",
    "Junk E-mail":     "Junk",
    "Junk Email":      "Junk",
    "Spam":            "Junk",
    "Archive":         "Archive",
}

# Regex to extract APPENDUID from a successful APPEND response
_APPENDUID_RE = re.compile(rb"\[APPENDUID\s+\d+\s+(\d+)\]", re.IGNORECASE)


class IMAPError(Exception):
    pass


class IMAPClient:
    def __init__(
        self,
        host: str,
        port: int,
        use_ssl: bool = True,
        verify_cert: bool = True,
        allow_insecure: bool = False,
        timeout: int = 60,
    ) -> None:
        self.host = host
        self.port = port
        self.use_ssl = use_ssl
        self.verify_cert = verify_cert
        self.allow_insecure = allow_insecure
        self.timeout = timeout
        self._conn: Optional[imaplib.IMAP4] = None
        self.separator: str = "."

    # ------------------------------------------------------------------
    # Connection lifecycle
    # ------------------------------------------------------------------

    def connect(self, username: str, password: str) -> None:
        ssl_ctx = build_ssl_context(
            verify_cert=self.verify_cert,
            allow_insecure=self.allow_insecure,
        )
        try:
            if self.use_ssl:
                self._conn = imaplib.IMAP4_SSL(
                    host=self.host, port=self.port, ssl_context=ssl_ctx
                )
            else:
                self._conn = imaplib.IMAP4(self.host, self.port)
                if "STARTTLS" in self._conn.capabilities:
                    self._conn.starttls(ssl_context=ssl_ctx)
                else:
                    logger.warning(
                        "Server %s:%s does not offer STARTTLS — connection is unencrypted",
                        self.host, self.port,
                    )
            self._conn.login(username, password)
            logger.debug("Authenticated: %s @ %s:%s", username, self.host, self.port)
        except imaplib.IMAP4.error as exc:
            raise IMAPError(f"Auth failed for {username}@{self.host}: {exc}") from exc
        except OSError as exc:
            raise IMAPError(f"Cannot connect to {self.host}:{self.port}: {exc}") from exc

    def disconnect(self) -> None:
        if self._conn:
            try:
                self._conn.logout()
            except Exception:
                pass
            self._conn = None

    def supports_move(self) -> bool:
        """Return True if the server advertises RFC 6851 MOVE extension."""
        if self._conn is None:
            return False
        caps = self._conn.capabilities
        if isinstance(caps, (list, tuple)):
            return b"MOVE" in caps or "MOVE" in caps
        return False

    # ------------------------------------------------------------------
    # Folder operations
    # ------------------------------------------------------------------

    def list_folders(self) -> List[str]:
        folders: List[str] = []
        try:
            status, items = self._conn.list()
            if status != "OK":
                return folders
            for item in items:
                if item is None:
                    continue
                decoded = item.decode("utf-8", errors="replace")
                m = re.match(r'\(([^)]*)\)\s+"([^"]+)"\s+"?([^"]*)"?\s*$', decoded)
                if not m:
                    m = re.match(r'\(([^)]*)\)\s+"([^"]+)"\s+(\S+)', decoded)
                if m:
                    flags_str = m.group(1)
                    self.separator = m.group(2)
                    name = m.group(3).strip().strip('"')
                    if "\\Noselect" not in flags_str and name:
                        folders.append(name)
        except Exception as exc:
            logger.error("list_folders on %s: %s", self.host, exc)
        return folders

    def folder_exists(self, folder: str) -> bool:
        try:
            status, _ = self._conn.select(self._quote(folder))
            if status == "OK":
                try:
                    self._conn.select("INBOX")
                except Exception:
                    pass
                return True
        except Exception:
            pass
        return False

    def create_folder(self, folder: str) -> bool:
        try:
            status, _ = self._conn.create(self._quote(folder))
            if status == "OK":
                return True
            return self.folder_exists(folder)
        except Exception as exc:
            logger.error("create_folder '%s': %s", folder, exc)
            return False

    def ensure_folder(self, folder: str) -> bool:
        if self.folder_exists(folder):
            return True
        return self.create_folder(folder)

    def select_folder(self, folder: str) -> Optional[int]:
        try:
            status, data = self._conn.select(self._quote(folder))
            if status == "OK" and data:
                return int(data[0]) if data[0] else 0
        except Exception as exc:
            logger.error("select_folder '%s': %s", folder, exc)
        return None

    # ------------------------------------------------------------------
    # Message fetch operations
    # ------------------------------------------------------------------

    def get_all_uids(self) -> List[str]:
        try:
            status, data = self._conn.uid("search", None, "ALL")
            if status == "OK" and data and data[0]:
                raw = data[0]
                return (raw.decode() if isinstance(raw, bytes) else raw).split()
        except Exception as exc:
            logger.error("UID SEARCH ALL: %s", exc)
        return []

    def fetch_message_id_header(self, uid: str) -> Optional[str]:
        """Fetch only the Message-ID header (cheap)."""
        try:
            status, data = self._conn.uid(
                "fetch", uid, "(BODY.PEEK[HEADER.FIELDS (MESSAGE-ID)])"
            )
            if status == "OK" and data:
                for part in data:
                    if isinstance(part, tuple) and len(part) == 2 and part[1]:
                        msg = email.message_from_bytes(part[1])
                        mid = msg.get("Message-ID", "").strip()
                        return mid if mid else None
        except Exception as exc:
            logger.debug("fetch_message_id UID %s: %s", uid, exc)
        return None

    def fetch_envelope_info(self, uid: str) -> dict:
        """
        Fetch headers and size needed for fingerprint computation.
        Returns dict: {message_id, from_, date_, subject_, size}
        All values default to empty string / 0 on failure.
        """
        result = {"message_id": "", "from_": "", "date_": "", "subject_": "", "size": 0}
        try:
            status, data = self._conn.uid(
                "fetch", uid,
                "(BODY.PEEK[HEADER.FIELDS (MESSAGE-ID FROM DATE SUBJECT)] RFC822.SIZE)"
            )
            if status != "OK" or not data:
                return result

            for part in data:
                if isinstance(part, tuple) and len(part) == 2:
                    header_info = part[0]
                    header_bytes = part[1]

                    # Parse RFC822.SIZE from the response header line
                    info_str = (
                        header_info.decode("utf-8", errors="replace")
                        if isinstance(header_info, bytes)
                        else str(header_info)
                    )
                    size_m = re.search(r"RFC822\.SIZE\s+(\d+)", info_str)
                    if size_m:
                        result["size"] = int(size_m.group(1))

                    # Parse email headers
                    if isinstance(header_bytes, bytes) and header_bytes:
                        msg = email.message_from_bytes(header_bytes)
                        result["message_id"] = msg.get("Message-ID", "").strip()
                        result["from_"]      = msg.get("From", "").strip()
                        result["date_"]      = msg.get("Date", "").strip()
                        result["subject_"]   = msg.get("Subject", "").strip()

        except Exception as exc:
            logger.debug("fetch_envelope_info UID %s: %s", uid, exc)

        return result

    def fetch_message(
        self, uid: str
    ) -> Optional[Tuple[bytes, List[str], Optional[str]]]:
        """
        Fetch full message bytes + flags + INTERNALDATE.
        Uses BODY.PEEK[] to avoid marking source as \\Seen.
        Returns (raw_bytes, flags, internal_date) or None.
        """
        try:
            status, data = self._conn.uid(
                "fetch", uid, "(FLAGS INTERNALDATE BODY.PEEK[])"
            )
            if status != "OK" or not data:
                return None

            raw_message: Optional[bytes] = None
            flags: List[str] = []
            internal_date: Optional[str] = None

            for part in data:
                if isinstance(part, tuple) and len(part) == 2:
                    header_raw = part[0]
                    body_raw   = part[1]
                    if not isinstance(body_raw, bytes):
                        continue
                    raw_message = body_raw
                    hdr = (
                        header_raw.decode("utf-8", errors="replace")
                        if isinstance(header_raw, bytes)
                        else str(header_raw)
                    )
                    fm = re.search(r"FLAGS \(([^)]*)\)", hdr)
                    if fm:
                        flags = fm.group(1).split()
                    dm = re.search(r'INTERNALDATE "([^"]+)"', hdr)
                    if dm:
                        internal_date = dm.group(1)

            return (raw_message, flags, internal_date) if raw_message is not None else None

        except Exception as exc:
            logger.error("fetch_message UID %s: %s", uid, exc)
            return None

    # ------------------------------------------------------------------
    # Message write operations
    # ------------------------------------------------------------------

    def append_message(
        self,
        folder: str,
        raw_message: bytes,
        flags: List[str],
        internal_date: Optional[str],
    ) -> Tuple[bool, Optional[str]]:
        """
        Append a message.  Returns (success, target_uid).
        target_uid is parsed from APPENDUID response if the server supports it,
        otherwise None.
        """
        clean_flags = " ".join(f for f in flags if f in _VALID_FLAGS) or None
        date_time = None
        if internal_date:
            try:
                date_time = imaplib.Internaldate2tuple(
                    f'* 1 FETCH (INTERNALDATE "{internal_date}")'.encode()
                )
            except Exception:
                pass

        try:
            status, response = self._conn.append(
                self._quote(folder), clean_flags, date_time, raw_message
            )
            if status != "OK":
                return False, None

            # Try to extract target UID from APPENDUID response
            target_uid: Optional[str] = None
            for part in response:
                if isinstance(part, bytes):
                    m = _APPENDUID_RE.search(part)
                    if m:
                        target_uid = m.group(1).decode()
                        break

            return True, target_uid

        except Exception as exc:
            logger.error("append_message to '%s': %s", folder, exc)
            return False, None

    def move_message(
        self,
        uid: str,
        src_folder: str,
        dst_folder: str,
    ) -> Tuple[bool, str]:
        """
        Move uid from src_folder to dst_folder using:
          1. RFC 6851 UID MOVE (if available)
          2. Fallback: UID COPY + UID STORE \\Deleted + EXPUNGE

        Returns (success, error_message).
        Caller must have already selected src_folder.
        """
        try:
            if self.supports_move():
                status, _ = self._conn.uid("move", uid, self._quote(dst_folder))
                if status == "OK":
                    return True, ""
                logger.debug("UID MOVE failed (status=%s) — falling back to COPY+DELETE", status)

            # COPY
            status, _ = self._conn.uid("copy", uid, self._quote(dst_folder))
            if status != "OK":
                return False, f"UID COPY to '{dst_folder}' failed"

            # Mark \Deleted
            status, _ = self._conn.uid("store", uid, "+FLAGS", "(\\Deleted)")
            if status != "OK":
                return False, "UID STORE \\Deleted failed"

            self._conn.expunge()
            return True, ""

        except Exception as exc:
            return False, str(exc)

    # ------------------------------------------------------------------
    # Static helpers
    # ------------------------------------------------------------------

    @staticmethod
    def _quote(folder: str) -> str:
        return f'"{folder.strip(chr(34))}"'

    @staticmethod
    def map_folder_name(
        source_name: str,
        extra_map: Optional[Dict[str, str]] = None,
    ) -> str:
        combined = dict(_DEFAULT_FOLDER_MAP)
        if extra_map:
            combined.update(extra_map)
        return combined.get(source_name, source_name)
