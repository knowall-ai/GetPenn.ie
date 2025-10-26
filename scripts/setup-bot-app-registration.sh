#!/bin/bash
set -e

# Setup Bot App Registration Script
# This script automates the creation of the Azure AD app registration for Pennie the Prepper Teams Bot
# and stores credentials in Azure Key Vault.

# Usage: ./scripts/setup-bot-app-registration.sh

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${GREEN}=== Pennie Bot App Registration Setup ===${NC}"

# Load environment variables safely
if [ -f .env ]; then
    set -a
    source .env
    set +a
else
    echo -e "${RED}Error: .env file not found${NC}"
    exit 1
fi

# Variables
APP_NAME="Pennie the Prepper Bot"
KEY_VAULT_NAME=${AZURE_KEY_VAULT_NAME:-"pennie-kv-mmdxqm3w7kjwm"}
RESOURCE_GROUP=${AZURE_RESOURCE_GROUP:-"TMinus15Agents"}

echo -e "${GREEN}Step 1: Creating Azure AD App Registration${NC}"
APP_REGISTRATION=$(az ad app create \
    --display-name "$APP_NAME" \
    --sign-in-audience AzureADMyOrg \
    --query "{appId: appId, id: id}" \
    -o json)

APP_ID=$(echo $APP_REGISTRATION | jq -r '.appId')
OBJECT_ID=$(echo $APP_REGISTRATION | jq -r '.id')

echo -e "${GREEN}✓ App Registration created${NC}"
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

echo -e "${GREEN}✓ Graph API permissions added:${NC}"
echo -e "  - Calls.AccessMedia.All"
echo -e "  - Calls.JoinGroupCall.All"
echo -e "  - OnlineMeetings.ReadWrite.All"

echo -e "\n${GREEN}Step 3: Creating Client Secret${NC}"
CREDENTIALS=$(az ad app credential reset \
    --id $APP_ID \
    --append \
    --display-name "PennieBot-Prod-Secret" \
    --years 2 \
    --query "{password: password}" \
    -o json)

CLIENT_SECRET=$(echo $CREDENTIALS | jq -r '.password')
echo -e "${GREEN}✓ Client secret created${NC}"

echo -e "\n${GREEN}Step 4: Storing credentials in Azure Key Vault${NC}"
az keyvault secret set \
    --vault-name $KEY_VAULT_NAME \
    --name "TEAMS-APP-ID" \
    --value "$APP_ID" \
    > /dev/null

az keyvault secret set \
    --vault-name $KEY_VAULT_NAME \
    --name "TEAMS-APP-PASSWORD" \
    --value "$CLIENT_SECRET" \
    > /dev/null

echo -e "${GREEN}✓ Credentials stored in Key Vault: $KEY_VAULT_NAME${NC}"
echo -e "  Secret names: TEAMS-APP-ID, TEAMS-APP-PASSWORD"

echo -e "\n${GREEN}Step 5: Updating .env file${NC}"
# Update .env file with new app ID (cross-platform compatible)
if grep -q "^TEAMS_APP_ID=" .env; then
    sed -i.bak "s|^TEAMS_APP_ID=.*|TEAMS_APP_ID=$APP_ID|" .env
else
    echo "TEAMS_APP_ID=$APP_ID" >> .env
fi

if grep -q "^TEAMS_APP_PASSWORD=" .env; then
    sed -i.bak "s|^TEAMS_APP_PASSWORD=.*|TEAMS_APP_PASSWORD=$CLIENT_SECRET|" .env
else
    echo "TEAMS_APP_PASSWORD=$CLIENT_SECRET" >> .env
fi

# Remove sed backup file
rm -f .env.bak

echo -e "${GREEN}✓ .env file updated${NC}"

echo -e "\n${YELLOW}=== MANUAL STEP REQUIRED ===${NC}"
echo -e "${YELLOW}Admin consent is required for the Graph API permissions.${NC}"
echo -e "\n${YELLOW}To grant admin consent:${NC}"
echo -e "1. Go to: ${GREEN}https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/$APP_ID${NC}"
echo -e "2. Click 'Grant admin consent for [Your Organization]'"
echo -e "3. Confirm the consent prompt"
echo -e "\nAlternatively, run:"
echo -e "${GREEN}az ad app permission admin-consent --id $APP_ID${NC}"
echo -e "\n${YELLOW}Note: This requires Global Administrator or Privileged Role Administrator permissions.${NC}"

echo -e "\n${GREEN}=== Setup Complete ===${NC}"
echo -e "App ID: ${YELLOW}$APP_ID${NC}"
echo -e "Key Vault: ${YELLOW}$KEY_VAULT_NAME${NC}"
echo -e "\n${GREEN}Next steps:${NC}"
echo -e "1. Grant admin consent (see above)"
echo -e "2. Deploy the Teams bot to the Windows VM"
echo -e "3. Configure the bot endpoint in Teams App Studio"
