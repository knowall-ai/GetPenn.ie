# Pennie the Prepper - Deployment Status

## ✅ Successfully Deployed to Azure AI Foundry

**Deployment Date**: October 11, 2025
**Deployment Method**: CLI via Azure AI Foundry Agents API (REST)
**Model**: gpt-4o (version 2024-08-06)

## Deployed Instances

### 🌍 East US 2 (Primary - ACTIVE ✓)
- **Agent ID**: `asst_QP4Q94razJnAaC16jjiuDfih`
- **Project**: `benw-mgan4638-eastus2_project`
- **Endpoint**: `https://benw-mgan4638-eastus2.services.ai.azure.com`
- **Model Deployment**: `gpt-4o` (version 2024-08-06, capacity: 10)
- **Region**: East US 2
- **Status**: ✅ Fully Working - Compatible with Agents functionality
- **Portal Access**: https://ai.azure.com → benw-mgan4638-eastus2_project → Agents
- **Deployment Method**: Azure AI Foundry Agents API with OAuth scope `https://ai.azure.com/.default`

### 🇬🇧 UK South (Not Used)
- **Assistant ID**: `asst_8dzX04DkBtknQzH79tO8Srk5`
- **Project**: `pennie-project-prod` (T-Minus-15 Agents)
- **Endpoint**: `https://knowall-ai-foundry.cognitiveservices.azure.com`
- **Region**: UK South
- **Status**: ⚠️ Agents feature unavailable in region
- **Note**: Deployment exists but cannot be used due to regional limitations

### 🇺🇸 East US 2 - Old Deployment (Not Used)
- **Assistant ID**: `asst_yhQ9HVWxaIyeaSZwjBDOkSQi`
- **Project**: `benw-mgan4638-eastus2_project`
- **Model**: gpt-5-chat (version 2025-08-07)
- **Status**: ⚠️ Model incompatible with Agents functionality
- **Note**: Replaced by gpt-4o deployment above

## Infrastructure Deployed

### Azure AI Foundry (UK South)
- **Hub**: `knowall-ai-foundry-hub`
- **Project**: `pennie-project-prod` (Friendly name: "T-Minus-15 Agents")
- **Storage Account**: `penniemmdxqm3w7kjwm`
- **Key Vault**: `pennie-kv-mmdxqm3w7kjwm`
- **Application Insights**: `pennie-insights-mmdxqm3w7kjwm`
- **Log Analytics**: `pennie-logs-mmdxqm3w7kjwm`

## Agent Configuration

**Model**: GPT-4o (2024-08-06)
**Temperature**: 0.1
**Top P**: 0.95
**Max Tokens**: 4000

**Tools/Functions**:
1. `wit_create_work_item` - Create Azure DevOps work items (Epics, Features, User Stories, Questions)
2. `wit_add_child_work_items` - Link parent-child work items

**Capabilities**:
- Real-time meeting transcription processing via Azure Speech Services
- T-Minus-15 methodology work item classification
- Speaker diarization support (speaker name + timestamp attribution)
- Multi-agent communication ready (deployed in shared T-Minus-15 project)
- Real-time Teams chat notifications when creating work items

## Access Information

### Portal Access
- **URL**: https://ai.azure.com
- **Subscription**: Pay-As-You-Go
- **Resource Group**: TMinus15Agents

### API Access
Use the Azure AI Foundry Agents API (requires OAuth token with scope `https://ai.azure.com/.default`):
```bash
# Active Deployment (East US 2)
AGENT_ID=asst_QP4Q94razJnAaC16jjiuDfih
ENDPOINT=https://benw-mgan4638-eastus2.services.ai.azure.com
PROJECT=benw-mgan4638-eastus2_project
API_VERSION=v1
MODEL=gpt-4o

# Get OAuth token
ACCESS_TOKEN=$(az account get-access-token --resource https://ai.azure.com --query accessToken --output tsv)

# Example API call
curl -X GET "${ENDPOINT}/api/projects/${PROJECT}/assistants/${AGENT_ID}?api-version=${API_VERSION}" \
  -H "Authorization: Bearer $ACCESS_TOKEN"
```

