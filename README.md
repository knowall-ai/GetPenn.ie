# Pennie the Prepper

Pennie is an AI-powered business analyst that joins Microsoft Teams meetings as a real-time participant. She listens to conversations using advanced speech-to-text with speaker diarization, identifies requirements, asks clarification questions, and creates high-quality Epics, Features, and User Stories in Azure DevOps — all using the T-Minus-15 methodology.

Pennie is built using [Azure AI Foundry](https://learn.microsoft.com/en-us/azure/ai-services/ai-foundry/overview), Graph Communications API for real-time audio access, and Azure Speech Services for transcription with speaker identification. The entire solution is deployed via GitHub Actions and is fully defined as code, enabling secure, reproducible, and tenant-agnostic deployments.

![Pennie Social Preview](https://github.com/user-attachments/assets/fbaed48a-a129-4d28-a565-66b91b27f5f6)

## Features

### Core Capabilities
- 🎙️ **Real-time meeting participation** — Joins Teams meetings and listens to live audio
- 👥 **Speaker diarization** — Identifies who said what with speaker attribution
- 🧠 **Intelligent requirement extraction** — Recognizes Epics, Features, and User Stories from conversation
- 💬 **Clarifying questions** — Asks follow-ups in Teams chat when requirements are ambiguous
- 📊 **Structured backlog creation** — Creates work items in Epic > Feature > User Story > Acceptance Criteria format
- 🔗 **Azure DevOps integration** — Real-time work item creation via Azure Functions
- 🎯 **T-Minus-15 methodology** — Follows enterprise-grade requirements framework
- 🛡️ **Tenant-contained** — All components deployed within your Azure subscription
- 📍 **Traceability** — Every work item tagged with speaker name, timestamp, and meeting context

### Optional Advanced Features
- 🗣️ **Voice interaction** — Pennie can speak clarifying questions (text-to-speech)
- 👤 **Visual presence** — Optional Azure AI Avatar for animated meeting participant
- 📈 **Real-time notifications** — Posts links to created work items in meeting chat
- 🔄 **Backlog updates** — Can update existing work items based on meeting discussion

## Architecture

```
Microsoft Teams Meeting (Live Audio)
           ↓
┌──────────────────────────────────────────────────────┐
│  Windows Server VM                                   │
│  ┌────────────────────────────────────────────────┐  │
│  │ Teams Media Bot (C#)                           │  │
│  │ - Graph Communications SDK                     │  │
│  │ - Real-time audio capture                      │  │
│  └────────────────┬───────────────────────────────┘  │
│                   ↓ RTP Audio                        │
│  ┌────────────────────────────────────────────────┐  │
│  │ Node.js Runtime                                │  │
│  │ - Azure DevOps MCP Server (@azure-devops/mcp)  │  │
│  └────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────┘
           ↓ Audio to Speech Services
Azure Speech Services (MeetingTranscriber + Diarization)
           ↓ Transcribed Text + Speaker Names
Azure AI Foundry Agent (Pennie with GPT-4o)
           ↓ Model Context Protocol (MCP)
Azure DevOps MCP Server (on Windows VM)
           ↓ Azure DevOps REST API
Azure DevOps Boards (Epics, Features, Stories, Questions)
```

**Simplified Single-Region Deployment**:
- **Single Windows Server VM**: Hosts both Teams Media Bot (requires Windows) and Azure DevOps MCP Server (Node.js)
- **Data Residency**: All components (AI, Speech, Storage) deployed in your chosen region
- **Co-Located Services**: Simpler deployment, lower cost, no cross-VM networking, no cross-region latency
- **Official Integration**: Uses [Microsoft's Azure DevOps MCP Server](https://github.com/microsoft/azure-devops-mcp)

**Region Selection**: GPT-4o is available in multiple Azure regions including UK South, East US 2, and others. Choose your region based on data residency requirements and compliance needs.

See [docs/SOLUTION_DESIGN.adoc](docs/SOLUTION_DESIGN.adoc) for detailed architecture documentation.

## Repository Structure

```
/
├── agent-config.json          # Pennie's AI agent configuration (system prompt, tools, model)
├── .env.example               # Environment variables template
├── .env                       # Local environment configuration (gitignored)
├── /docs/
│   ├── REQUIREMENTS.adoc      # T-Minus-15 requirements (Epic > Features > User Stories)
│   └── SOLUTION_DESIGN.adoc   # Detailed architecture, components, deployment
├── /infra/                    # Infrastructure as Code (Bicep)
│   ├── main.bicep             # Main orchestration template
│   ├── main.parameters.json   # Environment-specific parameters
│   └── /modules/              # Modular Bicep templates
│       ├── windows-vm.bicep   # Windows VM (Bot + MCP Server + Node.js)
│       ├── ai-services.bicep # AI Foundry, Speech, OpenAI
│       └── monitoring.bicep  # Application Insights, Storage
├── /bot/                      # Teams Media Bot (C# .NET)
│   ├── Program.cs             # Bot application entry point
│   ├── MediaBot.cs            # Media stream handling
│   ├── SpeechTranscriber.cs   # Azure Speech Services integration
│   └── ...
├── /mcp/                      # Azure DevOps MCP Server configuration
│   ├── mcp-config.json        # MCP server settings
│   └── README.md              # Setup instructions
├── /.github/workflows/
│   └── deploy.yml             # GitHub Actions deployment pipeline
├── requirements.txt           # Python dependencies (for Functions)
└── README.md                  # This file
```

## Prerequisites

### Azure Resources Required
- Active Azure subscription (you control the tenant)
- Azure AI Foundry Hub with GPT-4o deployment
- Azure Speech Services resource
- Azure DevOps organization and project
- Node.js 20+ (for Azure DevOps MCP Server)
- Windows Server VM (required for Graph Communications Media SDK)

### Permissions Required
- **Azure Subscription**: Contributor role (for infrastructure deployment)
- **Microsoft Entra ID**: Global Administrator or Privileged Role Administrator (for Graph API admin consent)
- **Azure DevOps**: Project Contributor access (for MCP server authentication)

### Graph API Permissions (Admin Consent Required)
The Teams bot requires these application-level permissions:
- `Calls.AccessMedia.All` — Access media streams in calls
- `Calls.JoinGroupCall.All` — Join group calls and meetings
- `OnlineMeetings.ReadWrite.All` — Read and create online meetings

## Getting Started

### 1. Clone and Configure

```bash
git clone https://github.com/benweeks/GetPenn.ie.git
cd GetPenn.ie
cp .env.example .env
```

Edit `.env` with your Azure environment details:
- Subscription ID and Tenant ID
- Resource Group name
- Azure DevOps organization and project
- Teams app credentials (after bot registration)

### 2. Deploy Infrastructure

```bash
# Login to Azure
az login

# Deploy Bicep templates
az deployment sub create \
  --location uksouth \
  --template-file infra/main.bicep \
  --parameters @infra/main.parameters.json
```

Alternatively, use GitHub Actions:
1. Configure GitHub secrets (AZURE_CREDENTIALS, AZURE_SUBSCRIPTION_ID, etc.)
2. Push to `main` branch or manually trigger workflow
3. GitHub Actions will deploy infrastructure and application

### 3. Register Bot and Grant Permissions

```bash
# Create Azure AD App Registration for bot
az ad app create \
  --display-name "Pennie the Prepper Bot" \
  --sign-in-audience AzureADMyOrg

# Grant admin consent for Graph API permissions
# (via Azure Portal: Entra ID > App Registrations > Pennie > API Permissions > Grant admin consent)
```

### 4. Deploy to Windows VM

Deployment installs both components on the Windows Server VM:

```bash
# Install Node.js 20+ (for MCP server)
choco install nodejs-lts

# Install Azure DevOps MCP Server globally
npm install -g @azure-devops/mcp

# Deploy Teams Media Bot (C# application)
# (Automated via GitHub Actions)
```

The MCP server provides these work item tools to Pennie:
- `wit_create_work_item` - Create Epics, Features, Stories, Questions
- `wit_update_work_item` - Update existing work items
- `wit_add_child_work_items` - Create parent-child hierarchy
- `wit_add_work_item_comment` - Add speaker attribution metadata

Learn more: [Azure DevOps MCP Server Documentation](https://github.com/microsoft/azure-devops-mcp)

### 5. Test Pennie

1. Create a Teams meeting
2. Invite Pennie to the meeting (via meeting options or @mention)
3. Pennie joins and begins listening
4. Discuss requirements — Pennie will create work items in real-time
5. Check Teams chat for links to created DevOps items

## Usage

### During Meetings

**Pennie automatically:**
- Listens to all conversation audio
- Transcribes speech with speaker identification
- Identifies requirements (Epics, Features, Stories)
- Creates work items in Azure DevOps
- Posts links in Teams chat

**Example Interaction:**

```
[Meeting Audio]
Ben: "We need an epic for the customer portal with SSO integration"
Sarah: "Should we support OAuth 2.0 and SAML?"

[Pennie in Teams Chat]
✓ Created Epic #500: Customer Portal with SSO Integration [link]
❓ Question #501: Which SSO protocols should be supported? OAuth 2.0, SAML, or both?

[Meeting Audio]
Ben: "Let's support both OAuth and SAML"

[Pennie in Teams Chat]
✓ Updated Epic #500: Added acceptance criteria for OAuth 2.0 and SAML support [link]
✓ Created Feature #502: OAuth 2.0 Authentication [link]
✓ Created Feature #503: SAML Authentication [link]
```

### Reviewing Work Items

After the meeting:
1. Open Azure DevOps project
2. Navigate to Boards
3. View newly created work items with:
   - Speaker attribution tags
   - Meeting timestamp metadata
   - Links to parent items
   - Given/When/Then acceptance criteria

## Configuration

### Agent Behavior

Edit `agent-config.json` to customize:
- System instructions and persona
- Temperature and model parameters (default: 0.1 for consistent output)
- T-Minus-15 methodology rules
- MCP server integration settings
- Security and filtering settings

### MCP Server Configuration

The Azure DevOps MCP Server is configured in `mcp/mcp.json`:

```json
{
  "mcpServers": {
    "azure-devops": {
      "command": "npx",
      "args": ["-y", "@azure-devops/mcp", "${AZURE_DEVOPS_ORG}"],
      "domains": ["work-items"],
      "env": {
        "AZURE_DEVOPS_ORG": "${AZURE_DEVOPS_ORG}",
        "AZURE_DEVOPS_PROJECT": "${AZURE_DEVOPS_PROJECT}"
      }
    }
  }
}
```

**Resources**:
- [Azure DevOps MCP Server GitHub](https://github.com/microsoft/azure-devops-mcp)
- [MCP Protocol Specification](https://modelcontextprotocol.io/)
- [Azure AI Foundry MCP Integration Guide](https://learn.microsoft.com/en-us/azure/ai-foundry/agents/how-to/tools/model-context-protocol)

### Environment Variables

Key variables in `.env`:
```bash
# Azure Environment
AZURE_SUBSCRIPTION_ID=your-subscription-id
AZURE_TENANT_ID=your-tenant-id
AZURE_RESOURCE_GROUP=TMinus15Agents

# Azure AI Foundry
AZURE_AI_HUB_NAME=knowall-ai-foundry
AZURE_AI_PROJECT_NAME=T-Minus-15 Agents

# Azure DevOps
AZURE_DEVOPS_ORG=your-org
AZURE_DEVOPS_PROJECT=your-project
AZURE_DEVOPS_PAT=your-pat-token

# Teams Bot
TEAMS_APP_ID=your-app-id
TEAMS_APP_PASSWORD=your-app-password
```

## Cost Estimation

**Monthly Costs (Approximate):**
- Windows Server VM (D2s_v3): ~$70-100 (Teams Bot + Node.js MCP Server)
- Azure Speech Services: ~$1/hour of meetings (~$30-150/month)
- Azure OpenAI GPT-4o: ~$10-30 (50-70% cheaper than GPT-4 Turbo)
- Storage & Monitoring: ~$10-20

**Total: $110-270/month** for typical internal team usage

**Why Windows Server?**
- Required for Teams Media Bot (Graph Communications SDK is Windows-only)
- Node.js MCP server co-located for simplicity (works fine on Windows)
- Simpler deployment, lower cost than separate Linux container

**Region Selection Considerations:**
- **Data Residency**: Choose region based on GDPR/compliance requirements (e.g., UK South for UK data, West Europe for EU)
- **GPT-4o Availability**: Available in UK South, East US 2, Sweden Central, and other regions (verify current availability)
- **Single-Region Architecture**: Deploy all components in same region to minimize latency and data transfer costs
- **Cost Optimization**: No inter-region data transfer fees when using single region

## Security & Compliance

- ✅ **Data residency**: All components deployed within your chosen Azure region
- ✅ **Tenant isolation**: Audio never leaves your Azure tenant boundary
- ✅ **Single-region architecture**: Entire processing pipeline in one region (configurable)
- ✅ **Encryption**: Audio processed in real-time, not stored long-term
- ✅ **GDPR compliant**: Speaker consent, right to deletion, configurable data locality
- ✅ **Managed Identity**: Secure service-to-service authentication
- ✅ **Key Vault**: Secrets and credentials securely stored

## Troubleshooting

### Bot doesn't join meeting
- Check bot app ID and password in Key Vault
- Verify Graph API permissions granted (admin consent)
- Check Windows VM is running and bot service is started

### No transcription/work items created
- Verify Azure Speech Services is provisioned
- Check Application Insights logs for errors
- Ensure meeting audio quality is good
- Verify Azure DevOps PAT has required permissions

### Permission errors
- Ensure admin consent granted for Graph API permissions
- Verify Managed Identity has DevOps access
- Check bot service account has required roles

## Roadmap

### Phase 1: MVP (Current)
- [x] Real-time meeting audio access
- [x] Speech-to-text with speaker diarization
- [x] Azure DevOps work item creation
- [x] Teams chat notifications
- [ ] Production deployment and testing

### Phase 2: Enhanced Features
- [ ] Text-to-speech (Pennie speaks)
- [ ] Azure AI Avatar (visual presence)
- [ ] Post-meeting summary emails
- [ ] Voice commands ("Pennie, create an epic for...")

### Phase 3: Intelligence
- [ ] Historical backlog analysis (RAG)
- [ ] Sentiment analysis during meetings
- [ ] Predictive story point estimation
- [ ] Multi-agent orchestration with Edmund

## Contributing

This is an open-source project. Contributions welcome!

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Submit a pull request

## Support

- **Documentation**: See [docs/](docs/) folder
- **Issues**: [GitHub Issues](https://github.com/benweeks/GetPenn.ie/issues)
- **Contact**: ben.weeks@outlook.com

## License

MIT License — open-source and free to adapt.

Built by [KnowAll AI](https://www.knowall.ai) with ❤️ for better backlogs.
