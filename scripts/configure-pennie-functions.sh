#!/bin/bash
set -e

# Configuration
ENDPOINT="https://benw-mgan4638-eastus2.cognitiveservices.azure.com"
API_VERSION="2025-05-01"
AGENT_NAME="Pennie the Prepper"

echo "🤖 Configuring Pennie the Prepper Functions"
echo "   Endpoint: $ENDPOINT"
echo ""

# Get access token
echo "🔑 Getting access token..."
TOKEN=$(az account get-access-token --resource https://cognitiveservices.azure.com --query accessToken --output tsv)
echo "   ✅ Got access token"

# List agents to find Pennie
echo ""
echo "📋 Finding Pennie's agent ID..."
AGENT_LIST=$(curl -s "$ENDPOINT/assistants?api-version=$API_VERSION" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json")

# Extract Pennie's ID (assuming she's named "Pennie the Prepper")
AGENT_ID=$(echo "$AGENT_LIST" | python3 -c "import json, sys; data=json.load(sys.stdin); agents=[a for a in data.get('data', []) if 'Pennie' in a.get('name', '')]; print(agents[0]['id'] if agents else '')")

if [ -z "$AGENT_ID" ]; then
  echo "   ❌ Could not find Pennie the Prepper agent"
  echo "   Available agents:"
  echo "$AGENT_LIST" | python3 -m json.tool | grep -E "(id|name)" | head -20
  exit 1
fi

echo "   ✅ Found Pennie: $AGENT_ID"

# Get current agent config
echo ""
echo "📋 Getting current configuration..."
CURRENT_AGENT=$(curl -s "$ENDPOINT/assistants/$AGENT_ID?api-version=$API_VERSION" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json")

CURRENT_NAME=$(echo "$CURRENT_AGENT" | python3 -c "import json, sys; print(json.load(sys.stdin).get('name', 'Unknown'))")
CURRENT_MODEL=$(echo "$CURRENT_AGENT" | python3 -c "import json, sys; print(json.load(sys.stdin).get('model', 'Unknown'))")
CURRENT_TOOLS=$(echo "$CURRENT_AGENT" | python3 -c "import json, sys; print(len(json.load(sys.stdin).get('tools', [])))")

echo "   Name: $CURRENT_NAME"
echo "   Model: $CURRENT_MODEL"
echo "   Current tools: $CURRENT_TOOLS"

# Create function definitions JSON
echo ""
echo "🔧 Creating function definitions..."
cat > /tmp/pennie-functions.json <<'EOF'
{
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "read_projects",
        "description": "List all Azure DevOps projects in the KnowAll organization (26 client projects). Returns project names, IDs, descriptions, and visibility.",
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
        "name": "read_teams",
        "description": "List all teams within a specific Azure DevOps project. Useful for understanding team structure.",
        "parameters": {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Project name or ID (e.g., 'Internal', 'HSE', 'Flogas')"
            }
          },
          "required": ["project"]
        }
      }
    },
    {
      "type": "function",
      "function": {
        "name": "read_work_item",
        "description": "Get detailed information about a single work item by ID. Returns all fields including title, description, state, assigned to, tags, and custom fields.",
        "parameters": {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Project name or ID"
            },
            "workItemId": {
              "type": "integer",
              "description": "Work item ID number"
            }
          },
          "required": ["project", "workItemId"]
        }
      }
    },
    {
      "type": "function",
      "function": {
        "name": "read_work_items",
        "description": "Get multiple work items with flexible filtering. Can get specific IDs, get children of a parent (with recursive depth 1-5 levels), and filter by type and state. Extremely useful for getting hierarchies like Epic → Features → Stories.",
        "parameters": {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Project name or ID"
            },
            "workItemIds": {
              "type": "array",
              "items": {"type": "integer"},
              "description": "Optional: List of specific work item IDs to retrieve"
            },
            "parentId": {
              "type": "integer",
              "description": "Optional: Get children of this parent work item ID"
            },
            "depth": {
              "type": "integer",
              "description": "Optional: Recursive depth for getting nested children (1-5). Default is 1. Use 2-3 for typical Epic->Feature->Story hierarchies.",
              "minimum": 1,
              "maximum": 5
            },
            "workItemType": {
              "type": "string",
              "description": "Optional: Filter by work item type (Epic, Feature, User Story, Task, Bug, Question)"
            },
            "state": {
              "type": "string",
              "description": "Optional: Filter by state (New, Active, Resolved, Closed, Removed)"
            }
          },
          "required": ["project"]
        }
      }
    },
    {
      "type": "function",
      "function": {
        "name": "read_work_item_types",
        "description": "Discover what work item types are available in a project (Epic, Feature, User Story, Task, Bug, Question, etc.). Returns names, descriptions, icons, and colors.",
        "parameters": {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Project name or ID"
            }
          },
          "required": ["project"]
        }
      }
    },
    {
      "type": "function",
      "function": {
        "name": "read_link_types",
        "description": "Discover all 7 available link types for connecting work items. Returns: Hierarchy-Forward (Parent→Child), Hierarchy-Reverse (Child→Parent), Related, Dependency-Forward (Predecessor), Dependency-Reverse (Successor), Duplicate-Forward, Duplicate-Reverse.",
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
        "name": "search_work_items",
        "description": "Advanced search for work items using WIQL (Work Item Query Language). Supports complex queries with multiple conditions, field comparisons, and date ranges.",
        "parameters": {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Project name or ID"
            },
            "wiql": {
              "type": "string",
              "description": "WIQL query string (e.g., 'SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.State] = \"Active\"')"
            }
          },
          "required": ["project", "wiql"]
        }
      }
    },
    {
      "type": "function",
      "function": {
        "name": "create_work_item",
        "description": "Create a new work item (Epic, Feature, User Story, Task, Bug, Question). Supports setting title, description, assigned to, tags, priority, effort, and custom fields.",
        "parameters": {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Project name or ID"
            },
            "workItemType": {
              "type": "string",
              "description": "Type of work item to create (Epic, Feature, User Story, Task, Bug, Question)"
            },
            "title": {
              "type": "string",
              "description": "Work item title (required)"
            },
            "description": {
              "type": "string",
              "description": "Optional: Detailed description (supports HTML)"
            },
            "assignedTo": {
              "type": "string",
              "description": "Optional: Email of person to assign"
            },
            "tags": {
              "type": "string",
              "description": "Optional: Comma-separated tags"
            },
            "priority": {
              "type": "integer",
              "description": "Optional: Priority (1-4, where 1 is highest)"
            },
            "effort": {
              "type": "number",
              "description": "Optional: Story points or effort estimate"
            }
          },
          "required": ["project", "workItemType", "title"]
        }
      }
    },
    {
      "type": "function",
      "function": {
        "name": "link_work_items",
        "description": "Create links between work items with flexible link types. Can create parent-child relationships, dependencies, related links, and duplicates. Supports linking multiple targets to one source.",
        "parameters": {
          "type": "object",
          "properties": {
            "project": {
              "type": "string",
              "description": "Project name or ID"
            },
            "sourceId": {
              "type": "integer",
              "description": "Source work item ID"
            },
            "targetIds": {
              "type": "array",
              "items": {"type": "integer"},
              "description": "List of target work item IDs to link"
            },
            "linkType": {
              "type": "string",
              "description": "Optional: Link type. Default is 'System.LinkTypes.Hierarchy-Forward' (parent→child). Options: Hierarchy-Forward, Hierarchy-Reverse, Related, Dependency-Forward, Dependency-Reverse, Duplicate-Forward, Duplicate-Reverse"
            },
            "comment": {
              "type": "string",
              "description": "Optional: Comment for the link relationship"
            }
          },
          "required": ["project", "sourceId", "targetIds"]
        }
      }
    }
  ]
}
EOF

echo "   Created 9 function definitions"

# Update Pennie
echo ""
echo "📤 Updating Pennie with new functions..."
RESPONSE=$(curl -s -X PATCH "$ENDPOINT/assistants/$AGENT_ID?api-version=$API_VERSION" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d @/tmp/pennie-functions.json)

# Check if successful
if echo "$RESPONSE" | grep -q "\"id\""; then
  echo "   ✅ Successfully updated Pennie's functions"
  NEW_TOOLS=$(echo "$RESPONSE" | python3 -c "import json, sys; print(len(json.load(sys.stdin).get('tools', [])))")
  echo "   New tool count: $NEW_TOOLS"
else
  echo "   ❌ Failed to update agent"
  echo "$RESPONSE" | python3 -m json.tool | head -30
  exit 1
fi

# Cleanup
rm -f /tmp/pennie-functions.json

echo ""
echo "✅ Done! Pennie now has all 9 functions configured."
echo ""
echo "   You can test by asking Pennie:"
echo '   "What DevOps projects do we have?"'
