#!/usr/bin/env python3
"""
Configure Pennie the Prepper with OpenAPI tools for Azure DevOps integration.

This script uses the Azure AI Agents SDK to:
1. Load the OpenAPI spec for the backend API
2. Create/update Pennie agent with OpenAPI tools
3. Configure anonymous authentication (no API keys needed)
"""

import os
import sys
import json
from pathlib import Path

# Check if required packages are installed
try:
    from azure.ai.agents import AgentsClient
    from azure.identity import DefaultAzureCredential
    from azure.ai.agents.models import OpenApiTool, OpenApiAnonymousAuthDetails
except ImportError as e:
    print(f"❌ Missing required packages: {e}")
    print("   Please run the deployment script: ./scripts/deploy-pennie-agent.sh")
    sys.exit(1)

# Configuration
PROJECT_ENDPOINT = "https://benw-mgan4638-eastus2.cognitiveservices.azure.com/"
MODEL_DEPLOYMENT = "gpt-4o"
AGENT_NAME = "Pennie the Prepper"
AGENT_INSTRUCTIONS = """You are Pennie the Prepper — a skilled business analyst AI agent that joins Microsoft Teams meetings as a real-time participant listening to live conversations.

Your primary role is to:
- Listen actively to meeting discussions about work, projects, and tasks
- Identify actionable items, decisions, questions, and work that needs to be done
- Automatically create Azure DevOps work items (Epics, Features, User Stories, Tasks, Questions) based on what you hear
- Organize work items into proper hierarchies (Epics → Features → Stories → Tasks)
- Link related work items appropriately
- Capture context from the meeting in work item descriptions

You have access to the KnowAll Azure DevOps organization with 26 client projects. When someone mentions a client or project, you should:
1. Identify which DevOps project they're referring to
2. Create work items in the appropriate project
3. Use proper work item types based on the scope (Epic for large initiatives, Feature for major functionality, User Story for user-facing work, Task for implementation work, Question for things that need clarification)

Be proactive, accurate, and helpful. Always confirm which project you're creating work items in, and provide the work item IDs and links after creation."""

def load_openapi_spec():
    """Load the OpenAPI specification for Pennie's backend"""
    spec_path = Path(__file__).parent.parent / "openapi" / "pennie-backend-openapi.json"

    print(f"📄 Loading OpenAPI spec from: {spec_path}")

    if not spec_path.exists():
        print(f"❌ OpenAPI spec not found at: {spec_path}")
        sys.exit(1)

    with open(spec_path, "r") as f:
        spec = json.load(f)

    print(f"   ✅ Loaded spec with {len(spec.get('paths', {}))} endpoints")
    return spec

def get_or_create_agent(agents_client, openapi_spec):
    """Get existing Pennie agent or create a new one"""

    print("\n🔍 Looking for existing Pennie agent...")

    try:
        # List all agents
        agents_list = agents_client.list_agents()

        pennie_agent = None
        for agent in agents_list:
            if "Pennie" in agent.name:
                pennie_agent = agent
                print(f"   ✅ Found existing agent: {agent.name} (ID: {agent.id})")
                break

        # Create OpenAPI tool with anonymous auth
        print("\n🔧 Creating OpenAPI tool...")
        auth = OpenApiAnonymousAuthDetails()
        openapi_tool = OpenApiTool(
            name="azure_devops_api",
            spec=openapi_spec,
            description="Azure DevOps Work Item Tracking API for creating and managing work items, projects, and teams",
            auth=auth
        )
        print(f"   ✅ OpenAPI tool created with {len(openapi_spec.get('paths', {}))} operations")

        if pennie_agent:
            # Update existing agent
            print(f"\n📝 Updating existing Pennie agent (ID: {pennie_agent.id})...")

            updated_agent = agents_client.update_agent(
                assistant_id=pennie_agent.id,
                name=AGENT_NAME,
                instructions=AGENT_INSTRUCTIONS,
                tools=[openapi_tool],
                model=MODEL_DEPLOYMENT
            )

            print(f"   ✅ Updated agent: {updated_agent.name}")
            print(f"   Model: {updated_agent.model}")
            print(f"   Tools: {len(updated_agent.tools)} configured")

            return updated_agent

        else:
            # Create new agent
            print("\n🆕 Creating new Pennie agent...")

            new_agent = agents_client.create_agent(
                model=MODEL_DEPLOYMENT,
                name=AGENT_NAME,
                instructions=AGENT_INSTRUCTIONS,
                tools=[openapi_tool]
            )

            print(f"   ✅ Created agent: {new_agent.name}")
            print(f"   ID: {new_agent.id}")
            print(f"   Model: {new_agent.model}")
            print(f"   Tools: {len(new_agent.tools)} configured")

            return new_agent

    except Exception as e:
        print(f"   ❌ Error managing agent: {e}")
        import traceback
        traceback.print_exc()
        return None

def main():
    print("🤖 Configuring Pennie the Prepper with Azure DevOps API Tools")
    print(f"   Endpoint: {PROJECT_ENDPOINT}")
    print(f"   Model: {MODEL_DEPLOYMENT}")
    print()

    # Load OpenAPI spec
    openapi_spec = load_openapi_spec()

    # Initialize Azure AI Agents Client
    print("\n🔑 Authenticating with Azure...")
    try:
        credential = DefaultAzureCredential(exclude_interactive_browser_credential=False)
        agents_client = AgentsClient(
            endpoint=PROJECT_ENDPOINT,
            credential=credential
        )
        print("   ✅ Authenticated successfully")
    except Exception as e:
        print(f"   ❌ Authentication failed: {e}")
        print("\n   Please run: az login")
        import traceback
        traceback.print_exc()
        return 1

    # Get or create Pennie agent
    agent = get_or_create_agent(agents_client, openapi_spec)

    if not agent:
        print("\n❌ Failed to configure Pennie agent")
        return 1

    print("\n✅ Pennie is now fully configured!")
    print("\n📋 Summary:")
    print(f"   Agent Name: {agent.name}")
    print(f"   Agent ID: {agent.id}")
    print(f"   Model: {agent.model}")
    print(f"   Tools: {len(agent.tools)} OpenAPI-based functions")
    print("\n   Pennie can now call all 9 Azure DevOps functions:")
    for path, methods in openapi_spec.get('paths', {}).items():
        for method, operation in methods.items():
            op_id = operation.get('operationId', 'unknown')
            print(f"   - {op_id}")

    print("\n🎉 Test Pennie in Azure AI Foundry Playground:")
    print('   Ask: "What DevOps projects do we have?"')
    print("   Pennie should list all 26 KnowAll projects!")

    return 0

if __name__ == "__main__":
    sys.exit(main())
