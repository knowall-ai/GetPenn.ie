"""
Preppie reply-back (#30): post the spine's meeting-backlog summary into a Teams channel as an
Adaptive Card, via a Teams Incoming Webhook / Workflows URL.

Mirrors the defensive, never-raises discipline of agent.spine.Backend._request: the HTTP
transport is injectable (an `opener` callable, default urllib.request.urlopen) so the whole
module is unit-testable offline with zero network and zero credentials - see
tests/test_reply_back.py.
"""
from __future__ import annotations

import json
import time
import urllib.request
import urllib.error
from typing import Any, Callable
from urllib.parse import quote

# Teams (Workflows / Incoming Webhook) rejects payloads over roughly 28KB. Capping the number of
# listed items keeps the card comfortably under that even for a very large meeting backlog.
DEFAULT_MAX_ITEMS = 20

# --- size guards (layers a/b/c against the ~28KB Teams card limit) ---
# (a) per-title cap, applied in _item_line.
MAX_TITLE_CHARS = 200
# (b) reply_back cap, applied in build_adaptive_card - the LLM's free-form final message is
# otherwise unbounded and a single verbose reply can blow the card past the limit on its own.
MAX_REPLY_BACK_CHARS = 3500
# Header text also comes from caller-supplied `meeting_title`; cap it too so a pathological
# caller can't defeat the guarantee below via that field.
MAX_HEADER_CHARS = 300
# (c) final hard guard: after assembling the whole card, we keep trimming body content until the
# serialized card is under this budget (a little short of the real ~28KB limit for safety
# margin), regardless of how (a) and (b) were bypassed or what nonsense `max_items` is. This is
# the layer that GUARANTEES the cap - (a) and (b) just make it rarely have to do any work.
MAX_CARD_BYTES = 26000

# urllib.parse.quote's "safe" set for URLs we re-embed in Adaptive Card markdown link syntax:
# keep URL-structural characters literal (so a normal https://host/path?a=b#frag round-trips
# unchanged) but percent-encode anything that would break the `(...)` markdown link syntax, most
# importantly spaces, unescaped parens, and raw unicode. "%" is kept safe so an already-encoded
# URL is not double-encoded.
_URL_SAFE_CHARS = ":/?#[]@!$&'()*+,;=%~"

_DEFAULT_HEADER = "🗂️ Preppie — meeting backlog"
_EMPTY_TEXT = "No backlog items were created from this meeting."


# ---------- small, dependency-free helpers ----------
def _pluralize(word: str) -> str:
    """Best-effort plural of a work item type name ('Bug' -> 'Bugs', 'Story' -> 'Stories')."""
    if not word:
        return word
    if word.endswith("y") and len(word) > 1 and word[-2].lower() not in "aeiou":
        return word[:-1] + "ies"
    if word.endswith(("s", "x", "sh", "ch")):
        return word + "es"
    return word + "s"


def _clean_text(value: Any, default: str = "") -> str:
    """Coerce anything to a single-line-ish plain string. Never raises."""
    try:
        if value is None:
            return default
        s = value if isinstance(value, str) else str(value)
        s = s.replace("\r\n", " ").replace("\r", " ").replace("\n", " ").strip()
        return s if s else default
    except Exception:
        return default


def _as_dict(value: Any) -> dict:
    return value if isinstance(value, dict) else {}


def _as_list(value: Any) -> list:
    return value if isinstance(value, list) else []


def _normalize_result(result: Any) -> tuple[list, str]:
    """Defensively pull (created_items, reply_back_text) out of a possibly-malformed result."""
    result = _as_dict(result)
    created = [_as_dict(item) for item in _as_list(result.get("created"))]
    reply_back = result.get("reply_back")
    reply_back = reply_back.strip() if isinstance(reply_back, str) else _clean_text(reply_back, "")
    return created, reply_back


def _quote_url(url: str) -> str:
    """Percent-encode a URL for safe interpolation into '(...)' markdown link syntax. A raw space
    or unescaped paren in the URL would otherwise break the link (nothing renders as clickable)."""
    try:
        return quote(url, safe=_URL_SAFE_CHARS)
    except Exception:
        return url


