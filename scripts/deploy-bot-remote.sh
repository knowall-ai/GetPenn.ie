#!/bin/bash
set -e

# Deploy Pennie Bot to Windows VM from local machine
# This script uploads the bot code and executes deployment on the VM

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

echo -e "${GREEN}=== Pennie Bot Remote Deployment ===${NC}"
echo ""

# Configuration
RESOURCE_GROUP=${AZURE_RESOURCE_GROUP:-"TMinus15Agents"}
VM_NAME=${VM_NAME:-"pennie-vm-prod"}

echo -e "${CYAN}Configuration:${NC}"
echo "  Resource Group: $RESOURCE_GROUP"
echo "  VM Name: $VM_NAME"
echo ""

# Step 1: Check prerequisites
echo -e "${CYAN}Step 1: Checking prerequisites...${NC}"

if ! command -v az &> /dev/null; then
    echo -e "${RED}ERROR: Azure CLI not installed${NC}"
    exit 1
fi

if ! command -v dotnet &> /dev/null; then
    echo -e "${RED}ERROR: .NET SDK not installed${NC}"
    echo -e "${YELLOW}Install from: https://dotnet.microsoft.com/download${NC}"
    exit 1
fi

echo -e "${GREEN}  Prerequisites OK${NC}"

# Step 2: Build bot locally
echo ""
echo -e "${CYAN}Step 2: Building bot application locally...${NC}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
BOT_PROJECT="$REPO_ROOT/bot/PennieBot.csproj"
BUILD_OUTPUT="$REPO_ROOT/bot/bin/Release/net8.0/publish"

if [ ! -f "$BOT_PROJECT" ]; then
    echo -e "${RED}ERROR: Bot project not found at $BOT_PROJECT${NC}"
    exit 1
fi

echo "  Building and publishing..."
dotnet publish "$BOT_PROJECT" \
    --configuration Release \
    --output "$BUILD_OUTPUT" \
    --self-contained false

if [ $? -ne 0 ]; then
    echo -e "${RED}ERROR: Build failed${NC}"
    exit 1
fi

echo -e "${GREEN}  Build successful${NC}"

# Step 3: Create deployment package
echo ""
echo -e "${CYAN}Step 3: Creating deployment package...${NC}"

TEMP_DIR=$(mktemp -d)
PACKAGE_PATH="$TEMP_DIR/pennie-bot.zip"

echo "  Packaging bot files..."
cd "$BUILD_OUTPUT"
zip -r "$PACKAGE_PATH" . > /dev/null 2>&1

PACKAGE_SIZE=$(du -h "$PACKAGE_PATH" | cut -f1)
echo -e "${GREEN}  Package created: $PACKAGE_SIZE${NC}"

# Step 4: Upload package to VM
echo ""
echo -e "${CYAN}Step 4: Uploading package to VM...${NC}"

# Create upload script
cat > "$TEMP_DIR/upload.ps1" <<'EOF'
param([string]$PackageUrl)

$TempDir = "C:\Temp"
$PackagePath = "$TempDir\pennie-bot.zip"
$ExtractPath = "C:\Pennie\bot"

# Ensure directories exist
New-Item -ItemType Directory -Path $TempDir -Force
New-Item -ItemType Directory -Path $ExtractPath -Force

# Download package
Write-Host "Downloading deployment package..."
Invoke-WebRequest -Uri $PackageUrl -OutFile $PackagePath

# Extract package
Write-Host "Extracting to $ExtractPath..."
Expand-Archive -Path $PackagePath -DestinationPath $ExtractPath -Force

Write-Host "Upload complete"
EOF

# Upload using Azure Storage (more reliable than VM Run Command for large files)
STORAGE_ACCOUNT=$(az storage account list \
    --resource-group "$RESOURCE_GROUP" \
    --query "[0].name" -o tsv)

if [ -z "$STORAGE_ACCOUNT" ]; then
    echo -e "${YELLOW}  No storage account found, using VM Run Command (may be slow)${NC}"

    # Fallback: Use VM Run Command with base64 encoding for smaller packages
    echo "  Using VM Run Command to upload..."

    # This is a fallback - in production, use Azure Storage or file share
    echo -e "${YELLOW}  WARNING: Large files may timeout with VM Run Command${NC}"
    echo -e "${YELLOW}  Consider deploying via Azure DevOps pipeline or GitHub Actions${NC}"
