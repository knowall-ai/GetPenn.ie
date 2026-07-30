"""Unit tests for agent.transcript_fetch. No network: the HTTP opener is injected everywhere,
and agent.transcript_fetch.time.sleep is monkeypatched so polling/backoff tests run instantly."""
import email.utils
import io
import json
import time
import urllib.error
import urllib.parse

import pytest

from agent.transcript_fetch import (
    GraphClient,
    GraphError,
    download_transcript_vtt,
    fetch_meeting_vtt,
    find_meeting_id,
    get_app_token,
    list_transcripts,
)


# =========================================================================
# shared fakes (mirror agent.tests.test_spine._FakeResp / test_reply_back._FakeResp)
# =========================================================================
class _FakeResp:
    def __init__(self, body=b"", status=200, headers=None):
        self._b = body if isinstance(body, bytes) else json.dumps(body).encode()
        self.status = status
        self.code = status
        self.headers = headers or {}

    def read(self):
        return self._b

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


def _http_error(code, body=b"", headers=None):
    if not isinstance(body, bytes):
        body = json.dumps(body).encode()
    return urllib.error.HTTPError(
        url="https://graph.microsoft.com/v1.0/x", code=code, msg=f"HTTP {code}",
        hdrs=headers or {}, fp=None if body == b"" else io.BytesIO(body))


class _ScriptedOpener:
    """Routes requests to canned responses by URL substring, tried in the given order. Each
    route's response list is popped left-to-right, repeating its last entry once exhausted -
    so a route can script "fail twice then succeed" or just "always return this"."""

    def __init__(self, routes):
        self.routes = [(substr, list(resps)) for substr, resps in routes]
        self.calls = []

    def __call__(self, req, timeout=None):
        url = req.full_url
        self.calls.append(req)
        for substr, queue in self.routes:
            if substr in url:
                item = queue.pop(0) if len(queue) > 1 else queue[0]
                if isinstance(item, BaseException):
                    raise item
                return item
        raise AssertionError(f"unrouted request: {url}")


TOKEN_ROUTE = "login.microsoftonline.com"


def _token_resp(token="tok-123"):
    return _FakeResp({"access_token": token, "token_type": "Bearer", "expires_in": 3600})


# =========================================================================
# get_app_token
# =========================================================================
def test_get_app_token_success_posts_client_credentials_form():
    seen = {}

    def opener(req, timeout=None):
        seen["url"] = req.full_url
        seen["method"] = req.get_method()
        seen["content_type"] = req.get_header("Content-type")
        seen["body"] = urllib.parse.parse_qs(req.data.decode())
        return _token_resp("abc123")

    token = get_app_token("ten1", "client1", "secret1", opener=opener)

    assert token == "abc123"
    assert seen["url"] == "https://login.microsoftonline.com/ten1/oauth2/v2.0/token"
    assert seen["method"] == "POST"
    assert seen["content_type"] == "application/x-www-form-urlencoded"
    assert seen["body"] == {
        "grant_type": ["client_credentials"],
        "client_id": ["client1"],
        "client_secret": ["secret1"],
        "scope": ["https://graph.microsoft.com/.default"],
    }


def test_get_app_token_custom_scope_is_sent():
    seen = {}

    def opener(req, timeout=None):
        seen["body"] = urllib.parse.parse_qs(req.data.decode())
        return _token_resp()

    get_app_token("t", "c", "s", opener=opener, scope="custom/.default")
    assert seen["body"]["scope"] == ["custom/.default"]


def test_get_app_token_http_error_surfaces_error_description():
    def opener(req, timeout=None):
        raise _http_error(401, body={"error": "invalid_client",
                                      "error_description": "AADSTS7000215: Invalid client secret"})

    with pytest.raises(GraphError) as exc:
        get_app_token("t", "c", "wrong-secret", opener=opener)
    assert "AADSTS7000215" in str(exc.value)


def test_get_app_token_http_error_without_json_body_still_raises_clean_error():
    def opener(req, timeout=None):
        raise _http_error(400, body=b"not json")

    with pytest.raises(GraphError) as exc:
        get_app_token("t", "c", "s", opener=opener)
    assert "HTTP 400" in str(exc.value)


