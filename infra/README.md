# Infrastructure as Code (Bicep)

This directory contains Azure Bicep templates for deploying Pennie the Prepper infrastructure.

## Structure

```
infra/
├── main.bicep                      # Main orchestration template
├── main.parameters.json            # Production parameters
├── main.parameters.test.json       # Test/staging parameters
├── modules/
│   ├── monitoring.bicep            # Application Insights, Log Analytics, Storage
│   ├── key-vault.bicep             # Azure Key Vault for secrets
│   ├── ai-services.bicep           # AI Foundry, Speech Services, OpenAI
│   └── windows-vm.bicep            # Windows Server VM with dependencies
└── README.md                       # This file
```

## Deployment

### Prerequisites

1. Azure CLI installed (`az` command)
2. Logged in to Azure: `az login`
3. Appropriate subscription selected: `az account set --subscription <subscription-id>`

### Deploy to Test Environment

```bash
az deployment sub create \
  --location uksouth \
  --template-file infra/main.bicep \
  --parameters @infra/main.parameters.test.json
```

### Deploy to Production

```bash
az deployment sub create \
  --location uksouth \
  --template-file infra/main.bicep \
  --parameters @infra/main.parameters.json
```

### Deploy with Parameter Overrides

```bash
az deployment sub create \
  --location uksouth \
  --template-file infra/main.bicep \
  --parameters @infra/main.parameters.json \
  --parameters environmentName=prod \
  --parameters aiHubName=my-custom-hub-name
```

## Parameters

### Required Parameters

| Parameter | Description | Example |
|-----------|-------------|---------|
| `environmentName` | Environment name (dev, test, prod) | `prod` |
| `location` | Azure region for all resources | `uksouth` |
| `resourceGroupName` | Name of the resource group | `TMinus15Agents` |
| `aiHubName` | Azure AI Foundry Hub name | `knowall-ai-foundry` |
| `devOpsOrg` | Azure DevOps organization | `YourOrg` |
| `devOpsProject` | Azure DevOps project name | `YourProject` |
| `teamsAppId` | Teams bot app ID (secure) | From Key Vault reference |

### Optional Parameters

| Parameter | Description | Default |
|-----------|-------------|---------|
| `aiProjectName` | AI Foundry project name | `T-Minus-15 Agents` |
| `tags` | Resource tags | See main.bicep |

## Resources Deployed

### Resource Group

- **Name**: `{resourceGroupName}` (e.g., `TMinus15Agents`)
- **Location**: `{location}` (e.g., `uksouth`)

### Monitoring Resources

- **Log Analytics Workspace**: `pennie-logs-{environmentName}`
  - Retention: 90 days (prod), 30 days (test/dev)
- **Application Insights**: `pennie-insights-{environmentName}`
  - Type: Web application monitoring
- **Storage Account**: `penniestorage{environmentName}{unique}`
  - SKU: Standard_LRS
  - Containers: `logs`

### Security Resources

- **Key Vault**: `pennie-kv-{environmentName}-{unique}`
  - Secrets: `teams-app-id`, `teams-app-password`, `devops-pat`
  - Soft delete enabled (90 days)
  - Purge protection enabled

### AI Services

- **Speech Services**: `pennie-speech-{environmentName}`
  - SKU: S0 (Standard)
  - Real-time transcription with speaker diarization
- **Azure OpenAI**: `pennie-openai-{environmentName}`
  - SKU: S0
  - Model deployment: `gpt-4o` (version 2024-08-06)
  - Capacity: 10K TPM
- **AI Foundry Hub**: `{aiHubName}`
  - SKU: Basic
  - Managed identity enabled
- **AI Foundry Project**: `pennie-project-{environmentName}`
  - Connected to AI Hub
  - Project name: `{aiProjectName}`

### Compute Resources

- **Virtual Network**: `pennie-vnet-{environmentName}`
  - Address space: `10.0.0.0/16`
  - Subnet: `default` (10.0.1.0/24)
- **Network Security Group**: `pennie-nsg-{environmentName}`
  - Inbound: HTTPS (443), RDP (3389)
