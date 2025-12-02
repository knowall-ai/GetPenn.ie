// Windows VM module: Teams Media Bot + Node.js MCP Server

param location string
param environmentName string
param keyVaultName string
param applicationInsightsConnectionString string
param devOpsOrg string
param devOpsProject string
param tags object

@description('Admin username for the VM')
param adminUsername string = 'pennieadmin'

@description('VM size for the Windows Server')
param vmSize string = 'Standard_D2s_v3' // 2 vCPU, 8 GB RAM

@description('Resource ID of an existing Azure OpenAI resource for RBAC (optional, for cross-region deployments)')
param existingOpenAiResourceId string = ''

// Virtual Network
resource vnet 'Microsoft.Network/virtualNetworks@2023-05-01' = {
  name: 'pennie-vnet-${environmentName}'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.0.0.0/16'
      ]
    }
    subnets: [
      {
        name: 'default'
        properties: {
          addressPrefix: '10.0.1.0/24'
          networkSecurityGroup: {
            id: nsg.id
          }
        }
      }
    ]
  }
}

// Network Security Group
resource nsg 'Microsoft.Network/networkSecurityGroups@2023-05-01' = {
  name: 'pennie-nsg-${environmentName}'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'AllowHTTPS'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
      {
        name: 'AllowRDP'
        properties: {
          priority: 200
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '3389'
          sourceAddressPrefix: '*' // Restrict to your IP in production
          destinationAddressPrefix: '*'
        }
      }
    ]
  }
}

// Public IP Address
resource publicIP 'Microsoft.Network/publicIPAddresses@2023-05-01' = {
  name: 'pennie-pip-${environmentName}'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
    dnsSettings: {
      domainNameLabel: 'pennie-${environmentName}-${uniqueString(resourceGroup().id)}'
    }
  }
}

// Network Interface
resource nic 'Microsoft.Network/networkInterfaces@2023-05-01' = {
  name: 'pennie-nic-${environmentName}'
  location: location
  tags: tags
  properties: {
    ipConfigurations: [
      {
        name: 'ipconfig1'
        properties: {
          subnet: {
            id: vnet.properties.subnets[0].id
          }
          privateIPAllocationMethod: 'Dynamic'
          publicIPAddress: {
            id: publicIP.id
          }
        }
      }
    ]
    networkSecurityGroup: {
      id: nsg.id
    }
  }
}

// Windows Server VM
resource vm 'Microsoft.Compute/virtualMachines@2023-09-01' = {
  name: 'pennie-vm-${environmentName}'
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    hardwareProfile: {
      vmSize: vmSize
    }
    osProfile: {
      computerName: 'pennie-${environmentName}'
      adminUsername: adminUsername
      adminPassword: 'P@ssw0rd!${uniqueString(resourceGroup().id)}' // Change in production via Key Vault
      windowsConfiguration: {
        enableAutomaticUpdates: true
        provisionVMAgent: true
        patchSettings: {
          patchMode: 'AutomaticByPlatform'
          automaticByPlatformSettings: {
            rebootSetting: 'IfRequired'
          }
        }
      }
    }
    storageProfile: {
      imageReference: {
        publisher: 'MicrosoftWindowsServer'
        offer: 'WindowsServer'
        sku: '2022-datacenter-azure-edition'
        version: 'latest'
      }
      osDisk: {
        name: 'pennie-osdisk-${environmentName}'
        caching: 'ReadWrite'
        createOption: 'FromImage'
        managedDisk: {
          storageAccountType: 'Premium_LRS'
        }
      }
    }
    networkProfile: {
      networkInterfaces: [
        {
          id: nic.id
        }
      ]
    }
    diagnosticsProfile: {
      bootDiagnostics: {
        enabled: true
      }
    }
  }
}

// VM Extension: Custom Script to install dependencies
resource vmExtension 'Microsoft.Compute/virtualMachines/extensions@2023-09-01' = {
  parent: vm
  name: 'InstallDependencies'
  location: location
  properties: {
    publisher: 'Microsoft.Compute'
    type: 'CustomScriptExtension'
    typeHandlerVersion: '1.10'
    autoUpgradeMinorVersion: true
    settings: {
      fileUris: []
    }
    protectedSettings: {
      commandToExecute: '''
        powershell.exe -ExecutionPolicy Bypass -Command "
          # Install Chocolatey
          Set-ExecutionPolicy Bypass -Scope Process -Force;
          [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072;
          iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'));

          # Install Node.js LTS
          choco install nodejs-lts -y;

          # Install .NET 8.0 SDK
          choco install dotnet-sdk -y;

          # Install NSSM (for running services)
          choco install nssm -y;

          # Refresh environment variables
          refreshenv;

          # Install Azure DevOps MCP Server globally
          npm install -g @azure-devops/mcp;

          # Create application directories
          New-Item -ItemType Directory -Path 'C:\\Pennie' -Force;
          New-Item -ItemType Directory -Path 'C:\\Pennie\\bot' -Force;
          New-Item -ItemType Directory -Path 'C:\\Pennie\\mcp' -Force;
          New-Item -ItemType Directory -Path 'C:\\Pennie\\logs' -Force;

          # Set environment variables
          [System.Environment]::SetEnvironmentVariable('AZURE_DEVOPS_ORG', '${devOpsOrg}', 'Machine');
          [System.Environment]::SetEnvironmentVariable('AZURE_DEVOPS_PROJECT', '${devOpsProject}', 'Machine');
          [System.Environment]::SetEnvironmentVariable('APPLICATIONINSIGHTS_CONNECTION_STRING', '${applicationInsightsConnectionString}', 'Machine');

          Write-Host 'Dependencies installed successfully';
        "
      '''
    }
  }
}

// Grant VM Managed Identity access to Key Vault
resource keyVaultReference 'Microsoft.KeyVault/vaults@2023-02-01' existing = {
  name: keyVaultName
}

resource keyVaultRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVaultReference
  name: guid(keyVaultReference.id, vm.id, 'Key Vault Secrets User')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') // Key Vault Secrets User
    principalId: vm.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Grant VM Managed Identity access to Azure OpenAI (if existing resource provided)
// Role: Cognitive Services OpenAI Contributor (a]001dd7-823b-4bf9-a81c-774440b5d111)
// Required for the bot to call Azure OpenAI APIs using managed identity
resource openAiReference 'Microsoft.CognitiveServices/accounts@2023-05-01' existing = if (!empty(existingOpenAiResourceId)) {
  name: last(split(existingOpenAiResourceId, '/'))
  scope: resourceGroup(split(existingOpenAiResourceId, '/')[2], split(existingOpenAiResourceId, '/')[4])
}

resource openAiRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(existingOpenAiResourceId)) {
  scope: openAiReference
  name: guid(existingOpenAiResourceId, vm.id, 'Cognitive Services OpenAI Contributor')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a001dd7-823b-4bf9-a81c-774440b5d111') // Cognitive Services OpenAI Contributor
    principalId: vm.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Outputs
output vmName string = vm.name
output vmId string = vm.id
output vmPublicIP string = publicIP.properties.ipAddress
output vmPrivateIP string = nic.properties.ipConfigurations[0].properties.privateIPAddress
output vmFQDN string = publicIP.properties.dnsSettings.fqdn
output vmPrincipalId string = vm.identity.principalId
