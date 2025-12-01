#!/bin/bash
set -e

# Deploy Pennie Teams Bot to Azure VM
#
# This script builds, packages, and deploys the Teams bot to the production VM.
# It reads credentials from .env and injects them into appsettings.json.
#
# Prerequisites:
# - Azure CLI logged in: az login
# - .NET SDK installed
# - Environment variables set in .env file
#
# Usage:
#     ./scripts/deploy-bot.sh
#
# Cross-platform support:
# - Works on Linux, macOS, and Windows (Git Bash/WSL)

echo "🤖 Deploying Pennie Teams Bot to Azure VM"
echo ""

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
BOT_DIR="$PROJECT_ROOT/bot"

# Use OS-appropriate temp directory (TEMP on Windows, /tmp on Unix)
TEMP_DIR="${TEMP:-/tmp}"
PUBLISH_DIR="$TEMP_DIR/pennie-bot-publish"
ZIP_FILE="$TEMP_DIR/pennie-bot-deploy.zip"

# Load environment variables from .env if available
if [ -f "$PROJECT_ROOT/.env" ]; then
    echo "📄 Loading environment from .env"
    while IFS='=' read -r key value; do
        # Skip comments and empty lines
        [[ $key =~ ^#.*$ ]] && continue
        [[ -z $key ]] && continue
        # Remove leading/trailing whitespace
        key=$(echo "$key" | xargs)
        value=$(echo "$value" | xargs)
        # Skip if key is empty after trimming
        [[ -z $key ]] && continue
        # Export the variable (without eval to prevent command injection)
        export "$key=$value"
    done < "$PROJECT_ROOT/.env"
else
    echo "❌ .env file not found at $PROJECT_ROOT/.env"
    exit 1
fi

# Validate required environment variables
# Note: Credentials are stored in Key Vault, not .env
required_vars=(
    "AZURE_RESOURCE_GROUP"
    "AZURE_KEY_VAULT_NAME"
)

missing_vars=()
for var in "${required_vars[@]}"; do
    if [ -z "${!var}" ]; then
        missing_vars+=("$var")
    fi
done

if [ ${#missing_vars[@]} -gt 0 ]; then
    echo "❌ Missing required environment variables:"
    for var in "${missing_vars[@]}"; do
        echo "   - $var"
    done
    exit 1
fi

# Configuration
VM_NAME="pennie-vm-prod"
STORAGE_ACCOUNT="penniebemmdxqm3w7kjwm"
CONTAINER_NAME="deployments"
VERSION=$(date +%Y%m%d%H%M%S)
BLOB_NAME="pennie-bot-$VERSION.zip"

echo "Configuration:"
echo "  Resource Group: $AZURE_RESOURCE_GROUP"
echo "  VM Name: $VM_NAME"
echo "  Key Vault: $AZURE_KEY_VAULT_NAME"
echo "  Version: $VERSION"
echo ""
echo "Note: Credentials loaded from Key Vault at runtime"
echo ""

# Step 1: Build the bot
echo "🔨 Building bot..."
dotnet build "$BOT_DIR/PennieBot.csproj" --configuration Release --verbosity minimal
if [ $? -ne 0 ]; then
    echo "❌ Build failed"
    exit 1
fi
echo "✅ Build successful"

# Step 2: Publish for Windows
echo ""
echo "📦 Publishing for Windows..."
rm -rf "$PUBLISH_DIR"
dotnet publish "$BOT_DIR/PennieBot.csproj" \
    -c Release \
    -r win-x64 \
    --self-contained \
    -o "$PUBLISH_DIR" \
    --verbosity minimal

if [ $? -ne 0 ]; then
    echo "❌ Publish failed"
    exit 1
fi
echo "✅ Published to $PUBLISH_DIR"

# Step 3: Update appsettings.json with Key Vault name
# Credentials are loaded from Key Vault at runtime using managed identity
echo ""
echo "🔐 Configuring Key Vault in appsettings.json..."
APPSETTINGS="$PUBLISH_DIR/appsettings.json"

# Cross-platform JSON update (works without jq)
# Check if AZURE_KEY_VAULT_NAME already exists in the file
if grep -q '"AZURE_KEY_VAULT_NAME"' "$APPSETTINGS"; then
    # Update existing key
    sed -i.bak "s|\"AZURE_KEY_VAULT_NAME\":.*|\"AZURE_KEY_VAULT_NAME\": \"$AZURE_KEY_VAULT_NAME\",|" "$APPSETTINGS"
else
    # Add key after opening brace (insert as first property)
    sed -i.bak "s|^{|{\n  \"AZURE_KEY_VAULT_NAME\": \"$AZURE_KEY_VAULT_NAME\",|" "$APPSETTINGS"
fi
rm -f "$APPSETTINGS.bak"

echo "✅ Key Vault configured: $AZURE_KEY_VAULT_NAME"
echo "   Bot will load MicrosoftAppId and MicrosoftAppPassword from Key Vault at startup"

# Step 4: Create zip archive (cross-platform)
echo ""
echo "📦 Creating deployment package..."
rm -f "$ZIP_FILE"

# Try zip first (Linux/macOS), fall back to PowerShell (Windows)
if command -v zip &> /dev/null; then
    cd "$PUBLISH_DIR"
    zip -r "$ZIP_FILE" . > /dev/null
    cd - > /dev/null
else
    # Use PowerShell Compress-Archive (available on Windows and can be installed on Linux)
    WIN_PUBLISH_DIR=$(cygpath -w "$PUBLISH_DIR" 2>/dev/null || echo "$PUBLISH_DIR")
    WIN_ZIP_FILE=$(cygpath -w "$ZIP_FILE" 2>/dev/null || echo "$ZIP_FILE")
    powershell -Command "Compress-Archive -Path '$WIN_PUBLISH_DIR\*' -DestinationPath '$WIN_ZIP_FILE' -Force"
fi

if [ ! -f "$ZIP_FILE" ]; then
    echo "❌ Failed to create zip file"
    exit 1
fi

# Get file size - try du first (Linux/macOS), fall back to PowerShell
ZIP_SIZE=$(du -h "$ZIP_FILE" 2>/dev/null | cut -f1 || powershell -Command "[math]::Round((Get-Item '$(cygpath -w "$ZIP_FILE" 2>/dev/null || echo "$ZIP_FILE")').Length / 1MB, 1).ToString() + 'M'")
echo "✅ Created $ZIP_FILE ($ZIP_SIZE)"

# Step 5: Upload to Azure Blob Storage
echo ""
echo "☁️  Uploading to Azure Blob Storage..."
az storage blob upload \
    --account-name "$STORAGE_ACCOUNT" \
    --container-name "$CONTAINER_NAME" \
    --file "$ZIP_FILE" \
    --name "$BLOB_NAME" \
    --auth-mode key \
    --overwrite \
    --output none 2>/dev/null

if [ $? -ne 0 ]; then
    echo "❌ Upload failed"
    exit 1
fi
echo "✅ Uploaded to $STORAGE_ACCOUNT/$CONTAINER_NAME/$BLOB_NAME"

# Step 6: Generate SAS URL
echo ""
echo "🔑 Generating SAS URL..."

# Calculate expiry time - try GNU date first (Linux/Git Bash), fall back to BSD date (macOS)
EXPIRY=$(date -u -d '+1 hour' +%Y-%m-%dT%H:%MZ 2>/dev/null || date -u -v+1H +%Y-%m-%dT%H:%MZ)
SAS_URL=$(az storage blob generate-sas \
    --account-name "$STORAGE_ACCOUNT" \
    --container-name "$CONTAINER_NAME" \
    --name "$BLOB_NAME" \
    --permissions r \
    --expiry "$EXPIRY" \
    --auth-mode key \
    --full-uri \
    --output tsv 2>/dev/null)

if [ -z "$SAS_URL" ]; then
    echo "❌ Failed to generate SAS URL"
    exit 1
fi
echo "✅ SAS URL generated (expires in 1 hour)"

# Step 7: Deploy to VM
echo ""
echo "🚀 Deploying to VM..."

# Escape special characters in the URL for PowerShell
ESCAPED_URL=$(echo "$SAS_URL" | sed 's/&/`&/g')

DEPLOY_RESULT=$(az vm run-command invoke \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$VM_NAME" \
    --command-id RunPowerShellScript \
    --scripts "
Write-Output '=== Stopping PennieBot service ==='
Stop-Service -Name PennieBot -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Write-Output '=== Downloading new version ==='
\$url = '$SAS_URL'
Invoke-WebRequest -Uri \$url -OutFile 'C:\Temp\pennie-bot-deploy.zip' -UseBasicParsing

Write-Output '=== Backing up current version ==='
if (Test-Path 'C:\Pennie\bot-backup') { Remove-Item -Path 'C:\Pennie\bot-backup' -Recurse -Force }
if (Test-Path 'C:\Pennie\bot') { Copy-Item -Path 'C:\Pennie\bot' -Destination 'C:\Pennie\bot-backup' -Recurse }

Write-Output '=== Extracting new version ==='
Remove-Item -Path 'C:\Pennie\bot\*' -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -Path 'C:\Temp\pennie-bot-deploy.zip' -DestinationPath 'C:\Pennie\bot' -Force

Write-Output '=== Restoring appsettings.json from backup ==='
if (Test-Path 'C:\Pennie\bot-backup\appsettings.json') {
    Copy-Item -Path 'C:\Pennie\bot-backup\appsettings.json' -Destination 'C:\Pennie\bot\appsettings.json' -Force
    Write-Output 'appsettings.json restored from backup'
} else {
    Write-Output 'WARNING: No appsettings.json backup found'
}

Write-Output '=== Verifying deployment ==='
Get-Item 'C:\Pennie\bot\PennieBot.exe' | Select-Object Name, Length, LastWriteTime

Write-Output '=== Starting PennieBot service ==='
Start-Service -Name PennieBot
Start-Sleep -Seconds 3
Get-Service -Name PennieBot | Select-Object Status, Name

Write-Output '=== Deployment complete ==='
" 2>&1)

# Check deployment result
if echo "$DEPLOY_RESULT" | grep -q '"Status": "Running"' || echo "$DEPLOY_RESULT" | grep -q 'Running PennieBot'; then
    echo "✅ Bot deployed and running"
else
    echo "⚠️  Deployment completed, but service status unclear"
    echo "$DEPLOY_RESULT" | jq -r '.value[0].message' 2>/dev/null || echo "$DEPLOY_RESULT"
fi

# Step 8: Verify endpoint
echo ""
echo "🔍 Verifying bot endpoint..."
VM_IP=$(az vm show -g "$AZURE_RESOURCE_GROUP" -n "$VM_NAME" -d --query publicIps -o tsv 2>/dev/null)

if [ -n "$VM_IP" ]; then
    HEALTH_CHECK=$(curl -s -k "https://$VM_IP/health" 2>/dev/null)
    if [ "$HEALTH_CHECK" = "Healthy" ]; then
        echo "✅ Health check passed: https://$VM_IP/health"
    else
        echo "⚠️  Health check returned: $HEALTH_CHECK"
    fi

    ROOT_CHECK=$(curl -s -k "https://$VM_IP/" 2>/dev/null)
    if echo "$ROOT_CHECK" | grep -q "Pennie the Prepper Bot"; then
        echo "✅ Root endpoint responding correctly"
    fi
fi

# Cleanup
echo ""
echo "🧹 Cleaning up..."
rm -f "$ZIP_FILE"
rm -rf "$PUBLISH_DIR"

echo ""
echo "✨ Deployment complete!"
echo ""
echo "Bot deployed to: https://$VM_IP/"
echo "Test in Teams by sending: \"What projects do we have in DevOps?\""
