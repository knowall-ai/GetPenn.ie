#!/bin/bash
set -e

# Setup Bot App Registration Script
# This script automates the creation of the Azure AD app registration for Pennie the Prepper Teams Bot
# Credentials are output for storage in GitHub Secrets.
#
# Usage:
#   ./scripts/setup-bot-app-registration.sh              # Create production app
#   ./scripts/setup-bot-app-registration.sh --env test   # Create test app
#   ./scripts/setup-bot-app-registration.sh --env prod   # Create production app (explicit)

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Parse arguments
ENV="prod"
while [[ $# -gt 0 ]]; do
    case $1 in
        --env|-e)
            ENV="$2"
            shift 2
            ;;
        --help|-h)
            echo "Setup Bot App Registration for Pennie the Prepper"
            echo ""
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --env, -e <env>   Environment: 'test' or 'prod' (default: prod)"
            echo "  --help, -h        Show this help message"
            echo ""
            echo "Examples:"
            echo "  $0                  # Create production app registration"
            echo "  $0 --env test       # Create test app registration"
            exit 0
            ;;
        *)
            echo -e "${RED}Unknown option: $1${NC}"
            exit 1
            ;;
    esac
done

# Validate environment
if [[ "$ENV" != "test" && "$ENV" != "prod" ]]; then
    echo -e "${RED}Invalid environment: $ENV${NC}"
    echo "Use 'test' or 'prod'"
    exit 1
fi

# Set environment-specific values
if [[ "$ENV" == "test" ]]; then
    APP_NAME="Pennie the Prepper (Test)"
    SECRET_NAME="PennieBot-Test-Secret"
    ACCENT_COLOR="#9C27B0"  # Purple for test
else
    APP_NAME="Pennie the Prepper Bot"
    SECRET_NAME="PennieBot-Prod-Secret"
    ACCENT_COLOR="#9DFF0A"  # Green for prod
fi

echo -e "${GREEN}=== Pennie Bot App Registration Setup ===${NC}"
echo -e "${CYAN}Environment: ${YELLOW}$ENV${NC}"
echo -e "${CYAN}App Name: ${YELLOW}$APP_NAME${NC}"
echo ""

# Load environment variables safely (optional - for resource group)
if [ -f .env ]; then
    set -a
    source .env
    set +a
fi

# Variables
RESOURCE_GROUP=${AZURE_RESOURCE_GROUP:-"TMinus15Agents"}
SECRET_EXPIRATION_YEARS=${SECRET_EXPIRATION_YEARS:-2}  # Configurable: default 2 years

echo -e "${GREEN}Step 1: Creating Azure AD App Registration${NC}"
APP_REGISTRATION=$(az ad app create \
    --display-name "$APP_NAME" \
    --sign-in-audience AzureADMyOrg \
    --query "{appId: appId, id: id}" \
    -o json)

APP_ID=$(echo $APP_REGISTRATION | jq -r '.appId')
OBJECT_ID=$(echo $APP_REGISTRATION | jq -r '.id')

echo -e "${GREEN}  App Registration created${NC}"
echo -e "  App ID: ${YELLOW}$APP_ID${NC}"
echo -e "  Object ID: $OBJECT_ID"

echo -e "\n${GREEN}Step 2: Adding Microsoft Graph API Permissions${NC}"
# Microsoft Graph App ID: 00000003-0000-0000-c000-000000000000

# Calls.AccessMedia.All: a7a681dc-756e-4909-b988-f160edc6655f
az ad app permission add \
    --id $APP_ID \
    --api 00000003-0000-0000-c000-000000000000 \
    --api-permissions a7a681dc-756e-4909-b988-f160edc6655f=Role \
    > /dev/null 2>&1

# Calls.JoinGroupCall.All: f6b49018-60ab-4f81-83bd-22caeabfed2d
az ad app permission add \
    --id $APP_ID \
    --api 00000003-0000-0000-c000-000000000000 \
    --api-permissions f6b49018-60ab-4f81-83bd-22caeabfed2d=Role \
    > /dev/null 2>&1

# OnlineMeetings.ReadWrite.All: b8bb2037-6e08-44ac-a4ea-4674e010e2a4
az ad app permission add \
    --id $APP_ID \
    --api 00000003-0000-0000-c000-000000000000 \
    --api-permissions b8bb2037-6e08-44ac-a4ea-4674e010e2a4=Role \
    > /dev/null 2>&1

