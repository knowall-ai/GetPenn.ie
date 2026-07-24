"""Unit tests for agent.reply_back. No network: the HTTP opener and the sender are injected."""
import json
import urllib.error

import pytest

from agent.reply_back import (
    DEFAULT_MAX_ITEMS,
    WebhookSender,
    build_adaptive_card,
    post_reply_back,
)


# ---------- shared fakes ----------
class _FakeResp:
    """Mirrors agent.tests.test_spine._FakeResp: a urlopen-context-manager stand-in."""

    def __init__(self, body=b"", status=200):
        self._b = body if isinstance(body, bytes) else json.dumps(body).encode()
        self.status = status
        self.code = status

    def read(self):
        return self._b

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


def _http_error(code, body=b"", headers=None):
    return urllib.error.HTTPError(
        url="https://example.invalid/webhook", code=code, msg=f"HTTP {code}",
        hdrs=headers or {}, fp=None if body == b"" else __import__("io").BytesIO(body))


def _sample_result():
    return {
        "created": [
            {"work_item_type": "Epic", "work_item_id": 1, "title": "Portal", "url": "http://x/1"},
            {"work_item_type": "Feature", "work_item_id": 2, "title": "Auth", "url": "http://x/2"},
            {"work_item_type": "Feature", "work_item_id": 3, "title": "Billing", "url": "http://x/3"},
            {"work_item_type": "Bug", "work_item_id": 4, "title": "Crash on save", "url": "http://x/4"},
        ],
        "reply_back": "Logged 4 items.",
        "turns": 3,
    }


# =========================================================================
# build_adaptive_card
# =========================================================================
def test_card_has_valid_adaptive_card_envelope():
    card = build_adaptive_card(_sample_result())
    assert card["$schema"] == "http://adaptivecards.io/schemas/adaptive-card.json"
    assert card["type"] == "AdaptiveCard"
    assert card["version"] == "1.4"
    assert isinstance(card["body"], list) and len(card["body"]) > 0


def test_card_default_header_text():
    card = build_adaptive_card(_sample_result())
    assert card["body"][0]["text"] == "\U0001F5C2️ Preppie — meeting backlog"
    assert card["body"][0]["wrap"] is True


def test_card_custom_meeting_title_overrides_header():
    card = build_adaptive_card(_sample_result(), meeting_title="Sprint 12 Planning")
    assert card["body"][0]["text"] == "Sprint 12 Planning"


def test_card_summary_line_breakdown_by_type_in_first_seen_order():
    card = build_adaptive_card(_sample_result())
    summary = card["body"][1]["text"]
    assert summary == "4 work item(s) created: 1 Epic · 2 Features · 1 Bug"


def test_card_body_lines_use_adaptive_card_markdown_with_link():
    card = build_adaptive_card(_sample_result())
    texts = [b["text"] for b in card["body"]]
    assert "- **Epic #1** [Portal](http://x/1)" in texts
    assert "- **Bug #4** [Crash on save](http://x/4)" in texts


def test_card_missing_url_renders_title_without_link():
    result = {"created": [{"work_item_type": "Task", "work_item_id": 9, "title": "No link"}],
              "reply_back": ""}
    card = build_adaptive_card(result)
    texts = [b["text"] for b in card["body"]]
    assert "- **Task #9** No link" in texts


def test_card_missing_title_falls_back_to_untitled():
    result = {"created": [{"work_item_type": "Task", "work_item_id": 9, "url": "http://x"}]}
    card = build_adaptive_card(result)
    texts = [b["text"] for b in card["body"]]
    assert "- **Task #9** [(untitled)](http://x)" in texts


def test_card_missing_type_and_id_use_fallbacks():
    result = {"created": [{"title": "Mystery item"}]}
    card = build_adaptive_card(result)
    texts = [b["text"] for b in card["body"]]
    assert "- **Item #?** Mystery item" in texts


def test_card_reply_back_appended_as_wrapped_text_block():
    card = build_adaptive_card(_sample_result())
    assert any(b["text"] == "Logged 4 items." and b.get("wrap") is True for b in card["body"])


