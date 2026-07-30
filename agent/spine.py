"""
Preppie spine - transcript -> Azure AI Foundry agent (Responses API) -> triaged Azure DevOps
backlog, with dedupe.

This is the meeting-to-backlog core (#27 + #62): the agent reasons over the transcript and drives
the deployed backend tools. Tool-calling is a thin, stateless loop - no separate long-running
middleware. Both the LLM client and the backend are injected, so the loop is unit-testable without
network access (see tests/test_spine.py).
"""
from __future__ import annotations

import json
import time
import urllib.request
import urllib.error
from typing import Any, Callable


# ---------- transcript parsing ----------
def parse_vtt(text: str) -> str:
    """Flatten a WEBVTT transcript to 'Speaker: text' lines (drops cue numbers/timestamps)."""
    lines = []
    for raw in text.splitlines():
        s = raw.strip()
        if not s or s == "WEBVTT" or "-->" in s or s.isdigit():
            continue
        head, sep, tail = s.replace("<v ", "").partition(">")
        if sep:  # had a <v Name> voice tag
            lines.append(f"{head.strip()}: {tail.strip()}")
        else:
            lines.append(s)
    return "\n".join(lines)


def read_transcript(path: str) -> str:
    with open(path, encoding="utf-8") as f:
        text = f.read()
    return parse_vtt(text) if path.lower().endswith(".vtt") else text


# ---------- backend tool dispatch ----------
class Backend:
    """Calls the deployed Azure Functions backend. Every call is scoped to one project."""

    def __init__(self, base_url: str, project: str, opener: Callable | None = None,
                 attempts: int = 4):
        self.base_url = base_url.rstrip("/")
        self.project = project
        self._opener = opener or urllib.request.urlopen
        self._attempts = attempts

    def _request(self, path: str, method: str, body: dict | None) -> dict:
        # Retry transient connection blips and 5xx (Flex Consumption cold-scale can drop a
        # connection); a single hiccup must not abort a whole meeting's backlog.
        data = json.dumps(body).encode() if body is not None else None
        for attempt in range(self._attempts):
            req = urllib.request.Request(
                f"{self.base_url}{path}", data=data, method=method,
                headers={"Content-Type": "application/json"})
            try:
                with self._opener(req, timeout=45) as r:
                    return json.loads(r.read().decode())
            except urllib.error.HTTPError as e:
                if e.code >= 500 and attempt < self._attempts - 1:
                    time.sleep(1.5 * (attempt + 1))
                    continue
                return {"success": False, "error": f"HTTP {e.code}: {e.read().decode()[:300]}"}
            except urllib.error.URLError as e:
                if attempt < self._attempts - 1:
                    time.sleep(1.5 * (attempt + 1))
                    continue
                return {"success": False, "error": f"connection error after {self._attempts} tries: {e}"}
        return {"success": False, "error": "no request attempts made (attempts must be >= 1)"}

    def dispatch(self, name: str, args: dict) -> dict:
        """Route a tool call to the backend, injecting the project. Returns the backend JSON."""
        if name == "read_projects":
            return self._request("/api/read_projects", "GET", None)
        if name == "search_work_items":
            return self._request("/api/search_work_items", "POST", {**args, "project": self.project})
        if name == "create_work_item":
            return self._request("/api/create_work_item", "POST", {**args, "project": self.project})
        if name == "link_work_items":
            return self._request("/api/link_work_items", "POST", {**args, "project": self.project})
        return {"success": False, "error": f"unknown tool {name}"}


# ---------- tool schemas (match the deployed backend contract) ----------
TOOLS: list[dict[str, Any]] = [
    {"type": "function", "name": "read_projects",
     "description": "List Azure DevOps projects in the org. Use to confirm the target project exists.",
     "parameters": {"type": "object", "properties": {}, "required": []}},
    {"type": "function", "name": "search_work_items",
     "description": "Search existing work items in the target project. Use for DEDUPE before creating "
                    "(titleContains), and to find a just-created parent.",
     "parameters": {"type": "object", "properties": {
         "titleContains": {"type": "string", "description": "Substring to match in the title."},
         "workItemType": {"type": "string", "description": "Filter by type e.g. Task, Bug, Issue, Epic, Feature, User Story."},
         "state": {"type": "string"},
         "tags": {"type": "string", "description": "Substring to match in the item's tags (e.g. 'Risk')."},
         "top": {"type": "integer"}}, "required": []}},
    {"type": "function", "name": "create_work_item",
     "description": "Create one work item in the target project. Returns its id and url. "
                    "Apply the type->ADO mapping and tags from your instructions.",
     "parameters": {"type": "object", "properties": {
         "type": {"type": "string", "description": "Azure DevOps type: Task, Bug, Issue, Epic, Feature, or User Story."},
         "title": {"type": "string", "description": "Clean, client-safe title (no type prefix)."},
         "description": {"type": "string", "description": "Rich description: context, who raised it, definition of done."},
         "acceptanceCriteria": {"type": "array", "items": {"type": "string"},
                                 "description": "User Stories only. AMP form (Acceptance/Measure/Proof)."},
         "tags": {"type": "array", "items": {"type": "string"},
                  "description": "Triage tag when the native type differs, e.g. ['Enhancement'], ['Risk'], ['Question']."},
         "priority": {"type": "integer", "description": "1 (highest) to 4."},
         "estimatedEffort": {"type": "string", "description": "Optional effort/size estimate, e.g. '3', '5', '8'."}},
         "required": ["type", "title", "description"]}},
    {"type": "function", "name": "link_work_items",
     "description": "Link a parent to child work items (parent-child hierarchy). "
                    "sourceId = parent id, targetIds = child ids.",
     "parameters": {"type": "object", "properties": {
         "sourceId": {"type": "integer", "description": "Parent work item id."},
         "targetIds": {"type": "array", "items": {"type": "integer"}, "description": "Child work item ids."},
         "linkType": {"type": "string", "description": "Default System.LinkTypes.Hierarchy-Forward (parent->child)."}},
         "required": ["sourceId", "targetIds"]}},
]

