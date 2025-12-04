<#
.SYNOPSIS
    Configures SSL certificate for Pennie bot using Let's Encrypt.
.DESCRIPTION
    Obtains a Let's Encrypt certificate using win-acme and configures Kestrel.
    Requires LE_EMAIL secret to be configured. Fails if certificate cannot be obtained.
.PARAMETER Fqdn
    Fully qualified domain name for the certificate
.PARAMETER Email
    Email address for Let's Encrypt notifications (required)
.PARAMETER CertPath
    Path to export PFX certificate (default: C:\Pennie\certs\pennie.pfx)
#>
param(
    [Parameter(Mandatory=$true)]
    [string]$Fqdn,

    [Parameter(Mandatory=$true)]
    [string]$Email,

    [Parameter(Mandatory=$false)]
    [string]$CertPath = "C:\Pennie\certs\pennie.pfx"
)

$ErrorActionPreference = 'Stop'

# Create certs directory
$certDir = Split-Path $CertPath -Parent
if (-not (Test-Path $certDir)) {
    New-Item -ItemType Directory -Path $certDir -Force | Out-Null
}

# Generate a random password for PFX
$pfxPassword = [System.Guid]::NewGuid().ToString().Substring(0, 16)
$pfxPasswordPath = Join-Path $certDir "pfx-password.txt"

# Function to configure Kestrel with certificate
function Set-KestrelCertConfig {
    param(
        [string]$CertificatePath,
        [string]$Password
    )

    $configPath = "C:\Pennie\bot\appsettings.json"
    if (-not (Test-Path $configPath)) {
        Write-Error "appsettings.json not found at $configPath"
        return $false
    }

    $config = Get-Content $configPath -Raw | ConvertFrom-Json

    # Configure Kestrel with PFX file (no AllowInvalid - LE certs are valid)
    $kestrel = @{
        Endpoints = @{
            Https = @{
                Url = "https://0.0.0.0:443"
                Certificate = @{
                    Path = $CertificatePath
                    Password = $Password
                }
            }
            Http = @{
                Url = "http://0.0.0.0:5000"
            }
        }
    }

    $config | Add-Member -NotePropertyName 'Kestrel' -NotePropertyValue $kestrel -Force
    $config | ConvertTo-Json -Depth 20 | Set-Content $configPath -Encoding UTF8

    Write-Host "Kestrel configured with certificate: $CertificatePath"
    return $true
}

# Main logic
Write-Host "=== Let's Encrypt SSL Certificate Configuration ==="
Write-Host "FQDN: $Fqdn"
Write-Host "Email: $Email"
Write-Host "Certificate Path: $CertPath"
Write-Host ""

# Download win-acme if not present
$wacmePath = "C:\Pennie\tools\win-acme"
$wacmeExe = Join-Path $wacmePath "wacs.exe"

if (-not (Test-Path $wacmeExe)) {
    Write-Host "Downloading win-acme..."

    if (-not (Test-Path $wacmePath)) {
        New-Item -ItemType Directory -Path $wacmePath -Force | Out-Null
    }

    $wacmeUrl = "https://github.com/win-acme/win-acme/releases/download/v2.2.9.1701/win-acme.v2.2.9.1701.x64.pluggable.zip"
    $zipPath = Join-Path $wacmePath "win-acme.zip"

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $wacmeUrl -OutFile $zipPath -UseBasicParsing
    Expand-Archive -Path $zipPath -DestinationPath $wacmePath -Force
    Remove-Item $zipPath -Force
    Write-Host "win-acme downloaded successfully"
}

# Stop the bot service temporarily to free port 443 and use port 80 for HTTP-01
$serviceName = "PennieBot"
$serviceWasRunning = $false

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -eq 'Running') {
    Write-Host "Stopping $serviceName service for certificate request..."
    Stop-Service -Name $serviceName -Force
    $serviceWasRunning = $true
    Start-Sleep -Seconds 5
}

try {
    # Run win-acme with HTTP-01 validation
    $wacmeArgs = @(
        "--target", "manual",
        "--host", $Fqdn,
        "--validation", "selfhosting",
        "--store", "pemfiles,pfxfile",
        "--pemfilespath", $certDir,
        "--pfxfilepath", $certDir,
        "--pfxfilename", "pennie",
        "--accepttos",
        "--emailaddress", $Email,
        "--pfxpassword", $pfxPassword,
        "--force"
    )

    Write-Host "Running: $wacmeExe $($wacmeArgs -join ' ')"
    $result = & $wacmeExe $wacmeArgs 2>&1
    $exitCode = $LASTEXITCODE

    Write-Host "win-acme output:"
    Write-Host $result

    # Check if PFX certificate was created
    $pfxFile = Join-Path $certDir "pennie.pfx"
    if (-not (Test-Path $pfxFile)) {
        # Try alternative naming
        $pfxFile = Join-Path $certDir "$Fqdn.pfx"
    }

    if ((Test-Path $pfxFile) -or $exitCode -eq 0) {
        Write-Host "Let's Encrypt certificate obtained successfully!"

        # Copy to expected path if different
        if ($pfxFile -ne $CertPath -and (Test-Path $pfxFile)) {
            Copy-Item $pfxFile $CertPath -Force
        }

        # Import into Windows cert store
        $cert = Import-PfxCertificate -FilePath $CertPath -CertStoreLocation Cert:\LocalMachine\My -Password (ConvertTo-SecureString $pfxPassword -AsPlainText -Force)

        # Save password
        Set-Content -Path $pfxPasswordPath -Value $pfxPassword -Encoding UTF8

        # Configure Kestrel
        Set-KestrelCertConfig -CertificatePath $CertPath -Password $pfxPassword

        Write-Host ""
        Write-Host "=== Certificate Configuration Complete ==="
        Write-Host "Type: Let's Encrypt"
        Write-Host "Thumbprint: $($cert.Thumbprint)"
        Write-Host "PFX Path: $CertPath"

        # Create scheduled task for renewal
        Write-Host ""
        Write-Host "Setting up automatic renewal..."

        $taskName = "Pennie-SSL-Renewal"
        $existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($existingTask) {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        }

        $action = New-ScheduledTaskAction -Execute $wacmeExe -Argument "--renew --baseuri https://acme-v02.api.letsencrypt.org/"
        $trigger = New-ScheduledTaskTrigger -Daily -At "03:00"
        $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest

        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Description "Renew Let's Encrypt certificate for Pennie bot"

        Write-Host "Scheduled task '$taskName' created for daily renewal check"
        exit 0
    } else {
        Write-Error "Failed to obtain Let's Encrypt certificate. Check that port 80 is open and DNS is configured."
        exit 1
    }
} finally {
    # Restart service if it was running
    if ($serviceWasRunning) {
        Write-Host "Restarting $serviceName service..."
        Start-Service -Name $serviceName
    }
}
