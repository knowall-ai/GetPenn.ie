#!/bin/bash
set -e

# Deploy Preppie AI Foundry Agent to Azure
#
# This script deploys the Preppie agent to Azure AI Foundry using the Assistants API.
# Based on the Edmund deployment pattern from T-Minus-15.
#
# Prerequisites:
# - Azure CLI logged in: az login
# - Environment variables set in .env file
#
# Usage:
#     ./scripts/deploy-agent.sh

echo "🚀 Deploying Preppie the Prepper to Azure AI Foundry"
echo ""

# Load environment variables from .env if available
if [ -f .env ]; then
    echo "📄 Loading environment from .env"
    # Use eval to properly handle quoted values and spaces
    while IFS='=' read -r key value; do
        # Skip comments and empty lines
        [[ $key =~ ^#.*$ ]] && continue
        [[ -z $key ]] && continue
        # Remove leading/trailing whitespace
        key=$(echo "$key" | xargs)
        value=$(echo "$value" | xargs)
        # Export the variable (use eval for proper expansion)
        eval export "$key"=\""$value"\"
    done < .env
fi

# Validate required environment variables
required_vars=(
    "AZURE_SUBSCRIPTION_ID"
    "AZURE_RESOURCE_GROUP"
    "AZURE_AI_HUB_NAME"
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
    echo ""
    echo "Please set these in .env file or environment."
    exit 1
fi

echo "Configuration:"
echo "  Subscription: $AZURE_SUBSCRIPTION_ID"
echo "  Resource Group: $AZURE_RESOURCE_GROUP"
echo "  AI Hub: $AZURE_AI_HUB_NAME"
if [ -n "$AZURE_OPENAI_ENDPOINT" ]; then
    echo "  OpenAI Endpoint: $AZURE_OPENAI_ENDPOINT"
fi
echo ""

# Get Azure AI Foundry endpoint
echo "🔍 Getting Azure AI Foundry endpoint..."
AI_FOUNDRY_ENDPOINT=$(az cognitiveservices account show \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_AI_HUB_NAME" \
    --query properties.endpoint \
    --output tsv 2>/dev/null || echo "")

if [ -z "$AI_FOUNDRY_ENDPOINT" ]; then
    echo "⚠️  Azure AI Foundry Hub not found. Using Azure OpenAI endpoint instead."
    AI_FOUNDRY_ENDPOINT="${AZURE_OPENAI_ENDPOINT%/}"
fi

echo "✅ AI Foundry Endpoint: $AI_FOUNDRY_ENDPOINT"

# Get access token
echo ""
echo "🔐 Getting Azure access token..."
ACCESS_TOKEN=$(az account get-access-token \
    --resource https://cognitiveservices.azure.com \
    --query accessToken \
    --output tsv)

if [ -z "$ACCESS_TOKEN" ]; then
    echo "❌ Failed to get access token. Make sure you're logged in: az login"
    exit 1
fi

echo "✅ Access token retrieved"

# Load agent configuration
echo ""
echo "📄 Loading agent configuration from agent-config.json"

if [ ! -f agent-config.json ]; then
    echo "❌ agent-config.json not found"
    exit 1
fi

# Extract configuration values for display
AGENT_NAME=$(jq -r '.name' agent-config.json)
AGENT_VERSION=$(jq -r '.version' agent-config.json)
MODEL_NAME=$(jq -r '.model.deployment_name' agent-config.json)

echo "✅ Agent: $AGENT_NAME v$AGENT_VERSION"
echo "✅ Model: $MODEL_NAME"

# Create agent payload
echo ""
echo "📝 Creating agent payload..."

# Use jq to properly build the JSON payload with correct escaping
jq --arg deployment_date "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
   --arg deployed_by "$(whoami)" \
   --arg version "$AGENT_VERSION" \
   '{
  name: .name,
  description: "Preppie the Prepper - AI agent for Microsoft Teams meetings that creates Azure DevOps work items from meeting transcripts",
  model: .model.deployment_name,
  instructions: .instructions,
  temperature: .model.temperature,
  top_p: .model.top_p,
  tools: [
    {
      type: "function",
      function: {
        name: "wit_create_work_item",
        description: "Create a new work item in Azure DevOps",
        parameters: {
          type: "object",
          properties: {
            type: {type: "string", enum: ["Epic", "Feature", "User Story", "Question"]},
            title: {type: "string"},
            description: {type: "string"},
            acceptanceCriteria: {type: "array", items: {type: "string"}},
            priority: {type: "integer"},
            estimatedEffort: {type: "string"}
          },
          required: ["type", "title", "description"]
        }
      }
    },
    {
      type: "function",
      function: {
        name: "wit_add_child_work_items",
        description: "Add child work items to a parent work item",
        parameters: {
          type: "object",
          properties: {
            parentId: {type: "integer"},
            childIds: {type: "array", items: {type: "integer"}}
          },
          required: ["parentId", "childIds"]
        }
      }
    }
  ],
  metadata: {
    version: $version,
    deployment_date: $deployment_date,
    deployed_by: $deployed_by,
    source: "GetPenn.ie"
  }
}' agent-config.json > /tmp/preppie-agent-payload.json

echo "✅ Agent payload created: /tmp/preppie-agent-payload.json"

# Deploy agent to Azure AI Foundry
echo ""
echo "🚀 Deploying agent to Azure AI Foundry..."

RESPONSE=$(curl -s -X POST "$AI_FOUNDRY_ENDPOINT/openai/assistants?api-version=2024-12-01-preview" \
    -H "Authorization: Bearer $ACCESS_TOKEN" \
    -H "Content-Type: application/json" \
    -d @/tmp/preppie-agent-payload.json)

# Check for errors
if echo "$RESPONSE" | jq -e '.error' > /dev/null 2>&1; then
    echo "❌ Deployment failed:"
    echo "$RESPONSE" | jq '.error'
    exit 1
fi

# Extract assistant ID
ASSISTANT_ID=$(echo "$RESPONSE" | jq -r '.id')

if [ -z "$ASSISTANT_ID" ] || [ "$ASSISTANT_ID" = "null" ]; then
    echo "❌ Failed to deploy agent. Response:"
    echo "$RESPONSE" | jq '.'
    exit 1
fi

echo "✅ Agent deployed successfully!"
echo "   Assistant ID: $ASSISTANT_ID"

# Save assistant ID to .env
if [ -f .env ]; then
    # Remove existing AZURE_AI_ASSISTANT_ID if present
    sed -i '/^AZURE_AI_ASSISTANT_ID=/d' .env
    echo "AZURE_AI_ASSISTANT_ID=$ASSISTANT_ID" >> .env
    echo "✅ Assistant ID saved to .env"
fi

# Test the agent
echo ""
echo "🧪 Testing agent with sample message..."

# Create a thread
THREAD_RESPONSE=$(curl -s -X POST "$AI_FOUNDRY_ENDPOINT/openai/threads?api-version=2024-12-01-preview" \
    -H "Authorization: Bearer $ACCESS_TOKEN" \
    -H "Content-Type: application/json" \
    -d '{}')

THREAD_ID=$(echo "$THREAD_RESPONSE" | jq -r '.id')

if [ -z "$THREAD_ID" ] || [ "$THREAD_ID" = "null" ]; then
    echo "⚠️  Failed to create test thread. Skipping test."
else
    echo "✅ Test thread created: $THREAD_ID"

    # Send a test message
    TEST_MESSAGE="Meeting transcript: We need to add a dark mode feature to the application. This should include a toggle in settings and proper styling for all components."

    curl -s -X POST "$AI_FOUNDRY_ENDPOINT/openai/threads/$THREAD_ID/messages?api-version=2024-12-01-preview" \
        -H "Authorization: Bearer $ACCESS_TOKEN" \
        -H "Content-Type: application/json" \
        -d "{\"role\": \"user\", \"content\": \"$TEST_MESSAGE\"}" > /dev/null

    # Run the assistant
    RUN_RESPONSE=$(curl -s -X POST "$AI_FOUNDRY_ENDPOINT/openai/threads/$THREAD_ID/runs?api-version=2024-12-01-preview" \
        -H "Authorization: Bearer $ACCESS_TOKEN" \
        -H "Content-Type: application/json" \
        -d "{\"assistant_id\": \"$ASSISTANT_ID\"}")

    RUN_ID=$(echo "$RUN_RESPONSE" | jq -r '.id')

    if [ -z "$RUN_ID" ] || [ "$RUN_ID" = "null" ]; then
        echo "⚠️  Failed to run test. Response:"
        echo "$RUN_RESPONSE" | jq '.'
    else
        echo "✅ Test run started: $RUN_ID"
        echo "   Check Azure AI Studio for results: https://ai.azure.com"
    fi
fi

echo ""
echo "✨ Deployment complete!"
echo ""
echo "Next steps:"
echo "1. Configure MCP Server for Azure DevOps integration"
echo "2. Test agent in Azure AI Studio: https://ai.azure.com"
echo "3. Deploy Teams Bot to Windows VM"
echo "4. Configure Teams app registration"
echo ""
echo "Assistant ID: $ASSISTANT_ID"
echo "Saved to .env as AZURE_AI_ASSISTANT_ID"