def test_get_app_token_urlerror_raises_graph_error(monkeypatch):
    # get_app_token now retries URLError like GraphClient.get does (finding #3) - patch sleep so
    # this exhausts its retries instantly instead of waiting on real backoff.
    monkeypatch.setattr("agent.transcript_fetch.time.sleep", lambda *_: None)

    def opener(req, timeout=None):
        raise urllib.error.URLError("connection refused")

    with pytest.raises(GraphError) as exc:
        get_app_token("t", "c", "s", opener=opener)
    assert "connection error" in str(exc.value)


def test_get_app_token_missing_access_token_raises_graph_error():
    def opener(req, timeout=None):
        return _FakeResp({"error": "unauthorized_client", "error_description": "app not authorized"})

    with pytest.raises(GraphError) as exc:
        get_app_token("t", "c", "s", opener=opener)
    assert "app not authorized" in str(exc.value)


def test_get_app_token_retries_5xx_then_succeeds(monkeypatch):
    sleeps = []
    monkeypatch.setattr("agent.transcript_fetch.time.sleep", lambda s: sleeps.append(s))
    calls = {"n": 0}

    def opener(req, timeout=None):
        calls["n"] += 1
        if calls["n"] < 3:
            raise _http_error(503)
        return _token_resp("retried-token")

    token = get_app_token("t", "c", "s", opener=opener, attempts=4)

    assert token == "retried-token"
    assert calls["n"] == 3
    assert len(sleeps) == 2


# =========================================================================
# GraphClient.get
# =========================================================================
def test_graphclient_get_success_returns_status_and_body_and_sets_auth_header():
    seen = {}

    def opener(req, timeout=None):
        seen["url"] = req.full_url
        seen["auth"] = req.get_header("Authorization")
        seen["accept"] = req.get_header("Accept")
        return _FakeResp({"value": []}, status=200)

    status, body = GraphClient("mytoken", opener=opener).get("/users/u1/onlineMeetings")

    assert status == 200
    assert json.loads(body.decode()) == {"value": []}
    assert seen["url"] == "https://graph.microsoft.com/v1.0/users/u1/onlineMeetings"
    assert seen["auth"] == "Bearer mytoken"
    assert seen["accept"] == "application/json"


def test_graphclient_get_absolute_url_used_as_is():
    seen = {}

    def opener(req, timeout=None):
        seen["url"] = req.full_url
        return _FakeResp({"ok": True})

    GraphClient("t", opener=opener).get("https://graph.microsoft.com/v1.0/other/path")
    assert seen["url"] == "https://graph.microsoft.com/v1.0/other/path"


def test_graphclient_get_custom_accept_header():
    seen = {}

    def opener(req, timeout=None):
        seen["accept"] = req.get_header("Accept")
        return _FakeResp(b"WEBVTT\n")

    GraphClient("t", opener=opener).get("/x", accept="text/vtt")
    assert seen["accept"] == "text/vtt"


def test_graphclient_get_retries_5xx_then_succeeds(monkeypatch):
    sleeps = []
    monkeypatch.setattr("agent.transcript_fetch.time.sleep", lambda s: sleeps.append(s))
    calls = {"n": 0}

    def opener(req, timeout=None):
        calls["n"] += 1
        if calls["n"] < 3:
            raise _http_error(503)
        return _FakeResp({"ok": True}, status=200)

    status, body = GraphClient("t", opener=opener, attempts=4).get("/x")
    assert status == 200 and calls["n"] == 3 and len(sleeps) == 2


def test_graphclient_get_retries_urlerror_then_gives_up():
    def always_refuse(req, timeout=None):
        raise urllib.error.URLError("refused")

    with pytest.raises(GraphError) as exc:
        GraphClient("t", opener=always_refuse, attempts=3).get("/x")
    assert "connection error after 3 tries" in str(exc.value)


