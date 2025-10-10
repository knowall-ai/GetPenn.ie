#!/usr/bin/env python3
"""
Deploy Pennie to Azure AI Foundry using the azure-ai-projects SDK
This uses the official Python SDK instead of direct REST API calls
"""

import os
import sys
import json
from datetime import datetime

try:
    from azure.ai.projects import AIProjectClient
    from azure.identity import DefaultAzureCredential
except ImportError:
    print("❌ Missing required packages. Installing...")
    import subprocess
    subprocess.check_call([sys.executable, "-m", "pip", "install", "--user", "azure-ai-projects", "azure-identity"])
    from azure.ai.projects import AIProjectClient
    from azure.identity import DefaultAzureCredential

def main():
    print("🚀 Deploying Pennie to Azure AI Foundry Agent Service (Python SDK)")
    print("")

    # Load configuration
    project_name = os.getenv("AZURE_AI_PROJECT_NAME", "pennie-project-prod")
    resource_group = os.getenv("AZURE_RESOURCE_GROUP", "TMinus15Agents")
    subscription_id = os.getenv("AZURE_SUBSCRIPTION_ID")

    if not subscription_id:
        print("❌ AZURE_SUBSCRIPTION_ID not set in environment")
        sys.exit(1)

    print(f"Configuration:")
    print(f"  Subscription: {subscription_id}")
    print(f"  Resource Group: {resource_group}")
    print(f"  Project: {project_name}")
    print("")

    # Load agent configuration
    with open("agent-config.json", "r") as f:
        agent_config = json.load(f)

    agent_name = agent_config["name"]
    agent_version = agent_config["version"]
    model_name = agent_config["model"]["deployment_name"]

    print(f"✅ Agent: {agent_name} v{agent_version}")
    print(f"✅ Model: {model_name}")
    print("")

    # Create AI Project client
    print("🔐 Authenticating with Azure...")
    credential = DefaultAzureCredential()

    # Project connection string format:
    # /subscriptions/<sub-id>/resourceGroups/<rg>/providers/Microsoft.MachineLearningServices/workspaces/<project-name>
    project_scope = f"/subscriptions/{subscription_id}/resourceGroups/{resource_group}/providers/Microsoft.MachineLearningServices/workspaces/{project_name}"

    try:
        client = AIProjectClient.from_connection_string(
            conn_str=project_scope,
            credential=credential
        )
        print("✅ Connected to AI Foundry project")
    except Exception as e:
        print(f"❌ Failed to connect: {e}")
        print("")
        print("Trying alternative connection method...")
        # Try with endpoint instead
        endpoint = f"https://knowall-ai-foundry.services.ai.azure.com"
        client = AIProjectClient(
            endpoint=endpoint,
            credential=credential,
            subscription_id=subscription_id,
            resource_group_name=resource_group,
            project_name=project_name
        )
        print("✅ Connected via endpoint")

    print("")
    print("📝 Creating agent...")

    # Create agent
    agent = client.agents.create_agent(
        model=model_name,
        name=agent_name,
        instructions=agent_config["instructions"],
        temperature=agent_config["model"]["temperature"],
        top_p=agent_config["model"]["top_p"],
        tools=[
            {
                "type": "function",
                "function": {
                    "name": "wit_create_work_item",
                    "description": "Create a new work item in Azure DevOps",
                    "parameters": {
                        "type": "object",
                        "properties": {
                            "type": {"type": "string", "enum": ["Epic", "Feature", "User Story", "Question"]},
                            "title": {"type": "string"},
                            "description": {"type": "string"},
                            "acceptanceCriteria": {"type": "array", "items": {"type": "string"}},
                            "priority": {"type": "integer"},
                            "estimatedEffort": {"type": "string"}
                        },
                        "required": ["type", "title", "description"]
                    }
                }
            },
            {
                "type": "function",
                "function": {
                    "name": "wit_add_child_work_items",
                    "description": "Add child work items to a parent work item",
                    "parameters": {
                        "type": "object",
                        "properties": {
                            "parentId": {"type": "integer"},
                            "childIds": {"type": "array", "items": {"type": "integer"}}
                        },
                        "required": ["parentId", "childIds"]
                    }
                }
            }
        ],
        metadata={
            "version": agent_version,
            "deployment_date": datetime.utcnow().isoformat(),
            "source": "GetPenn.ie",
            "project": "T-Minus-15 Agents"
        }
    )

    print("✅ Agent created successfully!")
    print(f"   Agent ID: {agent.id}")
    print("")

    # Save agent ID to .env
    agent_id_line = f"AZURE_AI_FOUNDRY_AGENT_ID={agent.id}\n"

    if os.path.exists(".env"):
        with open(".env", "r") as f:
            lines = f.readlines()

        # Remove existing agent ID if present
        lines = [l for l in lines if not l.startswith("AZURE_AI_FOUNDRY_AGENT_ID=")]
        lines.append(agent_id_line)

        with open(".env", "w") as f:
            f.writelines(lines)

        print("✅ Agent ID saved to .env")

    print("")
    print("✨ Deployment complete!")
    print("")
    print("Next steps:")
    print("1. View agent in Azure AI Foundry: https://ai.azure.com")
    print("2. Navigate to: T-Minus-15 Agents project → Agents")
    print("3. Configure MCP Server for Azure DevOps integration")
    print("")

if __name__ == "__main__":
    main()
