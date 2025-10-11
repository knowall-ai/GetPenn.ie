#!/usr/bin/env python3
"""
Configure Pennie's functions via Azure AI Foundry REST API.

This script updates Pennie the Prepper agent with all 9 Azure Functions
for Azure DevOps work item management.
"""

import os
import sys
import json
import requests
from azure.identity import DefaultAzureCredential

# Configuration
RESOURCE_GROUP = "TMinus15Agents"
PROJECT_NAME = "benw-mgan4638-eastus2"
AGENT_ID = "asst_QP4Q94razJnAaC16jjuDf"  # From screenshot - may need to get full ID
API_VERSION = "2025-05-01"
BACKEND_URL = "https://pennie-backend-prod.azurewebsites.net"

# AI Project endpoint
ENDPOINT = f"https://{PROJECT_NAME}.cognitiveservices.azure.com/"

def get_access_token():
    """Get access token for Azure AI Services"""
    credential = DefaultAzureCredential()
    token = credential.get_token("https://cognitiveservices.azure.com/.default")
    return token.token

def get_agent_details(token, agent_id):
    """Get current agent configuration"""
    url = f"{ENDPOINT}assistants/{agent_id}?api-version={API_VERSION}"
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json"
    }

    response = requests.get(url, headers=headers)
    if response.status_code == 200:
        return response.json()
    else:
        print(f"❌ Failed to get agent: {response.status_code}")
        print(response.text)
        return None

def create_function_definitions():
    """Create function definitions for all 9 Azure Functions"""

    functions = [
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
                            "description": "Optional: Link type. Default is 'System.LinkTypes.Hierarchy-Forward' (parent→child). Options: Hierarchy-Forward, Hierarchy-Reverse, Related, Dependency-Forward, Dependency-Reverse, Duplicate-Forward, Duplicate-Reverse",
                            "default": "System.LinkTypes.Hierarchy-Forward"
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

    return functions

def update_agent_functions(token, agent_id, functions):
    """Update agent with new function definitions"""
    url = f"{ENDPOINT}assistants/{agent_id}?api-version={API_VERSION}"
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json"
    }

    # Get current agent config first
    agent = get_agent_details(token, agent_id)
    if not agent:
        return False

    # Update only the tools field
    payload = {
        "tools": functions
    }

    response = requests.patch(url, headers=headers, json=payload)

    if response.status_code == 200:
        print("✅ Successfully updated Pennie's functions")
        result = response.json()
        print(f"   Agent: {result.get('name', 'Unknown')}")
        print(f"   Model: {result.get('model', 'Unknown')}")
        print(f"   Tools: {len(result.get('tools', []))} functions configured")
        return True
    else:
        print(f"❌ Failed to update agent: {response.status_code}")
        print(response.text)
        return False

def main():
    print("🤖 Configuring Pennie the Prepper Functions")
    print(f"   Endpoint: {ENDPOINT}")
    print(f"   Agent ID: {AGENT_ID}")
    print()

    # Get access token
    print("🔑 Getting access token...")
    try:
        token = get_access_token()
        print("   ✅ Got access token")
    except Exception as e:
        print(f"   ❌ Failed to get token: {e}")
        return 1

    # Get current agent config
    print("\n📋 Getting current agent configuration...")
    agent = get_agent_details(token, AGENT_ID)
    if agent:
        print(f"   Name: {agent.get('name', 'Unknown')}")
        print(f"   Model: {agent.get('model', 'Unknown')}")
        print(f"   Current tools: {len(agent.get('tools', []))}")
    else:
        print("   ⚠️  Could not get agent details, will try to update anyway")

    # Create function definitions
    print("\n🔧 Creating function definitions...")
    functions = create_function_definitions()
    print(f"   Created {len(functions)} function definitions")

    # Update agent
    print("\n📤 Updating Pennie with new functions...")
    success = update_agent_functions(token, AGENT_ID, functions)

    if success:
        print("\n✅ Done! Pennie now has all 9 functions configured.")
        print("\n   You can test by asking Pennie:")
        print('   "What DevOps projects do we have?"')
        return 0
    else:
        print("\n❌ Failed to update Pennie's functions")
        return 1

if __name__ == "__main__":
    sys.exit(main())