def test_card_empty_reply_back_adds_no_extra_block():
    result = {"created": [], "reply_back": ""}
    card = build_adaptive_card(result)
    assert not any(b["text"] == "" for b in card["body"])


def test_card_none_reply_back_is_safe():
    result = {"created": [], "reply_back": None}
    card = build_adaptive_card(result)
    assert card["body"][1]["text"] == "No backlog items were created from this meeting."


def test_card_open_board_action_present_when_board_url_given():
    card = build_adaptive_card(_sample_result(), board_url="https://dev.azure.com/org/proj/_boards")
    assert card["actions"] == [{"type": "Action.OpenUrl", "title": "Open board",
                                "url": "https://dev.azure.com/org/proj/_boards"}]


def test_card_no_actions_key_when_board_url_absent():
    card = build_adaptive_card(_sample_result())
    assert "actions" not in card


# ---- empty created ----
def test_card_empty_created_still_valid_and_has_no_items_message():
    card = build_adaptive_card({"created": [], "reply_back": ""})
    assert card["type"] == "AdaptiveCard"
    assert card["body"][1]["text"] == "No backlog items were created from this meeting."


def test_card_empty_created_with_reply_back_still_appends_it():
    card = build_adaptive_card({"created": [], "reply_back": "Nothing to log this time."})
    texts = [b["text"] for b in card["body"]]
    assert "No backlog items were created from this meeting." in texts
    assert "Nothing to log this time." in texts


# ---- truncation ----
def test_card_truncates_over_max_items_and_notes_extra_count():
    created = [{"work_item_type": "Task", "work_item_id": i, "title": f"T{i}", "url": f"http://x/{i}"}
               for i in range(25)]
    card = build_adaptive_card({"created": created, "reply_back": ""}, max_items=20)
    item_lines = [b["text"] for b in card["body"] if b["text"].startswith("- **Task")]
    assert len(item_lines) == 20
    more_line = card["body"][-1]["text"] if not card["body"][-1]["text"].startswith("- **Task") else None
    assert any("+5 more" in b["text"] for b in card["body"])


def test_card_truncation_links_board_url_when_given():
    created = [{"work_item_type": "Task", "work_item_id": i, "title": f"T{i}"} for i in range(21)]
    card = build_adaptive_card({"created": created}, max_items=20, board_url="https://board.example")
    assert any(b["text"] == "[+1 more — see the full board](https://board.example)"
              for b in card["body"])


def test_card_truncation_without_board_url_is_plain_text():
    created = [{"work_item_type": "Task", "work_item_id": i, "title": f"T{i}"} for i in range(21)]
    card = build_adaptive_card({"created": created}, max_items=20)
    assert any(b["text"] == "+1 more — see the full board" for b in card["body"])


def test_card_at_exactly_max_items_is_not_truncated():
    created = [{"work_item_type": "Task", "work_item_id": i, "title": f"T{i}"} for i in range(20)]
    card = build_adaptive_card({"created": created}, max_items=20)
    assert not any("more" in b["text"] and "see the full board" in b["text"] for b in card["body"])


# ---- markdown / JSON safety ----
def test_card_title_with_markdown_metachars_stays_json_safe():
    nasty_title = 'Fix "the" [bracket] (paren) *star* _under_ `tick` <tag> | pipe\nnewline'
    result = {"created": [{"work_item_type": "Bug", "work_item_id": 5, "title": nasty_title,
                          "url": "http://x"}]}
    card = build_adaptive_card(result)
    # Must round-trip through json.dumps/loads without error, and the title survives (minus
    # embedded newlines, which are flattened to spaces to keep each bullet on one line).
    encoded = json.dumps(card)
    decoded = json.loads(encoded)
    assert decoded == card
    line = [b["text"] for b in card["body"] if b["text"].startswith("- **Bug #5**")][0]
    assert "\n" not in line
    assert "bracket" in line and "pipe" in line and "newline" in line


