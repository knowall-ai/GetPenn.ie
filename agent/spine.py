"""
Preppie spine - transcript -> Azure AI Foundry agent (Responses API) -> triaged Azure DevOps
backlog, with dedupe.

This is the meeting-to-backlog core (#27 + #62): the agent reasons over the transcript and drives
the deployed backend tools. Tool-calling is a thin, stateless loop - no separate long-running
middleware. Both the LLM client and the backend are injected, so the loop is unit-testable without
network access (see tests/test_spine.py).
"""
from __future__ import annotations

import html
import json
import re
import time
import urllib.request
import urllib.error
from typing import Any, Callable


# ---------- transcript parsing ----------
# Speaker attribution is the whole point of this parser (#see task): the downstream agent needs
# to know WHO raised each item and WHO owns it, so getting "who said this" wrong is worse than
# getting formatting wrong. Real-world VTT exports (Teams and otherwise) disagree on nearly every
# optional piece of the spec - cue ids, voice classes, BOMs, line endings - so this is written to
# be lenient and never raise rather than strictly validate.

# Teams/other exporters sometimes emit a UTF-8 BOM before "WEBVTT" - strip it so the header check
# below doesn't fail to recognize an otherwise-valid file.
_BOM = "﻿"

# The WEBVTT header line can carry trailing metadata (e.g. "WEBVTT - Meeting recording") - match
# the whole line so it's dropped either way, not just the bare "WEBVTT" seen in toy examples.
_HEADER_RE = re.compile(r"^WEBVTT([ \t].*)?$")

# NOTE/STYLE/REGION are metadata blocks, not dialogue. Each runs from its keyword line through to
# the next blank line (or EOF) and must never leak into the transcript as if someone had said it.
_NOTE_RE = re.compile(r"^NOTE([ \t]|$)")
_METADATA_KEYWORDS = ("STYLE", "REGION")

# A real timing line is "<timestamp> --> <timestamp>" (optionally with trailing cue settings,
# ignored here via prefix match) - MM:SS.mmm or HH:MM:SS.mmm on both sides. This used to be a
# bare `"-->" in line` substring test, which misfires on ordinary dialogue that happens to
# contain a literal "-->" (e.g. someone says "a --> b") and silently drops the utterance.
_TIMING_RE = re.compile(
    r"^\d{2,}:\d{2}(?::\d{2})?\.\d{3}\s*-->\s*\d{2,}:\d{2}(?::\d{2})?\.\d{3}")

# A voice span looks like <v[.class[.class...]] Speaker Name>text</v>. Real exporters add
# cue-styling classes (e.g. <v.loud Alice>) that a naive "<v " prefix strip misses entirely, and
# the trailing </v> is easy to forget to strip (leaving it stuck onto the end of the utterance).
# The class char class deliberately excludes "." (not just space/tab/">") - allowing "." inside
# a repeated group here is a classic ReDoS trap: on an unterminated "<v" followed by a long run
# of dots, the engine can partition that run across the `(?:\...)*` repetition in exponentially
# many ways before concluding there's no final ">", hanging for effectively ever. Excluding "."
# forces a single, unambiguous partition (one dot begins each iteration) so matching stays linear.
_VOICE_OPEN_RE = re.compile(r"<v(?:\.[^ \t>.]+)*(?:[ \t]+([^>]*))?>", re.IGNORECASE)

# Inline styling tags (<i>, <b>, <u>, <c.class>, <ruby>, <rt>, <lang xx>, plus the closing </v>)
# carry no content of their own - strip the markup, keep the words that were inside it.
_INLINE_TAG_RE = re.compile(r"</?[A-Za-z][^>]*>")

# Karaoke-style inline word timestamps, e.g. <00:00:01.500>. These start with a digit, so the
# generic tag stripper above (which requires a letter after "<") deliberately doesn't catch them.
_INLINE_TIMESTAMP_RE = re.compile(r"<\d{1,2}:\d{2}(?::\d{2})?\.\d{3}>")

