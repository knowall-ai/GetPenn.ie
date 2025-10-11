#!/usr/bin/env python3
import subprocess
import sys

# Get subscription ID and construct proper endpoint
result = subprocess.run(
    ["az", "ml", "workspace", "show", 
     "--name", "pennie-project-prod",
     "--resource-group", "TMinus15Agents",
     "--query", "id",
     "--output", "tsv"],
    capture_output=True,
    text=True
)

workspace_id = result.stdout.strip()
print(f"Workspace ID: {workspace_id}")

# The workspace_id format is: /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.MachineLearningServices/workspaces/{name}
# Extract subscription and construct AI services endpoint
parts = workspace_id.split('/')
subscription = parts[2]
resource_group = parts[4]
workspace_name = parts[-1]

# AI Foundry project endpoint format
endpoint = f"https://uksouth.api.azureml.ms/mlflow/v2.0/subscriptions/{subscription}/resourceGroups/{resource_group}/providers/Microsoft.MachineLearningServices/workspaces/{workspace_name}"

print(f"AI Project Endpoint: {endpoint}")
print("\n🚀 Now deploying Pennie with OpenAPI tools...")

# Run the actual deployment
sys.exit(subprocess.call(["/mnt/raid1/GitHub/GetPenn.ie/scripts/deploy-pennie-agent.sh"]))
