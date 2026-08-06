// Windows VM module: Teams Media Bot + Node.js MCP Server

param location string
param environmentName string
#disable-next-line no-unused-params // Used in vmExtension commandToExecute string interpolation
param applicationInsightsConnectionString string
#disable-next-line no-unused-params // Used in vmExtension commandToExecute string interpolation
param devOpsOrg string
#disable-next-line no-unused-params // Used in vmExtension commandToExecute string interpolation
param devOpsProject string
param tags object

@description('Admin username for the VM')
param adminUsername string = 'pennieadmin'

@description('Admin password for the VM - REQUIRED: Must be provided via GitHub Secrets or parameters')
@secure()
param adminPassword string

@description('Allowed source IP/CIDR for RDP access. Resolve your dynamic DNS hostname to IP before deployment. Default blocks all RDP.')
param allowedRdpSourceIP string = ''

@description('VM size for the Windows Server')
param vmSize string = 'Standard_D2s_v3' // 2 vCPU, 8 GB RAM

@description('Use Azure Spot VM for cost savings (can be evicted)')
param useSpotVM bool = false

@description('Spot VM eviction policy: Deallocate (preserve disk) or Delete')
@allowed(['Deallocate', 'Delete'])
param spotEvictionPolicy string = 'Deallocate'

@description('Max price for Spot VM (-1 = up to on-demand price)')
param spotMaxPrice int = -1

@description('Enable auto-shutdown schedule')
param enableAutoShutdown bool = false

@description('Auto-shutdown time in 24h format (e.g., 1900 for 7pm)')
param autoShutdownTime string = '1900'

@description('Auto-shutdown timezone')
param autoShutdownTimezone string = 'GMT Standard Time'

// NOTE: If you need to grant VM access to an existing Azure OpenAI resource, use Azure CLI after deployment:
// az role assignment create --assignee <vm-principal-id> --role "Cognitive Services OpenAI Contributor" --scope <openai-resource-id>

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
// RDP rule is only created if allowedRdpSourceIP is provided (security best practice)
resource nsg 'Microsoft.Network/networkSecurityGroups@2023-05-01' = {
  name: 'pennie-nsg-${environmentName}'
  location: location
  tags: tags
  properties: {
    securityRules: concat([
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
        name: 'AllowHTTP'
        properties: {
          priority: 110
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '80'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
          description: 'Required for ACME HTTP-01 challenge (SSL certificate)'
        }
      }
    ], !empty(allowedRdpSourceIP) ? [
      {
        name: 'AllowRDP'
        properties: {
          priority: 200
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '3389'
          sourceAddressPrefix: allowedRdpSourceIP
          destinationAddressPrefix: '*'
        }
      }
    ] : [])
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
  tags: union(tags, useSpotVM ? { SpotVM: 'true' } : {})
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    hardwareProfile: {
      vmSize: vmSize
    }
    // Spot VM configuration (60-80% cost savings, can be evicted)
    priority: useSpotVM ? 'Spot' : 'Regular'
    evictionPolicy: useSpotVM ? spotEvictionPolicy : null
    billingProfile: useSpotVM ? {
      maxPrice: spotMaxPrice
    } : null
    osProfile: {
      computerName: 'pennie-${environmentName}'
      adminUsername: adminUsername
      adminPassword: adminPassword
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

// Auto-shutdown schedule (saves costs by stopping VM outside business hours)
resource autoShutdownSchedule 'Microsoft.DevTestLab/schedules@2018-09-15' = if (enableAutoShutdown) {
  name: 'shutdown-computevm-${vm.name}'
  location: location
  tags: tags
  properties: {
    status: 'Enabled'
    taskType: 'ComputeVmShutdownTask'
    dailyRecurrence: {
      time: autoShutdownTime
    }
    timeZoneId: autoShutdownTimezone
    targetResourceId: vm.id
    notificationSettings: {
      status: 'Disabled'
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

// Grant VM Managed Identity access to Azure OpenAI (if existing resource provided)
// NOTE: Cross-scope role assignment for OpenAI must be done via Azure CLI after deployment:
// az role assignment create \
//   --assignee <vm-principal-id> \
//   --role "Cognitive Services OpenAI Contributor" \
//   --scope <openai-resource-id>
// This is because Bicep doesn't support cross-resource-group role assignments in the same deployment.

// Outputs
output vmName string = vm.name
output vmId string = vm.id
output vmPublicIP string = publicIP.properties.ipAddress
output vmPrivateIP string = nic.properties.ipConfigurations[0].properties.privateIPAddress
output vmFQDN string = publicIP.properties.dnsSettings.fqdn
output vmPrincipalId string = vm.identity.principalId
output isSpotVM bool = useSpotVM
output autoShutdownEnabled bool = enableAutoShutdown