- **Public IP**: `pennie-pip-{environmentName}`
  - SKU: Standard (static)
  - DNS: `pennie-{environmentName}-{unique}.{location}.cloudapp.azure.com`
- **Windows Server VM**: `pennie-vm-{environmentName}`
  - Size: Standard_D2s_v3 (2 vCPU, 8 GB RAM)
  - OS: Windows Server 2022 Datacenter Azure Edition
  - Managed identity enabled
  - Custom Script Extension: Installs Node.js, .NET 8.0, NSSM, MCP Server

## Post-Deployment Configuration

After infrastructure deployment, complete these manual steps:

### 1. Set Key Vault Secrets

The following secrets are created with placeholder values and must be updated:

```bash
# Set Teams App Password
az keyvault secret set \
  --vault-name <keyvault-name> \
  --name teams-app-password \
  --value <actual-password-from-app-registration>

# Set Azure DevOps PAT
az keyvault secret set \
  --vault-name <keyvault-name> \
  --name devops-pat \
  --value <your-devops-pat>
```

### 2. Connect to Windows VM

```bash
# Get VM public IP
az vm show \
  --resource-group TMinus15Agents \
  --name pennie-vm-prod \
  --show-details \
  --query publicIps -o tsv

# RDP to VM
mstsc /v:<public-ip>
```

### 3. Authenticate MCP Server

On the Windows VM:

```powershell
# First-time MCP authentication (opens browser)
npx @azure-devops/mcp YourOrg

# Test MCP server
npx @azure-devops/mcp YourOrg --test
```

### 4. Deploy Bot Application

Copy the compiled bot application to `C:\Pennie\bot\` and install as Windows Service (see deployment guide).

## Outputs

The deployment provides these outputs:

| Output | Description |
|--------|-------------|
| `resourceGroupName` | Name of the resource group |
| `location` | Deployment region |
| `keyVaultName` | Key Vault name |
| `applicationInsightsName` | Application Insights name |
| `applicationInsightsConnectionString` | Connection string for app telemetry |
| `storageAccountName` | Storage account name |
| `aiHubName` | AI Foundry Hub name |
| `aiProjectName` | AI Foundry Project name |
| `speechServiceEndpoint` | Azure Speech Services endpoint |
| `openAiEndpoint` | Azure OpenAI endpoint |
| `vmName` | Windows VM name |
| `vmPublicIP` | VM public IP address |
| `vmPrivateIP` | VM private IP address |

## Cost Estimation

See [main README](../README.md#cost-estimation) for detailed cost breakdown.

**Approximate monthly costs (UK South region):**
- Windows VM (D2s_v3): ~£60-85
- Speech Services (S0): ~£25-130 (usage-based)
- OpenAI GPT-4o: ~£8-25 (usage-based)
- Storage & Monitoring: ~£8-17

**Total: ~£100-240/month**

## Troubleshooting

### Deployment Fails: Resource Already Exists

If resources already exist (e.g., AI Hub), the deployment uses the existing resource. Ensure resource names in parameters match existing resources.

### Key Vault Access Denied

Ensure the deployment principal has `Key Vault Administrator` role or `Key Vault Secrets Officer` role on the Key Vault.

### VM Extension Failed

Check VM extension logs:
```bash
az vm extension show \
  --resource-group TMinus15Agents \
  --vm-name pennie-vm-prod \
  --name InstallDependencies
```

### Public IP Not Accessible

Check Network Security Group rules allow traffic from your IP address.

## Clean Up

To delete all resources:

```bash
# Delete resource group (deletes all resources)
az group delete --name TMinus15Agents --yes --no-wait

# For test environment
az group delete --name TMinus15Agents-Test --yes --no-wait
```

**Warning**: This is irreversible. Work items in Azure DevOps are NOT deleted (they're in a separate service).

## References

- [Azure Bicep Documentation](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/)
- [Azure AI Foundry Documentation](https://learn.microsoft.com/en-us/azure/ai-services/ai-foundry/)
- [Azure Speech Services Documentation](https://learn.microsoft.com/en-us/azure/cognitive-services/speech-service/)
- [Azure OpenAI Service Documentation](https://learn.microsoft.com/en-us/azure/cognitive-services/openai/)
