#!/bin/bash
# Update Pennie the Prepper with OpenAPI tools for Azure DevOps backend

set -e

echo "🤖 Updating Pennie the Prepper with OpenAPI Tools"
echo ""

# Configuration
ENDPOINT="https://benw-mgan4638-eastus2.cognitiveservices.azure.com"
API_VERSION="2024-05-01-preview"
AGENT_ID="asst_yhQ9HVWxaIyeaSZwjBDOkSQi"
OPENAPI_SPEC="$(cd "$(dirname "$0")/.." && pwd)/openapi/pennie-backend-openapi.json"

echo "📋 Configuration:"
echo "   Endpoint: $ENDPOINT"
echo "   Agent ID: $AGENT_ID"
echo "   OpenAPI Spec: $OPENAPI_SPEC"
echo ""

# Get access token
echo "🔑 Getting access token..."
TOKEN=$(az account get-access-token --resource https://cognitiveservices.azure.com --query accessToken --output tsv)
echo "   ✅ Got token"

# Load OpenAPI spec
echo "📄 Loading OpenAPI spec..."
if [ ! -f "$OPENAPI_SPEC" ]; then
  echo "   ❌ OpenAPI spec not found"
  exit 1
fi
SPEC_CONTENT=$(cat "$OPENAPI_SPEC" | jq -c '.')
echo "   ✅ Loaded spec"

# Create payload for tools update
echo "🔧 Creating tools payload..."
cat > /tmp/pennie-tools.json <<PAYLOAD_EOF
{
  "tools": [
    {
      "type": "openapi",
      "openapi": {
        "name": "azure_devops_backend",
        "description": "Azure DevOps Work Item Tracking API - 9 functions for managing projects, teams, work items, and links",
        "spec": $SPEC_CONTENT,
        "auth": {
          "type": "none"
        }
      }
    }
  ]
}
PAYLOAD_EOF
echo "   ✅ Payload created"

# Update Pennie
echo ""
echo "📤 Updating Pennie with Azure DevOps API tools..."
RESPONSE=$(curl -s -X PATCH "$ENDPOINT/openai/assistants/$AGENT_ID?api-version=$API_VERSION" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "api-key: $TOKEN" \
  -d @/tmp/pennie-tools.json)

# Check result
if echo "$RESPONSE" | jq -e '.id' >/dev/null 2>&1; then
  echo "   ✅ Successfully updated Pennie!"
  echo ""
  echo "📋 Updated Agent:"
  echo "$RESPONSE" | jq -r '"   Name: " + .name'
  echo "$RESPONSE" | jq -r '"   Model: " + .model'
  TOOL_COUNT=$(echo "$RESPONSE" | jq '.tools | length')
  echo "   Tools: $TOOL_COUNT configured"
  
  echo ""
  echo "   Tool Details:"
  echo "$RESPONSE" | jq -r '.tools[] | "   - " + (.type) + ": " + (if .function then .function.name elif .openapi then .openapi.name else "unknown" end)'
else
  echo "   ❌ Failed to update"
  echo "$RESPONSE" | jq '.' 2>/dev/null || echo "$RESPONSE"
  exit 1
fi

# Cleanup
rm -f /tmp/pennie-tools.json

echo ""
echo "✅ Done! Pennie now has access to all 9 Azure DevOps functions via OpenAPI"
echo ""
echo "🎉 Test in Azure AI Foundry:"
echo '   Ask Pennie: "What DevOps projects do we have?"'
echo "   She should list all 26 KnowAll projects!"
