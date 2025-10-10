# Pennie the Prepper 🧠📋

Pennie is a conversational AI assistant designed to help teams prepare better backlogs. She joins Microsoft Teams meetings and works silently in the background — listening, asking clarification questions in the chat, and creating high-quality Epics, Features, and User Stories in Azure DevOps, all using the T-Minus-15 methodology.

Pennie is built using [Azure AI Foundry](https://learn.microsoft.com/en-us/azure/ai-services/ai-foundry/overview) and is deployed via GitHub Actions. The agent and its supporting tools are fully defined as code, enabling secure, reproducible, and tenant-agnostic deployments.

## Features
- ✅ Works silently in Microsoft Teams meetings
- 🧠 Uses conversational context to capture business needs
- 📊 Outputs requirements in Epic > Feature > User Story > Acceptance Criteria format
- 🔗 Integrates with Azure DevOps via Azure Functions
- 🛡️ Fully contained within your Azure tenant
- 🔁 Can be extended to analyze historical DevOps backlogs in future

## Repository Structure
```
/agents/
penn.ie.yml # Pennie's agent definition (system prompt, tools, model)
/docs/
REQUIREMENTS.adoc # T-Minus-15 style requirements (Epic > Feature > User Stories)
SOLUTION_DESIGN.adoc # Architecture, tools, deployment pipeline, security, etc.
/functions/
... # Azure Functions for DevOps integration (create/read items)
/.github/workflows/
deploy.yml # GitHub Action to deploy Pennie + infra
infra/
... # Bicep or Terraform scripts to provision Azure AI Foundry
```

## Getting Started

1. **Fork this repo**
2. **Update environment-specific values** (DevOps org, Azure region, etc.)
3. **Run GitHub Action** to provision resources and deploy Pennie

## Usage
Invite Pennie to your Teams meeting. She’ll listen quietly and:
- Ask clarification questions in chat
- Create DevOps work items in real time
- Post links to backlog entries she’s created

## License
MIT — open-source and free to adapt. Built by [KnowAll AI](https://www.knowall.ai)
