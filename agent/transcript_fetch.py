"""
Preppie transcript fetch: authenticate to Microsoft Graph as an Entra app (client-credentials /
app-only) and download a Teams meeting's transcript as WEBVTT text, which then feeds the existing
parse_vtt -> run_spine pipeline (see agent.spine).

Mirrors the defensive, injectable-opener + retry/backoff discipline of agent.spine.Backend and
agent.reply_back.WebhookSender: the HTTP transport is injectable (an `opener` callable, default
urllib.request.urlopen) so the whole module is unit-testable offline with zero network and zero
credentials - see tests/test_transcript_fetch.py.
"""
from __future__ import annotations

import email.utils
import json
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from typing import Any, Callable

GRAPH_BASE_URL = "https://graph.microsoft.com/v1.0"
TOKEN_URL_TEMPLATE = "https://login.microsoftonline.com/{tenant_id}/oauth2/v2.0/token"


class GraphError(Exception):
    """Raised for any unrecoverable Microsoft Graph / token-endpoint failure.

    `status` is the originating HTTP status code when known (e.g. 403), else None - callers can
    use it to append actionable, status-specific hints without re-parsing the message string.
    """

    def __init__(self, message: str, *, status: int | None = None):
        super().__init__(message)
        self.status = status


_ACCESS_POLICY_HINT = (
    "check the Teams application access policy (an admin must grant this app access via "
    "New-CsApplicationAccessPolicy / Grant-CsApplicationAccessPolicy) and that "
    "OnlineMeetingTranscript.Read.All has admin consent")


def _quote_seg(value: str) -> str:
    """URL-encode a single path segment (user id / meeting id / transcript id)."""
    return urllib.parse.quote(str(value), safe="")


# ---------- app-only token ----------
def get_app_token(tenant_id: str, client_id: str, client_secret: str, *,
                   opener: Callable | None = None,
                   scope: str = "https://graph.microsoft.com/.default",
                   attempts: int = 4) -> str:
    """POST a client-credentials grant to the v2.0 token endpoint. Returns the access_token.

    Raises GraphError with the Graph/AAD error_description on any failure (bad creds, network
    error, malformed response). Retries transient failures (5xx from the token endpoint, or a
    URLError/connection problem) with the same bounded backoff as GraphClient.get.
    """
    opener = opener or urllib.request.urlopen
    attempts = (attempts if isinstance(attempts, int)
                and not isinstance(attempts, bool) and attempts >= 1 else 1)
    url = TOKEN_URL_TEMPLATE.format(tenant_id=tenant_id)
    body = urllib.parse.urlencode({
        "grant_type": "client_credentials",
        "client_id": client_id,
        "client_secret": client_secret,
        "scope": scope,
    }).encode()

    for attempt in range(attempts):
        req = urllib.request.Request(
            url, data=body, method="POST",
            headers={"Content-Type": "application/x-www-form-urlencoded"})
        try:
            with opener(req, timeout=30) as r:
                raw = r.read()
        except urllib.error.HTTPError as e:
            if e.code >= 500 and attempt < attempts - 1:
                time.sleep(1.5 * (attempt + 1))
                continue
            try:
                err_body = json.loads(e.read().decode())
            except Exception:
                err_body = {}
            msg = err_body.get("error_description") or err_body.get("error") or f"HTTP {e.code}"
            raise GraphError(f"token request failed: {msg}") from e
        except urllib.error.URLError as e:
            if attempt < attempts - 1:
                time.sleep(1.5 * (attempt + 1))
                continue
            raise GraphError(f"token request failed: connection error: {e}") from e
        else:
            break
    else:
        raise GraphError("token request failed: no request attempts made (attempts must be >= 1)")

    try:
        payload = json.loads(raw.decode())
    except Exception as e:
        raise GraphError(f"token request failed: could not parse response: {e}") from e

    token = payload.get("access_token")
    if not token:
        msg = payload.get("error_description") or payload.get("error") or "no access_token in response"
        raise GraphError(f"token request failed: {msg}")
    return token


