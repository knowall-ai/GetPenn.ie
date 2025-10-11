@description('Name of the Azure Function App')
param functionAppName string = 'pennie-backend'

@description('Location for all resources')
param location string = resourceGroup().location

@description('Environment name (dev, prod)')
param environmentName string = 'prod'

@description('Azure DevOps Organization')
param devOpsOrg string

@description('Azure DevOps PAT (secured)')
@secure()
param devOpsPAT string

@description('Application Insights Connection String')
param appInsightsConnectionString string = ''

var storageAccountName = 'penniebe${uniqueString(resourceGroup().id)}'
var hostingPlanName = '${functionAppName}-plan'
var functionAppNameFull = '${functionAppName}-${environmentName}'

// Storage Account for Azure Functions
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

// App Service Plan (Consumption) - Linux for Python
resource hostingPlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: hostingPlanName
  location: location
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  kind: 'linux'
  properties: {
    reserved: true  // Required for Linux
  }
}

// Azure Function App - Linux Python
resource functionApp 'Microsoft.Web/sites@2023-01-01' = {
  name: functionAppNameFull
  location: location
  kind: 'functionapp,linux'
  properties: {
    serverFarmId: hostingPlan.id
    reserved: true  // Required for Linux
    siteConfig: {
      linuxFxVersion: 'Python|3.11'  // Required for Linux Python apps
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'WEBSITE_CONTENTSHARE'
          value: toLower(functionAppNameFull)
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'python'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'AZURE_DEVOPS_ORG'
          value: devOpsOrg
        }
        {
          name: 'AZURE_DEVOPS_PAT'
          value: devOpsPAT
        }
        {
          name: 'LOG_LEVEL'
          value: 'INFO'
        }
      ]
      cors: {
        allowedOrigins: [
          'https://ai.azure.com'
          'https://portal.azure.com'
        ]
      }
    }
    httpsOnly: true
  }
}

output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output functionAppResourceId string = functionApp.id
output witCreateWorkItemUrl string = 'https://${functionApp.properties.defaultHostName}/api/wit_create_work_item'
output witAddChildWorkItemsUrl string = 'https://${functionApp.properties.defaultHostName}/api/wit_add_child_work_items'