def test_graphclient_get_429_retry_after_honored_capped_at_30(monkeypatch):
    sleeps = []
    monkeypatch.setattr("agent.transcript_fetch.time.sleep", lambda s: sleeps.append(s))
    calls = {"n": 0}

    def opener(req, timeout=None):
        calls["n"] += 1
        if calls["n"] == 1:
            raise _http_error(429, headers={"Retry-After": "120"})
        return _FakeResp({"ok": True})

    status, _ = GraphClient("t", opener=opener, attempts=3).get("/x")
    assert status == 200
    assert sleeps == [30.0]


def test_graphclient_get_429_retry_after_below_cap_honored_exactly(monkeypatch):
    sleeps = []
    monkeypatch.setattr("agent.transcript_fetch.time.sleep", lambda s: sleeps.append(s))
    calls = {"n": 0}

    def opener(req, timeout=None):
        calls["n"] += 1
        if calls["n"] == 1:
            raise _http_error(429, headers={"Retry-After": "5"})
        return _FakeResp({"ok": True})

    GraphClient("t", opener=opener, attempts=3).get("/x")
    assert sleeps == [5.0]


def test_graphclient_get_429_retry_after_http_date_is_honored(monkeypatch):
    sleeps = []
    monkeypatch.setattr("agent.transcript_fetch.time.sleep", lambda s: sleeps.append(s))
    calls = {"n": 0}
    # ~10s in the future, formatted as an HTTP-date (RFC 1123) - the format Retry-After may use
    # instead of a plain integer-seconds value.
    future_date = email.utils.formatdate(time.time() + 10, usegmt=True)

    def opener(req, timeout=None):
        calls["n"] += 1
        if calls["n"] == 1:
            raise _http_error(429, headers={"Retry-After": future_date})
        return _FakeResp({"ok": True})

    status, _ = GraphClient("t", opener=opener, attempts=3).get("/x")

    assert status == 200
    assert len(sleeps) == 1
    # Loosely pinned to avoid clock flakiness: well above the plain 1.5s default backoff, and
    # under the 30s cap.
    assert 5 < sleeps[0] <= 30.0


def test_graphclient_get_429_retry_after_unparseable_falls_back_to_default_backoff(monkeypatch):
    sleeps = []
    monkeypatch.setattr("agent.transcript_fetch.time.sleep", lambda s: sleeps.append(s))
    calls = {"n": 0}

    def opener(req, timeout=None):
        calls["n"] += 1
        if calls["n"] == 1:
            raise _http_error(429, headers={"Retry-After": "not-a-number-or-date"})
        return _FakeResp({"ok": True})

    GraphClient("t", opener=opener, attempts=3).get("/x")
    assert sleeps == [1.5]  # attempt 0 default backoff: 1.5 * (0 + 1)


def test_graphclient_get_non_2xx_with_graph_error_body_surfaces_message():
    def opener(req, timeout=None):
        raise _http_error(403, body={"error": {"code": "Forbidden",
                                                "message": "app lacks OnlineMeetings.Read.All"}})

    with pytest.raises(GraphError) as exc:
        GraphClient("t", opener=opener, attempts=2).get("/x")
    assert "Forbidden" in str(exc.value) and "OnlineMeetings.Read.All" in str(exc.value)


def test_graphclient_get_direct_non_2xx_response_object_is_handled_without_exception(monkeypatch):
    # Some (test/custom) openers hand back a non-2xx response object directly instead of raising
    # HTTPError - must be treated the same way, including retrying a direct 500 and a direct 429.
    monkeypatch.setattr("agent.transcript_fetch.time.sleep", lambda *_: None)

    def opener(req, timeout=None):
        return _FakeResp({"error": {"code": "X", "message": "boom"}}, status=500)

    with pytest.raises(GraphError) as exc:
        GraphClient("t", opener=opener, attempts=2).get("/x")
    assert "X" in str(exc.value) and "boom" in str(exc.value)


def test_graphclient_get_gives_up_after_attempts_exhausted_on_5xx(monkeypatch):
    monkeypatch.setattr("agent.transcript_fetch.time.sleep", lambda *_: None)

    def always_503(req, timeout=None):
        raise _http_error(503, body=b"upstream down")

    with pytest.raises(GraphError) as exc:
        GraphClient("t", opener=always_503, attempts=3).get("/x")
    assert "503" in str(exc.value)