def test_card_reply_back_with_metachars_stays_json_safe():
    nasty = 'Summary: <b>bold</b> & "quotes" & [brackets] & `code`\nsecond line'
    card = build_adaptive_card({"created": [], "reply_back": nasty})
    json.dumps(card)  # must not raise
    assert any("quotes" in b["text"] for b in card["body"])


# ---- never raises ----
def test_card_never_raises_on_none_result():
    card = build_adaptive_card(None)
    assert card["type"] == "AdaptiveCard" and card["version"] == "1.4"


def test_card_never_raises_on_empty_dict_result():
    card = build_adaptive_card({})
    assert card["type"] == "AdaptiveCard"
    assert card["body"][1]["text"] == "No backlog items were created from this meeting."


def test_card_never_raises_on_completely_malformed_result():
    card = build_adaptive_card({"created": "not-a-list", "reply_back": 12345})
    assert card["type"] == "AdaptiveCard"


def test_card_never_raises_on_created_items_that_are_not_dicts():
    card = build_adaptive_card({"created": [None, "oops", 42, {"title": "ok one"}]})
    assert card["type"] == "AdaptiveCard"
    texts = [b["text"] for b in card["body"]]
    assert any("ok one" in t for t in texts)


# =========================================================================
# WebhookSender.send
# =========================================================================
def test_sender_no_url_configured_returns_error_without_request(monkeypatch):
    called = {"n": 0}

    def opener(req, timeout=None):
        called["n"] += 1
        return _FakeResp(status=202)

    for bad_url in (None, "", "   "):
        sender = WebhookSender(bad_url, opener=opener)
        out = sender.send({"type": "AdaptiveCard"})
        assert out == {"sent": False, "status": None, "error": "no webhook url configured"}
    assert called["n"] == 0


def test_sender_success_on_202_workflows(monkeypatch):
    monkeypatch.setattr("agent.reply_back.time.sleep", lambda *_: None)
    seen = {}

    def opener(req, timeout=None):
        seen["url"] = req.full_url
        seen["method"] = req.get_method()
        seen["content_type"] = req.get_header("Content-type")
        seen["body"] = json.loads(req.data.decode())
        return _FakeResp(status=202)

    sender = WebhookSender("https://example.invalid/webhook", opener=opener)
    out = sender.send({"type": "AdaptiveCard", "body": []})

    assert out == {"sent": True, "status": 202, "error": None}
    assert seen["url"] == "https://example.invalid/webhook"
    assert seen["method"] == "POST"
    assert seen["content_type"] == "application/json"
    assert seen["body"]["type"] == "message"
    assert seen["body"]["attachments"][0]["contentType"] == "application/vnd.microsoft.card.adaptive"
    assert seen["body"]["attachments"][0]["content"] == {"type": "AdaptiveCard", "body": []}


def test_sender_success_on_200_legacy_connector_body_1(monkeypatch):
    monkeypatch.setattr("agent.reply_back.time.sleep", lambda *_: None)

    def opener(req, timeout=None):
        return _FakeResp(body=b"1", status=200)

    out = WebhookSender("https://example.invalid/webhook", opener=opener).send({"type": "AdaptiveCard"})
    assert out == {"sent": True, "status": 200, "error": None}


def test_sender_retries_then_succeeds_on_5xx(monkeypatch):
    sleeps = []
    monkeypatch.setattr("agent.reply_back.time.sleep", lambda s: sleeps.append(s))
    calls = {"n": 0}

    def opener(req, timeout=None):
        calls["n"] += 1
        if calls["n"] < 3:
            raise _http_error(503)
        return _FakeResp(status=202)

    out = WebhookSender("https://x", opener=opener, attempts=4).send({})
    assert out == {"sent": True, "status": 202, "error": None}
    assert calls["n"] == 3
    assert len(sleeps) == 2  # backed off twice before succeeding


def test_sender_retries_then_succeeds_on_urlerror(monkeypatch):
    monkeypatch.setattr("agent.reply_back.time.sleep", lambda *_: None)
    calls = {"n": 0}

    def opener(req, timeout=None):
        calls["n"] += 1
        if calls["n"] < 2:
            raise urllib.error.URLError("connection refused")
        return _FakeResp(status=202)

    out = WebhookSender("https://x", opener=opener, attempts=4).send({})
    assert out == {"sent": True, "status": 202, "error": None}
    assert calls["n"] == 2


