// Main Bicep orchestration for Pennie the Prepper
// Deploys all infrastructure to an existing resource group
//
// Prerequisites:
//   - Resource group must be created manually before deployment
//   - See docs/DEPLOYMENT.adoc for setup instructions

// Parameters
@description('Name of the environment (dev, test, prod)')
param environmentName string = 'prod'

@description('Primary Azure region for all resources')
param location string = resourceGroup().location

@description('Name of the Azure AI Foundry Hub (existing or new)')
param aiHubName string

@description('Name of the Azure AI Foundry Project')
param aiProjectName string = 'T-Minus-15 Agents'

@description('Azure DevOps organization name')
param devOpsOrg string

@description('Azure DevOps project name')
param devOpsProject string

@description('Deploy AI services (OpenAI, Speech, AI Foundry). Set to false for test environments that share prod AI services.')
param deployAiServices bool = true

@description('Tags to apply to all resources')
param tags object = {
  Environment: environmentName
  Project: 'Pennie'
  ManagedBy: 'Bicep'
  CostCenter: 'AI-Agents'
}

// Module: Monitoring (Application Insights, Log Analytics, Storage)
module monitoring './modules/monitoring.bicep' = {
  name: 'monitoring-deployment'
  params: {
    location: location
    environmentName: environmentName
    tags: tags
  }
}

// Module: AI Services (AI Foundry, Speech Services, OpenAI)
// Optional: Test environments can share production AI services
module aiServices './modules/ai-services.bicep' = if (deployAiServices) {
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
output resourceGroupName string = resourceGroup().name
output location string = location
output applicationInsightsName string = monitoring.outputs.applicationInsightsName
output applicationInsightsConnectionString string = monitoring.outputs.applicationInsightsConnectionString
output storageAccountName string = monitoring.outputs.storageAccountName
output aiHubName string = deployAiServices ? aiServices.outputs.aiHubName : 'not-deployed'
output aiProjectName string = deployAiServices ? aiServices.outputs.aiProjectName : 'not-deployed'
output speechServiceEndpoint string = deployAiServices ? aiServices.outputs.speechServiceEndpoint : 'not-deployed'
output openAiEndpoint string = deployAiServices ? aiServices.outputs.openAiEndpoint : 'not-deployed'
output vmName string = windowsVM.outputs.vmName
output vmPublicIP string = windowsVM.outputs.vmPublicIP
output vmPrivateIP string = windowsVM.outputs.vmPrivateIP
