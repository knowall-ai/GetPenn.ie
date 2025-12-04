# Deploy Pennie Bot to Windows Server VM
# This script deploys the Teams Media Bot to the Windows VM and configures it as a Windows Service

param(
    [Parameter(Mandatory=$false)]
    [string]$BotDirectory = "C:\Pennie\bot",

    [Parameter(Mandatory=$false)]
    [string]$ServiceName = "PennieBot",

    [Parameter(Mandatory=$false)]
    [string]$KeyVaultName = "",

    [Parameter(Mandatory=$false)]
    [string]$BuildConfiguration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "=== Pennie Bot Deployment Script ===" -ForegroundColor Green
Write-Host ""

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: This script must be run as Administrator" -ForegroundColor Red
    exit 1
}

# Step 1: Stop existing service if running
Write-Host "Step 1: Stopping existing service (if running)..." -ForegroundColor Cyan
try {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -eq 'Running') {
            Write-Host "  Stopping service: $ServiceName"
            Stop-Service -Name $ServiceName -Force
            Start-Sleep -Seconds 3
        }
        Write-Host "  Removing existing service"
        # Use sc.exe as nssm might not be installed yet
        sc.exe delete $ServiceName 2>&1 | Out-Null
    }
} catch {
    Write-Host "  No existing service found (this is OK for first deployment)" -ForegroundColor Yellow
}

# Step 2: Check if bot is pre-built or needs building
Write-Host ""
Write-Host "Step 2: Preparing bot application..." -ForegroundColor Cyan

$botExePath = Join-Path $BotDirectory "PennieBot.exe"
$repoRoot = Split-Path -Parent $PSScriptRoot
$botProjectPath = Join-Path $repoRoot "bot\PennieBot.csproj"

# If PennieBot.exe already exists and source code doesn't exist, skip build (pre-built deployment)
if ((Test-Path $botExePath) -and (-not (Test-Path $botProjectPath))) {
    Write-Host "  Pre-built deployment detected - skipping build step" -ForegroundColor Green
    Write-Host "  Bot executable found at: $botExePath" -ForegroundColor Green
} elseif (Test-Path $botProjectPath) {
    # Build from source
    Write-Host "  Building from source..."

    # CRITICAL: Backup appsettings.json before build
    $appSettingsPath = Join-Path $BotDirectory "appsettings.json"
    $appSettingsBackup = Join-Path $env:TEMP "appsettings.json.backup"
    if (Test-Path $appSettingsPath) {
        Write-Host "  Backing up existing appsettings.json..."
        Copy-Item -Path $appSettingsPath -Destination $appSettingsBackup -Force
    }

    Write-Host "  Building project: $botProjectPath"
    & dotnet build $botProjectPath --configuration $BuildConfiguration --output $BotDirectory

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Build failed" -ForegroundColor Red
        exit 1
    }

    # CRITICAL: Restore appsettings.json after build
    if (Test-Path $appSettingsBackup) {
        Write-Host "  Restoring appsettings.json from backup..."
        Copy-Item -Path $appSettingsBackup -Destination $appSettingsPath -Force
        Remove-Item -Path $appSettingsBackup -Force
    }
    Write-Host "  Build successful" -ForegroundColor Green
} else {
    Write-Host "ERROR: Neither pre-built bot nor source code found" -ForegroundColor Red
    Write-Host "  Expected executable: $botExePath" -ForegroundColor Red
    Write-Host "  Or project file: $botProjectPath" -ForegroundColor Red
    exit 1
}

# Step 3: Configure appsettings from Key Vault
Write-Host ""
Write-Host "Step 3: Configuring application settings..." -ForegroundColor Cyan