def test_sender_429_respects_retry_after_header_then_succeeds(monkeypatch):
    sleeps = []
    monkeypatch.setattr("agent.reply_back.time.sleep", lambda s: sleeps.append(s))
    calls = {"n": 0}

    def opener(req, timeout=None):
        calls["n"] += 1
        if calls["n"] == 1:
            raise _http_error(429, headers={"Retry-After": "5"})
        return _FakeResp(status=202)

    out = WebhookSender("https://x", opener=opener, attempts=3).send({})
    assert out == {"sent": True, "status": 202, "error": None}
    assert sleeps == [5.0]


def test_sender_429_retry_after_capped_at_30_seconds(monkeypatch):
    sleeps = []
    monkeypatch.setattr("agent.reply_back.time.sleep", lambda s: sleeps.append(s))
    calls = {"n": 0}

    def opener(req, timeout=None):
        calls["n"] += 1
        if calls["n"] == 1:
            raise _http_error(429, headers={"Retry-After": "120"})
        return _FakeResp(status=202)

    WebhookSender("https://x", opener=opener, attempts=3).send({})
    assert sleeps == [30.0]


def test_sender_429_without_retry_after_uses_default_backoff(monkeypatch):
    sleeps = []
    monkeypatch.setattr("agent.reply_back.time.sleep", lambda s: sleeps.append(s))
    calls = {"n": 0}

    def opener(req, timeout=None):
        calls["n"] += 1
        if calls["n"] == 1:
            raise _http_error(429)
        return _FakeResp(status=202)

    out = WebhookSender("https://x", opener=opener, attempts=3).send({})
    assert out["sent"] is True
    assert sleeps == [1.5]


def test_sender_gives_up_after_attempts_exhausted(monkeypatch):
    monkeypatch.setattr("agent.reply_back.time.sleep", lambda *_: None)

    def always_503(req, timeout=None):
        raise _http_error(503)

    out = WebhookSender("https://x", opener=always_503, attempts=3).send({})
    assert out["sent"] is False
    assert out["status"] == 503
    assert "HTTP 503" in out["error"]


def test_sender_gives_up_after_attempts_exhausted_urlerror(monkeypatch):
    monkeypatch.setattr("agent.reply_back.time.sleep", lambda *_: None)

    def always_refuse(req, timeout=None):
        raise urllib.error.URLError("refused")

    out = WebhookSender("https://x", opener=always_refuse, attempts=3).send({})
    assert out == {"sent": False, "status": None,
                   "error": "connection error after 3 tries: <urlopen error refused>"}


def test_sender_non_retryable_4xx_fails_without_burning_all_attempts(monkeypatch):
    monkeypatch.setattr("agent.reply_back.time.sleep", lambda *_: None)
    calls = {"n": 0}

    def opener(req, timeout=None):
        calls["n"] += 1
        raise _http_error(400, body=b"bad card schema")

    out = WebhookSender("https://x", opener=opener, attempts=4).send({})
    assert out == {"sent": False, "status": 400, "error": "HTTP 400: bad card schema"}
    assert calls["n"] == 1  # no retries burned on a non-retryable 4xx


def test_sender_never_raises_on_unexpected_opener_exception(monkeypatch):
    monkeypatch.setattr("agent.reply_back.time.sleep", lambda *_: None)

    def boom(req, timeout=None):
        raise ValueError("opener exploded")

    out = WebhookSender("https://x", opener=boom, attempts=2).send({})
    assert out["sent"] is False
    assert "unexpected error" in out["error"]


# =========================================================================
# post_reply_back
# =========================================================================
class _FakeSender:
    def __init__(self, response):
        self._response = response
        self.sent_cards = []

    def send(self, card):
        self.sent_cards.append(card)
        return self._response