# =========================================================================
# find_meeting_id
# =========================================================================
def test_find_meeting_id_success_returns_first_meeting_id():
    def opener(req, timeout=None):
        return _FakeResp({"value": [{"id": "meeting-1"}, {"id": "meeting-2"}]})

    client = GraphClient("t", opener=opener)
    assert find_meeting_id(client, "organizer1", "https://teams.microsoft.com/l/x") == "meeting-1"


def test_find_meeting_id_not_found_raises_graph_error():
    def opener(req, timeout=None):
        return _FakeResp({"value": []})

    client = GraphClient("t", opener=opener)
    with pytest.raises(GraphError) as exc:
        find_meeting_id(client, "organizer1", "https://teams.microsoft.com/l/missing")
    assert "meeting not found" in str(exc.value)
    assert "access policy" in str(exc.value)


def test_find_meeting_id_non_object_top_level_json_raises_graph_error_not_attributeerror():
    def opener(req, timeout=None):
        return _FakeResp(b"[]")  # a bare JSON array, not an object with .get()

    client = GraphClient("t", opener=opener)
    with pytest.raises(GraphError) as exc:
        find_meeting_id(client, "organizer1", "https://teams.microsoft.com/l/x")
    assert "list" in str(exc.value) or "onlineMeetings" in str(exc.value)


def test_find_meeting_id_value_item_missing_id_raises_graph_error_not_keyerror():
    def opener(req, timeout=None):
        return _FakeResp({"value": [{"subject": "no id here"}]})

    client = GraphClient("t", opener=opener)
    with pytest.raises(GraphError) as exc:
        find_meeting_id(client, "organizer1", "https://teams.microsoft.com/l/x")
    assert "id" in str(exc.value)


def test_find_meeting_id_uses_correct_path_and_url_encodes_organizer():
    seen = {}

    def opener(req, timeout=None):
        seen["url"] = req.full_url
        return _FakeResp({"value": [{"id": "m1"}]})

    client = GraphClient("t", opener=opener)
    find_meeting_id(client, "user@contoso.com", "https://teams.microsoft.com/l/x")
    assert seen["url"].startswith(
        "https://graph.microsoft.com/v1.0/users/user%40contoso.com/onlineMeetings?")


def test_find_meeting_id_url_encodes_filter_with_special_chars():
    # Note: this URL contains a literal apostrophe (in "a b's"), which OData V4 requires be
    # escaped by doubling it ('' ) inside the filter's string literal - see
    # test_find_meeting_id_escapes_odata_single_quote for that behavior specifically. Here we
    # just check the round-trip of the (already-escaped) filter value through percent-encoding.
    join_url = "https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc?tenantId=t1&x=a b's \"q\""
    seen = {}

    def opener(req, timeout=None):
        seen["url"] = req.full_url
        return _FakeResp({"value": [{"id": "m1"}]})

    client = GraphClient("t", opener=opener)
    find_meeting_id(client, "organizer1", join_url)

    parsed = urllib.parse.urlparse(seen["url"])
    qs = urllib.parse.parse_qs(parsed.query)
    expected_odata_value = join_url.replace("'", "''")
    assert qs["$filter"] == [f"JoinWebUrl eq '{expected_odata_value}'"]


def test_find_meeting_id_escapes_odata_single_quote():
    """A join URL containing a literal apostrophe must have it doubled ('') in the OData
    $filter value per OData V4 string-literal escaping - otherwise Graph returns 400."""
    join_url = "https://teams.microsoft.com/l/meetup-join/19:meeting_o'brien@thread.v2/0"
    seen = {}

    def opener(req, timeout=None):
        seen["url"] = req.full_url
        return _FakeResp({"value": [{"id": "m1"}]})

    client = GraphClient("t", opener=opener)
    find_meeting_id(client, "organizer1", join_url)

    parsed = urllib.parse.urlparse(seen["url"])
    qs = urllib.parse.parse_qs(parsed.query)
    assert qs["$filter"] == ["JoinWebUrl eq 'https://teams.microsoft.com/l/meetup-join/19:meeting_o''brien@thread.v2/0'"]


