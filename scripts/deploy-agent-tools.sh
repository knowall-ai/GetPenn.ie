#!/bin/bash
#
# Deploy Pennie's Azure DevOps API tools using Azure AI Agents REST API
#

set -e

echo "🤖 Deploying Pennie's Azure DevOps API Tools via REST API"
echo ""

# Configuration
RESOURCE_GROUP="TMinus15Agents"
COG_ACCOUNT="benw-mgan4638-eastus2"
AGENT_NAME="Pennie the Prepper"
OPENAPI_SPEC_PATH="$(cd "$(dirname "$0")/.." && pwd)/openapi/pennie-backend-openapi.json"

# Get the Cognitive Services endpoint
echo "🔍 Getting AI Foundry endpoint..."
ENDPOINT=$(az cognitiveservices account show \
  --name "$COG_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --query "properties.endpoint" \
  --output tsv)

echo "   Endpoint: $ENDPOINT"

# Get access token
echo "🔑 Getting access token..."
TOKEN=$(az account get-access-token --resource https://cognitiveservices.azure.com --query accessToken --output tsv)
echo "   ✅ Got token"

# Load OpenAPI spec
echo "📄 Loading OpenAPI spec..."
if [ ! -f "$OPENAPI_SPEC_PATH" ]; then
  echo "   ❌ OpenAPI spec not found at: $OPENAPI_SPEC_PATH"
  exit 1
fi
OPENAPI_SPEC=$(cat "$OPENAPI_SPEC_PATH")
echo "   ✅ Loaded OpenAPI spec"

# List agents to find Pennie
echo ""
echo "🔍 Finding Pennie the Prepper..."
AGENTS_RESPONSE=$(curl -s "${ENDPOINT}assistants?api-version=2025-05-01" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json")

# Extract Pennie's ID
AGENT_ID=$(echo "$AGENTS_RESPONSE" | python3 -c "
import json, sys
try:
    data = json.load(sys.stdin)
    agents = data.get('data', [])
    for agent in agents:
        if 'Pennie' in agent.get('name', ''):
            print(agent['id'])
            break
except:
    pass
")

if [ -z "$AGENT_ID" ]; then
  echo "   ❌ Could not find Pennie the Prepper"
  echo "   Response: $AGENTS_RESPONSE"
  exit 1
fi

echo "   ✅ Found Pennie: $AGENT_ID"

# Create tool configuration payload
echo ""
echo "🔧 Preparing OpenAPI tool configuration..."
cat > /tmp/agent-tools-payload.json <<EOF
{
  "tools": [
    {
      "type": "openapi",
      "openapi": {
        "name": "azure_devops_api",
        "description": "Azure DevOps Work Item Tracking API for creating and managing work items",
        "spec": $OPENAPI_SPEC,
        "auth": {
          "type": "none"
        }
      }
    }
  ]
}
EOF

echo "   ✅ Payload created"

# Update agent with tools
echo ""
echo "📤 Updating Pennie with Azure DevOps API tools..."
UPDATE_RESPONSE=$(curl -s -X PATCH "${ENDPOINT}assistants/${AGENT_ID}?api-version=2025-05-01" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d @/tmp/agent-tools-payload.json)

# Check if successful
if echo "$UPDATE_RESPONSE" | grep -q '"id"'; then
  echo "   ✅ Successfully updated Pennie!"
  echo ""
  echo "📋 Agent Details:"
  echo "$UPDATE_RESPONSE" | python3 -c "
import json, sys
try:
    data = json.load(sys.stdin)
    print(f\"   Name: {data.get('name', 'Unknown')}\")
    print(f\"   Model: {data.get('model', 'Unknown')}\")
    print(f\"   Tools: {len(data.get('tools', []))} configured\")
except:
    pass
"
else
  echo "   ❌ Failed to update agent"
  echo "$UPDATE_RESPONSE" | python3 -m json.tool | head -50
  exit 1
fi

# Cleanup
rm -f /tmp/agent-tools-payload.json

echo ""
echo "✅ Deployment complete!"
echo ""
echo "🎉 Test Pennie in Azure AI Foundry Playground:"
echo "   Ask: 'What DevOps projects do we have?'"
echo "   Pennie should list all 26 KnowAll projects!"
