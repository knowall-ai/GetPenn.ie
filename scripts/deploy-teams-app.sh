#!/bin/bash
# Deploy Teams app package to organization's app catalog
# Uses Microsoft Graph API to upload/update the Teams app manifest
#
# IMPORTANT: First-time deployment requires manual upload due to Microsoft
# restrictions. This script can only UPDATE existing apps in the catalog.
#
# Prerequisites:
#   - Azure CLI logged in: az login
#   - Bot credentials in Key Vault with AppCatalog.ReadWrite.All permission
#
# First-time deployment (manual):
#   1. Teams Admin Center: https://admin.teams.microsoft.com
#      > Teams apps > Manage apps > Upload new app
#   2. Or Teams client: Apps > Manage your apps > Upload a custom app
#   3. Select: bot/teams-manifest/pennie-app-v1.1.0.zip
#
# Subsequent updates (automated):
#   ./scripts/deploy-teams-app.sh              # Update existing app
#   ./scripts/deploy-teams-app.sh --create     # Create package then update
#
# Why can't we automate first-time upload?
#   - Microsoft Graph API restricts NEW app publishing via application credentials
#   - Only existing apps can be updated programmatically
#   - This is by design for security/governance

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
MANIFEST_DIR="$PROJECT_ROOT/bot/teams-manifest"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

show_help() {
    echo "Deploy Teams App Package to Organization Catalog"
    echo ""
    echo "Usage: $0 [OPTIONS]"
    echo ""
    echo "Options:"
    echo "  --create    Create the app package before deploying"
    echo "  --help      Show this help message"
    echo ""
    echo -e "${YELLOW}IMPORTANT: First-time deployment requires manual upload.${NC}"
    echo "This script can only UPDATE existing apps in the catalog."
    echo ""
    echo "First-time upload (manual):"
    echo "  1. Teams Admin Center: https://admin.teams.microsoft.com"
    echo "     > Teams apps > Manage apps > Upload new app"
    echo "  2. Or Teams client: Apps > Manage your apps > Upload a custom app"
    echo ""
    echo "After manual upload, this script can update the app automatically."
}

create_package() {
    echo -e "${YELLOW}Creating Teams app package...${NC}"

    # Get version from manifest
    VERSION=$(jq -r '.version' "$MANIFEST_DIR/manifest.json")
    PACKAGE_NAME="pennie-app-v${VERSION}.zip"

    cd "$MANIFEST_DIR"
    rm -f "$PACKAGE_NAME"
    zip "$PACKAGE_NAME" manifest.json color.png outline.png

    echo -e "${GREEN}Created: $MANIFEST_DIR/$PACKAGE_NAME${NC}"
    cd - > /dev/null
}

# Parse arguments
CREATE_PACKAGE=false
for arg in "$@"; do
    case $arg in
        --create)
            CREATE_PACKAGE=true
            ;;
        --help|-h)
            show_help
            exit 0
            ;;
    esac
done

# Create package if requested
if [ "$CREATE_PACKAGE" = true ]; then
    create_package
fi

# Find the latest app package
APP_PACKAGE=$(ls -t "$MANIFEST_DIR"/pennie-app-v*.zip 2>/dev/null | head -1)

if [ -z "$APP_PACKAGE" ]; then
    echo -e "${RED}ERROR: No Teams app package found in $MANIFEST_DIR${NC}"
    echo ""
    echo "Create one with:"
    echo "  $0 --create"
    echo ""
    echo "Or manually:"
    echo "  cd $MANIFEST_DIR"
    echo "  zip pennie-app-v1.1.0.zip manifest.json color.png outline.png"
    exit 1
fi

# Get App ID from manifest
APP_ID=$(jq -r '.id' "$MANIFEST_DIR/manifest.json")
APP_VERSION=$(jq -r '.version' "$MANIFEST_DIR/manifest.json")

echo "=== Deploy Teams App Package ==="
echo "Package: $APP_PACKAGE"
echo "App ID:  $APP_ID"
echo "Version: $APP_VERSION"
echo ""

# Get access token for Microsoft Graph using bot credentials
KEY_VAULT_NAME="${AZURE_KEY_VAULT_NAME:-"pennie-kv-mmdxqm3w7kjwm"}"
echo "Getting bot credentials from Key Vault ($KEY_VAULT_NAME)..."
BOT_APP_ID=$(az keyvault secret show --vault-name "$KEY_VAULT_NAME" --name "MicrosoftAppId" --query value -o tsv 2>/dev/null)
BOT_APP_SECRET=$(az keyvault secret show --vault-name "$KEY_VAULT_NAME" --name "MicrosoftAppPassword" --query value -o tsv 2>/dev/null)
TENANT_ID=$(az account show --query tenantId -o tsv 2>/dev/null)

if [ -z "$BOT_APP_ID" ] || [ -z "$BOT_APP_SECRET" ]; then
    echo -e "${RED}ERROR: Failed to get bot credentials from Key Vault${NC}"
    echo "Make sure you're logged in: az login"
    exit 1
fi

echo "Using bot app: $BOT_APP_ID"
echo "Getting access token for Microsoft Graph..."