# =========================================================================
# list_transcripts
# =========================================================================
def test_list_transcripts_empty_is_valid():
    def opener(req, timeout=None):
        return _FakeResp({"value": []})

    client = GraphClient("t", opener=opener)
    assert list_transcripts(client, "org1", "meeting1") == []


def test_list_transcripts_returns_value_list_and_correct_path():
    seen = {}

    def opener(req, timeout=None):
        seen["url"] = req.full_url
        return _FakeResp({"value": [{"id": "tr1", "createdDateTime": "2026-07-01T00:00:00Z"}]})

    client = GraphClient("t", opener=opener)
    out = list_transcripts(client, "org1", "meeting1")

    assert out == [{"id": "tr1", "createdDateTime": "2026-07-01T00:00:00Z"}]
    assert seen["url"] == "https://graph.microsoft.com/v1.0/users/org1/onlineMeetings/meeting1/transcripts"


def test_list_transcripts_non_object_top_level_json_raises_graph_error_not_attributeerror():
    def opener(req, timeout=None):
        return _FakeResp(b"[]")

    client = GraphClient("t", opener=opener)
    with pytest.raises(GraphError):
        list_transcripts(client, "org1", "meeting1")


def test_list_transcripts_403_gets_actionable_access_policy_hint():
    def opener(req, timeout=None):
        raise _http_error(403, body={"error": {"code": "Forbidden", "message": "denied"}})

    client = GraphClient("t", opener=opener)
    with pytest.raises(GraphError) as exc:
        list_transcripts(client, "org1", "meeting1")
    assert "denied" in str(exc.value)
    assert "access policy" in str(exc.value)
    assert "OnlineMeetingTranscript.Read.All" in str(exc.value)


def test_list_transcripts_non_403_error_has_no_access_policy_hint():
    def opener(req, timeout=None):
        raise _http_error(404, body={"error": {"code": "NotFound", "message": "gone"}})

    client = GraphClient("t", opener=opener)
    with pytest.raises(GraphError) as exc:
        list_transcripts(client, "org1", "meeting1")
    assert "access policy" not in str(exc.value)


# =========================================================================
# download_transcript_vtt
# =========================================================================
def test_download_transcript_vtt_returns_decoded_text_with_correct_request():
    seen = {}

    def opener(req, timeout=None):
        seen["url"] = req.full_url
        seen["accept"] = req.get_header("Accept")
        return _FakeResp(b"WEBVTT\n\n1\n00:00:00.000 --> 00:00:01.000\n<v Alice>Hi\n")

    client = GraphClient("t", opener=opener)
    vtt = download_transcript_vtt(client, "org1", "meeting1", "tr1")

    assert vtt.startswith("WEBVTT")
    assert "<v Alice>Hi" in vtt
    assert seen["url"] == (
        "https://graph.microsoft.com/v1.0/users/org1/onlineMeetings/meeting1"
        "/transcripts/tr1/content?$format=text/vtt")
    assert seen["accept"] == "text/vtt"


def test_download_transcript_vtt_403_gets_actionable_access_policy_hint():
    def opener(req, timeout=None):
        raise _http_error(403, body={"error": {"code": "Forbidden", "message": "denied"}})

    client = GraphClient("t", opener=opener)
    with pytest.raises(GraphError) as exc:
        download_transcript_vtt(client, "org1", "meeting1", "tr1")
    assert "denied" in str(exc.value)
    assert "access policy" in str(exc.value)
    assert "OnlineMeetingTranscript.Read.All" in str(exc.value)


# =========================================================================
# fetch_meeting_vtt (full orchestration)
# =========================================================================
def _happy_routes(vtt_text=b"WEBVTT\n\n1\n00:00:00.000 --> 00:00:01.000\n<v Bob>Hello\n",
                   transcripts=None):
    transcripts = transcripts if transcripts is not None else [
        {"id": "tr1", "createdDateTime": "2026-07-01T10:00:00Z"}]
    return [
        (TOKEN_ROUTE, [_token_resp("tok")]),
        ("/content?$format=text/vtt", [_FakeResp(vtt_text)]),
        ("/transcripts", [_FakeResp({"value": transcripts})]),
        ("onlineMeetings?", [_FakeResp({"value": [{"id": "meeting-1"}]})]),
    ]


