<#
.SYNOPSIS
    Configures bot appsettings.json with credentials and URLs.
.DESCRIPTION
    Updates the bot's appsettings.json with Teams credentials, backend URL,
    media platform settings, and Azure OpenAI settings. Includes null safety checks.
.PARAMETER ConfigPath
    Path to appsettings.json file
.PARAMETER TeamsAppId
    Microsoft App ID for Teams bot
.PARAMETER TeamsAppPassword
    Microsoft App Password for Teams bot
.PARAMETER VmFqdn
    Fully qualified domain name of the VM
.PARAMETER BackendUrl
    URL of the Azure Functions backend
.PARAMETER AzureOpenAiEndpoint
    Azure OpenAI endpoint URL for Pennie AI (optional)
.PARAMETER AzureOpenAiAssistantId
    Azure OpenAI Assistant ID for Pennie AI (optional)
#>
param(
    [Parameter(Mandatory=$true)]
    [string]$ConfigPath,

    [Parameter(Mandatory=$true)]
    [string]$TeamsAppId,

    [Parameter(Mandatory=$true)]
    [string]$TeamsAppPassword,

    [Parameter(Mandatory=$true)]
    [string]$VmFqdn,

    [Parameter(Mandatory=$true)]
    [string]$BackendUrl,

    [Parameter(Mandatory=$false)]
    [string]$AzureOpenAiEndpoint = "",

    [Parameter(Mandatory=$false)]
    [string]$AzureOpenAiAssistantId = ""
)

$ErrorActionPreference = 'Stop'

# Verify config file exists
if (-not (Test-Path $ConfigPath)) {
    Write-Error "Configuration file not found: $ConfigPath"
    exit 1
}

try {
    # Read and parse JSON
    $configContent = Get-Content $ConfigPath -Raw
    if ([string]::IsNullOrWhiteSpace($configContent)) {
        Write-Error "Configuration file is empty: $ConfigPath"
        exit 1
    }

    $config = $configContent | ConvertFrom-Json
    if ($null -eq $config) {
        Write-Error "Failed to parse JSON from: $ConfigPath"
        exit 1
    }

    Write-Host "Loaded configuration from $ConfigPath"

    # Set Teams/Bot credentials
    $config.TeamsAppId = $TeamsAppId
    $config.TeamsAppPassword = $TeamsAppPassword
    $config.MicrosoftAppId = $TeamsAppId
    $config.MicrosoftAppPassword = $TeamsAppPassword

    # Set Bot base URL
    $config.BotBaseUrl = "https://$VmFqdn"

    # Ensure MediaPlatform section exists with null safety
    if ($null -eq $config.MediaPlatform) {
        Write-Host "Creating MediaPlatform section..."
        $config | Add-Member -NotePropertyName 'MediaPlatform' -NotePropertyValue @{} -Force
    }

    # Convert to hashtable for easier manipulation if it's a PSCustomObject
    if ($config.MediaPlatform -is [PSCustomObject]) {
        $mp = @{}
        $config.MediaPlatform.PSObject.Properties | ForEach-Object { $mp[$_.Name] = $_.Value }
    } else {
        $mp = $config.MediaPlatform
    }

    $mp.ServiceFqdn = $VmFqdn
    $mp.CallNotificationUrl = "https://$VmFqdn/api/calling"
    $mp.MediaDnsName = $VmFqdn
    $mp.UseApplicationHostedMedia = $false

    # Reassign MediaPlatform
    $config.MediaPlatform = $mp

    # Set backend URL
    $config.AZURE_FUNCTIONS_BACKEND_URL = $BackendUrl

    # Set Azure OpenAI settings if provided (required for Pennie AI responses)
    if (-not [string]::IsNullOrWhiteSpace($AzureOpenAiEndpoint)) {
        # Use hyphen format as expected by PennieAgentClient.cs
        $config | Add-Member -NotePropertyName 'AZURE-OPENAI-ENDPOINT' -NotePropertyValue $AzureOpenAiEndpoint -Force
        Write-Host "  - Azure OpenAI Endpoint: $AzureOpenAiEndpoint"
    }

    if (-not [string]::IsNullOrWhiteSpace($AzureOpenAiAssistantId)) {
        $config | Add-Member -NotePropertyName 'AZURE-OPENAI-ASSISTANT-ID' -NotePropertyValue $AzureOpenAiAssistantId -Force
        Write-Host "  - Azure OpenAI Assistant ID: $($AzureOpenAiAssistantId.Substring(0, [Math]::Min(15, $AzureOpenAiAssistantId.Length)))..."
    }

    # Write back to file (depth 20 to handle deeply nested objects like Kestrel config)
    $config | ConvertTo-Json -Depth 20 | Set-Content $ConfigPath -Encoding UTF8

    Write-Host "Configuration updated successfully:"
    Write-Host "  - TeamsAppId: $($TeamsAppId.Substring(0, 8))..."
    Write-Host "  - BotBaseUrl: https://$VmFqdn"
    Write-Host "  - BackendUrl: $BackendUrl"
    Write-Host "  - MediaPlatform.ServiceFqdn: $VmFqdn"

} catch {
    Write-Error "Failed to configure bot settings: $_"
    exit 1
}