USER_PROMPT = (
    "Process this meeting transcript into the backlog now. Dedupe before creating, build the "
    "Epic/Feature/User Story structure, attach captured items under the nearest relevant parent, "
    "and finish with the reply-back roll-up table.\n\n=== TRANSCRIPT ===\n"
)


def run_spine(transcript: str, instructions: str, client, backend: Backend, model: str,
              *, on_event: Callable[[str], None] | None = None, max_turns: int = 30) -> dict:
    """
    Drive the Responses-API tool loop until the model stops calling tools.

    `client` must expose `responses.create(...)` (an openai AzureOpenAI, or a fake in tests).
    Returns {created: [...backend results...], reply_back: str, turns: int}.
    """
    def emit(msg: str) -> None:
        if on_event:
            on_event(msg)

    resp = client.responses.create(
        model=model, instructions=instructions,
        input=[{"role": "user", "content": USER_PROMPT + transcript}], tools=TOOLS)

    created: list[dict] = []
    truncated = False
    turns = 0
    for turns in range(1, max_turns + 1):
        calls = [o for o in resp.output if getattr(o, "type", None) == "function_call"]
        if not calls:
            break
        outputs = []
        for c in calls:
            try:
                args = json.loads(c.arguments or "{}")
                if not isinstance(args, dict):
                    raise ValueError(f"expected a JSON object, got {type(args).__name__}")
            except (json.JSONDecodeError, ValueError) as e:
                # A malformed tool call must not kill the whole run (and every call_id still
                # needs a matching output, or the next Responses API call is invalid) - report
                # it back to the model as a failed call and move on.
                result = {"success": False, "error": f"malformed arguments for {c.name}: {e}"}
                emit(f"BAD ARGS for {c.name}: {c.arguments!r} ({e})")
                outputs.append({"type": "function_call_output", "call_id": c.call_id,
                                 "output": json.dumps(result)})
                continue
            result = backend.dispatch(c.name, args)
            if c.name == "create_work_item" and result.get("success"):
                created.append(result)
                emit(f"create {result['work_item_type']} #{result['work_item_id']}: {result['title']}")
            elif c.name == "create_work_item":
                emit(f"CREATE FAILED: {result.get('error')}")
            elif c.name == "search_work_items":
                emit(f"dedupe '{args.get('titleContains', '')}' -> {result.get('count', '?')} match(es)")
            elif c.name == "link_work_items":
                emit(f"link parent {args.get('sourceId')} -> {args.get('targetIds')}")
            outputs.append({"type": "function_call_output", "call_id": c.call_id, "output": json.dumps(result)})
        resp = client.responses.create(
            model=model, instructions=instructions,
            previous_response_id=resp.id, input=outputs, tools=TOOLS)
    else:
        # The for-loop ran out of turns without ever hitting the `break` above, i.e. the model
        # was still calling tools when the budget ran out. The last resp we just got back may
        # itself carry unserviced function_calls and therefore no real reply_back text - don't
        # let that surface as a silent blank summary when items may already have been created.
        pending = [o for o in resp.output if getattr(o, "type", None) == "function_call"]
        if pending:
            truncated = True
            emit(f"max_turns ({max_turns}) reached with {len(pending)} pending tool call(s); stopping")

    reply_back = resp.output_text
    if truncated and not reply_back:
        reply_back = (f"[Preppie stopped after {max_turns} turns without a final reply - "
                       f"{len(created)} item(s) were created; check Azure DevOps directly.]")
    return {"created": created, "reply_back": reply_back, "turns": turns}
