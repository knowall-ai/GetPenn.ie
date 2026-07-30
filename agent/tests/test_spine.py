"""Unit tests for the Preppie spine. No network: the backend and the LLM client are injected."""
import json
import os
import types

from agent.spine import parse_vtt, read_transcript, Backend, run_spine


# ---------- transcript parsing ----------
def test_parse_vtt_extracts_speakers():
    vtt = ("WEBVTT\n\n1\n00:00:01.000 --> 00:00:03.000\n<v Alice>Hello there\n\n"
           "00:00:04.000 --> 00:00:05.000\n<v Bob>Hi Alice\n")
    assert parse_vtt(vtt) == "Alice: Hello there\nBob: Hi Alice"


def test_parse_vtt_keeps_untagged_lines_and_drops_noise():
    assert parse_vtt("WEBVTT\n\n2\n00:00:01.000 --> 00:00:02.000\nplain line\n") == "plain line"


def test_read_transcript_plain_file_is_verbatim(tmp_path):
    p = tmp_path / "notes.txt"
    p.write_text("Alice: just do it")
    assert read_transcript(str(p)) == "Alice: just do it"


def test_sample_fixture_parses():
    fixture = os.path.join(os.path.dirname(__file__), "fixtures", "sample-meeting.vtt")
    out = read_transcript(fixture)
    assert "Priya Sharma:" in out and "Daniel Okoro:" in out and "-->" not in out


# ---------- backend dispatch ----------
class _FakeResp:
    def __init__(self, body):
        self._b = json.dumps(body).encode()

    def read(self):
        return self._b

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


def test_backend_dispatch_injects_project_and_posts():
    seen = {}

    def opener(req, timeout=None):
        seen["url"] = req.full_url
        seen["method"] = req.get_method()
        seen["body"] = json.loads(req.data.decode()) if req.data else None
        return _FakeResp({"success": True, "count": 0})

    Backend("https://backend.example/", "MyProj", opener=opener).dispatch(
        "search_work_items", {"titleContains": "foo"})
    assert seen["url"].endswith("/api/search_work_items")
    assert seen["method"] == "POST"
    assert seen["body"] == {"project": "MyProj", "titleContains": "foo"}


def test_backend_read_projects_is_get_no_body():
    def opener(req, timeout=None):
        assert req.get_method() == "GET"
        assert req.data is None
        return _FakeResp({"success": True, "projects": []})

    Backend("https://b", "P", opener=opener).dispatch("read_projects", {})


def test_backend_retries_transient_connection_error(monkeypatch):
    import urllib.error
    monkeypatch.setattr("agent.spine.time.sleep", lambda *_: None)  # no real backoff in tests
    calls = {"n": 0}

    def flaky_opener(req, timeout=None):
        calls["n"] += 1
        if calls["n"] < 3:
            raise urllib.error.URLError("connection refused")
        return _FakeResp({"success": True, "count": 0})

    out = Backend("https://b", "P", opener=flaky_opener).dispatch("search_work_items", {})
    assert out["success"] is True and calls["n"] == 3  # failed twice, succeeded on the third


def test_backend_gives_up_after_attempts(monkeypatch):
    import urllib.error
    monkeypatch.setattr("agent.spine.time.sleep", lambda *_: None)

    def always_refuse(req, timeout=None):
        raise urllib.error.URLError("refused")

    out = Backend("https://b", "P", opener=always_refuse, attempts=3).dispatch("read_projects", {})
    assert out["success"] is False and "connection error" in out["error"]


def test_backend_dispatch_project_cannot_be_overridden_by_args():
    # The backend's guarantee is that every call is scoped to Backend.project. If the model's
    # tool-call arguments ever contained a stray "project" key, it must not win over that.
    seen = {}

    def opener(req, timeout=None):
        seen["body"] = json.loads(req.data.decode())
        return _FakeResp({"success": True})

    Backend("https://b", "RealProject", opener=opener).dispatch(
        "create_work_item", {"type": "Task", "title": "t", "description": "d", "project": "Hijacked"})
    assert seen["body"]["project"] == "RealProject"


# ---------- the run loop ----------
def _fcall(name, args, call_id):
    return types.SimpleNamespace(type="function_call", name=name,
                                 arguments=json.dumps(args), call_id=call_id)


class _FakeClient:
    """Replays a scripted list of (output_items, output_text) responses."""

    def __init__(self, scripted):
        self._scripted = list(scripted)
        self._n = 0
        self.calls = []
        self.responses = self

    def create(self, **kwargs):
        self.calls.append(kwargs)
        out, text = self._scripted[self._n]
        self._n += 1
        return types.SimpleNamespace(output=out, output_text=text, id=f"resp_{self._n}")