def _item_line(item: dict) -> str:
    """'- **{type} #{id}** [{title}](url)' with defensive fallbacks for any missing field.

    Note the intentional asymmetry with reply_back (see build_adaptive_card): titles are
    flattened to single-line labels here, while reply_back keeps its newlines as prose.
    """
    item = _as_dict(item)
    wtype = _clean_text(item.get("work_item_type"), "Item")
    wid = item.get("work_item_id")
    wid_str = _clean_text(wid, "?") if wid is not None else "?"
    title = _clean_text(item.get("title"), "(untitled)")
    if len(title) > MAX_TITLE_CHARS:
        title = title[:MAX_TITLE_CHARS] + "…"
    # Escape markdown link brackets in the title so an unbalanced "]" can't break the rest of the
    # line, and so an embedded "[text](some-other-url)" snippet (e.g. copied from a transcript)
    # can never hijack the bullet's clickable target away from the real work-item url.
    title = title.replace("[", "\\[").replace("]", "\\]")
    url = item.get("url")
    url = url.strip() if isinstance(url, str) and url.strip() else None
    if url:
        return f"- **{wtype} #{wid_str}** [{title}]({_quote_url(url)})"
    return f"- **{wtype} #{wid_str}** {title}"


def _breakdown(created: list) -> str:
    """'1 Epic · 2 Features · 1 Bug' - grouped by work_item_type, first-seen order."""
    counts: dict[str, int] = {}
    for item in created:
        wtype = _clean_text(_as_dict(item).get("work_item_type"), "Item")
        counts[wtype] = counts.get(wtype, 0) + 1
    parts = []
    for wtype, n in counts.items():
        label = wtype if n == 1 else _pluralize(wtype)
        parts.append(f"{n} {label}")
    return " · ".join(parts)


def _text_block(text: str, **extra: Any) -> dict:
    block = {"type": "TextBlock", "text": text, "wrap": True}
    block.update(extra)
    return block


def _minimal_card(header_text: str) -> dict:
    return {
        "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
        "type": "AdaptiveCard",
        "version": "1.4",
        "body": [_text_block(header_text, weight="Bolder", size="Medium")],
    }


def _clamp_max_items(value: Any) -> int:
    # bool is an int subclass (isinstance(True, int) is True) - exclude it explicitly so
    # max_items=True doesn't silently get treated as max_items=1.
    if isinstance(value, int) and not isinstance(value, bool) and value >= 0:
        return value
    return DEFAULT_MAX_ITEMS


def _clean_url(url: Any) -> str | None:
    return url.strip() if isinstance(url, str) and url.strip() else None


def _card_bytes(card: dict) -> int:
    try:
        return len(json.dumps(card).encode())
    except Exception:
        return MAX_CARD_BYTES + 1  # force further trimming/fallback rather than silently pass


