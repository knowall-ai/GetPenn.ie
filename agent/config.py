"""Deployment configuration for the Preppie spine - all overridable via environment variables."""
import os

# Azure AI Foundry resource's Azure OpenAI endpoint (Responses API lives here).
FOUNDRY_OPENAI_ENDPOINT = os.environ.get(
    "PREPPIE_FOUNDRY_OPENAI_ENDPOINT", "https://preppie-foundry-ayush866.openai.azure.com/")

# Model deployment name on that resource.
MODEL = os.environ.get("PREPPIE_MODEL", "gpt-5-mini")

# API version that exposes the Responses API.
API_VERSION = os.environ.get("PREPPIE_API_VERSION", "2025-04-01-preview")

# Deployed Azure Functions backend (the Azure DevOps tool surface).
BACKEND_URL = os.environ.get(
    "PREPPIE_BACKEND_URL", "https://preppie-backend-ayush866-prod.azurewebsites.net")

# Target Azure DevOps project.
PROJECT = os.environ.get("PREPPIE_PROJECT", "Preppie")

# Compiled agent instructions (see build_instructions.py).
INSTRUCTIONS_PATH = os.environ.get(
    "PREPPIE_INSTRUCTIONS_PATH", os.path.join(os.path.dirname(__file__), "preppie_instructions.md"))

# AAD scope for the Foundry data plane.
TOKEN_SCOPE = os.environ.get("PREPPIE_TOKEN_SCOPE", "https://cognitiveservices.azure.com/.default")

# Teams Incoming Webhook / Power Automate Workflows URL to post the meeting reply-back into.
# Empty (the default) means Teams posting is skipped entirely - no credentials required.
TEAMS_WEBHOOK_URL = os.environ.get("PREPPIE_TEAMS_WEBHOOK_URL", "")

# Optional link to the Azure DevOps board, added as an "Open board" action on the reply-back card.
BOARD_URL = os.environ.get("PREPPIE_BOARD_URL", "")