def test_post_reply_back_success_reports_item_count_and_not_truncated():
    sender = _FakeSender({"sent": True, "status": 202, "error": None})
    out = post_reply_back(_sample_result(), sender)
    assert out == {"sent": True, "status": 202, "error": None, "item_count": 4, "truncated": False}
    assert sender.sent_cards[0]["type"] == "AdaptiveCard"


def test_post_reply_back_failure_propagates_error():
    sender = _FakeSender({"sent": False, "status": None, "error": "no webhook url configured"})
    out = post_reply_back({"created": [], "reply_back": ""}, sender)
    assert out == {"sent": False, "status": None, "error": "no webhook url configured",
                   "item_count": 0, "truncated": False}


def test_post_reply_back_reports_truncated_when_over_max_items():
    created = [{"work_item_type": "Task", "work_item_id": i, "title": f"T{i}"} for i in range(23)]
    sender = _FakeSender({"sent": True, "status": 202, "error": None})
    out = post_reply_back({"created": created, "reply_back": ""}, sender, max_items=20)
    assert out["item_count"] == 23
    assert out["truncated"] is True
    assert out["truncated"] is True and DEFAULT_MAX_ITEMS == 20


def test_post_reply_back_never_raises_on_none_result():
    sender = _FakeSender({"sent": True, "status": 202, "error": None})
    out = post_reply_back(None, sender)
    assert out["sent"] is True and out["item_count"] == 0 and out["truncated"] is False


def test_post_reply_back_never_raises_when_sender_raises():
    class _ExplodingSender:
        def send(self, card):
            raise RuntimeError("network is on fire")

    out = post_reply_back(_sample_result(), _ExplodingSender())
    assert out["sent"] is False
    assert "network is on fire" in out["error"]
    assert out["item_count"] == 4


def test_post_reply_back_handles_sender_returning_non_dict():
    class _WeirdSender:
        def send(self, card):
            return None

    out = post_reply_back({"created": []}, _WeirdSender())
    assert out["sent"] is False
    assert out["error"] == "sender returned a non-dict result"


# ---- end-to-end: post_reply_back with a real WebhookSender + fake opener ----
def test_post_reply_back_end_to_end_with_fake_opener(monkeypatch):
    monkeypatch.setattr("agent.reply_back.time.sleep", lambda *_: None)
    posted = {}

    def opener(req, timeout=None):
        posted["envelope"] = json.loads(req.data.decode())
        return _FakeResp(status=202)

    sender = WebhookSender("https://example.invalid/webhook", opener=opener)
    out = post_reply_back(_sample_result(), sender, board_url="https://board.example")

    assert out["sent"] is True and out["status"] == 202 and out["item_count"] == 4
    content = posted["envelope"]["attachments"][0]["content"]
    assert content["type"] == "AdaptiveCard"
    assert content["actions"][0]["url"] == "https://board.example"


if __name__ == "__main__":
    import sys
    sys.exit(pytest.main([__file__, "-q"]))


# =========================================================================
# adversarial-review regressions (production-hardening)
# =========================================================================
def test_card_stays_under_28kb_with_huge_reply_back_and_title():
    # spine sets reply_back from raw LLM output (uncapped); a verbose reply or a pathological
    # title must never blow the ~28KB Teams cap and sink the whole post. The size guard caps
    # fields and hard-trims the body, but the created-item count (summary) must always survive.
    result = {
        "created": [{"work_item_type": "Bug", "work_item_id": 5,
                     "title": "T" * 40000, "url": "http://x/5"}],
        "reply_back": "x" * 40000,
    }
    card = build_adaptive_card(result)
    assert len(json.dumps(card).encode()) < 28000
    assert any("1 work item(s) created" in b.get("text", "") for b in card["body"])


def test_card_many_long_items_hard_trims_to_fit_and_keeps_summary():
    result = {"created": [{"work_item_type": "Task", "work_item_id": i,
                           "title": "long title " * 40, "url": "http://x/%d" % i}
                          for i in range(500)],
              "reply_back": ""}
    card = build_adaptive_card(result, max_items=500)
    assert len(json.dumps(card).encode()) < 28000
    assert any("work item(s) created" in b.get("text", "") for b in card["body"])


