#!/bin/bash
set -e

# Deploy Pennie to Azure AI Foundry Agent Service
# Uses the Azure AI Foundry Agent Service REST API (GA API version 2025-05-01)

echo "🚀 Deploying Pennie to Azure AI Foundry Agent Service"
echo ""

# Load environment variables from .env if available
if [ -f .env ]; then
    echo "📄 Loading environment from .env"
    while IFS='=' read -r key value; do
        [[ $key =~ ^#.*$ ]] && continue
        [[ -z $key ]] && continue
        key=$(echo "$key" | xargs)
        value=$(echo "$value" | xargs)
        eval export "$key"=\""$value"\"
    done < .env
fi

# Required variables
RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:-TMinus15Agents}"
PROJECT_NAME="${AZURE_AI_PROJECT_NAME:-pennie-project-prod}"
HUB_NAME="${AZURE_AI_HUB_NAME:-knowall-ai-foundry-hub}"

echo "Configuration:"
echo "  Resource Group: $RESOURCE_GROUP"
echo "  Project: $PROJECT_NAME"
echo "  Hub: $HUB_NAME"
echo ""

# Get Azure AI Foundry project endpoint
echo "🔍 Getting Azure AI Foundry project endpoint..."
PROJECT_ENDPOINT=$(az ml workspace show \
    --name "$PROJECT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query 'properties.discoveryUrl' \
    --output tsv 2>/dev/null | sed 's|/discovery||')

if [ -z "$PROJECT_ENDPOINT" ]; then
    # Fallback: construct endpoint from Azure region
    LOCATION=$(az ml workspace show --name "$PROJECT_NAME" --resource-group "$RESOURCE_GROUP" --query location --output tsv)
    # Azure AI Foundry project endpoint format: https://<location>.services.ai.azure.com/api/projects/<project-name>
    AI_SERVICE_NAME=$(az resource list --resource-group "$RESOURCE_GROUP" --query "[?kind=='AIServices'].name | [0]" --output tsv)
    if [ -n "$AI_SERVICE_NAME" ]; then
        PROJECT_ENDPOINT="https://${AI_SERVICE_NAME}.services.ai.azure.com/api/projects/${PROJECT_NAME}"
    else
        echo "❌ Could not determine AI Foundry project endpoint"
        exit 1
    fi
fi

echo "✅ Project Endpoint: $PROJECT_ENDPOINT"

# Get access token
echo ""
echo "🔐 Getting Azure access token..."
ACCESS_TOKEN=$(az account get-access-token \
    --resource https://ai.azure.com \
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

AGENT_NAME=$(jq -r '.name' agent-config.json)
AGENT_VERSION=$(jq -r '.version' agent-config.json)
MODEL_NAME=$(jq -r '.model.deployment_name' agent-config.json)

echo "✅ Agent: $AGENT_NAME v$AGENT_VERSION"
echo "✅ Model: $MODEL_NAME"

# Create agent payload using jq
echo ""
echo "📝 Creating Azure AI Foundry Agent payload..."

jq --arg deployment_date "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
   --arg deployed_by "$(whoami)" \
   --arg version "$AGENT_VERSION" \
   '{
  name: .name,
  description: "Pennie the Prepper - AI agent for Microsoft Teams meetings that creates Azure DevOps work items from meeting transcripts",
  model: .model.deployment_name,
  instructions: .instructions,
  temperature: .model.temperature,
  top_p: .model.top_p,
  tools: [
    {
      type: "function",
      function: {
        name: "read_projects",
        description: "List all Azure DevOps projects (GET /api/read_projects)",
        parameters: {
          type: "object",
          properties: {},
          required: []
        }
      }
    },
    {
      type: "function",
      function: {
        name: "read_teams",
        description: "List teams in a specific Azure DevOps project (POST /api/read_teams)",
        parameters: {
          type: "object",
          properties: {
            project: {type: "string", description: "Name of the Azure DevOps project"}
          },
          required: ["project"]
        }
      }
    },
    {
      type: "function",
      function: {
        name: "read_work_item",
        description: "Get a single work item by ID (POST /api/read_work_item)",
        parameters: {
          type: "object",
          properties: {
            project: {type: "string", description: "Name of the Azure DevOps project"},
            workItemId: {type: "integer", description: "ID of the work item to retrieve"}
          },
          required: ["project", "workItemId"]
        }
      }
    },
    {
      type: "function",
      function: {
        name: "read_work_items",
        description: "Get work items with optional filtering and recursive depth 1-5 (POST /api/read_work_items)",
        parameters: {
          type: "object",
          properties: {
            project: {type: "string", description: "Name of the Azure DevOps project"},
            parentId: {type: "integer", description: "Optional parent work item ID to filter by"},
            depth: {type: "integer", minimum: 1, maximum: 5, description: "Recursive depth for child items (1-5)"}
          },
          required: ["project"]
        }
      }
    },
    {
      type: "function",
      function: {
        name: "read_work_item_types",
        description: "Get available work item types for a project (POST /api/read_work_item_types)",
        parameters: {
          type: "object",
          properties: {
            project: {type: "string", description: "Name of the Azure DevOps project"}
          },
          required: ["project"]
        }
      }
    },
    {
      type: "function",
      function: {
        name: "read_link_types",
        description: "Get all 7 available link types (GET /api/read_link_types)",
        parameters: {
          type: "object",
          properties: {},
          required: []
        }
      }
    },
    {
      type: "function",
      function: {
        name: "search_work_items",
        description: "Search work items using WIQL (Work Item Query Language) (POST /api/search_work_items)",
        parameters: {
          type: "object",
          properties: {
            project: {type: "string", description: "Name of the Azure DevOps project"},
            wiql: {type: "string", description: "WIQL query string"}
          },
          required: ["project", "wiql"]
        }
      }
    },
    {
      type: "function",
      function: {
        name: "create_work_item",
        description: "Create a new work item (Epic, Feature, Story, Task, Bug, Question) (POST /api/create_work_item)",
        parameters: {
          type: "object",
          properties: {
            project: {type: "string", description: "Name of the Azure DevOps project"},
            workItemType: {type: "string", enum: ["Epic", "Feature", "User Story", "Task", "Bug", "Question"], description: "Type of work item to create"},
            title: {type: "string", description: "Title of the work item"}
          },
          required: ["project", "workItemType", "title"]
        }
      }
    },
    {
      type: "function",
      function: {
        name: "link_work_items",
        description: "Link work items together (child relationship) (POST /api/link_work_items)",
        parameters: {
          type: "object",
          properties: {
            project: {type: "string", description: "Name of the Azure DevOps project"},
            sourceId: {type: "integer", description: "Source work item ID (parent)"},
            targetIds: {type: "array", items: {type: "integer"}, description: "Array of target work item IDs (children)"}
          },
          required: ["project", "sourceId", "targetIds"]
        }
      }
    }
  ],
  metadata: {
    version: $version,
    deployment_date: $deployment_date,
    deployed_by: $deployed_by,
    source: "GetPenn.ie",
    project: "T-Minus-15 Agents"
  }
}' agent-config.json > /tmp/ai-foundry-agent-payload.json

echo "✅ Agent payload created: /tmp/ai-foundry-agent-payload.json"

# Deploy agent to Azure AI Foundry Agent Service
echo ""
echo "🚀 Deploying agent to Azure AI Foundry Agent Service..."

# API endpoint: https://learn.microsoft.com/en-us/azure/ai-foundry/agents/quickstart
AGENT_API_URL="${PROJECT_ENDPOINT}/agents?api-version=2025-05-01"

RESPONSE=$(curl -s -X POST "$AGENT_API_URL" \
    -H "Authorization: Bearer $ACCESS_TOKEN" \
    -H "Content-Type: application/json" \
    -d @/tmp/ai-foundry-agent-payload.json)

# Check for errors
if echo "$RESPONSE" | jq -e '.error' > /dev/null 2>&1; then
    echo "❌ Deployment failed:"
    echo "$RESPONSE" | jq '.error'
    exit 1
fi

# Extract agent ID
AGENT_ID=$(echo "$RESPONSE" | jq -r '.id')

if [ -z "$AGENT_ID" ] || [ "$AGENT_ID" = "null" ]; then
    echo "❌ Failed to deploy agent. Response:"
    echo "$RESPONSE" | jq '.'
    exit 1
fi

echo "✅ Agent deployed successfully!"
echo "   Agent ID: $AGENT_ID"

# Save agent ID to .env
if [ -f .env ]; then
    sed -i '/^AZURE_AI_FOUNDRY_AGENT_ID=/d' .env
    echo "AZURE_AI_FOUNDRY_AGENT_ID=$AGENT_ID" >> .env
    echo "✅ Agent ID saved to .env"
fi

echo ""
echo "✨ Deployment complete!"
echo ""
echo "Next steps:"
echo "1. View agent in Azure AI Foundry: https://ai.azure.com"
echo "2. Navigate to: T-Minus-15 Agents project → Agents"
echo "3. Configure MCP Server for Azure DevOps integration"
echo "4. Test multi-agent communication with Edmund"
echo ""
echo "Agent ID: $AGENT_ID"
echo "Saved to .env as AZURE_AI_FOUNDRY_AGENT_ID"