# ---------- Graph HTTP client ----------
class GraphClient:
    """GETs Microsoft Graph URLs with a bearer token.

    Mirrors agent.spine.Backend / agent.reply_back.WebhookSender: an injectable `opener` (default
    urllib.request.urlopen) and a retry/backoff loop for transient failures (5xx, URLError, and
    429 honoring Retry-After capped at 30s). Unlike those, `get()` DOES raise GraphError on
    failure - callers of this module (find_meeting_id, list_transcripts, download_transcript_vtt,
    fetch_meeting_vtt) want a hard stop with a clear message rather than a soft {"success": False}.
    """

    def __init__(self, token: str, *, opener: Callable | None = None, attempts: int = 4):
        self.token = token
        self._opener = opener or urllib.request.urlopen
        self._attempts = (attempts if isinstance(attempts, int)
                           and not isinstance(attempts, bool) and attempts >= 1 else 1)

    def get(self, path_or_url: str, *, accept: str = "application/json") -> tuple[int, bytes]:
        url = (path_or_url if path_or_url.startswith("http://") or path_or_url.startswith("https://")
               else f"{GRAPH_BASE_URL}{path_or_url}")
        headers = {"Authorization": f"Bearer {self.token}", "Accept": accept}

        for attempt in range(self._attempts):
            req = urllib.request.Request(url, method="GET", headers=headers)
            try:
                with self._opener(req, timeout=45) as r:
                    status = getattr(r, "status", None)
                    if status is None:
                        status = getattr(r, "code", 200)
                    resp_body = r.read()
                    if 200 <= status < 300:
                        return status, resp_body
                    # A fake/test opener (or an unusual real one) may hand back a non-2xx
                    # response directly instead of raising HTTPError - handle both paths
                    # identically, same as WebhookSender.
                    if status == 429 and attempt < self._attempts - 1:
                        time.sleep(self._retry_after_wait(r, attempt))
                        continue
                    if status >= 500 and attempt < self._attempts - 1:
                        time.sleep(1.5 * (attempt + 1))
                        continue
                    raise GraphError(self._graph_error_message(status, resp_body), status=status)
            except urllib.error.HTTPError as e:
                if e.code == 429 and attempt < self._attempts - 1:
                    time.sleep(self._retry_after_wait(e, attempt))
                    continue
                if e.code >= 500 and attempt < self._attempts - 1:
                    time.sleep(1.5 * (attempt + 1))
                    continue
                try:
                    err_body = e.read()
                except Exception:
                    err_body = b""
                raise GraphError(self._graph_error_message(e.code, err_body), status=e.code) from e
            except urllib.error.URLError as e:
                if attempt < self._attempts - 1:
                    time.sleep(1.5 * (attempt + 1))
                    continue
                raise GraphError(f"connection error after {self._attempts} tries: {e}") from e
        raise GraphError("no request attempts made (attempts must be >= 1)")

    @staticmethod
    def _graph_error_message(status: int, body: Any) -> str:
        """Surface Graph's {"error": {"code", "message"}} shape if present, else a snippet."""
        try:
            raw = body.decode() if isinstance(body, (bytes, bytearray)) else str(body)
        except Exception:
            raw = ""
        try:
            parsed = json.loads(raw)
            err = parsed.get("error")
            if isinstance(err, dict) and (err.get("code") or err.get("message")):
                return f"Graph API error (HTTP {status}): {err.get('code', '')} {err.get('message', '')}".strip()
        except Exception:
            pass
        return f"HTTP {status}: {raw[:300]}"

    @staticmethod
    def _retry_after_wait(headers_source: Any, attempt: int) -> float:
        """Compute the 429 backoff, honoring Retry-After (capped at 30s) if present.

        `headers_source` may be an HTTPError (has `.headers`), a plain response object handed
        back directly by an injected opener (also expected to expose `.headers`), or a raw
        headers mapping.
        """
        if hasattr(headers_source, "headers"):
            headers = getattr(headers_source, "headers", None)
        else:
            headers = headers_source
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
            try:
                dt = email.utils.parsedate_to_datetime(retry_after)
                if dt is not None:
                    if dt.tzinfo is None:
                        dt = dt.replace(tzinfo=timezone.utc)
                    delta = (dt - datetime.now(timezone.utc)).total_seconds()
                    if delta > 0:
                        return min(delta, 30.0)
            except (TypeError, ValueError, OverflowError):
                pass
        return 1.5 * (attempt + 1)


def _parse_graph_object(body: bytes, what: str) -> dict:
    """Decode a Graph JSON response and require it to be a top-level object (not e.g. a bare
    array), so callers can safely call .get(...) on it. Raises GraphError, never
    AttributeError/KeyError, on any malformed-but-valid-JSON shape."""
    try:
        payload = json.loads(body.decode())
    except Exception as e:
        raise GraphError(f"could not parse {what} response: {e}") from e
    if not isinstance(payload, dict):
        raise GraphError(
            f"unexpected {what} response shape: expected a JSON object, got "
            f"{type(payload).__name__}")
    return payload


def _require_id(item: Any, what: str) -> str:
    """Pull `id` out of a Graph list item, raising GraphError (not KeyError) if it's missing or
    the item itself isn't an object."""
    if not isinstance(item, dict) or not item.get("id"):
        raise GraphError(f"{what} is missing an 'id' field in the Graph response: {item!r}")
    return item["id"]


def _append_403_hint(err: GraphError) -> GraphError:
    """If `err` came from an HTTP 403, append the actionable access-policy/consent hint (the
    same one find_meeting_id gives on a not-found, which is often *also* a 403-in-disguise)."""
    if getattr(err, "status", None) == 403:
        return GraphError(f"{err} - {_ACCESS_POLICY_HINT}", status=403)
    return err


