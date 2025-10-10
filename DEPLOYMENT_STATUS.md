# Pennie the Prepper - Deployment Status

## ✅ Successfully Deployed to Azure AI Foundry

**Deployment Date**: October 11, 2025
**Deployment Method**: CLI via OpenAI Assistants API

## Deployed Instances

### 🌍 East US 2 (Primary - Recommended)
- **Assistant ID**: `asst_9zbB4FWQACPTUnl9kSfJS3IN`
- **Project**: `benw-mgan4638-eastus2_project`
- **Endpoint**: `https://benw-mgan4638-eastus2.cognitiveservices.azure.com`
- **Region**: East US 2
- **Status**: ✅ Working - Agents feature available
- **Portal Access**: https://ai.azure.com → benw-mgan4638-eastus2_project → Agents

### 🇬🇧 UK South (Backup)
- **Assistant ID**: `asst_8dzX04DkBtknQzH79tO8Srk5`
- **Project**: `pennie-project-prod` (T-Minus-15 Agents)
- **Endpoint**: `https://knowall-ai-foundry.cognitiveservices.azure.com`
- **Region**: UK South
- **Status**: ⚠️ Agents feature unavailable (region limitation)
- **Note**: Can still use via API, but portal Agents tab shows error

## Infrastructure Deployed

### Azure AI Foundry (UK South)
- **Hub**: `knowall-ai-foundry-hub`
- **Project**: `pennie-project-prod` (Friendly name: "T-Minus-15 Agents")
- **Storage Account**: `penniemmdxqm3w7kjwm`
- **Key Vault**: `pennie-kv-mmdxqm3w7kjwm`
- **Application Insights**: `pennie-insights-mmdxqm3w7kjwm`
- **Log Analytics**: `pennie-logs-mmdxqm3w7kjwm`

## Agent Configuration

**Model**: GPT-4o
**Temperature**: 0.2
**Top P**: 0.95

**Tools/Functions**:
1. `wit_create_work_item` - Create Azure DevOps work items
2. `wit_add_child_work_items` - Link parent-child work items

**Capabilities**:
- Real-time meeting transcription processing
- T-Minus-15 methodology work item classification
- Speaker diarization support
- Multi-agent communication ready

## Access Information

### Portal Access
- **URL**: https://ai.azure.com
- **Subscription**: Pay-As-You-Go
- **Resource Group**: TMinus15Agents

### API Access
Use the Assistant IDs above with Azure OpenAI Assistants API:
```bash
# East US 2 (Recommended)
ASSISTANT_ID=asst_9zbB4FWQACPTUnl9kSfJS3IN
ENDPOINT=https://benw-mgan4638-eastus2.cognitiveservices.azure.com

# UK South (Backup)
ASSISTANT_ID=asst_8dzX04DkBtknQzH79tO8Srk5
ENDPOINT=https://knowall-ai-foundry.cognitiveservices.azure.com
```

## Key Learnings

1. **Assistants vs Agents**: In Azure AI Foundry portal, "Assistants" and "Agents" refer to the same thing (deployed via Assistants API)

2. **Regional Availability**: Agents feature works in East US 2 but not UK South due to connected Azure OpenAI resource configuration

3. **Deployment Method**: Successfully deployed via CLI using OpenAI Assistants API endpoint (`/openai/assistants?api-version=2024-12-01-preview`)

4. **Portal Registration**: Projects need to be registered with AI Foundry service - this happens automatically when created in portal or via proper infrastructure deployment

## Next Steps

- [ ] **Verify in Portal**: Check that Pennie appears in https://ai.azure.com → benw-mgan4638-eastus2_project → Agents
- [ ] **Configure MCP Server**: Connect Azure DevOps MCP server for actual work item creation
- [ ] **Test Communication**: Test Pennie with sample meeting transcripts
- [ ] **Multi-Agent Setup**: Configure communication with other agents (e.g., Edmund)
- [ ] **Deploy Teams Bot**: Connect Pennie to Microsoft Teams for live meeting transcription

## Deployment Scripts

All deployment scripts are in `/scripts`:
- `deploy-agent.sh` - Original deployment script (OpenAI Assistants API) ✅ Working
- `deploy-ai-foundry-agent.sh` - Azure AI Foundry Agents API attempt
- `deploy-agent-sdk.py` - Python SDK approach (environment restrictions)

## Infrastructure Templates

All Bicep templates are in `/infra`:
- `deploy-ai-foundry-complete.bicep` - Complete infrastructure (Hub + Project + dependencies) ✅ Used
- `main.bicep` - Full solution infrastructure
- `modules/` - Modular infrastructure components

## Documentation

- `docs/CREATE_PENNIE_IN_PORTAL.md` - Step-by-step portal creation guide
- `docs/PENNIE_AGENT_CONFIG_FOR_PORTAL.md` - Complete configuration reference
- `docs/LOCAL_TESTING.md` - Local development and testing guide
- `docs/DEPLOYMENT.adoc` - Complete deployment documentation

## Environment Variables

Stored in `.env` (not committed to Git):
```bash
AZURE_AI_ASSISTANT_ID=asst_8dzX04DkBtknQzH79tO8Srk5  # UK South
AZURE_AI_ASSISTANT_ID_EASTUS2=asst_9zbB4FWQACPTUnl9kSfJS3IN  # East US 2 (Primary)
```

---

**Deployment completed successfully via CLI! 🎉**
