// AI Services module: AI Foundry Hub, Project, Speech Services, OpenAI

param location string
param environmentName string
param aiHubName string
param aiProjectName string
param tags object

// Azure Cognitive Services Account (for Speech Services)
resource speechService 'Microsoft.CognitiveServices/accounts@2023-05-01' = {
  name: 'pennie-speech-${environmentName}'
  location: location
  tags: tags
  kind: 'SpeechServices'
  sku: {
    name: 'S0' // Standard tier for production workloads
  }
  properties: {
    customSubDomainName: 'pennie-speech-${environmentName}-${uniqueString(resourceGroup().id)}'
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
}

// Azure OpenAI Service
resource openAI 'Microsoft.CognitiveServices/accounts@2023-05-01' = {
  name: 'pennie-openai-${environmentName}'
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: 'pennie-openai-${environmentName}-${uniqueString(resourceGroup().id)}'
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
}

// GPT-4o Deployment
resource gpt4oDeployment 'Microsoft.CognitiveServices/accounts/deployments@2023-05-01' = {
  parent: openAI
  name: 'gpt-4o'
  sku: {
    name: 'Standard'
    capacity: 10 // Tokens per minute (TPM) in thousands
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o'
      version: '2024-08-06'
    }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
    raiPolicyName: 'Microsoft.Default'
  }
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
  name: 'pennie-project-${environmentName}'
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
output speechServiceName string = speechService.name
output speechServiceEndpoint string = speechService.properties.endpoint
output speechServiceId string = speechService.id
output openAiName string = openAI.name
output openAiEndpoint string = openAI.properties.endpoint
output openAiId string = openAI.id
output aiHubName string = aiHub.name
output aiHubId string = aiHub.id
output aiProjectName string = aiProject.name
output aiProjectId string = aiProject.id
