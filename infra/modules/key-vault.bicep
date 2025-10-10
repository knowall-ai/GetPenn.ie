// Key Vault module: Secrets management

param location string
param environmentName string
param tags object
@secure()
param teamsAppId string

// Key Vault
resource keyVault 'Microsoft.KeyVault/vaults@2023-02-01' = {
  name: 'pennie-kv-${environmentName}-${uniqueString(resourceGroup().id)}'
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    networkAcls: {
      defaultAction: 'Allow' // Change to 'Deny' and use private endpoints for production
      bypass: 'AzureServices'
    }
  }
}

// Store Teams App ID as secret
resource teamsAppIdSecret 'Microsoft.KeyVault/vaults/secrets@2023-02-01' = {
  parent: keyVault
  name: 'teams-app-id'
  properties: {
    value: teamsAppId
    contentType: 'text/plain'
  }
}

// Placeholder secret for Teams App Password (to be set manually or via pipeline)
resource teamsAppPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-02-01' = {
  parent: keyVault
  name: 'teams-app-password'
  properties: {
    value: 'PLACEHOLDER-SET-VIA-PIPELINE'
    contentType: 'text/plain'
  }
}

// Placeholder for Azure DevOps PAT
resource devOpsPATSecret 'Microsoft.KeyVault/vaults/secrets@2023-02-01' = {
  parent: keyVault
  name: 'devops-pat'
  properties: {
    value: 'PLACEHOLDER-SET-VIA-PIPELINE'
    contentType: 'text/plain'
  }
}

// Outputs
output keyVaultName string = keyVault.name
output keyVaultId string = keyVault.id
output keyVaultUri string = keyVault.properties.vaultUri
