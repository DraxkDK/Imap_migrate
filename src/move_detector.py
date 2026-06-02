"""
Move-aware sync engine.

Problem
-------
After first migration, a user moves email A from Folder-X to Folder-Y on source.
A naive incremental sync would copy email A again into Folder-Y on target,
creating a duplicate.

Solution
--------
Every migrated message is identified by a *fingerprint* derived from its
Message-ID header (or a combination of From + Date + Subject + size when
Message-ID is absent/unreliable).  The fingerprint is stored in the state DB
independently of which folder the message currently lives in.

Three move policies
-------------------
copy-only    (default, safest)
  - Ignore fingerprint matches in other folders.
  - Always copy; never delete anything on target.
  - May produce duplicates when the user moves mail between syncs.

global-dedupe
  - If the same fingerprint already exists on target (any folder), skip.
  - No copies, no deletes.  Folder structure on target is NOT updated.
  - Safe, no target modifications.

move-target  (requires --sync-moves --move-policy move-target --allow-target-delete)
  - If fingerprint is found in a different folder than before, treat as MOVED.
  - Copy message to the new target folder (if not already there).
  - Delete old target copy (IMAP MOVE if supported, else COPY+STORE+EXPUNGE).
  - Update state DB.
  - Generates moved_items.csv.

No target deletion happens without --allow-target-delete.
"""
import hashlib
import logging
import threading
from dataclasses import dataclass
from typing import Optional

logger = logging.getLogger(__name__)

POLICY_COPY_ONLY   = "copy-only"
POLICY_GLOBAL_DEDUPE = "global-dedupe"
POLICY_MOVE_TARGET = "move-target"
VALID_POLICIES = (POLICY_COPY_ONLY, POLICY_GLOBAL_DEDUPE, POLICY_MOVE_TARGET)

# Action codes returned by MoveDetector.check()
ACTION_COPY   = "copy"
ACTION_SKIP   = "skip"
ACTION_MOVE   = "move"


# ---------------------------------------------------------------------------
# Fingerprint
# ---------------------------------------------------------------------------

def compute_fingerprint(
    message_id: str,
    from_: str = "",
    date_: str = "",
    subject: str = "",
    size: int = 0,
) -> str:
    """
    Compute a stable 40-hex-char fingerprint for a message.

    Priority:
      1. Message-ID (RFC 2822) — globally unique when present and well-formed.
      2. Heuristic combination of From + Date + Subject + size — fallback
         for messages without a Message-ID or with known-bad duplicated IDs.

    The fingerprint is stored in the state DB and used for cross-folder
    duplicate/move detection across incremental syncs.
    """
    mid = (message_id or "").strip()
    if mid and len(mid) > 4 and mid not in ("<>", ""):
        raw = f"mid\x00{mid}"
    else:
        # Normalise aggressively to survive whitespace/case drift
        raw = "hdr\x00{}\x00{}\x00{}\x00{}".format(
            from_.strip().lower(),
            date_.strip(),
            subject.strip(),
            size,
        )
    return hashlib.sha256(raw.encode("utf-8", errors="replace")).hexdigest()[:40]


# ---------------------------------------------------------------------------
# Moved-item record (for report)
# ---------------------------------------------------------------------------

@dataclass
class MovedItem:
    source_mailbox: str
    from_folder: str
    to_folder: str
    source_uid: str
    target_uid: Optional[str]
    message_id: str
    fingerprint: str
    action: str          # "move-target" | "global-dedupe-skip" | "copy-only-copy"
    status: str          # "success" | "failed"
    timestamp: str
    error: str = ""


# ---------------------------------------------------------------------------
# Move detector
# ---------------------------------------------------------------------------

