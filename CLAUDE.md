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

3. **Azure AI Foundry Agent** (Pennie)
   - GPT-4o (model version 2024-08-06) with T-Minus-15 logic (temperature: 0.1)
   - Deployed in same region as other components for optimal performance
   - Processes transcribed conversation with 100% reliable structured outputs
   - Calls Azure DevOps MCP Server via Model Context Protocol

4. **Azure DevOps MCP Server** (Node.js on Windows VM)
   - Microsoft's official MCP server (`@azure-devops/mcp`)
   - Co-located with Teams Bot for simplicity
   - Provides work item tools to AI Foundry Agent

### Key Design Decisions
- **Real-time Audio**: Graph Communications Media SDK (Windows-only requirement)
- **Speaker Identification**: Azure Speech Services MeetingTranscriber API
- **DevOps Integration**: Official Azure DevOps MCP Server (not custom Azure Functions)
- **Deployment**: Single Windows VM hosts both Bot (C#) and MCP Server (Node.js)

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
- **Subscription**: Pay-As-You-Go (SUBSCRIPTION_ID_REDACTED)
- **AI Hub**: `knowall-ai-foundry` (existing, UK South)
- **AI Project**: `T-Minus-15 Agents` (existing)
- **OpenAI Model**: GPT-4o (2024-08-06) - verified available in UK South

**Note for Other Deployers**: This is KnowAll's internal configuration. Choose your own region based on compliance needs. GPT-4o is available in UK South, East US 2, Sweden Central, and other regions.

### GitHub Actions Workflow
File: `.github/workflows/deploy.yml`

**Automated Deployment**:
1. Deploy Bicep infrastructure (Windows VM, AI services, storage, monitoring)
2. Install Node.js 20+ on Windows VM
3. Install Azure DevOps MCP Server globally (`npm install -g @azure-devops/mcp`)
4. Deploy Teams Media Bot C# application
5. Configure AI Foundry agent from `agent-config.json`
6. Run health checks and integration tests

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

## Development Workflow

### Adding New Features to Pennie
1. Update `agent-config.json` instructions if behavior changes needed
2. No code changes required for DevOps integration (uses MCP server)
3. Test locally using `test_pennie_local.py` (to be created)
4. Deploy via GitHub Actions workflow

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