# ---------- public API ----------
def build_adaptive_card(result: dict, *, meeting_title: str | None = None,
                         board_url: str | None = None, max_items: int = DEFAULT_MAX_ITEMS) -> dict:
    """
    Build a valid Adaptive Card (1.4) JSON dict summarizing a run_spine() result. Never raises -
    on a malformed/empty `result` it still returns a minimal valid card.

    Guarantees the serialized card stays under MAX_CARD_BYTES (Teams' ~28KB card limit) no matter
    how large `result` is: titles and reply_back are pre-truncated, and as a final hard guard the
    body is trimmed (dropping item lines, then reply_back) until it fits - so a single oversized
    title or reply_back can never sink the whole post and lose every created-item's info.
    """
    header_text = _clean_text(meeting_title, "") or _DEFAULT_HEADER
    if len(header_text) > MAX_HEADER_CHARS:
        header_text = header_text[:MAX_HEADER_CHARS] + "…"
    try:
        created, reply_back = _normalize_result(result)
        cap = _clamp_max_items(max_items)
        total = len(created)
        visible = created[:cap]
        count_truncated = total > cap
        extra_count = total - cap if count_truncated else 0

        b_url = _clean_url(board_url)
        b_url_q = _quote_url(b_url) if b_url else None

        header_block = _text_block(header_text, weight="Bolder", size="Medium")
        summary_text = _EMPTY_TEXT if total == 0 else f"{total} work item(s) created: {_breakdown(created)}"
        summary_block = _text_block(summary_text, isSubtle=True)

        item_blocks = [_text_block(_item_line(item)) for item in visible]

        more_block = None
        if count_truncated:
            more = f"+{extra_count} more — see the full board"
            if b_url_q:
                more = f"[{more}]({b_url_q})"
            more_block = _text_block(more, isSubtle=True)

        reply_back_block = None
        if reply_back:
            # reply_back is the LLM's own free-form final message (prose) - unlike titles above
            # (flattened to single-line labels), we deliberately keep its newlines. Known display
            # limitation: if reply_back contains a markdown pipe-table, Adaptive Card TextBlocks
            # can't render tables, so it shows as raw text, not a table. That's acceptable because
            # the structured item list built above is the reliable representation of what was
            # created; reply_back is just supplementary color.
            rb = reply_back
            if len(rb) > MAX_REPLY_BACK_CHARS:
                rb = rb[:MAX_REPLY_BACK_CHARS] + "… (truncated — see the full board)"
            reply_back_block = _text_block(rb)

        def assemble() -> dict:
            body = [header_block, summary_block, *item_blocks]
            if more_block:
                body.append(more_block)
            if reply_back_block:
                body.append(reply_back_block)
            c: dict[str, Any] = {
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "type": "AdaptiveCard",
                "version": "1.4",
                "body": body,
            }
            if b_url_q:
                c["actions"] = [{"type": "Action.OpenUrl", "title": "Open board", "url": b_url_q}]
            return c

        card = assemble()

        # (c) Final hard guard: drop trailing item lines, then reply_back, until it fits - this is
        # what actually guarantees the cap regardless of what (a)/(b) or a huge max_items let through.
        trimmed = False
        while _card_bytes(card) > MAX_CARD_BYTES and (item_blocks or reply_back_block is not None):
            trimmed = True
            if item_blocks:
                item_blocks.pop()
            else:
                reply_back_block = None
            card = assemble()

        if trimmed:
            note_text = "(some items trimmed — see the full board)"
            if b_url_q:
                note_text = f"[{note_text}]({b_url_q})"
            card["body"].append(_text_block(note_text, isSubtle=True))

            if _card_bytes(card) > MAX_CARD_BYTES:
                # Pathological case (e.g. a huge breakdown/summary line) - collapse to the bare
                # minimum. The created-item counts (summary_block) are always kept: that's the
                # one piece of info this function must never lose.
                card["body"] = [header_block, summary_block,
                                 _text_block("(details omitted — see the full board)", isSubtle=True)]
                if _card_bytes(card) > MAX_CARD_BYTES:
                    # Still too big only if header/summary themselves are enormous - shrink those too.
                    card["body"] = [
                        _text_block(header_text[:200], weight="Bolder", size="Medium"),
                        _text_block(summary_text[:200], isSubtle=True),
                    ]

        return card
    except Exception:
        return _minimal_card(header_text)