class MoveDetector:
    """
    Encapsulates per-mailbox move detection and action logic.
    One instance is shared across all workers — all public methods are thread-safe.
    """

    def __init__(
        self,
        db,                        # StateDB instance
        policy: str,
        allow_target_delete: bool,
        reporter,                  # Reporter instance
    ) -> None:
        if policy not in VALID_POLICIES:
            raise ValueError(f"Invalid move policy '{policy}'. Choose from: {VALID_POLICIES}")
        self.db = db
        self.policy = policy
        self.allow_target_delete = allow_target_delete
        self.reporter = reporter
        self._lock = threading.Lock()

    # ------------------------------------------------------------------
    # Decision: what to do with this message?
    # ------------------------------------------------------------------

    def check(
        self,
        source_mailbox: str,
        target_mailbox: str,
        src_folder: str,
        uid: str,
        fingerprint: str,
    ) -> str:
        """
        Returns one of: ACTION_COPY | ACTION_SKIP | ACTION_MOVE

        Called for every message that is NOT already recorded for
        (source_mailbox, src_folder, uid) — i.e. UID-level dup already cleared.
        """
        if self.policy == POLICY_COPY_ONLY:
            return ACTION_COPY

        existing = self.db.find_by_fingerprint(source_mailbox, target_mailbox, fingerprint)
        if not existing:
            return ACTION_COPY   # never seen — normal copy

        if existing["source_folder"] == src_folder:
            # Same folder, already migrated — treat as normal dup (shouldn't
            # reach here normally because UID check above would have caught it,
            # but handle defensively).
            return ACTION_SKIP

        # Fingerprint found in a DIFFERENT folder → message was moved on source
        logger.debug(
            "Move detected: fingerprint=%s was in '%s', now in '%s'",
            fingerprint[:12], existing["source_folder"], src_folder,
        )

        if self.policy == POLICY_GLOBAL_DEDUPE:
            return ACTION_SKIP    # already on target somewhere, don't copy again

        if self.policy == POLICY_MOVE_TARGET:
            return ACTION_MOVE    # execute move on target

        return ACTION_COPY        # fallback

    # ------------------------------------------------------------------
    # Execution: perform move on target
    # ------------------------------------------------------------------

    def execute_move(
        self,
        target,                   # IMAPClient connected to target
        fingerprint: str,
        source_mailbox: str,
        target_mailbox: str,
        new_src_folder: str,
        new_tgt_folder: str,
        new_src_uid: str,
        message_id: str,
        timestamp: str,
        worker_id: str,
    ) -> bool:
        """
        Move the existing target message from old_folder → new_tgt_folder.

        Returns True if the move completed successfully.
        The caller should NOT copy the message separately if True is returned.
        """
        existing = self.db.find_by_fingerprint(source_mailbox, target_mailbox, fingerprint)
        if not existing:
            logger.debug("[%s] execute_move: no existing record for fp=%s", worker_id, fingerprint[:12])
            return False

        old_tgt_folder = existing["target_folder"]
        old_target_uid = existing.get("target_uid")

        if old_tgt_folder == new_tgt_folder:
            # Already in the right place — just update source metadata
            self.db.update_fingerprint_location(
                source_mailbox=source_mailbox,
                fingerprint=fingerprint,
                new_src_folder=new_src_folder,
                new_src_uid=new_src_uid,
            )
            logger.debug(
                "[%s] Move: already in correct target folder '%s' — metadata updated",
                worker_id, new_tgt_folder,
            )
            return True

        moved_ok = False
        error_msg = ""

        if old_target_uid and self.allow_target_delete:
            # Attempt IMAP-level move: MOVE or COPY+STORE+EXPUNGE
            moved_ok, error_msg = self._imap_move_message(
                target=target,
                src_folder=old_tgt_folder,
                uid=old_target_uid,
                dst_folder=new_tgt_folder,
                worker_id=worker_id,
            )
        else:
            if not self.allow_target_delete:
                error_msg = "--allow-target-delete not set; cannot delete old copy"
            else:
                error_msg = "target_uid unknown; cannot locate old copy"
            logger.warning(
                "[%s] Move: cannot remove old copy from '%s': %s",
                worker_id, old_tgt_folder, error_msg,
            )

        # Update state DB regardless (source location changed)
        if moved_ok or not self.allow_target_delete:
            self.db.update_fingerprint_location(
                source_mailbox=source_mailbox,
                fingerprint=fingerprint,
                new_src_folder=new_src_folder,
                new_src_uid=new_src_uid,
                new_tgt_folder=new_tgt_folder if moved_ok else None,
            )

        # Record in moved_items report
        self.reporter.add_moved(
            MovedItem(
                source_mailbox=source_mailbox,
                from_folder=old_tgt_folder,
                to_folder=new_tgt_folder,
                source_uid=new_src_uid,
                target_uid=old_target_uid,
                message_id=message_id,
                fingerprint=fingerprint,
                action="move-target" if self.allow_target_delete else "move-no-delete",
                status="success" if moved_ok else "partial",
                timestamp=timestamp,
                error=error_msg,
            )
        )

        logger.info(
            "[%s] Move-target: '%s'→'%s' fp=%s status=%s",
            worker_id, old_tgt_folder, new_tgt_folder,
            fingerprint[:12], "success" if moved_ok else "partial",
        )
        return moved_ok

    # ------------------------------------------------------------------
    # IMAP move implementation
    # ------------------------------------------------------------------

    def _imap_move_message(
        self,
        target,
        src_folder: str,
        uid: str,
        dst_folder: str,
        worker_id: str,
    ) -> tuple:
        """
        Move uid from src_folder to dst_folder on the target server.
        Returns (success: bool, error_msg: str).

        Tries RFC 6851 UID MOVE first; falls back to UID COPY + STORE \\Deleted + EXPUNGE.
        """
        try:
            # Select the old folder
            cnt = target.select_folder(src_folder)
            if cnt is None:
                return False, f"Cannot select source folder '{src_folder}' on target"

            conn = target._conn   # access underlying imaplib connection

            # --- Try RFC 6851 MOVE ---
            if b"MOVE" in conn.capabilities or "MOVE" in conn.capabilities:
                status, data = conn.uid("move", uid, target._quote(dst_folder))
                if status == "OK":
                    logger.debug(
                        "[%s] UID MOVE %s '%s'→'%s' OK", worker_id, uid, src_folder, dst_folder
                    )
                    return True, ""
                logger.debug("[%s] UID MOVE failed (%s), falling back to COPY+DELETE", worker_id, status)

            # --- Fallback: COPY + STORE \Deleted + EXPUNGE ---
            # 1. Copy to destination
            status, _ = conn.uid("copy", uid, target._quote(dst_folder))
            if status != "OK":
                return False, f"UID COPY to '{dst_folder}' failed (status={status})"

            # 2. Mark original as \Deleted
            status, _ = conn.uid("store", uid, "+FLAGS", "(\\Deleted)")
            if status != "OK":
                return False, f"UID STORE \\Deleted failed"

            # 3. Expunge only the flagged messages in this folder
            conn.expunge()
            logger.debug(
                "[%s] COPY+DELETE %s '%s'→'%s' OK", worker_id, uid, src_folder, dst_folder
            )
            return True, ""

        except Exception as exc:
            return False, str(exc)