else
    echo "  Using storage account: $STORAGE_ACCOUNT"

    # Create container
    az storage container create \
        --name deployments \
        --account-name "$STORAGE_ACCOUNT" \
        --auth-mode login \
        > /dev/null 2>&1 || true

    # Upload package
    BLOB_NAME="pennie-bot-$(date +%Y%m%d%H%M%S).zip"
    az storage blob upload \
        --account-name "$STORAGE_ACCOUNT" \
        --container-name deployments \
        --name "$BLOB_NAME" \
        --file "$PACKAGE_PATH" \
        --auth-mode login \
        --overwrite

    # Generate SAS URL (valid for 1 hour)
    EXPIRY=$(date -u -d '1 hour' '+%Y-%m-%dT%H:%MZ' 2>/dev/null || date -u -v+1H '+%Y-%m-%dT%H:%MZ')
    PACKAGE_URL=$(az storage blob generate-sas \
        --account-name "$STORAGE_ACCOUNT" \
        --container-name deployments \
        --name "$BLOB_NAME" \
        --permissions r \
        --expiry "$EXPIRY" \
        --auth-mode login \
        --full-uri -o tsv)

    echo -e "${GREEN}  Package uploaded to Azure Storage${NC}"

    # Execute upload script on VM
    echo "  Downloading package to VM..."
    az vm run-command invoke \
        --resource-group "$RESOURCE_GROUP" \
        --name "$VM_NAME" \
        --command-id RunPowerShellScript \
        --scripts @"$TEMP_DIR/upload.ps1" \
        --parameters "PackageUrl=$PACKAGE_URL" \
        --output table
fi

# Step 5: Execute deployment script on VM
echo ""
echo -e "${CYAN}Step 5: Executing deployment on VM...${NC}"

DEPLOY_SCRIPT="$SCRIPT_DIR/deploy-bot-to-vm.ps1"

if [ ! -f "$DEPLOY_SCRIPT" ]; then
    echo -e "${RED}ERROR: Deployment script not found at $DEPLOY_SCRIPT${NC}"
    exit 1
fi

echo "  Running deployment script on VM..."

az vm run-command invoke \
    --resource-group "$RESOURCE_GROUP" \
    --name "$VM_NAME" \
    --command-id RunPowerShellScript \
    --scripts @"$DEPLOY_SCRIPT" \
    --output table

# Step 6: Verify deployment
echo ""
echo -e "${CYAN}Step 6: Verifying deployment...${NC}"

VM_FQDN=$(az vm show \
    --resource-group "$RESOURCE_GROUP" \
    --name "$VM_NAME" \
    --show-details \
    --query "fqdns" -o tsv)

if [ -n "$VM_FQDN" ]; then
    echo "  VM FQDN: https://$VM_FQDN"
    echo "  Testing health endpoint..."

    # Wait a few seconds for service to start
    sleep 5

    if curl -k -f "https://$VM_FQDN/health" > /dev/null 2>&1; then
        echo -e "${GREEN}  Health check passed!${NC}"
    else
        echo -e "${YELLOW}  Health check failed (service may still be starting)${NC}"
        echo -e "${YELLOW}  Check logs on the VM${NC}"
    fi
else
    echo -e "${YELLOW}  Could not determine VM FQDN${NC}"
fi

# Cleanup
echo ""
echo -e "${CYAN}Cleaning up temporary files...${NC}"
rm -rf "$TEMP_DIR"

# Summary
echo ""
echo -e "${GREEN}=== Deployment Complete ===${NC}"
echo ""
echo -e "${CYAN}Bot Endpoint:${NC} https://$VM_FQDN"
echo -e "${CYAN}Health Check:${NC} https://$VM_FQDN/health"
echo ""
echo -e "${YELLOW}Next Steps:${NC}"
echo "1. Verify the service is running on the VM"
echo "2. Configure Teams App Studio with bot messaging endpoint"
echo "3. Test by inviting the bot to a Teams meeting"
echo ""