class WebhookSender:
    """POSTs an Adaptive Card to a Teams Incoming Webhook / Power Automate Workflows URL.

    Mirrors agent.spine.Backend: an injectable `opener` (default urllib.request.urlopen), a
    retry/backoff loop for transient failures, and a `send()` that never raises.
    """

    def __init__(self, url: str | None, opener: Callable | None = None, attempts: int = 4):
        self.url = url
        self._opener = opener or urllib.request.urlopen
        self._attempts = attempts

    def send(self, card: dict) -> dict:
        if not isinstance(self.url, str) or not self.url.strip():
            return {"sent": False, "status": None, "error": "no webhook url configured"}

        envelope = {
            "type": "message",
            "attachments": [{
                "contentType": "application/vnd.microsoft.card.adaptive",
                "content": card,
            }],
        }
        try:
            data = json.dumps(envelope).encode()
        except Exception as e:
            return {"sent": False, "status": None, "error": f"could not encode card: {e}"}

        attempts = (self._attempts if isinstance(self._attempts, int)
                    and not isinstance(self._attempts, bool) and self._attempts >= 1 else 0)
        for attempt in range(attempts):
            req = urllib.request.Request(
                self.url, data=data, method="POST",
                headers={"Content-Type": "application/json"})
            try:
                with self._opener(req, timeout=15) as r:
                    status = getattr(r, "status", None)
                    if status is None:
                        status = getattr(r, "code", 200)
                    try:
                        r.read()
                    except Exception:
                        pass
                    if 200 <= status < 300:
                        return {"sent": True, "status": status, "error": None}
                    # Most openers raise HTTPError for >=400 instead of returning here, but a
                    # fake/test opener may hand back a non-2xx (or even non-3xx-successful, e.g.
                    # a bare 300) response object directly - treat anything outside 2xx as not
                    # sent, same as the HTTPError path below.
                    if status >= 500 and attempt < attempts - 1:
                        time.sleep(1.5 * (attempt + 1))
                        continue
                    if status == 429 and attempt < attempts - 1:
                        # Direct-response 429 (no exception raised) must honor Retry-After
                        # identically to the HTTPError branch below.
                        time.sleep(self._retry_after_wait(r, attempt))
                        continue
                    return {"sent": False, "status": status, "error": f"HTTP {status}"}
            except urllib.error.HTTPError as e:
                if e.code == 429 and attempt < attempts - 1:
                    time.sleep(self._retry_after_wait(e, attempt))
                    continue
                if e.code >= 500 and attempt < attempts - 1:
                    time.sleep(1.5 * (attempt + 1))
                    continue
                try:
                    body = e.read().decode()[:300]
                except Exception:
                    body = ""
                return {"sent": False, "status": e.code, "error": f"HTTP {e.code}: {body}"}
            except urllib.error.URLError as e:
                if attempt < attempts - 1:
                    time.sleep(1.5 * (attempt + 1))
                    continue
                return {"sent": False, "status": None,
                        "error": f"connection error after {attempts} tries: {e}"}
            except Exception as e:
                # Belt-and-braces: an unexpected opener failure must never propagate out of send().
                if attempt < attempts - 1:
                    time.sleep(1.5 * (attempt + 1))
                    continue
                return {"sent": False, "status": None, "error": f"unexpected error: {e}"}
        return {"sent": False, "status": None, "error": "no request attempts made (attempts must be >= 1)"}

    @staticmethod
    def _retry_after_wait(headers_source: Any, attempt: int) -> float:
        """Compute the 429 backoff, honoring Retry-After (capped at 30s) if present.

        `headers_source` may be an HTTPError (has `.headers`), a plain response object handed
        back directly by an injected opener (also expected to expose `.headers`), or a raw
        headers mapping - so both the "opener raised HTTPError" and the "opener returned a 429
        response object" paths honor Retry-After identically.
        """
        if hasattr(headers_source, "headers"):
            headers = getattr(headers_source, "headers", None)
        else:
            headers = headers_source  # already a headers mapping
        retry_after = None
        try:
            if headers is not None:
                retry_after = headers.get("Retry-After")
        except Exception:
            retry_after = None
        if retry_after:
            try:
                return min(float(retry_after), 30.0)
            except (TypeError, ValueError):
                pass
        return 1.5 * (attempt + 1)


def post_reply_back(result: dict, sender: "WebhookSender", *, meeting_title: str | None = None,
                     board_url: str | None = None, max_items: int = DEFAULT_MAX_ITEMS) -> dict:
    """Build the Adaptive Card for `result` and send it via `sender`. Never raises."""
    cap = _clamp_max_items(max_items)
    try:
        created, _ = _normalize_result(result)
    except Exception:
        created = []
    item_count = len(created)
    truncated = item_count > cap

    try:
        card = build_adaptive_card(result, meeting_title=meeting_title, board_url=board_url, max_items=cap)
    except Exception:
        card = _minimal_card(_clean_text(meeting_title, "") or _DEFAULT_HEADER)

    try:
        send_result = sender.send(card)
        if not isinstance(send_result, dict):
            send_result = {"sent": False, "status": None, "error": "sender returned a non-dict result"}
    except Exception as e:
        send_result = {"sent": False, "status": None, "error": f"sender raised: {e}"}

    return {
        "sent": bool(send_result.get("sent")),
        "status": send_result.get("status"),
        "error": send_result.get("error"),
        "item_count": item_count,
        "truncated": truncated,
    }