if ($KeyVaultName) {
    Write-Host "  Fetching secrets from Key Vault: $KeyVaultName"

    # Fetch secrets using VM's managed identity
    $teamsAppId = az keyvault secret show --vault-name $KeyVaultName --name "TEAMS-APP-ID" --query value -o tsv
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to fetch TEAMS-APP-ID from Key Vault" -ForegroundColor Red
        Write-Host "  Ensure the secret exists and the VM has access to the Key Vault" -ForegroundColor Red
    }

    $teamsAppPassword = az keyvault secret show --vault-name $KeyVaultName --name "TEAMS-APP-PASSWORD" --query value -o tsv
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to fetch TEAMS-APP-PASSWORD from Key Vault" -ForegroundColor Red
        Write-Host "  Ensure the secret exists and the VM has access to the Key Vault" -ForegroundColor Red
    }

    $speechKey = az keyvault secret show --vault-name $KeyVaultName --name "AZURE-SPEECH-KEY" --query value -o tsv
    if ($LASTEXITCODE -ne 0) {
        Write-Host "WARNING: AZURE-SPEECH-KEY not found in Key Vault (speech features may not work)" -ForegroundColor Yellow
    }

    if (-not $teamsAppId -or -not $teamsAppPassword) {
        Write-Host "ERROR: Required secrets not found in Key Vault" -ForegroundColor Red
        Write-Host "  Please ensure TEAMS-APP-ID and TEAMS-APP-PASSWORD exist" -ForegroundColor Red
        exit 1
    }

    # Create appsettings.Production.json
    $appSettings = @{
        "TeamsAppId" = $teamsAppId
        "TeamsAppPassword" = $teamsAppPassword
        "AzureTenantId" = $env:AZURE_TENANT_ID
        "BotBaseUrl" = "https://$env:COMPUTERNAME.cloudapp.azure.com"
        "AZURE_SPEECH_KEY" = $speechKey
        "AZURE_LOCATION" = $env:AZURE_LOCATION
        "AZURE_AI_FOUNDRY_ENDPOINT" = $env:AZURE_AI_FOUNDRY_ENDPOINT
        "AZURE_AI_FOUNDRY_AGENT_ID" = $env:AZURE_AI_FOUNDRY_AGENT_ID
        "AZURE_FUNCTIONS_BACKEND_URL" = $env:AZURE_FUNCTIONS_BACKEND_URL
        "APPLICATIONINSIGHTS_CONNECTION_STRING" = $env:APPLICATIONINSIGHTS_CONNECTION_STRING
    }

    $appSettingsPath = Join-Path $BotDirectory "appsettings.Production.json"
    $appSettings | ConvertTo-Json | Set-Content -Path $appSettingsPath

    Write-Host "  Configuration saved to: $appSettingsPath" -ForegroundColor Green
} else {
    Write-Host "  WARNING: No Key Vault specified, using environment variables only" -ForegroundColor Yellow
    Write-Host "  Ensure all required environment variables are set on the VM" -ForegroundColor Yellow
}

# Step 4: Install NSSM if not present, then install bot as Windows Service
Write-Host ""
Write-Host "Step 4: Installing bot as Windows Service..." -ForegroundColor Cyan

# Check and install NSSM if needed
$nssmPath = Get-Command nssm -ErrorAction SilentlyContinue
if (-not $nssmPath) {
    Write-Host "  NSSM not found - installing..." -ForegroundColor Yellow

    $nssmZipUrl = "https://nssm.cc/release/nssm-2.24.zip"
    $nssmZipPath = Join-Path $env:TEMP "nssm.zip"
    $nssmExtractPath = Join-Path $env:TEMP "nssm"
    $nssmInstallPath = "C:\Tools\nssm"

    # Download NSSM
    Write-Host "  Downloading NSSM..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $nssmZipUrl -OutFile $nssmZipPath -UseBasicParsing

    # Extract
    Write-Host "  Extracting NSSM..."
    Expand-Archive -Path $nssmZipPath -DestinationPath $nssmExtractPath -Force

    # Copy to install location
    New-Item -ItemType Directory -Path $nssmInstallPath -Force | Out-Null
    Copy-Item -Path "$nssmExtractPath\nssm-2.24\win64\nssm.exe" -Destination $nssmInstallPath -Force

    # Add to PATH for this session
    $env:PATH = "$nssmInstallPath;$env:PATH"

    # Verify installation
    if (Test-Path "$nssmInstallPath\nssm.exe") {
        Write-Host "  NSSM installed successfully to $nssmInstallPath" -ForegroundColor Green
    } else {
        Write-Host "ERROR: NSSM installation failed" -ForegroundColor Red
        exit 1
    }

    # Cleanup
    Remove-Item -Path $nssmZipPath -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $nssmExtractPath -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "  NSSM already installed" -ForegroundColor Green
}

