"""
Compile Preppie's agent instructions FROM the T-Minus-15 SKILL.md files.

This is the heart of the Foundry migration (#62): the methodology is the source of truth, not a
hand-maintained agent-config.json. We pull the canonical sections out of the `workitems` skill and
wrap them with the deployment-specific runtime contract (tools, target project, the type ->
Azure DevOps mapping for this process template, dedupe, hierarchy, reply-back, title hygiene).

The T-Minus-15 skills live in a separate repo (T-Minus-15/claude-plugins). Point SKILLS_DIR at a
checkout of it; the compiled result is committed alongside as `preppie_instructions.md` so the agent
can be deployed without that checkout, and re-compiled whenever the methodology changes:

    SKILLS_DIR=/path/to/claude-plugins/skills python agent/build_instructions.py > agent/preppie_instructions.md
"""
import os
import re

SKILLS = os.environ.get("SKILLS_DIR", os.path.expanduser("~/t-minus-15-claude-plugins/skills"))


def section(skill: str, heading: str) -> str:
    """Return the markdown block under a '## heading' (up to the next '## ') from a SKILL.md."""
    path = os.path.join(SKILLS, skill, "SKILL.md")
    with open(path, encoding="utf-8") as f:
        text = f.read()
    text = re.sub(r"^---.*?---\n", "", text, count=1, flags=re.DOTALL)  # strip frontmatter
    pat = re.compile(rf"^##\s+{re.escape(heading)}\s*$(.*?)(?=^##\s|\Z)", re.MULTILINE | re.DOTALL)
    m = pat.search(text)
    if not m:
        raise SystemExit(f"[build] section '{heading}' not found in {skill}/SKILL.md")
    return f"## {heading}\n{m.group(1).rstrip()}\n"


HEADER = """# Preppie the Prepper - agent instructions

You are **Preppie the Prepper**, the T-Minus-15 requirements/backlog agent. You turn a meeting
transcript into a clean, well-triaged Azure DevOps backlog. You are precise, never invent facts,
and you follow the T-Minus-15 methodology below (these rules are compiled from the T-Minus-15
`workitems` skill and its per-type skills - they are the source of truth).

## Your job for each run
1. Read the transcript. Enumerate every actionable item raised, with who raised it and any owner.
2. Triage each item to a type (see "The six work item types" + "Triage" below).
3. Build the backlog structure (Epic -> Feature -> User Story) when the discussion implies it, and
   attach the captured items under the right parent.
4. BEFORE creating anything, DEDUPE: call `search_work_items` (titleContains) to check the item
   doesn't already exist. Skip creation if a clear match exists; say so in your summary.
5. Create each item with `create_work_item`, then link children to parents with `link_work_items`.
6. End with a reply-back roll-up table mapping each thing raised -> what you logged (see format).

## Runtime contract (this deployment)
- Target project is **"{{PROJECT}}"** in Azure DevOps org `{{ORG}}`. Confirm with `read_projects`
  if unsure; do not assume any other project.
- You act ONLY through the provided tools. Never fabricate work item IDs or URLs - use what the
  `create_work_item` tool returns.
- **Type -> Azure DevOps mapping** (this project's Agile process template has native Task, Bug,
  Issue, Epic, Feature, User Story - but NO native Enhancement/Risk/Question type). Map as:
  | Triage type | `type` to pass | `tags` to pass |
  |---|---|---|
  | Task | `Task` | - |
  | Bug | `Bug` | - |
  | Issue | `Issue` | - |
  | Enhancement | `Task` | `["Enhancement"]` |
  | Risk | `Issue` | `["Risk"]` |
  | Question | `Issue` | `["Question"]` |
  | Epic / Feature / User Story | `Epic` / `Feature` / `User Story` | - |
  Always set the tag when the mapping requires it, so the item stays filterable as its real type.
- **Linking - attach to the nearest relevant parent.** Link every captured item to the MOST
  specific parent that fits: a User Story if one applies, otherwise its Feature, otherwise the
  Epic. A Bug/Task/Enhancement that affects a specific story must hang off that User Story, not the
  Epic, when such a story exists.
- Pass acceptance criteria ONLY on User Stories, via `acceptanceCriteria` (a list of strings), in
  AMP form (Acceptance / Measure / Proof).
- Keep descriptions rich enough that someone picking the item up cold understands it: what was
  said, who raised it, why, and what "done" looks like. Never log a bare title.

## Title hygiene (deployment-specific)
- **Do NOT prefix a title with its type** ("Risk:", "Question:", "Bug:" ...). The work item type
  and tag already convey that - the title states the thing itself.
- Otherwise follow the "Title hygiene (all types)" rules below.

## Output
When all items are logged, output ONLY the reply-back summary described in "Reply-back convention"
below: a one-line-per-item list, then the roll-up cross-reference table. Note any renames (title
hygiene) and any items you skipped as duplicates.
"""


def neutralize_interactive_tools(text: str) -> str:
    """Rewrite references to Claude-Code authoring tools out of the compiled instructions.

    The methodology skills are written for a human-in-the-loop Claude Code session and mention tools
    like `AskUserQuestion` / the `Task` subagent tool. The deployed runtime agent has NO such tools -
    only the four backend function tools - so those references just make it waste turns trying to
    call something that isn't there. Rewrite them into the runtime-appropriate behaviour: when unsure,
    log a Question work item and surface it in the reply-back for humans to resolve. Applied at
    compile time so regenerating the instructions can't reintroduce them.
    """
    # --- AskUserQuestion: the agent can't ask a human mid-run; it logs a Question instead. ---
    # Sentence-level rewrites for the known phrasings, so the result still reads naturally.
    text = re.sub(r"use\s+`AskUserQuestion`",
                  "log it as a **Question** work item (and flag it in the reply-back)", text)
    text = re.sub(r"\(`AskUserQuestion`\)",
                  "(log it as a **Question** work item and flag it in the reply-back)", text)
    # Catch-all for any remaining mention (bare or back-ticked).
    text = text.replace("`AskUserQuestion`", "a **Question** work item")
    text = text.replace("AskUserQuestion", "a Question work item")

    # --- Task subagent: the methodology says to delegate transcript-reading to a `Task` subagent,
    # but the runtime agent already has the full transcript in its prompt and no subagent tool, so
    # rewrite those to "read it yourself". Order: specific phrasings first, then a catch-all. ---
    text = re.sub(r"\s*[—-]\s*or delegate a sub-agent via `Task`\s*[—-]\s*", " ", text)
    text = re.sub(r"\*\*delegate a sub-agent via `Task`\*\* to read it", "read it carefully", text)
    text = re.sub(r"delegate a sub-agent via `Task`", "read the source material yourself", text)
    return text


def compile_instructions() -> str:
    parts = [HEADER, "\n---\n\n# Methodology (compiled from the T-Minus-15 skills)\n"]
    parts.append(section("workitems", "Safety rules (before you log anything)"))
    parts.append(section("workitems", "The six work item types"))
    parts.append(section("workitems", "Triage: deciding the type"))
    parts.append(section("workitems", "Capture flow (meetings & stand-ups)"))
    parts.append(section("workitems", "Title hygiene (all types)"))
    parts.append(section("workitems", "Writing the description"))
    parts.append(section("workitems", "Reply-back convention (important)"))
    return neutralize_interactive_tools("\n\n".join(p.strip() for p in parts))


if __name__ == "__main__":
    print(compile_instructions())
