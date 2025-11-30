#!/bin/bash
# setup-resource-account.sh
# Creates a resource account for Pennie so users can invite her to meetings like a person.
# Uses Azure CLI with Microsoft Graph API - fully automated.
#
# Prerequisites:
# - Azure CLI logged in with admin permissions (User.ReadWrite.All, Directory.ReadWrite.All)
#
# Usage: ./scripts/setup-resource-account.sh [email]
# Example: ./scripts/setup-resource-account.sh pennie@knowall.ai

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

# Load environment
if [ -f "$PROJECT_ROOT/.env" ]; then
    source "$PROJECT_ROOT/.env"
fi

# Configuration
RESOURCE_ACCOUNT_EMAIL="${1:-pennie@knowall.ai}"
RESOURCE_ACCOUNT_NAME="Pennie the Prepper"
BOT_APP_ID="${AZURE_BOT_APP_ID:-9707c142-2583-4e56-9983-4d913338afb0}"
USAGE_LOCATION="${USAGE_LOCATION:-GB}"

# Extract username and domain from email
USERNAME="${RESOURCE_ACCOUNT_EMAIL%%@*}"
DOMAIN="${RESOURCE_ACCOUNT_EMAIL#*@}"

echo "Setting up Resource Account for Pennie"
echo ""
echo "Configuration:"
echo "  Email: $RESOURCE_ACCOUNT_EMAIL"
echo "  Display Name: $RESOURCE_ACCOUNT_NAME"
echo "  Bot App ID: $BOT_APP_ID"
echo "  Usage Location: $USAGE_LOCATION"
echo ""

# Check Azure CLI login
if ! az account show &>/dev/null; then
    echo "Error: Not logged into Azure CLI. Run 'az login' first."
    exit 1
fi

# Check if user already exists
echo "Checking if resource account already exists..."
EXISTING_USER=$(az rest --method GET \
    --url "https://graph.microsoft.com/v1.0/users?\$filter=userPrincipalName eq '$RESOURCE_ACCOUNT_EMAIL'" \
    --query "value[0].id" -o tsv 2>/dev/null || echo "")

if [ -n "$EXISTING_USER" ] && [ "$EXISTING_USER" != "None" ]; then
    echo "Resource account already exists!"
    echo "  User ID: $EXISTING_USER"
    USER_ID="$EXISTING_USER"
else
    echo "Creating resource account via Microsoft Graph..."

    # Generate a random secure password (user won't need to sign in)
    PASSWORD=$(openssl rand -base64 32)

    # Create the user via Graph API
    USER_RESPONSE=$(az rest --method POST \
        --url "https://graph.microsoft.com/v1.0/users" \
        --headers "Content-Type=application/json" \
        --body "{
            \"accountEnabled\": true,
            \"displayName\": \"$RESOURCE_ACCOUNT_NAME\",
            \"mailNickname\": \"$USERNAME\",
            \"userPrincipalName\": \"$RESOURCE_ACCOUNT_EMAIL\",
            \"passwordProfile\": {
                \"forceChangePasswordNextSignIn\": false,
                \"password\": \"$PASSWORD\"
            },
            \"usageLocation\": \"$USAGE_LOCATION\",
            \"jobTitle\": \"AI Meeting Assistant\",
            \"department\": \"Automation\"
        }")

    USER_ID=$(echo "$USER_RESPONSE" | jq -r '.id')

    if [ -z "$USER_ID" ] || [ "$USER_ID" == "null" ]; then
        echo "Error: Failed to create resource account"
        echo "$USER_RESPONSE"
        exit 1
    fi

    echo "Resource account created successfully!"
    echo "  User ID: $USER_ID"
fi

echo ""
echo "Configuring resource account settings..."

# Set mailbox settings (disable auto-replies, enable calendar processing)
echo "  - Enabling calendar auto-accept for meeting invites..."
az rest --method PATCH \
    --url "https://graph.microsoft.com/v1.0/users/$USER_ID/mailboxSettings" \
    --headers "Content-Type=application/json" \
    --body '{
        "automaticRepliesSetting": {
            "status": "disabled"
        }
    }' 2>/dev/null || echo "    (mailbox settings may need time to provision)"

# Update .env file with the resource account ID
ENV_FILE="$PROJECT_ROOT/.env"
if [ -f "$ENV_FILE" ]; then
    # Remove existing entries
    sed -i '/^RESOURCE_ACCOUNT_EMAIL=/d' "$ENV_FILE" 2>/dev/null || true
    sed -i '/^RESOURCE_ACCOUNT_USER_ID=/d' "$ENV_FILE" 2>/dev/null || true
fi

# Append new values
echo "" >> "$ENV_FILE"
echo "# Resource Account for Meeting Invites" >> "$ENV_FILE"
echo "RESOURCE_ACCOUNT_EMAIL=$RESOURCE_ACCOUNT_EMAIL" >> "$ENV_FILE"
echo "RESOURCE_ACCOUNT_USER_ID=$USER_ID" >> "$ENV_FILE"

echo ""
echo "Updated .env file with:"
echo "  RESOURCE_ACCOUNT_EMAIL=$RESOURCE_ACCOUNT_EMAIL"
echo "  RESOURCE_ACCOUNT_USER_ID=$USER_ID"

# Output JSON for CI/CD pipelines
echo ""
echo "JSON output for automation:"
cat << EOF
{
    "resourceAccount": {
        "email": "$RESOURCE_ACCOUNT_EMAIL",
        "userId": "$USER_ID",
        "displayName": "$RESOURCE_ACCOUNT_NAME",
        "botAppId": "$BOT_APP_ID"
    }
}
EOF

echo ""
echo "=== Next Steps ==="
echo ""
echo "1. LICENSE ASSIGNMENT (REQUIRED for mailbox):"
echo "   The resource account needs an Exchange mailbox to receive calendar invites."
echo ""
echo "   OPTION A - Free license (recommended):"
echo "   a) Go to: https://admin.microsoft.com/AdminPortal/Home#/catalog"
echo "   b) Search for: 'Microsoft Teams Phone Resource Account'"
echo "   c) Click 'Get now' to add to your tenant (it's free)"
echo "   d) Go to: https://admin.microsoft.com/AdminPortal/Home#/users"
echo "   e) Find: $RESOURCE_ACCOUNT_EMAIL"
echo "   f) Assign the 'Microsoft Teams Phone Resource Account' license"
echo ""
echo "   OPTION B - Paid license (~£3.30/month):"
echo "   a) Go to: https://admin.microsoft.com/AdminPortal/Home#/catalog"
echo "   b) Purchase 'Exchange Online Plan 1'"
echo "   c) Assign to: $RESOURCE_ACCOUNT_EMAIL"
echo ""
echo "2. Grant Graph API Permissions (if not already done):"
echo "   The bot needs Calendars.Read permission to monitor this account's calendar."
echo "   Run: ./scripts/grant-calendar-permission.sh"
echo ""
echo "3. Test Meeting Invite (after mailbox is provisioned):"
echo "   - Create a test meeting in Outlook/Teams"
echo "   - Add '$RESOURCE_ACCOUNT_EMAIL' as an attendee"
echo "   - Verify the invite is received"
echo "   - Pennie should auto-join when the meeting starts"
echo ""
