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

# Target Azure DevOps organization. This is informational only - it's interpolated into the compiled
# instructions at load time (see cli.py) so the agent can name the right org in its reply-back. The
# backend writes to the org from its OWN `AZURE_DEVOPS_ORG` setting, so keep PREPPIE_ORG in step with
# that. (PROJECT is stronger: the same config.PROJECT both scope-locks the Backend and fills the
# instructions - see cli.py - so those two genuinely cannot drift; ORG has no such cross-check.)
ORG = os.environ.get("PREPPIE_ORG", "ayush866")

# Compiled agent instructions (see build_instructions.py).
INSTRUCTIONS_PATH = os.environ.get(
    "PREPPIE_INSTRUCTIONS_PATH", os.path.join(os.path.dirname(__file__), "preppie_instructions.md"))

# AAD token scope for the Foundry data plane. The Foundry resource fronts its Responses API on the
# Azure OpenAI / Cognitive Services endpoint, so the bearer token is minted for the Cognitive
# Services scope (NOT https://ai.azure.com, which is the control-plane/management scope).
TOKEN_SCOPE = os.environ.get("PREPPIE_TOKEN_SCOPE", "https://cognitiveservices.azure.com/.default")
