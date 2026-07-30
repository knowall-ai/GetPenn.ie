"""
Run the Preppie spine over a transcript file:

    python -m agent.cli path/to/meeting.vtt

Authenticates to Azure AI Foundry with your `az login` identity (DefaultAzureCredential), so no
keys are handled here. Reads deployment settings from agent/config.py (env-overridable).
"""
import sys

from azure.identity import DefaultAzureCredential, get_bearer_token_provider
from openai import AzureOpenAI

from . import config
from .spine import Backend, read_transcript, run_spine


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print("usage: python -m agent.cli <transcript.vtt>", file=sys.stderr)
        return 2

    transcript = read_transcript(argv[1])
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