TOKEN_RESPONSE=$(curl -s -X POST \
    "https://login.microsoftonline.com/$TENANT_ID/oauth2/v2.0/token" \
    -H "Content-Type: application/x-www-form-urlencoded" \
    -d "client_id=$BOT_APP_ID" \
    -d "client_secret=$BOT_APP_SECRET" \
    -d "scope=https://graph.microsoft.com/.default" \
    -d "grant_type=client_credentials")

ACCESS_TOKEN=$(echo "$TOKEN_RESPONSE" | jq -r '.access_token // empty')

if [ -z "$ACCESS_TOKEN" ] || [ "$ACCESS_TOKEN" = "null" ]; then
    echo -e "${RED}ERROR: Failed to get access token${NC}"
    echo "$TOKEN_RESPONSE" | jq .
    exit 1
fi

echo -e "${GREEN}Token obtained.${NC}"
echo ""

# Check if the app already exists in the catalog
echo "Checking for existing app in catalog..."
EXISTING_APP=$(curl -s -X GET \
    "https://graph.microsoft.com/v1.0/appCatalogs/teamsApps?\$filter=externalId%20eq%20'$APP_ID'" \
    -H "Authorization: Bearer $ACCESS_TOKEN" \
    -H "Content-Type: application/json")

# Check for permission errors
if echo "$EXISTING_APP" | jq -e '.error' > /dev/null 2>&1; then
    ERROR_CODE=$(echo "$EXISTING_APP" | jq -r '.error.code')
    ERROR_MSG=$(echo "$EXISTING_APP" | jq -r '.error.message')

    echo -e "${RED}ERROR: $ERROR_CODE${NC}"
    echo "$ERROR_MSG"
    echo ""
    echo -e "${YELLOW}To fix permission issues:${NC}"
    echo "  1. Go to: https://portal.azure.com/#view/Microsoft_AAD_IAM/ActiveDirectoryMenuBlade/~/RegisteredApps"
    echo "  2. Find your Azure CLI app registration"
    echo "  3. API permissions > Add permission > Microsoft Graph > Application"
    echo "  4. Add: AppCatalog.ReadWrite.All"
    echo "  5. Click 'Grant admin consent for [org]'"
    echo "  6. Re-login: az logout && az login"
    echo ""
    echo -e "${YELLOW}Manual upload alternative:${NC}"
    echo "  Teams > Apps > Manage your apps > Upload a custom app"
    echo "  Select: $APP_PACKAGE"
    exit 1
fi

TEAMS_APP_ID=$(echo "$EXISTING_APP" | jq -r '.value[0].id // empty')

if [ -n "$TEAMS_APP_ID" ] && [ "$TEAMS_APP_ID" != "null" ]; then
    echo -e "${GREEN}Found existing app: $TEAMS_APP_ID${NC}"
    echo "Updating app..."

    RESPONSE=$(curl -s -w "\nHTTP_CODE:%{http_code}" -X POST \
        "https://graph.microsoft.com/v1.0/appCatalogs/teamsApps/$TEAMS_APP_ID/appDefinitions" \
        -H "Authorization: Bearer $ACCESS_TOKEN" \
        -H "Content-Type: application/zip" \
        --data-binary @"$APP_PACKAGE")

    HTTP_CODE=$(echo "$RESPONSE" | grep "HTTP_CODE:" | cut -d: -f2)
    BODY=$(echo "$RESPONSE" | grep -v "HTTP_CODE:")

    if [ "$HTTP_CODE" = "200" ] || [ "$HTTP_CODE" = "201" ]; then
        echo -e "${GREEN}SUCCESS: Teams app updated!${NC}"
        echo "$BODY" | jq . 2>/dev/null || echo "$BODY"
    else
        echo -e "${RED}Update failed (HTTP $HTTP_CODE)${NC}"
        echo "$BODY" | jq . 2>/dev/null || echo "$BODY"
        exit 1
    fi
else
    echo -e "${YELLOW}App not found in catalog.${NC}"
    echo ""
    echo "First-time deployment requires manual upload."
    echo "Microsoft Graph API restricts automated publishing of NEW apps."
    echo ""
    echo -e "${GREEN}Manual upload options:${NC}"
    echo ""
    echo "Option 1 - Teams Admin Center (recommended):"
    echo "  1. Open: https://admin.teams.microsoft.com"
    echo "  2. Go to: Teams apps > Manage apps > Upload new app"
    echo "  3. Select: $APP_PACKAGE"
    echo ""
    echo "Option 2 - Teams client:"
    echo "  1. Open Teams > Apps > Manage your apps"
    echo "  2. Click: Upload a custom app > Upload for [org]"
    echo "  3. Select: $APP_PACKAGE"
    echo ""
    echo "After manual upload, run this script again to verify and update."
    exit 1
fi

echo ""
echo -e "${GREEN}=== Deployment Complete ===${NC}"
echo ""
echo "Next steps:"
echo "  1. Find 'Pennie the Prepper' in Teams app store"
echo "  2. Add to a chat or meeting"
echo "  3. For meetings, invite before or during the meeting"
