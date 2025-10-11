#!/bin/bash
# Deploy Pennie with Azure Functions as OpenAI Assistant tool_resources
# This uses the Azure OpenAI Assistants API (not AI Foundry Agents)

set -e

echo "🤖 Deploying OpenAI Functions to Pennie via Assistants API"
echo ""

# Configuration
ENDPOINT="https://benw-mgan4638-eastus2.cognitiveservices.azure.com"
API_VERSION="2024-05-01-preview"
AGENT_ID="asst_yhQ9HVWxaIyeaSZwjBDOkSQi"
BACKEND_URL="https://pennie-backend-prod.azurewebsites.net"

# Get token
echo "🔑 Getting access token..."
TOKEN=$(az account get-access-token --resource https://cognitiveservices.azure.com --query accessToken --output tsv)
echo "   ✅ Token acquired"

# Create function definitions that make HTTP calls
# NOTE: OpenAI Assistants don't make HTTP calls directly - we need to convert to Azure Functions integration
echo ""
echo "🔧 Creating Azure Functions integration payload..."

# Create payload with tool_resources that point to Azure Functions
cat > /tmp/pennie-update.json <<'PAYLOAD'
{
  "model": "gpt-4o",
  "name": "Pennie the Prepper",
  "description": "Business analyst AI that creates Azure DevOps work items from Teams meetings",
  "instructions": "You are Pennie the Prepper. Use the provided functions to interact with Azure DevOps. When asked about projects, call read_projects.",
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "read_projects",
        "description": "List all 26 Azure DevOps projects in the KnowAll organization",
        "parameters": {
          "type": "object",
          "properties": {},
          "required": []
        }
      }
    },
    {
      "type": "function",
      "function": {
        "name": "read_work_items",
        "description": "Get work items with flexible filtering. Can get by IDs or children of a parent with recursive depth 1-5",
        "parameters": {
          "type": "object",
          "properties": {
            "project": {"type": "string", "description": "Project name"},
            "workItemIds": {"type": "array", "items": {"type": "integer"}, "description": "Specific IDs to retrieve"},
            "parentId": {"type": "integer", "description": "Get children of this parent"},
            "depth": {"type": "integer", "minimum": 1, "maximum": 5, "description": "Recursive depth (1-5)"}
          },
          "required": ["project"]
        }
      }
    },
    {
      "type": "function",
      "function": {
        "name": "create_work_item",
        "description": "Create a new work item (Epic, Feature, User Story, Task, Bug, Question)",
        "parameters": {
          "type": "object",
          "properties": {
            "project": {"type": "string"},
            "workItemType": {"type": "string", "enum": ["Epic", "Feature", "User Story", "Task", "Bug", "Question"]},
            "title": {"type": "string"},
            "description": {"type": "string"}
          },
          "required": ["project", "workItemType", "title"]
        }
      }
    },
    {
      "type": "function",
      "function": {
        "name": "link_work_items",
        "description": "Link work items together with parent-child or other relationships",
        "parameters": {
          "type": "object",
          "properties": {
            "project": {"type": "string"},
            "sourceId": {"type": "integer"},
            "targetIds": {"type": "array", "items": {"type": "integer"}},
            "linkType": {"type": "string", "default": "System.LinkTypes.Hierarchy-Forward"}
          },
          "required": ["project", "sourceId", "targetIds"]
        }
      }
    }
  ]
}
PAYLOAD

echo "   ✅ Payload created with 4 core functions"

# Update Pennie
echo ""
echo "📤 Updating Pennie via OpenAI Assistants API..."
RESPONSE=$(curl -s -X PATCH "$ENDPOINT/openai/assistants/$AGENT_ID?api-version=$API_VERSION" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "api-key: $TOKEN" \
  -d @/tmp/pennie-update.json)

# Check result
if echo "$RESPONSE" | jq -e '.id' >/dev/null 2>&1; then
  echo "   ✅ Successfully updated Pennie!"
  echo ""
  echo "📋 Updated Assistant:"
  echo "$RESPONSE" | jq -r '"   Name: " + .name'
  echo "$RESPONSE" | jq -r '"   Model: " + .model'
  TOOL_COUNT=$(echo "$RESPONSE" | jq '.tools | length')
  echo "   Functions: $TOOL_COUNT"
  echo ""
  echo "   Functions configured:"
  echo "$RESPONSE" | jq -r '.tools[] | "   - " + .function.name'
  
  echo ""
  echo "⚠️  NOTE: OpenAI Assistants require YOU to implement function handlers"
  echo "   Pennie will call these functions, but YOUR code must:"
  echo "   1. Receive the function call"
  echo "   2. Call $BACKEND_URL/api/{function_name}"
  echo "   3. Return the result back to Pennie"
  echo ""
  echo "   This requires a middleware/handler application."
else
  echo "   ❌ Failed to update"
  echo "$RESPONSE" | jq '.' 2>/dev/null || echo "$RESPONSE"
  exit 1
fi

rm -f /tmp/pennie-update.json

echo "✅ Configuration complete"
