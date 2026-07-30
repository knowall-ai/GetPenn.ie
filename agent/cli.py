"""
Run the Preppie spine over a transcript:

    python -m agent.cli path/to/meeting.vtt                       # local file (unchanged)
    python -m agent.cli https://teams.microsoft.com/l/meetup-join/...   # live Teams meeting

If the argument looks like a Teams meeting join URL (or PREPPIE_MEETING_URL is set in the
environment), the transcript is fetched live from Microsoft Graph via agent.transcript_fetch,
using the PREPPIE_GRAPH_* credentials in agent/config.py. Otherwise the argument is read as a
local .vtt/.txt file, exactly as before. The Graph import is lazy/guarded so a local-file run
never requires Graph to be configured.

Authenticates to Azure AI Foundry with your `az login` identity (DefaultAzureCredential), so no
keys are handled here. Reads deployment settings from agent/config.py (env-overridable).
"""
import os
import sys

from azure.identity import DefaultAzureCredential, get_bearer_token_provider
from openai import AzureOpenAI

from . import config
from .spine import Backend, parse_vtt, read_transcript, run_spine

MEETING_URL_PREFIX = "https://teams.microsoft.com/"


def _is_meeting_url(arg: str) -> bool:
    return isinstance(arg, str) and arg.startswith(MEETING_URL_PREFIX)


def _resolve_transcript(arg: str, *, explicit_arg: bool = True) -> str:
    """Resolve the flattened transcript text for either input mode. Prints which mode is used.

    `explicit_arg` is True when the caller actually passed argv[1] (as opposed to `arg` being an
    empty placeholder because no argv was given at all - see main()). An explicit, non-meeting-URL
    argv always wins over PREPPIE_MEETING_URL: the env var must not silently redirect a run the
    user pointed at a specific local file.
    """
    env_meeting_url = os.environ.get("PREPPIE_MEETING_URL")

    if explicit_arg and not _is_meeting_url(arg):
        if env_meeting_url:
            print(f"[warning] PREPPIE_MEETING_URL is set but ignored in favor of the explicit "
                  f"argument {arg!r}", file=sys.stderr)
        print(f"[mode] local transcript file: {arg}")
        return read_transcript(arg)

    meeting_url = arg if _is_meeting_url(arg) else env_meeting_url
    if meeting_url:
        print(f"[mode] live Teams meeting via Microsoft Graph: {meeting_url}")
        # Lazy import: a local-.vtt-file run must work even when Graph isn't configured at all.
        from .transcript_fetch import fetch_meeting_vtt
        vtt = fetch_meeting_vtt(
            config.GRAPH_TENANT_ID, config.GRAPH_CLIENT_ID, config.GRAPH_CLIENT_SECRET,
            config.GRAPH_ORGANIZER_USER_ID, meeting_url)
        return parse_vtt(vtt)

    print(f"[mode] local transcript file: {arg}")
    return read_transcript(arg)


def main(argv: list[str]) -> int:
    meeting_url_env = os.environ.get("PREPPIE_MEETING_URL")
    explicit_arg = len(argv) >= 2
    if not explicit_arg and not meeting_url_env:
        print("usage: python -m agent.cli <transcript.vtt | Teams meeting join URL>", file=sys.stderr)
        return 2

    arg = argv[1] if explicit_arg else ""
    try:
        transcript = _resolve_transcript(arg, explicit_arg=explicit_arg)
    except Exception as e:
        print(f"error: could not fetch/read transcript: {e}", file=sys.stderr)
        return 1

    with open(config.INSTRUCTIONS_PATH, encoding="utf-8") as f:
        instructions = f.read()

    token_provider = get_bearer_token_provider(DefaultAzureCredential(), config.TOKEN_SCOPE)
    client = AzureOpenAI(azure_endpoint=config.FOUNDRY_OPENAI_ENDPOINT,
                         azure_ad_token_provider=token_provider, api_version=config.API_VERSION)
    backend = Backend(config.BACKEND_URL, config.PROJECT)

    result = run_spine(transcript, instructions, client, backend, config.MODEL,
                       on_event=lambda m: print(f"  [tool] {m}", flush=True))

    print("\n===================== PREPPIE REPLY-BACK =====================\n")
    print(result["reply_back"])
    print(f"\n[summary] created {len(result['created'])} work items in '{config.PROJECT}' "
          f"over {result['turns']} turns.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