# ---------- Graph lookups ----------
def find_meeting_id(client: GraphClient, organizer_user_id: str, join_web_url: str) -> str:
    """Resolve an onlineMeeting id from its join URL. Raises GraphError if none is found."""
    safe_join_web_url = join_web_url.replace("'", "''")
    filter_value = f"JoinWebUrl eq '{safe_join_web_url}'"
    query = urllib.parse.urlencode({"$filter": filter_value})
    path = f"/users/{_quote_seg(organizer_user_id)}/onlineMeetings?{query}"
    try:
        _, body = client.get(path)
    except GraphError as e:
        raise _append_403_hint(e) from e
    payload = _parse_graph_object(body, "onlineMeetings")

    meetings = payload.get("value") or []
    if not meetings:
        raise GraphError(
            f"meeting not found for join URL {join_web_url!r} - either the URL/organizer is "
            f"wrong, or the app is missing the application access policy that grants it access "
            f"to this organizer's online meetings; {_ACCESS_POLICY_HINT}")
    return _require_id(meetings[0], "the first onlineMeetings result")


def list_transcripts(client: GraphClient, organizer_user_id: str, meeting_id: str) -> list[dict]:
    """List transcripts for a meeting. An empty list is valid - transcription may still lag."""
    path = f"/users/{_quote_seg(organizer_user_id)}/onlineMeetings/{_quote_seg(meeting_id)}/transcripts"
    try:
        _, body = client.get(path)
    except GraphError as e:
        raise _append_403_hint(e) from e
    payload = _parse_graph_object(body, "transcripts")
    return payload.get("value") or []


def download_transcript_vtt(client: GraphClient, organizer_user_id: str, meeting_id: str,
                             transcript_id: str) -> str:
    """Download one transcript's content as WEBVTT text."""
    path = (f"/users/{_quote_seg(organizer_user_id)}/onlineMeetings/{_quote_seg(meeting_id)}"
            f"/transcripts/{_quote_seg(transcript_id)}/content?$format=text/vtt")
    try:
        _, body = client.get(path, accept="text/vtt")
    except GraphError as e:
        raise _append_403_hint(e) from e
    try:
        return body.decode("utf-8")
    except Exception as e:
        raise GraphError(f"could not decode transcript content as utf-8: {e}") from e


# ---------- orchestration ----------
def fetch_meeting_vtt(tenant_id: str, client_id: str, client_secret: str, organizer_user_id: str,
                       join_web_url: str, *, opener: Callable | None = None,
                       poll_attempts: int = 1, poll_seconds: float = 15) -> str:
    """
    Token -> GraphClient -> find_meeting_id -> list_transcripts -> pick the LATEST by
    createdDateTime -> download its VTT.

    If no transcripts are available yet and poll_attempts > 1, polls list_transcripts up to
    poll_attempts times, sleeping poll_seconds between tries (transcription often lags a minute
    or two after the meeting ends). Raises GraphError if still no transcript after polling.

    Pre-flight: raises GraphError immediately (making NO network call at all) if any of the
    required Graph credentials/config is empty, instead of firing a doomed request to e.g.
    https://login.microsoftonline.com//oauth2/... with an empty tenant_id.
    """
    missing = [name for name, value in (
        ("tenant_id", tenant_id), ("client_id", client_id),
        ("client_secret", client_secret), ("organizer_user_id", organizer_user_id),
    ) if not value]
    if missing:
        raise GraphError(
            "Graph credentials not configured - set PREPPIE_GRAPH_TENANT_ID / "
            "PREPPIE_GRAPH_CLIENT_ID / PREPPIE_GRAPH_CLIENT_SECRET / "
            f"PREPPIE_GRAPH_ORGANIZER_USER_ID (missing: {', '.join(missing)})")

    token = get_app_token(tenant_id, client_id, client_secret, opener=opener)
    client = GraphClient(token, opener=opener)
    meeting_id = find_meeting_id(client, organizer_user_id, join_web_url)

    attempts = (poll_attempts if isinstance(poll_attempts, int)
                and not isinstance(poll_attempts, bool) and poll_attempts >= 1 else 1)

    transcripts: list[dict] = []
    for attempt in range(attempts):
        transcripts = list_transcripts(client, organizer_user_id, meeting_id)
        if transcripts:
            break
        if attempt < attempts - 1:
            time.sleep(poll_seconds)

    if not transcripts:
        raise GraphError(
            f"no transcript available yet for meeting {meeting_id!r} after {attempts} attempt(s) "
            "- transcription may still be processing; try again in a minute or two")

    latest = max(transcripts, key=lambda t: (t.get("createdDateTime") or "") if isinstance(t, dict) else "")
    latest_id = _require_id(latest, "the latest transcript")
    return download_transcript_vtt(client, organizer_user_id, meeting_id, latest_id)