echo -e "${GREEN}  Graph API permissions added:${NC}"
echo -e "  - Calls.AccessMedia.All"
echo -e "  - Calls.JoinGroupCall.All"
echo -e "  - OnlineMeetings.ReadWrite.All"

echo -e "\n${GREEN}Step 3: Creating Client Secret${NC}"
CREDENTIALS=$(az ad app credential reset \
    --id $APP_ID \
    --append \
    --display-name "$SECRET_NAME" \
    --years $SECRET_EXPIRATION_YEARS \
    --query "{password: password}" \
    -o json)

CLIENT_SECRET=$(echo $CREDENTIALS | jq -r '.password')
EXPIRY_DATE=$(date -d "+${SECRET_EXPIRATION_YEARS} years" +%Y-%m-%d 2>/dev/null || date -v+${SECRET_EXPIRATION_YEARS}y +%Y-%m-%d)
echo -e "${GREEN}  Client secret created (expires: $EXPIRY_DATE)${NC}"

echo -e "\n${GREEN}Step 4: Store credentials in GitHub Secrets${NC}"
echo -e "${YELLOW}Run these commands to store credentials:${NC}"
echo ""
echo -e "  gh secret set TEAMS_APP_ID --env $ENV --body \"$APP_ID\""
echo -e "  gh secret set TEAMS_APP_PASSWORD --env $ENV --body \"$CLIENT_SECRET\""
echo ""

# Attempt to set secrets automatically if gh is available and user confirms
if command -v gh &> /dev/null; then
    echo -e "${CYAN}Would you like to set these secrets automatically? (y/N)${NC}"
    read -r CONFIRM
    if [[ "$CONFIRM" =~ ^[Yy]$ ]]; then
        echo -e "Setting TEAMS_APP_ID..."
        gh secret set TEAMS_APP_ID --env "$ENV" --body "$APP_ID"
        echo -e "Setting TEAMS_APP_PASSWORD..."
        gh secret set TEAMS_APP_PASSWORD --env "$ENV" --body "$CLIENT_SECRET"
        echo -e "${GREEN}  Secrets set successfully!${NC}"
    fi
fi

echo -e "\n${YELLOW}=== MANUAL STEP REQUIRED ===${NC}"
echo -e "${YELLOW}Admin consent is required for the Graph API permissions.${NC}"
echo -e "\n${YELLOW}Option 1 - Azure Portal:${NC}"
echo -e "1. Go to: ${GREEN}https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/$APP_ID${NC}"
echo -e "2. Click 'Grant admin consent for [Your Organization]'"
echo -e "3. Confirm the consent prompt"
echo -e "\n${YELLOW}Option 2 - Azure CLI (requires admin permissions):${NC}"
echo -e "  ${GREEN}az ad app permission admin-consent --id $APP_ID${NC}"

echo -e "\n${GREEN}=== Summary ===${NC}"
echo -e "Environment:  ${YELLOW}$ENV${NC}"
echo -e "App Name:     ${YELLOW}$APP_NAME${NC}"
echo -e "App ID:       ${YELLOW}$APP_ID${NC}"
echo -e "Accent Color: ${YELLOW}$ACCENT_COLOR${NC} (for Teams manifest)"

echo -e "\n${GREEN}Next steps:${NC}"
echo -e "1. Grant admin consent (see above)"
if [[ "$ENV" == "test" ]]; then
    echo -e "2. Create test Teams manifest: cp bot/teams-manifest/manifest.json bot/teams-manifest/manifest.test.json"
    echo -e "3. Update manifest.test.json with:"
    echo -e "   - id: $(uuidgen 2>/dev/null || echo '<generate-new-uuid>')"
    echo -e "   - name.short: \"Pennie the Prepper (Test)\""
    echo -e "   - accentColor: \"$ACCENT_COLOR\""
    echo -e "   - bots[0].botId: \"$APP_ID\""
    echo -e "4. Create test app package: cd bot/teams-manifest && zip pennie-app-test.zip manifest.test.json color.png outline.png"
    echo -e "5. Upload to Teams Admin Center (first time only)"
else
    echo -e "2. Deploy the Teams bot to the Windows VM"
    echo -e "3. Upload Teams manifest to Admin Center (first time only)"
fi