def test_fetch_meeting_vtt_happy_path_threads_all_calls_through_one_opener():
    opener = _ScriptedOpener(_happy_routes())
    vtt = fetch_meeting_vtt("tenant1", "client1", "secret1", "organizer1",
                            "https://teams.microsoft.com/l/x", opener=opener)
    assert vtt.startswith("WEBVTT")
    assert "<v Bob>Hello" in vtt
    # every leg of the pipeline actually made a request
    urls = [r.full_url for r in opener.calls]
    assert any(TOKEN_ROUTE in u for u in urls)
    assert any("onlineMeetings?" in u for u in urls)
    assert any(u.endswith("/transcripts") for u in urls)
    assert any("/content?$format=text/vtt" in u for u in urls)


def test_fetch_meeting_vtt_picks_latest_transcript_by_created_date_time():
    transcripts = [
        {"id": "old", "createdDateTime": "2026-01-01T00:00:00Z"},
        {"id": "newest", "createdDateTime": "2026-06-15T12:00:00Z"},
        {"id": "middle", "createdDateTime": "2026-03-01T00:00:00Z"},
    ]
    seen_content_url = {}

    routes = [
        (TOKEN_ROUTE, [_token_resp("tok")]),
        ("onlineMeetings?", [_FakeResp({"value": [{"id": "meeting-1"}]})]),
        ("/transcripts", [_FakeResp({"value": transcripts})]),
    ]
    opener = _ScriptedOpener(routes)

    def wrapped(req, timeout=None):
        if "/content?" in req.full_url:
            seen_content_url["url"] = req.full_url
            return _FakeResp(b"WEBVTT\nlatest content\n")
        return opener(req, timeout=timeout)

    vtt = fetch_meeting_vtt("t", "c", "s", "org1", "https://teams.microsoft.com/l/x", opener=wrapped)
    assert "/transcripts/newest/content" in seen_content_url["url"]
    assert "latest content" in vtt


def test_fetch_meeting_vtt_no_transcript_single_attempt_raises_immediately(monkeypatch):
    sleeps = []
    monkeypatch.setattr("agent.transcript_fetch.time.sleep", lambda s: sleeps.append(s))
    routes = [
        (TOKEN_ROUTE, [_token_resp()]),
        ("onlineMeetings?", [_FakeResp({"value": [{"id": "meeting-1"}]})]),
        ("/transcripts", [_FakeResp({"value": []})]),
    ]
    opener = _ScriptedOpener(routes)

    with pytest.raises(GraphError) as exc:
        fetch_meeting_vtt("t", "c", "s", "org1", "https://teams.microsoft.com/l/x",
                          opener=opener, poll_attempts=1)
    assert "no transcript available" in str(exc.value)
    assert sleeps == []  # single attempt: never slept


def test_fetch_meeting_vtt_polls_then_succeeds(monkeypatch):
    sleeps = []
    monkeypatch.setattr("agent.transcript_fetch.time.sleep", lambda s: sleeps.append(s))
    list_calls = {"n": 0}

    def transcripts_handler(req):
        list_calls["n"] += 1
        if list_calls["n"] < 3:
            return _FakeResp({"value": []})
        return _FakeResp({"value": [{"id": "tr1", "createdDateTime": "2026-07-01T00:00:00Z"}]})

    routes = [
        (TOKEN_ROUTE, [_token_resp()]),
        ("onlineMeetings?", [_FakeResp({"value": [{"id": "meeting-1"}]})]),
    ]
    base = _ScriptedOpener(routes)

    def opener(req, timeout=None):
        if req.full_url.endswith("/transcripts"):
            return transcripts_handler(req)
        if "/content?" in req.full_url:
            return _FakeResp(b"WEBVTT\ndone\n")
        return base(req, timeout=timeout)

    vtt = fetch_meeting_vtt("t", "c", "s", "org1", "https://teams.microsoft.com/l/x",
                            opener=opener, poll_attempts=3, poll_seconds=15)
    assert "done" in vtt
    assert list_calls["n"] == 3
    assert sleeps == [15, 15]  # slept between attempts 1->2 and 2->3, not after the final success