class _FakeBackend:
    def __init__(self):
        self.dispatched = []
        self._next_id = 100

    def dispatch(self, name, args):
        self.dispatched.append((name, args))
        if name == "create_work_item":
            self._next_id += 1
            return {"success": True, "work_item_id": self._next_id,
                    "work_item_type": args["type"], "title": args["title"], "url": "http://x"}
        if name == "search_work_items":
            return {"success": True, "count": 0, "workItems": []}
        return {"success": True}


def test_run_spine_dedupes_before_create_and_collects_results():
    scripted = [
        ([_fcall("search_work_items", {"titleContains": "Portal"}, "c1"),
          _fcall("create_work_item", {"type": "Epic", "title": "Portal", "description": "d"}, "c2")], ""),
        ([], "done: logged 1 item"),
    ]
    client, backend = _FakeClient(scripted), _FakeBackend()
    result = run_spine("Alice: build a portal", "INSTR", client, backend, "gpt-x")

    names = [n for n, _ in backend.dispatched]
    assert names.index("search_work_items") < names.index("create_work_item")  # dedupe first
    assert len(result["created"]) == 1
    assert result["created"][0]["title"] == "Portal"
    assert result["reply_back"] == "done: logged 1 item"
    # tool outputs were fed back referencing the prior response id
    assert client.calls[1].get("previous_response_id") == "resp_1"


def test_run_spine_terminates_at_max_turns():
    forever = ([_fcall("search_work_items", {}, "c")], "")
    result = run_spine("t", "i", _FakeClient([forever] * 50), _FakeBackend(), "m", max_turns=5)
    assert result["turns"] == 5
    # the model never produced a final text reply (it kept calling tools) - the caller must still
    # get a non-blank explanation rather than silently seeing an empty reply-back.
    assert result["reply_back"] and "5 turns" in result["reply_back"]


def test_run_spine_handles_malformed_tool_arguments_without_crashing():
    scripted = [
        ([types.SimpleNamespace(type="function_call", name="create_work_item",
                                arguments="{not valid json", call_id="c1")], ""),
        ([], "done"),
    ]
    client, backend = _FakeClient(scripted), _FakeBackend()
    result = run_spine("t", "i", client, backend, "m")
    # the bad call must never reach the backend, but the loop must still feed an output back
    # (referencing the same call_id) so the model isn't left hanging, and the run completes.
    assert backend.dispatched == []
    assert result["reply_back"] == "done"
    second_call_outputs = client.calls[1]["input"]
    assert second_call_outputs[0]["call_id"] == "c1"
    assert json.loads(second_call_outputs[0]["output"])["success"] is False


def test_run_spine_handles_non_object_tool_arguments():
    scripted = [
        ([types.SimpleNamespace(type="function_call", name="search_work_items",
                                arguments="[1, 2, 3]", call_id="c1")], ""),
        ([], "done"),
    ]
    result = run_spine("t", "i", _FakeClient(scripted), _FakeBackend(), "m")
    assert result["reply_back"] == "done"


class _FailingCreateBackend:
    """A backend whose create_work_item always fails, to check the loop doesn't leave the model
    hanging (every call_id still needs a function_call_output) or lose track of the run."""

    def __init__(self):
        self.dispatched = []

    def dispatch(self, name, args):
        self.dispatched.append((name, args))
        return {"success": False, "error": "Azure DevOps rejected the request"}


def test_run_spine_feeds_output_back_after_failed_create():
    scripted = [
        ([_fcall("create_work_item", {"type": "Task", "title": "T", "description": "d"}, "c1")], ""),
        ([], "done: 0 items logged"),
    ]
    client = _FakeClient(scripted)
    result = run_spine("t", "i", client, _FailingCreateBackend(), "m")

    assert result["created"] == []  # nothing to show for a failed create
    assert result["reply_back"] == "done: 0 items logged"  # the loop still completed normally
    fed_back = client.calls[1]["input"]
    assert fed_back[0]["call_id"] == "c1"
    assert json.loads(fed_back[0]["output"])["success"] is False


def test_run_spine_records_events():
    events = []
    scripted = [([_fcall("create_work_item", {"type": "Task", "title": "T", "description": "d"}, "c1")], ""),
                ([], "ok")]
    run_spine("t", "i", _FakeClient(scripted), _FakeBackend(), "m", on_event=events.append)
    assert any("create Task" in e for e in events)
