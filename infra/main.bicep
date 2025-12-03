// Main Bicep orchestration for Pennie the Prepper
// Deploys all infrastructure in single Azure region

targetScope = 'subscription'

// Parameters
@description('Name of the environment (dev, test, prod)')
param environmentName string = 'prod'

@description('Primary Azure region for all resources')
param location string = 'uksouth'

@description('Name of the resource group')
param resourceGroupName string = 'TMinus15Agents'

@description('Name of the Azure AI Foundry Hub (existing or new)')
param aiHubName string

@description('Name of the Azure AI Foundry Project')
param aiProjectName string = 'T-Minus-15 Agents'

@description('Azure DevOps organization name')
param devOpsOrg string

@description('Azure DevOps project name')
param devOpsProject string

@description('Teams bot app ID (from Azure AD app registration)')
@secure()
param teamsAppId string

@description('Tags to apply to all resources')
param tags object = {
  Environment: environmentName
  Project: 'Pennie'
  ManagedBy: 'Bicep'
  CostCenter: 'AI-Agents'
}

// Resource Group
resource rg 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

// Module: Monitoring (Application Insights, Log Analytics, Storage)
module monitoring './modules/monitoring.bicep' = {
  scope: rg
  name: 'monitoring-deployment'
  params: {
    location: location
    environmentName: environmentName
    tags: tags
  }
}

// Module: Key Vault (Secrets management)
module keyVault './modules/key-vault.bicep' = {
  scope: rg
  name: 'keyvault-deployment'
  params: {
    location: location
    environmentName: environmentName
    tags: tags
    teamsAppId: teamsAppId
  }
}

// Module: AI Services (AI Foundry, Speech Services, OpenAI)
module aiServices './modules/ai-services.bicep' = {
  scope: rg
  name: 'ai-services-deployment'
  params: {
    location: location
    environmentName: environmentName
    aiHubName: aiHubName
    aiProjectName: aiProjectName
    tags: tags
  }
}

// Module: Windows VM (Teams Media Bot + Node.js MCP Server)
module windowsVM './modules/windows-vm.bicep' = {
  scope: rg
  name: 'windows-vm-deployment'
  params: {
    location: location
    environmentName: environmentName
    applicationInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
    devOpsOrg: devOpsOrg
    devOpsProject: devOpsProject
    tags: tags
  }
}

// Outputs
output resourceGroupName string = rg.name
output location string = location
output keyVaultName string = keyVault.outputs.keyVaultName
output applicationInsightsName string = monitoring.outputs.applicationInsightsName
output applicationInsightsConnectionString string = monitoring.outputs.applicationInsightsConnectionString
output storageAccountName string = monitoring.outputs.storageAccountName
output aiHubName string = aiServices.outputs.aiHubName
output aiProjectName string = aiServices.outputs.aiProjectName
output speechServiceEndpoint string = aiServices.outputs.speechServiceEndpoint
output openAiEndpoint string = aiServices.outputs.openAiEndpoint
output vmName string = windowsVM.outputs.vmName
output vmPublicIP string = windowsVM.outputs.vmPublicIP
output vmPrivateIP string = windowsVM.outputs.vmPrivateIP
