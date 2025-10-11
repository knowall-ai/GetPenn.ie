# Azure Endpoints for Pennie the Prepper

## East US 2 Resources

### OpenAI Endpoints
- **OpenAI Resource**: `https://benw-mgan4638-eastus2.openai.azure.com/`
- **AI Services (Cognitive Services)**: `https://benw-mgan4638-eastus2.cognitiveservices.azure.com/`

### Speech Services
- **Speech to Text**: `https://eastus2.stt.speech.microsoft.com`
- **Text to Speech**: `https://eastus2.tts.speech.microsoft.com`

### AI Foundry
- **AI Foundry Project Endpoint**: `https://benw-mgan4638-eastus2.services.ai.azure.com/api/projects/benw-mgan4638-eastus2_project`
- **Project Name**: `benw-mgan4638-eastus2_project`
- **Display Name**: T-Minus-15-Agents-US

## API Access Patterns

### For OpenAI Assistants (Pennie Agent)
Use the **OpenAI Resource endpoint** (NOT the AI Services endpoint):

```bash
ENDPOINT="https://benw-mgan4638-eastus2.openai.azure.com"
API_VERSION="2024-05-01-preview"
TOKEN=$(az account get-access-token --resource https://cognitiveservices.azure.com --query accessToken --output tsv)

# List assistants
curl "$ENDPOINT/openai/assistants?api-version=$API_VERSION" \
  -H "Authorization: Bearer $TOKEN"

# Get specific assistant
curl "$ENDPOINT/openai/assistants/{assistant_id}?api-version=$API_VERSION" \
  -H "Authorization: Bearer $TOKEN"
```

### For AI Foundry Agents API
Use the **AI Foundry project endpoint**:

```bash
PROJECT_ENDPOINT="https://benw-mgan4638-eastus2.services.ai.azure.com/api/projects/benw-mgan4638-eastus2_project"
TOKEN=$(az account get-access-token --resource https://ai.azure.com --query accessToken --output tsv)

# List agents
curl "$PROJECT_ENDPOINT/agents?api-version=2024-05-01-preview" \
  -H "Authorization: Bearer $TOKEN"
```

## Important Notes

1. **OpenAI vs AI Services**:
   - OpenAI endpoint: `*.openai.azure.com` - Use this for Assistants API
   - AI Services endpoint: `*.cognitiveservices.azure.com` - Legacy endpoint

2. **Authentication**:
   - OpenAI/AI Services: `https://cognitiveservices.azure.com` resource
   - AI Foundry: `https://ai.azure.com` resource

3. **Region Support**:
   - UK South: Does NOT support Agents feature in Azure AI Foundry portal
   - East US 2: Fully supports Agents feature

## Documentation
- [Azure AI Foundry SDK Overview](https://ai.azure.com/doc/azure/ai-foundry/how-to/develop/sdk-overview?tid=f36f6414-cb7d-4545-9cf2-7574f7b5c584)
- [OpenAI Assistants API Reference](https://learn.microsoft.com/en-us/azure/ai-services/openai/reference)

## Current Deployment

### Pennie Agent
- **Agent ID**: `asst_NpRS5WvtJOW8DeWgIKz11JA8`
- **Location**: East US 2
- **Endpoint**: `https://benw-mgan4638-eastus2.openai.azure.com`
- **Functions**: 9 (all Azure DevOps backend functions)
- **Model**: gpt-4o

### Azure Functions Backend
- **URL**: `https://pennie-backend-prod.azurewebsites.net`
- **Auth**: ANONYMOUS
- **Functions**: 9 HTTP endpoints for Azure DevOps integration