def test_item_url_with_space_is_percent_encoded_so_link_survives():
    result = {"created": [{"work_item_type": "Task", "work_item_id": 3,
                           "title": "Normal", "url": "http://x/3?a=b c&d=1"}]}
    card = build_adaptive_card(result)
    line = [b["text"] for b in card["body"] if b["text"].startswith("- **Task #3**")][0]
    assert "%20" in line          # the space was encoded
    assert " c&d" not in line     # no raw space left inside the (...) link
    assert line.endswith(")")


def test_title_with_unbalanced_bracket_is_escaped():
    result = {"created": [{"work_item_type": "Task", "work_item_id": 5,
                           "title": "Weird ] title", "url": "http://x/5"}]}
    card = build_adaptive_card(result)
    line = [b["text"] for b in card["body"] if b["text"].startswith("- **Task #5**")][0]
    assert line == r"- **Task #5** [Weird \] title](http://x/5)"


def test_title_with_embedded_link_cannot_hijack_the_real_url():
    # A transcript-derived title containing its own [text](url) must not become the clickable
    # target - the real work-item url has to win.
    result = {"created": [{"work_item_type": "Bug", "work_item_id": 2,
                           "title": "See [details](http://evil.example/phish)",
                           "url": "http://x/2"}]}
    card = build_adaptive_card(result)
    line = [b["text"] for b in card["body"] if b["text"].startswith("- **Bug #2**")][0]
    assert r"\[details\]" in line          # embedded brackets escaped -> inert
    assert line.endswith("](http://x/2)")  # the real url is the link target


def test_card_max_items_true_does_not_silently_cap_to_one():
    # bool is an int subclass; max_items=True must not be read as max_items=1.
    result = {"created": [{"work_item_type": "Task", "work_item_id": i, "title": "t%d" % i,
                           "url": "http://x/%d" % i} for i in range(5)], "reply_back": ""}
    card = build_adaptive_card(result, max_items=True)
    item_lines = [b["text"] for b in card["body"] if b["text"].startswith("- **Task")]
    assert len(item_lines) == 5


class _FakeRespWithHeaders(_FakeResp):
    def __init__(self, body=b"", status=200, headers=None):
        super().__init__(body, status)
        self.headers = headers or {}


def test_sender_direct_response_429_honors_retry_after(monkeypatch):
    # A custom injected opener that RETURNS a 429 response object (rather than raising HTTPError)
    # must still honor Retry-After, identically to the HTTPError path.
    sleeps = []
    monkeypatch.setattr("agent.reply_back.time.sleep", lambda s: sleeps.append(s))
    seq = [_FakeRespWithHeaders(status=429, headers={"Retry-After": "7"}),
           _FakeRespWithHeaders(b"1", status=200)]

    def opener(req, timeout=None):
        return seq.pop(0)

    out = WebhookSender("https://x", opener=opener, attempts=3).send({})
    assert out["sent"] is True
    assert sleeps == [7.0]


def test_sender_direct_response_3xx_is_treated_as_failure(monkeypatch):
    monkeypatch.setattr("agent.reply_back.time.sleep", lambda *_: None)

    def opener(req, timeout=None):
        return _FakeResp(b"", status=300)

    out = WebhookSender("https://x", opener=opener, attempts=3).send({})
    assert out["sent"] is False and out["status"] == 300


def test_sender_attempts_zero_returns_clean_failure_without_request():
    called = {"n": 0}

    def opener(req, timeout=None):
        called["n"] += 1
        return _FakeResp(b"1", status=200)

    out = WebhookSender("https://x", opener=opener, attempts=0).send({})
    assert out["sent"] is False
    assert called["n"] == 0  # no request attempted


def test_post_reply_back_handles_non_dict_int_and_list_sender_results():
    class _IntSender:
        def send(self, card):
            return 7

    class _ListSender:
        def send(self, card):
            return ["nope"]

    for sender in (_IntSender(), _ListSender()):
        out = post_reply_back(_sample_result(), sender)
        assert out["sent"] is False
        assert out["item_count"] == 4