def test_fetch_meeting_vtt_polls_then_gives_up(monkeypatch):
    sleeps = []
    monkeypatch.setattr("agent.transcript_fetch.time.sleep", lambda s: sleeps.append(s))
    list_calls = {"n": 0}

    def opener(req, timeout=None):
        if req.full_url.endswith("/transcripts"):
            list_calls["n"] += 1
            return _FakeResp({"value": []})
        if TOKEN_ROUTE in req.full_url:
            return _token_resp()
        if "onlineMeetings?" in req.full_url:
            return _FakeResp({"value": [{"id": "meeting-1"}]})
        raise AssertionError(f"unexpected request {req.full_url}")

    with pytest.raises(GraphError) as exc:
        fetch_meeting_vtt("t", "c", "s", "org1", "https://teams.microsoft.com/l/x",
                          opener=opener, poll_attempts=3, poll_seconds=1)
    assert "no transcript available" in str(exc.value)
    assert list_calls["n"] == 3
    assert sleeps == [1, 1]


def test_fetch_meeting_vtt_token_error_propagates():
    def opener(req, timeout=None):
        if TOKEN_ROUTE in req.full_url:
            raise _http_error(401, body={"error": "invalid_client",
                                          "error_description": "bad secret"})
        raise AssertionError("should not reach Graph without a token")

    with pytest.raises(GraphError) as exc:
        fetch_meeting_vtt("t", "c", "wrong", "org1", "https://teams.microsoft.com/l/x", opener=opener)
    assert "bad secret" in str(exc.value)


def test_fetch_meeting_vtt_latest_transcript_missing_id_raises_graph_error_not_keyerror():
    routes = [
        (TOKEN_ROUTE, [_token_resp()]),
        ("onlineMeetings?", [_FakeResp({"value": [{"id": "meeting-1"}]})]),
        ("/transcripts", [_FakeResp({"value": [{"createdDateTime": "2026-07-01T00:00:00Z"}]})]),
    ]
    opener = _ScriptedOpener(routes)

    with pytest.raises(GraphError) as exc:
        fetch_meeting_vtt("t", "c", "s", "org1", "https://teams.microsoft.com/l/x", opener=opener)
    assert "id" in str(exc.value)


def test_fetch_meeting_vtt_transcripts_non_object_top_level_json_raises_graph_error():
    routes = [
        (TOKEN_ROUTE, [_token_resp()]),
        ("onlineMeetings?", [_FakeResp({"value": [{"id": "meeting-1"}]})]),
        ("/transcripts", [_FakeResp(b"[]")]),
    ]
    opener = _ScriptedOpener(routes)

    with pytest.raises(GraphError):
        fetch_meeting_vtt("t", "c", "s", "org1", "https://teams.microsoft.com/l/x", opener=opener)


def test_fetch_meeting_vtt_missing_credentials_raises_graph_error_with_no_network_call():
    calls = []

    def opener(req, timeout=None):
        calls.append(req)
        raise AssertionError("no network call should have been made")

    with pytest.raises(GraphError) as exc:
        fetch_meeting_vtt("", "client1", "secret1", "organizer1",
                          "https://teams.microsoft.com/l/x", opener=opener)
    assert "Graph credentials not configured" in str(exc.value)
    assert "PREPPIE_GRAPH_TENANT_ID" in str(exc.value)
    assert calls == []


def test_fetch_meeting_vtt_meeting_not_found_propagates():
    routes = [
        (TOKEN_ROUTE, [_token_resp()]),
        ("onlineMeetings?", [_FakeResp({"value": []})]),
    ]
    opener = _ScriptedOpener(routes)

    with pytest.raises(GraphError) as exc:
        fetch_meeting_vtt("t", "c", "s", "org1", "https://teams.microsoft.com/l/x", opener=opener)
    assert "meeting not found" in str(exc.value)


if __name__ == "__main__":
    import sys
    sys.exit(pytest.main([__file__, "-q"]))
