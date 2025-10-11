// Simplified deployment: Azure AI Foundry Hub and Project only
// Does not create Azure OpenAI or Speech Services (assumes they exist)

param location string = 'uksouth'
param aiHubName string = 'knowall-ai-foundry-hub'
param aiProjectName string = 'T-Minus-15-Agents'
param tags object = {
  Environment: 'prod'
  Project: 'Pennie'
  ManagedBy: 'Bicep'
}

// Azure AI Foundry Hub (Machine Learning Workspace)
resource aiHub 'Microsoft.MachineLearningServices/workspaces@2024-04-01' = {
  name: aiHubName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: 'Basic'
    tier: 'Basic'
  }
  properties: {
    friendlyName: aiHubName
    description: 'AI Foundry Hub for Pennie the Prepper'
    publicNetworkAccess: 'Enabled'
    v1LegacyMode: false
  }
}

// Azure AI Foundry Project (connected to hub)
resource aiProject 'Microsoft.MachineLearningServices/workspaces@2024-04-01' = {
  name: 'pennie-project-prod'
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: 'Basic'
    tier: 'Basic'
  }
  properties: {
    friendlyName: aiProjectName
    description: 'AI Foundry Project for Pennie Agent'
    hubResourceId: aiHub.id
    publicNetworkAccess: 'Enabled'
  }
}

// Outputs
output aiHubName string = aiHub.name
output aiHubId string = aiHub.id
output aiProjectName string = aiProject.name
output aiProjectId string = aiProject.id