# Collapse runs of literal whitespace left behind by multi-line joins / tag stripping. This is
# deliberately NOT str.split()/join(), which would also swallow a decoded &nbsp; (U+00A0) - once
# decoded that's a real character in what the speaker said, not layout whitespace to discard.
_WHITESPACE_RE = re.compile(r"[ \t\r\n\f\v]+")

UNKNOWN_SPEAKER = "Unknown"  # attribution used when a <v> tag is present but carries no name


def _clean_cue_text(raw: str) -> str:
    """Strip inline cue markup/timestamps from one utterance and decode HTML entities.

    Tags are replaced with a space, not deleted outright - e.g. "<ruby>base<rt>ann</rt></ruby>"
    has no whitespace around the inner tag, and a bare delete would fuse it into "baseann". The
    extra whitespace this introduces elsewhere is harmless since it's collapsed right after.
    """
    text = _INLINE_TIMESTAMP_RE.sub(" ", raw)
    text = _INLINE_TAG_RE.sub(" ", text)
    text = html.unescape(text)
    return _WHITESPACE_RE.sub(" ", text).strip()


def _split_voice_spans(full_text: str) -> list[tuple[str | None, str]]:
    """Split one cue's (already line-joined) payload into (speaker, text) segments.

    A single cue can contain more than one voice span - a speaker change mid-cue, e.g.
    "<v Alice>Hi</v> <v Bob>Yo</v>" - so this returns a list, not a single segment. Text before
    the first voice tag (if any) is genuinely un-attributed and comes back with speaker None.
    """
    opens = list(_VOICE_OPEN_RE.finditer(full_text))
    if not opens:
        text = _clean_cue_text(full_text)
        return [(None, text)] if text else []

    segments: list[tuple[str | None, str]] = []
    if opens[0].start() > 0:
        preamble = _clean_cue_text(full_text[: opens[0].start()])
        if preamble:
            segments.append((None, preamble))

    for idx, m in enumerate(opens):
        end = opens[idx + 1].start() if idx + 1 < len(opens) else len(full_text)
        text = _clean_cue_text(full_text[m.end():end])
        if not text:
            continue
        # <v> / <v > (no name at all) is malformed but real - attribute it rather than crash
        # or silently drop what was said. A malformed tag can also smuggle a stray "<"/">" into
        # the captured name (e.g. "<v Alice <Manager>>Hi" - the regex's [^>]* stops at the first
        # ">" it meets, capturing "Alice <Manager") - strip those out rather than let markup leak
        # into what's supposed to be a plain speaker name.
        name = re.sub(r"[<>]", "", (m.group(1) or "")).strip() or UNKNOWN_SPEAKER
        segments.append((name, text))
    return segments


