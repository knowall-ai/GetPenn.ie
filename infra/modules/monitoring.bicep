// Monitoring module: Application Insights, Log Analytics, Storage Account

param location string
param environmentName string
param tags object

// Log Analytics Workspace
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: 'pennie-logs-${environmentName}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: environmentName == 'prod' ? 90 : 30
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
  }
}

// Application Insights
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'pennie-insights-${environmentName}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    RetentionInDays: environmentName == 'prod' ? 90 : 30
    IngestionMode: 'LogAnalytics'
  }
}

// Storage Account (for logs, diagnostics, backups)
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: 'penniestorage${environmentName}${uniqueString(resourceGroup().id)}'
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    encryption: {
      services: {
        blob: {
          enabled: true
        }
        file: {
          enabled: true
        }
      }
      keySource: 'Microsoft.Storage'
    }
  }
}

// Blob container for logs
resource blobContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: '${storageAccount.name}/default/logs'
  properties: {
    publicAccess: 'None'
  }
}

// Outputs
output logAnalyticsWorkspaceId string = logAnalytics.id
output applicationInsightsName string = appInsights.name
output applicationInsightsInstrumentationKey string = appInsights.properties.InstrumentationKey
output applicationInsightsConnectionString string = appInsights.properties.ConnectionString
output storageAccountName string = storageAccount.name
output storageAccountId string = storageAccount.id
