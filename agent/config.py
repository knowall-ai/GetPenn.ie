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

# Microsoft Graph app-only auth (client-credentials), for fetching a Teams meeting transcript
# directly from a join URL instead of a local .vtt file - see agent/transcript_fetch.py.
GRAPH_TENANT_ID = os.environ.get("PREPPIE_GRAPH_TENANT_ID", "")
GRAPH_CLIENT_ID = os.environ.get("PREPPIE_GRAPH_CLIENT_ID", "")
GRAPH_CLIENT_SECRET = os.environ.get("PREPPIE_GRAPH_CLIENT_SECRET", "")
GRAPH_ORGANIZER_USER_ID = os.environ.get("PREPPIE_GRAPH_ORGANIZER_USER_ID", "")
