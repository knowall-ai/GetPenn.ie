# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Pennie the Prepper is an AI-powered business analyst that joins Microsoft Teams meetings as a real-time participant, listens to live audio with speaker diarization, and creates structured backlog items in Azure DevOps using the T-Minus-15 methodology. The entire solution is defined as code for secure, reproducible deployments.

## Final Architecture (October 2025)

### Components
1. **Teams Media Bot** (C# on Windows Server VM)
   - Graph Communications SDK for real-time audio capture
   - Receives RTP audio streams at 50 frames/sec
   - Sends audio to Azure Speech Services

2. **Azure Speech Services**
   - MeetingTranscriber API with speaker diarization
   - Real-time speech-to-text transcription
   - Outputs: Speaker name + timestamp + text

3. **Azure OpenAI Assistant** (Pennie) - **OpenAI Resource Level**
   - Deployed via scripts/deploy-agent.sh
   - **Assistant ID**: `asst_6Xp8voe3wn4BnIRBqM9CPl5Y` (East US 2)
   - **OpenAI Resource API**: `https://benw-mgan4638-eastus2.openai.azure.com/openai/assistants`
   - **API Version**: `2024-05-01-preview` (for Azure.AI.OpenAI.Assistants SDK compatibility)
   - **Azure CLI Authentication**: `--resource https://cognitiveservices.azure.com`
   - GPT-4o (model version 2024-08-06) with T-Minus-15 logic (temperature: 0.1)
   - **OpenAI Assistants function calling pattern** - Pennie calls functions, application code must handle them
   - **IMPORTANT**: Uses OpenAI resource-level assistant (NOT AI Foundry project agent) for SDK compatibility
   - Functions defined: All 9 backend functions (read_projects, read_teams, read_work_item, read_work_items, read_work_item_types, read_link_types, search_work_items, create_work_item, link_work_items)
   - **Note**: AI Foundry project agents use different API path and are NOT visible to Azure.AI.OpenAI.Assistants SDK

4. **Azure Functions Backend** (Python 3.11 on Linux)
   - URL: https://pennie-backend-prod.azurewebsites.net
   - 9 HTTP endpoints for Azure DevOps integration
   - Functions: read_projects, read_teams, read_work_item, read_work_items, read_work_item_types, read_link_types, search_work_items, create_work_item, link_work_items
   - Anonymous authentication (no API keys)
   - Recursive depth support (1-5 levels) for work item hierarchies
   - 7 link types supported (not just parent-child)

5. **Function Call Handler** (Teams Bot or middleware - REQUIRED)
   - **Architecture Pattern**: OpenAI Assistants don't make HTTP requests directly
   - When Pennie calls a function (e.g., read_projects):
     1. Azure OpenAI returns "requires_action" status
     2. YOUR code must intercept this function call
     3. Call the Azure Functions backend (https://pennie-backend-prod.azurewebsites.net/api/read_projects)
     4. Submit the result back to Pennie via the Runs API
     5. Pennie then processes the result and responds
   - This handler is what makes Pennie's function calls work

### Key Design Decisions
- **Real-time Audio**: Graph Communications Media SDK (Windows-only requirement)
- **Speaker Identification**: Azure Speech Services MeetingTranscriber API
- **DevOps Integration**: Custom Azure Functions backend (9 HTTP endpoints)
- **Agent Pattern**: OpenAI Assistants with function calling (requires application handler)
- **Backend**: Linux Function App (Python 3.11) - Windows doesn't support Python properly
- **Regional**: UK South for AI services, Linux Function App can be any region

## T-Minus-15 Methodology

This project follows the T-Minus-15 framework for requirements structuring:

**Hierarchy**: Epic > Feature > User Story > Acceptance Criteria

**Work Item Types**:
- **Epics**: High-level goals with value statements, business outcome hypotheses, and delivery strategy
- **Features**: Grouped capabilities that deliver value
- **User Stories**: Specific user-facing functionality with Given/When/Then acceptance criteria
- **Questions**: Work items for ambiguous or incomplete requirements that need clarification

**Metadata Requirements**:
- Speaker attribution and timestamps for traceability
- Parent-child relationship validation
- Change history tracking
- Links to originating Teams meeting context

## Agent Configuration

The core agent is defined in [agent-config.json](agent-config.json):
- System instructions (persona, behavior, T-Minus-15 methodology)
- Model configuration (GPT-4o with model version 2024-08-06, temperature 0.1, top_p 0.95)
- Response format configured for json_schema_strict (100% reliable structured outputs)
- MCP server tool connections (Azure DevOps work items)
- Integration settings (Teams, Speech Services, DevOps)

When modifying Pennie's behavior:
- Update `instructions` in `agent-config.json` to change requirement capture logic
- Keep temperature very low (0.1) for consistent, structured output
- Never hallucinate - always use MCP tools to verify existing work items
- Speaker attribution is CRITICAL - always capture who said what with timestamps

## MCP Server Integration

Pennie uses Microsoft's official Azure DevOps MCP Server for work item operations:

**Available Tools**:
- `wit_create_work_item` - Create Epics, Features, Stories, Questions
- `wit_update_work_item` - Update existing work items
- `wit_add_child_work_items` - Create parent-child hierarchy
- `wit_get_work_item` - Retrieve work items by ID
- `wit_add_work_item_comment` - Add speaker attribution metadata

**Configuration**: See [mcp/mcp-config.json](mcp/mcp-config.json) and [mcp/README.md](mcp/README.md)

## Deployment Strategy

### Target Environment (KnowAll Ltd - Internal Deployment)
- **Resource Group**: `TMinus15Agents` (existing in KnowAll Ltd tenant)
- **Location**: `uksouth` (single-region deployment for UK data residency)
- **Subscription**: See `.env` file (not committed to Git)
- **AI Hub**: `knowall-ai-foundry` (existing, UK South)
- **AI Project**: `T-Minus-15 Agents` (existing)
- **OpenAI Model**: GPT-4o (2024-08-06) - verified available in UK South

**Note for Other Deployers**: This is KnowAll's internal configuration. Choose your own region based on compliance needs. GPT-4o is available in UK South, East US 2, Sweden Central, and other regions.

### Deployment Scripts

**Pennie Agent Deployment**:
```bash
./scripts/deploy-agent.sh
```
- Reads configuration from `agent-config.json`
- Creates OpenAI Assistant in UK South
- Returns Assistant ID (saved to .env as AZURE_AI_ASSISTANT_ID)
- Currently configured with 2 functions (wit_create_work_item, wit_add_child_work_items)

**Azure Functions Backend Deployment**:
```bash
az deployment group create \
  --resource-group TMinus15Agents \
  --template-file infra/deploy-function-app.bicep \
  --parameters functionAppName="pennie-backend" location="uksouth" environmentName="prod"
```
- Deploys Linux Function App (Python 3.11)
- **CRITICAL**: Must be Linux - Python Azure Functions don't work properly on Windows
- Sets linuxFxVersion: 'Python|3.11' in Bicep
- Configures CORS for https://ai.azure.com
- Sets auth level to ANONYMOUS (no API keys)

**Infrastructure Deployment**:
```bash
az deployment sub create \
  --location uksouth \
  --template-file infra/main.bicep \
  --parameters environmentName=prod
```
- Deploys AI Foundry Hub, Project, Storage, Key Vault, Monitoring
- Windows VM for Teams Bot (future phase)

### GitHub Actions Workflow
File: `.github/workflows/deploy.yml`

**Automated Deployment**:
1. Deploy Bicep infrastructure (AI services, storage, monitoring)
2. Deploy Azure Functions backend (Linux Function App)
3. Deploy Pennie agent via deploy-agent.sh
4. Deploy Teams Bot with function call handler (future)
5. Run health checks and integration tests

**Manual One-Time Setup** (security/compliance requirements):
1. Create Azure AD App Registration for Teams Bot
2. Grant admin consent for Graph API permissions:
   - `Calls.AccessMedia.All` - Access media streams
   - `Calls.JoinGroupCall.All` - Join group calls
   - `OnlineMeetings.ReadWrite.All` - Manage meetings
3. Configure GitHub Secrets (Azure credentials, subscription ID, tenant ID)
4. First-time MCP server authentication (OAuth browser flow on VM)

### Environment Variables
Values already configured in `.env`:
- Subscription ID, Tenant ID, Resource Group
- AI Foundry Hub and Project names
- Azure DevOps organization and project
- Teams bot app ID and credentials (after app registration)

## Security Principles

- All components must reside within the organization's Azure tenant
- No external services required
- Secrets managed via GitHub Secrets and Azure Key Vault
- Authentication uses managed identity, not PAT tokens where possible

## Repository Structure

```
/
├── agent-config.json          # Pennie AI agent configuration (MCP tools, instructions)
├── .env / .env.example        # Environment variables
├── /docs/
│   ├── REQUIREMENTS.adoc      # T-Minus-15 requirements (Epic > Features > Stories)
│   └── SOLUTION_DESIGN.adoc   # Detailed architecture, components, deployment
├── /infra/                    # Infrastructure as Code (Bicep)
│   ├── main.bicep             # Main orchestration
│   ├── main.parameters.json   # Environment-specific parameters
│   └── /modules/
│       ├── windows-vm.bicep   # Windows VM (Bot + Node.js + MCP)
│       ├── ai-services.bicep  # AI Foundry, Speech, OpenAI
│       └── monitoring.bicep   # Application Insights, Storage
├── /bot/                      # Teams Media Bot (C# .NET)
│   ├── Program.cs
│   ├── MediaBot.cs
│   └── SpeechTranscriber.cs
├── /mcp/                      # Azure DevOps MCP Server configuration
│   ├── mcp-config.json
│   └── README.md
├── /.github/workflows/
│   └── deploy.yml             # GitHub Actions deployment pipeline
└── README.md                  # Project overview and quick start
```

## Azure AI Foundry Agent API (Reference Only)

> **NOTE**: The bot currently uses an **OpenAI resource-level assistant** (`asst_6Xp8voe3wn4BnIRBqM9CPl5Y`) for SDK compatibility with `Azure.AI.OpenAI.Assistants`. The AI Foundry project agent documented below exists but is NOT used by the bot. This section is kept for reference if migrating to the AI Foundry Agents SDK in the future.

### Critical Information

**API Endpoint Structure**:
```
https://{resource-name}.services.ai.azure.com/api/projects/{project-name}/assistants/{assistant-id}?api-version=2025-05-15-preview
```

**For Pennie**:
- Resource: benw-mgan4638-eastus2
- Project: benw-mgan4638-eastus2_project
- Assistant: asst_QP4Q94razJnAaC16jjiuDfih
- Full URL: `https://benw-mgan4638-eastus2.services.ai.azure.com/api/projects/benw-mgan4638-eastus2_project/assistants/asst_QP4Q94razJnAaC16jjiuDfih?api-version=2025-05-15-preview`

**API Version**: `2025-05-15-preview`
- This is the ONLY supported API version for AI Foundry project agents
- DO NOT use older versions like 2024-05-01-preview, 2024-07-01-preview, etc.
- These will return 404 or "API version not supported" errors

**Authentication**:
```bash
# Correct resource scope for AI Foundry agents
az rest --url "<url>" --resource https://ai.azure.com --method GET

# WRONG - this is for OpenAI/Cognitive Services
az rest --url "<url>" --resource https://cognitiveservices.azure.com --method GET
```

**Managing AI Foundry Agents**:
```bash
# Get agent details
az rest --url "https://benw-mgan4638-eastus2.services.ai.azure.com/api/projects/benw-mgan4638-eastus2_project/assistants/asst_QP4Q94razJnAaC16jjiuDfih?api-version=2025-05-15-preview" \
  --resource https://ai.azure.com \
  --method GET

# Update agent (e.g., add functions)
az rest --url "https://benw-mgan4638-eastus2.services.ai.azure.com/api/projects/benw-mgan4638-eastus2_project/assistants/asst_QP4Q94razJnAaC16jjiuDfih?api-version=2025-05-15-preview" \
  --resource https://ai.azure.com \
  --method POST \
  --body @update-payload.json
```

**Key Differences from OpenAI Assistants API**:
- AI Foundry agents are PROJECT-scoped (not OpenAI resource-scoped)
- Different endpoint structure: `/api/projects/{project}/assistants` vs `/openai/assistants`
- Different authentication scope: `https://ai.azure.com` vs `https://cognitiveservices.azure.com`
- Newer API version: `2025-05-15-preview` vs older 2024-* versions
- Agents created at the OpenAI resource level are NOT visible in AI Foundry project portal
- Agents created at the AI Foundry project level ARE visible in the portal

## Critical Troubleshooting

### Pennie Calls Functions But Gets Empty Output

**Symptom**: In Azure AI Foundry playground, Pennie calls `read_projects()` but the output is empty (`output: ""`).

**Root Cause**: OpenAI Assistants use function calling pattern, not HTTP tools. When Pennie calls a function:
1. Azure OpenAI returns `requires_action` status with function call details
2. **Your application code must**:
   - Receive this function call
   - Call the Azure Functions backend (https://pennie-backend-prod.azurewebsites.net/api/read_projects)
   - Get the response (26 projects)
   - Submit the result back to Pennie via the Runs API
3. Only then can Pennie process the result and respond

**The Missing Piece**: A function call handler (Teams Bot or middleware) that intercepts Pennie's function calls and proxies them to the backend.

**Verification**:
```bash
# Test backend directly - this works:
curl https://pennie-backend-prod.azurewebsites.net/api/read_projects
# Returns: {"success": true, "count": 26, "projects": [...]}

# But Pennie gets empty output because there's no handler connecting her to the backend
```

**Solution**: Deploy the Teams Bot which includes the function call handler that:
- Monitors Pennie's runs for `requires_action` status
- Calls the appropriate backend endpoint
- Submits results back to Pennie

### Python Azure Functions Must Be on Linux

**Problem**: Function App returns HTTP 503 "Function host is not running"

**Cause**: Python Azure Functions were deployed to Windows Function App. Python requires Linux.

**Solution**: In Bicep template, set:
```bicep
kind: 'functionapp,linux'
properties: {
  reserved: true
  siteConfig: {
    linuxFxVersion: 'Python|3.11'
  }
}
```

## Development Workflow

### Adding New Features to Pennie
1. Update `agent-config.json` instructions if behavior changes needed
2. Redeploy via `./scripts/deploy-agent.sh`
3. Update function definitions in agent-config.json
4. Test via Azure AI Studio: https://ai.azure.com

### Modifying Work Item Creation Logic
- Edit `instructions` in `agent-config.json`
- Focus on decision-making rules (when to create Epic vs Feature vs Story)
- Update chat notification formats
- Keep speaker attribution and timestamps mandatory

### Testing MCP Server Integration
```powershell
# On Windows VM
npx @azure-devops/mcp ${AZURE_DEVOPS_ORG} --test
```

## Cost Optimization

**Monthly Estimate**: $110-270
- Windows VM: $70-100 (required for Media Bot)
- Speech Services: $30-150 (based on meeting hours, ~$1/hour)
- OpenAI GPT-4o: $10-30 (50-70% cheaper than GPT-4 Turbo)
- Storage & Monitoring: $10-20

**Savings**:
- GPT-4o vs GPT-4 Turbo: ~$20-40/month saved (5.5x faster, 50-70% cheaper)
- Co-located MCP server (vs separate container): ~$10-20/month saved
- Single-region deployment: No inter-region data transfer fees
- Domain-based MCP loading (only `work-items`): Faster, less memory

## Future Enhancements

### Phase 2
- Text-to-speech (Pennie speaks clarifying questions)
- Azure AI Avatar (visual presence in meetings)
- Post-meeting summary emails

### Phase 3
- Historical backlog analysis (RAG with Azure Search)
- Sentiment analysis during meetings
- Multi-agent orchestration with Edmund the Engineer
