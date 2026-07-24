@description('Name of the Azure Function App')
param functionAppName string = 'preppie-backend'

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

@description('Python version for the Flex Consumption runtime')
param pythonVersion string = '3.11'

var storageAccountName = 'preppiebe${uniqueString(resourceGroup().id)}'
var hostingPlanName = '${functionAppName}-plan'
var functionAppNameFull = '${functionAppName}-${environmentName}'
var deploymentContainerName = 'app-package'

// Storage Account for Azure Functions (deployment package + runtime state)
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

// Blob container that Flex Consumption uses to hold the deployed app package.
resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: '${storageAccount.name}/default/${deploymentContainerName}'
}

// App Service Plan - Flex Consumption (FC1).
// NOTE: this replaces the previous Y1 "Dynamic" (classic Consumption) plan. On this subscription
// Y1 Linux Consumption fails preflight with SubscriptionIsOverQuotaForSku / "Total VMs: 0";
// Flex Consumption (FC1) is the SKU that actually provisions, and is what the app runs on today.
resource hostingPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: hostingPlanName
  location: location
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true // Linux
  }
}

// Azure Function App - Flex Consumption, Linux Python.
// Flex configures the runtime and deployment source under `functionAppConfig` rather than via
// linuxFxVersion / FUNCTIONS_* app settings (those are classic-Consumption only).
resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: functionAppNameFull
  location: location
  kind: 'functionapp,linux'
  properties: {
    serverFarmId: hostingPlan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageAccount.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'StorageAccountConnectionString'
            storageAccountConnectionStringName: 'DEPLOYMENT_STORAGE_CONNECTION_STRING'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 100
        instanceMemoryMB: 2048
      }
      runtime: {
        name: 'python'
        version: pythonVersion
      }
    }
    siteConfig: {
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'DEPLOYMENT_STORAGE_CONNECTION_STRING'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
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
  }
  dependsOn: [
    deploymentContainer
  ]
}

output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output functionAppResourceId string = functionApp.id
// Backend endpoints the Foundry agent's tools call (match src/function_app.py routes).
output createWorkItemUrl string = 'https://${functionApp.properties.defaultHostName}/api/create_work_item'
output readProjectsUrl string = 'https://${functionApp.properties.defaultHostName}/api/read_projects'