def parse_transcript_segments(text: str) -> list[tuple[str | None, str]]:
    """
    Parse a WEBVTT (or VTT-ish) transcript into (speaker, utterance) segments, in order.

    speaker is None for genuinely un-attributed text (no <v> tag found anywhere for that line/
    cue) so the caller can decide how to render it raw instead of inventing a name. Never raises.
    """
    try:
        if not text:
            return []
        if text[0] == _BOM:
            text = text[1:]
        text = text.replace("\r\n", "\n").replace("\r", "\n")
        lines = text.split("\n")
        n = len(lines)
        segments: list[tuple[str | None, str]] = []
        i = 0

        def skip_block(idx: int) -> int:
            # Consume a NOTE/STYLE/REGION block: everything up to (and including) the next
            # blank line, or EOF - whichever comes first.
            idx += 1
            while idx < n and lines[idx].strip() != "":
                idx += 1
            return idx + 1 if idx < n else idx

        while i < n:
            stripped = lines[i].strip()
            if not stripped:
                i += 1
                continue
            if _HEADER_RE.match(stripped):
                i += 1
                continue
            if _NOTE_RE.match(stripped) or stripped in _METADATA_KEYWORDS:
                # Per spec a metadata block's keyword line is never immediately followed by a
                # timing line - only a genuine cue id is. So a bare "STYLE"/"REGION"/"NOTE" line
                # sitting directly in front of a real timing line (no blank separator) is the far
                # more likely case of "someone's cue id happened to be that word", not an actual
                # metadata block - treat it as a (dropped) cue id instead of swallowing the real
                # cue that follows. This is a best-effort disambiguation, not a proof: a transcript
                # line that is genuinely a NOTE/STYLE/REGION block AND is immediately followed -
                # with no blank line - by something that looks like a timing line is inherently
                # indistinguishable from a cue id in this grammar; we deliberately favor the
                # spec-compliant cue-id reading in that ambiguous case rather than add a fragile
                # heuristic to tell them apart.
                next_line = lines[i + 1].strip() if i + 1 < n else ""
                if _TIMING_RE.match(next_line):
                    i += 1
                    continue
                i = skip_block(i)
                continue
            if _TIMING_RE.match(stripped):
                # Timing line (cue settings like "align:start position:0%" may trail it - the
                # regex only matches the leading timestamps, so any trailing settings are ignored).
                i += 1
                payload: list[str] = []
                while i < n and lines[i].strip() != "":
                    payload.append(lines[i].strip())
                    i += 1
                while i < n and lines[i].strip() == "":
                    i += 1
                # A cue's payload can span several physical lines - they belong to the same
                # speaker turn, so join them before splitting on voice tags, not one raw line each.
                segments.extend(_split_voice_spans(" ".join(payload)))
                continue
            # Not a timing line - if the *next* line is one, this line is a cue identifier
            # (numeric, "intro-1", a UUID, ...) that carries no text of its own and must not
            # leak into the transcript.
            if i + 1 < n and _TIMING_RE.match(lines[i + 1].strip()):
                i += 1
                continue
            # No surrounding cue structure at all: a stray line, or already-flattened
            # "Name: text" input. Pass it through unattributed rather than lose or mis-tag it.
            segments.append((None, stripped))
            i += 1
        return segments
    except Exception:
        # A malformed transcript must never take down the caller's pipeline.
        return [(None, text)] if isinstance(text, str) and text.strip() else []


def _merge_consecutive_speakers(
        segments: list[tuple[str | None, str]]) -> list[tuple[str | None, str]]:
    """Merge adjacent segments from the same named speaker (typically consecutive cues).

    This is the actual attribution payoff: an action item or acceptance criterion that spans
    several cues stays under one person's line instead of fragmenting at every cue boundary.
    Un-attributed (speaker None) segments are left as separate lines - there's no speaker
    identity to merge on. UNKNOWN_SPEAKER segments are treated the same way: it's a placeholder
    for "no name was given", not an actual identity, so two different unnamed speakers must not
    be fused into one "Unknown:" turn just because the label happens to match.
    """
    merged: list[tuple[str | None, str]] = []
    for speaker, text in segments:
        if (merged and speaker is not None and speaker != UNKNOWN_SPEAKER
                and merged[-1][0] == speaker):
            prev_speaker, prev_text = merged[-1]
            merged[-1] = (prev_speaker, f"{prev_text} {text}")
        else:
            merged.append((speaker, text))
    return merged


def parse_vtt(text: str) -> str:
    """Flatten a WEBVTT transcript to 'Speaker: text' lines, one per merged speaker turn.

    Un-attributable text is passed through raw (no invented speaker) rather than dropped -
    losing what was said is worse than losing who said it. Never raises.
    """
    try:
        merged = _merge_consecutive_speakers(parse_transcript_segments(text))
        lines = [f"{speaker}: {utterance}" if speaker is not None else utterance
                 for speaker, utterance in merged]
        return "\n".join(lines)
    except Exception:
        return text if isinstance(text, str) else ""


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
