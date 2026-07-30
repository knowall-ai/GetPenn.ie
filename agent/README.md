# Preppie spine — transcript → triaged Azure DevOps backlog

The meeting-to-backlog core. A transcript goes in; a well-triaged, deduped, hierarchical Azure
DevOps backlog comes out. This is the implementation of the Azure AI Foundry migration (#62) and
the "actually reason over the transcript" behaviour (#27).

## Architecture

```
transcript.vtt
   │  parse_vtt (speaker attribution preserved)
   ▼
run_spine ──────────────► Azure AI Foundry (Responses API, gpt-5-mini)
   ▲  function calls          │  instructions compiled from the T-Minus-15 workitems SKILL
   │  tool outputs            ▼
Backend (deployed Azure Functions) ──► Azure DevOps work items
   read_projects · search_work_items (dedupe) · create_work_item · link_work_items
```

**Why the Responses API + a thin loop (not a middleware service).** The earlier design used an
OpenAI resource-level *assistant*, whose function calls surface as a `requires_action` run that a
separate long-running handler must service — the "missing piece" the old docs describe. The Foundry
agent here uses the **Responses API**: `run_spine` is a stateless loop that receives the model's
function calls, calls the deployed backend, and feeds the results back. No extra deployed component.

**Why instructions are compiled, not hand-written.** `build_instructions.py` pulls the canonical
sections (six work item types, triage, title hygiene, dedupe, reply-back) straight out of the
T-Minus-15 `workitems` SKILL and wraps them with this deployment's runtime contract. The methodology
stays the single source of truth; regenerate when it changes.

## Model & type mapping

- Model: **gpt-5-mini** (see `config.py`). gpt-4o is not deployable on this subscription's quota;
  gpt-5-mini is current-gen and strong at tool-calling. One-line swap via `PREPPIE_MODEL`.
- The Agile process template has native Task, Bug, Issue, Epic, Feature, User Story but **no**
  Enhancement/Risk/Question type, so those map to Task/Issue **+ a tag** (`Enhancement`, `Risk`,
  `Question`) — filterable, no custom process required. Full table in `preppie_instructions.md`.

## Run it

```bash
# one-time: compile the instructions from a checkout of T-Minus-15/claude-plugins
SKILLS_DIR=/path/to/claude-plugins/skills python agent/build_instructions.py > agent/preppie_instructions.md

# run the spine over a transcript (uses your `az login` identity; no keys)
python -m agent.cli path/to/meeting.vtt
```

Configuration (all optional, see `config.py`): `PREPPIE_FOUNDRY_OPENAI_ENDPOINT`, `PREPPIE_MODEL`,
`PREPPIE_API_VERSION`, `PREPPIE_BACKEND_URL`, `PREPPIE_PROJECT`, `PREPPIE_INSTRUCTIONS_PATH`.

## Tests

```bash
python -m pytest agent/tests/ -q
```

The backend and the LLM client are injected, so the parser, tool dispatch, and the full tool loop
(dedupe-before-create, result collection, termination) are tested with no network access.

## Files

| File | Purpose |
|------|---------|
| `spine.py` | `parse_vtt`, `Backend` (tool dispatch), `run_spine` (the loop) — the testable core |
| `build_instructions.py` | compiles the system prompt from the T-Minus-15 SKILLs |
| `preppie_instructions.md` | the compiled instructions (committed artifact) |
| `config.py` | env-overridable deployment settings |
| `cli.py` | wires the real Azure client + backend and runs a transcript |
| `tests/` | unit tests + the sample transcript fixture |
