#!/bin/bash
# grant-calendar-permission.sh
# Grants the Calendars.Read permission to the bot so it can monitor Pennie's calendar.
# Uses Azure CLI with Microsoft Graph API.
#
# Prerequisites:
# - Azure CLI logged in with admin permissions
#
# Usage: ./scripts/grant-calendar-permission.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

# Load environment
if [ -f "$PROJECT_ROOT/.env" ]; then
    source "$PROJECT_ROOT/.env"
fi

BOT_APP_ID="${AZURE_BOT_APP_ID:-9707c142-2583-4e56-9983-4d913338afb0}"
MICROSOFT_GRAPH_APP_ID="00000003-0000-0000-c000-000000000000"

echo "Granting Calendar Permissions to Bot"
echo ""
echo "Bot App ID: $BOT_APP_ID"
echo ""

# Check Azure CLI login
if ! az account show &>/dev/null; then
    echo "Error: Not logged into Azure CLI. Run 'az login' first."
    exit 1
fi

# Get the service principal for the bot
echo "Finding bot's service principal..."
BOT_SP_ID=$(az ad sp list --filter "appId eq '$BOT_APP_ID'" --query "[0].id" -o tsv 2>/dev/null)

if [ -z "$BOT_SP_ID" ] || [ "$BOT_SP_ID" == "None" ]; then
    echo "Error: Could not find service principal for bot app ID: $BOT_APP_ID"
    echo "Make sure the bot is registered in Azure AD."
    exit 1
fi

echo "  Service Principal ID: $BOT_SP_ID"
echo ""

# Get Microsoft Graph service principal
echo "Finding Microsoft Graph service principal..."
GRAPH_SP_ID=$(az ad sp list --filter "appId eq '$MICROSOFT_GRAPH_APP_ID'" --query "[0].id" -o tsv 2>/dev/null)

if [ -z "$GRAPH_SP_ID" ] || [ "$GRAPH_SP_ID" == "None" ]; then
    echo "Error: Could not find Microsoft Graph service principal"
    exit 1
fi

echo "  Microsoft Graph SP ID: $GRAPH_SP_ID"
echo ""

# Permission IDs for Microsoft Graph (application permissions)
# Calendars.Read: 798ee544-9d2d-430c-a058-570e29e34338
# Calendars.ReadWrite: ef54d2bf-783f-4e0f-bca1-3210c0444d99

CALENDARS_READ_ID="798ee544-9d2d-430c-a058-570e29e34338"

echo "Granting Calendars.Read permission..."

# Check if permission already granted
EXISTING=$(az ad app permission list --id "$BOT_APP_ID" --query "[?resourceAppId=='$MICROSOFT_GRAPH_APP_ID'].resourceAccess[?id=='$CALENDARS_READ_ID']" -o tsv 2>/dev/null || echo "")

if [ -n "$EXISTING" ]; then
    echo "  Permission already configured on app registration."
else
    # Add the permission to the app registration
    az ad app permission add \
        --id "$BOT_APP_ID" \
        --api "$MICROSOFT_GRAPH_APP_ID" \
        --api-permissions "$CALENDARS_READ_ID=Role"

    echo "  Permission added to app registration."
fi

# Grant admin consent
echo ""
echo "Granting admin consent..."
az ad app permission admin-consent --id "$BOT_APP_ID" 2>/dev/null || {
    echo ""
    echo "Note: Admin consent may require higher privileges."
    echo "If this failed, grant consent manually in Azure Portal:"
    echo "  1. Go to: https://portal.azure.com"
    echo "  2. Navigate to: Azure Active Directory > App registrations"
    echo "  3. Find: $BOT_APP_ID"
    echo "  4. Go to: API permissions"
    echo "  5. Click: Grant admin consent"
}

echo ""
echo "Permission setup complete!"
echo ""
echo "The bot now has Calendars.Read permission to monitor"
echo "the resource account's calendar for meeting invites."
echo ""
echo "Current Graph API permissions for bot:"
az ad app permission list --id "$BOT_APP_ID" --query "[].resourceAccess[].{id:id,type:type}" -o table 2>/dev/null || echo "(unable to list)"
echo ""