$botExePath = Join-Path $BotDirectory "PennieBot.exe"
if (-not (Test-Path $botExePath)) {
    Write-Host "ERROR: Bot executable not found at $botExePath" -ForegroundColor Red
    exit 1
}

Write-Host "  Installing service: $ServiceName"
& nssm install $ServiceName $botExePath

# Configure service - use & nssm to respect PATH changes
Write-Host "  Configuring service..."
& nssm set $ServiceName AppDirectory $BotDirectory
& nssm set $ServiceName AppEnvironmentExtra "ASPNETCORE_ENVIRONMENT=Production"
& nssm set $ServiceName DisplayName "Pennie the Prepper Teams Bot"
& nssm set $ServiceName Description "AI-powered Teams bot for Azure DevOps backlog creation"
& nssm set $ServiceName Start SERVICE_AUTO_START

# Create logs directory
$logsDir = "C:\Pennie\logs"
if (-not (Test-Path $logsDir)) {
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
    Write-Host "  Created logs directory: $logsDir"
}

& nssm set $ServiceName AppStdout "$logsDir\bot-stdout.log"
& nssm set $ServiceName AppStderr "$logsDir\bot-stderr.log"
& nssm set $ServiceName AppRotateFiles 1
& nssm set $ServiceName AppRotateOnline 1
& nssm set $ServiceName AppRotateBytes 10485760  # 10MB

Write-Host "  Service installed successfully" -ForegroundColor Green

# Step 5: Start the service
Write-Host ""
Write-Host "Step 5: Starting bot service..." -ForegroundColor Cyan

Start-Service -Name $ServiceName
Start-Sleep -Seconds 3

$service = Get-Service -Name $ServiceName
if ($service.Status -eq 'Running') {
    Write-Host "  Service started successfully" -ForegroundColor Green
} else {
    Write-Host "  WARNING: Service failed to start (Status: $($service.Status))" -ForegroundColor Yellow
    Write-Host "  Check logs at C:\Pennie\logs\" -ForegroundColor Yellow
}

# Step 6: Configure Windows Firewall
Write-Host ""
Write-Host "Step 6: Configuring Windows Firewall..." -ForegroundColor Cyan

$firewallRuleName = "Pennie Bot HTTPS"
$existingRule = Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue

if ($existingRule) {
    Write-Host "  Firewall rule already exists"
} else {
    Write-Host "  Creating firewall rule: $firewallRuleName"
    New-NetFirewallRule -DisplayName $firewallRuleName `
        -Direction Inbound `
        -Protocol TCP `
        -LocalPort 443 `
        -Action Allow `
        -Profile Any
    Write-Host "  Firewall rule created" -ForegroundColor Green
}

# Step 7: Verify deployment
Write-Host ""
Write-Host "Step 7: Verifying deployment..." -ForegroundColor Cyan

Write-Host "  Service Status:"
Get-Service -Name $ServiceName | Format-Table -Property Name, Status, StartType

Write-Host ""
Write-Host "  Deployed Files:"
Get-ChildItem -Path $BotDirectory -Filter "*.dll" | Select-Object -First 5 | Format-Table Name, Length, LastWriteTime

# Summary
Write-Host ""
Write-Host "=== Deployment Complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "Service Name:       $ServiceName" -ForegroundColor Cyan
Write-Host "Installation Path:  $BotDirectory" -ForegroundColor Cyan
Write-Host "Logs Directory:     C:\Pennie\logs\" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Yellow
Write-Host "1. Verify the service is running: Get-Service -Name $ServiceName"
Write-Host "2. Check logs: Get-Content C:\Pennie\logs\bot-stdout.log -Tail 50"
Write-Host "3. Test health endpoint: Invoke-WebRequest https://localhost/health"
Write-Host "4. Configure Teams App Studio with bot endpoint URL"
Write-Host ""