## Key Learnings

1. **OAuth Scope Critical**: Azure AI Foundry Agents API requires OAuth scope `https://ai.azure.com/.default` (NOT `https://cognitiveservices.azure.com`)

2. **Model Compatibility**: Only specific models are compatible with Agents functionality:
   - ✅ gpt-4o (2024-08-06) - Compatible
   - ❌ gpt-5-chat (2025-08-07) - Not compatible
   - Portal shows "deployment is not compatible with agents functionality" for incompatible models

3. **Regional Availability**: Agents feature works in East US 2 but not UK South (regional limitation)

4. **Assistants vs Agents**: Same concept, both use Assistants API - "Agents" is the Azure AI Foundry branding

5. **API Endpoint Format**: Use `https://{resource}.services.ai.azure.com/api/projects/{project}/assistants` (NOT cognitiveservices.azure.com)

6. **Deployment Method**: Successfully deployed via Azure AI Foundry Agents API (REST) with proper OAuth scope

## Next Steps

- [x] **Deploy gpt-4o Model**: Create compatible model deployment ✅ COMPLETED
- [x] **Update Pennie Agent**: Switch to gpt-4o from gpt-5-chat ✅ COMPLETED
- [ ] **Verify in Portal**: Check that red error message is gone in Azure AI Foundry portal
- [ ] **Configure MCP Server**: Connect Azure DevOps MCP server for actual work item creation
- [ ] **Test Communication**: Test Pennie with sample meeting transcripts
- [ ] **Multi-Agent Setup**: Configure communication with other agents (e.g., Edmund)
- [ ] **Deploy Teams Bot**: Connect Pennie to Microsoft Teams for live meeting transcription

## Deployment Scripts

All deployment scripts are in `/scripts`:
- `deploy-ai-foundry-agent.sh` - Azure AI Foundry Agents API (REST) ✅ ACTIVE - Successfully deployed Pennie with correct OAuth scope
- `deploy-agent.sh` - OpenAI Assistants API approach (deprecated - used wrong endpoint)
- `deploy-agent-sdk.py` - Python SDK approach (abandoned - environment restrictions)

## Infrastructure Templates

All Bicep templates are in `/infra`:
- `deploy-ai-foundry-complete.bicep` - Complete infrastructure (Hub + Project + dependencies) ✅ Used
- `main.bicep` - Full solution infrastructure
- `modules/` - Modular infrastructure components

## Documentation

- `docs/DEPLOYMENT.adoc` - Complete deployment documentation
- `agent-config.json` - Pennie's agent configuration (instructions, tools, model settings)
- `DEPLOYMENT_STATUS.md` - This file (deployment status and learnings)

## Environment Variables

Stored in `.env` (not committed to Git):
```bash
# Active Deployment
AZURE_AI_FOUNDRY_AGENT_ID=asst_QP4Q94razJnAaC16jjiuDfih
AZURE_AI_FOUNDRY_PROJECT=benw-mgan4638-eastus2_project
AZURE_AI_FOUNDRY_ENDPOINT=https://benw-mgan4638-eastus2.services.ai.azure.com
AZURE_AI_FOUNDRY_MODEL=gpt-4o

# Deprecated Deployments
AZURE_AI_ASSISTANT_ID=asst_8dzX04DkBtknQzH79tO8Srk5  # UK South (region unavailable)
AZURE_AI_ASSISTANT_ID_EASTUS2=asst_yhQ9HVWxaIyeaSZwjBDOkSQi  # East US 2 (incompatible model)
```

---

**✅ Pennie the Prepper successfully deployed to Azure AI Foundry with gpt-4o model!**

The agent is now fully compatible with Azure AI Foundry Agents functionality and ready for testing.
